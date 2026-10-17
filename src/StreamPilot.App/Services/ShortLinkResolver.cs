using System.Net.Http;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;

namespace StreamPilot.App.Services;

/// <summary>
/// 把平台分享短链（<c>v.douyin.com</c>、<c>b23.tv</c>）跟随跳转解析成真实直播间地址。
/// </summary>
/// <remarks>
/// 解析器只接受"以域名开头的完整房间地址"，而分享短链里没有房间号，只有一串随机 id；
/// 直接把短链域名加进白名单会把"域名不属于 X"换成"房间不存在"，比现状更糟。
/// 因此这里用一次有界的 GET（跟随最多 <see cref="MaxRedirects"/> 次跳转）拿到最终地址，
/// 再把最终地址交给解析器。任何失败都原样返回输入，由解析器给出明确失败分类，绝不抛给界面。
/// </remarks>
public sealed class ShortLinkResolver : IDisposable
{
    /// <summary>抖音分享短链主机名。</summary>
    private const string DouyinShortHost = "v.douyin.com";

    /// <summary>哔哩哔哩分享短链主机名。</summary>
    private const string BilibiliShortHost = "b23.tv";

    /// <summary>可识别的短链主机名（精确匹配，不做后缀匹配）。</summary>
    private static readonly string[] ShortHosts = [DouyinShortHost, BilibiliShortHost];

    /// <summary>允许跟随的最大跳转次数。</summary>
    private const int MaxRedirects = 5;

    /// <summary>解析短链的整体超时（秒）：分享跳转通常 1 秒内完成，超过说明网络异常。</summary>
    private const int TimeoutSeconds = 10;

    private readonly HttpClient _client;
    private readonly IStructuredLogger? _logger;

    /// <summary>模块名（日志字段）。</summary>
    private readonly string _moduleName = "App.ShortLink";

    /// <summary>用默认处理器创建解析器。</summary>
    public ShortLinkResolver()
        : this(null, null)
    {
    }

    /// <summary>用指定的日志器创建解析器（使用跟随跳转的默认处理器）。</summary>
    /// <param name="logger">结构化日志器，可为 <see langword="null"/>。</param>
    public ShortLinkResolver(IStructuredLogger? logger)
        : this(logger, null)
    {
    }

    /// <summary>用指定的日志器与可注入的 HTTP 处理器创建解析器（后者供离线用例使用）。</summary>
    /// <param name="logger">结构化日志器，可为 <see langword="null"/>。</param>
    /// <param name="handler">HTTP 处理器；为 <see langword="null"/> 时使用跟随跳转的长连接处理器。</param>
    public ShortLinkResolver(IStructuredLogger? logger, HttpMessageHandler? handler)
    {
        _logger = logger;
        _client = new HttpClient(handler ?? CreateDefaultHandler())
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };
    }

    /// <summary>判断输入是否是受支持的平台分享短链。</summary>
    /// <param name="input">已规范化的输入。</param>
    /// <returns>是短链时返回 <see langword="true"/>。</returns>
    public static bool IsShortLink(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)
            || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        string host = uri.Host.ToLowerInvariant();
        return ShortHosts.Contains(host, StringComparer.Ordinal);
    }

    /// <summary>跟随短链跳转，返回最终地址；非短链或解析失败时原样返回输入。</summary>
    /// <param name="input">已规范化的输入。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>最终绝对地址或原输入。</returns>
    public async Task<string> ResolveAsync(string input, CancellationToken cancellationToken)
    {
        if (!IsShortLink(input))
        {
            return input;
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, input.Trim());
            request.Headers.TryAddWithoutValidation("User-Agent", HttpClientFactory.DefaultUserAgent);
            using HttpResponseMessage response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            Uri? finalUri = response.RequestMessage?.RequestUri;
            string finalUrl = finalUri?.AbsoluteUri ?? string.Empty;
            if (finalUrl.Length == 0 || string.Equals(finalUrl, input.Trim(), StringComparison.Ordinal))
            {
                LogWarn("短链跳转未产生新地址，按原输入继续。", input);
                return input;
            }

            _logger?.Info(_moduleName, "短链已解析。", BuildFields(input, finalUrl));
            return finalUrl;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // 短链跳转是"锦上添花"：失败不阻断解析，原文继续交给解析器给出明确失败分类。
            LogWarn("短链跳转失败，按原输入继续。", input, error);
            return input;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    /// <summary>创建跟随跳转、不保存 Cookie 的默认处理器。</summary>
    /// <returns>HTTP 处理器。</returns>
    private static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = MaxRedirects,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(TimeoutSeconds),
    };

    /// <summary>记录短链解析失败（只记主机名与错误类型，不记完整签名地址）。</summary>
    /// <param name="message">日志文案。</param>
    /// <param name="input">原始输入。</param>
    /// <param name="error">异常，可为 <see langword="null"/>。</param>
    private void LogWarn(string message, string input, Exception? error = null)
    {
        string host = Uri.TryCreate(input.Trim(), UriKind.Absolute, out Uri? uri) ? uri.Host : "(invalid)";
        _logger?.Info(_moduleName, message, new Dictionary<string, object?>
        {
            ["host"] = host,
            ["errorType"] = error?.GetType().Name,
        });
    }

    /// <summary>构造成功日志字段。</summary>
    /// <param name="input">原始短链。</param>
    /// <param name="finalUrl">最终地址。</param>
    /// <returns>日志字段。</returns>
    private static Dictionary<string, object?> BuildFields(string input, string finalUrl) => new()
    {
        ["inputHost"] = Uri.TryCreate(input.Trim(), UriKind.Absolute, out Uri? uri) ? uri.Host : "(invalid)",
        ["finalHost"] = Uri.TryCreate(finalUrl, UriKind.Absolute, out Uri? final) ? final.Host : "(invalid)",
    };
}
