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

    /// <summary>FLV 收尾（关闭末分片并登记元数据）的超时秒数。</summary>
    /// <remarks>
    /// 收尾必须用**独立且有界**的令牌：录制被取消时上层令牌已经取消，若继续沿用它会在 flush 之前
    /// 立刻抛 <see cref="OperationCanceledException"/>，导致末分片登记不上、文件句柄不释放（SP-03）；
    /// 而改用"永不过期"的 <see cref="CancellationToken.None"/> 又会让 flush 卡死时无限阻塞
    /// <c>RecordingSession.StopAsync</c>。本地磁盘 flush 通常在 100 毫秒内完成，10 秒留出两个数量级
    /// 余量（慢盘、杀毒软件实时扫描），同时保证停止录制时的等待一定有界。
    /// 取值与 <c>TsStreamRecorder.FinalizeTimeoutSeconds</c> 一致：两侧收尾面对的是同一类本地磁盘。
    /// </remarks>
    public const int FinalizeTimeoutSeconds = 10;

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
                        return await FinishAsync(session, RecordingStopReason.DurationLimitReached, onSegmentCompleted).ConfigureAwait(false);
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
                    return await FinishAsync(session, RecordingStopReason.DurationLimitReached, onSegmentCompleted).ConfigureAwait(false);
                }

                if (session.ReconnectCount >= maxReconnectAttempts)
                {
                    _logger.Warn(_moduleName, "重连次数达到上限，结束录制。", new Dictionary<string, object?>
                    {
                        ["roomId"] = room.RoomId,
                        ["reconnectCount"] = session.ReconnectCount,
                    });
                    return await FinishAsync(session, RecordingStopReason.ReconnectLimitReached, onSegmentCompleted).ConfigureAwait(false);
                }

                session.ReconnectCount++;
                await DelayAsync(session.ReconnectCount, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return await FinishAsync(session, RecordingStopReason.UserStopped, onSegmentCompleted).ConfigureAwait(false);
        }
        catch (Core.Errors.RecordingException)
        {
            return await FinishAsync(session, RecordingStopReason.Failed, onSegmentCompleted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 落盘 IO 异常（分片写入失败，或分片回调里刷新侧车元数据失败）：同样必须收尾，
            // 否则末分片登记不上、FLV 分片句柄一直占着文件（SP-03 的同族问题）。
            _logger.LogError(LogLevel.Error, _moduleName, "录制过程中落盘失败，已收尾并释放分片句柄。", exception, new Dictionary<string, object?>
            {
                ["roomId"] = room.RoomId,
                ["segmentIndex"] = session.SegmentIndex,
            });
            return await FinishAsync(session, RecordingStopReason.Failed, onSegmentCompleted).ConfigureAwait(false);
        }
        finally
        {
            // 兜底：任何没走到上面收尾路径的异常（例如回调抛出的非 IO 异常）都不得泄漏分片句柄。
            // 收尾后再调用这里是幂等的无操作（句柄已置空）。
            using CancellationTokenSource boundedRelease = new(TimeSpan.FromSeconds(FinalizeTimeoutSeconds));
            await session.ReleaseWriterAsync(boundedRelease.Token).ConfigureAwait(false);
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
        FlvSegmentWriter? writer = session.Writer;
        if (writer is null)
        {
            return;
        }

        // 先摘掉引用再收尾：回调（上层据此登记分片并刷新侧车元数据）一旦抛错，
        // 也不会让同一分片在随后的收尾里被重复登记。
        session.Writer = null;
        try
        {
            RecordingSegment segment = await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            session.Segments.Add(segment);
            onSegmentCompleted?.Invoke(segment);
        }
        finally
        {
            // 句柄必须无条件释放：CompleteAsync 或回调抛出时同样不能把产物锁住（SP-03 同族）。
            await writer.DisposeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private FlvSegmentWriter CreateWriter(SessionState session, ResolvedRoom room, string outputDirectory)
    {
        string fileName = RecordingFileNaming.BuildFileName(room.Platform, room.Anchor, room.RoomId, _startedAt, session.SegmentIndex, session.FileExtension);
        string path = RecordingFileNaming.ResolveUniquePath(outputDirectory, fileName);
        session.SegmentIndex++;
        return new FlvSegmentWriter(path, _policy, _logger);
    }

    /// <summary>
    /// 收尾：关闭并登记末分片，汇总录制结果。
    /// </summary>
    /// <param name="session">会话状态。</param>
    /// <param name="stopReason">停止原因。</param>
    /// <param name="onSegmentCompleted">分片完成回调。</param>
    /// <returns>录制结果。</returns>
    /// <remarks>
    /// 收尾用**独立且有界**的令牌（<see cref="FinalizeTimeoutSeconds"/>），既不受已经取消的上层令牌
    /// 影响（否则末分片登记不上，见 SP-03），也不会因为 flush 卡死而无限阻塞上层停止录制。
    /// 收尾超时或落盘失败时记 Error 日志，并把停止原因归一为 <see cref="RecordingStopReason.Failed"/>，
    /// 绝不静默吞掉；无论成败，分片句柄都由 <see cref="RollSegmentAsync"/> 的 finally 释放。
    /// </remarks>
    private async Task<FlvRecordingResult> FinishAsync(
        SessionState session,
        RecordingStopReason stopReason,
        Action<RecordingSegment>? onSegmentCompleted)
    {
        RecordingStopReason effectiveReason = stopReason;
        using CancellationTokenSource bounded = new(TimeSpan.FromSeconds(FinalizeTimeoutSeconds));
        try
        {
            await RollSegmentAsync(session, onSegmentCompleted, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            effectiveReason = RecordingStopReason.Failed;
            _logger.LogError(LogLevel.Error, _moduleName, "FLV 录制收尾超时：末分片未登记。", exception, new Dictionary<string, object?>
            {
                ["roomId"] = session.RoomId,
                ["segmentIndex"] = session.SegmentIndex,
                ["finalizeTimeoutSeconds"] = FinalizeTimeoutSeconds,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            effectiveReason = RecordingStopReason.Failed;
            _logger.LogError(LogLevel.Error, _moduleName, "FLV 录制收尾失败：末分片未能落盘。", exception, new Dictionary<string, object?>
            {
                ["roomId"] = session.RoomId,
                ["segmentIndex"] = session.SegmentIndex,
            });
        }

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
            effectiveReason);
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
        /// 释放当前分片写入器（幂等：已经收尾过则什么也不做）。
        /// </summary>
        /// <param name="cancellationToken">只约束最后一次 flush 的等待，超时也会释放文件句柄。</param>
        /// <returns>异步释放任务。</returns>
        public async Task ReleaseWriterAsync(CancellationToken cancellationToken)
        {
            FlvSegmentWriter? writer = Writer;
            Writer = null;
            if (writer is not null)
            {
                await writer.DisposeAsync(cancellationToken).ConfigureAwait(false);
            }
        }

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
