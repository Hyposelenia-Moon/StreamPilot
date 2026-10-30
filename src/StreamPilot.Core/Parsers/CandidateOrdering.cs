namespace StreamPilot.Core.Parsers;

using StreamPilot.Core.Models;

/// <summary>
/// 候选优先级排序：决定"截断时先保留谁"。
/// </summary>
/// <remarks>
/// <para>
/// 平台返回候选的顺序是"接口遍历顺序"（协议 → 容器 → 编码 → CDN），既不代表画质，
/// 也不保证同一档位下先给兼容性最好的编码。播放编排按固定上限截断前 N 条时，
/// 直接沿用接口顺序会让结果取决于平台实现细节：实测 B站同一房间返回 12 条
/// （AVC/HEVC 交错、多条 CDN），截到 8 条后可能一条 HEVC 都不剩，
/// HDR 房间因此永远播不到 HDR 码流。
/// </para>
/// <para>
/// 排序规则（每级都是显式权重，不依赖枚举数值）：
/// <list type="number">
///   <item><b>画质降序</b>：HDR/杜比 高于同分辨率 SDR，4K 高于 2K；</item>
///   <item><b>容器</b>：FLV 优先（走字节中继，延迟最低、无需改写播放列表），
///     其次 HLS-TS、HLS-fMP4，最后 RTMP（Web 端不可播，仅排给 mpv/录制）；</item>
///   <item><b>编码</b>：同画质同容器内 AVC 优先于 HEVC——WebView2 未装 HEVC 扩展时
///     HEVC 只能交给 mpv，把 AVC 排在前面可以避免"默认路径直接不可播"；</item>
///   <item><b>源索引</b>：以上都相同时保留平台顺序，保证结果可复现。</item>
/// </list>
/// </para>
/// <para>
/// 注意这只是"优先顺序"，不是"过滤"：HEVC 只要画质更高（HDR 房间就是这种情况）依然排在最前，
/// 截断也不会把它挤掉。
/// </para>
/// </remarks>
public static class CandidateOrdering
{
    /// <summary>
    /// 按优先级返回候选（不修改入参）。
    /// </summary>
    /// <param name="candidates">平台给出的候选。</param>
    /// <returns>排好序的新列表。</returns>
    public static IReadOnlyList<StreamCandidate> Prioritize(IEnumerable<StreamCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return
        [
            .. candidates
                .OrderByDescending(static candidate => RankQuality(candidate.Quality))
                .ThenBy(static candidate => RankFormat(candidate.Format))
                .ThenBy(static candidate => RankCodec(candidate.Codec))
                .ThenBy(static candidate => candidate.SourceIndex),
        ];
    }

    /// <summary>画质权重：数值越大越优先。</summary>
    /// <param name="quality">画质档位。</param>
    /// <returns>权重。</returns>
    /// <remarks>
    /// HDR 与"原画真彩"都排在 4K SDR 之前：它们通常就是该房间的最高画质档，
    /// 若排在 4K SDR 之后，截断时会被同房间的 SDR 副本挤掉。
    /// </remarks>
    public static int RankQuality(StreamQuality quality) => quality switch
    {
        StreamQuality.Dolby => 9,
        StreamQuality.Hdr => 8,
        StreamQuality.OriginalTrueColor => 7,
        StreamQuality.Uhd4K => 6,
        StreamQuality.Qhd2K => 5,
        StreamQuality.Hd1080HighFps => 4,
        StreamQuality.Hd1080 => 3,
        StreamQuality.Hd720 => 2,
        StreamQuality.Sd480 => 1,
        _ => 0,
    };

    /// <summary>容器权重：数值越小越优先。</summary>
    /// <param name="format">容器格式。</param>
    /// <returns>权重。</returns>
    public static int RankFormat(StreamFormat format) => format switch
    {
        StreamFormat.FlvHttp => 0,
        StreamFormat.HlsTs => 1,
        StreamFormat.HlsFmp4 => 2,
        StreamFormat.Rtmp => 3,
        _ => 4,
    };

    /// <summary>编码权重：数值越小越优先。</summary>
    /// <param name="codec">视频编码。</param>
    /// <returns>权重。</returns>
    public static int RankCodec(VideoCodec codec) => codec switch
    {
        VideoCodec.Avc => 0,
        VideoCodec.Hevc => 1,
        VideoCodec.Av1 => 2,
        _ => 3,
    };

    /// <summary>
    /// 为 mpv 外挂播放排序候选（与 <see cref="Prioritize"/> 的 Web 播放规则不同）。
    /// </summary>
    /// <param name="candidates">平台给出的候选。</param>
    /// <returns>按 mpv 播放优先级排好序的新列表。</returns>
    /// <remarks>
    /// mpv 直连 CDN、用内置 ffmpeg demux，因此格式优先级与 Web 播放相反：
    /// B站 HEVC 的 FLV 是 Enhanced FLV 封装，mpv/ffmpeg 对其 demux 会读错 NAL 大小
    /// （<c>Invalid NAL unit size</c>），表现为"mpv 收不到数据/画面出不来"；
    /// 同画质的 HLS(ts) 封装则完全正常，所以 HEVC 优先 HLS(ts)。
    /// AVC 的 FLV 是标准 FLV，保持 FLV（延迟最低）。
    /// </remarks>
    public static IReadOnlyList<StreamCandidate> PrioritizeForMpv(IEnumerable<StreamCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return
        [
            .. candidates
                .OrderByDescending(static candidate => RankQuality(candidate.Quality))
                .ThenBy(static candidate => RankMpvFormat(candidate))
                .ThenBy(static candidate => candidate.SourceIndex),
        ];
    }

    /// <summary>mpv 播放的格式权重（数值越小越优先）。</summary>
    /// <param name="candidate">候选。</param>
    /// <returns>权重。</returns>
    public static int RankMpvFormat(StreamCandidate candidate) => (candidate.Codec, candidate.Format) switch
    {
        (VideoCodec.Hevc, StreamFormat.HlsTs) => 0,   // HEVC 走 HLS：mpv 能正确 demux（Enhanced FLV 有 bug）
        (VideoCodec.Hevc, StreamFormat.FlvHttp) => 2, // Enhanced FLV 的 HEVC：mpv 会报 Invalid NAL unit size，最后才选
        (_, StreamFormat.FlvHttp) => 1,               // AVC 的 FLV：标准 FLV，延迟最低
        _ => 3,
    };
}
