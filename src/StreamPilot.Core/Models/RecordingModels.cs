namespace StreamPilot.Core.Models;

/// <summary>
/// 录制请求。
/// </summary>
public sealed record RecordingRequest
{
    /// <summary>房间信息（已解析）。</summary>
    public required ResolvedRoom Room { get; init; }

    /// <summary>优先使用的候选；为 <see langword="null"/> 时自动选择第一个可录制候选。</summary>
    public StreamCandidate? Candidate { get; init; }

    /// <summary>输出根目录；为 <see langword="null"/> 时使用配置中的默认目录。</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>分片策略；为 <see langword="null"/> 时使用默认策略。</summary>
    public SegmentPolicyOptions? SegmentPolicy { get; init; }

    /// <summary>最长录制时长（分钟）；为 <see langword="null"/> 时使用默认值。</summary>
    public int? MaxDurationMinutes { get; init; }
}

/// <summary>
/// 单个录制分片的元数据。
/// </summary>
public sealed record RecordingSegment
{
    /// <summary>分片序号，从 0 开始。</summary>
    public required int Index { get; init; }

    /// <summary>分片文件名（不含目录）。</summary>
    public required string FileName { get; init; }

    /// <summary>分片字节数。</summary>
    public required long Bytes { get; init; }

    /// <summary>分片时长（秒，按时间戳差值计算）。</summary>
    public required double DurationSeconds { get; init; }
}

/// <summary>
/// 录制会话状态快照，用于 UI 展示。
/// </summary>
public sealed record RecordingStatus
{
    /// <summary>平台标识。</summary>
    public required PlatformId Platform { get; init; }

    /// <summary>房间号。</summary>
    public required string RoomId { get; init; }

    /// <summary>主播名。</summary>
    public required string Anchor { get; init; }

    /// <summary>是否仍在录制。</summary>
    public required bool IsRecording { get; init; }

    /// <summary>已写入的总字节数。</summary>
    public required long TotalBytes { get; init; }

    /// <summary>已录制时长。</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>当前分片序号。</summary>
    public required int CurrentSegmentIndex { get; init; }

    /// <summary>重连次数。</summary>
    public required int ReconnectCount { get; init; }

    /// <summary>当前分片文件名（不含目录）。</summary>
    public required string CurrentFileName { get; init; }
}
