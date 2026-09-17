namespace StreamPilot.Core.Parsers;

using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;

/// <summary>
/// 候选流构造器：把平台原始数据归一化为 <see cref="StreamCandidate"/>。
/// </summary>
/// <remarks>
/// 集中处理 URL 校验、指纹计算、CDN host 提取与源索引分配，
/// 避免各平台解析器重复实现（也保证"有效期/去重/指纹"等规则只有一处）。
/// </remarks>
public sealed class StreamCandidateBuilder
{
    private readonly List<StreamCandidate> _candidates = [];
    private readonly IStructuredLogger? _logger;
    private readonly PlatformId _platform;

    /// <summary>初始化构造器。</summary>
    /// <param name="platform">平台标识。</param>
    /// <param name="logger">结构化日志，可为 <see langword="null"/>。</param>
    public StreamCandidateBuilder(PlatformId platform, IStructuredLogger? logger = null)
    {
        _platform = platform;
        _logger = logger;
    }

    /// <summary>已成功加入的候选数量。</summary>
    public int Count => _candidates.Count;

    /// <summary>
    /// 尝试加入一个候选；URL 非法或为空时丢弃并记录 Warn（不抛异常）。
    /// </summary>
    /// <param name="url">流地址。</param>
    /// <param name="format">容器格式。</param>
    /// <param name="codec">视频编码。</param>
    /// <param name="quality">画质档位。</param>
    /// <param name="expiresAt">有效期（UTC），可为 <see langword="null"/>。</param>
    /// <param name="referer">播放时需要携带的 Referer，可为 <see langword="null"/>。</param>
    /// <param name="cdnHost">CDN 主机名；为 <see langword="null"/> 时从 URL 推导。</param>
    /// <returns>加入成功返回 <see langword="true"/>。</returns>
    public bool TryAdd(
        string? url,
        StreamFormat format,
        VideoCodec codec,
        StreamQuality quality,
        DateTimeOffset? expiresAt = null,
        string? referer = null,
        string? cdnHost = null)
    {
        StreamCandidate? candidate = StreamCandidate.TryCreate(
            _platform,
            _candidates.Count,
            url,
            format,
            codec,
            quality,
            expiresAt,
            referer,
            cdnHost,
            _logger);

        if (candidate is null)
        {
            return false;
        }

        _candidates.Add(candidate);
        return true;
    }

    /// <summary>返回按加入顺序排列的候选列表（源索引与下标一致）。</summary>
    /// <returns>候选列表的只读副本。</returns>
    public IReadOnlyList<StreamCandidate> Build() => _candidates.ToArray();

    /// <summary>清空已加入的候选。</summary>
    public void Clear() => _candidates.Clear();
}
