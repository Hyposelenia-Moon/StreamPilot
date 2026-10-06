namespace StreamPilot.Tests.Cases;

using System.Net;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Recording;
using StreamPilot.Recording.Flv;
using StreamPilot.Recording.Hls;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// <see cref="TsStreamRecorder"/> 的分片落盘测试：验证"只写从首个同步字节开始的完整 TS 包"。
/// </summary>
/// <remarks>
/// 用例通过替换 <see cref="HttpMessageHandler"/> 提供内存分片，因此无需网络即可逐字节校验写出内容。
/// </remarks>
[TestClass]
public sealed class TsStreamRecorderTests
{
    /// <summary>测试分片包含的完整 TS 包数量。</summary>
    private const int PacketCount = 4;

    /// <summary>分片头部注入的垃圾字节数（模拟 CDN 分片的前导残留）。</summary>
    private const int LeadingGarbageBytes = 7;

    /// <summary>分片尾部注入的垃圾字节数（不足一个 TS 包，必须被丢弃）。</summary>
    private const int TrailingGarbageBytes = 23;

    /// <summary>垃圾字节取值（必须不是 0x47，否则会被当成同步字节）。</summary>
    private const byte GarbageByte = 0x00;

    private const string PlaylistUri = "https://cdn.example/live/index.m3u8";

    private const string SegmentUri = "https://cdn.example/live/seg-1.ts";

    /// <summary>ENDLIST 播放列表：拉取一次即结束，不会进入刷新等待。</summary>
    private static readonly string PlaylistText =
        $"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.000,\n{SegmentUri}\n#EXT-X-ENDLIST\n";

    /// <summary>带前导垃圾的分片必须从首个 0x47 起逐字节落盘，且长度为 188 的整数倍。</summary>
    [TestMethod("TS 录制：跳过前导垃圾后逐字节落盘")]
    public async Task WritesAlignedPacketsVerbatim()
    {
        byte[] payload = BuildSegmentPayload();
        int packetBytes = TsStreamRecorder.TsPacketSize * PacketCount;
        byte[] expected = payload[LeadingGarbageBytes..(LeadingGarbageBytes + packetBytes)];

        using TempDirectory temp = new();
        using HttpClient client = new(new StubHttpMessageHandler(SegmentUri, payload));
        HlsPlaylist playlist = HlsPlaylistParser.Parse(PlaylistText, new Uri(PlaylistUri));

        TsStreamRecorder recorder = new(new SegmentPolicy(), DateTimeOffset.Now, NullStructuredLogger.Instance);
        FlvRecordingResult result = await recorder.RecordFragmentsAsync(
            client,
            playlist,
            new Uri(PlaylistUri),
            BuildRoom(),
            temp.Path,
            referer: null,
            onSegmentCompleted: null,
            CancellationToken.None);

        Assert.Equal(1, result.Segments.Count, "ENDLIST 播放列表必须收尾并登记一个分片");
        byte[] written = await File.ReadAllBytesAsync(Path.Combine(temp.Path, result.Segments[0].FileName));
        Assert.SequenceEqual(expected, written, "写出的内容必须等于从首个 0x47 开始的整包序列");
        Assert.Equal(packetBytes, written.Length, "不足一个 TS 包的尾部残留必须被丢弃");
        Assert.Equal(0, written.Length % TsStreamRecorder.TsPacketSize, "长度必须是 188 的整数倍");
        Assert.Equal(TsStreamRecorder.TsSyncByte, written[0], "首字节必须是 TS 同步字节 0x47");
    }

    /// <summary>构造带前导/尾部垃圾的分片字节。</summary>
    /// <returns>分片字节。</returns>
    private static byte[] BuildSegmentPayload()
    {
        List<byte> buffer = [];
        for (int index = 0; index < LeadingGarbageBytes; index++)
        {
            buffer.Add(GarbageByte);
        }

        for (int packet = 0; packet < PacketCount; packet++)
        {
            buffer.Add(TsStreamRecorder.TsSyncByte);
            for (int offset = 1; offset < TsStreamRecorder.TsPacketSize; offset++)
            {
                buffer.Add((byte)((packet + offset) % byte.MaxValue));
            }
        }

        for (int index = 0; index < TrailingGarbageBytes; index++)
        {
            buffer.Add(GarbageByte);
        }

        return [.. buffer];
    }

    /// <summary>构造测试房间信息。</summary>
    /// <returns>房间信息。</returns>
    private static ResolvedRoom BuildRoom() => new()
    {
        Platform = PlatformId.Bilibili,
        RoomId = "123456",
        Anchor = "测试主播",
        Title = "测试直播间",
        Candidates = [],
        ResolvedAt = DateTimeOffset.Now,
    };

    /// <summary>只对指定地址返回 200 + 固定字节的测试 HTTP 处理器。</summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _expectedUri;
        private readonly byte[] _payload;

        /// <summary>初始化处理器。</summary>
        /// <param name="expectedUri">唯一允许的请求地址。</param>
        /// <param name="payload">响应体字节。</param>
        public StubHttpMessageHandler(string expectedUri, byte[] payload)
        {
            _expectedUri = expectedUri;
            _payload = payload;
        }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            string actualUri = request.RequestUri?.ToString() ?? string.Empty;
            if (!string.Equals(actualUri, _expectedUri, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload),
            });
        }
    }
}
