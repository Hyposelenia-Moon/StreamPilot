namespace StreamPilot.Parsers.Douyin;

using System.Net.Http;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;

/// <summary>
/// 抖音 Web 会话 cookie（<c>ttwid</c>）的获取与短时缓存。
/// </summary>
/// <remarks>
/// 实测（房间 745964462470）：<c>live.douyin.com/webcast/room/web/enter/</c> 在
/// 不带 <c>ttwid</c> 时返回 <b>HTTP 200 且响应体为空</b>；带上由
/// <c>https://live.douyin.com/</c> 首页 <c>Set-Cookie</c> 下发的真实 <c>ttwid</c> 后才返回 JSON，
/// 而伪造的 <c>ttwid</c> 同样只得到空响应体。
/// 因此这里做一次普通首页 GET 并把服务器下发的会话 cookie 复用给解析请求：
/// 不实现、不伪造任何平台签名（<c>a_bogus</c>/<c>ms_token</c>/<c>__ac_signature</c> 一律不涉及），
/// 也不处理验证码或风控中间页。cookie 值只用于请求头，永不写入日志。
/// </remarks>
internal sealed class DouyinWebSession
{
    /// <summary>会话 cookie 名称。</summary>
    internal const string CookieName = "ttwid";

    /// <summary>获取会话 cookie 的页面地址。</summary>
    private const string SessionUrl = "https://live.douyin.com/";

    /// <summary>请求超时（秒）。</summary>
    private const int RequestTimeoutSeconds = 8;

    /// <summary>最多尝试次数（含首次）。</summary>
    private const int MaxAttempts = 2;

    /// <summary>会话 cookie 的内存缓存时长。</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

    /// <summary><c>Set-Cookie</c> 响应头名称。</summary>
    private const string SetCookieHeaderName = "Set-Cookie";

    /// <summary>cookie 名与值之间的分隔符。</summary>
    private const char CookiePairSeparator = '=';

    /// <summary>cookie 属性之间的分隔符。</summary>
    private const char CookieAttributeSeparator = ';';

    private readonly HttpClientFactory _factory;
    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Parsers.Douyin";
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _ttwid;
    private DateTimeOffset _acquiredAt;

    /// <summary>初始化会话管理器。</summary>
    /// <param name="factory">HTTP 客户端工厂（复用统一的超时与代理配置）。</param>
    /// <param name="logger">结构化日志。</param>
    public DouyinWebSession(HttpClientFactory factory, IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// 取得当前可用的 <c>ttwid</c>；不可得时返回 <see langword="null"/>（由调用方降级）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>cookie 值；获取失败时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 结果在内存中缓存 <see cref="CacheLifetime"/>，避免每次解析都多打一次首页请求。
    /// 该方法是线程安全的：并发调用只会有一个请求真正发出。
    /// </remarks>
    public async Task<string?> TryGetTtwidAsync(CancellationToken cancellationToken)
    {
        if (IsCacheValid(DateTimeOffset.UtcNow))
        {
            return _ttwid;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsCacheValid(DateTimeOffset.UtcNow))
            {
                return _ttwid;
            }

            string? acquired = await FetchTtwidAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(acquired))
            {
                _ttwid = acquired;
                _acquiredAt = DateTimeOffset.UtcNow;
            }

            return _ttwid;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>判断缓存的会话 cookie 是否仍然有效。</summary>
    /// <param name="now">当前时间。</param>
    /// <returns>有效返回 <see langword="true"/>。</returns>
    private bool IsCacheValid(DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(_ttwid) && now - _acquiredAt < CacheLifetime;

    /// <summary>请求首页并从中取出 <c>ttwid</c>；失败只记日志并返回 <see langword="null"/>。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>cookie 值；未取到时返回 <see langword="null"/>。</returns>
    private async Task<string?> FetchTtwidAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, SessionUrl);
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                HttpClient client = _factory.GetClient(RequestTimeoutSeconds);
                using HttpResponseMessage response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn(_moduleName, "抖音首页返回非成功状态码，无法取得会话 cookie。", new Dictionary<string, object?>
                    {
                        ["attempt"] = attempt,
                        ["status"] = (int)response.StatusCode,
                    });
                    continue;
                }

                string? value = ExtractCookieValue(response, CookieName);
                if (string.IsNullOrWhiteSpace(value))
                {
                    _logger.Warn(_moduleName, "抖音首页未下发会话 cookie。", new Dictionary<string, object?>
                    {
                        ["attempt"] = attempt,
                        ["cookieName"] = CookieName,
                    });
                    continue;
                }

                // 只记录长度与指纹级别信息，cookie 原文永不写入日志。
                _logger.Info(_moduleName, "已取得抖音会话 cookie（用于解析请求，不写入日志）。", new Dictionary<string, object?>
                {
                    ["cookieName"] = CookieName,
                    ["valueLength"] = value.Length,
                });
                return value;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                _logger.LogError(LogLevel.Warn, _moduleName, "请求抖音首页失败，无法取得会话 cookie。", exception, new Dictionary<string, object?>
                {
                    ["attempt"] = attempt,
                    ["url"] = SessionUrl,
                });
            }
        }

        return null;
    }

    /// <summary>从响应的 <c>Set-Cookie</c> 头里取指定 cookie 的值（只取名值对，丢弃属性）。</summary>
    /// <param name="response">HTTP 响应。</param>
    /// <param name="cookieName">cookie 名称。</param>
    /// <returns>cookie 值；不存在时返回 <see langword="null"/>。</returns>
    private static string? ExtractCookieValue(HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues(SetCookieHeaderName, out IEnumerable<string>? headers))
        {
            return null;
        }

        foreach (string header in headers)
        {
            string pair = header.Split(CookieAttributeSeparator)[0].Trim();
            int separatorIndex = pair.IndexOf(CookiePairSeparator);
            if (separatorIndex <= 0)
            {
                continue;
            }

            if (pair[..separatorIndex].Trim().Equals(cookieName, StringComparison.OrdinalIgnoreCase))
            {
                return pair[(separatorIndex + 1)..].Trim();
            }
        }

        return null;
    }
}
