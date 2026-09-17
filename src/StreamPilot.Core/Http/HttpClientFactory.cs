namespace StreamPilot.Core.Http;

using StreamPilot.Core.Configuration;
using StreamPilot.Core.Logging;

/// <summary>
/// 统一构造 <see cref="HttpClient"/> 的工厂。
/// </summary>
/// <remarks>
/// 一个进程内共享少量 <see cref="HttpClient"/> 实例（按超时/代理组合缓存），
/// 避免端口耗尽与重复 TLS 握手；所有请求都必须显式指定超时（CLAUDE.md 性能规则）。
/// </remarks>
public sealed class HttpClientFactory : IDisposable
{
    /// <summary>默认 User-Agent：Windows Chrome，与主流 Web 端一致。</summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36";

    /// <summary>默认请求超时（秒）。</summary>
    public const int DefaultTimeoutSeconds = 8;

    /// <summary>重定向最大跳数。</summary>
    public const int MaxRedirects = 5;

    /// <summary>流式客户端（长连接）在空闲连接池中保留的时间。</summary>
    private static readonly TimeSpan StreamingPooledConnectionLifetime = TimeSpan.FromMinutes(10);

    /// <summary>流式客户端的连接超时（秒）。</summary>
    public const int StreamingConnectTimeoutSeconds = 15;

    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Core.Http";
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly string _userAgent;
    private HttpClient? _streamingClient;
    private bool _disposed;

    /// <summary>初始化工厂。</summary>
    /// <param name="options">网络配置。</param>
    /// <param name="logger">结构化日志。</param>
    public HttpClientFactory(NetworkOptions options, IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        NetworkOptions = options;
        _userAgent = string.IsNullOrWhiteSpace(options.UserAgent) ? DefaultUserAgent : options.UserAgent;
    }

    /// <summary>当前网络配置。</summary>
    public NetworkOptions NetworkOptions { get; }

    /// <summary>取得（或创建）指定超时的共享客户端。</summary>
    /// <param name="timeoutSeconds">请求超时（秒）；为 <see langword="null"/> 时使用配置值。</param>
    /// <returns>共享的 <see cref="HttpClient"/>。</returns>
    /// <exception cref="ObjectDisposedException">工厂已释放时抛出。</exception>
    public HttpClient GetClient(int? timeoutSeconds = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int timeout = Math.Clamp(timeoutSeconds ?? NetworkOptions.RequestTimeoutSeconds, 1, 120);
        string key = timeout.ToString(System.Globalization.CultureInfo.InvariantCulture);

        lock (_gate)
        {
            if (_clients.TryGetValue(key, out HttpClient? existing))
            {
                return existing;
            }

            HttpClient created = Create(timeout);
            _clients[key] = created;
            return created;
        }
    }

    /// <summary>
    /// 取得专用于长连接直播流的客户端：无整体超时，仅限制建立连接的超时。
    /// </summary>
    /// <returns>共享的流式客户端（所有权属于本工厂，调用方 <b>不得</b> 释放）。</returns>
    /// <remarks>
    /// 直播流是持续读取的长连接，若使用带整体超时的客户端，录制会在超时时间到达时被取消。
    /// 读取停滞由录制器的 stall 超时（默认 12 秒）负责检测与重连。
    /// </remarks>
    /// <exception cref="ObjectDisposedException">工厂已释放时抛出。</exception>
    public HttpClient GetStreamingClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_streamingClient is not null)
            {
                return _streamingClient;
            }

            SocketsHttpHandler handler = CreateHandler();
            HttpClient client = new(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            ApplyDefaultHeaders(client);
            _streamingClient = client;
            return client;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            foreach (HttpClient client in _clients.Values)
            {
                client.Dispose();
            }

            _clients.Clear();
            _streamingClient?.Dispose();
            _streamingClient = null;
        }
    }

    private HttpClient Create(int timeoutSeconds)
    {
        HttpClient client = new(CreateHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

        ApplyDefaultHeaders(client);
        return client;
    }

    /// <summary>创建统一的 HTTP 处理器（连接池、代理、自动解压）。</summary>
    /// <returns>配置好的 <see cref="SocketsHttpHandler"/>。</returns>
    private SocketsHttpHandler CreateHandler()
    {
        SocketsHttpHandler handler = new()
        {
            // 不使用 CookieContainer：避免跨请求串号，Cookie 由调用方按请求注入。
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = MaxRedirects,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(StreamingConnectTimeoutSeconds),
            PooledConnectionLifetime = StreamingPooledConnectionLifetime,
        };

        if (!string.IsNullOrWhiteSpace(NetworkOptions.Proxy))
        {
            if (Uri.TryCreate(NetworkOptions.Proxy, UriKind.Absolute, out Uri? proxyUri))
            {
                handler.Proxy = new System.Net.WebProxy(proxyUri, false);
                handler.UseProxy = true;
            }
            else
            {
                _logger.Warn(_moduleName, "代理地址非法，已忽略。", new Dictionary<string, object?>
                {
                    ["proxy"] = NetworkOptions.Proxy,
                });
            }
        }
        else
        {
            handler.UseProxy = false;
        }

        return handler;
    }

    private void ApplyDefaultHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        client.DefaultRequestHeaders.ConnectionClose = false;
    }
}
