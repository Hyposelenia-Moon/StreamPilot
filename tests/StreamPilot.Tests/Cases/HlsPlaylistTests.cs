namespace StreamPilot.Tests.Cases;

using StreamPilot.Recording.Hls;
using StreamPilot.Tests.Framework;

/// <summary>
/// <see cref="HlsPlaylistParser"/> 与 TS 对齐逻辑的单元测试。
/// </summary>
[TestClass]
public sealed class HlsPlaylistTests
{
    private const string MediaPlaylist = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-TARGETDURATION:4
        #EXT-X-MEDIA-SEQUENCE:12
        #EXTINF:4.000,
        seg-12.ts
        #EXTINF:4.000,
        https://cdn.example/hls/seg-13.ts
        #EXTINF:3.500,
        seg-14.ts
        """;

    private const string MasterPlaylist = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080
        high/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=854x480
        low/index.m3u8
        """;

    /// <summary>解析 media 播放列表：分片、目标时长、媒体序号。</summary>
    [TestMethod("HLS 解析：media 播放列表")]
    public void ParsesMediaPlaylist()
    {
        Uri baseUri = new("https://cdn.example/live/index.m3u8");
        HlsPlaylist playlist = HlsPlaylistParser.Parse(MediaPlaylist, baseUri);

        Assert.False(playlist.IsMasterPlaylist);
        Assert.Equal(3, playlist.SegmentUris.Count);
        Assert.Equal("https://cdn.example/live/seg-12.ts", playlist.SegmentUris[0]);
        Assert.Equal("https://cdn.example/hls/seg-13.ts", playlist.SegmentUris[1]);
        Assert.Equal(4, playlist.TargetDurationSeconds);
        Assert.Equal(12, playlist.MediaSequence);
        Assert.False(playlist.IsEndList);

        // 每个分片的 #EXTINF 时长被单独保留（TS 分片时长的依据）。
        Assert.Equal(3, playlist.SegmentDurationsSeconds.Count);
        Assert.EqualDouble(4.0, playlist.SegmentDurationsSeconds[0]);
        Assert.EqualDouble(4.0, playlist.SegmentDurationsSeconds[1]);
        Assert.EqualDouble(3.5, playlist.SegmentDurationsSeconds[2]);
        Assert.EqualDouble(11.5, playlist.TotalDurationSeconds);
    }

    /// <summary>缺少 EXTINF 时用 TARGETDURATION 兜底。</summary>
    [TestMethod("HLS 解析：缺少 EXTINF 回退目标时长")]
    public void FallsBackToTargetDuration()
    {
        HlsPlaylist playlist = HlsPlaylistParser.Parse(
            "#EXTM3U\n#EXT-X-TARGETDURATION:6\na.ts\nb.ts\n",
            new Uri("https://cdn.example/x.m3u8"));

        Assert.Equal(2, playlist.SegmentUris.Count);
        Assert.EqualDouble(6.0, playlist.SegmentDurationsSeconds[0]);
        Assert.EqualDouble(6.0, playlist.SegmentDurationsSeconds[1]);
        Assert.EqualDouble(12.0, playlist.TotalDurationSeconds);
    }

    /// <summary>解析 master 播放列表：variant 地址被解析为绝对地址。</summary>
    [TestMethod("HLS 解析：master 播放列表")]
    public void ParsesMasterPlaylist()
    {
        Uri baseUri = new("https://cdn.example/live/master.m3u8");
        HlsPlaylist playlist = HlsPlaylistParser.Parse(MasterPlaylist, baseUri);

        Assert.True(playlist.IsMasterPlaylist);
        Assert.Equal(2, playlist.VariantUris.Count);
        Assert.Equal("https://cdn.example/live/high/index.m3u8", playlist.VariantUris[0]);
        Assert.Equal(0, playlist.SegmentUris.Count);
    }

    /// <summary>ENDLIST 被识别为直播结束。</summary>
    [TestMethod("HLS 解析：ENDLIST 标记")]
    public void DetectsEndList()
    {
        HlsPlaylist playlist = HlsPlaylistParser.Parse(
            "#EXTM3U\n#EXTINF:1.0,\na.ts\n#EXT-X-ENDLIST\n",
            new Uri("https://cdn.example/x.m3u8"));

        Assert.True(playlist.IsEndList);
        Assert.Equal(1, playlist.SegmentUris.Count);
    }

    /// <summary>空内容抛出参数异常（不返回空结果蒙混过关）。</summary>
    [TestMethod("HLS 解析：空内容抛异常")]
    public void RejectsEmptyContent()
    {
        Assert.Throws<ArgumentException>(() => HlsPlaylistParser.Parse("   ", new Uri("https://cdn.example/x.m3u8")));
    }

    /// <summary>TS 对齐：只保留完整的 188 字节包。</summary>
    [TestMethod("TS 对齐：按 188 字节整包截断")]
    public void AlignsToPacketBoundary()
    {
        byte[] payload = new byte[(TsStreamRecorder.TsPacketSize * 3) + 17];
        payload[0] = TsStreamRecorder.TsSyncByte;
        payload[TsStreamRecorder.TsPacketSize] = TsStreamRecorder.TsSyncByte;
        payload[TsStreamRecorder.TsPacketSize * 2] = TsStreamRecorder.TsSyncByte;

        Assert.Equal(TsStreamRecorder.TsPacketSize * 3, TsStreamRecorder.AlignToPacketBoundary(payload));
    }

    /// <summary>TS 对齐：跳过前导垃圾直到第一个同步字节。</summary>
    [TestMethod("TS 对齐：跳过前导垃圾")]
    public void SkipsLeadingGarbage()
    {
        byte[] payload = new byte[(TsStreamRecorder.TsPacketSize * 2) + 5];
        payload[3] = TsStreamRecorder.TsSyncByte;
        payload[3 + TsStreamRecorder.TsPacketSize] = TsStreamRecorder.TsSyncByte;

        Assert.Equal(TsStreamRecorder.TsPacketSize * 2, TsStreamRecorder.AlignToPacketBoundary(payload));
    }

    /// <summary>TS 对齐：不足一包或无同步字节时返回 0。</summary>
    [TestMethod("TS 对齐：不足一包或无同步字节")]
    public void ReturnsZeroWhenUnusable()
    {
        Assert.Equal(0, TsStreamRecorder.AlignToPacketBoundary(new byte[10]));
        Assert.Equal(0, TsStreamRecorder.AlignToPacketBoundary(new byte[TsStreamRecorder.TsPacketSize * 2]));
    }
}
