namespace StreamPilot.Bridge;

using System.Net;
using System.Net.Sockets;
using System.Text;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Errors;
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

    /// <summary>客户端断开后可继续等待上游的最长时间（秒）。</summary>
    private const int IdleTimeoutSeconds = 30;

    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Bridge.Host";
    private readonly BridgeOptions _bridgeOptions;
    private readonly RelayRegistry _registry;
    private readonly MpvLauncher _mpvLauncher;
    private readonly Func<PlaybackOptions?> _playbackOptionsProvider;
    private readonly HttpClient _relayClient;
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
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            // 中继是长连接：整体超时交由读取消与客户端断开处理，这里只限制"建连+首字节"。
            Timeout = TimeSpan.FromSeconds(IdleTimeoutSeconds),
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
    public void ReleaseRelay(string localUrl) => _registry.ReleaseByLocalUrl(localUrl);

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

        RelayTarget target = new()
        {
            UpstreamUrl = upstream,
            Referer = ExtractJsonString(body, "referer"),
            ContentType = ExtractJsonString(body, "contentType"),
        };
        string localUrl = RegisterRelay(target);
        await WriteTextAsync(context, HttpStatusCode.OK, $"{{\"localUrl\":\"{localUrl}\"}}").ConfigureAwait(false);
    }

    private async Task StreamRelayAsync(HttpListenerContext context, RelayTarget target)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, target.UpstreamUrl);
        if (!string.IsNullOrWhiteSpace(target.Referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", target.Referer);
        }

        string? rangeHeader = context.Request.Headers["Range"];
        if (!string.IsNullOrWhiteSpace(rangeHeader))
        {
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);
        }

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

        await using Stream upstream = await response.Content.ReadAsStreamAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            await upstream.CopyToAsync(context.Response.OutputStream, _shutdown.Token).ConfigureAwait(false);
        }
        catch (HttpListenerException exception)
        {
            _logger.Debug(_moduleName, "客户端提前断开中继连接。", new Dictionary<string, object?>
            {
                ["detail"] = exception.Message,
            });
        }
    }

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
