namespace StreamPilot.Core.Models;

/// <summary>
/// 播放模式：是否启用"极限追帧"低延迟档位。
/// </summary>
public enum PlaybackMode
{
    /// <summary>稳定模式：允许较大缓冲，抗抖动优先。</summary>
    Stable = 0,

    /// <summary>极限追帧模式：以 <see cref="PlaybackRequest.ExtremeTargetMs"/> 为目标延迟。</summary>
    Extreme = 1,
}

/// <summary>
/// 录制会话的停止原因。
/// </summary>
public enum RecordingStopReason
{
    /// <summary>尚未停止。</summary>
    None = 0,

    /// <summary>用户主动停止。</summary>
    UserStopped = 1,

    /// <summary>主播下播，流自然结束。</summary>
    StreamEnded = 2,

    /// <summary>重连次数达到上限。</summary>
    ReconnectLimitReached = 3,

    /// <summary>达到最长录制时长上限。</summary>
    DurationLimitReached = 4,

    /// <summary>发生不可恢复的错误。</summary>
    Failed = 5,
}
