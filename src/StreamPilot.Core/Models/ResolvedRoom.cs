namespace StreamPilot.Core.Models;

/// <summary>
/// 一次成功解析得到的房间信息与候选流集合。
/// </summary>
public sealed record ResolvedRoom
{
    /// <summary>平台标识。</summary>
    public required PlatformId Platform { get; init; }

    /// <summary>平台返回的最终房间号（可能与用户输入不同，例如短号跳转）。</summary>
    public required string RoomId { get; init; }

    /// <summary>主播名。</summary>
    public required string Anchor { get; init; }

    /// <summary>直播间标题。</summary>
    public required string Title { get; init; }

    /// <summary>直播分区/分类，平台未提供时为空字符串。</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>候选流，按优先级从高到低排列。<see cref="StreamCandidate.SourceIndex"/> 与下标一致。</summary>
    public required IReadOnlyList<StreamCandidate> Candidates { get; init; }

    /// <summary>解析完成时间（UTC）。</summary>
    public required DateTimeOffset ResolvedAt { get; init; }

    /// <summary>平台提供的直播间封面地址，可能为 <see langword="null"/>。</summary>
    public string? CoverUrl { get; init; }

    /// <summary>本房间当前可选的画质档位（按从高到低排列）；平台不区分档位时为空。</summary>
    public IReadOnlyList<QualityOption> Qualities { get; init; } = [];

    /// <summary>本次候选使用的画质档位键；未指定或平台单档时为 <see langword="null"/>。</summary>
    public string? SelectedQualityKey { get; init; }

    /// <summary>
    /// 把候选列表换成另一组候选，并记录新的画质档位（供解析器切换档位时使用）。
    /// </summary>
    /// <param name="candidates">新的候选列表。</param>
    /// <param name="selectedQualityKey">新的档位键。</param>
    /// <returns>替换后的房间对象。</returns>
    public ResolvedRoom WithCandidates(IReadOnlyList<StreamCandidate> candidates, string? selectedQualityKey) =>
        this with
        {
            Candidates = candidates,
            SelectedQualityKey = selectedQualityKey,
            ResolvedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>返回第一个可用于 Web 播放的候选。</summary>
    /// <returns>找到时返回候选，否则返回 <see langword="null"/>。</returns>
    public StreamCandidate? FirstWebPlayableOrDefault()
    {
        foreach (StreamCandidate candidate in Candidates)
        {
            if (candidate.IsWebPlayable() && !string.IsNullOrWhiteSpace(candidate.Url))
            {
                return candidate;
            }
        }

        return null;
    }
}
