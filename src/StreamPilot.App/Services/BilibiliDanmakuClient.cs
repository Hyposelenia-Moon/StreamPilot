namespace StreamPilot.App.Services;

using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Services;
using StreamPilot.Parsers.Bilibili;

/// <summary>
/// B站直播弹幕客户端：获取凭证 → 建立 WebSocket → 认证 → 心跳 → 接收并聚合消息。
/// </summary>
/// <remarks>
/// <para><b>只读</b>：不发送任何弹幕，只接收与展示（发送需登录态 + 风控，触碰项目红线）。</para>
/// <para>
/// <b>失败不影响播放</b>：所有异常都被限制在弹幕子系统内部，最终表现为 <see cref="DanmakuConnectionState.Failed"/>，
/// 调用方（ShellViewModel）据此提示"弹幕不可用"即可，视频链路完全不受影响。
/// </para>
/// <para>
/// 关键实测结论（房间 21144080）：<c>getDanmuInfo</c> 必须带 WBI 签名 <b>且</b> Cookie 里要有 <c>buvid4</c>，
/// 否则返回 <c>-352</c>（风控拒绝）。<c>buvid4</c> 通过 <c>/x/frontend/finger/spi</c> 获取，无需登录。
/// </para>
/// </remarks>
public sealed class BilibiliDanmakuClient : IDanmakuClient
{
    /// <summary>批量聚合间隔（毫秒）：把这段时间内收到的消息合并成一次事件。</summary>
    private const int BatchIntervalMs = 100;

    /// <summary>单批最大条数（超过则分多次下发，避免单条消息过大）。</summary>
    private const int MaxBatchSize = 100;

    /// <summary>心跳间隔（秒）。</summary>
    private const int HeartbeatIntervalSeconds = 30;

    /// <summary>最大重连次数。</summary>
    private const int MaxReconnectAttempts = 3;

    /// <summary>重连退避序列（毫秒）。</summary>
    private static readonly int[] ReconnectDelayMs = [1000, 2000, 4000];

    /// <summary>WebSocket 接收缓冲区（字节）。</summary>
    private const int ReceiveBufferBytes = 64 * 1024;

    /// <summary>单帧消息上限（字节），超过视为异常并放弃该连接。</summary>
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>建立 WebSocket 连接的超时（秒）。</summary>
    private const int ConnectTimeoutSeconds = 15;

    /// <summary>等待认证回复的超时（秒）。</summary>
    private const int AuthTimeoutSeconds = 10;

    /// <summary>HTTP 请求超时（秒）。</summary>
    private const int HttpTimeoutSeconds = 15;

    /// <summary>日志模块名。</summary>
    private const string ModuleName = "App.Danmaku";

    /// <summary>B站直播 WebSocket 子协议路径。</summary>
    private const string WebSocketPath = "/sub";

    private readonly HttpClientFactory _httpClients;
    private readonly IStructuredLogger _logger;
    private readonly Lock _pendingGate = new();
    private readonly List<DanmakuMessage> _pending = [];

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _loopCts;
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;
    private Task? _batchLoop;
    private TaskCompletionSource<bool>? _authCompletion;
    private long _sequence;
    private long _receivedCount;
    private DanmakuConnectionState _state = DanmakuConnectionState.Disconnected;

    /// <summary>初始化弹幕客户端。</summary>
    /// <param name="httpClients">HTTP 客户端工厂（复用全局连接池与 UA 配置）。</param>
    /// <param name="logger">结构化日志。</param>
    public BilibiliDanmakuClient(HttpClientFactory httpClients, IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClients);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClients = httpClients;
        _logger = logger;
    }

    /// <inheritdoc />
    public DanmakuConnectionState State => _state;

    /// <inheritdoc />
    public long ReceivedCount => Interlocked.Read(ref _receivedCount);

    /// <inheritdoc />
    public event EventHandler<DanmakuBatchEventArgs>? MessagesReceived;

    /// <inheritdoc />
    public event EventHandler<DanmakuConnectionState>? StateChanged;

    /// <inheritdoc />
    public async Task ConnectAsync(DanmakuConnectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Platform != PlatformId.Bilibili)
        {
            throw new DanmakuException("connect", $"当前只支持哔哩哔哩弹幕，收到平台 {request.Platform}。");
        }

        if (!long.TryParse(request.RoomId, out long roomId) || roomId <= 0)
        {
            throw new DanmakuException("connect", $"房间号非法：{request.RoomId}");
        }

        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        SetState(DanmakuConnectionState.Connecting);

        CancellationTokenSource loopCts = new();
        _loopCts = loopCts;

        try
        {
            DanmakuSession session = await OpenSessionAsync(roomId, request.Cookie, cancellationToken).ConfigureAwait(false);
            _socket = session.Socket;
            _authCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            /*
              接收循环必须先于认证启动。
              认证是"发一包、等一包回复"的握手，没有读循环在跑，回复永远读不到，
              只会一直等到 AuthTimeoutSeconds 超时（表现为"弹幕连接失败：等待认证回复超时"）。
            */
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(session, loopCts.Token), CancellationToken.None);
            await AuthenticateAsync(session.Socket, session.Token, roomId, session.CookieHeader, cancellationToken).ConfigureAwait(false);

            _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(loopCts.Token), CancellationToken.None);
            _batchLoop = Task.Run(() => BatchLoopAsync(loopCts.Token), CancellationToken.None);

            SetState(DanmakuConnectionState.Connected);
            _logger.Info(ModuleName, "弹幕连接已建立。", new Dictionary<string, object?>
            {
                ["roomId"] = roomId,
                ["host"] = session.Host,
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(LogLevel.Warn, ModuleName, "弹幕连接失败。", exception);
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            SetState(DanmakuConnectionState.Failed);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? loopCts = _loopCts;
        _loopCts = null;
        if (loopCts is not null)
        {
            try
            {
                await loopCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // 已释放，忽略。
            }
        }

        ClientWebSocket? socket = _socket;
        _socket = null;
        if (socket is not null)
        {
            await CloseSocketAsync(socket, cancellationToken).ConfigureAwait(false);
        }

        Task[] loops = [.. new[] { _receiveLoop, _heartbeatLoop, _batchLoop }.Where(static task => task is not null)!];
        _receiveLoop = null;
        _heartbeatLoop = null;
        _batchLoop = null;
        if (loops.Length > 0)
        {
            try
            {
                await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                _logger.Debug(ModuleName, "弹幕循环停止超时（忽略）。", new Dictionary<string, object?>
                {
                    ["detail"] = exception.Message,
                });
            }
        }

        loopCts?.Dispose();
        lock (_pendingGate)
        {
            _pending.Clear();
        }

        SetState(DanmakuConnectionState.Disconnected);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<DanmakuSession> OpenSessionAsync(long roomId, string? cookie, CancellationToken cancellationToken)
    {
        HttpClient http = _httpClients.GetClient(HttpTimeoutSeconds);
        string cookieHeader = await BuildCookieAsync(http, cookie, cancellationToken).ConfigureAwait(false);
        DanmakuToken token = await FetchTokenAsync(http, roomId, cookieHeader, cancellationToken).ConfigureAwait(false);

        ClientWebSocket socket = new();
        socket.Options.SetRequestHeader("User-Agent", HttpClientFactory.DefaultUserAgent);
        socket.Options.SetRequestHeader("Origin", "https://live.bilibili.com");

        using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
        try
        {
            await socket.ConnectAsync(new Uri(token.WebSocketUrl), connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or UriFormatException)
        {
            socket.Dispose();
            throw new DanmakuException("websocket-connect", $"连接弹幕服务器失败：{exception.Message}", exception);
        }

        return new DanmakuSession(socket, token, cookieHeader);
    }

    private async Task<string> BuildCookieAsync(HttpClient http, string? cookie, CancellationToken cancellationToken)
    {
        string baseCookie = cookie ?? string.Empty;
        if (baseCookie.Contains("buvid4=", StringComparison.Ordinal) && baseCookie.Contains("buvid3=", StringComparison.Ordinal))
        {
            return baseCookie;
        }

        try
        {
            using JsonDocument document = await GetJsonAsync(
                http,
                "https://api.bilibili.com/x/frontend/finger/spi",
                string.Empty,
                cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("code", out JsonElement code) && code.GetInt32() == 0
                && root.TryGetProperty("data", out JsonElement data))
            {
                string buvid3 = ReadString(data, "b_3");
                string buvid4 = ReadString(data, "b_4");
                if (buvid3.Length > 0 && buvid4.Length > 0)
                {
                    string merged = baseCookie.Length == 0 ? string.Empty : baseCookie.TrimEnd(';', ' ') + "; ";
                    return merged + $"buvid3={buvid3}; buvid4={buvid4}; b_nut={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.Debug(ModuleName, "获取 buvid 指纹失败，回退为原始 Cookie。", new Dictionary<string, object?>
            {
                ["detail"] = exception.Message,
            });
        }

        return baseCookie;
    }

    private async Task<DanmakuToken> FetchTokenAsync(
        HttpClient http,
        long roomId,
        string cookie,
        CancellationToken cancellationToken)
    {
        (string imgKey, string subKey) = await FetchWbiKeysAsync(http, cookie, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> parameters = new(StringComparer.Ordinal)
        {
            ["id"] = roomId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["type"] = "0",
        };
        string query = BilibiliDanmakuProtocol.BuildWbiQuery(
            parameters,
            imgKey,
            subKey,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        using JsonDocument document = await GetJsonAsync(
            http,
            $"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?{query}",
            cookie,
            cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("code", out JsonElement code) || code.GetInt32() != 0)
        {
            string message = ReadString(root, "message");
            throw new DanmakuException("fetch-token", $"getDanmuInfo 被拒绝（{code.GetInt32()}）：{message}");
        }

        if (!root.TryGetProperty("data", out JsonElement data))
        {
            throw new DanmakuException("fetch-token", "getDanmuInfo 未返回 data。");
        }

        string token = ReadString(data, "token");
        if (token.Length == 0)
        {
            throw new DanmakuException("fetch-token", "getDanmuInfo 未返回 token。");
        }

        if (!data.TryGetProperty("host_list", out JsonElement hosts) || hosts.ValueKind != JsonValueKind.Array || hosts.GetArrayLength() == 0)
        {
            throw new DanmakuException("fetch-token", "getDanmuInfo 未返回可用节点。");
        }

        JsonElement host = hosts[0];
        string hostName = ReadString(host, "host");
        int wssPort = ReadInt(host, "wss_port");
        if (hostName.Length == 0 || wssPort <= 0)
        {
            throw new DanmakuException("fetch-token", "节点信息不完整（host/wss_port）。");
        }

        return new DanmakuToken(token, hostName, $"wss://{hostName}:{wssPort}{WebSocketPath}");
    }

    private async Task<(string ImgKey, string SubKey)> FetchWbiKeysAsync(
        HttpClient http,
        string cookie,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetJsonAsync(
            http,
            "https://api.bilibili.com/x/web-interface/nav",
            cookie,
            cancellationToken).ConfigureAwait(false);

        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("data", out JsonElement data)
            || !data.TryGetProperty("wbi_img", out JsonElement wbi))
        {
            throw new DanmakuException("wbi-keys", "nav 接口未返回 wbi_img。");
        }

        string imgUrl = ReadString(wbi, "img_url");
        string subUrl = ReadString(wbi, "sub_url");
        return BilibiliDanmakuProtocol.ExtractWbiKeys(imgUrl, subUrl);
    }

    private async Task AuthenticateAsync(
        ClientWebSocket socket,
        DanmakuToken token,
        long roomId,
        string cookie,
        CancellationToken cancellationToken)
    {
        long userId = ReadUserId(cookie);
        byte[] authPacket = BilibiliDanmakuProtocol.EncodeAuthPacket(token.Token, roomId, userId);
        // TCS 由 ConnectAsync 在启动接收循环之前创建：读循环要靠它把认证回复送回来。
        TaskCompletionSource<bool> completion = _authCompletion
            ?? throw new DanmakuException("auth", "认证状态未初始化。");

        await socket.SendAsync(authPacket, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);

        bool authenticated;
        try
        {
            authenticated = await completion.Task
                .WaitAsync(TimeSpan.FromSeconds(AuthTimeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new DanmakuException("auth", "等待认证回复超时。", exception);
        }

        if (!authenticated)
        {
            throw new DanmakuException("auth", "弹幕认证被拒绝。");
        }
    }

    private async Task ReceiveLoopAsync(DanmakuSession session, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[ReceiveBufferBytes];
        try
        {
            while (!cancellationToken.IsCancellationRequested && session.Socket.State == WebSocketState.Open)
            {
                byte[] frame = await ReceiveFrameAsync(session.Socket, buffer, cancellationToken).ConfigureAwait(false);
                if (frame.Length == 0)
                {
                    break;
                }

                foreach (DanmakuPacket packet in BilibiliDanmakuProtocol.DecodePackets(frame))
                {
                    HandlePacket(session.Socket, packet);
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.Debug(ModuleName, "弹幕接收中断。", new Dictionary<string, object?>
                {
                    ["detail"] = exception.Message,
                });
            }
        }
        finally
        {
            _authCompletion?.TrySetResult(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                SetState(DanmakuConnectionState.Failed);
            }
        }
    }

    private void HandlePacket(ClientWebSocket socket, DanmakuPacket packet)
    {
        switch (packet.Operation)
        {
            case BilibiliDanmakuProtocol.OperationAuthReply:
                bool ok = BilibiliDanmakuProtocol.IsAuthSucceeded(packet);
                _authCompletion?.TrySetResult(ok);
                if (!ok)
                {
                    _logger.Warn(ModuleName, "弹幕认证被拒绝。", new Dictionary<string, object?>
                    {
                        ["detail"] = Encoding.UTF8.GetString(packet.Body, 0, Math.Min(packet.Body.Length, 200)),
                    });
                }

                break;

            case BilibiliDanmakuProtocol.OperationMessage:
                DanmakuMessage? message = BilibiliDanmakuMessageParser.Parse(
                    packet.Body,
                    Interlocked.Increment(ref _sequence));
                if (message is not null)
                {
                    Interlocked.Increment(ref _receivedCount);
                    lock (_pendingGate)
                    {
                        _pending.Add(message);
                    }
                }

                break;

            case BilibiliDanmakuProtocol.OperationHeartbeatReply:
                // 心跳回复只用于确认链路存活，人气值不参与展示。
                break;

            default:
                // 其他操作码（如推送公告）当前不处理。
                break;
        }

        _ = socket;
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        byte[] heartbeat = BilibiliDanmakuProtocol.EncodeHeartbeatPacket();
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(HeartbeatIntervalSeconds));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                ClientWebSocket? socket = _socket;
                if (socket is null || socket.State != WebSocketState.Open)
                {
                    return;
                }

                await socket.SendAsync(heartbeat, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // 正常停止或链路已断：由接收循环负责置 Failed。
        }
    }

    private async Task BatchLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(BatchIntervalMs));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                FlushPending();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    private void FlushPending()
    {
        List<DanmakuMessage> batch;
        lock (_pendingGate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            int take = Math.Min(_pending.Count, MaxBatchSize);
            batch = _pending.GetRange(0, take);
            _pending.RemoveRange(0, take);
        }

        try
        {
            MessagesReceived?.Invoke(this, new DanmakuBatchEventArgs(batch));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 订阅方异常不能拖垮接收循环。
            _logger.LogError(LogLevel.Warn, ModuleName, "弹幕批量事件处理失败。", exception);
        }
    }

    private async Task<byte[]> ReceiveFrameAsync(ClientWebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        using MemoryStream frame = new();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return [];
            }

            frame.Write(buffer, 0, result.Count);
            if (frame.Length > MaxMessageBytes)
            {
                throw new DanmakuException("receive", "单帧消息超过上限，放弃该连接。");
            }

            if (result.EndOfMessage)
            {
                return frame.ToArray();
            }
        }
    }

    private async Task CloseSocketAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client-close", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 关闭失败不影响资源释放。
        }
        finally
        {
            socket.Dispose();
        }
    }

    private async Task<JsonDocument> GetJsonAsync(
        HttpClient http,
        string url,
        string cookie,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", HttpClientFactory.DefaultUserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void SetState(DanmakuConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(LogLevel.Warn, ModuleName, "弹幕状态事件处理失败。", exception);
        }
    }

    private static long ReadUserId(string cookie)
    {
        foreach (string pair in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (pair.StartsWith("DedeUserID=", StringComparison.Ordinal)
                && long.TryParse(pair["DedeUserID=".Length..], out long userId))
            {
                return userId;
            }
        }

        return 0;
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    /// <summary>一次弹幕会话的凭证与连接信息。</summary>
    private sealed record DanmakuToken(string Token, string Host, string WebSocketUrl);

    /// <summary>一次已建立的弹幕会话。</summary>
    /// <param name="Socket">已连接的 WebSocket。</param>
    /// <param name="Token">getDanmuInfo 返回的令牌与节点。</param>
    /// <param name="CookieHeader">建立会话时实际使用的 Cookie（含补全的 buvid），认证时要读 uid。</param>
    private sealed record DanmakuSession(ClientWebSocket Socket, DanmakuToken Token, string CookieHeader)
    {
        /// <summary>当前节点主机名（仅用于日志）。</summary>
        public string Host => Token.Host;
    }
}
