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
///   <item><c>GET /relay/{token}</c>：为需要 Referer 的流提供本地中继（**只读**，注册只能进程内发起）；</item>
///   <item><c>OPTIONS</c>：CORS 预检（仅预检响应携带 <c>Access-Control-Allow-Private-Network</c>）。</item>
/// </list>
/// <para>安全约束：</para>
/// <list type="bullet">
///   <item>前缀固定为 <c>http://127.0.0.1:{port}/</c>，由 <see cref="LoopbackOnlyGuard"/> 强制校验；</item>
///   <item>不引入 ASP.NET Core，避免额外框架依赖与体积；</item>
///   <item><c>Access-Control-Allow-Origin</c> 只回播放页的实际来源（<see cref="PlayerPageOrigin"/>），不再使用 <c>*</c>；</item>
///   <item>中继目的地址只允许公网 <c>http</c>/<c>https</c>（见 <see cref="IsAllowedUpstreamUrl(string)"/>）；</item>
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

    /// <summary>
    /// 播放页的来源（WebView2 虚拟主机映射，见 <c>WebPlayerHost.VirtualHostBase</c>）。
    /// </summary>
    /// <remarks>
    /// 中继响应只回这一个来源：过去用 <c>*</c>，本机任意网页都能注册中继并读回数据。
    /// 固定来源同样满足播放页的跨源读取（页面源就是它），因此不引入额外复杂度。
    /// </remarks>
    public const string PlayerPageOrigin = "https://appassets.local";

    /// <summary>等上游响应头的超时（秒）。</summary>
    private const int UpstreamHeaderTimeoutSeconds = 10;

    /// <summary>拉取 HLS 播放列表正文的超时（秒）。</summary>
    private const int PlaylistDownloadTimeoutSeconds = 15;

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

    /// <summary>请求头名称：User-Agent。</summary>
    private const string HeaderNameUserAgent = "User-Agent";

    /// <summary>请求头名称：Referer。</summary>
    private const string HeaderNameReferer = "Referer";

    /// <summary>请求头名称：Range。</summary>
    private const string HeaderNameRange = "Range";

    /// <summary>请求头名称：Origin。</summary>
    private const string HeaderNameOrigin = "Origin";

    /// <summary>预检请求头名称：声明要访问私有网络（Chromium PNA）。</summary>
    private const string HeaderNameRequestPrivateNetwork = "Access-Control-Request-Private-Network";

    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Bridge.Host";
    private readonly BridgeOptions _bridgeOptions;
    private readonly RelayRegistry _registry;
    private readonly MpvLauncher _mpvLauncher;
    private readonly Func<PlaybackOptions?> _playbackOptionsProvider;
    private readonly HttpClient _relayClient;

    /// <summary>等上游响应头的超时。</summary>
    private readonly TimeSpan _upstreamHeaderTimeout;

    /// <summary>拉取 HLS 播放列表正文的超时。</summary>
    private readonly TimeSpan _playlistDownloadTimeout;

    /// <summary>中继上游连续无数据的断流判定窗口。</summary>
    private readonly TimeSpan _idleTimeout;

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
        : this(
            bridgeOptions,
            playbackOptionsProvider,
            logger,
            relayHandler: null,
            TimeSpan.FromSeconds(UpstreamHeaderTimeoutSeconds),
            TimeSpan.FromSeconds(PlaylistDownloadTimeoutSeconds),
            TimeSpan.FromSeconds(IdleTimeoutSeconds))
    {
    }

    /// <summary>
    /// 初始化桥接服务（离线用例专用：可注入上游消息处理器与各段超时）。
    /// </summary>
    /// <param name="bridgeOptions">桥接配置。</param>
    /// <param name="playbackOptionsProvider">获取最新播放配置的回调（用于 mpv 路径等）。</param>
    /// <param name="logger">结构化日志。</param>
    /// <param name="relayHandler">中继上游消息处理器（<see langword="null"/> 时使用真实网络处理器）。</param>
    /// <param name="upstreamHeaderTimeout">等上游响应头的超时。</param>
    /// <param name="playlistDownloadTimeout">拉取 HLS 播放列表正文的超时。</param>
    /// <param name="idleTimeout">中继上游连续无数据的断流判定窗口。</param>
    /// <remarks>
    /// 之所以保留这组 internal 参数：超时与地址约束必须能被离线用例驱动
    /// （用 <see cref="HttpMessageHandler"/> 替身在超时窗口之后继续产出数据，断言长连接不被误杀），
    /// 真实运行时一律走上面的公开构造函数。
    /// </remarks>
    internal BridgeHost(
        BridgeOptions bridgeOptions,
        Func<PlaybackOptions?> playbackOptionsProvider,
        IStructuredLogger logger,
        HttpMessageHandler? relayHandler,
        TimeSpan upstreamHeaderTimeout,
        TimeSpan playlistDownloadTimeout,
        TimeSpan idleTimeout)
    {
        ArgumentNullException.ThrowIfNull(bridgeOptions);
        ArgumentNullException.ThrowIfNull(playbackOptionsProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _bridgeOptions = bridgeOptions;
        _playbackOptionsProvider = playbackOptionsProvider;
        _logger = logger;
        _registry = new RelayRegistry(logger);
        _mpvLauncher = new MpvLauncher(logger);
        _upstreamHeaderTimeout = upstreamHeaderTimeout;
        _playlistDownloadTimeout = playlistDownloadTimeout;
        _idleTimeout = idleTimeout;
        _relayClient = CreateRelayClient(relayHandler);
    }

    /// <summary>
    /// 创建中继专用的 <see cref="HttpClient"/>。
    /// </summary>
    /// <param name="handler">消息处理器；<see langword="null"/> 时使用默认的 <see cref="SocketsHttpHandler"/>。</param>
    /// <returns>中继客户端。</returns>
    /// <remarks>
    /// <para>
    /// 中继拉的是直播长连接，因此这里做两件事：
    /// </para>
    /// <list type="number">
    ///   <item>关闭连接池的"回收寿命"（<see cref="SocketsHttpHandler.PooledConnectionLifetime"/>）：
    ///     定时回收会把一条正在读的流一起换掉，表现为固定时长的"看着看着断一下"；</item>
    ///   <item>把 <see cref="HttpClient.Timeout"/> 设为 <see cref="Timeout.InfiniteTimeSpan"/>。</item>
    /// </list>
    /// <para>
    /// **<see cref="HttpClient.Timeout"/> 在这里不能改成有限值，但也管不到体读取**：
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> 下它只约束"到响应头为止"，
    /// 设成 30 秒既不会、也不应该把直播流一起取消（旧注释把它说成"否则 30 秒后连直播流一起取消"，
    /// 这个理由不成立）。因此"等响应头"与"读 HLS 播放列表正文"各自用独立的
    /// <see cref="CancellationTokenSource"/> 加有限超时（见 <see cref="_upstreamHeaderTimeout"/> 与
    /// <see cref="_playlistDownloadTimeout"/>），FLV/TS 长连接的边界只由 <see cref="_idleTimeout"/> 空闲看门狗负责。
    /// </para>
    /// <para>
    /// 跟随重定向（<see cref="SocketsHttpHandler.AllowAutoRedirect"/>）保留，但**重定向后的最终地址**
    /// 同样要过 <see cref="IsAllowedUpstreamUrl(string)"/>（见 <see cref="StreamRelayAsync"/>），
    /// 否则公网地址可以把中继指向回环/私网。
    /// </para>
    /// </remarks>
    private static HttpClient CreateRelayClient(HttpMessageHandler? handler)
    {
        SocketsHttpHandler sockets = new()
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            ConnectTimeout = TimeSpan.FromSeconds(UpstreamConnectTimeoutSeconds),
        };
        HttpMessageHandler effective = handler ?? sockets;
        return new HttpClient(effective, disposeHandler: true)
        {
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
        if (string.IsNullOrWhiteSpace(url) || !IsAllowedMpvUrl(url))
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
        // 中继只提供 GET /relay/{token}：注册一律走进程内的 IPlaybackBridge.RegisterRelay。
        // 过去这里还有一条 POST 注册路由，它没有任何身份校验、又允许任意来源读取，
        // 本机任意网页都能注册自己的回环地址再读回数据（SP-02）。播放页从不使用它
        // （Web/player.html 只 fetch 候选地址；候选地址由 PlaybackCoordinator 在进程内注册），
        // 因此直接关闭该路由（最小攻击面），而不是补一层令牌校验。
        if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warn(_moduleName, "中继路由拒绝非 GET 请求（注册只能进程内发起）。", new Dictionary<string, object?>
            {
                ["method"] = context.Request.HttpMethod,
            });
            await WriteTextAsync(context, HttpStatusCode.NotFound, "{\"error\":\"not-found\"}").ConfigureAwait(false);
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

    /// <summary>
    /// 向中继上游发起一次请求并等响应头，带**有限**超时。
    /// </summary>
    /// <param name="target">中继目标。</param>
    /// <param name="rangeHeader">客户端传来的 <c>Range</c> 头，可为 <see langword="null"/>。</param>
    /// <param name="completionOption">读取方式（取响应头 / 读完整正文）。</param>
    /// <param name="timeout">等上游的时限。</param>
    /// <returns>发送结果：成功时带请求与响应，超时时带判定原因。</returns>
    /// <remarks>
    /// 超时用独立的 <see cref="CancellationTokenSource"/>（而不是 <see cref="HttpClient.Timeout"/>）：
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> 下后者只管到响应头，
    /// 且中继整体必须保持 <see cref="Timeout.InfiniteTimeSpan"/> 才能承载直播长连接；
    /// 断流判定仍由 <see cref="PumpWithIdleTimeoutAsync"/> 的空闲看门狗负责。
    /// </remarks>
    private async Task<RelaySendResult> SendUpstreamAsync(
        RelayTarget target,
        string? rangeHeader,
        HttpCompletionOption completionOption,
        TimeSpan timeout)
    {
        HttpRequestMessage request = CreateUpstreamRequest(target, rangeHeader);
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeoutSource.CancelAfter(timeout);
        try
        {
            HttpResponseMessage response = await _relayClient
                .SendAsync(request, completionOption, timeoutSource.Token)
                .ConfigureAwait(false);
            return RelaySendResult.Success(request, response);
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            request.Dispose();
            return RelaySendResult.Timeout();
        }
        catch (HttpRequestException)
        {
            request.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 校验**重定向之后的最终地址**：公网 <c>http</c>/<c>https</c> 之外一律拒绝。
    /// </summary>
    /// <param name="response">上游响应。</param>
    /// <param name="target">中继目标（日志只取主机名，不含签名参数）。</param>
    /// <param name="context">当前请求。</param>
    /// <returns>地址合法返回 <see langword="true"/>；已写入 502 响应时返回 <see langword="false"/>。</returns>
    private async Task<bool> EnsureUpstreamAddressAllowedAsync(
        HttpResponseMessage response,
        RelayTarget target,
        HttpListenerContext context)
    {
        Uri? finalAddress = response.RequestMessage?.RequestUri;
        if (finalAddress is null || IsAllowedUpstreamUrl(finalAddress))
        {
            return true;
        }

        _logger.Warn(_moduleName, "中继上游重定向到了不允许的地址，已拒绝。", new Dictionary<string, object?>
        {
            ["host"] = finalAddress.Host,
            ["requestedHost"] = TryGetHost(target.UpstreamUrl),
        });
        await WriteTextAsync(context, HttpStatusCode.BadGateway, "{\"error\":\"forbidden-upstream\"}").ConfigureAwait(false);
        return false;
    }

    /// <summary>上游超时后的收尾：回 504（日志已由 <see cref="ReportUpstreamTimeout"/> 记录）。</summary>
    /// <param name="context">当前请求。</param>
    /// <param name="moduleOperation">发生超时的环节（写入响应体便于页面侧定位）。</param>
    private async Task FailUpstreamTimeoutAsync(HttpListenerContext context, string moduleOperation)
    {
        await WriteTextAsync(
            context,
            HttpStatusCode.GatewayTimeout,
            "{\"error\":\"upstream-timeout\",\"operation\":\"" + moduleOperation + "\"}").ConfigureAwait(false);
    }

    /// <summary>
    /// 等上游响应头并校验"重定向后的最终地址"（离线用例可直接调用的入口）。
    /// </summary>
    /// <param name="target">中继目标。</param>
    /// <param name="rangeHeader">客户端传来的 <c>Range</c> 头，可为 <see langword="null"/>。</param>
    /// <returns>发送结果：成功且地址合法时带响应，超时或最终地址不被允许时判定为失败。</returns>
    internal async Task<RelaySendResult> TrySendUpstreamAsync(RelayTarget target, string? rangeHeader = null)
    {
        RelaySendResult sent = await SendUpstreamAsync(
            target,
            rangeHeader,
            HttpCompletionOption.ResponseHeadersRead,
            _upstreamHeaderTimeout).ConfigureAwait(false);
        if (sent.TimedOut)
        {
            return ReportUpstreamTimeout(sent, target, "response-headers");
        }

        if (!IsAllowedUpstreamUrl(sent.Response!.RequestMessage?.RequestUri ?? new Uri(target.UpstreamUrl)))
        {
            sent.Request!.Dispose();
            sent.Response!.Dispose();
            return RelaySendResult.Timeout();
        }

        return sent;
    }

    /// <summary>
    /// 记录"上游超时"并给出超时结论（时间敏感路径唯一的落日志点）。
    /// </summary>
    /// <param name="result">已经判定为超时的发送结果。</param>
    /// <param name="target">中继目标（日志只取主机名）。</param>
    /// <param name="moduleOperation">超时的环节。</param>
    /// <returns>原样返回的超时结果。</returns>
    private RelaySendResult ReportUpstreamTimeout(RelaySendResult result, RelayTarget target, string moduleOperation)
    {
        _logger.Warn(_moduleName, "中继上游超时，已断开。", new Dictionary<string, object?>
        {
            ["operation"] = moduleOperation,
            ["host"] = TryGetHost(target.UpstreamUrl),
        });
        return result;
    }

    /// <summary>
    /// 拉取并改写 HLS 播放列表（离线用例可直接调用的入口）。
    /// </summary>
    /// <param name="target">播放列表中继目标。</param>
    /// <param name="playlistTimeout">播放列表正文读取的时限。</param>
    /// <returns>播放列表拉取结果：成功时带改写后的播放列表文本，超时时 <c>TimedOut</c> 为真。</returns>
    internal async Task<PlaylistFetchResult> TryReadPlaylistAsync(RelayTarget target, TimeSpan playlistTimeout)
    {
        RelaySendResult sent = await SendUpstreamAsync(
            target,
            rangeHeader: null,
            HttpCompletionOption.ResponseContentRead,
            _upstreamHeaderTimeout).ConfigureAwait(false);
        if (sent.TimedOut)
        {
            _ = ReportUpstreamTimeout(sent, target, "playlist");
            return PlaylistFetchResult.Timeout();
        }

        using HttpRequestMessage request = sent.Request!;
        using HttpResponseMessage response = sent.Response!;
        if (!IsAllowedUpstreamUrl(response.RequestMessage?.RequestUri ?? new Uri(target.UpstreamUrl)))
        {
            return PlaylistFetchResult.Timeout();
        }

        if (!response.IsSuccessStatusCode)
        {
            return new PlaylistFetchResult(null, (int)response.StatusCode, TimedOut: false);
        }

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeoutSource.CancelAfter(playlistTimeout);
        try
        {
            string playlist = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            return new PlaylistFetchResult(RewritePlaylist(playlist, target), (int)HttpStatusCode.OK, TimedOut: false);
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            _logger.Warn(_moduleName, "中继上游超时，已断开。", new Dictionary<string, object?>
            {
                ["operation"] = "playlist-body",
                ["host"] = TryGetHost(target.UpstreamUrl),
            });
            return PlaylistFetchResult.Timeout();
        }
    }

    /// <summary>
    /// 等上游响应头并返回**长连接正文流**（离线用例可直接调用的入口）。
    /// </summary>
    /// <param name="target">中继目标。</param>
    /// <returns>上游正文流（调用方负责释放）；超时或地址非法时返回 <see langword="null"/>。</returns>
    internal async Task<Stream?> TryOpenUpstreamStreamAsync(RelayTarget target)
    {
        RelaySendResult sent = await TrySendUpstreamAsync(target).ConfigureAwait(false);
        if (sent.TimedOut)
        {
            return null;
        }

        // 只关心正文流，因此把响应对象与流一起释放（见 RelayUpstreamStream）。
        Stream body = await sent.Response!.Content.ReadAsStreamAsync(_shutdown.Token).ConfigureAwait(false);
        return new RelayUpstreamStream(sent.Response!, body);
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

        RelaySendResult sent = await SendUpstreamAsync(
            target,
            context.Request.Headers[HeaderNameRange],
            HttpCompletionOption.ResponseHeadersRead,
            _upstreamHeaderTimeout).ConfigureAwait(false);
        if (sent.TimedOut)
        {
            await FailUpstreamTimeoutAsync(context, "response-headers").ConfigureAwait(false);
            return;
        }

        using HttpRequestMessage request = sent.Request!;
        using HttpResponseMessage response = sent.Response!;
        if (!await EnsureUpstreamAddressAllowedAsync(response, target, context).ConfigureAwait(false))
        {
            return;
        }

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
    /// <para>
    /// 只中继 m3u8 本身没用：播放列表里的切片地址如果仍指向 <c>http://</c> CDN，
    /// 页面照样会因为混合内容被拦下，所以这里逐行改写（含 <c>#EXT-X-KEY</c>/<c>#EXT-X-MAP</c> 的 URI）。
    /// </para>
    /// <para>
    /// 播放列表是**有限正文**，因此不必用长连接的 idle 看门狗，而是用
    /// <see cref="_playlistDownloadTimeout"/> 这条独立的有限超时把"等响应头"与"读正文"两段都框住：
    /// 上游半开连接时页面只会看到一次明确的 504，而不是无限等下去。
    /// </para>
    /// </remarks>
    private async Task ServePlaylistAsync(HttpListenerContext context, RelayTarget target)
    {
        PlaylistFetchResult fetched = await TryReadPlaylistAsync(target, _playlistDownloadTimeout).ConfigureAwait(false);
        if (fetched.TimedOut)
        {
            await FailUpstreamTimeoutAsync(context, "playlist").ConfigureAwait(false);
            return;
        }

        if (fetched.StatusCode != (int)HttpStatusCode.OK || fetched.Playlist is null)
        {
            context.Response.StatusCode = fetched.StatusCode;
            context.Response.ContentLength64 = 0;
            return;
        }

        byte[] payload = Encoding.UTF8.GetBytes(fetched.Playlist);

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
    /// 一次中继上游请求的结果：成功时带请求与响应，超时时两者均为 <see langword="null"/>。
    /// </summary>
    /// <param name="Request">请求（成功时非空，调用方负责释放）。</param>
    /// <param name="Response">响应（成功时非空，调用方负责释放）。</param>
    /// <param name="TimedOut">是否因为超过时限而被判定失败。</param>
    /// <remarks>
    /// 类型是 internal 而不是 private：离线用例要直接读这三个字段来断言超时与重定向拒绝
    /// （见 <c>tests/StreamPilot.Tests/Cases/RelaySafetyTests.cs</c>），
    /// 而对程序集外它仍然不可见（`InternalsVisibleTo` 只开放给测试工程）。
    /// </remarks>
    internal readonly record struct RelaySendResult(
        HttpRequestMessage? Request,
        HttpResponseMessage? Response,
        bool TimedOut)
    {
        /// <summary>构造成功结果。</summary>
        /// <param name="request">请求。</param>
        /// <param name="response">响应。</param>
        /// <returns>成功结果。</returns>
        public static RelaySendResult Success(HttpRequestMessage request, HttpResponseMessage response) =>
            new(request, response, TimedOut: false);

        /// <summary>构造超时结果。</summary>
        /// <returns>超时结果。</returns>
        public static RelaySendResult Timeout() => new(null, null, TimedOut: true);
    }

    /// <summary>
    /// 一次 HLS 播放列表拉取的结果。
    /// </summary>
    /// <param name="Playlist">改写后的播放列表文本；失败时为 <see langword="null"/>。</param>
    /// <param name="StatusCode">上游状态码（成功时为 200）。</param>
    /// <param name="TimedOut">是否因为超过时限而被判定失败。</param>
    /// <remarks>类型是 internal 的原因同 <see cref="RelaySendResult"/>。</remarks>
    internal readonly record struct PlaylistFetchResult(string? Playlist, int StatusCode, bool TimedOut)
    {
        /// <summary>构造超时结果。</summary>
        /// <returns>超时结果。</returns>
        public static PlaylistFetchResult Timeout() => new(null, (int)HttpStatusCode.GatewayTimeout, TimedOut: true);
    }

    /// <summary>
    /// 把上游响应对象与它的正文流绑成一个可释放对象（避免调用方分别持有两半）。
    /// </summary>
    /// <param name="response">上游响应。</param>
    /// <param name="body">上游正文流。</param>
    private sealed class RelayUpstreamStream(HttpResponseMessage response, Stream body) : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => body.CanRead;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => body.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => body.Position;
            set => throw new NotSupportedException("中继上游流不支持定位。");
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => body.Read(buffer, offset, count);

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            body.ReadAsync(buffer, cancellationToken);

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            body.ReadAsync(buffer, offset, count, cancellationToken);

        /// <inheritdoc />
        public override void Flush() => body.Flush();

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("中继上游流不支持定位。");

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException("中继上游流不支持改长度。");

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("中继上游流不支持写入。");

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                body.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            await body.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
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

    /// <summary>
    /// 写入 CORS 头。
    /// </summary>
    /// <param name="context">当前请求。</param>
    /// <param name="includePrivateNetworkHeader">是否携带 <c>Access-Control-Allow-Private-Network</c>。</param>
    /// <remarks>
    /// <para>
    /// <c>Access-Control-Allow-Origin</c> 固定为播放页的来源（<see cref="PlayerPageOrigin"/>），
    /// **不再使用 <c>*</c>**：中继监听在回环地址上，<c>*</c> 会让本机任意网页都能跨源读取中继响应
    /// （含平台签名地址与直播内容），而播放页的来源只有一个，收敛成固定值不影响任何正常路径。
    /// </para>
    /// <para>
    /// <c>Access-Control-Allow-Private-Network</c> 按预检请求的
    /// <c>Access-Control-Request-Private-Network: true</c> 决定是否返回，
    /// 而不是每个 <c>OPTIONS</c> 都返回：规范要求它只出现在声明了私有网络访问的预检响应上。
    /// </para>
    /// </remarks>
    private static void ApplyCorsHeaders(HttpListenerContext context, bool includePrivateNetworkHeader)
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = PlayerPageOrigin;
        context.Response.Headers["Vary"] = HeaderNameOrigin;
        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, HEAD, OPTIONS";
        context.Response.Headers["Access-Control-Allow-Headers"] = "*";
        context.Response.Headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges";
        context.Response.Headers["Cache-Control"] = "no-store";

        if (includePrivateNetworkHeader
            && string.Equals(context.Request.Headers[HeaderNameRequestPrivateNetwork], "true", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
        }
    }

    /// <summary>
    /// 判断地址是否可以交给 mpv 外挂播放。
    /// </summary>
    /// <param name="url">待校验地址。</param>
    /// <returns>允许返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// mpv 自带 RTMP 支持，因此 mpv 路径保留 <c>rtmp</c>/<c>rtmps</c>；
    /// 中继路径**不允许**它们（中继是 HTTP 客户端，拉不了 RTMP），见 <see cref="IsAllowedUpstreamUrl(string)"/>。
    /// 这条区别是 SP-02 的要点之一：过去两条路径共用一份只查 scheme 的校验。
    /// </remarks>
    private static bool IsAllowedMpvUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return uri.Scheme is "http" or "https" or "rtmp" or "rtmps";
    }

    /// <summary>
    /// 判断地址是否可以交给中继上游（**唯一**目的地址白名单）。
    /// </summary>
    /// <param name="url">待校验地址。</param>
    /// <returns>允许返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 两条规则，注册与**重定向后的最终地址**都要过（见 <see cref="EnsureUpstreamAddressAllowedAsync"/>）：
    /// </para>
    /// <list type="number">
    ///   <item>只允许 <c>http</c>/<c>https</c>：中继本身是 HTTP 客户端，<c>rtmp</c>/<c>rtmps</c>
    ///     从来就不可能被它拉取，过去放行只扩大了攻击面；</item>
    ///   <item>拒绝回环 / 私网 / 链路本地 / 未指定地址的字面量与 <c>localhost</c> 主机名：
    ///     允许它们会把这个只监听回环的服务变成"访问本机与内网服务的跳板"
    ///     （SP-02 复现的正是"注册自己的回环地址后取回数据"）。CDN 都是公网域名，不受影响。</item>
    /// </list>
    /// <para>
    /// 只做**字面量**判定，不做 DNS 解析：解析结果会变，且解析-连接之间仍可能被改写
    /// （DNS rebinding），凭它做安全决策只会给出虚假的保证。这里的目的是去掉"零成本地访问内网"，
    /// 并把回到回环的重定向挡掉。
    /// </para>
    /// </remarks>
    internal static bool IsAllowedUpstreamUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return IsAllowedUpstreamUrl(uri);
    }

    /// <summary>
    /// 判断地址是否可以交给中继上游（<see cref="Uri"/> 重载）。
    /// </summary>
    /// <param name="uri">待校验地址。</param>
    /// <returns>允许返回 <see langword="true"/>。</returns>
    internal static bool IsAllowedUpstreamUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        return !IsBlockedUpstreamHost(uri.Host);
    }

    /// <summary>
    /// 判断主机名是否指向本机或内网（字面量判定）。
    /// </summary>
    /// <param name="host"><see cref="Uri.Host"/> 形态的主机名（IPv6 会带方括号，见 <see cref="Uri.HostNameType"/>）。</param>
    /// <returns>命中本机 / 内网 / 链路本地 / 未指定返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 带方括号的 IPv6 会被 <see cref="IPAddress.TryParse(string, out IPAddress)"/> 拒绝
    /// （<c>[::1]</c> 解析失败、<c>::1</c> 成功），所以这里先剥掉方括号再解析；
    /// 同时把解析失败但形态是数字/十六进制字面量的主机名（<c>2130706433</c>、<c>0x7f.1</c>、
    /// <c>127.1</c> 这类"整数形式的回环"）也一律拒绝：.NET 的 <see cref="Uri"/> 会把它们
    /// 规范化成 <c>127.0.0.1</c>，交给下游就变成一次回环访问。
    /// </para>
    /// <para>
    /// IPv4-mapped IPv6（<c>::ffff:10.0.0.5</c>）必须按内层 IPv4 判定，否则一个
    /// "看起来是 IPv6" 的地址就能绕过私网检查。
    /// </para>
    /// </remarks>
    private static bool IsBlockedUpstreamHost(string host)
    {
        if (host.Length == 0)
        {
            return true;
        }

        string candidate = host;
        if (candidate.Length > 2 && candidate[0] == '[' && candidate[^1] == ']')
        {
            candidate = candidate[1..^1];
        }

        if (IsLocalhostHostName(candidate))
        {
            return true;
        }

        if (!IPAddress.TryParse(candidate, out IPAddress? address))
        {
            // 解析失败即按公网域名放行。注意 <c>0x7f.1</c> / <c>2130706433</c> / <c>127.1</c> 这类
            // "整数形式的回环"不需要在这里额外处理：.NET 的 <see cref="Uri"/> 会把它们规范化成
            // <c>127.0.0.1</c>，下面按 IPv4 判定即可拦下（见 RelaySafetyTests 的对应用例）。
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsPrivateIPv4(address.MapToIPv4());
        }

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || IsIPv6UniqueLocal(address) || IsIPv6SiteLocal(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return IsIPv6Multicast(address) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None);
        }

        return IsPrivateIPv4(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast);
    }

    /// <summary>
    /// 判断主机名是否是 <c>localhost</c>（含 <c>sub.localhost</c> 这类保留后缀）。
    /// </summary>
    /// <param name="host">主机名。</param>
    /// <returns>是 localhost 返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 按**标签**判定而不是子串：<c>localhost.example.com</c> 是普通公网域名（CDN 可能用它），
    /// 用 <c>Contains</c> 会把它误判成回环地址。
    /// </remarks>
    private static bool IsLocalhostHostName(string host)
    {
        const string LocalhostLabel = "localhost";
        if (host.Equals(LocalhostLabel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 去掉一个根域点（http://localhost./ 在 Uri 里会带尾点）。
        string normalized = host.EndsWith('.') ? host[..^1] : host;
        return normalized.EndsWith("." + LocalhostLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断 IPv6 地址是否属于唯一本地地址段 <c>fc00::/7</c>。</summary>
    /// <param name="address">IPv6 地址。</param>
    /// <returns>属于唯一本地地址返回 <see langword="true"/>。</returns>
    private static bool IsIPv6UniqueLocal(IPAddress address) => (address.GetAddressBytes()[0] & 0xFE) == 0xFC;

    /// <summary>判断 IPv6 地址是否属于废弃的站点本地地址段 <c>fec0::/10</c>（仍可能出现在内网配置里）。</summary>
    /// <param name="address">IPv6 地址。</param>
    /// <returns>属于站点本地地址返回 <see langword="true"/>。</returns>
    private static bool IsIPv6SiteLocal(IPAddress address)
    {
        byte[] octets = address.GetAddressBytes();
        return octets[0] == 0xFE && (octets[1] & 0xC0) == 0xC0;
    }

    /// <summary>判断 IPv6 地址是否属于组播地址段 <c>ff00::/8</c>。</summary>
    /// <param name="address">IPv6 地址。</param>
    /// <returns>属于组播地址返回 <see langword="true"/>。</returns>
    private static bool IsIPv6Multicast(IPAddress address) => address.GetAddressBytes()[0] == 0xFF;

    /// <summary>
    /// 判断 IPv4 地址是否属于私有 / 链路本地 / 共享地址段。
    /// </summary>
    /// <param name="address">IPv4 地址。</param>
    /// <returns>属于内网段返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 覆盖 RFC 1918（10/8、172.16/12、192.168/16）、RFC 6598（100.64/10）、
    /// RFC 3927（169.254/16）与保留段 0.0.0.0/8、192.0.0/24、198.18/15、240/4。
    /// </remarks>
    private static bool IsPrivateIPv4(IPAddress address)
    {
        byte[] octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 => true,
            10 => true,
            100 => octets[1] is >= 64 and <= 127,
            127 => true,
            169 => octets[1] == 254,
            172 => octets[1] is >= 16 and <= 31,
            192 => octets[1] == 168 || (octets[1] == 0 && octets[2] == 0) || (octets[1] == 0 && octets[2] == 2),
            198 => octets[1] is 18 or 19,
            >= 240 => true,
            _ => false,
        };
    }
}
