namespace StreamPilot.Tests.Cases;

using System.Diagnostics;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Recording;
using StreamPilot.Recording.Flv;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// <see cref="RecordingSession" /> 的实时进度测试：录制**进行中**就必须能读到时长与字节。
/// </summary>
/// <remarks>
/// 状态字段原先只在分片完成后结算，而分片上限默认 10 GB / 480 分钟——整场录制期间界面会一直显示
/// "录制中 00:00:00 · 0.0 MiB"，用户无法判断到底有没有在写。这里用回环服务器慢速下发 FLV 流，
/// 在录制未结束时采样状态，确保进度来自录制器回报而不是分片结算。
/// </remarks>
[TestClass]
public sealed class RecordingSessionProgressTests
{
    /// <summary>FLV 流路径。</summary>
    private const string StreamPath = "/live/stream.flv";

    /// <summary>每个分块下发的字节数。</summary>
    private const int ChunkBytes = 64;

    /// <summary>相邻分块的间隔（毫秒）。</summary>
    private const int ChunkIntervalMs = 10;

    /// <summary>正文写完后保持连接的毫秒数（保证采样时录制仍未结束）。</summary>
    private const int TrailingHoldMs = 3_000;

    /// <summary>无数据到达判定秒数（须大于保持时间，避免测试期间被判停滞）。</summary>
    private const int StallTimeoutSeconds = 10;

    /// <summary>采样等待上限。</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>媒体时长：5 个关键帧、间隔 500 毫秒。</summary>
    private const int MediaDurationMs = 2_000;

    /// <summary>录制进行中就能读到已录时长与已写字节。</summary>
    [TestMethod("录制会话：录制中就能读到已录时长与已写字节")]
    public async Task ReportsLiveProgressWhileRecording()
    {
        using TempDirectory temp = new();
        using LoopbackHttpServer server = new();
        server.ServePacedBytes(StreamPath, BuildStream(), ChunkBytes, ChunkIntervalMs, TrailingHoldMs);
        RecordingLogger logger = new();
        using HttpClientFactory clients = new(new NetworkOptions(), logger);

        await using RecordingSession session = await RecordingSession.StartAsync(
            BuildRequest(server.AbsoluteUri(StreamPath), temp.Path),
            clients,
            StallTimeoutSeconds,
            maxReconnectAttempts: 1,
            logger).ConfigureAwait(false);

        TimeSpan expectedDuration = TimeSpan.FromMilliseconds(MediaDurationMs);
        RecordingStatus live = session.Status;
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < WaitTimeout && (live.TotalBytes <= 0 || live.Duration < expectedDuration))
        {
            await Task.Delay(10).ConfigureAwait(false);
            live = session.Status;
        }

        Assert.True(live.IsRecording, "采样时录制应仍在进行（否则测到的是分片结算值，不是实时进度）");
        Assert.True(live.TotalBytes > 0, "录制进行中必须能看到已写字节，而不是 0");
        Assert.True(
            live.Duration >= expectedDuration,
            $"录制进行中必须能看到已录时长（期望至少 {MediaDurationMs} 毫秒，实际 {live.Duration.TotalMilliseconds:F0} 毫秒）");

        await session.StopAsync(RecordingStopReason.UserStopped, CancellationToken.None).ConfigureAwait(false);
        Assert.False(session.Status.IsRecording, "停止后状态必须变为未录制");
    }

    /// <summary>构造一段最小合法 FLV 流：脚本标签 + 两路序列头 + 5 个关键帧。</summary>
    /// <returns>完整 FLV 字节流。</returns>
    private static byte[] BuildStream()
    {
        List<byte> buffer = [.. FlvTestData.BuildFileHeader()];
        buffer.AddRange(FlvTestData.BuildTag(FlvTagType.Script, 0, FlvTestData.BuildScriptPayload()));
        buffer.AddRange(FlvTestData.BuildTag(FlvTagType.Video, 0, FlvTestData.BuildAvcSequenceHeaderPayload()));
        buffer.AddRange(FlvTestData.BuildTag(FlvTagType.Audio, 0, FlvTestData.BuildAacSequenceHeaderPayload()));
        for (int index = 0; index < 5; index++)
        {
            int timestampMs = index * (MediaDurationMs / 4);
            buffer.AddRange(FlvTestData.BuildTag(FlvTagType.Video, timestampMs, FlvTestData.BuildAvcNaluPayload(isKeyFrame: true)));
        }

        return [.. buffer];
    }

    /// <summary>构造录制请求（单条 FLV 候选）。</summary>
    /// <param name="url">流地址。</param>
    /// <param name="outputRoot">输出根目录。</param>
    /// <returns>录制请求。</returns>
    private static RecordingRequest BuildRequest(string url, string outputRoot)
    {
        StreamCandidate candidate = new()
        {
            SourceIndex = 0,
            Url = url,
            Format = StreamFormat.FlvHttp,
            CdnHost = "127.0.0.1",
            Codec = VideoCodec.Avc,
            Quality = StreamQuality.Hd1080,
            UrlFingerprint = "progress-test",
        };
        return new RecordingRequest
        {
            Candidate = candidate,
            OutputDirectory = outputRoot,
            Room = new ResolvedRoom
            {
                Platform = PlatformId.Bilibili,
                RoomId = "123456",
                Anchor = "测试主播",
                Title = "测试直播间",
                Candidates = [candidate],
                ResolvedAt = DateTimeOffset.Now,
            },
        };
    }
}
