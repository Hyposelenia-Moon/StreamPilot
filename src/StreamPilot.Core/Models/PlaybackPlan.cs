namespace StreamPilot.Core.Models;

/// <summary>
/// 交给 Web 播放页的候选（与 <c>player.html</c> 的候选字段一一对应）。
/// </summary>
/// <remarks>
/// 字段名与 Web 播放消息契约保持一致，序列化时使用 camelCase（见 docs/architecture/player-message-contract.md）。
/// </remarks>
public sealed record WebPlayerCandidate
{
    /// <summary>源索引。</summary>
    public required int SourceIndex { get; init; }

    /// <summary>可直接被浏览器读取的流地址（必要时为本地中继地址）。</summary>
    public required string Url { get; init; }

    /// <summary>格式字符串：<c>flv</c>、<c>ts</c>、<c>hls</c>、<c>fmp4</c>。</summary>
    public required string Format { get; init; }

    /// <summary>编码字符串：<c>avc</c>、<c>hevc</c>、<c>av1</c>、<c>unknown</c>。</summary>
    public required string Codec { get; init; }

    /// <summary>CDN 主机名，用于展示与诊断。</summary>
    public required string Host { get; init; }

    /// <summary>用于诊断的 URL 指纹（页面会原样回传）。</summary>
    public required string UrlFingerprint { get; init; }

    /// <summary>播放该候选时需要携带的 Referer（页面用于 mpv 外挂播放），可为 <see langword="null"/>。</summary>
    public string? Referer { get; init; }

    /// <summary>可选的展示标签（例如画质名）。</summary>
    public string? Label { get; init; }
}

/// <summary>
/// 播放计划：宿主准备好的候选列表与模式参数。
/// </summary>
public sealed record PlaybackPlan
{
    /// <summary>播放会话标识，用于丢弃过期消息。</summary>
    public required int SessionId { get; init; }

    /// <summary>房间信息。</summary>
    public required ResolvedRoom Room { get; init; }

    /// <summary>播放模式字符串：<c>extreme</c> 或 <c>stable</c>。</summary>
    public required string Mode { get; init; }

    /// <summary>极限追帧目标延迟（毫秒）。</summary>
    public required int ExtremeTargetMs { get; init; }

    /// <summary>候选列表。</summary>
    public required IReadOnlyList<WebPlayerCandidate> Candidates { get; init; }

    /// <summary>是否存在因编码不支持而被排除的候选（UI 据此提示改用 mpv）。</summary>
    public bool HasUnsupportedCodec { get; init; }

    /// <summary>提示信息，可能为 <see langword="null"/>。</summary>
    public string? Hint { get; init; }

    /// <summary>当前房间可选的画质档位（播放页据此渲染下拉框）。</summary>
    public IReadOnlyList<QualityOption> Qualities { get; init; } = [];

    /// <summary>当前候选使用的画质档位键，可为 <see langword="null"/>（平台单档）。</summary>
    public string? SelectedQualityKey { get; init; }
}
