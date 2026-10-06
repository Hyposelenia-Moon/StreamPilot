namespace StreamPilot.Recording.Flv;

using StreamPilot.Core.Configuration;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Recording.Streams;

/// <summary>
/// FLV 原始流录制器：按标签搬运字节，按关键帧分片，断流后自动重连。
/// </summary>
/// <remarks>
/// 严格约束（docs/adr/0004-raw-recording.md）：
/// <list type="bullet">
///   <item>不转码、不重新编码，视频/音频载荷逐字节原样写入；</item>
///   <item>唯一允许的改写是"分片起始处的 FLV 文件头 + 序列头复制 + 时间戳重定基"；</item>
///   <item>断流重连后若检测到编码参数变化（AVC/AAC sequence header 内容不同），必须切分新分片。</item>
/// </list>
/// </remarks>
public sealed class FlvStreamRecorder
{
    /// <summary>默认的最长录制时长（分钟，8 小时；与 <see cref="RecordingLimits.DefaultMaxRecordingMinutes"/> 一致）。</summary>
    public const int DefaultMaxDurationMinutes = RecordingLimits.DefaultMaxRecordingMinutes;

    /// <summary>默认重连退避序列（秒），超出长度后复用最后一项。</summary>
    public static IReadOnlyList<int> DefaultReconnectBackoffSeconds { get; } = [2, 4, 8, 15, 30];

    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Recording.Flv";
    private readonly SegmentPolicy _policy;
    private readonly DateTimeOffset _startedAt;
    private readonly int[] _reconnectBackoffSeconds;

    /// <summary>初始化 FLV 录制器。</summary>
    /// <param name="segmentPolicy">分片策略。</param>
    /// <param name="startedAt">录制开始时间。</param>
    /// <param name="logger">结构化日志。</param>
    /// <param name="reconnectBackoffSeconds">
    /// 重连退避序列（秒）；为 <see langword="null"/> 或空时使用 <see cref="DefaultReconnectBackoffSeconds"/>。
    /// 自动化测试可传入全零序列，从而在不等候真实退避的前提下覆盖重连预算逻辑。
    /// </param>
    public FlvStreamRecorder(
        SegmentPolicy segmentPolicy,
        DateTimeOffset startedAt,
        IStructuredLogger logger,
        IReadOnlyList<int>? reconnectBackoffSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(segmentPolicy);
        ArgumentNullException.ThrowIfNull(logger);
        _policy = segmentPolicy;
        _startedAt = startedAt;
        _logger = logger;
        _reconnectBackoffSeconds = reconnectBackoffSeconds is { Count: > 0 }
            ? [.. reconnectBackoffSeconds]
            : [.. DefaultReconnectBackoffSeconds];
    }

    /// <summary>
    /// 执行一次完整录制（会话级，内部包含断流重连）。
    /// </summary>
    /// <param name="source">流数据源。</param>
    /// <param name="room">房间信息。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="format">本次录制的流格式（决定文件扩展名）。</param>
    /// <param name="maxDurationMinutes">最长录制时长（分钟）。</param>
    /// <param name="stallTimeoutSeconds">无数据到达判定断流的秒数。</param>
    /// <param name="maxReconnectAttempts">最大重连次数。</param>
    /// <param name="onSegmentCompleted">分片完成回调，可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>录制结果。</returns>
    /// <exception cref="Core.Errors.RecordingException">首次连接失败且无法重试时抛出。</exception>
    public async Task<FlvRecordingResult> RecordAsync(
        IStreamSource source,
        ResolvedRoom room,
        string outputDirectory,
        StreamFormat format,
        int maxDurationMinutes,
        int stallTimeoutSeconds,
        int maxReconnectAttempts,
        Action<RecordingSegment>? onSegmentCompleted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);

        SessionState session = new() { RoomId = room.RoomId, FileExtension = RecordingFileNaming.ExtensionFor(format) };
        long safeMaxDurationMs = Math.Max(1, maxDurationMinutes) * (long)RecordingLimits.MillisecondsPerMinute;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 打开动作在重连预算内循环重试：只有真正连上（或预算耗尽抛错）才会返回，
                // 因此此处取到的必然是可用流，不会因"首帧未连上"而误判 Failed。
                await TryOpenSourceAsync(source, session, maxReconnectAttempts, cancellationToken).ConfigureAwait(false);
                session.BeginConnection();

                FlvTagReader reader = new(GetCurrentStream(session));
                while (true)
                {
                    if (session.SessionDurationMs >= safeMaxDurationMs)
                    {
                        _logger.Info(_moduleName, "达到最长录制时长，停止录制。", new Dictionary<string, object?>
                        {
                            ["roomId"] = room.RoomId,
                            ["maxDurationMinutes"] = maxDurationMinutes,
                        });
                        return await FinishAsync(session, room, RecordingStopReason.DurationLimitReached, onSegmentCompleted, cancellationToken).ConfigureAwait(false);
                    }

                    FlvTag? tag;
                    try
                    {
                        tag = await ReadWithStallTimeoutAsync(reader, stallTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException exception)
                    {
                        _logger.Warn(_moduleName, "直播流读取中断。", new Dictionary<string, object?>
                        {
                            ["roomId"] = room.RoomId,
                            ["segmentIndex"] = session.SegmentIndex,
                            ["detail"] = exception.Message,
                        });
                        break;
                    }

                    if (tag is null)
                    {
                        break;
                    }

                    await HandleTagAsync(session, tag, room, outputDirectory, onSegmentCompleted, cancellationToken).ConfigureAwait(false);
                }

                session.CurrentStream = null;
                await source.DisposeAsync().ConfigureAwait(false);

                if (session.SessionDurationMs >= safeMaxDurationMs)
                {
                    return await FinishAsync(session, room, RecordingStopReason.DurationLimitReached, onSegmentCompleted, cancellationToken).ConfigureAwait(false);
                }

                if (session.ReconnectCount >= maxReconnectAttempts)
                {
                    _logger.Warn(_moduleName, "重连次数达到上限，结束录制。", new Dictionary<string, object?>
                    {
                        ["roomId"] = room.RoomId,
                        ["reconnectCount"] = session.ReconnectCount,
                    });
                    return await FinishAsync(session, room, RecordingStopReason.ReconnectLimitReached, onSegmentCompleted, cancellationToken).ConfigureAwait(false);
                }

                session.ReconnectCount++;
                await DelayAsync(session.ReconnectCount, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return await FinishAsync(session, room, RecordingStopReason.UserStopped, onSegmentCompleted, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Core.Errors.RecordingException)
        {
            return await FinishAsync(session, room, RecordingStopReason.Failed, onSegmentCompleted, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DelayAsync(int reconnectCount, CancellationToken cancellationToken)
    {
        int index = Math.Clamp(reconnectCount - 1, 0, _reconnectBackoffSeconds.Length - 1);
        await Task.Delay(TimeSpan.FromSeconds(_reconnectBackoffSeconds[index]), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FlvTag?> ReadWithStallTimeoutAsync(
        FlvTagReader reader,
        int stallTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, stallTimeoutSeconds)));
        try
        {
            return await reader.ReadTagAsync(stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static Stream GetCurrentStream(SessionState session)
    {
        return session.CurrentStream
            ?? throw new Core.Errors.RecordingException(
                Core.Errors.RecordingErrorCategory.MalformedStream,
                "直播流未连接。");
    }

    /// <summary>
    /// 在重连预算内反复尝试建立连接，直到成功取到可读流或预算耗尽。
    /// </summary>
    /// <param name="source">流数据源。</param>
    /// <param name="session">会话状态。</param>
    /// <param name="maxReconnectAttempts">最大重连次数（不含首次连接）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="Core.Errors.RecordingException">重连预算耗尽仍无法连接时抛出。</exception>
    private async Task TryOpenSourceAsync(
        IStreamSource source,
        SessionState session,
        int maxReconnectAttempts,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                session.CurrentStream = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Core.Errors.RecordingException exception)
            {
                if (session.ReconnectCount >= maxReconnectAttempts)
                {
                    _logger.LogError(LogLevel.Error, _moduleName, "连接直播流失败且重连次数已用尽。", exception, new Dictionary<string, object?>
                    {
                        ["roomId"] = session.RoomId,
                        ["reconnectCount"] = session.ReconnectCount,
                    });
                    throw;
                }

                session.ReconnectCount++;
                _logger.Warn(_moduleName, "连接直播流失败，准备重连。", new Dictionary<string, object?>
                {
                    ["roomId"] = session.RoomId,
                    ["reconnectCount"] = session.ReconnectCount,
                    ["detail"] = exception.Message,
                });
                await DelayAsync(session.ReconnectCount, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleTagAsync(
        SessionState session,
        FlvTag tag,
        ResolvedRoom room,
        string outputDirectory,
        Action<RecordingSegment>? onSegmentCompleted,
        CancellationToken cancellationToken)
    {
        if (tag.Type == FlvTagType.Script)
        {
            session.Writer?.RememberScriptTag(tag);
            return;
        }

        if (tag.IsSequenceHeader())
        {
            bool encodingChanged = HandleSequenceHeader(session, tag);
            if (encodingChanged && session.Writer is not null)
            {
                _logger.Info(_moduleName, "编码参数变化，切分新分片。", new Dictionary<string, object?>
                {
                    ["roomId"] = room.RoomId,
                    ["segmentIndex"] = session.SegmentIndex,
                });
                await RollSegmentAsync(session, onSegmentCompleted, cancellationToken).ConfigureAwait(false);
            }

            await EnsureWriterAsync(session, room, outputDirectory, tag.TimestampMs, cancellationToken).ConfigureAwait(false);
            return;
        }

        await EnsureWriterAsync(session, room, outputDirectory, tag.TimestampMs, cancellationToken).ConfigureAwait(false);

        if (tag.IsVideoKeyFrame() && session.Writer is not null && _policy.ShouldSplit(session.Writer.BytesWritten, tag.TimestampMs))
        {
            await RollSegmentAsync(session, onSegmentCompleted, cancellationToken).ConfigureAwait(false);
            await EnsureWriterAsync(session, room, outputDirectory, tag.TimestampMs, cancellationToken).ConfigureAwait(false);
        }

        if (session.Writer is null)
        {
            return;
        }

        await session.Writer.WriteTagAsync(tag, cancellationToken).ConfigureAwait(false);
        session.AdvanceMediaDuration(tag.TimestampMs);
    }

    /// <summary>
    /// 确保当前分片写入器存在；新建时写入文件头与缓存的脚本/序列头。
    /// </summary>
    /// <param name="session">会话状态。</param>
    /// <param name="room">房间信息。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="firstDataTimestampMs">本分片首个待写入标签的时间戳（分片基准）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task EnsureWriterAsync(
        SessionState session,
        ResolvedRoom room,
        string outputDirectory,
        int firstDataTimestampMs,
        CancellationToken cancellationToken)
    {
        if (session.Writer is not null)
        {
            return;
        }

        FlvSegmentWriter writer = CreateWriter(session, room, outputDirectory);
        writer.BeginSegment(firstDataTimestampMs);
        await writer.WriteSequenceHeadersAsync(session.VideoHeader, session.AudioHeader, cancellationToken).ConfigureAwait(false);
        session.Writer = writer;
    }

    private static bool HandleSequenceHeader(SessionState session, FlvTag tag)
    {
        if (tag.Type == FlvTagType.Video)
        {
            bool changed = session.VideoHeader is not null && !session.VideoHeader.HasSamePayload(tag);
            session.VideoHeader = tag;
            return changed;
        }

        bool audioChanged = session.AudioHeader is not null && !session.AudioHeader.HasSamePayload(tag);
        session.AudioHeader = tag;
        return audioChanged;
    }

    private static async Task RollSegmentAsync(
        SessionState session,
        Action<RecordingSegment>? onSegmentCompleted,
        CancellationToken cancellationToken)
    {
        if (session.Writer is null)
        {
            return;
        }

        RecordingSegment segment = await session.Writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        session.Segments.Add(segment);
        onSegmentCompleted?.Invoke(segment);
        await session.Writer.DisposeAsync().ConfigureAwait(false);
        session.Writer = null;
    }

    private FlvSegmentWriter CreateWriter(SessionState session, ResolvedRoom room, string outputDirectory)
    {
        string fileName = RecordingFileNaming.BuildFileName(room.Platform, room.Anchor, room.RoomId, _startedAt, session.SegmentIndex, session.FileExtension);
        string path = RecordingFileNaming.ResolveUniquePath(outputDirectory, fileName);
        session.SegmentIndex++;
        return new FlvSegmentWriter(path, _policy, _logger);
    }

    private static async Task<FlvRecordingResult> FinishAsync(
        SessionState session,
        ResolvedRoom room,
        RecordingStopReason stopReason,
        Action<RecordingSegment>? onSegmentCompleted,
        CancellationToken cancellationToken)
    {
        _ = room;
        await RollSegmentAsync(session, onSegmentCompleted, cancellationToken).ConfigureAwait(false);

        long totalBytes = 0;
        foreach (RecordingSegment segment in session.Segments)
        {
            totalBytes += segment.Bytes;
        }

        return new FlvRecordingResult(
            session.Segments,
            totalBytes,
            session.SessionDurationMs / 1000.0,
            session.ReconnectCount,
            stopReason);
    }

    /// <summary>一次录制会话的可变状态。</summary>
    private sealed class SessionState
    {
        /// <summary>当前录制的房间号（仅用于日志上下文）。</summary>
        public string RoomId { get; set; } = string.Empty;

        /// <summary>本次录制的文件扩展名。</summary>
        public string FileExtension { get; set; } = ".flv";

        /// <summary>当前连接的可读流。</summary>
        public Stream? CurrentStream { get; set; }

        /// <summary>当前分片写入器。</summary>
        public FlvSegmentWriter? Writer { get; set; }

        /// <summary>最近一次 AVC sequence header。</summary>
        public FlvTag? VideoHeader { get; set; }

        /// <summary>最近一次 AAC sequence header。</summary>
        public FlvTag? AudioHeader { get; set; }

        /// <summary>已完成的分片。</summary>
        public List<RecordingSegment> Segments { get; } = [];

        /// <summary>下一个分片序号。</summary>
        public int SegmentIndex { get; set; }

        /// <summary>重连次数。</summary>
        public int ReconnectCount { get; set; }

        /// <summary>本次会话已结算连接的累计媒体时长（毫秒）。</summary>
        public long CompletedMediaDurationMs { get; set; }

        /// <summary>当前连接首个数据标签的上游时间戳（毫秒）；尚未收到数据标签时为 <see langword="null"/>。</summary>
        public long? ConnectionBaseTimestampMs { get; set; }

        /// <summary>当前连接内已推进的媒体时长（毫秒），从 0 开始且单调不减。</summary>
        public long ConnectionMediaDurationMs { get; set; }

        /// <summary>
        /// 会话时长（毫秒）：已结算连接的媒体时长 + 当前连接内推进的媒体时长。
        /// </summary>
        /// <remarks>
        /// 该值由上游时间戳归一化而来（见 <see cref="AdvanceMediaDuration"/>），与上游绝对时间戳的
        /// 起点解耦：真实 CDN 的 FLV 首帧时间戳常常非 0（例如 28800100 毫秒），若不归一化会被
        /// 误当成"已录制时长"，导致刚开始录制就命中时长上限而丢数据。
        /// </remarks>
        public long SessionDurationMs => CompletedMediaDurationMs + ConnectionMediaDurationMs;

        /// <summary>
        /// 开始一次新连接：结算上一条连接的媒体时长，并重置连接内的归一化基准。
        /// </summary>
        /// <remarks>会话总时长在该调用前后保持不变（只是把"当前连接"并入"已结算"）。</remarks>
        public void BeginConnection()
        {
            CompletedMediaDurationMs += ConnectionMediaDurationMs;
            ConnectionBaseTimestampMs = null;
            ConnectionMediaDurationMs = 0;
        }

        /// <summary>
        /// 用一个上游标签时间戳推进会话媒体时长。
        /// </summary>
        /// <param name="timestampMs">上游标签时间戳（毫秒）。</param>
        /// <remarks>
        /// 首个数据标签只确定本连接的基准（本连接媒体时长从 0 开始）；
        /// 时间戳回跳（含重连后归零）时整体平移基准，使媒体时长保持单调不减且不重复计时。
        /// </remarks>
        public void AdvanceMediaDuration(int timestampMs)
        {
            if (ConnectionBaseTimestampMs is null)
            {
                ConnectionBaseTimestampMs = timestampMs;
                return;
            }

            long offsetMs = timestampMs - ConnectionBaseTimestampMs.Value;
            if (offsetMs >= ConnectionMediaDurationMs)
            {
                ConnectionMediaDurationMs = offsetMs;
                return;
            }

            ConnectionBaseTimestampMs = timestampMs - ConnectionMediaDurationMs;
        }
    }
}

/// <summary>
/// FLV 录制结果。
/// </summary>
/// <param name="Segments">分片列表。</param>
/// <param name="TotalBytes">写入的总字节数。</param>
/// <param name="DurationSeconds">录制时长（秒）。</param>
/// <param name="ReconnectCount">重连次数。</param>
/// <param name="StopReason">停止原因。</param>
public sealed record FlvRecordingResult(
    IReadOnlyList<RecordingSegment> Segments,
    long TotalBytes,
    double DurationSeconds,
    int ReconnectCount,
    RecordingStopReason StopReason);
