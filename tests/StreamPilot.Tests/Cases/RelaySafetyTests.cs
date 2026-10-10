namespace StreamPilot.Tests.Cases;

using System.Net;
using StreamPilot.Bridge;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Services;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// 中继的安全与超时回归：目的地址白名单（含重定向后的最终地址）、
/// "等上游响应头"与"HLS 播放列表正文读取"的有限超时，以及长连接不被这两条超时误杀。
/// </summary>
/// <remarks>
/// 全部离线运行：用 <see cref="HttpMessageHandler"/> 替身替代真实 CDN，
/// 通过 <c>BridgeHost</c> 的 internal 构造函数与 internal 转发入口注入
/// （见 <c>TrySendUpstreamAsync</c> / <c>TryReadPlaylistAsync</c> / <c>TryOpenUpstreamStreamAsync</c> 的 remarks），
/// 因此不启动 <c>HttpListener</c>、不访问网络。
/// </remarks>
[TestClass]
public sealed class RelaySafetyTests
{
    /// <summary>上游等响应头的替身超时：用例要快速得到结论，不等待真实常量。</summary>
    private static readonly TimeSpan ShortHeaderTimeout = TimeSpan.FromMilliseconds(80);

    /// <summary>播放列表正文读取的替身超时。</summary>
    private static readonly TimeSpan ShortPlaylistTimeout = TimeSpan.FromMilliseconds(80);

    /// <summary>长连接空闲看门狗窗口：取足够长，确保用例里"数据之间的小间隔"不会触发它。</summary>
    private static readonly TimeSpan GenerousIdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>应当放行的中继目的地址。</summary>
    private static readonly string[] AllowedUpstreamUrls =
    [
        "https://d1--cn-gotcha104.bilivideo.com/live-bvc/x.flv?expires=1&sign=secret",
        "http://cdn.example.com/live/index.m3u8",
        // 含 "localhost" 的公网域名必须放行：只拒绝它以独立标签出现（localhost / sub.localhost）。
        "https://localhost.example.com/edge/seg-1.ts",
        "https://cdn.example.com:8443/edge/seg-1.ts",
        "http://203.0.113.10/x.flv",
    ];

    /// <summary>应当拒绝的中继目的地址：非 http(s) 协议、回环 / 私网 / 链路本地 / 未指定。</summary>
    private static readonly string[] BlockedUpstreamUrls =
    [
        "rtmp://live.example.com/live/room",
        "rtmps://live.example.com/live/room",
        "file:///C:/Windows/win.ini",
        "ftp://cdn.example.com/x.flv",
        "http://127.0.0.1:5566/health",
        "http://127.1.2.3/x.flv",
        "https://localhost/x.flv",
        "https://localhost:5566/x.flv",
        "https://sub.localhost/x.flv",
        "http://0.0.0.0/x.flv",
        "http://[::1]/x.flv",
        "http://[::]/x.flv",
        "http://10.0.0.5/x.flv",
        "http://172.16.9.9/x.flv",
        "http://172.31.255.254/x.flv",
        "http://192.168.1.10/x.flv",
        "http://169.254.10.10/x.flv",
        "http://100.64.1.1/x.flv",
        "http://198.18.0.1/x.flv",
        "http://192.0.0.8/x.flv",
        "http://240.0.0.1/x.flv",
        "http://[fe80::1]/x.flv",
        "http://[fd00::1]/x.flv",
        "http://[ff02::1]/x.flv",
        // IPv4-mapped IPv6 必须按内层 IPv4 判定，否则"看起来是 IPv6"的地址就能绕过私网检查。
        "http://[::ffff:10.0.0.5]/x.flv",
        "http://[::ffff:127.0.0.1]/x.flv",
        // 整数 / 十六进制形式的回环字面量：Uri 会把它们规范化成 127.0.0.1。
        "http://2130706433/x.flv",
        "http://0x7f.1/x.flv",
        "not-a-url",
        "",
    ];

    /// <summary>目的地址白名单：公网 http/https 放行，其余一律拒绝。</summary>
    [TestMethod("中继安全：目的地址只允许公网 http / https")]
    public void UpstreamAddressAllowlist()
    {
        foreach (string url in AllowedUpstreamUrls)
        {
            Assert.True(BridgeHost.IsAllowedUpstreamUrl(url), "应当放行的地址：" + url);
        }

        foreach (string url in BlockedUpstreamUrls)
        {
            Assert.False(BridgeHost.IsAllowedUpstreamUrl(url), "应当拒绝的地址：" + url);
        }

        // 重定向后的最终地址走同一个判定（Uri 重载，避免字符串往返带来的歧义）。
        Assert.False(BridgeHost.IsAllowedUpstreamUrl(new Uri("http://127.0.0.1:9/relay/x")), "重定向到回环必须被拒绝");
        Assert.True(BridgeHost.IsAllowedUpstreamUrl(new Uri("https://cdn.example.com/x.flv")), "重定向到公网仍然放行");
    }

    /// <summary>
    /// SP-02：中继的 HTTP 注册路由已关闭，注册只能进程内发起。
    /// </summary>
    /// <remarks>
    /// 这里做源码级断言（而不去真实绑定 <c>HttpListener</c> 发一次 POST）：注册入口只剩
    /// <see cref="BridgeHost.RegisterRelay"/> 这一条进程内 API，HTTP 侧没有任何注册处理器；
    /// 路由本身只按 <see cref="BridgeHost.RelayPathPrefix"/> 处理 GET，其余方法一律 404。
    /// </remarks>
    [TestMethod("中继安全：HTTP 侧不存在注册处理器（注册只能进程内发起）")]
    public void RelayHasNoHttpRegisterHandler()
    {
        System.Reflection.MethodInfo[] declared = typeof(BridgeHost)
            .GetMethods(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly);

        foreach (System.Reflection.MethodInfo method in declared)
        {
            string name = method.Name;
            // 只关心 HTTP 处理器：进程内的注册表操作（RegisterRelay / RegisterChild）不在检查范围。
            bool isHttpHandler = name.Contains("Handler", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Http", StringComparison.OrdinalIgnoreCase);
            bool isRegistrar = name.Contains("Register", StringComparison.OrdinalIgnoreCase);
            if (!isHttpHandler || !isRegistrar)
            {
                continue;
            }

            throw new AssertionFailedException("HTTP 侧不得再出现中继注册处理器：" + name);
        }

        Assert.Equal("/relay", BridgeHost.RelayPathPrefix, "中继路由前缀保持 /relay（GET /relay/{token}）");
        Assert.Equal("https://appassets.local", BridgeHost.PlayerPageOrigin, "跨源响应头只回播放页来源，不再用 *");
    }

    /// <summary>
    /// 预检只对已知路由返回 204：未知路径（含旧文档里并不存在的 <c>/web/*</c> 静态回退）一律 404。
    /// </summary>
    /// <remarks>
    /// 路由判定是纯函数（<see cref="BridgeHost.ResolveRoute"/>），因此这里直接断言而无需启动
    /// <c>HttpListener</c>：旧实现对**任意**路径的 <c>OPTIONS</c> 都回 204 + CORS，
    /// 等于向本机任意页面确认"这个回环端口上什么路径都有人应答"。
    /// </remarks>
    [TestMethod("桥接路由：预检只为已知路径返回 204，未知路径一律 404")]
    public void PreflightOnlyAnswersKnownRoutes()
    {
        Assert.Equal(BridgeHost.BridgeRoute.Preflight, BridgeHost.ResolveRoute("OPTIONS", "/health"));
        Assert.Equal(BridgeHost.BridgeRoute.Preflight, BridgeHost.ResolveRoute("options", "/play"), "方法名大小写不敏感");
        Assert.Equal(BridgeHost.BridgeRoute.Preflight, BridgeHost.ResolveRoute("OPTIONS", "/relay/abc123"));
        Assert.Equal(
            BridgeHost.BridgeRoute.NotFound,
            BridgeHost.ResolveRoute("OPTIONS", "/web/player.html"),
            "旧文档里描述的静态回退路径并不存在，预检不得应答");
        Assert.Equal(BridgeHost.BridgeRoute.NotFound, BridgeHost.ResolveRoute("OPTIONS", "/"));
        Assert.Equal(BridgeHost.BridgeRoute.NotFound, BridgeHost.ResolveRoute("OPTIONS", "/relayx/1"), "前缀相似不等于已知路由");

        Assert.Equal(BridgeHost.BridgeRoute.Relay, BridgeHost.ResolveRoute("GET", "/relay/abc123"));
        Assert.Equal(
            BridgeHost.BridgeRoute.Relay,
            BridgeHost.ResolveRoute("POST", "/relay/abc123"),
            "HTTP 注册路由已关闭：非 GET 仍进中继处理器，由它回 404 并记 Warn");
        Assert.Equal(BridgeHost.BridgeRoute.Play, BridgeHost.ResolveRoute("GET", "/play"));
        Assert.Equal(BridgeHost.BridgeRoute.Health, BridgeHost.ResolveRoute("HEAD", "/health"));
        Assert.Equal(BridgeHost.BridgeRoute.NotFound, BridgeHost.ResolveRoute("GET", "/web/index.html"));
    }

    /// <summary>上游被重定向到回环地址：最终地址判定为不允许，转发入口直接判定失败。</summary>
    [TestMethod("中继安全：重定向到回环地址被拒绝")]
    public async Task RedirectToLoopbackIsRejected()
    {
        using RedirectingHandler handler = new("http://127.0.0.1:5566/health");
        await using BridgeHost host = CreateHost(handler);

        RelayTarget target = new() { UpstreamUrl = "https://cdn.example.com/live.flv" };
        BridgeHost.RelaySendResult result = await host.TrySendUpstreamAsync(target);

        Assert.True(result.TimedOut, "最终地址不被允许时必须判定为失败（调用方据此回 502）");
        Assert.Null(result.Response, "被拒绝的响应不得交给调用方");
        Assert.False(
            BridgeHost.IsAllowedUpstreamUrl(new Uri("http://127.0.0.1:5566/health")),
            "重定向后的回环地址必须被判为不允许");
    }

    /// <summary>上游响应头在超时窗口之后返回缺失：判定为超时，并记 Warn。</summary>
    [TestMethod("中继超时：上游响应头超时被判为超时并记日志")]
    public async Task HeaderTimeoutIsReported()
    {
        using StallingHandler handler = new();
        RecordingLogger logger = new();
        await using BridgeHost host = CreateHost(handler, logger);

        RelayTarget target = new() { UpstreamUrl = "https://cdn.example.com/live.flv" };
        BridgeHost.RelaySendResult result = await host.TrySendUpstreamAsync(target);

        Assert.True(result.TimedOut, "上游一直不回响应头时必须判定为超时");
        Assert.Null(result.Response, "超时不得返回半成品响应");
        Assert.True(handler.WasCancelled, "超时必须真的取消上游请求，而不是继续等");
        Assert.True(logger.HasLevel(LogLevel.Warn), "上游超时必须记 Warn 日志");
        Assert.True(logger.HasMessageContaining("中继上游超时"), "日志必须说清是上游超时");
    }

    /// <summary>HLS 播放列表正文读取超时：判定为超时并保持状态码语义。</summary>
    [TestMethod("中继超时：播放列表正文读取超时被判为超时")]
    public async Task PlaylistBodyTimeoutIsReported()
    {
        using SlowBodyHandler handler = new(TimeSpan.FromSeconds(5));
        RecordingLogger logger = new();
        await using BridgeHost host = CreateHost(handler, logger);

        RelayTarget target = new()
        {
            UpstreamUrl = "https://cdn.example.com/live/index.m3u8",
            Kind = RelayKind.HlsPlaylist,
        };
        BridgeHost.PlaylistFetchResult result = await host.TryReadPlaylistAsync(target, ShortPlaylistTimeout);

        Assert.True(result.TimedOut, "播放列表正文在时限内读不完时必须判定为超时");
        Assert.Equal((int)HttpStatusCode.GatewayTimeout, result.StatusCode, "超时的状态码语义是 504");
        Assert.Null(result.Playlist, "超时不得返回半截播放列表");
        Assert.True(logger.HasLevel(LogLevel.Warn), "播放列表正文超时必须记 Warn 日志");
    }

    /// <summary>正常播放列表：正文可读时给出改写后的文本（对照组，避免"一律超时"的假通过）。</summary>
    [TestMethod("中继超时：正常播放列表仍被正确改写")]
    public async Task PlaylistIsRewrittenOnSuccess()
    {
        using SlowBodyHandler handler = new(TimeSpan.Zero);
        await using BridgeHost host = CreateHost(handler);

        RelayTarget target = new()
        {
            UpstreamUrl = "https://cdn.example.com/live/index.m3u8",
            Kind = RelayKind.HlsPlaylist,
        };
        BridgeHost.PlaylistFetchResult result = await host.TryReadPlaylistAsync(target, ShortPlaylistTimeout);

        Assert.False(result.TimedOut);
        Assert.Equal((int)HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Playlist);
        Assert.DoesNotContain("cdn.example.com", result.Playlist!, "改写后不得残留上游主机名（地址已换成本地中继 token）");
        Assert.True(result.Playlist!.Contains("#EXTM3U", StringComparison.Ordinal), "播放列表头必须保留");
        Assert.True(result.Playlist.Contains("#EXTINF", StringComparison.Ordinal), "分片时长标签必须保留");
    }

    /// <summary>长连接：响应头在超时窗口内返回后，只要数据持续到达就不能被这两条超时误杀。</summary>
    [TestMethod("中继超时：长连接不被响应头 / 播放列表超时误杀")]
    public async Task LongLivedStreamIsNotKilledByHeaderTimeout()
    {
        using PacedStreamHandler handler = new(totalChunks: 8, chunkInterval: TimeSpan.FromMilliseconds(20));
        await using BridgeHost host = CreateHost(
            handler,
            headerTimeout: TimeSpan.FromMilliseconds(50),
            idleTimeout: GenerousIdleTimeout);

        RelayTarget target = new() { UpstreamUrl = "https://cdn.example.com/live.flv" };
        Stream? upstream = await host.TryOpenUpstreamStreamAsync(target);
        Assert.NotNull(upstream, "响应头及时返回时不应被判为超时");

        // 读取发生在响应头超时（50 ms）之后，总耗时（约 160 ms）也远超该窗口：
        // 只要这里能读到全部 8 个分片，就证明"响应头超时"没有把长连接一起掐掉。
        int totalBytes = 0;
        byte[] buffer = new byte[16];
        int read;
        while ((read = await upstream!.ReadAsync(buffer)) > 0)
        {
            totalBytes += read;
        }

        await upstream!.DisposeAsync();
        Assert.Equal(8, totalBytes, "长连接的每个分片都必须被读到（数据间隔小于空闲窗口）");
    }

    /// <summary>被测实例：注入替身处理器与毫秒级超时。</summary>
    /// <param name="handler">上游消息处理器替身。</param>
    /// <param name="logger">日志记录替身（缺省用不记录的空实现）。</param>
    /// <param name="playlistTimeout">播放列表正文读取超时。</param>
    /// <param name="headerTimeout">等响应头超时。</param>
    /// <param name="idleTimeout">空闲看门狗窗口。</param>
    /// <returns>被测实例。</returns>
    private static BridgeHost CreateHost(
        HttpMessageHandler handler,
        RecordingLogger? logger = null,
        TimeSpan? playlistTimeout = null,
        TimeSpan? headerTimeout = null,
        TimeSpan? idleTimeout = null) =>
        new(
            new BridgeOptions(),
            () => null,
            (IStructuredLogger?)logger ?? NullStructuredLogger.Instance,
            handler,
            headerTimeout ?? ShortHeaderTimeout,
            playlistTimeout ?? ShortPlaylistTimeout,
            idleTimeout ?? GenerousIdleTimeout);

    /// <summary>永远不回响应头的替身。</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        /// <summary>本次请求是否被取消（用于断言超时真的取消了上游请求）。</summary>
        public bool WasCancelled { get; private set; }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }

            throw new InvalidOperationException("永不返回：取消令牌应当在超时后终止这一次等待。");
        }
    }

    /// <summary>响应头立刻返回、正文按指定延迟产出的替身。</summary>
    private sealed class SlowBodyHandler : HttpMessageHandler
    {
        /// <summary>播放列表正文（含一个绝对切片地址，用于验证改写）。</summary>
        private const string PlaylistBody = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.000,\nhttps://cdn.example.com/live/seg-1.ts\n";

        private readonly TimeSpan _bodyDelay;

        /// <summary>初始化替身。</summary>
        /// <param name="bodyDelay">正文产出前的等待时长。</param>
        public SlowBodyHandler(TimeSpan bodyDelay) => _bodyDelay = bodyDelay;

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(PlaylistBody, System.Text.Encoding.UTF8, "application/vnd.apple.mpegurl"),
            };
            if (_bodyDelay > TimeSpan.Zero)
            {
                response.Content = new StreamContent(new DelayedPlaylistStream(PlaylistBody, _bodyDelay));
            }

            return Task.FromResult(response);
        }
    }

    /// <summary>把公网地址重定向到指定地址的替身（等价于真实处理器已跟到最终地址）。</summary>
    private sealed class RedirectingHandler : HttpMessageHandler
    {
        private readonly string _location;

        /// <summary>初始化替身。</summary>
        /// <param name="location">重定向目标地址。</param>
        public RedirectingHandler(string location) => _location = location;

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.Redirect)
            {
                // 真实 HttpClient（AllowAutoRedirect = true）跟随后会把最终地址写回 RequestMessage，
                // 这里把替身响应的 RequestMessage 指向 Location，等价于"已经跟到了最终地址"。
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, _location),
            };
            response.Headers.Location = new Uri(_location);
            return Task.FromResult(response);
        }
    }

    /// <summary>按固定间隔持续产出数据的替身（模拟健康直播流）。</summary>
    private sealed class PacedStreamHandler : HttpMessageHandler
    {
        private readonly int _totalChunks;
        private readonly TimeSpan _chunkInterval;

        /// <summary>初始化替身。</summary>
        /// <param name="totalChunks">总分片数。</param>
        /// <param name="chunkInterval">分片间隔。</param>
        public PacedStreamHandler(int totalChunks, TimeSpan chunkInterval)
        {
            _totalChunks = totalChunks;
            _chunkInterval = chunkInterval;
        }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new PacedStream(_totalChunks, _chunkInterval)),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/x-flv");
            return Task.FromResult(response);
        }
    }

    /// <summary>先等待指定时长再一次性给出正文的流（正文因此会跨过播放列表超时）。</summary>
    private sealed class DelayedPlaylistStream : Stream
    {
        private readonly byte[] _payload;
        private readonly TimeSpan _delay;

        /// <summary>初始化流。</summary>
        /// <param name="text">正文文本。</param>
        /// <param name="delay">产出前的等待时长。</param>
        public DelayedPlaylistStream(string text, TimeSpan delay)
        {
            _payload = System.Text.Encoding.UTF8.GetBytes(text);
            _delay = delay;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => _payload.Length;

        /// <inheritdoc />
        public override long Position { get; set; }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= _payload.Length)
            {
                return 0;
            }

            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            int written = Math.Min(buffer.Length, _payload.Length - (int)Position);
            _payload.AsMemory((int)Position, written).CopyTo(buffer);
            Position += written;
            return written;
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc />
        public override void Flush()
        {
            // 只读流没有需要刷新的内容。
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("只读流不支持定位。");

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException("只读流不支持改长度。");

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("只读流不支持写入。");
    }

    /// <summary>按固定间隔产出"每片 1 字节"的只读流。</summary>
    private sealed class PacedStream : Stream
    {
        private readonly int _totalChunks;
        private readonly TimeSpan _chunkInterval;
        private int _produced;

        /// <summary>初始化流。</summary>
        /// <param name="totalChunks">总分片数。</param>
        /// <param name="chunkInterval">分片间隔。</param>
        public PacedStream(int totalChunks, TimeSpan chunkInterval)
        {
            _totalChunks = totalChunks;
            _chunkInterval = chunkInterval;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => _totalChunks;

        /// <inheritdoc />
        public override long Position
        {
            get => _produced;
            set => throw new NotSupportedException("只读流不支持定位。");
        }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_produced >= _totalChunks)
            {
                return 0;
            }

            await Task.Delay(_chunkInterval, cancellationToken).ConfigureAwait(false);
            int written = Math.Min(buffer.Length, 1);
            buffer.Span[..written].Fill(0x47);
            _produced++;
            return written;
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc />
        public override void Flush()
        {
            // 只读流没有需要刷新的内容。
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("只读流不支持定位。");

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException("只读流不支持改长度。");

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("只读流不支持写入。");
    }
}
