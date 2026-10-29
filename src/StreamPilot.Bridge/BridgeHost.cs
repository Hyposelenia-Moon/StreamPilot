namespace StreamPilot.Bridge;

using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Errors;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Runtime;
using StreamPilot.Core.Services;

/// <summary>
/// 内嵌的本地桥接服务（Kestrel 托管，只监听 127.0.0.1）。
/// </summary>
/// <remarks>
/// <para>提供的能力：</para>
/// <list type="bullet">
///   <item><c>GET /health</c>：存活探测与版本信息；</item>
///   <item><c>GET /play?url=&amp;title=&amp;referer=</c>：调用 mpv 外挂播放；</item>
///   <item><c>GET /relay/{token}</c>：为需要 Referer 的流提供本地中继（**只读**，注册只能进程内发起）；</item>
///   <item><c>OPTIONS</c>：CORS 预检（**只对已知路由**返回 204，未知路径一律 404；
///     仅预检响应携带 <c>Access-Control-Allow-Private-Network</c>）。</item>
/// </list>
/// <para>安全约束：</para>
/// <list type="bullet">
///   <item>绑定地址固定为 <see cref="IPAddress.Loopback"/>，并由 <see cref="LoopbackOnlyGuard"/> 再做一次校验；</item>
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

    /// <summary>内容嗅探读取的字节数：足以判断 <c>#EXTM3U</c> 头（含 BOM 与前导空白）。</summary>
    private const int PlaylistSniffBytes = 32;

    /// <summary>嗅探认定为播放列表后允许的最大正文（防止把超大响应当播放列表缓存）。</summary>
    private const int MaxSniffedPlaylistBytes = 8 * 1024 * 1024;

    /// <summary>读播放列表正文时每次读取的块大小。</summary>
    private const int PlaylistBodyChunkBytes = 16 * 1024;

    /// <summary>建立上游连接的超时（秒）。</summary>
    private const int UpstreamConnectTimeoutSeconds = 10;

    /// <summary>
    /// 跟随上游重定向的最大跳数。
    /// </summary>
    /// <remarks>
    /// 重定向**逐跳**发送与校验（见 <see cref="SendUpstreamAsync"/>）：每一跳的实际目标都要先过
    /// <see cref="IsAllowedUpstreamUrl(string)"/> 再发包，因此跳数必须有上限，避免"公网地址 → 公网地址 → …"的循环。
    /// </remarks>
    private const int MaxUpstreamRedirects = 5;

    /// <summary>需要跟随的上游重定向状态码（300/304/305/306 不跟随）。</summary>
    private static readonly HttpStatusCode[] RedirectStatusCodes =
    [
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.Found,
        HttpStatusCode.SeeOther,
        HttpStatusCode.TemporaryRedirect,
        HttpStatusCode.PermanentRedirect,
    ];

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

    /// <summary>每个播放列表**至少**保留的改写代数：播放器可能仍在用上一版列表里的切片地址。</summary>
    private const int MinRetainedPlaylistGenerations = 2;

    /// <summary>每个播放列表**最多**保留的改写代数（刷新很密时的硬上限，防止注册表无限增长）。</summary>
    private const int MaxRetainedPlaylistGenerations = 8;

    /// <summary>旧一代子地址的最短保留时间（秒）。</summary>
    /// <remarks>
    /// 只按"代数"保留会误伤慢链路：一个 6 MB 的切片在 10 Mbps 上要 5 秒，
    /// 而直播播放列表每 2~4 秒就刷新一次，两代很快过去，正在下载的地址变成 404，
    /// hls.js 只能重试 → 卡顿。这里改成"至少保留 60 秒，且不超过 8 代"：
    /// 只要下载还在进行，对应地址就还在。
    /// </remarks>
    private const int PlaylistGenerationGraceSeconds = 60;

    /// <summary>长连接转发期间给中继注册续命的间隔（秒）。</summary>
    /// <remarks>
    /// 注册表按"空闲 10 分钟"淘汰，而 FLV 只在建连时请求一次 <c>/relay/{token}</c>；
    /// 不续命的话，播到第 11 分钟时条目已被清掉，第一次断流重连就变成 404。
    /// </remarks>
    private const int RelayTouchIntervalSeconds = 30;

    /// <summary>每个中继令牌当前的一条上游连接（同一令牌只允许一条）。</summary>
    /// <remarks>
    /// 斗鱼等平台对同一签名地址只允许 1 条并发连接，第 2 条会在 0.2~0.4 秒内被上游切断。
    /// 页面重连/换播放器时旧连接可能还挂在上游读数据，这里在受理新连接时主动取消旧连接。
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _relayLeases =
        new(StringComparer.Ordinal);

    /// <summary>播放列表地址 → 按"新 → 旧"排列的改写代数。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<PlaylistGeneration>> _playlistChildren =
        new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _outputLock = new(1, 1);
    private WebApplication? _app;
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
    /// 跟随重定向的能力**不用** <see cref="SocketsHttpHandler.AllowAutoRedirect"/>：自动跳转会在
    /// 应用层拿到控制权之前先请求中间地址，回环/私网目标因此照样会收到一次 GET（SP-04）。
    /// 这里关闭自动跳转，改由 <see cref="SendUpstreamAsync"/> 逐跳校验、逐跳发送。
    /// </para>
    /// </remarks>
    private static HttpClient CreateRelayClient(HttpMessageHandler? handler)
    {
        SocketsHttpHandler sockets = new()
        {
            UseCookies = false,
            AllowAutoRedirect = false,
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
    public bool IsRunning => _app is not null;

    /// <inheritdoc />
    public int Port { get; private set; }

    /// <inheritdoc />
    public string BaseAddress => IsRunning ? $"http://{BridgeConstants.LoopbackHost}:{Port}" : string.Empty;

    /// <summary>mpv 可执行文件解析结果（供 UI 提示使用）。</summary>
    public string? MpvPath => _mpvLauncher.ResolvedPath;

    /// <summary>当前中继注册数量。</summary>
    public int RelayCount => _registry.Count;

    /// <summary>
    /// 判断某个本地中继地址当前是否仍然可用（离线用例据此断言子节点回收时机）。
    /// </summary>
    /// <param name="localUrl">本地中继地址（例如 <c>http://127.0.0.1:5566/relay/{token}</c>）。</param>
    /// <returns>仍然登记时返回 <see langword="true"/>。</returns>
    internal bool IsRelayRegistered(string localUrl)
    {
        if (string.IsNullOrWhiteSpace(localUrl))
        {
            return false;
        }

        // 未启动监听的离线用例里，登记得到的地址是相对的（"/relay/{token}"），因此按路径取最后一段。
        string path = Uri.TryCreate(localUrl, UriKind.Absolute, out Uri? absolute)
            ? absolute.AbsolutePath
            : localUrl.Split('?')[0];
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        string token = segments[^1];
        return token.Length > 0 && _registry.TryResolve(token, out _);
    }

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
        Exception? lastError = null;

        for (int port = preferred; port <= maximum; port++)
        {
            if (TryStartOnPort(port, out int boundPort, out Exception? error))
            {
                Port = boundPort;
                _logger.Info(_moduleName, "桥接服务已启动。", new Dictionary<string, object?>
                {
                    ["address"] = BaseAddress,
                });
                return;
            }

            lastError = error;
        }

        if (TryStartOnPort(GetEphemeralPort(), out int ephemeralPort, out Exception? ephemeralError))
        {
            Port = ephemeralPort;
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

        return $"{BaseAddress}{RelayPathPrefix}/{RegisterRelayCore(target)}";
    }

    /// <summary>
    /// 往注册表里登记一个中继目标（不要求监听已启动）。
    /// </summary>
    /// <param name="target">中继目标。</param>
    /// <returns>中继令牌。</returns>
    /// <remarks>
    /// 供离线用例驱动"父节点 → 子节点"的回收链：<see cref="ReleaseRelay"/> 要按**完整本地地址**释放父节点，
    /// 而完整地址里的令牌只能由注册得到，离线环境又没有监听端口可以拼地址。
    /// 生产路径仍走 <see cref="RegisterRelay"/>（带运行状态检查）。
    /// </remarks>
    internal string RegisterRelayCore(RelayTarget target) => _registry.Register(target);

    /// <inheritdoc />
    /// <remarks>
    /// 释放一个父中继（HLS 播放列表）时必须**递归**回收它改写出来的子节点：
    /// 生产调用者传进来的是完整的本地地址（<c>http://127.0.0.1:端口/relay/{token}</c>），
    /// 而子节点集合按"播放列表上游地址"分组、元素是裸令牌——过去用完整地址去匹配这份集合永远匹配不上，
    /// 于是释放父节点后子注册仍可访问（SP-03）。这里先由令牌反查上游地址，再沿子分组逐代回收，
    /// 子播放列表自己的子节点同样递归处理。
    /// </remarks>
    public void ReleaseRelay(string localUrl)
    {
        string? token = RelayRegistry.ExtractToken(localUrl);
        if (token is null)
        {
            return;
        }

        ReleaseRegistration(token, new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// 释放一条注册，并递归释放它作为播放列表时登记的所有子节点。
    /// </summary>
    /// <param name="token">中继令牌。</param>
    /// <param name="visited">本次释放已经处理过的令牌（防止子节点环导致无限递归）。</param>
    private void ReleaseRegistration(string token, HashSet<string> visited)
    {
        if (!visited.Add(token))
        {
            return;
        }

        // 用"不刷新活跃时间"的读取：回收不是访问，不该延长这条注册的过期时间。
        if (_registry.TryPeek(token, out RelayTarget? target) && target is not null)
        {
            ReleasePlaylistChildren(target.UpstreamUrl, visited);
        }

        _registry.Release(token);
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

        WebApplication? app = _app;
        _app = null;
        if (app is not null)
        {
            try
            {
                // 先停止接收新请求并等在途请求收尾，再释放宿主。
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
                await app.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
                _logger.Debug(_moduleName, "停止桥接监听超时或已被释放。", new Dictionary<string, object?>
                {
                    ["detail"] = exception.Message,
                });
            }

            await app.DisposeAsync().ConfigureAwait(false);
        }

        _registry.Clear();
        _shutdown.Dispose();
        _relayClient.Dispose();
        _outputLock.Dispose();
        _logger.Info(_moduleName, "桥接服务已停止。");
    }

    private bool TryStartOnPort(int port, out int boundPort, out Exception? error)
    {
        boundPort = 0;
        error = null;
        if (port is < 1024 or > 65535)
        {
            return false;
        }

        // 防御性校验：Kestrel 只允许绑定回环地址，这里再用同一个守卫复核一次，
        // 避免将来有人把 Listen(IPAddress.Loopback) 改成通配地址而不被测试发现。
        LoopbackOnlyGuard.EnsureLoopback(LoopbackOnlyGuard.BuildPrefix(port));

        WebApplication? app = null;
        try
        {
            app = BuildApplication(port);
            // Start 是同步 API（WPF 启动阶段调用），Kestrel 启动本身很短，这里同步等待即可。
            app.StartAsync().GetAwaiter().GetResult();
            _app = app;
            boundPort = port;
            return true;
        }
        catch (IOException exception)
        {
            error = exception;
            DisposeApplication(app);
            _logger.Debug(_moduleName, "端口被占用，尝试下一个端口。", new Dictionary<string, object?>
            {
                ["port"] = port,
            });
            return false;
        }
        catch (SocketException exception)
        {
            /*
              端口被别的进程独占时 Windows 返回的是 WSAEACCES（10013，"访问权限不允许"），
              而不是"地址已在使用"。它一样属于"这个端口拿不到"，必须继续试下一个，
              否则桥接会带着异常冒到启动流程，把整个程序拖下水（实测两个实例同时启动即触发）。
            */
            error = exception;
            DisposeApplication(app);
            _logger.Debug(_moduleName, "端口无法绑定，尝试下一个端口。", new Dictionary<string, object?>
            {
                ["port"] = port,
                ["socketError"] = exception.SocketErrorCode.ToString(),
            });
            return false;
        }
        catch (InvalidOperationException exception)
        {
            error = exception;
            DisposeApplication(app);
            _logger.Debug(_moduleName, "端口无法绑定，尝试下一个端口。", new Dictionary<string, object?>
            {
                ["port"] = port,
            });
            return false;
        }
    }

    /// <summary>构造一套只监听回环地址的 Kestrel 宿主。</summary>
    /// <param name="port">监听端口。</param>
    /// <returns>尚未启动的宿主。</returns>
    private WebApplication BuildApplication(int port)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(BridgeHost).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
        });

        // 桥接自己写结构化日志；Kestrel 的默认 provider 会把"Now listening on..."之类的日志
        // 丢给控制台（WPF 下无人消费），这里直接清空 provider。
        builder.Services.RemoveAll<Microsoft.Extensions.Logging.ILoggerProvider>();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, port);
        });

        WebApplication app = builder.Build();
        app.Run((RequestDelegate)HandleContextAsync);
        return app;
    }

    /// <summary>释放一套未启动成功（或已停止）的宿主。</summary>
    /// <param name="app">宿主，可为 <see langword="null"/>。</param>
    private static void DisposeApplication(WebApplication? app)
    {
        if (app is null)
        {
            return;
        }

        try
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or IOException)
        {
            // 启动失败后的清理，失败不影响"换下一个端口"。
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

    /// <summary>
    /// 把请求行（方法 + 路径）解析成路由结论。
    /// </summary>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="path">绝对路径（形如 <c>/relay/{token}</c>）。</param>
    /// <returns>路由结论。</returns>
    /// <remarks>
    /// 预检（<c>OPTIONS</c>）只为**已知路由**（<c>/relay</c>、<c>/play</c>、<c>/health</c>）返回
    /// <see cref="BridgeRoute.Preflight"/>：过去任意路径的 <c>OPTIONS</c> 都会拿到 204 + CORS，
    /// 等于向本机任意页面确认"这个回环端口上什么路径都有人应答"；收紧后未知路径的预检与普通请求
    /// 一样是 404（最小攻击面）。方法是纯函数，因此离线用例可以直接断言，不必启动桥接监听。
    /// </remarks>
    internal static BridgeRoute ResolveRoute(string method, string path)
    {
        bool isRelay = path.Equals(RelayPathPrefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(RelayPathPrefix + "/", StringComparison.OrdinalIgnoreCase);
        bool isPlay = string.Equals(path, PlayPath, StringComparison.OrdinalIgnoreCase);
        bool isHealth = string.Equals(path, HealthPath, StringComparison.OrdinalIgnoreCase);
        if (!isRelay && !isPlay && !isHealth)
        {
            return BridgeRoute.NotFound;
        }

        if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return BridgeRoute.Preflight;
        }

        if (isRelay)
        {
            return BridgeRoute.Relay;
        }

        return isPlay ? BridgeRoute.Play : BridgeRoute.Health;
    }

    private async Task HandleContextAsync(HttpContext context)
    {
        try
        {
            string path = context.Request.Path.Value ?? "/";
            switch (ResolveRoute(context.Request.Method, path))
            {
                case BridgeRoute.Preflight:
                    await WriteEmptyAsync(context, HttpStatusCode.NoContent, includePrivateNetworkHeader: true).ConfigureAwait(false);
                    return;
                case BridgeRoute.Relay:
                    await HandleRelayAsync(context, path).ConfigureAwait(false);
                    return;
                case BridgeRoute.Play:
                    await HandlePlayAsync(context).ConfigureAwait(false);
                    return;
                case BridgeRoute.Health:
                    await HandleHealthAsync(context).ConfigureAwait(false);
                    return;
                default:
                    await WriteTextAsync(context, HttpStatusCode.NotFound, "{\"error\":\"not-found\"}").ConfigureAwait(false);
                    return;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 页面停止播放 / 切线路时客户端会直接断开：这是正常路径，不是桥接故障。
            _logger.Debug(_moduleName, "客户端已断开桥接请求。", new Dictionary<string, object?>
            {
                ["path"] = context.Request.Path.Value,
            });
        }
        catch (Exception exception) when (exception is BridgeException or IOException or ObjectDisposedException or InvalidOperationException or HttpRequestException)
        {
            _logger.LogError(LogLevel.Warn, _moduleName, "处理桥接请求失败。", exception, new Dictionary<string, object?>
            {
                ["path"] = context.Request.Path.Value,
            });

            HttpStatusCode status = exception is BridgeException or HttpRequestException
                ? HttpStatusCode.BadGateway
                : HttpStatusCode.InternalServerError;
            string error = exception switch
            {
                BridgeException => "bridge-unavailable",
                HttpRequestException => "upstream-unavailable",
                _ => "internal",
            };
            try
            {
                await WriteTextAsync(context, status, "{\"error\":\"" + error + "\"}").ConfigureAwait(false);
            }
            catch (Exception writeFailure) when (writeFailure is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                _logger.LogError(LogLevel.Debug, _moduleName, "写入错误响应失败（客户端可能已断开）。", writeFailure);
            }
        }
    }

    private async Task HandleHealthAsync(HttpContext context)
    {
        string body = $"{{\"status\":\"ok\",\"version\":\"{AppVersion.Current}\",\"port\":{Port},\"relayCount\":{_registry.Count}}}";
        await WriteTextAsync(context, HttpStatusCode.OK, body).ConfigureAwait(false);
    }

    private async Task HandlePlayAsync(HttpContext context)
    {
        string? url = context.Request.Query["url"].ToString();
        if (string.IsNullOrWhiteSpace(url) || !IsAllowedMpvUrl(url))
        {
            await WriteTextAsync(context, HttpStatusCode.BadRequest, "{\"error\":\"invalid-url\"}").ConfigureAwait(false);
            return;
        }

        string? title = context.Request.Query["title"].ToString();
        string? referer = context.Request.Query["referer"].ToString();
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

    private async Task HandleRelayAsync(HttpContext context, string path)
    {
        // 中继只提供 GET /relay/{token}：注册一律走进程内的 IPlaybackBridge.RegisterRelay。
        // 过去这里还有一条 POST 注册路由，它没有任何身份校验、又允许任意来源读取，
        // 本机任意网页都能注册自己的回环地址再读回数据（SP-02）。播放页从不使用它
        // （Web/player.html 只 fetch 候选地址；候选地址由 PlaybackCoordinator 在进程内注册），
        // 因此直接关闭该路由（最小攻击面），而不是补一层令牌校验。
        if (!string.Equals(context.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warn(_moduleName, "中继路由拒绝非 GET 请求（注册只能进程内发起）。", new Dictionary<string, object?>
            {
                ["method"] = context.Request.Method,
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

        // 同一令牌单飞：新连接先取消旧连接，避免旧上游连接继续占着"唯一并发名额"。
        using CancellationTokenSource lease = new();
        CancellationToken leaseToken = TakeRelayLease(token, lease);
        try
        {
            await StreamRelayAsync(context, target, token, leaseToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseRelayLease(token, lease);
        }
    }

    /// <summary>
    /// 取得某令牌的上游连接租约，并取消上一条连接。
    /// </summary>
    /// <param name="token">中继令牌。</param>
    /// <param name="lease">本次连接的取消源。</param>
    /// <returns>本次连接的取消令牌（仅覆盖本连接，不影响服务本身）。</returns>
    private CancellationToken TakeRelayLease(string token, CancellationTokenSource lease)
    {
        while (true)
        {
            if (_relayLeases.TryGetValue(token, out CancellationTokenSource? previous))
            {
                try
                {
                    previous.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 上一条连接刚好收尾并释放了取消源，属正常竞态。
                }

                if (_relayLeases.TryUpdate(token, lease, previous))
                {
                    return lease.Token;
                }

                continue;
            }

            if (_relayLeases.TryAdd(token, lease))
            {
                return lease.Token;
            }
        }
    }

    /// <summary>释放某令牌的租约（只移除自己那一条，避免误删后来者）。</summary>
    /// <param name="token">中继令牌。</param>
    /// <param name="lease">本次连接的取消源。</param>
    private void ReleaseRelayLease(string token, CancellationTokenSource lease)
    {
        // 原子地"只删自己那一条"：先查后删会让"查完之后新连接刚好接管"的竞态
        // 把新租约误删（此后第三条连接就不会取消它了）。
        _relayLeases.TryRemove(new KeyValuePair<string, CancellationTokenSource>(token, lease));
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
        return CreateUpstreamRequest(target.UpstreamUrl, target.Referer, rangeHeader);
    }

    /// <summary>
    /// 按"地址 + Referer"构造发往上游 CDN 的请求（重定向时逐跳重建使用）。
    /// </summary>
    /// <param name="url">本跳的实际目标地址。</param>
    /// <param name="referer">上游 Referer，可为 <see langword="null"/>。</param>
    /// <param name="rangeHeader">客户端传来的 <c>Range</c> 头，可为 <see langword="null"/>。</param>
    /// <returns>可直接发送的请求（调用方负责释放）。</returns>
    private static HttpRequestMessage CreateUpstreamRequest(string url, string? referer, string? rangeHeader)
    {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(HeaderNameUserAgent, HttpClientFactory.DefaultUserAgent);
        if (!string.IsNullOrWhiteSpace(referer))
        {
            request.Headers.TryAddWithoutValidation(HeaderNameReferer, referer);
        }

        if (!string.IsNullOrWhiteSpace(rangeHeader))
        {
            request.Headers.TryAddWithoutValidation(HeaderNameRange, rangeHeader);
        }

        return request;
    }

    /// <summary>
    /// 向中继上游发起一次请求并等响应头，带**有限**超时；重定向**逐跳校验后再发送**。
    /// </summary>
    /// <param name="target">中继目标。</param>
    /// <param name="rangeHeader">客户端传来的 <c>Range</c> 头，可为 <see langword="null"/>。</param>
    /// <param name="completionOption">读取方式（取响应头 / 读完整正文）。</param>
    /// <param name="timeout">每一跳等上游的时限。</param>
    /// <returns>发送结果：成功时带请求与响应；超时、被拒或跳转过多时判定为失败。</returns>
    /// <remarks>
    /// <para>
    /// 超时用独立的 <see cref="CancellationTokenSource"/>（而不是 <see cref="HttpClient.Timeout"/>）：
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> 下后者只管到响应头，
    /// 且中继整体必须保持 <see cref="Timeout.InfiniteTimeSpan"/> 才能承载直播长连接；
    /// 断流判定仍由 <see cref="PumpWithIdleTimeoutAsync"/> 的空闲看门狗负责。
    /// </para>
    /// <para>
    /// 所有发出（等响应头、读播放列表、读长连接正文）都从这里出去，因此**重定向的每一跳**
    /// 都在发包**之前**过 <see cref="IsAllowedUpstreamUrl(string)"/>（SP-04）：过去依赖
    /// <see cref="SocketsHttpHandler.AllowAutoRedirect"/>，中间地址已经收到一次 GET 才轮到应用层检查。
    /// </para>
    /// </remarks>
    private async Task<RelaySendResult> SendUpstreamAsync(
        RelayTarget target,
        string? rangeHeader,
        HttpCompletionOption completionOption,
        TimeSpan timeout)
    {
        string currentUrl = target.UpstreamUrl;
        for (int hop = 0; hop <= MaxUpstreamRedirects; hop++)
        {
            if (!IsAllowedUpstreamUrl(currentUrl))
            {
                _logger.LogError(
                    LogLevel.Warn,
                    _moduleName,
                    "中继目标地址不被允许，已在发送前拦截。",
                    new InvalidOperationException("blocked-upstream-address"),
                    new Dictionary<string, object?>
                    {
                        ["host"] = TryGetHost(currentUrl),
                        ["hop"] = hop,
                    });
                return RelaySendResult.Rejected();
            }

            HttpRequestMessage request = CreateUpstreamRequest(currentUrl, target.Referer, rangeHeader);
            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeoutSource.CancelAfter(timeout);
            HttpResponseMessage response;
            try
            {
                response = await _relayClient
                    .SendAsync(request, completionOption, timeoutSource.Token)
                    .ConfigureAwait(false);
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

            if (!TryGetRedirectTarget(response, currentUrl, out string redirectUrl))
            {
                return RelaySendResult.Success(request, response);
            }

            request.Dispose();
            response.Dispose();
            currentUrl = redirectUrl;
        }

        _logger.Warn(_moduleName, "中继上游重定向次数超过上限，已放弃。", new Dictionary<string, object?>
        {
            ["host"] = TryGetHost(target.UpstreamUrl),
            ["maxRedirects"] = MaxUpstreamRedirects,
        });
        return RelaySendResult.Timeout();
    }

    /// <summary>
    /// 判断上游响应是不是需要跟随的重定向，并给出下一跳的绝对地址。
    /// </summary>
    /// <param name="response">上游响应。</param>
    /// <param name="currentUrl">本跳的请求地址（相对 <c>Location</c> 的基地址）。</param>
    /// <param name="redirectUrl">下一跳的绝对地址（不是重定向时为空字符串）。</param>
    /// <returns>需要跟随重定向返回 <see langword="true"/>。</returns>
    private static bool TryGetRedirectTarget(HttpResponseMessage response, string currentUrl, out string redirectUrl)
    {
        redirectUrl = string.Empty;
        if (!RedirectStatusCodes.Contains(response.StatusCode))
        {
            return false;
        }

        Uri? location = response.Headers.Location;
        if (location is null)
        {
            return false;
        }

        redirectUrl = location.IsAbsoluteUri
            ? location.ToString()
            : new Uri(new Uri(currentUrl), location).ToString();
        return true;
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
        HttpContext context)
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
    private Task FailUpstreamTimeoutAsync(HttpContext context, string moduleOperation) =>
        FailUpstreamAsync(context, HttpStatusCode.GatewayTimeout, "upstream-timeout", moduleOperation);

    /// <summary>上游判定失败后的收尾：按失败原因回 502 / 504，状态码与错误码都不含签名地址。</summary>
    /// <param name="context">当前请求。</param>
    /// <param name="status">HTTP 状态码。</param>
    /// <param name="error">错误码（写入响应体）。</param>
    /// <param name="moduleOperation">发生失败的环节（写入响应体便于页面侧定位）。</param>
    /// <returns>异步任务。</returns>
    private Task FailUpstreamAsync(
        HttpContext context,
        HttpStatusCode status,
        string error,
        string moduleOperation) =>
        WriteTextAsync(
            context,
            status,
            "{\"error\":\"" + error + "\",\"operation\":\"" + moduleOperation + "\"}");

    /// <summary>
    /// 等上游响应头并校验"重定向后的最终地址"（离线用例可直接调用的入口）。
    /// </summary>
    /// <param name="target">中继目标。</param>
    /// <param name="rangeHeader">客户端传来的 <c>Range</c> 头，可为 <see langword="null"/>。</param>
    /// <returns>发送结果：成功且地址合法时带响应，超时或最终地址不被允许时判定为失败。</returns>
    internal async Task<RelaySendResult> TrySendUpstreamAsync(RelayTarget target, string? rangeHeader = null)
    {
        /*
          地址校验统一在 SendUpstreamAsync 的"每一跳发送之前"完成：这里先做一次快速判定只是为了
          在明显非法的目标上不创建请求；真正的保证来自发送入口本身（生产路径同样经过它）。
        */
        if (!IsAllowedUpstreamUrl(target.UpstreamUrl))
        {
            _logger.LogError(
                LogLevel.Warn,
                _moduleName,
                "中继目标地址不被允许，已在发送前拦截。",
                new InvalidOperationException("blocked-upstream-address"));
            return RelaySendResult.Rejected();
        }

        RelaySendResult sent = await SendUpstreamAsync(
            target,
            rangeHeader,
            HttpCompletionOption.ResponseHeadersRead,
            _upstreamHeaderTimeout).ConfigureAwait(false);
        if (sent.Blocked)
        {
            return sent;
        }

        if (sent.TimedOut)
        {
            return ReportUpstreamTimeout(sent, target, "response-headers");
        }

        if (!IsAllowedUpstreamUrl(sent.Response!.RequestMessage?.RequestUri ?? new Uri(target.UpstreamUrl)))
        {
            sent.Request!.Dispose();
            sent.Response!.Dispose();
            return RelaySendResult.Rejected();
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
        // 与字节流路径一致：先把目标地址过一遍（真正的逐跳校验在 SendUpstreamAsync 里）。
        if (!IsAllowedUpstreamUrl(target.UpstreamUrl))
        {
            _logger.LogError(
                LogLevel.Warn,
                _moduleName,
                "中继播放列表目标地址不被允许，已在发送前拦截。",
                new InvalidOperationException("blocked-upstream-address"));
            return PlaylistFetchResult.Rejected();
        }

        RelaySendResult sent = await SendUpstreamAsync(
            target,
            rangeHeader: null,
            HttpCompletionOption.ResponseContentRead,
            _upstreamHeaderTimeout).ConfigureAwait(false);
        if (sent.Blocked)
        {
            return PlaylistFetchResult.Rejected();
        }

        if (sent.TimedOut)
        {
            _ = ReportUpstreamTimeout(sent, target, "playlist");
            return PlaylistFetchResult.Timeout();
        }

        using HttpRequestMessage request = sent.Request!;
        using HttpResponseMessage response = sent.Response!;
        if (!IsAllowedUpstreamUrl(response.RequestMessage?.RequestUri ?? new Uri(target.UpstreamUrl)))
        {
            return PlaylistFetchResult.Rejected();
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

            // 相对地址必须按**最终地址**解析：上游可能把播放列表跳到另一个目录（例如 /live → /live2），
            // 拿原始地址当基地址会把重定向后的分片解析到不存在的路径。
            string baseUrl = response.RequestMessage?.RequestUri?.ToString() ?? target.UpstreamUrl;
            return new PlaylistFetchResult(RewritePlaylist(playlist, target, baseUrl), (int)HttpStatusCode.OK, TimedOut: false);
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

    private async Task StreamRelayAsync(
        HttpContext context,
        RelayTarget target,
        string token,
        CancellationToken leaseToken)
    {
        // 中继响应必须带 CORS 头：页面源是 https://appassets.local，而中继在 http://127.0.0.1，
        // 少了 Access-Control-Allow-Origin 时浏览器会直接以 "Failed to fetch" 拒绝，线路全部显示不可用。
        ApplyCorsHeaders(context, includePrivateNetworkHeader: false);

        if (target.Kind == RelayKind.HlsPlaylist)
        {
            await ServePlaylistAsync(context, target).ConfigureAwait(false);
            return;
        }

        /*
          字节流是**生产入口**：必须和播放列表走同一个发送入口（TrySendUpstreamAsync → SendUpstreamAsync），
          否则"发送前校验"只管住了离线用例与播放列表，真实的分片/FLV 请求仍会先打到目标再被拒绝（SP-04）。
        */
        RelaySendResult sent = await TrySendUpstreamAsync(
            target,
            context.Request.Headers[HeaderNameRange]).ConfigureAwait(false);
        if (sent.Blocked)
        {
            await FailUpstreamAsync(
                context,
                HttpStatusCode.BadGateway,
                "forbidden-upstream",
                "response-headers").ConfigureAwait(false);
            return;
        }

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

        await using Stream upstream = await response.Content.ReadAsStreamAsync(_shutdown.Token).ConfigureAwait(false);

        /*
          子播放列表不一定带 .m3u8 后缀：部分平台给的是 .../playlist/abc123?token=... 这种形式。
          只按后缀判定会把变体列表当二进制流转发，里面的相对切片地址不会被改写，
          hls.js 随后拿到 CDN 原始 http 地址，被混合内容拦截整条 HLS 失败。
          因此这里对"200 + 非 Range + 非空正文"的响应嗅探开头：
          以 #EXTM3U 开头就按播放列表读完并改写，否则把已读字节原样转发后继续泵。
        */
        byte[] prefix = new byte[PlaylistSniffBytes];
        int prefixLength = 0;
        bool canSniff = response.StatusCode == HttpStatusCode.OK
            && string.IsNullOrWhiteSpace(context.Request.Headers[HeaderNameRange])
            && response.Content.Headers.ContentLength != 0;
        if (canSniff)
        {
            using CancellationTokenSource prefixTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, leaseToken);
            prefixTimeout.CancelAfter(_idleTimeout);
            try
            {
                prefixLength = await ReadPrefixAsync(upstream, prefix, prefixTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!leaseToken.IsCancellationRequested)
            {
                await FailUpstreamTimeoutAsync(context, "response-body").ConfigureAwait(false);
                return;
            }

            if (LooksLikePlaylist(prefix.AsSpan(0, prefixLength)))
            {
                string playlist = await ReadSniffedPlaylistAsync(upstream, prefix.AsMemory(0, prefixLength), leaseToken)
                    .ConfigureAwait(false);
                string baseUrl = response.RequestMessage?.RequestUri?.ToString() ?? target.UpstreamUrl;
                await WritePlaylistAsync(context, RewritePlaylist(playlist, target, baseUrl)).ConfigureAwait(false);
                return;
            }
        }

        context.Response.StatusCode = (int)response.StatusCode;
        string? contentType = response.Content.Headers.ContentType?.ToString() ?? target.ContentType;
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            context.Response.ContentType = contentType;
        }

        if (response.Content.Headers.ContentLength is { } length)
        {
            context.Response.ContentLength = length;
        }

        CopyOptionalHeader(response, context, "Accept-Ranges");
        CopyOptionalHeader(response, context, "Content-Range");
        CopyOptionalHeader(response, context, "Content-Encoding");

        await PumpWithIdleTimeoutAsync(
            upstream,
            context,
            target,
            token,
            leaseToken,
            prefix.AsMemory(0, prefixLength)).ConfigureAwait(false);
    }

    /// <summary>
    /// 判断响应开头是否为 HLS 播放列表（<c>#EXTM3U</c>，允许 UTF-8 BOM 与前导空白）。
    /// </summary>
    /// <param name="prefix">响应开头若干字节。</param>
    /// <returns>是播放列表返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 只看内容不看后缀：平台给的变体列表地址经常没有扩展名，
    /// 而播放列表规范要求首行必须是 <c>#EXTM3U</c>，这是唯一可靠的判据。
    /// </remarks>
    internal static bool LooksLikePlaylist(ReadOnlySpan<byte> prefix)
    {
        ReadOnlySpan<byte> body = prefix;
        if (body.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            body = body[3..];
        }

        while (body.Length > 0 && (body[0] == (byte)'\r' || body[0] == (byte)'\n' || body[0] == (byte)' ' || body[0] == (byte)'\t'))
        {
            body = body[1..];
        }

        return body.StartsWith("#EXTM3U"u8);
    }

    /// <summary>把上游开头读到指定长度（或读到 EOF 为止）。</summary>
    /// <param name="upstream">上游数据流。</param>
    /// <param name="buffer">目标缓冲区。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际读到的字节数。</returns>
    private static async Task<int> ReadPrefixAsync(Stream upstream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await upstream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>读完嗅探认定为播放列表的正文。</summary>
    /// <param name="upstream">上游数据流（已读过前缀）。</param>
    /// <param name="prefix">已读出的前缀字节。</param>
    /// <param name="leaseToken">本连接的租约令牌。</param>
    /// <returns>播放列表文本。</returns>
    /// <exception cref="BridgeException">正文超过上限时抛出（按 502 处理）。</exception>
    private async Task<string> ReadSniffedPlaylistAsync(
        Stream upstream,
        ReadOnlyMemory<byte> prefix,
        CancellationToken leaseToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, leaseToken);
        timeout.CancelAfter(_playlistDownloadTimeout);

        using MemoryStream buffer = new(MaxSniffedPlaylistBytes);
        await buffer.WriteAsync(prefix, timeout.Token).ConfigureAwait(false);
        byte[] chunk = new byte[PlaylistBodyChunkBytes];
        while (true)
        {
            int read = await upstream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }

            if (buffer.Length + read > MaxSniffedPlaylistBytes)
            {
                throw new BridgeException("relay-playlist", "播放列表正文超过上限，已拒绝继续缓冲。");
            }

            buffer.Write(chunk, 0, read);
        }
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
    private async Task ServePlaylistAsync(HttpContext context, RelayTarget target)
    {
        PlaylistFetchResult fetched = await TryReadPlaylistAsync(target, _playlistDownloadTimeout).ConfigureAwait(false);
        if (fetched.Blocked)
        {
            await FailUpstreamAsync(
                context,
                HttpStatusCode.BadGateway,
                "forbidden-upstream",
                "playlist").ConfigureAwait(false);
            return;
        }

        if (fetched.TimedOut)
        {
            await FailUpstreamTimeoutAsync(context, "playlist").ConfigureAwait(false);
            return;
        }

        if (fetched.StatusCode != (int)HttpStatusCode.OK || fetched.Playlist is null)
        {
            context.Response.StatusCode = fetched.StatusCode;
            context.Response.ContentLength = 0;
            return;
        }

        await WritePlaylistAsync(context, fetched.Playlist).ConfigureAwait(false);
    }

    /// <summary>把（改写后的）播放列表文本写回客户端。</summary>
    /// <param name="context">当前请求。</param>
    /// <param name="playlist">播放列表文本。</param>
    private async Task WritePlaylistAsync(HttpContext context, string playlist)
    {
        byte[] payload = Encoding.UTF8.GetBytes(playlist);

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = HlsPlaylistContentType;
        context.Response.ContentLength = payload.Length;
        await _outputLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await context.Response.Body.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            _outputLock.Release();
        }
    }

    /// <summary>
    /// 一次桥接请求的路由结论（把"方法 + 路径 → 处理器"的映射抽成可离线断言的纯判定）。
    /// </summary>
    internal enum BridgeRoute
    {
        /// <summary>未知路径：一律 404（未知路径的预检同样是 404）。</summary>
        NotFound = 0,

        /// <summary>中继路由（<c>/relay/**</c>）。</summary>
        Relay = 1,

        /// <summary>mpv 外挂播放路由（<c>/play</c>）。</summary>
        Play = 2,

        /// <summary>健康检查路由（<c>/health</c>）。</summary>
        Health = 3,

        /// <summary>已知路由的 CORS 预检：204 + CORS 头（仅此处携带私有网络头）。</summary>
        Preflight = 4,
    }

    /// <summary>
    /// 一次播放列表改写登记出的子地址（同一次改写算一代）。
    /// </summary>
    /// <param name="CreatedAt">该代的登记时间（UTC），用于按时间保留旧代。</param>
    /// <param name="Children">该代登记的本地子地址。</param>
    /// <remarks>类型是 internal 的原因同 <see cref="RelaySendResult"/>：离线用例要直接验证裁剪规则。</remarks>
    internal sealed record PlaylistGeneration(DateTimeOffset CreatedAt, List<string> Children);

    /// <summary>
    /// 一次中继上游请求的结果：成功时带请求与响应，超时时两者均为 <see langword="null"/>。
    /// </summary>
    /// <param name="Request">请求（成功时非空，调用方负责释放）。</param>
    /// <param name="Response">响应（成功时非空，调用方负责释放）。</param>
    /// <param name="TimedOut">是否没有拿到可用的上游响应（超时、被拒、跳转过多）。</param>
    /// <param name="Blocked">是否因为目标地址不被允许而被拒（调用方据此回 502 而不是 504）。</param>
    /// <remarks>
    /// 类型是 internal 而不是 private：离线用例要直接读这三个字段来断言超时与重定向拒绝
    /// （见 <c>tests/StreamPilot.Tests/Cases/RelaySafetyTests.cs</c>），
    /// 而对程序集外它仍然不可见（`InternalsVisibleTo` 只开放给测试工程）。
    /// </remarks>
    internal readonly record struct RelaySendResult(
        HttpRequestMessage? Request,
        HttpResponseMessage? Response,
        bool TimedOut,
        bool Blocked = false)
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

        /// <summary>构造"目标地址被拒"结果。</summary>
        /// <returns>被拒结果（<see cref="TimedOut"/> 同样为真，调用方按 <see cref="Blocked"/> 区分 502/504）。</returns>
        public static RelaySendResult Rejected() => new(null, null, TimedOut: true, Blocked: true);
    }

    /// <summary>
    /// 一次 HLS 播放列表拉取的结果。
    /// </summary>
    /// <param name="Playlist">改写后的播放列表文本；失败时为 <see langword="null"/>。</param>
    /// <param name="StatusCode">上游状态码（成功时为 200，被拒时为 502，超时为 504）。</param>
    /// <param name="TimedOut">是否没有拿到可用的播放列表（超时、被拒、跳转过多）。</param>
    /// <param name="Blocked">是否因为目标地址不被允许而被拒（调用方据此回 502 而不是 504）。</param>
    /// <remarks>类型是 internal 的原因同 <see cref="RelaySendResult"/>。</remarks>
    internal readonly record struct PlaylistFetchResult(string? Playlist, int StatusCode, bool TimedOut, bool Blocked = false)
    {
        /// <summary>构造超时结果。</summary>
        /// <returns>超时结果。</returns>
        public static PlaylistFetchResult Timeout() => new(null, (int)HttpStatusCode.GatewayTimeout, TimedOut: true);

        /// <summary>构造"目标地址被拒"结果。</summary>
        /// <returns>被拒结果。</returns>
        public static PlaylistFetchResult Rejected() =>
            new(null, (int)HttpStatusCode.BadGateway, TimedOut: true, Blocked: true);
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
    /// <param name="target">播放列表中继目标（改写出的子节点继承它的 Referer）。</param>
    /// <param name="baseUrl">相对地址的基地址（跟随重定向后是最后一跳的地址）。</param>
    /// <returns>改写后的播放列表文本。</returns>
    private string RewritePlaylist(string playlist, RelayTarget target, string baseUrl)
    {
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
                builder.Append(RewritePlaylistTag(line, target, baseUrl, children)).Append('\n');
                continue;
            }

            builder.Append(RegisterChild(ResolveChildUrl(baseUrl, line), target, children)).Append('\n');
        }

        AppendPlaylistGeneration(target.UpstreamUrl, children);
        return builder.ToString();
    }

    /// <summary>改写带 URI 属性的标签（密钥、初始化段、备用音视频轨）。</summary>
    /// <param name="line">标签行。</param>
    /// <param name="target">播放列表中继目标。</param>
    /// <param name="baseUrl">相对地址的基地址（跟随重定向后是最后一跳的地址）。</param>
    /// <param name="children">本次改写登记的本地地址集合。</param>
    /// <returns>改写后的标签行。</returns>
    private string RewritePlaylistTag(string line, RelayTarget target, string baseUrl, List<string> children)
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

        string childUrl = ResolveChildUrl(baseUrl, line[valueStart..valueEnd]);
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
        // 子地址是改写播放列表时批量登记出来的：探测请求（拿到响应头就 abort）也会走这条路，
        // 而探测不会再请求这些子地址。按"试用期"登记，没人真正请求过就在
        // BridgeConstants.ProvisionalRelaySeconds 秒后自动消失，首个真实请求会把它转正。
        string localUrl = _registry.Register(
            new RelayTarget
            {
                UpstreamUrl = childUrl,
                Referer = target.Referer,
                Kind = kind,
            },
            provisional: true);
        children.Add(localUrl);
        return localUrl;
    }

    /// <summary>
    /// 登记新一代子节点，并回收超出保留代数的旧一代。
    /// </summary>
    /// <param name="playlistUrl">播放列表上游地址（作为分组键）。</param>
    /// <param name="children">本次改写登记的本地子地址。</param>
    /// <remarks>
    /// 不能每次刷新就把上一代全部释放：播放器可能仍在请求上一版列表里的切片，
    /// 立即回收会表现为"备用地址 404"。这里保留最近 <see cref="MaxRetainedPlaylistGenerations"/> 代，
    /// 既保证注册数量有界（不随刷新无限增长），也不打断正在使用的上一版列表。
    /// </remarks>
    private void AppendPlaylistGeneration(string playlistUrl, List<string> children)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<string> expired = [];
        _playlistChildren.AddOrUpdate(
            playlistUrl,
            _ => [new PlaylistGeneration(now, children)],
            (_, generations) =>
            {
                List<PlaylistGeneration> updated = [new PlaylistGeneration(now, children), .. generations];
                expired = TrimGenerations(updated, now);
                return updated;
            });

        foreach (string url in expired)
        {
            string? expiredToken = RelayRegistry.ExtractToken(url);
            if (expiredToken is not null)
            {
                // 过期的一代里如果有子播放列表，它自己登记的子节点也要一起回收。
                ReleaseRegistration(expiredToken, new HashSet<string>(StringComparer.Ordinal));
            }
        }
    }

    /// <summary>
    /// 从"新 → 旧"排列的代数里裁掉可以回收的旧代，并返回这些代里的子地址。
    /// </summary>
    /// <param name="generations">代数列表（会被就地修改）。</param>
    /// <param name="now">当前时间。</param>
    /// <returns>被裁掉的子地址。</returns>
    /// <remarks>
    /// 两条约束同时生效：至少保留 <see cref="MinRetainedPlaylistGenerations"/> 代（防止刚刷新就把
    /// 上一版列表里的切片地址释放掉），单代至少存活
    /// <see cref="PlaylistGenerationGraceSeconds"/> 秒（照顾慢链路的长下载），
    /// 总量不超过 <see cref="MaxRetainedPlaylistGenerations"/> 代（防止刷新过密时无限增长）。
    /// </remarks>
    internal static List<string> TrimGenerations(List<PlaylistGeneration> generations, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(generations);
        List<string> expired = [];
        while (generations.Count > MinRetainedPlaylistGenerations)
        {
            PlaylistGeneration oldest = generations[^1];
            bool aged = now - oldest.CreatedAt >= TimeSpan.FromSeconds(PlaylistGenerationGraceSeconds);
            bool overcrowded = generations.Count > MaxRetainedPlaylistGenerations;
            if (!aged && !overcrowded)
            {
                break;
            }

            expired.AddRange(oldest.Children);
            generations.RemoveAt(generations.Count - 1);
        }

        return expired;
    }

    /// <summary>释放某个播放列表登记的所有子节点（含子播放列表自己的子节点）。</summary>
    /// <param name="playlistUrl">播放列表上游地址（分组键）。</param>
    /// <param name="visited">本次释放已经处理过的令牌（防止重复释放与环）。</param>
    private void ReleasePlaylistChildren(string playlistUrl, HashSet<string> visited)
    {
        if (!_playlistChildren.TryRemove(playlistUrl, out List<PlaylistGeneration>? previous))
        {
            return;
        }

        foreach (PlaylistGeneration generation in previous)
        {
            foreach (string child in generation.Children)
            {
                string? childToken = RelayRegistry.ExtractToken(child);
                if (childToken is not null)
                {
                    ReleaseRegistration(childToken, visited);
                }
            }
        }
    }

    /// <summary>判断地址是否指向 HLS 播放列表。</summary>
    /// <param name="url">地址。</param>
    /// <returns>是播放列表返回 <see langword="true"/>。</returns>
    private static bool IsPlaylistUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase));

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
    /// <param name="token">中继令牌（转发期间用于续命）。</param>
    /// <param name="leaseToken">本连接的租约令牌（被同令牌的新连接接管时取消）。</param>
    /// <param name="prefix">嗅探时已经读出的字节（需先转发给客户端），可为空。</param>
    private async Task PumpWithIdleTimeoutAsync(
        Stream upstream,
        HttpContext context,
        RelayTarget target,
        string token,
        CancellationToken leaseToken,
        ReadOnlyMemory<byte> prefix = default)
    {
        byte[] buffer = new byte[RelayBufferBytes];
        /*
          把 RequestAborted 纳入取消链是本轮迁移的核心收益：HttpListener 拿不到"客户端已断开"，
          只能等下一次写失败（或在空闲看门狗 30 秒后）才发现，上游连接因此会多挂几十秒；
          斗鱼这类"同一签名地址只允许 1 条并发"的平台，下一次重连正好撞上就会被切断。
        */
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token,
            leaseToken,
            context.RequestAborted);
        long nextTouchAt = Environment.TickCount64 + (RelayTouchIntervalSeconds * 1000L);
        _registry.Touch(token);
        try
        {
            if (!prefix.IsEmpty)
            {
                await context.Response.Body.WriteAsync(prefix, linked.Token).ConfigureAwait(false);
            }

            while (true)
            {
                using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));
                int read = await upstream.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);

                if (Environment.TickCount64 >= nextTouchAt)
                {
                    // 连接还活着就等于注册还有效：否则 10 分钟后断流重连会 404（见 RelayTouchIntervalSeconds）。
                    _registry.Touch(token);
                    nextTouchAt = Environment.TickCount64 + (RelayTouchIntervalSeconds * 1000L);
                }
            }

            await context.Response.Body.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            if (leaseToken.IsCancellationRequested)
            {
                _logger.Debug(_moduleName, "同一中继的新连接已接管，旧连接关闭。", new Dictionary<string, object?>
                {
                    ["host"] = TryGetHost(target.UpstreamUrl),
                });
            }
            else if (context.RequestAborted.IsCancellationRequested)
            {
                _logger.Debug(_moduleName, "客户端提前断开中继连接，已同时关闭上游连接。", new Dictionary<string, object?>
                {
                    ["host"] = TryGetHost(target.UpstreamUrl),
                });
            }
            else
            {
                _logger.Warn(_moduleName, "中继上游长时间无数据，已断开。", new Dictionary<string, object?>
                {
                    ["host"] = TryGetHost(target.UpstreamUrl),
                });
            }
        }
        catch (IOException exception)
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
    private static void CopyOptionalHeader(HttpResponseMessage response, HttpContext context, string name)
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

    private async Task WriteTextAsync(HttpContext context, HttpStatusCode status, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = payload.Length;
        ApplyCorsHeaders(context, includePrivateNetworkHeader: false);

        await _outputLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await context.Response.Body.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            _outputLock.Release();
        }
    }

    private static Task WriteEmptyAsync(HttpContext context, HttpStatusCode status, bool includePrivateNetworkHeader)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentLength = 0;
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
    private static void ApplyCorsHeaders(HttpContext context, bool includePrivateNetworkHeader)
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
