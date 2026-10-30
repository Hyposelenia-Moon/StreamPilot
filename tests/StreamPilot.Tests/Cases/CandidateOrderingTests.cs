namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Models;
using StreamPilot.Core.Parsers;
using StreamPilot.Tests.Framework;

/// <summary>
/// 候选排序测试：截断前必须按画质/容器/编码排序，而不是沿用平台接口顺序。
/// </summary>
/// <remarks>
/// B站同一房间实测返回 12 条候选（AVC/HEVC 交错、多条 CDN），播放编排只保留 8 条。
/// 若沿用接口顺序，HEVC（HDR 房间的唯一编码）是否被保留完全取决于平台实现细节。
/// </remarks>
[TestClass]
public sealed class CandidateOrderingTests
{
    /// <summary>画质高的候选必须排在前面，与接口顺序无关。</summary>
    [TestMethod("候选排序：画质优先于接口顺序")]
    public void SortsByQualityFirst()
    {
        IReadOnlyList<StreamCandidate> ordered = CandidateOrdering.Prioritize(
        [
            Candidate(0, StreamQuality.Hd720, VideoCodec.Avc, StreamFormat.FlvHttp),
            Candidate(1, StreamQuality.Hd1080, VideoCodec.Avc, StreamFormat.FlvHttp),
            Candidate(2, StreamQuality.Uhd4K, VideoCodec.Hevc, StreamFormat.HlsTs),
        ]);

        Assert.Equal(StreamQuality.Uhd4K, ordered[0].Quality, "4K 必须排在最前");
        Assert.Equal(StreamQuality.Hd1080, ordered[1].Quality);
        Assert.Equal(StreamQuality.Hd720, ordered[2].Quality);
    }

    /// <summary>HDR 是"原画"，必须排在 4K SDR 之前，否则截断时会被同房间的 SDR 副本挤掉。</summary>
    [TestMethod("候选排序：HDR 排在 4K SDR 之前")]
    public void RanksHdrAboveSdr4K()
    {
        Assert.True(
            CandidateOrdering.RankQuality(StreamQuality.Hdr) > CandidateOrdering.RankQuality(StreamQuality.Uhd4K),
            "HDR 是原画档，权重必须高于 4K SDR");
        Assert.True(
            CandidateOrdering.RankQuality(StreamQuality.Dolby) > CandidateOrdering.RankQuality(StreamQuality.Hdr),
            "杜比仍是最高档");
    }

    /// <summary>
    /// B站「原画真彩」（qn=25000）必须被识别，且排在「原画」（qn=10000）之前。
    /// </summary>
    /// <remarks>
    /// 实测房间 27183290：AVC 的 <c>current_qn=25000</c>、HEVC 的 <c>current_qn=10000</c>。
    /// 25000 过去没有映射，落到 <see cref="StreamQuality.Unknown"/>（权重 0），
    /// 于是码率更高、兼容性也更好的 AVC 原画真彩被排到最后，默认反而播了 HEVC 原画。
    /// </remarks>
    [TestMethod("候选排序：原画真彩排在原画之前且不退化为未知")]
    public void RanksTrueColorAboveOriginal()
    {
        StreamQuality trueColor = QualityNames.FromBilibiliQualityNumber(25000);
        Assert.Equal(StreamQuality.OriginalTrueColor, trueColor, "qn=25000 是「原画真彩」，不能落到未知画质");
        Assert.True(
            CandidateOrdering.RankQuality(trueColor) > CandidateOrdering.RankQuality(StreamQuality.Hd1080HighFps),
            "原画真彩必须高于原画档（qn=10000）");

        IReadOnlyList<StreamCandidate> ordered = CandidateOrdering.Prioritize(
        [
            Candidate(0, StreamQuality.Hd1080HighFps, VideoCodec.Hevc, StreamFormat.FlvHttp),
            Candidate(1, StreamQuality.OriginalTrueColor, VideoCodec.Avc, StreamFormat.FlvHttp),
        ]);

        Assert.Equal(VideoCodec.Avc, ordered[0].Codec, "画质更高的 AVC 原画真彩必须排第一");
        Assert.Equal(StreamQuality.OriginalTrueColor, ordered[0].Quality);
    }

    /// <summary>同画质同容器内 AVC 先于 HEVC：没有 HEVC 扩展时默认路径仍可播。</summary>
    [TestMethod("候选排序：同画质同容器 AVC 先于 HEVC")]
    public void PrefersAvcWithinSameQuality()
    {
        IReadOnlyList<StreamCandidate> ordered = CandidateOrdering.Prioritize(
        [
            Candidate(0, StreamQuality.Hd1080, VideoCodec.Hevc, StreamFormat.FlvHttp),
            Candidate(1, StreamQuality.Hd1080, VideoCodec.Avc, StreamFormat.FlvHttp),
        ]);

        Assert.Equal(VideoCodec.Avc, ordered[0].Codec, "同档位下 AVC 兼容性更好，必须排在前面");
        Assert.Equal(VideoCodec.Hevc, ordered[1].Codec, "HEVC 仍需保留在列表中作为后备");
    }

    /// <summary>同画质时 FLV 优先：走字节中继、延迟最低，也不需要改写播放列表。</summary>
    [TestMethod("候选排序：同画质 FLV 先于 HLS")]
    public void PrefersFlvWithinSameQuality()
    {
        IReadOnlyList<StreamCandidate> ordered = CandidateOrdering.Prioritize(
        [
            Candidate(0, StreamQuality.Hd1080, VideoCodec.Avc, StreamFormat.HlsFmp4),
            Candidate(1, StreamQuality.Hd1080, VideoCodec.Avc, StreamFormat.HlsTs),
            Candidate(2, StreamQuality.Hd1080, VideoCodec.Avc, StreamFormat.FlvHttp),
        ]);

        Assert.Equal(StreamFormat.FlvHttp, ordered[0].Format);
        Assert.Equal(StreamFormat.HlsTs, ordered[1].Format);
        Assert.Equal(StreamFormat.HlsFmp4, ordered[2].Format);
    }

    /// <summary>完全同权重时保留平台顺序，结果可复现。</summary>
    [TestMethod("候选排序：同权重保持平台顺序")]
    public void KeepsPlatformOrderForTies()
    {
        IReadOnlyList<StreamCandidate> ordered = CandidateOrdering.Prioritize(
        [
            Candidate(0, StreamQuality.Hd1080, VideoCodec.Avc, StreamFormat.FlvHttp),
            Candidate(1, StreamQuality.Hd1080, VideoCodec.Avc, StreamFormat.FlvHttp),
            Candidate(2, StreamQuality.Hd1080, VideoCodec.Avc, StreamFormat.FlvHttp),
        ]);

        Assert.Equal(0, ordered[0].SourceIndex);
        Assert.Equal(1, ordered[1].SourceIndex);
        Assert.Equal(2, ordered[2].SourceIndex);
    }

    /// <summary>
    /// 模拟 B站 12 条交错候选 + 只保留 8 条：高画质与 HEVC 都不能被接口顺序挤掉。
    /// </summary>
    [TestMethod("候选排序：截断 8 条后仍保留最高画质与 HEVC")]
    public void KeepsHighQualityAndHevcAfterTruncation()
    {
        List<StreamCandidate> candidates = [];
        int index = 0;

        // 前 8 条是低画质 SDR（真实接口里通常先列完 AVC 再列 HEVC），后 4 条是高画质。
        for (int repeat = 0; repeat < 8; repeat++)
        {
            candidates.Add(Candidate(index++, StreamQuality.Hd720, VideoCodec.Avc, StreamFormat.FlvHttp));
        }

        candidates.Add(Candidate(index++, StreamQuality.Hd1080, VideoCodec.Avc, StreamFormat.FlvHttp));
        candidates.Add(Candidate(index++, StreamQuality.Hd1080, VideoCodec.Hevc, StreamFormat.HlsTs));
        candidates.Add(Candidate(index++, StreamQuality.Hd1080, VideoCodec.Avc, StreamFormat.HlsTs));
        candidates.Add(Candidate(index, StreamQuality.Hdr, VideoCodec.Hevc, StreamFormat.FlvHttp));

        IReadOnlyList<StreamCandidate> retained = [.. CandidateOrdering.Prioritize(candidates).Take(8)];

        Assert.Equal(8, retained.Count);
        Assert.True(retained[0].Quality == StreamQuality.Hdr, "HDR 原画必须排在第一条");
        Assert.True(
            retained.Any(static candidate => candidate.Codec == VideoCodec.Hevc),
            "截断后必须仍有 HEVC 候选（否则 HDR 房间永远播不到 HDR）");
        Assert.False(
            retained.Skip(4).Any(static candidate => candidate.Quality != StreamQuality.Hd720),
            "高画质候选必须全部保留在前 4 条，不能被低画质挤掉，也不能漏掉");
        Assert.Equal(
            3,
            retained.Count(static candidate => candidate.Quality == StreamQuality.Hd1080),
            "三条 1080P 与一条 HDR 必须全部进入保留列表（共 4 条高画质）");
    }

    /// <summary>
    /// mpv 播放 HEVC 必须优先 HLS(ts)：B站 HEVC 的 FLV 是 Enhanced FLV 封装，
    /// mpv/ffmpeg 对其 demux 会读错 NAL 大小（Invalid NAL unit size），画面出不来。
    /// </summary>
    [TestMethod("mpv 候选排序：HEVC 优先 HLS 而非 Enhanced FLV")]
    public void MpvPrefersHlsForHevc()
    {
        IReadOnlyList<StreamCandidate> ordered = CandidateOrdering.PrioritizeForMpv(
        [
            Candidate(0, StreamQuality.Hdr, VideoCodec.Hevc, StreamFormat.FlvHttp),
            Candidate(1, StreamQuality.Hdr, VideoCodec.Hevc, StreamFormat.HlsTs),
        ]);

        Assert.Equal(StreamFormat.HlsTs, ordered[0].Format, "HEVC 必须优先 HLS(ts)，Enhanced FLV 会被 mpv 误判");
        Assert.Equal(VideoCodec.Hevc, ordered[0].Codec);
        Assert.Equal(StreamFormat.FlvHttp, ordered[1].Format, "Enhanced FLV 的 HEVC 仍保留作为后备");
    }

    /// <summary>mpv 播放 AVC 保持 FLV 优先：标准 FLV 延迟最低，不受 Enhanced FLV 问题影响。</summary>
    [TestMethod("mpv 候选排序：AVC 保持 FLV 优先")]
    public void MpvKeepsFlvForAvc()
    {
        IReadOnlyList<StreamCandidate> ordered = CandidateOrdering.PrioritizeForMpv(
        [
            Candidate(0, StreamQuality.Hd1080, VideoCodec.Avc, StreamFormat.HlsTs),
            Candidate(1, StreamQuality.Hd1080, VideoCodec.Avc, StreamFormat.FlvHttp),
        ]);

        Assert.Equal(StreamFormat.FlvHttp, ordered[0].Format, "AVC 的 FLV 是标准 FLV，应保持优先");
    }

    /// <summary>mpv 播放仍按画质降序：即使高画质只有 Enhanced FLV，也排在低画质前面。</summary>
    [TestMethod("mpv 候选排序：画质仍优先")]
    public void MpvStillSortsByQuality()
    {
        IReadOnlyList<StreamCandidate> ordered = CandidateOrdering.PrioritizeForMpv(
        [
            Candidate(0, StreamQuality.Hd1080, VideoCodec.Avc, StreamFormat.FlvHttp),
            Candidate(1, StreamQuality.Hdr, VideoCodec.Hevc, StreamFormat.FlvHttp),
        ]);

        Assert.Equal(StreamQuality.Hdr, ordered[0].Quality, "mpv 也要画质优先，HDR 排最前");
    }

    /// <summary>构造测试候选。</summary>
    /// <param name="sourceIndex">源索引。</param>
    /// <param name="quality">画质。</param>
    /// <param name="codec">编码。</param>
    /// <param name="format">容器。</param>
    /// <returns>候选。</returns>
    private static StreamCandidate Candidate(
        int sourceIndex,
        StreamQuality quality,
        VideoCodec codec,
        StreamFormat format) => new()
    {
        SourceIndex = sourceIndex,
        Url = "https://cdn.example.com/live/" + sourceIndex,
        Format = format,
        CdnHost = "cdn.example.com",
        Codec = codec,
        Quality = quality,
        UrlFingerprint = "probe-" + sourceIndex,
    };
}
