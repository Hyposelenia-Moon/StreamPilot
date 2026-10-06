namespace StreamPilot.Tests.Cases;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Recording;
using StreamPilot.Recording.Hls;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// <see cref="RecordingSession"/> 的 TS 录制端到端测试（SP-03 的会话侧症状：sidecar 全 0）。
/// </summary>
/// <remarks>
/// 用例通过回环 HTTP 服务器提供真实 m3u8 与 TS 分片，走真实落盘：
/// 录制结束后侧车元数据必须反映真实产物（字节数、时长、分片列表），
/// 且分片文件句柄必须已释放（能以 <see cref="FileShare.None"/> 重新打开）。
/// 旧实现中取消会让录制器跳过收尾，sidecar 会写成 <c>totalBytes=0 / durationSeconds=0 / segments=[]</c>。
/// </remarks>
[TestClass]
public sealed class RecordingSessionTsTests
{
    /// <summary>测试分片包含的完整 TS 包数量。</summary>
    private const int PacketCount = 4;

    /// <summary>每个分片的字节数。</summary>
    private const long SegmentBytes = PacketCount * TsStreamRecorder.TsPacketSize;

    /// <summary>单个分片的媒体时长（秒）。</summary>
    private const double SegmentDurationSeconds = 4.0;

    /// <summary>播放列表路径。</summary>
    private const string PlaylistPath = "/live/index.m3u8";

    /// <summary>第一个分片路径（正常返回）。</summary>
    private const string FirstSegmentPath = "/live/seg-1.ts";

    /// <summary>第二个分片路径（永远卡住，用于制造"取消发生在拉分片期间"）。</summary>
    private const string SecondSegmentPath = "/live/seg-2.ts";

    /// <summary>无数据到达判定秒数（TS 录制不使用，仅为满足参数校验）。</summary>
    private const int StallTimeoutSeconds = 10;

    /// <summary>最大重连次数。</summary>
    private const int MaxReconnectAttempts = 3;

    /// <summary>轮询间隔（毫秒）。</summary>
    private const int PollIntervalMs = 10;

    /// <summary>等待上限。</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>取消录制后：sidecar 反映真实产物，且文件句柄已释放。</summary>
    [TestMethod("TS 录制会话：取消后 sidecar 有真实分片且句柄已释放")]
    public async Task StopDuringRecordingWritesRealMetadataAndReleasesHandle()
    {
        byte[] payload = BuildSegmentPayload(fill: 0x21);
        using TempDirectory temp = new();
        using LoopbackHttpServer server = new();
        server.ServeText(PlaylistPath, BuildPlaylist(endList: false, ("seg-1.ts", SegmentDurationSeconds), ("seg-2.ts", SegmentDurationSeconds)))
            .ServeBytes(FirstSegmentPath, payload)
            .Stall(SecondSegmentPath);
        RecordingLogger logger = new();
        using HttpClientFactory clients = new(new NetworkOptions(), logger);

        await using RecordingSession session = await RecordingSession.StartAsync(
            BuildRequest(server.AbsoluteUri(PlaylistPath), temp.Path),
            clients,
            StallTimeoutSeconds,
            MaxReconnectAttempts,
            logger).ConfigureAwait(false);

        await server.WaitForRequestAsync(SecondSegmentPath, WaitTimeout).ConfigureAwait(false);
        await session.StopAsync(RecordingStopReason.UserStopped, CancellationToken.None).ConfigureAwait(false);

        JsonElement metadata = await WaitForCompletedMetadataAsync(session.MetadataPath, WaitTimeout).ConfigureAwait(false);
        Assert.Equal(SegmentBytes, metadata.GetProperty("totalBytes").GetInt64(), "sidecar 必须记录真实字节数，而不是 0");
        Assert.EqualDouble(SegmentDurationSeconds, metadata.GetProperty("durationSeconds").GetDouble(), 0.001, "sidecar 必须记录真实时长");
        Assert.Equal(1, metadata.GetProperty("segments").GetArrayLength(), "sidecar 必须登记末分片");
        Assert.Equal(RecordingStopReason.UserStopped, ReadStopReason(metadata), "取消必须被记录为用户主动停止");
        Assert.False(metadata.GetProperty("endedAtUtc").ValueKind == JsonValueKind.Null, "sidecar 必须写入结束时间");

        AssertArtifactReleased(session.OutputDirectory);

        // 文件句柄释放后应能立刻改名（再次证明没有残留句柄）。
        string segment = RecordingArtifacts.ListSegmentFiles(session.OutputDirectory)[0];
        string renamed = segment + ".moved";
        File.Move(segment, renamed);
        Assert.True(File.Exists(renamed), "收尾后分片必须可以立即改名");
    }

    /// <summary>播放列表 ENDLIST 自然结束：sidecar 同样反映真实产物。</summary>
    [TestMethod("TS 录制会话：ENDLIST 结束后 sidecar 有真实分片")]
    public async Task EndListWritesRealMetadataAndReleasesHandle()
    {
        byte[] payload = BuildSegmentPayload(fill: 0x31);
        using TempDirectory temp = new();
        using LoopbackHttpServer server = new();
        server.ServeText(PlaylistPath, BuildPlaylist(endList: true, ("seg-1.ts", SegmentDurationSeconds)))
            .ServeBytes(FirstSegmentPath, payload);
        RecordingLogger logger = new();
        using HttpClientFactory clients = new(new NetworkOptions(), logger);

        await using RecordingSession session = await RecordingSession.StartAsync(
            BuildRequest(server.AbsoluteUri(PlaylistPath), temp.Path),
            clients,
            StallTimeoutSeconds,
            MaxReconnectAttempts,
            logger).ConfigureAwait(false);

        JsonElement metadata = await WaitForCompletedMetadataAsync(session.MetadataPath, WaitTimeout).ConfigureAwait(false);

        Assert.Equal(SegmentBytes, metadata.GetProperty("totalBytes").GetInt64(), "sidecar 必须记录真实字节数");
        Assert.EqualDouble(SegmentDurationSeconds, metadata.GetProperty("durationSeconds").GetDouble(), 0.001, "sidecar 必须记录真实时长");
        Assert.Equal(1, metadata.GetProperty("segments").GetArrayLength(), "sidecar 必须登记分片");
        Assert.Equal(RecordingStopReason.StreamEnded, ReadStopReason(metadata), "ENDLIST 必须被记录为流自然结束");
        Assert.False(metadata.GetProperty("endedAtUtc").ValueKind == JsonValueKind.Null, "sidecar 必须写入结束时间");

        AssertArtifactReleased(session.OutputDirectory);
    }

    /// <summary>读取侧车元数据里的停止原因（大小写不敏感，避免依赖枚举序列化的命名策略）。</summary>
    /// <param name="metadata">元数据根节点。</param>
    /// <returns>停止原因。</returns>
    private static RecordingStopReason ReadStopReason(JsonElement metadata) =>
        Enum.Parse<RecordingStopReason>(metadata.GetProperty("stopReason").GetString() ?? string.Empty, ignoreCase: true);

    /// <summary>断言输出目录中存在且仅存在一个分片，且其句柄已释放、内容已落盘。</summary>
    /// <param name="outputDirectory">输出目录。</param>
    private static void AssertArtifactReleased(string outputDirectory)
    {
        string[] segments = RecordingArtifacts.ListSegmentFiles(outputDirectory);
        Assert.Equal(1, segments.Length, "输出目录必须只有一个分片文件");
        long length = RecordingArtifacts.AssertHandleReleased(segments[0]);
        Assert.Equal(SegmentBytes, length, "分片必须完整落盘");
    }

    /// <summary>轮询等待侧车元数据的停止原因不再是 None（即会话已完成收尾）。</summary>
    /// <param name="metadataPath">侧车文件路径。</param>
    /// <param name="timeout">最长等待时间。</param>
    /// <returns>已完成的元数据根节点。</returns>
    /// <exception cref="AssertionFailedException">超时仍未收尾时抛出。</exception>
    private static async Task<JsonElement> WaitForCompletedMetadataAsync(string metadataPath, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(metadataPath))
                {
                    JsonElement root = await ReadMetadataAsync(metadataPath).ConfigureAwait(false);
                    if (ReadStopReason(root) != RecordingStopReason.None)
                    {
                        return root;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // 侧车文件正在被原子替换，下一轮重试；超时后由下方统一判失败。
            }

            await Task.Delay(PollIntervalMs).ConfigureAwait(false);
        }

        throw new AssertionFailedException($"等待录制会话收尾超时（{timeout.TotalSeconds} 秒）：{metadataPath}");
    }

    /// <summary>读取侧车元数据 JSON 根节点。</summary>
    /// <param name="metadataPath">侧车文件路径。</param>
    /// <returns>元数据根节点（已克隆，脱离文档生命周期）。</returns>
    private static async Task<JsonElement> ReadMetadataAsync(string metadataPath)
    {
        string json = await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>构造录制请求（HLS-TS 候选指向回环服务器）。</summary>
    /// <param name="playlistUrl">播放列表绝对地址。</param>
    /// <param name="outputRoot">输出根目录（会话会在其下再建平台/主播子目录）。</param>
    /// <returns>录制请求。</returns>
    private static RecordingRequest BuildRequest(string playlistUrl, string outputRoot) => new()
    {
        Room = BuildRoom(playlistUrl),
        Candidate = BuildCandidate(playlistUrl),
        OutputDirectory = outputRoot,
    };

    /// <summary>构造测试房间信息。</summary>
    /// <param name="playlistUrl">播放列表地址（作为唯一候选）。</param>
    /// <returns>房间信息。</returns>
    private static ResolvedRoom BuildRoom(string playlistUrl) => new()
    {
        Platform = PlatformId.Bilibili,
        RoomId = "123456",
        Anchor = "测试主播",
        Title = "测试直播间",
        Candidates = [BuildCandidate(playlistUrl)],
        ResolvedAt = DateTimeOffset.Now,
    };

    /// <summary>构造 HLS-TS 测试候选。</summary>
    /// <param name="playlistUrl">播放列表地址。</param>
    /// <returns>流候选。</returns>
    private static StreamCandidate BuildCandidate(string playlistUrl) => new()
    {
        SourceIndex = 0,
        Url = playlistUrl,
        Format = StreamFormat.HlsTs,
        CdnHost = "127.0.0.1",
        Codec = VideoCodec.Avc,
        Quality = StreamQuality.Hd1080,
        UrlFingerprint = "test-fingerprint",
    };

    /// <summary>构造 m3u8 文本（相对地址，由解析器解析为绝对地址）。</summary>
    /// <param name="endList">是否包含 ENDLIST。</param>
    /// <param name="segments">分片相对地址与时长。</param>
    /// <returns>m3u8 文本。</returns>
    private static string BuildPlaylist(bool endList, params (string Uri, double DurationSeconds)[] segments)
    {
        StringBuilder builder = new();
        builder.Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n");
        foreach ((string uri, double durationSeconds) in segments)
        {
            builder.Append("#EXTINF:").Append(durationSeconds.ToString("F3", CultureInfo.InvariantCulture)).Append(",\n");
            builder.Append(uri).Append('\n');
        }

        if (endList)
        {
            builder.Append("#EXT-X-ENDLIST\n");
        }

        return builder.ToString();
    }

    /// <summary>构造整包对齐的 TS 分片字节。</summary>
    /// <param name="fill">包内填充字节。</param>
    /// <returns>分片字节。</returns>
    private static byte[] BuildSegmentPayload(byte fill)
    {
        byte[] payload = new byte[PacketCount * TsStreamRecorder.TsPacketSize];
        for (int packet = 0; packet < PacketCount; packet++)
        {
            int offset = packet * TsStreamRecorder.TsPacketSize;
            payload[offset] = TsStreamRecorder.TsSyncByte;
            for (int index = 1; index < TsStreamRecorder.TsPacketSize; index++)
            {
                payload[offset + index] = fill;
            }
        }

        return payload;
    }
}
