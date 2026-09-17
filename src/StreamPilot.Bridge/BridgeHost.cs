namespace StreamPilot.Bridge;

using System.Net;
using System.Net.Sockets;
using System.Text;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Errors;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Runtime;
using StreamPilot.Core.Services;

/// <summary>
/// 内嵌的本地桥接服务（只用 <see cref="HttpListener"/>，只监听 127.0.0.1）。
/// </summary>
/// <remarks>
/// <para>提供的能力：</para>
/// <list type="bullet">
///   <item><c>GET /health</c>：存活探测与版本信息；</item>
///   <item><c>GET /play?url=&amp;title=&amp;referer=</c>：调用 mpv 外挂播放；</item>
///   <item><c>POST /relay</c> + <c>GET /relay/{token}</c>：为需要 Referer 的流提供本地中继；</item>
///   <item><c>OPTIONS</c>：CORS 预检（仅预检响应携带 <c>Access-Control-Allow-Private-Network</c>）。</item>
/// </list>
/// <para>安全约束：</para>
/// <list type="bullet">
///   <item>前缀固定为 <c>http://127.0.0.1:{port}/</c>，由 <see cref="LoopbackOnlyGuard"/> 强制校验；</item>
///   <item>不引入 ASP.NET Core，避免额外框架依赖与体积；</item>
///   <item>所有处理器都有 try/catch，异常记录日志并返回结构化错误，不吞异常。</item>
/// </list>
/// </remarks>
public sealed class BridgeHost : IPlaybackBridge, IAsyncDisposable
{
    /// <summary>中继路由前缀。</summary>
    public const string RelayPathPrefix = "/relay";

    /// <summary>播放路由。</summary>
    public const string PlayPath = "/play";

    /// <summary>健康检查路由。</summary>
    public const string HealthPath = "/health";

    /// <summary>请求体最大字节数（防止被本机恶意页面塞入超大请求）。</summary>
    private const int MaxRequestBodyBytes = 8 * 1024;

    /// <summary>中继上游连续无数据多久判定断流（秒）。</summary>
    private const int IdleTimeoutSeconds = 30;

    /// <summary>中继转发缓冲区大小（字节）。</summary>
    private const int RelayBufferBytes = 64 * 1024;

    /// <summary>播放列表改写时的预留容量（避免边拼接边扩容）。</summary>
    private const int PlaylistRewriteHeadroomBytes = 4096;

    /// <summary>建立上游连接的超时（秒）。</summary>
    private const int UpstreamConnectTimeoutSeconds = 10;

    /// <summary>HLS 播放列表响应的内容类型。</summary>
    private const string HlsPlaylistContentType = "application/vnd.apple.mpegurl; charset=utf-8";

    /// <summary>中继请求体里表示 HLS 播放列表的 kind 取值。</summary>
    private const string PlaylistKindValue = "hls";

    /// <summary>请求头名称：User-Agent。</summary>
    private const string HeaderNameUserAgent = "User-Agent";

    /// <summary>请求头名称：Referer。</summary>
    private const string HeaderNameReferer = "Referer";

    /// <summary>请求头名称：Range。</summary>
    private const string HeaderNameRange = "Range";

    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Bridge.Host";
    private readonly BridgeOptions _bridgeOptions;
    private readonly RelayRegistry _registry;
    private readonly MpvLauncher _mpvLauncher;
    private readonly Func<PlaybackOptions?> _playbackOptionsProvider;
    private readonly HttpClient _relayClient;

    /// <summary>播放列表地址 → 该列表上一次改写登记的子节点本地地址。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _playlistChildren = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _outputLock = new(1, 1);
    private HttpListener? _listener;
    private Task? _acceptLoop;
    private bool _disposed;

    /// <summary>初始化桥接服务。</summary>
    /// <param name="bridgeOptions">桥接配置。</param>
    /// <param name="playbackOptionsProvider">获取最新播放配置的回调（用于 mpv 路径等）。</param>
    /// <param name="logger">结构化日志。</param>
    public BridgeHost(
        BridgeOptions bridgeOptions,
        Func<PlaybackOptions?> playbackOptionsProvider,
        IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(bridgeOptions);
        ArgumentNullException.ThrowIfNull(playbackOptionsProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _bridgeOptions = bridgeOptions;
        _playbackOptionsProvider = playbackOptionsProvider;
        _logger = logger;
        _registry = new RelayRegistry(logger);
        _mpvLauncher = new MpvLauncher(logger);
        _relayClient = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.None,

            // 中继拉的是直播长连接：连接池的"回收寿命"会把一条正在读的流一起换掉，
            // 表现为固定时长的"看着看着断一下"（页面随后重连）。
            // 这里显式关闭回收，连接何时结束只由流本身与空闲超时决定。
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            ConnectTimeout = TimeSpan.FromSeconds(UpstreamConnectTimeoutSeconds),
        })
        {
            // 中继是长连接：整体超时必须关闭，否则 HttpClient 会在 30 秒后连直播流一起取消；
            // 断流判定改由 PumpWithIdleTimeoutAsync 的"空闲超时"负责。
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <inheritdoc />
    public bool IsRunning => _listener?.IsListening ?? false;

    /// <inheritdoc />
    public int Port { get; private set; }

    /// <inheritdoc />
    public string BaseAddress => IsRunning ? $"http://{BridgeConstants.LoopbackHost}:{Port}" : string.Empty;

    /// <summary>mpv 可执行文件解析结果（供 UI 提示使用）。</summary>
    public string? MpvPath => _mpvLauncher.ResolvedPath;

    /// <summary>当前中继注册数量。</summary>
    public int RelayCount => _registry.Count;

    /// <summary>
    /// 启动监听。端口依次尝试 <c>PreferredPort..MaxPort</c>，全部占用时由系统分配。
    /// </summary>
    /// <exception cref="BridgeException">无法绑定任何端口时抛出。</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            return;
        }

        int preferred = Math.Clamp(_bridgeOptions.PreferredPort, 1024, 65535);
        int maximum = Math.Clamp(Math.Max(preferred, _bridgeOptions.MaxPort), preferred, 65535);
        HttpListenerException? lastError = null;

        for (int port = preferred; port <= maximum; port++)
        {
            if (TryStartOnPort(port, out int boundPort, out HttpListenerException? error))
            {
                Port = boundPort;
                _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token), CancellationToken.None);
                _logger.Info(_moduleName, "桥接服务已启动。", new Dictionary<string, object?>
                {
                    ["address"] = BaseAddress,
                });
                return;
            }

            lastError = error;
        }

        if (TryStartOnPort(GetEphemeralPort(), out int ephemeralPort, out HttpListenerException? ephemeralError))
        {
            Port = ephemeralPort;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token), CancellationToken.None);
            _logger.Info(_moduleName, "桥接服务已启动（系统分配端口）。", new Dictionary<string, object?>
            {
                ["address"] = BaseAddress,
            });
            return;
        }

        throw new BridgeException(
            "start-listener",
            $"桥接服务启动失败（{preferred}-{maximum} 端口全部不可用）。",
            lastError ?? ephemeralError);
    }

    /// <inheritdoc />
    public string RegisterRelay(RelayTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!IsRunning)
        {
            throw new BridgeException("register-relay", "桥接服务未启动，无法注册中继。");
        }

        string token = _registry.Register(target);
        return $"{BaseAddress}{RelayPathPrefix}/{token}";
    }

    /// <inheritdoc />
    public void ReleaseRelay(string localUrl)
    {
        if (!string.IsNullOrWhiteSpace(localUrl))
        {
            // 释放播放列表时连同它上一次改写登记的子节点一起回收。
            foreach (KeyValuePair<string, List<string>> entry in _playlistChildren)
            {
                if (entry.Value.Contains(localUrl, StringComparer.Ordinal))
                {
                    ReleasePlaylistChildren(entry.Key);
                }
            }
        }

        _registry.ReleaseByLocalUrl(localUrl);
    }

    /// <inheritdoc />
    public Task<bool> PlayWithMpvAsync(string url, string? title, string? referer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlaybackOptions options = _playbackOptionsProvider() ?? new PlaybackOptions();
        return Task.FromResult(_mpvLauncher.Launch(url, title, referer, options));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
        _registry.Clear();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 关闭过程中的取消属于正常路径。
            }
        }

        _shutdown.Dispose();
        _relayClient.Dispose();
        _outputLock.Dispose();
        _logger.Info(_moduleName, "桥接服务已停止。");
    }

    private bool TryStartOnPort(int port, out int boundPort, out HttpListenerException? error)
    {
        boundPort = 0;
        error = null;
        if (port is < 1024 or > 65535)
        {
            return false;
        }

        string prefix = LoopbackOnlyGuard.BuildPrefix(port);
        LoopbackOnlyGuard.EnsureLoopback(prefix);
        HttpListener listener = new();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
            _listener = listener;
            boundPort = port;
            return true;
        }
        catch (HttpListenerException exception)
        {
            error = exception;
            listener.Close();
            _logger.Debug(_moduleName, "端口被占用，尝试下一个端口。", new Dictionary<string, object?>
            {
                ["port"] = port,
            });
            return false;
        }
    }

    private static int GetEphemeralPort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        HttpListener? listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (context is null)
            {
                continue;
            }

            _ = Task.Run(() => HandleContextAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            if (string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                await WriteEmptyAsync(context, HttpStatusCode.NoContent, includePrivateNetworkHeader: true).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith(RelayPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                await HandleRelayAsync(context, path).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, PlayPath, StringComparison.OrdinalIgnoreCase))
            {
                await HandlePlayAsync(context).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, HealthPath, StringComparison.OrdinalIgnoreCase))
            {
                await HandleHealthAsync(context).ConfigureAwait(false);
                return;
            }

            await WriteTextAsync(context, HttpStatusCode.NotFound, "{\"error\":\"not-found\"}").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is BridgeException or IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogError(LogLevel.Warn, _moduleName, "处理桥接请求失败。", exception, new Dictionary<string, object?>
            {
                ["path"] = context.Request.Url?.AbsolutePath,
            });

            HttpStatusCode status = exception is BridgeException ? HttpStatusCode.BadGateway : HttpStatusCode.InternalServerError;
            try
            {
                await WriteTextAsync(
                    context,
                    status,
                    exception is BridgeException ? "{\"error\":\"bridge-unavailable\"}" : "{\"error\":\"internal\"}").ConfigureAwait(false);
            }
            catch (Exception writeFailure) when (writeFailure is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                _logger.LogError(LogLevel.Debug, _moduleName, "写入错误响应失败（客户端可能已断开）。", writeFailure);
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (ObjectDisposedException)
            {
                // 已关闭。
            }
        }
    }

    private async Task HandleHealthAsync(HttpListenerContext context)
    {
        string body = $"{{\"status\":\"ok\",\"version\":\"{AppVersion.Current}\",\"port\":{Port},\"relayCount\":{_registry.Count}}}";
        await WriteTextAsync(context, HttpStatusCode.OK, body).ConfigureAwait(false);
    }

    private async Task HandlePlayAsync(HttpListenerContext context)
    {
        string? url = context.Request.QueryString["url"];
        if (string.IsNullOrWhiteSpace(url) || !IsAllowedStreamUrl(url))
        {
            await WriteTextAsync(context, HttpStatusCode.BadRequest, "{\"error\":\"invalid-url\"}").ConfigureAwait(false);
            return;
        }

        string? title = context.Request.QueryString["title"];
        string? referer = context.Request.QueryString["referer"];
        try
        {
            bool started = await PlayWithMpvAsync(url, title, referer, CancellationToken.None).ConfigureAwait(false);
            if (started)
            {
                await WriteTextAsync(context, HttpStatusCode.OK, "ok").ConfigureAwait(false);
                return;
            }

            await WriteTextAsync(context, HttpStatusCode.InternalServerError, "{\"error\":\"mpv-not-available\"}").ConfigureAwait(false);
        }
        catch (BridgeException exception)
        {
            _logger.LogError(LogLevel.Error, _moduleName, "mpv 启动失败。", exception);
            await WriteTextAsync(context, HttpStatusCode.InternalServerError, "{\"error\":\"mpv-launch-failed\"}").ConfigureAwait(false);
        }
    }

    private async Task HandleRelayAsync(HttpListenerContext context, string path)
    {
        if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRelayRegisterAsync(context).ConfigureAwait(false);
            return;
        }

        string token = path[RelayPathPrefix.Length..].Trim('/');
        if (token.Length == 0)
        {
            await WriteTextAsync(context, HttpStatusCode.BadRequest, "{\"error\":\"missing-token\"}").ConfigureAwait(false);
            return;
        }

        if (!_registry.TryResolve(token, out RelayTarget? target) || target is null)
        {
            await WriteTextAsync(context, HttpStatusCode.NotFound, "{\"error\":\"unknown-relay\"}").ConfigureAwait(false);
            return;
        }

        await StreamRelayAsync(context, target).ConfigureAwait(false);
    }

    private async Task HandleRelayRegisterAsync(HttpListenerContext context)
    {
        string body;
        using (StreamReader reader = new(context.Request.InputStream, Encoding.UTF8))
        {
            char[] buffer = new char[MaxRequestBodyBytes];
            int read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
            body = new string(buffer, 0, read);
        }

        if (body.Length == 0)
        {
            await WriteTextAsync(context, HttpStatusCode.BadRequest, "{\"error\":\"empty-body\"}").ConfigureAwait(false);
            return;
        }

        string? upstream = ExtractJsonString(body, "url");
        if (string.IsNullOrWhiteSpace(upstream) || !IsAllowedStreamUrl(upstream))
        {
            await WriteTextAsync(context, HttpStatusCode.BadRequest, "{\"error\":\"invalid-url\"}").ConfigureAwait(false);
            return;
        }

        string? kindValue = ExtractJsonString(body, "kind");
        RelayTarget target = new()
        {
            UpstreamUrl = upstream,
            Referer = ExtractJsonString(body, "referer"),
            ContentType = ExtractJsonString(body, "contentType"),
            Kind = string.Equals(kindValue, PlaylistKindValue, StringComparison.OrdinalIgnoreCase)
                ? RelayKind.HlsPlaylist
                : RelayKind.Stream,
        };
        string localUrl = RegisterRelay(target);
        await WriteTextAsync(context, HttpStatusCode.OK, $"{{\"localUrl\":\"{localUrl}\"}}").ConfigureAwait(false);
    }

    /// <summary>
    /// 构造发往上游 CDN 的请求：注入浏览器 <c>User-Agent</c>，并按需注入 <c>Referer</c> 与 <c>Range</c>。
    /// </summary>
    /// <param name="target">中继目标。</param>
    /// <param name="rangeHeader">客户端传来的 <c>Range</c> 头，可为 <see langword="null"/>。</param>
    /// <returns>可直接发送的请求（调用方负责释放）。</returns>
    /// <remarks>
    /// <para>
    /// <c>User-Agent</c> 是**必需**的，不是可选优化：<see cref="HttpClient"/> 默认不发送 UA，
    /// 而 B站 CDN 的部分节点（如 <c>d1--cn-gotcha104.bilivideo.com</c>、
    /// <c>d1--cn-gotcha04b.bilivideo.com</c>、<c>cn-zjhz-cm-01-08.bilivideo.com</c>）对
    /// 不带 UA 的请求一律回 <c>403</c>（room_id=814 实测，见
    /// <c>docs/adr/0006-relay-upstream-headers.md</c>）。
    /// 缺 UA 时中继的每条线路都会 403，表现为"探测判定全部线路不可用"与真实播放同时失败，
    /// 而 mpv 播放同一地址正常（mpv 自带 UA）——这正是本方法的由来。
    /// </para>
    /// <para>
    /// HLS 播放列表与切片走同一个方法，保证两者的请求头完全一致；
    /// 播放列表里的子地址由 <see cref="RegisterChild"/> 继承 <see cref="RelayTarget.Referer"/>，
    /// 因此切片请求同样带 UA 与 Referer。
    /// </para>
    /// </remarks>
    internal static HttpRequestMessage CreateUpstreamRequest(RelayTarget target, string? rangeHeader)
    {
        ArgumentNullException.ThrowIfNull(target);
        HttpRequestMessage request = new(HttpMethod.Get, target.UpstreamUrl);
        request.Headers.TryAddWithoutValidation(HeaderNameUserAgent, HttpClientFactory.DefaultUserAgent);
        if (!string.IsNullOrWhiteSpace(target.Referer))
        {
            request.Headers.TryAddWithoutValidation(HeaderNameReferer, target.Referer);
        }

        if (!string.IsNullOrWhiteSpace(rangeHeader))
        {
            request.Headers.TryAddWithoutValidation(HeaderNameRange, rangeHeader);
        }

        return request;
    }

    private async Task StreamRelayAsync(HttpListenerContext context, RelayTarget target)
    {
        // 中继响应必须带 CORS 头：页面源是 https://appassets.local，而中继在 http://127.0.0.1，
        // 少了 Access-Control-Allow-Origin 时浏览器会直接以 "Failed to fetch" 拒绝，线路全部显示不可用。
        ApplyCorsHeaders(context, includePrivateNetworkHeader: false);

        if (target.Kind == RelayKind.HlsPlaylist)
        {
            await ServePlaylistAsync(context, target).ConfigureAwait(false);
            return;
        }

        using HttpRequestMessage request = CreateUpstreamRequest(target, context.Request.Headers[HeaderNameRange]);

        using HttpResponseMessage response = await _relayClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _shutdown.Token)
            .ConfigureAwait(false);

        context.Response.StatusCode = (int)response.StatusCode;
        string? contentType = response.Content.Headers.ContentType?.ToString() ?? target.ContentType;
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            context.Response.ContentType = contentType;
        }

        if (response.Content.Headers.ContentLength is { } length)
        {
            context.Response.ContentLength64 = length;
        }

        CopyOptionalHeader(response, context, "Accept-Ranges");
        CopyOptionalHeader(response, context, "Content-Range");
        CopyOptionalHeader(response, context, "Content-Encoding");

        await using Stream upstream = await response.Content.ReadAsStreamAsync(_shutdown.Token).ConfigureAwait(false);
        await PumpWithIdleTimeoutAsync(upstream, context, target).ConfigureAwait(false);
    }

    /// <summary>
    /// 拉取 HLS 播放列表并把其中的切片地址改写成本地中继地址。
    /// </summary>
    /// <param name="context">当前请求。</param>
    /// <param name="target">播放列表中继目标。</param>
    /// <remarks>
    /// 只中继 m3u8 本身没用：播放列表里的切片地址如果仍指向 <c>http://</c> CDN，
    /// 页面照样会因为混合内容被拦下，所以这里逐行改写（含 <c>#EXT-X-KEY</c>/<c>#EXT-X-MAP</c> 的 URI）。
    /// </remarks>
    private async Task ServePlaylistAsync(HttpListenerContext context, RelayTarget target)
    {
        using HttpRequestMessage request = CreateUpstreamRequest(target, rangeHeader: null);

        using HttpResponseMessage response = await _relayClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, _shutdown.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentLength64 = 0;
            return;
        }

        string playlist = await response.Content.ReadAsStringAsync(_shutdown.Token).ConfigureAwait(false);
        string rewritten = RewritePlaylist(playlist, target);
        byte[] payload = Encoding.UTF8.GetBytes(rewritten);

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = HlsPlaylistContentType;
        context.Response.ContentLength64 = payload.Length;
        await _outputLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        }
        finally
        {
            _outputLock.Release();
        }
    }

    /// <summary>
    /// 改写播放列表：把每个切片/子播放列表地址注册成新的本地中继并替换。
    /// </summary>
    /// <param name="playlist">上游播放列表文本。</param>
    /// <param name="target">播放列表中继目标。</param>
    /// <returns>改写后的播放列表文本。</returns>
    private string RewritePlaylist(string playlist, RelayTarget target)
    {
        ReleasePlaylistChildren(target.UpstreamUrl);

        List<string> children = [];
        StringBuilder builder = new(playlist.Length + PlaylistRewriteHeadroomBytes);
        foreach (string rawLine in playlist.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                builder.Append('\n');
                continue;
            }

            if (line[0] == '#')
            {
                builder.Append(RewritePlaylistTag(line, target, children)).Append('\n');
                continue;
            }

            builder.Append(RegisterChild(ResolveChildUrl(target.UpstreamUrl, line), target, children)).Append('\n');
        }

        _playlistChildren[target.UpstreamUrl] = children;
        return builder.ToString();
    }

    /// <summary>改写带 URI 属性的标签（密钥、初始化段、备用音视频轨）。</summary>
    /// <param name="line">标签行。</param>
    /// <param name="target">播放列表中继目标。</param>
    /// <param name="children">本次改写登记的本地地址集合。</param>
    /// <returns>改写后的标签行。</returns>
    private string RewritePlaylistTag(string line, RelayTarget target, List<string> children)
    {
        const string AttributePrefix = "URI=\"";
        int index = line.IndexOf(AttributePrefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return line;
        }

        int valueStart = index + AttributePrefix.Length;
        int valueEnd = line.IndexOf('"', valueStart);
        if (valueEnd < 0)
        {
            return line;
        }

        string childUrl = ResolveChildUrl(target.UpstreamUrl, line[valueStart..valueEnd]);
        string localUrl = RegisterChild(childUrl, target, children);
        return string.Concat(line[..valueStart], localUrl, line[valueEnd..]);
    }

    /// <summary>把切片地址注册成本地中继地址，并记录到本次播放列表的子节点集合。</summary>
    /// <param name="childUrl">切片或子播放列表的绝对地址。</param>
    /// <param name="target">父中继目标。</param>
    /// <param name="children">本次改写登记的本地地址集合。</param>
    /// <returns>本地中继地址。</returns>
    private string RegisterChild(string childUrl, RelayTarget target, List<string> children)
    {
        RelayKind kind = IsPlaylistUrl(childUrl) ? RelayKind.HlsPlaylist : RelayKind.Stream;
        string localUrl = _registry.Register(new RelayTarget
        {
            UpstreamUrl = childUrl,
            Referer = target.Referer,
            Kind = kind,
        });
        children.Add(localUrl);
        return localUrl;
    }

    /// <summary>释放某个播放列表上一次改写登记的子节点。</summary>
    /// <param name="playlistUrl">播放列表上游地址。</param>
    private void ReleasePlaylistChildren(string playlistUrl)
    {
        if (!_playlistChildren.TryRemove(playlistUrl, out List<string>? previous))
        {
            return;
        }

        foreach (string url in previous)
        {
            _registry.ReleaseByLocalUrl(url);
        }
    }

    /// <summary>判断地址是否指向 HLS 播放列表。</summary>
    /// <param name="url">地址。</param>
    /// <returns>是播放列表返回 <see langword="true"/>。</returns>
    private static bool IsPlaylistUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    /// <summary>把播放列表里的相对地址解析为绝对地址。</summary>
    /// <param name="playlistUrl">播放列表地址（作为基地址）。</param>
    /// <param name="child">播放列表里的地址片段。</param>
    /// <returns>绝对地址；无法解析时返回原片段。</returns>
    private static string ResolveChildUrl(string playlistUrl, string child)
    {
        string trimmed = child.Trim();
        return Uri.TryCreate(new Uri(playlistUrl), trimmed, out Uri? absolute) ? absolute.ToString() : trimmed;
    }

    /// <summary>
    /// 以"空闲超时"方式把上游数据搬到客户端：只要持续有数据就不计时，
    /// 连续 <see cref="IdleTimeoutSeconds"/> 秒没有新数据才判定断流。
    /// </summary>
    /// <param name="upstream">上游数据流。</param>
    /// <param name="context">当前请求。</param>
    /// <param name="target">中继目标。</param>
    private async Task PumpWithIdleTimeoutAsync(Stream upstream, HttpListenerContext context, RelayTarget target)
    {
        byte[] buffer = new byte[RelayBufferBytes];
        try
        {
            while (true)
            {
                using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));
                int read = await upstream.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), _shutdown.Token).ConfigureAwait(false);
            }

            await context.Response.OutputStream.FlushAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            _logger.Warn(_moduleName, "中继上游长时间无数据，已断开。", new Dictionary<string, object?>
            {
                ["host"] = TryGetHost(target.UpstreamUrl),
            });
        }
        catch (HttpListenerException exception)
        {
            _logger.Debug(_moduleName, "客户端提前断开中继连接。", new Dictionary<string, object?>
            {
                ["detail"] = exception.Message,
            });
        }
    }

    /// <summary>复制上游响应头（不存在时跳过）。</summary>
    /// <param name="response">上游响应。</param>
    /// <param name="context">当前请求。</param>
    /// <param name="name">头名。</param>
    private static void CopyOptionalHeader(HttpResponseMessage response, HttpListenerContext context, string name)
    {
        if (response.Headers.TryGetValues(name, out IEnumerable<string>? values))
        {
            context.Response.Headers[name] = string.Join(", ", values);
        }
    }

    /// <summary>取地址主机名（日志用，不含签名参数）。</summary>
    /// <param name="url">地址。</param>
    /// <returns>主机名；地址非法时返回 <see langword="null"/>。</returns>
    private static string? TryGetHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : null;

    private async Task WriteTextAsync(HttpListenerContext context, HttpStatusCode status, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = payload.Length;
        ApplyCorsHeaders(context, includePrivateNetworkHeader: false);

        await _outputLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        }
        finally
        {
            _outputLock.Release();
        }
    }

    private static Task WriteEmptyAsync(HttpListenerContext context, HttpStatusCode status, bool includePrivateNetworkHeader)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentLength64 = 0;
        ApplyCorsHeaders(context, includePrivateNetworkHeader);
        return Task.CompletedTask;
    }

    private static void ApplyCorsHeaders(HttpListenerContext context, bool includePrivateNetworkHeader)
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, HEAD, OPTIONS";
        context.Response.Headers["Access-Control-Allow-Headers"] = "*";
        context.Response.Headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges";
        context.Response.Headers["Cache-Control"] = "no-store";

        // 规范要求该头只出现在预检响应上。
        if (includePrivateNetworkHeader)
        {
            context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
        }
    }

    private static bool IsAllowedStreamUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return uri.Scheme is "http" or "https" or "rtmp" or "rtmps";
    }

    private static string? ExtractJsonString(string json, string key)
    {
        string needle = "\"" + key + "\"";
        int keyIndex = json.IndexOf(needle, StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return null;
        }

        int colon = json.IndexOf(':', keyIndex + needle.Length);
        if (colon < 0)
        {
            return null;
        }

        int start = json.IndexOf('"', colon + 1);
        if (start < 0)
        {
            return null;
        }

        StringBuilder builder = new();
        for (int index = start + 1; index < json.Length; index++)
        {
            char character = json[index];
            if (character == '\\' && index + 1 < json.Length)
            {
                index++;
                char escaped = json[index];
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    _ => escaped,
                });
                continue;
            }

            if (character == '"')
            {
                return builder.ToString();
            }

            builder.Append(character);
        }

        return null;
    }
}
