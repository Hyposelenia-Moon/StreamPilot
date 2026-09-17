namespace StreamPilot.Core.Models;

using StreamPilot.Core.Logging;

/// <summary>
/// 平台解析产出的单个可播放/可录制流候选。
/// </summary>
/// <remarks>
/// 该类是解析层与播放/录制层之间的唯一契约（见 docs/adr/0003-parser-contract.md）。
/// <see cref="Url"/> 含平台签名与过期参数，禁止写入日志；日志一律使用 <see cref="UrlFingerprint"/>。
/// </remarks>
public sealed record StreamCandidate
{
    /// <summary>未知 CDN 主机时使用的占位值。</summary>
    public const string UnknownHost = "unknown";

    /// <summary>源索引：同一房间内候选的唯一序号，0 表示平台给出的最高优先级流。</summary>
    public required int SourceIndex { get; init; }

    /// <summary>完整流地址（可能带签名与过期参数，禁止写入日志）。</summary>
    public required string Url { get; init; }

    /// <summary>容器/传输格式。</summary>
    public required StreamFormat Format { get; init; }

    /// <summary>CDN 主机名；无法判定时为 <see cref="UnknownHost"/>。</summary>
    public required string CdnHost { get; init; }

    /// <summary>视频编码。</summary>
    public required VideoCodec Codec { get; init; }

    /// <summary>画质档位。</summary>
    public required StreamQuality Quality { get; init; }

    /// <summary>URL 指纹（长度 + 前缀哈希），仅用于日志与去重，不泄露签名。</summary>
    public required string UrlFingerprint { get; init; }

    /// <summary>有效期（UTC）；<see langword="null"/> 表示平台未声明。</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>该候选在播放时是否需要在请求中携带 Referer（例如 B站/抖音）。</summary>
    public string? HttpReferer { get; init; }

    /// <summary>
    /// 解析平台原始 URL，构造一个候选（统一入口，避免各解析器重复处理指纹与 CDN host）。
    /// </summary>
    /// <param name="platform">平台标识（仅用于日志）。</param>
    /// <param name="sourceIndex">源索引。</param>
    /// <param name="url">流地址。</param>
    /// <param name="format">容器格式。</param>
    /// <param name="codec">视频编码。</param>
    /// <param name="quality">画质档位。</param>
    /// <param name="expiresAt">有效期，可为 <see langword="null"/>。</param>
    /// <param name="referer">播放时需要携带的 Referer，可为 <see langword="null"/>。</param>
    /// <param name="cdnHost">CDN 主机名；为 <see langword="null"/> 时从 URL 推导。</param>
    /// <param name="logger">结构化日志，可为 <see langword="null"/>。</param>
    /// <returns>候选；URL 非法或为空时返回 <see langword="null"/>。</returns>
    public static StreamCandidate? TryCreate(
        PlatformId platform,
        int sourceIndex,
        string? url,
        StreamFormat format,
        VideoCodec codec,
        StreamQuality quality,
        DateTimeOffset? expiresAt = null,
        string? referer = null,
        string? cdnHost = null,
        IStructuredLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            logger?.Warn("Core.StreamCandidate", "丢弃非法候选地址。", new Dictionary<string, object?>
            {
                ["platform"] = platform.ToString(),
                ["format"] = format.ToString(),
                ["fingerprint"] = SensitiveData.Fingerprint(url),
            });
            return null;
        }

        string host = string.IsNullOrWhiteSpace(cdnHost) ? uri.Host : cdnHost;
        return new StreamCandidate
        {
            SourceIndex = sourceIndex,
            Url = url,
            Format = format,
            CdnHost = string.IsNullOrWhiteSpace(host) ? UnknownHost : host,
            Codec = codec,
            Quality = quality,
            UrlFingerprint = SensitiveData.Fingerprint(url),
            ExpiresAt = expiresAt,
            HttpReferer = referer,
        };
    }

    /// <summary>判断该候选是否可用于 Web 端（WebView2）播放。</summary>
    /// <returns>可播放返回 <see langword="true"/>。</returns>
    public bool IsWebPlayable() => Format switch
    {
        StreamFormat.FlvHttp => true,
        StreamFormat.HlsTs => true,
        StreamFormat.HlsFmp4 => true,
        _ => false,
    };

    /// <summary>判断该候选是否支持原始流录制。</summary>
    /// <returns>支持返回 <see langword="true"/>。</returns>
    public bool IsRecordable() => Format is StreamFormat.FlvHttp or StreamFormat.HlsTs;
}
