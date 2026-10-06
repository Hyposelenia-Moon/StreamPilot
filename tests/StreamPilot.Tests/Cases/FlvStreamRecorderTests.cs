namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Configuration;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Recording;
using StreamPilot.Recording.Flv;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// <see cref="FlvStreamRecorder"/> 的会话时长口径与重连预算测试（不依赖网络与真实等待）。
/// </summary>
/// <remarks>
/// 用例通过 <see cref="ScriptedStreamSource"/> 注入脚本化的连接序列与内存 FLV 字节流，
/// 并用全零退避序列构造录制器，因此可在毫秒级完成"重连重试"路径的覆盖。
/// </remarks>
[TestClass]
public sealed class FlvStreamRecorderTests
{
    /// <summary>真实 CDN 常见的首帧时间戳：8 小时 + 100 毫秒（旧实现会立刻判"已录满 8 小时"）。</summary>
    private const int NonZeroInitialTimestampMs = 28_800_100;

    /// <summary>模拟媒体的时间步长（毫秒）。</summary>
    private const int MediaStepMs = 1_000;

    /// <summary>每分钟的秒数（用于把毫秒配置换算成秒级断言期望值）。</summary>
    private const int SecondsPerMinute = 60;

    /// <summary>无数据到达的判定秒数（内存流不会阻塞，仅为满足参数校验）。</summary>
    private const int StallTimeoutSeconds = 10;

    /// <summary>全零退避：让重连用例不产生真实等待。</summary>
    private static readonly int[] InstantBackoffSeconds = [0, 0, 0, 0, 0];

    /// <summary>测试房间（仅用于命名与日志上下文）。</summary>
    private static readonly ResolvedRoom Room = new()
    {
        Platform = PlatformId.Bilibili,
        RoomId = "123456",
        Anchor = "测试主播",
        Title = "测试直播间",
        Candidates = [],
        ResolvedAt = DateTimeOffset.Now,
    };

    /// <summary>首帧时间戳非 0 时必须按媒体时间计时（10 秒录制报告 10 秒）。</summary>
    [TestMethod("FLV 录制：首帧时间戳非 0 时按媒体时间计时")]
    public async Task MeasuresSessionDurationFromMediaTime()
    {
        using TempDirectory temp = new();
        ScriptedStreamSource source = new ScriptedStreamSource().ThenStream(BuildMediaStream(NonZeroInitialTimestampMs, seconds: 10));

        FlvRecordingResult result = await RecordAsync(source, temp.Path, maxDurationMinutes: 480, maxReconnectAttempts: 0);

        Assert.EqualDouble(10.0, result.DurationSeconds, 0.001, "10 秒媒体必须报告 10 秒，而不是 28800100 毫秒的绝对时间戳");
        Assert.True(result.TotalBytes > 0, "录制必须真的写出数据");
    }

    /// <summary>上游绝对时间戳已超过 8 小时也不得触发时长上限（否则第二个标签就停止录制）。</summary>
    [TestMethod("FLV 录制：绝对时间戳超过 8 小时不触发限时")]
    public async Task DoesNotReachDurationLimitFromAbsoluteTimestamp()
    {
        using TempDirectory temp = new();
        ScriptedStreamSource source = new ScriptedStreamSource().ThenStream(BuildMediaStream(NonZeroInitialTimestampMs, seconds: 10));

        FlvRecordingResult result = await RecordAsync(source, temp.Path, maxDurationMinutes: 480, maxReconnectAttempts: 0);

        Assert.False(
            result.StopReason == RecordingStopReason.DurationLimitReached,
            "首帧时间戳 28800100 毫秒（8 小时 + 100 毫秒）不得被当成已录制时长");
        Assert.Equal(RecordingStopReason.ReconnectLimitReached, result.StopReason);
    }

    /// <summary>正常语义不变：媒体时间累计满 8 小时才触发时长上限。</summary>
    [TestMethod("FLV 录制：媒体时间累计满 8 小时触发限时")]
    public async Task ReachesDurationLimitAtEightHours()
    {
        long eightHoursMs = 8L * RecordingLimits.MinutesPerHour * RecordingLimits.MillisecondsPerMinute;
        double eightHoursSeconds = 8.0 * RecordingLimits.MinutesPerHour * SecondsPerMinute;
        using TempDirectory temp = new();
        ScriptedStreamSource source = new ScriptedStreamSource().ThenStream(BuildTagStream(
            (FlvTagType.Video, 0, FlvTestData.BuildAvcSequenceHeaderPayload()),
            (FlvTagType.Video, 0, FlvTestData.BuildAvcNaluPayload(isKeyFrame: true)),
            (FlvTagType.Video, (int)eightHoursMs, FlvTestData.BuildAvcNaluPayload(isKeyFrame: true))));

        FlvRecordingResult result = await RecordAsync(source, temp.Path, maxDurationMinutes: 480, maxReconnectAttempts: 1);

        Assert.Equal(RecordingStopReason.DurationLimitReached, result.StopReason);
        Assert.EqualDouble(eightHoursSeconds, result.DurationSeconds, 0.001, "8 小时媒体时间必须触发时长上限");
    }

    /// <summary>重连后时间戳归零时必须继续累计，而不是把会话时长清零。</summary>
    [TestMethod("FLV 录制：重连后时间戳归零继续累计")]
    public async Task KeepsAccumulatingAcrossReconnect()
    {
        using TempDirectory temp = new();
        ScriptedStreamSource source = new ScriptedStreamSource()
            .ThenStream(BuildMediaStream(startTimestampMs: 10_000, seconds: 10))
            .ThenStream(BuildMediaStream(startTimestampMs: 0, seconds: 5));

        FlvRecordingResult result = await RecordAsync(source, temp.Path, maxDurationMinutes: 480, maxReconnectAttempts: 1);

        Assert.Equal(2, source.OpenCount, "断流后必须重新建立连接");
        Assert.Equal(1, result.ReconnectCount);
        Assert.EqualDouble(15.0, result.DurationSeconds, 0.001, "重连归零后必须继续累计（10 秒 + 5 秒）");
    }

    /// <summary>时间戳回跳不得重复计时（媒体时长保持单调不减）。</summary>
    [TestMethod("FLV 录制：时间戳回跳不重复计时")]
    public async Task IgnoresBackwardsTimestampJump()
    {
        using TempDirectory temp = new();
        ScriptedStreamSource source = new ScriptedStreamSource().ThenStream(BuildTagStream(
            (FlvTagType.Video, 5_000, FlvTestData.BuildAvcSequenceHeaderPayload()),
            (FlvTagType.Video, 5_000, FlvTestData.BuildAvcNaluPayload(isKeyFrame: true)),
            (FlvTagType.Video, 12_000, FlvTestData.BuildAvcNaluPayload(isKeyFrame: true)),
            (FlvTagType.Video, 9_000, FlvTestData.BuildAvcNaluPayload(isKeyFrame: true)),
            (FlvTagType.Video, 9_500, FlvTestData.BuildAvcNaluPayload(isKeyFrame: true))));

        FlvRecordingResult result = await RecordAsync(source, temp.Path, maxDurationMinutes: 480, maxReconnectAttempts: 0);

        Assert.EqualDouble(7.5, result.DurationSeconds, 0.001, "回跳到 9000 毫秒不得把进度倒退回 4000 毫秒");
    }

    /// <summary>首次连接失败必须在重连预算内重试，成功后才开始录制。</summary>
    [TestMethod("FLV 录制：首次连接失败后重试成功")]
    public async Task RetriesFirstOpenUntilSuccess()
    {
        using TempDirectory temp = new();
        ScriptedStreamSource source = new ScriptedStreamSource()
            .ThenFailure()
            .ThenStream(BuildMediaStream(startTimestampMs: 0, seconds: 2));

        FlvRecordingResult result = await RecordAsync(source, temp.Path, maxDurationMinutes: 480, maxReconnectAttempts: 1);

        Assert.Equal(2, source.OpenCount, "首次失败后必须真正发起重连");
        Assert.Equal(1, result.ReconnectCount);
        Assert.Equal(1, result.Segments.Count, "重试成功后必须真的录到分片");
        Assert.False(result.StopReason == RecordingStopReason.Failed, "首次连接失败不得直接判 Failed");
    }

    /// <summary>断流后连接失败仍需继续重试，直到预算内连上。</summary>
    [TestMethod("FLV 录制：断流后连接失败再成功")]
    public async Task RetriesAfterStreamBreakAndOpenFailure()
    {
        using TempDirectory temp = new();
        ScriptedStreamSource source = new ScriptedStreamSource()
            .ThenStream(BuildMediaStream(startTimestampMs: 0, seconds: 3))
            .ThenFailure()
            .ThenStream(BuildMediaStream(startTimestampMs: 0, seconds: 3));

        FlvRecordingResult result = await RecordAsync(source, temp.Path, maxDurationMinutes: 480, maxReconnectAttempts: 2);

        Assert.Equal(3, source.OpenCount, "断流 + 连接失败后必须继续重试");
        Assert.Equal(RecordingStopReason.ReconnectLimitReached, result.StopReason);
        Assert.EqualDouble(6.0, result.DurationSeconds, 0.001, "两次成功连接的媒体时间都必须计入会话时长");
    }

    /// <summary>只有重连预算真正耗尽才判失败，且必须留下错误日志。</summary>
    [TestMethod("FLV 录制：重连预算耗尽才判失败")]
    public async Task FailsOnlyAfterReconnectBudgetExhausted()
    {
        using TempDirectory temp = new();
        RecordingLogger logger = new();
        ScriptedStreamSource source = new ScriptedStreamSource().ThenFailure().ThenFailure().ThenFailure();

        FlvRecordingResult result = await RecordAsync(source, temp.Path, maxDurationMinutes: 480, maxReconnectAttempts: 2, logger);

        Assert.Equal(3, source.OpenCount, "首次连接 + 2 次重试后预算才耗尽");
        Assert.Equal(2, result.ReconnectCount);
        Assert.Equal(RecordingStopReason.Failed, result.StopReason);
        Assert.True(logger.HasLevel(LogLevel.Error), "预算耗尽判失败必须记录错误日志");
    }

    /// <summary>执行一次录制（全零退避，避免测试等待）。</summary>
    /// <param name="source">脚本化数据源。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="maxDurationMinutes">最长录制时长（分钟）。</param>
    /// <param name="maxReconnectAttempts">最大重连次数。</param>
    /// <param name="logger">可选日志替身。</param>
    /// <returns>录制结果。</returns>
    private static Task<FlvRecordingResult> RecordAsync(
        ScriptedStreamSource source,
        string outputDirectory,
        int maxDurationMinutes,
        int maxReconnectAttempts,
        IStructuredLogger? logger = null)
    {
        FlvStreamRecorder recorder = new(
            new SegmentPolicy(),
            DateTimeOffset.Now,
            logger ?? NullStructuredLogger.Instance,
            InstantBackoffSeconds);

        return recorder.RecordAsync(
            source,
            Room,
            outputDirectory,
            StreamFormat.FlvHttp,
            maxDurationMinutes,
            StallTimeoutSeconds,
            maxReconnectAttempts,
            onSegmentCompleted: null,
            CancellationToken.None);
    }

    /// <summary>构造一段媒体流：序列头 + 每秒一个关键帧，共 <paramref name="seconds"/> 秒媒体时间。</summary>
    /// <param name="startTimestampMs">首帧的上游时间戳（毫秒）。</param>
    /// <param name="seconds">媒体时长（秒）。</param>
    /// <returns>完整 FLV 字节流。</returns>
    private static byte[] BuildMediaStream(int startTimestampMs, int seconds)
    {
        List<(FlvTagType Type, int TimestampMs, byte[] Payload)> tags =
        [
            (FlvTagType.Video, startTimestampMs, FlvTestData.BuildAvcSequenceHeaderPayload()),
        ];

        for (int second = 0; second <= seconds; second++)
        {
            tags.Add((FlvTagType.Video, startTimestampMs + (second * MediaStepMs), FlvTestData.BuildAvcNaluPayload(isKeyFrame: true)));
        }

        return BuildTagStream([.. tags]);
    }

    /// <summary>把 FLV 文件头与若干标签拼成完整字节流。</summary>
    /// <param name="tags">标签序列。</param>
    /// <returns>完整 FLV 字节流。</returns>
    private static byte[] BuildTagStream(params (FlvTagType Type, int TimestampMs, byte[] Payload)[] tags)
    {
        List<byte> buffer = [.. FlvTestData.BuildFileHeader()];
        foreach ((FlvTagType type, int timestampMs, byte[] payload) in tags)
        {
            buffer.AddRange(FlvTestData.BuildTag(type, timestampMs, payload));
        }

        return [.. buffer];
    }
}
