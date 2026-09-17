namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Models;
using StreamPilot.Core.Parsers;
using StreamPilot.Tests.Framework;

/// <summary>
/// <see cref="StreamCandidate"/> 与 <see cref="StreamCandidateBuilder"/> 的测试。
/// </summary>
[TestClass]
public sealed class StreamCandidateTests
{
    /// <summary>合法 URL 被接受，源索引按加入顺序递增。</summary>
    [TestMethod("候选构造：源索引递增与指纹生成")]
    public void AddsCandidatesInOrder()
    {
        StreamCandidateBuilder builder = new(PlatformId.Bilibili);
        Assert.True(builder.TryAdd("https://a.example/live.flv?sign=1", StreamFormat.FlvHttp, VideoCodec.Avc, StreamQuality.Dolby));
        Assert.True(builder.TryAdd("https://b.example/live.m3u8?sign=2", StreamFormat.HlsTs, VideoCodec.Hevc, StreamQuality.Hd1080));

        IReadOnlyList<StreamCandidate> candidates = builder.Build();
        Assert.Equal(2, candidates.Count);
        Assert.Equal(0, candidates[0].SourceIndex);
        Assert.Equal(1, candidates[1].SourceIndex);
        Assert.Equal("a.example", candidates[0].CdnHost);
        Assert.Contains("fp=", candidates[0].UrlFingerprint);
    }

    /// <summary>非法 URL 被丢弃，不抛异常。</summary>
    [TestMethod("候选构造：非法与非绝对地址被丢弃")]
    public void RejectsInvalidUrls()
    {
        StreamCandidateBuilder builder = new(PlatformId.Huya);
        Assert.False(builder.TryAdd(null, StreamFormat.FlvHttp, VideoCodec.Avc, StreamQuality.Unknown));
        Assert.False(builder.TryAdd("   ", StreamFormat.FlvHttp, VideoCodec.Avc, StreamQuality.Unknown));
        Assert.False(builder.TryAdd("/relative/path.flv", StreamFormat.FlvHttp, VideoCodec.Avc, StreamQuality.Unknown));
        Assert.Equal(0, builder.Count);
    }

    /// <summary>CDN host 显式覆盖与回退。</summary>
    [TestMethod("候选构造：CDN host 显式覆盖与回退")]
    public void HandlesCdnHost()
    {
        StreamCandidate? explicitHost = StreamCandidate.TryCreate(
            PlatformId.Bilibili, 0, "https://a.example/x.flv", StreamFormat.FlvHttp, VideoCodec.Avc, StreamQuality.Hd1080, null, null, "cn-jsnj-01");
        StreamCandidate? fallbackHost = StreamCandidate.TryCreate(
            PlatformId.Bilibili, 0, "https://a.example/x.flv", StreamFormat.FlvHttp, VideoCodec.Avc, StreamQuality.Hd1080, null, null, "  ");

        Assert.NotNull(explicitHost);
        Assert.Equal("cn-jsnj-01", explicitHost!.CdnHost);
        Assert.NotNull(fallbackHost);
        Assert.Equal("a.example", fallbackHost!.CdnHost);
    }

    /// <summary>可播放 / 可录制判定。</summary>
    [TestMethod("候选能力：Web 播放与录制支持矩阵")]
    public void PlayabilityMatrix()
    {
        Assert.True(Create(StreamFormat.FlvHttp).IsWebPlayable());
        Assert.True(Create(StreamFormat.HlsTs).IsWebPlayable());
        Assert.True(Create(StreamFormat.HlsFmp4).IsWebPlayable());
        Assert.False(Create(StreamFormat.Rtmp).IsWebPlayable());

        Assert.True(Create(StreamFormat.FlvHttp).IsRecordable());
        Assert.True(Create(StreamFormat.HlsTs).IsRecordable());
        Assert.False(Create(StreamFormat.HlsFmp4).IsRecordable());
        Assert.False(Create(StreamFormat.Rtmp).IsRecordable());
    }

    /// <summary>房间的"首个可 Web 播放候选"跳过 RTMP 与空地址。</summary>
    [TestMethod("房间模型：首播候选跳过 RTMP 与空地址")]
    public void PicksFirstPlayableCandidate()
    {
        ResolvedRoom room = new()
        {
            Platform = PlatformId.Douyu,
            RoomId = "1",
            Anchor = "a",
            Title = "t",
            Candidates =
            [
                Create(StreamFormat.Rtmp),
                Create(StreamFormat.FlvHttp),
            ],
            ResolvedAt = DateTimeOffset.UtcNow,
        };

        StreamCandidate? candidate = room.FirstWebPlayableOrDefault();
        Assert.NotNull(candidate);
        Assert.Equal(StreamFormat.FlvHttp, candidate!.Format);
    }

    /// <summary>B站/抖音画质映射。</summary>
    [TestMethod("画质映射：B站 qn 与抖音键名")]
    public void QualityMapping()
    {
        Assert.Equal(StreamQuality.Dolby, QualityNames.FromBilibiliQualityNumber(30000));
        Assert.Equal(StreamQuality.Uhd4K, QualityNames.FromBilibiliQualityNumber(20000));
        Assert.Equal(StreamQuality.Qhd2K, QualityNames.FromBilibiliQualityNumber(15000));
        Assert.Equal(StreamQuality.Hd1080HighFps, QualityNames.FromBilibiliQualityNumber(10000));
        Assert.Equal(StreamQuality.Hd1080, QualityNames.FromBilibiliQualityNumber(400));
        Assert.Equal(StreamQuality.Hd720, QualityNames.FromBilibiliQualityNumber(250));
        Assert.Equal(StreamQuality.Sd480, QualityNames.FromBilibiliQualityNumber(150));
        Assert.Equal(StreamQuality.Unknown, QualityNames.FromBilibiliQualityNumber(12345));

        Assert.Equal(StreamQuality.Hd1080, QualityNames.FromDouyinQualityName("FULL_HD1"));
        Assert.Equal(StreamQuality.Hd720, QualityNames.FromDouyinQualityName("HD1"));
        Assert.Equal(StreamQuality.Sd480, QualityNames.FromDouyinQualityName("SD1"));
        Assert.Equal(StreamQuality.Unknown, QualityNames.FromDouyinQualityName("OTHER"));
        Assert.Equal(StreamQuality.Unknown, QualityNames.FromDouyinQualityName(null));
    }

    private static StreamCandidate Create(StreamFormat format) => new()
    {
        SourceIndex = 0,
        Url = "https://a.example/x",
        Format = format,
        CdnHost = "a.example",
        Codec = VideoCodec.Avc,
        Quality = StreamQuality.Unknown,
        UrlFingerprint = "len=1;fp=-",
    };
}
