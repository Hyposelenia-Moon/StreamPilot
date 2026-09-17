namespace StreamPilot.Core.Models;

/// <summary>
/// 录制输出元数据（写入 <c>{anchor}-{roomId}-{startTime}.meta.json</c> 侧车文件）。
/// </summary>
/// <remarks>
/// 严禁写入签名 URL 或 Cookie；流地址只以指纹形式记录（见 docs/adr/0004-raw-recording.md）。
/// </remarks>
public sealed record RecordingMetadata
{
    /// <summary>元数据文件格式版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>元数据格式版本。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>平台标识。</summary>
    public required PlatformId Platform { get; init; }

    /// <summary>房间号。</summary>
    public required string RoomId { get; init; }

    /// <summary>主播名。</summary>
    public required string Anchor { get; init; }

    /// <summary>直播标题。</summary>
    public required string Title { get; init; }

    /// <summary>直播分区。</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>开始时间（UTC）。</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>结束时间（UTC）；录制中为 <see langword="null"/>。</summary>
    public DateTimeOffset? EndedAtUtc { get; set; }

    /// <summary>录制时长（秒）。</summary>
    public double DurationSeconds { get; set; }

    /// <summary>容器格式字符串，例如 <c>flv-http</c>。</summary>
    public required string Format { get; init; }

    /// <summary>视频编码字符串，例如 <c>avc</c>。</summary>
    public required string Codec { get; init; }

    /// <summary>已写入总字节数。</summary>
    public long TotalBytes { get; set; }

    /// <summary>重连次数。</summary>
    public int ReconnectCount { get; set; }

    /// <summary>停止原因。</summary>
    public RecordingStopReason StopReason { get; set; } = RecordingStopReason.None;

    /// <summary>候选流指纹（不含签名）。</summary>
    public string CandidateFingerprint { get; init; } = string.Empty;

    /// <summary>分片列表。</summary>
    public List<RecordingSegment> Segments { get; init; } = [];
}
