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

    /// <summary>
    /// 音频序列头先到时，首分片仍必须同时包含 onMetaData、音频与视频（HEVC/HDR）序列头。
    /// </summary>
    /// <remarks>
    /// 复审参考录像（B站 HDR，HEVC Main10 PQ）的标签顺序是 script → 音频头 → 视频头：
    /// 旧实现用音频头创建分片，随后到达的视频头既不写进文件也不补写，首分片因此缺少 hvcC，
    /// 播放器直接报 "No start code is found"、整段无法解码。
    /// </remarks>
    [TestMethod("FLV 录制：音频头先到时首分片仍含脚本与 HEVC 序列头")]
    public async Task WritesAllInitializationTagsWhenAudioHeaderArrivesFirst()
    {
        using TempDirectory temp = new();
        byte[] script = FlvTestData.BuildScriptPayload();
        byte[] audio = FlvTestData.BuildAacSequenceHeaderPayload();
        byte[] video = FlvTestData.BuildHevcSequenceHeaderPayload();
        ScriptedStreamSource source = new ScriptedStreamSource().ThenStream(BuildTagStream(
            (FlvTagType.Script, 0, script),
            (FlvTagType.Audio, 0, audio),
            (FlvTagType.Video, 0, video),
            (FlvTagType.Video, 0, FlvTestData.BuildHevcNaluPayload(isKeyFrame: true)),
            (FlvTagType.Video, MediaStepMs, FlvTestData.BuildHevcNaluPayload(isKeyFrame: true))));

        FlvRecordingResult result = await RecordAsync(source, temp.Path, maxDurationMinutes: 480, maxReconnectAttempts: 0);

        Assert.Equal(1, result.Segments.Count);
        List<FlvTag> tags = ReadFirstTags(Path.Combine(temp.Path, result.Segments[0].FileName), count: 4);
        Assert.Equal(FlvTagType.Script, tags[0].Type, "首分片必须带 onMetaData");
        Assert.SequenceEqual(script, tags[0].Payload);
        Assert.Equal(FlvTagType.Audio, tags[1].Type, "音频序列头必须写入首分片");
        Assert.SequenceEqual(audio, tags[1].Payload);
        Assert.Equal(FlvTagType.Video, tags[2].Type, "视频序列头（HEVC/hvcC）必须写入首分片");
        Assert.SequenceEqual(video, tags[2].Payload);
        Assert.True(tags[3].IsVideoKeyFrame(), "序列头之后应直接是首个关键帧");
        Assert.Equal(0, tags[3].TimestampMs, "首帧必须重定基到 0（序列头的时间戳不能先占用基准）");
    }

    /// <summary>视频序列头先到时，后到的音频序列头同样必须写入首分片。</summary>
    [TestMethod("FLV 录制：视频头先到时首分片仍含音频序列头")]
    public async Task WritesLateAudioHeaderWhenVideoHeaderArrivesFirst()
    {
        using TempDirectory temp = new();
        byte[] script = FlvTestData.BuildScriptPayload();
        byte[] audio = FlvTestData.BuildAacSequenceHeaderPayload();
        byte[] video = FlvTestData.BuildHevcSequenceHeaderPayload();
        ScriptedStreamSource source = new ScriptedStreamSource().ThenStream(BuildTagStream(
            (FlvTagType.Script, 0, script),
            (FlvTagType.Video, 0, video),
            (FlvTagType.Audio, 0, audio),
            (FlvTagType.Video, 0, FlvTestData.BuildHevcNaluPayload(isKeyFrame: true)),
            (FlvTagType.Video, MediaStepMs, FlvTestData.BuildHevcNaluPayload(isKeyFrame: true))));

        FlvRecordingResult result = await RecordAsync(source, temp.Path, maxDurationMinutes: 480, maxReconnectAttempts: 0);

        Assert.Equal(1, result.Segments.Count);
        List<FlvTag> tags = ReadFirstTags(Path.Combine(temp.Path, result.Segments[0].FileName), count: 4);
        Assert.Equal(FlvTagType.Script, tags[0].Type);
        Assert.Equal(FlvTagType.Video, tags[1].Type);
        Assert.SequenceEqual(video, tags[1].Payload);
        Assert.Equal(FlvTagType.Audio, tags[2].Type, "后到的音频序列头必须补写进首分片");
        Assert.SequenceEqual(audio, tags[2].Payload);
        Assert.True(tags[3].IsVideoKeyFrame());
    }

    /// <summary>
    /// 加入直播时，序列头可能带着"开播至今"的大时间戳：写入文件必须归零。
    /// </summary>
    /// <remarks>
    /// 实测房间 28576（B站，1080p50 AVC）加入时：视频序列头 ts=0，AAC 序列头 ts=4831206（约 80 分钟）。
    /// 旧实现把序列头的原始时间戳照原样写进分片，文件里因此出现一个 80 分钟的假时间戳，
    /// 播放器与转封装工具的时长/定位都会跟着错乱（录播姬同一路流写的是 0）。
    /// </remarks>
    [TestMethod("FLV 录制：序列头带大时间戳时按 0 写入")]
    public async Task NormalizesSequenceHeaderTimestampsToZero()
    {
        using TempDirectory temp = new();
        const int liveTimestampMs = 4_831_206;
        byte[] script = FlvTestData.BuildScriptPayload();
        byte[] audio = FlvTestData.BuildAacSequenceHeaderPayload();
        byte[] video = FlvTestData.BuildAvcSequenceHeaderPayload();
        ScriptedStreamSource source = new ScriptedStreamSource().ThenStream(BuildTagStream(
            (FlvTagType.Script, 0, script),
            (FlvTagType.Audio, liveTimestampMs, audio),
            (FlvTagType.Video, 0, video),
            (FlvTagType.Video, liveTimestampMs, FlvTestData.BuildAvcNaluPayload(isKeyFrame: true)),
            (FlvTagType.Video, liveTimestampMs + MediaStepMs, FlvTestData.BuildAvcNaluPayload(isKeyFrame: true))));

        FlvRecordingResult result = await RecordAsync(source, temp.Path, maxDurationMinutes: 480, maxReconnectAttempts: 0);

        Assert.Equal(1, result.Segments.Count);
        List<FlvTag> tags = ReadFirstTags(Path.Combine(temp.Path, result.Segments[0].FileName), count: 5);
        Assert.Equal(0, tags[1].TimestampMs, "后到的 AAC 序列头不能把直播时间戳写进文件");
        Assert.Equal(0, tags[2].TimestampMs, "视频序列头同样按 0 写入");
        Assert.Equal(0, tags[3].TimestampMs, "首个媒体帧必须以 0 为基准");
        Assert.Equal(1000, tags[4].TimestampMs, "后续帧按与首帧的相对偏移递增");
    }

    /// <summary>
    /// 录制过程中必须持续回报进度：界面不能等到分片结算才显示时长与字节。
    /// </summary>
    /// <remarks>
    /// 分片上限默认 10 GB / 480 分钟，一次直播通常只会有一个分片；若只在分片完成时结算，
    /// 整场录制的界面都会停在"00:00:00 · 0.0 MiB"。
    /// </remarks>
    [TestMethod("FLV 录制：录制过程中回报递增的字节与时长")]
    public async Task ReportsProgressWhileWriting()
    {
        using TempDirectory temp = new();
        const int startTimestampMs = 4_000_000;
        List<RecordingProgress> progress = [];
        ScriptedStreamSource source = new ScriptedStreamSource()
            .ThenStream(BuildMediaStream(startTimestampMs, seconds: 5));
        FlvStreamRecorder recorder = new(
            new SegmentPolicy(),
            DateTimeOffset.Now,
            NullStructuredLogger.Instance,
            InstantBackoffSeconds);

        await recorder.RecordAsync(
            source,
            Room,
            temp.Path,
            StreamFormat.FlvHttp,
            maxDurationMinutes: 480,
            StallTimeoutSeconds,
            maxReconnectAttempts: 0,
            onSegmentCompleted: null,
            CancellationToken.None,
            progress.Add).ConfigureAwait(false);

        Assert.True(progress.Count >= 5, "每写入一个标签都应回报一次进度");
        Assert.True(progress[^1].SegmentBytes > 0, "进度必须带当前分片已写字节");
        Assert.True(progress[^1].SessionDurationMs >= 5_000, "进度必须带会话媒体时长");
        for (int index = 1; index < progress.Count; index++)
        {
            Assert.True(progress[index].SegmentBytes >= progress[index - 1].SegmentBytes, "已写字节必须单调不减");
            Assert.True(progress[index].SessionDurationMs >= progress[index - 1].SessionDurationMs, "媒体时长必须单调不减");
        }
    }

    /// <summary>切分出的每个新分片都必须重新写入 onMetaData 与两路序列头。</summary>
    [TestMethod("FLV 录制：切分后的新分片同样带脚本与序列头")]
    public async Task WritesInitializationTagsToEverySegment()
    {
        using TempDirectory temp = new();
        byte[] script = FlvTestData.BuildScriptPayload();
        byte[] audio = FlvTestData.BuildAacSequenceHeaderPayload();
        byte[] video = FlvTestData.BuildHevcSequenceHeaderPayload();
        ScriptedStreamSource source = new ScriptedStreamSource().ThenStream(BuildTagStream(
            (FlvTagType.Script, 0, script),
            (FlvTagType.Audio, 0, audio),
            (FlvTagType.Video, 0, video),
            (FlvTagType.Video, 0, FlvTestData.BuildHevcNaluPayload(isKeyFrame: true)),
            (FlvTagType.Video, 70_000, FlvTestData.BuildHevcNaluPayload(isKeyFrame: true))));
        SegmentPolicy policy = new(new SegmentPolicyOptions
        {
            MaxDurationMinutes = 1,
            MinBytes = 0,
        });

        FlvRecordingResult result = await RecordAsync(
            source,
            temp.Path,
            maxDurationMinutes: 480,
            maxReconnectAttempts: 0,
            policy: policy);

        Assert.Equal(2, result.Segments.Count, "超过 1 分钟必须在关键帧处切分");
        List<FlvTag> tags = ReadFirstTags(Path.Combine(temp.Path, result.Segments[1].FileName), count: 4);
        Assert.Equal(FlvTagType.Script, tags[0].Type, "新分片必须带 onMetaData");
        Assert.SequenceEqual(script, tags[0].Payload);
        // WriteSequenceHeadersAsync 的写出顺序固定为 脚本 → 视频 → 音频；两路头都在时以该顺序落盘。
        Assert.Equal(FlvTagType.Video, tags[1].Type);
        Assert.SequenceEqual(video, tags[1].Payload);
        Assert.Equal(FlvTagType.Audio, tags[2].Type);
        Assert.SequenceEqual(audio, tags[2].Payload);
    }

    /// <summary>读取文件开头的若干个标签。</summary>
    /// <param name="path">FLV 文件路径。</param>
    /// <param name="count">最多读取的标签数量。</param>
    /// <returns>解析出的标签。</returns>
    private static List<FlvTag> ReadFirstTags(string path, int count)
    {
        using FileStream stream = File.OpenRead(path);
        FlvTagReader reader = new(stream);
        List<FlvTag> tags = [];
        for (int index = 0; index < count; index++)
        {
            FlvTag? tag = reader.ReadTagAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (tag is null)
            {
                break;
            }

            tags.Add(tag);
        }

        return tags;
    }

    /// <summary>执行一次录制（全零退避，避免测试等待）。</summary>
    /// <param name="source">脚本化数据源。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="maxDurationMinutes">最长录制时长（分钟）。</param>
    /// <param name="maxReconnectAttempts">最大重连次数。</param>
    /// <param name="logger">可选日志替身。</param>
    /// <param name="policy">分片策略；为 <see langword="null"/> 时使用默认策略。</param>
    /// <returns>录制结果。</returns>
    private static Task<FlvRecordingResult> RecordAsync(
        ScriptedStreamSource source,
        string outputDirectory,
        int maxDurationMinutes,
        int maxReconnectAttempts,
        IStructuredLogger? logger = null,
        SegmentPolicy? policy = null)
    {
        FlvStreamRecorder recorder = new(
            policy ?? new SegmentPolicy(),
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
