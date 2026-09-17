namespace StreamPilot.Core.Models;

/// <summary>
/// 播放请求：由 UI 发起，交给 <c>IPlaybackCoordinator</c> 准备候选。
/// </summary>
public sealed record PlaybackRequest
{
    /// <summary>极限追帧的三档目标延迟（毫秒）。</summary>
    public static readonly int[] ExtremeTargetsMs = [150, 200, 250];

    /// <summary>默认目标延迟（毫秒）。</summary>
    public const int DefaultExtremeTargetMs = 250;

    /// <summary>房间信息（已解析）。</summary>
    public required ResolvedRoom Room { get; init; }

    /// <summary>播放模式。</summary>
    public PlaybackMode Mode { get; init; } = PlaybackMode.Extreme;

    /// <summary>极限追帧目标延迟（毫秒）；仅允许 150/200/250，其他值回落到默认值。</summary>
    public int ExtremeTargetMs { get; init; } = DefaultExtremeTargetMs;

    /// <summary>是否允许桥接中继（需要 Referer 或跨域受限的流）。</summary>
    public bool AllowRelay { get; init; } = true;

    /// <summary>把 <see cref="ExtremeTargetMs"/> 归一化为合法档位。</summary>
    /// <returns>150、200 或 250。</returns>
    public int NormalizeExtremeTargetMs()
    {
        foreach (int candidate in ExtremeTargetsMs)
        {
            if (candidate == ExtremeTargetMs)
            {
                return candidate;
            }
        }

        return DefaultExtremeTargetMs;
    }
}
