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
using StreamPilot.Recording.Flv;
using StreamPilot.Recording.Hls;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// <see cref="RecordingSession"/> 的失败收尾与停止原因回归测试。
/// </summary>
/// <remarks>
/// 覆盖三类过去会漏掉的路径：
/// <list type="number">
///   <item>worker 抛出非取消 / 非录制异常时，异常不得漏到 <c>StopAsync</c> / <c>DisposeAsync</c>，
///     且 sidecar 的 <c>stopReason</c> 必须是明确取值（旧实现停在 <c>None</c>）；</item>
///   <item>调用方通过 <c>StopAsync(reason)</c> 给出的原因必须被尊重（旧实现一律被覆盖成 <c>UserStopped</c>）；</item>
///   <item>HTTP-FLV 会话在真实落盘下同样要写出真实产物并释放分片句柄。</item>
/// </list>
/// </remarks>
[TestClass]
public sealed class RecordingSessionFailureTests
{
    /// <summary>TS 测试分片包含的完整 TS 包数量。</summary>
    private const int PacketCount = 4;

    /// <summary>每个分片的字节数。</summary>
    private const long SegmentBytes = PacketCount * TsStreamRecorder.TsPacketSize;

    /// <summary>单个分片的媒体时长（秒）。</summary>
    private const double SegmentDurationSeconds = 4.0;

    /// <summary>播放列表路径。</summary>
    private const string PlaylistPath = "/live/index.m3u8";

    /// <summary>第一个分片路径（正常返回）。</summary>
    private const string FirstSegmentPath = "/live/seg-1.ts";

    /// <summary>第二个分片路径（永远卡住，用于制造"停止发生在拉分片期间"）。</summary>
    private const string SecondSegmentPath = "/live/seg-2.ts";

    /// <summary>HTTP-FLV 路径。</summary>
    private const string FlvPath = "/live/stream.flv";

    /// <summary>无数据到达判定秒数。</summary>
    private const int StallTimeoutSeconds = 10;

    /// <summary>轮询间隔（毫秒）。</summary>
    private const int PollIntervalMs = 10;

    /// <summary>等待上限。</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>worker 意外失败：不冒泡到 StopAsync，且 sidecar 记录为失败。</summary>
    [TestMethod("录制会话：worker 意外失败被收尾且 stopReason 为失败")]
    public async Task ContainsUnexpectedWorkerFailureAndRecordsStopReason()
    {
        using TempDirectory temp = new();
        RecordingLogger logger = new();
        using HttpClientFactory clients = new(new NetworkOptions(), logger);

        // 候选地址不是合法绝对地址：RunTsAsync 在建连之前就会抛 UriFormatException（非取消、非录制异常）。
        await using RecordingSession session = await RecordingSession.StartAsync(
            BuildRequest(BuildCandidate("这不是一个合法的绝对地址", StreamFormat.HlsTs), temp.Path),
            clients,
            StallTimeoutSeconds,
            maxReconnectAttempts: 0,
            logger).ConfigureAwait(false);

        await WaitUntilAsync(() => !session.Status.IsRecording, "等待 worker 失败结束").ConfigureAwait(false);

        // 旧实现：异常从 worker 漏到 StopAsync（这里会抛 UriFormatException）。
        await session.StopAsync(RecordingStopReason.UserStopped, CancellationToken.None).ConfigureAwait(false);

        JsonElement metadata = await WaitForCompletedMetadataAsync(session.MetadataPath).ConfigureAwait(false);
        Assert.Equal(RecordingStopReason.Failed, ReadStopReason(metadata), "意外失败必须写成明确的停止原因，而不是 None");
        Assert.Equal(RecordingStopReason.Failed, session.StopReason, "会话内存状态同样不得停在 None");
        Assert.True(logger.HasLevel(LogLevel.Error), "意外失败必须留下 Error 日志");
    }

    /// <summary>调用方给出的停止原因必须被保留，不得被录制器的 UserStopped 覆盖。</summary>
    [TestMethod("录制会话：StopAsync 传入的停止原因被保留")]
    public async Task KeepsStopReasonPassedByCaller()
    {
        JsonElement metadata = await StopStalledSessionAsync(RecordingStopReason.StreamEnded).ConfigureAwait(false);
        Assert.Equal(
            RecordingStopReason.StreamEnded,
            ReadStopReason(metadata),
            "调用方传入的停止原因不得被录制器的 UserStopped 覆盖");
    }

    /// <summary>调用方未指定原因（None）时保持既有默认行为：按用户主动停止处理。</summary>
    [TestMethod("录制会话：StopAsync 传 None 时回落为用户主动停止")]
    public async Task FallsBackToUserStoppedWhenStopReasonUnspecified()
    {
        JsonElement metadata = await StopStalledSessionAsync(RecordingStopReason.None).ConfigureAwait(false);
        Assert.Equal(RecordingStopReason.UserStopped, ReadStopReason(metadata), "未指定原因时保持既有默认行为");
    }

    /// <summary>HTTP-FLV 会话：真实落盘后 sidecar 反映真实产物，且分片句柄已释放。</summary>
    [TestMethod("录制会话：HTTP-FLV 结束后 sidecar 有真实分片且句柄已释放")]
    public async Task FlvSessionWritesRealMetadataAndReleasesHandle()
    {
        byte[] payload = BuildFlvStream();
        using TempDirectory temp = new();
        using LoopbackHttpServer server = new();
        server.ServeBytes(FlvPath, payload);
        RecordingLogger logger = new();
        using HttpClientFactory clients = new(new NetworkOptions(), logger);

        await using RecordingSession session = await RecordingSession.StartAsync(
            BuildRequest(BuildCandidate(server.AbsoluteUri(FlvPath), StreamFormat.FlvHttp), temp.Path),
            clients,
            StallTimeoutSeconds,
            maxReconnectAttempts: 0,
            logger).ConfigureAwait(false);

        JsonElement metadata = await WaitForCompletedMetadataAsync(session.MetadataPath).ConfigureAwait(false);

        Assert.Equal(RecordingStopReason.ReconnectLimitReached, ReadStopReason(metadata), "流结束后重连预算已用尽，原因必须明确");
        Assert.True(metadata.GetProperty("totalBytes").GetInt64() > 0, "sidecar 必须记录真实字节数");
        Assert.Equal(1, metadata.GetProperty("segments").GetArrayLength(), "sidecar 必须登记末分片");
        Assert.False(metadata.GetProperty("endedAtUtc").ValueKind == JsonValueKind.Null, "sidecar 必须写入结束时间");

        string[] segments = Directory.GetFiles(session.OutputDirectory, "*.flv", SearchOption.TopDirectoryOnly);
        Assert.Equal(1, segments.Length, "输出目录必须只有一个 FLV 分片");
        long length = RecordingArtifacts.AssertHandleReleased(segments[0]);
        Assert.True(length > 0, "分片必须真的落盘");
        File.Delete(segments[0]);
        Assert.False(File.Exists(segments[0]), "收尾后分片必须可以立即删除（句柄已释放）");
    }

    /// <summary>用一个"卡在第二个分片下载"的 TS 会话执行指定原因的停止，并返回已完成的 sidecar。</summary>
    /// <param name="reason">传给 <c>StopAsync</c> 的停止原因。</param>
    /// <returns>已完成的 sidecar 根节点。</returns>
    private static async Task<JsonElement> StopStalledSessionAsync(RecordingStopReason reason)
    {
        byte[] payload = BuildTsSegmentPayload(fill: 0x51);
        using TempDirectory temp = new();
        using LoopbackHttpServer server = new();
        server.ServeText(PlaylistPath, BuildPlaylist(endList: false, ("seg-1.ts", SegmentDurationSeconds), ("seg-2.ts", SegmentDurationSeconds)))
            .ServeBytes(FirstSegmentPath, payload)
            .Stall(SecondSegmentPath);
        RecordingLogger logger = new();
        using HttpClientFactory clients = new(new NetworkOptions(), logger);

        await using RecordingSession session = await RecordingSession.StartAsync(
            BuildRequest(BuildCandidate(server.AbsoluteUri(PlaylistPath), StreamFormat.HlsTs), temp.Path),
            clients,
            StallTimeoutSeconds,
            maxReconnectAttempts: 3,
            logger).ConfigureAwait(false);

        // 第二个分片已被请求 ⇒ 第一个分片已经写完，停止点因此是确定的。
        await server.WaitForRequestAsync(SecondSegmentPath, WaitTimeout).ConfigureAwait(false);
        await session.StopAsync(reason, CancellationToken.None).ConfigureAwait(false);
        return await WaitForCompletedMetadataAsync(session.MetadataPath).ConfigureAwait(false);
    }

    /// <summary>读取 sidecar 里的停止原因（大小写不敏感）。</summary>
    /// <param name="metadata">元数据根节点。</param>
    /// <returns>停止原因。</returns>
    private static RecordingStopReason ReadStopReason(JsonElement metadata) =>
        Enum.Parse<RecordingStopReason>(metadata.GetProperty("stopReason").GetString() ?? string.Empty, ignoreCase: true);

    /// <summary>轮询等待 sidecar 的停止原因不再是 None（即会话已完成收尾）。</summary>
    /// <param name="metadataPath">sidecar 路径。</param>
    /// <returns>已完成的元数据根节点。</returns>
    /// <exception cref="AssertionFailedException">超时仍未收尾时抛出。</exception>
    private static async Task<JsonElement> WaitForCompletedMetadataAsync(string metadataPath)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < WaitTimeout)
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
                // sidecar 正在被原子替换：下一轮重试，超时后统一判失败。
            }

            await Task.Delay(PollIntervalMs).ConfigureAwait(false);
        }

        throw new AssertionFailedException($"等待录制会话收尾超时（{WaitTimeout.TotalSeconds} 秒）：{metadataPath}");
    }

    /// <summary>读取 sidecar JSON 根节点。</summary>
    /// <param name="metadataPath">sidecar 路径。</param>
    /// <returns>元数据根节点（已克隆，脱离文档生命周期）。</returns>
    private static async Task<JsonElement> ReadMetadataAsync(string metadataPath)
    {
        string json = await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>轮询等待条件成立。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="description">超时描述。</param>
    /// <returns>异步等待任务。</returns>
    /// <exception cref="AssertionFailedException">超时仍未成立时抛出。</exception>
    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < WaitTimeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(PollIntervalMs).ConfigureAwait(false);
        }

        throw new AssertionFailedException($"{description}超时（{WaitTimeout.TotalSeconds} 秒）");
    }

    /// <summary>构造录制请求。</summary>
    /// <param name="candidate">录制候选。</param>
    /// <param name="outputRoot">输出根目录。</param>
    /// <returns>录制请求。</returns>
    private static RecordingRequest BuildRequest(StreamCandidate candidate, string outputRoot) => new()
    {
        Room = BuildRoom(candidate),
        Candidate = candidate,
        OutputDirectory = outputRoot,
    };

    /// <summary>构造测试房间信息。</summary>
    /// <param name="candidate">唯一候选。</param>
    /// <returns>房间信息。</returns>
    private static ResolvedRoom BuildRoom(StreamCandidate candidate) => new()
    {
        Platform = PlatformId.Bilibili,
        RoomId = "123456",
        Anchor = "测试主播",
        Title = "测试直播间",
        Candidates = [candidate],
        ResolvedAt = DateTimeOffset.Now,
    };

    /// <summary>构造测试候选。</summary>
    /// <param name="url">流地址。</param>
    /// <param name="format">流格式。</param>
    /// <returns>流候选。</returns>
    private static StreamCandidate BuildCandidate(string url, StreamFormat format) => new()
    {
        SourceIndex = 0,
        Url = url,
        Format = format,
        CdnHost = "127.0.0.1",
        Codec = VideoCodec.Avc,
        Quality = StreamQuality.Hd1080,
        UrlFingerprint = "test-fingerprint",
    };

    /// <summary>构造 m3u8 文本（相对地址由解析器解析为绝对地址）。</summary>
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
    private static byte[] BuildTsSegmentPayload(byte fill)
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

    /// <summary>构造一段最小的合法 HTTP-FLV 字节流（视频序列头 + 两个关键帧）。</summary>
    /// <returns>FLV 字节流。</returns>
    private static byte[] BuildFlvStream()
    {
        List<byte> buffer = [.. FlvTestData.BuildFileHeader()];
        buffer.AddRange(FlvTestData.BuildTag(FlvTagType.Video, 0, FlvTestData.BuildAvcSequenceHeaderPayload()));
        buffer.AddRange(FlvTestData.BuildTag(FlvTagType.Video, 0, FlvTestData.BuildAvcNaluPayload(isKeyFrame: true)));
        buffer.AddRange(FlvTestData.BuildTag(FlvTagType.Video, 1_000, FlvTestData.BuildAvcNaluPayload(isKeyFrame: true)));
        return [.. buffer];
    }
}
