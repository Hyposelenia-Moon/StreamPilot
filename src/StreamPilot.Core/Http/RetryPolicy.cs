namespace StreamPilot.Core.Http;

using StreamPilot.Core.Models;

/// <summary>
/// HTTP 重试策略（纯函数，便于单元测试）。
/// </summary>
/// <remarks>
/// 规则（见 docs/adr/0003-parser-contract.md 决策 3）：
/// 只对"连接失败 / 超时 / 409 / 425 / 429 / 5xx"重试，最多 3 次尝试（含首次），
/// 退避序列为 300ms、900ms、2000ms，并叠加 ±20% 抖动以打散并发重试。
/// </remarks>
public static class RetryPolicy
{
    /// <summary>退避基数（毫秒）。</summary>
    private static readonly int[] BackoffMilliseconds = [300, 900, 2000];

    /// <summary>抖动比例（±20%）。</summary>
    private const double JitterRatio = 0.2;

    /// <summary>最大尝试次数（含首次）。</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>判断指定 HTTP 状态码是否值得重试。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <returns>值得重试返回 <see langword="true"/>。</returns>
    public static bool IsRetryableStatusCode(int statusCode) =>
        statusCode is 408 or 409 or 425 or 429 || statusCode >= 500;

    /// <summary>判断指定异常是否值得重试。</summary>
    /// <param name="exception">异常。</param>
    /// <returns>值得重试返回 <see langword="true"/>。</returns>
    public static bool IsRetryableException(Exception? exception) => exception switch
    {
        null => false,
        TaskCanceledException => true,
        TimeoutException => true,
        System.Net.Http.HttpRequestException => true,
        IOException => true,
        _ => false,
    };

    /// <summary>
    /// 计算第 <paramref name="attempt"/> 次失败后的等待时间。
    /// </summary>
    /// <param name="attempt">已完成尝试次数，从 1 开始。</param>
    /// <param name="jitterSource">抖动来源（0.0-1.0），默认为随机数；测试时传入固定值。</param>
    /// <returns>等待时长；超过上限时返回 <see cref="TimeSpan.Zero"/>。</returns>
    public static TimeSpan GetDelay(int attempt, double? jitterSource = null)
    {
        if (attempt < 1 || attempt > BackoffMilliseconds.Length)
        {
            return TimeSpan.Zero;
        }

        int baseDelay = BackoffMilliseconds[attempt - 1];
        double jitter = jitterSource ?? Random.Shared.NextDouble();
        double factor = 1.0 + ((jitter * 2.0) - 1.0) * JitterRatio;
        int milliseconds = (int)Math.Round(baseDelay * factor, MidpointRounding.AwayFromZero);
        return TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
    }

    /// <summary>判断在已完成 <paramref name="attempt"/> 次尝试后是否还能继续重试。</summary>
    /// <param name="attempt">已完成尝试次数。</param>
    /// <param name="maxAttempts">最大尝试次数。</param>
    /// <returns>可以继续返回 <see langword="true"/>。</returns>
    public static bool CanRetry(int attempt, int maxAttempts) => attempt < Math.Max(1, maxAttempts);

    /// <summary>把 HTTP 状态码映射为解析失败分类。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <returns>失败分类。</returns>
    public static ResolveFailure ToFailure(int statusCode) => statusCode switch
    {
        401 or 403 => ResolveFailure.Rejected,
        404 or 410 => ResolveFailure.RoomNotFound,
        429 => ResolveFailure.Rejected,
        >= 500 => ResolveFailure.NetworkError,
        _ => ResolveFailure.ParseError,
    };
}
