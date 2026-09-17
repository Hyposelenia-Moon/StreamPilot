namespace StreamPilot.Core.Services;

using StreamPilot.Core.Models;

/// <summary>
/// 候选流有效期校验。
/// </summary>
public interface ICandidateValidity
{
    /// <summary>
    /// 校验候选是否仍可使用；已过期时抛出异常。
    /// </summary>
    /// <param name="candidate">候选。</param>
    /// <param name="now">当前时间。</param>
    /// <exception cref="Errors.ResolveException">候选已过期或地址非法时抛出。</exception>
    void EnsureUsable(StreamCandidate candidate, DateTimeOffset now);

    /// <summary>判断候选是否已过期。</summary>
    /// <param name="candidate">候选。</param>
    /// <param name="now">当前时间。</param>
    /// <returns>已过期返回 <see langword="true"/>。</returns>
    bool IsExpired(StreamCandidate candidate, DateTimeOffset now);
}
