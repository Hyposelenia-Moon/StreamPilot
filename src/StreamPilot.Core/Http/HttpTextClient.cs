namespace StreamPilot.Core.Http;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using StreamPilot.Core.Errors;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;

/// <summary>
/// 一次 HTTP 文本/JSON 请求的描述。
/// </summary>
public sealed record HttpRequestSpec
{
    /// <summary>请求地址。</summary>
    public required string Url { get; init; }

    /// <summary>平台标识（用于错误分类）。</summary>
    public required PlatformId Platform { get; init; }

    /// <summary>操作名（用于错误上下文与日志）。</summary>
    public required string Operation { get; init; }

    /// <summary>附加请求头。</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>POST 请求体工厂；为 <see langword="null"/> 时使用 GET。</summary>
    /// <remarks>
    /// 必须是工厂而不是实例：重试时每次都要新建请求体，否则已被释放的
    /// <see cref="HttpContent"/>（例如 <c>FormUrlEncodedContent</c>）无法复用。
    /// </remarks>
    public Func<HttpContent>? ContentFactory { get; init; }

    /// <summary>请求超时（秒）；为 <see langword="null"/> 时使用网络配置值。</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>是否允许非 2xx 状态码（为 <see langword="false"/> 时非 2xx 直接按失败分类抛出）。</summary>
    public bool AllowNonSuccessStatus { get; init; }
}

/// <summary>
/// 一次 HTTP 请求的响应快照。
/// </summary>
/// <param name="Status">HTTP 状态码。</param>
/// <param name="Text">按响应声明的字符集解码后的正文。</param>
/// <param name="Bytes">原始字节。</param>
public readonly record struct HttpTextResponse(HttpStatusCode Status, string Text, byte[] Bytes);

/// <summary>
/// 带超时、有界重试与脱敏日志的 HTTP 客户端。
/// </summary>
public sealed class HttpTextClient
{
    /// <summary>请求头名称：Cookie。</summary>
    private const string CookieHeaderName = "Cookie";

    /// <summary>
    /// 当前异步作用域内附加的 Cookie（由 <see cref="UseCookie"/> 设置）。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="AsyncLocal{T}"/> 而不是实例字段：同一个客户端实例会被多个平台解析器共用，
    /// 并发解析时必须各带各的 Cookie，不能互相串。Cookie 只用于解析请求，
    /// 播放地址与中继请求都不带它（见 README 的隐私说明）。
    /// </remarks>
    private static readonly AsyncLocal<string?> CookieScopeValue = new();

    private readonly HttpClientFactory _factory;
    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Core.Http";

    /// <summary>初始化客户端。</summary>
    /// <param name="factory">HTTP 客户端工厂。</param>
    /// <param name="logger">结构化日志。</param>
    public HttpTextClient(HttpClientFactory factory, IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// 在返回的作用域内，所有请求都带上该 Cookie（空值表示不带）。
    /// </summary>
    /// <param name="cookie">用户自备的该平台 Cookie，可为空。</param>
    /// <returns>释放后恢复原值的作用域。</returns>
    public IDisposable UseCookie(string? cookie)
    {
        string? previous = CookieScopeValue.Value;
        CookieScopeValue.Value = string.IsNullOrWhiteSpace(cookie) ? null : cookie.Trim();
        return new CookieScope(previous);
    }

    /// <summary>发送请求并返回完整响应快照。</summary>
    /// <param name="spec">请求描述。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应快照。</returns>
    /// <exception cref="ResolveException">网络错误、平台拒绝或状态码不可接受时抛出。</exception>
    public async Task<HttpTextResponse> SendAsync(HttpRequestSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        int maxAttempts = Math.Max(1, _factory.NetworkOptions.MaxAttempts);
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpRequestMessage request = BuildRequest(spec);
                HttpClient client = _factory.GetClient(spec.TimeoutSeconds);
                using HttpResponseMessage response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);

                int statusCode = (int)response.StatusCode;
                if (!spec.AllowNonSuccessStatus && !response.IsSuccessStatusCode)
                {
                    if (RetryPolicy.IsRetryableStatusCode(statusCode) && RetryPolicy.CanRetry(attempt, maxAttempts))
                    {
                        await DelayAsync(attempt, spec, statusCode, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new ResolveException(
                        RetryPolicy.ToFailure(statusCode),
                        spec.Platform,
                        spec.Operation,
                        $"平台返回 HTTP {statusCode}。");
                }

                byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                string text = DecodeText(payload, response.Content.Headers.ContentType?.CharSet);
                HttpTextResponse result = new(response.StatusCode, text, payload);
                _logger.Trace(_moduleName, "HTTP 响应完成。", new Dictionary<string, object?>
                {
                    ["operation"] = spec.Operation,
                    ["platform"] = spec.Platform.ToString(),
                    ["status"] = statusCode,
                    ["bytes"] = payload.Length,
                    ["url"] = SensitiveData.RedactUrl(spec.Url),
                });
                return result;
            }
            catch (ResolveException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (RetryPolicy.IsRetryableException(exception))
            {
                lastException = exception;
                if (!RetryPolicy.CanRetry(attempt, maxAttempts))
                {
                    break;
                }

                await DelayAsync(attempt, spec, statusCode: 0, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogError(LogLevel.Warn, _moduleName, "HTTP 请求最终失败。", lastException ?? new TimeoutException(), new Dictionary<string, object?>
        {
            ["operation"] = spec.Operation,
            ["platform"] = spec.Platform.ToString(),
            ["attempts"] = maxAttempts,
            ["url"] = SensitiveData.RedactUrl(spec.Url),
        });

        throw new ResolveException(
            ResolveFailure.NetworkError,
            spec.Platform,
            spec.Operation,
            $"网络请求失败（已尝试 {maxAttempts} 次）：{lastException?.Message ?? "未知原因"}。",
            lastException);
    }

    /// <summary>发送请求并返回文本内容。</summary>
    /// <param name="spec">请求描述。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应文本。</returns>
    /// <exception cref="ResolveException">网络错误、平台拒绝或状态码不可接受时抛出。</exception>
    public async Task<string> GetStringAsync(HttpRequestSpec spec, CancellationToken cancellationToken)
    {
        HttpTextResponse response = await SendAsync(spec, cancellationToken).ConfigureAwait(false);
        return response.Text;
    }

    /// <summary>发送请求并返回原始字节（用于需要自行解码的页面抓取）。</summary>
    /// <param name="spec">请求描述。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应字节。</returns>
    /// <exception cref="ResolveException">网络错误、平台拒绝或状态码不可接受时抛出。</exception>
    public async Task<byte[]> GetBytesAsync(HttpRequestSpec spec, CancellationToken cancellationToken)
    {
        HttpTextResponse response = await SendAsync(spec, cancellationToken).ConfigureAwait(false);
        return response.Bytes;
    }

    /// <summary>发送请求并把响应解析为 JSON 文档。</summary>
    /// <param name="spec">请求描述。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>JSON 文档（调用方负责释放）。</returns>
    /// <exception cref="ResolveException">网络错误、平台拒绝或 JSON 非法时抛出。</exception>
    public async Task<JsonDocument> GetJsonAsync(HttpRequestSpec spec, CancellationToken cancellationToken)
    {
        HttpTextResponse response = await SendAsync(spec, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(response.Text);
        }
        catch (JsonException exception)
        {
            throw new ResolveException(
                ResolveFailure.ParseError,
                spec.Platform,
                spec.Operation,
                $"响应不是合法 JSON（HTTP {(int)response.Status}）。",
                exception);
        }
    }

    private async Task DelayAsync(int attempt, HttpRequestSpec spec, int statusCode, CancellationToken cancellationToken)
    {
        TimeSpan delay = RetryPolicy.GetDelay(attempt);
        _logger.Warn(_moduleName, "HTTP 请求失败，准备重试。", new Dictionary<string, object?>
        {
            ["operation"] = spec.Operation,
            ["platform"] = spec.Platform.ToString(),
            ["attempt"] = attempt,
            ["status"] = statusCode,
            ["delayMs"] = (int)delay.TotalMilliseconds,
            ["url"] = SensitiveData.RedactUrl(spec.Url),
        });

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpRequestMessage BuildRequest(HttpRequestSpec spec)
    {
        bool isPost = spec.ContentFactory is not null;
        HttpRequestMessage request = new(isPost ? HttpMethod.Post : HttpMethod.Get, spec.Url);
        if (isPost)
        {
            request.Content = spec.ContentFactory!();
        }

        if (spec.Headers is null)
        {
            ApplyAmbientCookie(request);
            return request;
        }

        foreach (KeyValuePair<string, string> header in spec.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        ApplyAmbientCookie(request);
        return request;
    }

    /// <summary>在没有显式 Cookie 头时，补上当前作用域的 Cookie。</summary>
    /// <param name="request">待发送的请求。</param>
    private static void ApplyAmbientCookie(HttpRequestMessage request)
    {
        string? cookie = CookieScopeValue.Value;
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return;
        }

        // 解析器显式设置的 Cookie 优先，避免出现两个 Cookie 头。
        bool hasExplicit = request.Headers.Contains(CookieHeaderName)
            || request.Content?.Headers.Contains(CookieHeaderName) == true;
        if (!hasExplicit)
        {
            request.Headers.TryAddWithoutValidation(CookieHeaderName, cookie);
        }
    }

    /// <summary>Cookie 作用域：释放时恢复进入前的值。</summary>
    /// <param name="previous">进入前的 Cookie。</param>
    private sealed class CookieScope(string? previous) : IDisposable
    {
        private bool _disposed;

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CookieScopeValue.Value = previous;
        }
    }

    private static string DecodeText(byte[] payload, string? charset)
    {
        Encoding encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }
        }

        return encoding.GetString(payload);
    }
}
