namespace StreamPilot.Recording.Hls;

using System.Net;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Recording.Flv;

/// <summary>
/// HLS/TS 原始流录制器：按分片拉取 MPEG-TS 并直接追加 188 字节包流。
/// </summary>
/// <remarks>
/// 与 FLV 录制器相同，绝不转码：TS 分片本身可无缝拼接，因此只做
/// "拉取分片 → 去掉不足一个 TS 包的尾部残留 → 直接写文件"。
/// 分片边界与 m3u8 的分片对齐，天然满足"按大小/时长切分"的要求。
/// </remarks>
public sealed class TsStreamRecorder
{
    /// <summary>MPEG-TS 包长度。</summary>
    public const int TsPacketSize = 188;

    /// <summary>MPEG-TS 同步字节。</summary>
    public const byte TsSyncByte = 0x47;

    /// <summary>直播播放列表的刷新间隔（秒）。</summary>
    public const int PlaylistRefreshSeconds = 10;

    /// <summary>收尾（关闭末分片并登记元数据）的超时秒数。</summary>
    /// <remarks>
    /// 收尾必须使用独立且有界的令牌：取消录制时上层令牌已经取消，若继续沿用会在 flush 之前
    /// 立刻抛 <see cref="OperationCanceledException"/>，导致末分片登记不上、文件句柄不释放（SP-03）。
    /// 本地磁盘 flush 通常在 100 毫秒以内完成，10 秒留出两个数量级余量（慢盘、杀毒软件实时扫描），
    /// 同时保证停止录制时的等待一定有界。
    /// </remarks>
    public const int FinalizeTimeoutSeconds = 10;

    /// <summary>默认的"上游无数据"停滞判定秒数（与 FLV 录制保持一致）。</summary>
    public const int DefaultStallTimeoutSeconds = 12;

    /// <summary>默认的重连/重试上限（配置未指定时使用）。</summary>
    /// <remarks>
    /// 与会话侧的 <c>RecordingOptions.MaxReconnectAttempts</c> 同义：分片下载失败、播放列表刷新失败
    /// 都共用这一份预算，用尽即结束录制并给出明确原因（不再把失败记成"自然下播"）。
    /// </remarks>
    public const int DefaultMaxReconnectAttempts = 3;

    /// <summary>停滞超时的下限秒数：配置再小也至少给这么多，避免误杀慢速分片。</summary>
    internal const int MinimumStallTimeoutSeconds = 2;

    /// <summary>重试失败分片/播放列表前的固定等待（毫秒）。</summary>
    /// <remarks>不给"立刻重试"：上游限流或短暂抖动时，连续秒发只会继续失败；固定短等待足够跨过一个抖动窗口。</remarks>
    private const int RetryDelayMilliseconds = 500;

    /// <summary>单个分片下载的总耗时上限（秒）。</summary>
    /// <remarks>
    /// 停滞看门狗按"读取空闲间隔"计时（见 <see cref="DownloadSegmentBodyAsync"/>），
    /// 因此一个持续有数据、但整体很慢的分片不会被误杀；这里的总上限只用于兜住
    /// "每 1 秒来 1 字节"这类永不结束的慢速上游。
    /// </remarks>
    private const int SegmentDownloadTimeoutSeconds = 120;

    /// <summary>读取分片正文时的缓冲区大小。</summary>
    private const int SegmentReadBufferBytes = 64 * 1024;

    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Recording.Hls";
    private readonly SegmentPolicy _policy;
    private readonly DateTimeOffset _startedAt;
    private readonly int _playlistRefreshSeconds;
    private readonly int _stallTimeoutSeconds;
    private readonly int _maxReconnectAttempts;
    private readonly int _segmentDownloadTimeoutSeconds;

    /// <summary>初始化 TS 录制器。</summary>
    /// <param name="segmentPolicy">分片策略。</param>
    /// <param name="startedAt">录制开始时间。</param>
    /// <param name="logger">结构化日志。</param>
    /// <param name="playlistRefreshSeconds">
    /// 播放列表刷新间隔（秒）；为 <see langword="null"/> 时使用 <see cref="PlaylistRefreshSeconds"/>。
    /// 自动化测试可传入更短（含 0）的间隔，从而在不等候真实刷新周期的前提下覆盖
    /// "取消/上游失败发生在刷新播放列表时"的必达收尾路径。
    /// </param>
    /// <param name="stallTimeoutSeconds">
    /// "上游无数据"的停滞判定秒数；为 <see langword="null"/> 时使用 <see cref="DefaultStallTimeoutSeconds"/>，
    /// 低于 <see cref="MinimumStallTimeoutSeconds"/> 时按该下限处理（避免误杀慢速分片）。
    /// </param>
    /// <param name="maxReconnectAttempts">
    /// 分片下载失败与播放列表刷新失败共用的重试上限；为 <see langword="null"/> 时使用
    /// <see cref="DefaultMaxReconnectAttempts"/>。
    /// </param>
    /// <param name="segmentDownloadTimeoutSeconds">
    /// 单个分片下载的**总耗时**上限（秒）；为 <see langword="null"/> 时使用 <see cref="SegmentDownloadTimeoutSeconds"/>。
    /// 它与"读取空闲间隔"是两条独立的边界：前者兜住永不结束的慢速上游，后者兜住真正没有数据的上游。
    /// </param>
    public TsStreamRecorder(
        SegmentPolicy segmentPolicy,
        DateTimeOffset startedAt,
        IStructuredLogger logger,
        int? playlistRefreshSeconds = null,
        int? stallTimeoutSeconds = null,
        int? maxReconnectAttempts = null,
        int? segmentDownloadTimeoutSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(segmentPolicy);
        ArgumentNullException.ThrowIfNull(logger);
        _policy = segmentPolicy;
        _startedAt = startedAt;
        _logger = logger;
        _playlistRefreshSeconds = Math.Max(0, playlistRefreshSeconds ?? PlaylistRefreshSeconds);
        _stallTimeoutSeconds = Math.Max(MinimumStallTimeoutSeconds, stallTimeoutSeconds ?? DefaultStallTimeoutSeconds);
        _maxReconnectAttempts = Math.Max(0, maxReconnectAttempts ?? DefaultMaxReconnectAttempts);
        _segmentDownloadTimeoutSeconds = Math.Max(
            MinimumStallTimeoutSeconds,
            segmentDownloadTimeoutSeconds ?? SegmentDownloadTimeoutSeconds);
    }

    /// <summary>
    /// 录制 HLS/TS 流：持续刷新 media 播放列表，按 m3u8 分片边界写入 TS 字节流。
    /// </summary>
    /// <param name="client">已配置的 HTTP 客户端（应为无限超时的流式客户端）。</param>
    /// <param name="playlist">已解析的播放列表（master 或 media）。</param>
    /// <param name="playlistUri">播放列表绝对地址，用于周期性刷新（直播播放列表会滚动更新）。</param>
    /// <param name="room">房间信息。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="referer">可选 Referer。</param>
    /// <param name="onSegmentCompleted">分片完成回调，可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>录制结果。</returns>
    /// <remarks>
    /// 收尾（关闭末分片 + 登记末分片 + 计算最终结果）是必达的：正常结束、播放列表 ENDLIST、
    /// 上游失败、取消都会执行，且使用独立的 <see cref="FinalizeTimeoutSeconds"/> 超时令牌，
    /// 不受已经取消的上层令牌影响。因此返回结果与回调登记的分片始终一致（不会出现"有分片但字节数为 0"）。
    /// 收尾失败时抛出分类为 <see cref="Core.Errors.RecordingErrorCategory.OutputUnavailable"/> 的异常，
    /// 与录制过程中的上游失败（分类 <see cref="Core.Errors.RecordingErrorCategory.MalformedStream"/>）区分。
    /// <para>
    /// 上游恢复（分片下载、播放列表刷新）共用一份有上限的预算：预算用尽时返回
    /// <see cref="RecordingStopReason.ReconnectLimitReached"/> 而不是 <see cref="RecordingStopReason.StreamEnded"/>，
    /// 避免把"上游取不到"记成"主播下播"；尝试过分片却一个字节都没写进去时返回
    /// <see cref="RecordingStopReason.Failed"/>。
    /// </para>
    /// </remarks>
    /// <exception cref="Core.Errors.RecordingException">收尾失败（末分片未能落盘）时抛出。</exception>
    public async Task<FlvRecordingResult> RecordFragmentsAsync(
        HttpClient client,
        HlsPlaylist playlist,
        Uri playlistUri,
        ResolvedRoom room,
        string outputDirectory,
        string? referer,
        Action<RecordingSegment>? onSegmentCompleted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(playlistUri);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!playlistUri.IsAbsoluteUri)
        {
            throw new ArgumentException("播放列表地址必须是绝对地址。", nameof(playlistUri));
        }

        Directory.CreateDirectory(outputDirectory);

        TsRecordingState state = new();

        bool refreshable = false;
        bool reconnectLimitReached = false;
        RecordingStopReason reason = RecordingStopReason.StreamEnded;
        Exception? finalizeFailure = null;

        try
        {
            if (playlist.IsMasterPlaylist && playlist.VariantUris.Count > 0)
            {
                // master 播放列表：切换到第一个 variant 的 media 播放列表，
                // 之后周期性刷新该 variant 的播放列表（分片地址由解析器解析为绝对地址）。
                playlistUri = new Uri(playlist.VariantUris[0], UriKind.Absolute);
                HlsPlaylist? variant = await FetchMediaPlaylistWithRecoveryAsync(
                    client,
                    playlistUri,
                    referer,
                    room,
                    state,
                    cancellationToken).ConfigureAwait(false);
                if (variant is null)
                {
                    reconnectLimitReached = true;
                }
                else
                {
                    playlist = variant;
                }
            }

            // 只刷新 media 播放列表；含 ENDLIST 的播放列表（点播/已结束）读完即结束，不刷新。
            refreshable = !reconnectLimitReached && !playlist.IsMasterPlaylist && !playlist.IsEndList;

            while (!reconnectLimitReached)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await AppendPlaylistAsync(client, playlist, room, outputDirectory, referer, state, onSegmentCompleted, cancellationToken).ConfigureAwait(false))
                {
                    reconnectLimitReached = true;
                    break;
                }

                if (!refreshable)
                {
                    break;
                }

                if (state.LastSegmentUri is null || playlist.SegmentUris.Count == 0)
                {
                    _logger.Warn(_moduleName, "直播播放列表未提供可用分片，结束录制。", new Dictionary<string, object?>
                    {
                        ["roomId"] = room.RoomId,
                    });
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(_playlistRefreshSeconds), cancellationToken).ConfigureAwait(false);

                HlsPlaylist? refreshed = await FetchMediaPlaylistWithRecoveryAsync(
                    client,
                    playlistUri,
                    referer,
                    room,
                    state,
                    cancellationToken).ConfigureAwait(false);
                if (refreshed is null)
                {
                    reconnectLimitReached = true;
                    break;
                }

                if (refreshed.SegmentUris.Count == 0)
                {
                    _logger.Warn(_moduleName, "刷新后的播放列表为空，结束录制。", new Dictionary<string, object?>
                    {
                        ["roomId"] = room.RoomId,
                    });
                    break;
                }

                playlist = refreshed;
                refreshable = !refreshed.IsMasterPlaylist && !refreshed.IsEndList;
            }

            // refreshable 为 true 说明是直播流被用户/上层取消；为 false 说明播放列表已 ENDLIST（直播结束）。
            // 重连预算用尽必须与"自然下播"分开：播放列表还有分片却拿不到，是上游失败而不是主播下播。
            // 另一个同样不能算"自然下播"的情形：尝试过分片、却一个字节都没写进去。
            reason = reconnectLimitReached
                ? RecordingStopReason.ReconnectLimitReached
                : (state.ReconnectCount > 0 && state.CurrentBytes == 0 && state.Segments.Count == 0
                    ? RecordingStopReason.Failed
                    : (refreshable ? RecordingStopReason.UserStopped : RecordingStopReason.StreamEnded));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消不是失败：停止原因归一为用户主动停止，收尾由下面的 finally 负责。
            reason = RecordingStopReason.UserStopped;
        }
        finally
        {
            // 必达收尾：正常结束、ENDLIST、上游失败、取消都要关闭末分片并登记元数据，
            // 否则文件句柄不释放、末分片丢失、上层元数据停留在全 0（SP-03）。
            finalizeFailure = await FinalizeAsync(state, onSegmentCompleted, room).ConfigureAwait(false);
        }

        if (finalizeFailure is not null)
        {
            // 收尾失败属于"输出侧"问题，用独立分类上抛，使上层能与上游导致的录制失败区分。
            throw new Core.Errors.RecordingException(
                Core.Errors.RecordingErrorCategory.OutputUnavailable,
                "TS 录制收尾失败：末分片未能写入文件。",
                finalizeFailure);
        }

        return new FlvRecordingResult(
            state.Segments,
            state.TotalBytes,
            state.TotalDurationSeconds,
            state.ReconnectCount,
            reason);
    }

    /// <summary>把一批播放列表分片追加写入文件（已写入过的分片会被跳过）。</summary>
    /// <param name="client">HTTP 客户端。</param>
    /// <param name="playlist">当前播放列表。</param>
    /// <param name="room">房间信息。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="referer">可选 Referer。</param>
    /// <param name="state">会话状态。</param>
    /// <param name="onSegmentCompleted">分片完成回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 可以继续录制返回 <see langword="true"/>；重连预算已用尽返回 <see langword="false"/>（调用方据此结束并归因）。
    /// </returns>
    private async Task<bool> AppendPlaylistAsync(
        HttpClient client,
        HlsPlaylist playlist,
        ResolvedRoom room,
        string outputDirectory,
        string? referer,
        TsRecordingState state,
        Action<RecordingSegment>? onSegmentCompleted,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < playlist.SegmentUris.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string segmentUri = playlist.SegmentUris[index];
            if (!state.SeenSegmentUris.Add(segmentUri))
            {
                continue;
            }

            double segmentDuration = index < playlist.SegmentDurationsSeconds.Count
                ? playlist.SegmentDurationsSeconds[index]
                : playlist.TargetDurationSeconds;

            SegmentDownloadOutcome outcome = await DownloadSegmentWithRecoveryAsync(
                client,
                segmentUri,
                referer,
                room,
                state,
                cancellationToken).ConfigureAwait(false);
            if (outcome.ReconnectLimitReached)
            {
                // 失败分片仍留在 Seen 里：这里已经用尽重试预算，再重来一遍只会重复同样的失败。
                return false;
            }

            if (outcome.Payload is null)
            {
                continue;
            }

            (int payloadOffset, int usableBytes) = AlignToPacketBoundary(outcome.Payload);
            if (usableBytes == 0)
            {
                _logger.Warn(_moduleName, "HLS 分片不含完整 TS 包，已跳过。", new Dictionary<string, object?>
                {
                    ["roomId"] = room.RoomId,
                });
                continue;
            }

            if (state.FileStream is null)
            {
                OpenNewSegment(state, room, outputDirectory);
            }

            if (_policy.ShouldSplit(state.CurrentBytes, state.TimestampMs))
            {
                await CloseCurrentSegmentAsync(state, onSegmentCompleted, cancellationToken).ConfigureAwait(false);
                OpenNewSegment(state, room, outputDirectory);
            }

            // 必须从对齐后的偏移开始写：前导垃圾既不是 TS 包，也不该出现在文件里。
            await state.FileStream!.WriteAsync(outcome.Payload.AsMemory(payloadOffset, usableBytes), cancellationToken).ConfigureAwait(false);
            state.CurrentBytes += usableBytes;
            state.CurrentDurationSeconds += segmentDuration;
            state.TimestampMs += (long)Math.Round(segmentDuration * 1000);
            state.LastSegmentUri = segmentUri;

            // 真的写进了一个分片：连续失败窗口就此结束，下一次失败重新从 1 开始计。
            state.ConsecutiveFailures = 0;
        }

        return true;
    }

    /// <summary>下载单个分片，并按共享的重连预算决定"重试 / 放弃该分片 / 结束录制"。</summary>
    /// <param name="client">HTTP 客户端。</param>
    /// <param name="segmentUri">分片绝对地址。</param>
    /// <param name="referer">可选 Referer。</param>
    /// <param name="room">房间信息（日志上下文）。</param>
    /// <param name="state">会话状态（累计重连次数 + 连续失败次数）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 成功时带正文；永久失败（4xx）时为 <see langword="null"/> 且 <c>ReconnectLimitReached</c> 为假（跳过该分片）；
    /// 连续失败用尽预算时 <c>ReconnectLimitReached</c> 为真。
    /// </returns>
    /// <remarks>
    /// 过去失败分片加入 <c>Seen</c> 后既不重试也不计入上限，直播中途连续失败会被当成"主播下播"（SP-06）。
    /// 这里让分片失败与播放列表刷新共用同一份 <c>MaxReconnectAttempts</c> 预算，
    /// 且区分"这一片确实取不到（4xx，跳过、不重试）"与"上游暂时取不到（重试）"。
    /// <para>
    /// 上限按**连续失败**计（成功写入一个分片即清零），4xx 跳过同样计入：
    /// 否则连续 4xx（切片过期、Referer 被拒）永远不会触发上限，录制会一直"进行中"却写不出任何数据。
    /// </para>
    /// </remarks>
    private async Task<SegmentDownloadOutcome> DownloadSegmentWithRecoveryAsync(
        HttpClient client,
        string segmentUri,
        string? referer,
        ResolvedRoom room,
        TsRecordingState state,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            SegmentDownloadResult attempt = await TryDownloadSegmentAsync(client, segmentUri, referer, cancellationToken).ConfigureAwait(false);
            if (attempt.Payload is not null)
            {
                return new SegmentDownloadOutcome(attempt.Payload, ReconnectLimitReached: false);
            }

            state.ReconnectCount++;
            state.ConsecutiveFailures++;

            // 上限判断必须在"永久失败直接跳过"之前：4xx 也要消耗连续失败预算。
            if (state.ConsecutiveFailures > _maxReconnectAttempts)
            {
                _logger.Warn(_moduleName, "HLS 分片连续下载失败，已达到重连上限，结束录制。", new Dictionary<string, object?>
                {
                    ["roomId"] = room.RoomId,
                    ["reconnectCount"] = state.ReconnectCount,
                    ["consecutiveFailures"] = state.ConsecutiveFailures,
                    ["maxReconnectAttempts"] = _maxReconnectAttempts,
                    ["detail"] = attempt.Reason,
                });
                return new SegmentDownloadOutcome(null, ReconnectLimitReached: true);
            }

            if (!attempt.IsRetryable)
            {
                _logger.Warn(_moduleName, "HLS 分片下载失败（不可恢复），跳过该分片。", new Dictionary<string, object?>
                {
                    ["roomId"] = room.RoomId,
                    ["reconnectCount"] = state.ReconnectCount,
                    ["consecutiveFailures"] = state.ConsecutiveFailures,
                    ["detail"] = attempt.Reason,
                });
                return new SegmentDownloadOutcome(null, ReconnectLimitReached: false);
            }

            _logger.Warn(_moduleName, "HLS 分片下载失败，准备重试。", new Dictionary<string, object?>
            {
                ["roomId"] = room.RoomId,
                ["reconnectCount"] = state.ReconnectCount,
                ["consecutiveFailures"] = state.ConsecutiveFailures,
                ["maxReconnectAttempts"] = _maxReconnectAttempts,
                ["detail"] = attempt.Reason,
            });
            await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>打开一个新的 TS 分片文件并重置分片计时基准。</summary>
    /// <param name="state">会话状态。</param>
    /// <param name="room">房间信息。</param>
    /// <param name="outputDirectory">输出目录。</param>
    private void OpenNewSegment(TsRecordingState state, ResolvedRoom room, string outputDirectory)
    {
        (FileStream stream, string path) = OpenSegment(room, outputDirectory, state.SegmentIndex);
        state.SegmentIndex++;
        state.FileStream = stream;
        state.CurrentPath = path;
        state.CurrentBytes = 0;
        state.CurrentDurationSeconds = 0;
        _policy.BeginSegment(state.TimestampMs);
    }

    /// <summary>
    /// 必达收尾：在独立且有界的令牌下关闭当前分片、登记末分片并累计总字节数与总时长。
    /// </summary>
    /// <param name="state">会话状态。</param>
    /// <param name="onSegmentCompleted">分片完成回调。</param>
    /// <param name="room">房间信息（仅用于日志上下文）。</param>
    /// <returns>收尾失败的原因；收尾成功或没有待收尾分片时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 收尾令牌独立于上层令牌：录制被取消时上层令牌已取消，继承它会让 flush 立刻抛异常，
    /// 末分片永远登记不上（SP-03）。无论收尾成功与否，<see cref="CloseCurrentSegmentAsync"/>
    /// 都会释放文件句柄，因此调用方在收尾后可以立刻改名或删除产物。
    /// 收尾失败一律记 Error 日志并返回原因，绝不静默吞掉。
    /// </remarks>
    private async Task<Exception?> FinalizeAsync(
        TsRecordingState state,
        Action<RecordingSegment>? onSegmentCompleted,
        ResolvedRoom room)
    {
        if (state.FileStream is null)
        {
            return null;
        }

        using CancellationTokenSource bounded = new(TimeSpan.FromSeconds(FinalizeTimeoutSeconds));
        try
        {
            await CloseCurrentSegmentAsync(state, onSegmentCompleted, bounded.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogError(LogLevel.Error, _moduleName, "TS 录制收尾超时：末分片未登记。", exception, new Dictionary<string, object?>
            {
                ["roomId"] = room.RoomId,
                ["segmentIndex"] = state.SegmentIndex,
                ["finalizeTimeoutSeconds"] = FinalizeTimeoutSeconds,
            });
            return exception;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(LogLevel.Error, _moduleName, "TS 录制收尾失败：末分片未能落盘或句柄未能释放。", exception, new Dictionary<string, object?>
            {
                ["roomId"] = room.RoomId,
                ["segmentIndex"] = state.SegmentIndex,
            });
            return exception;
        }
    }

    /// <summary>
    /// 结束当前分片：flush 落盘、登记元数据，并<b>无论如何</b>释放文件句柄。
    /// </summary>
    /// <param name="state">会话状态。</param>
    /// <param name="onSegmentCompleted">分片完成回调。</param>
    /// <param name="cancellationToken">flush 使用的令牌（必达收尾路径必须传独立的有界令牌）。</param>
    /// <returns>已完成的分片；没有正在写入的分片时返回 <see langword="null"/>。</returns>
    /// <exception cref="OperationCanceledException">令牌在 flush 完成前被取消时抛出（句柄仍会被释放）。</exception>
    /// <exception cref="IOException">落盘或释放句柄失败时抛出。</exception>
    private async Task<RecordingSegment?> CloseCurrentSegmentAsync(
        TsRecordingState state,
        Action<RecordingSegment>? onSegmentCompleted,
        CancellationToken cancellationToken)
    {
        if (state.FileStream is null)
        {
            return null;
        }

        FileStream stream = state.FileStream;
        string path = state.CurrentPath;
        long bytes = state.CurrentBytes;
        double durationSeconds = state.CurrentDurationSeconds;
        try
        {
            RecordingSegment segment = await CompleteSegmentAsync(stream, path, bytes, durationSeconds, cancellationToken).ConfigureAwait(false);
            state.Segments.Add(segment);
            state.TotalBytes += segment.Bytes;
            state.TotalDurationSeconds += segment.DurationSeconds;
            onSegmentCompleted?.Invoke(segment);
            return segment;
        }
        finally
        {
            // 释放句柄放在 finally：取消或落盘失败时同样必须关闭文件，否则产物被锁住（SP-03）。
            await ReleaseSegmentStreamAsync(stream, path).ConfigureAwait(false);
            state.FileStream = null;
            state.CurrentPath = string.Empty;
            state.CurrentBytes = 0;
            state.CurrentDurationSeconds = 0;
        }
    }

    /// <summary>释放分片文件句柄；失败时记 Error 日志并抛出，绝不静默吞掉。</summary>
    /// <param name="stream">分片文件流。</param>
    /// <param name="path">分片路径（仅用于日志）。</param>
    /// <exception cref="IOException">文件句柄未能正常关闭时抛出。</exception>
    private async Task ReleaseSegmentStreamAsync(FileStream stream, string path)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(LogLevel.Error, _moduleName, "关闭 TS 分片文件失败：文件句柄可能仍被占用。", exception, new Dictionary<string, object?>
            {
                ["fileName"] = Path.GetFileName(path),
            });
            throw;
        }
    }

    /// <summary>TS 录制的会话状态。</summary>
    private sealed class TsRecordingState
    {
        /// <summary>已处理过的分片地址（用于跳过刷新后的重复分片）。</summary>
        public HashSet<string> SeenSegmentUris { get; } = new(StringComparer.Ordinal);

        /// <summary>已完成的分片。</summary>
        public List<RecordingSegment> Segments { get; } = [];

        /// <summary>当前分片文件流。</summary>
        public FileStream? FileStream { get; set; }

        /// <summary>当前分片路径。</summary>
        public string CurrentPath { get; set; } = string.Empty;

        /// <summary>当前分片字节数。</summary>
        public long CurrentBytes { get; set; }

        /// <summary>当前分片时长（秒）。</summary>
        public double CurrentDurationSeconds { get; set; }

        /// <summary>累计时间戳（毫秒），用于分片时长判断。</summary>
        public long TimestampMs { get; set; }

        /// <summary>下一个分片序号。</summary>
        public int SegmentIndex { get; set; }

        /// <summary>分片下载失败次数（累计值，用于展示与元数据）。</summary>
        public int ReconnectCount { get; set; }

        /// <summary>连续失败次数（成功写入一个分片即清零），用于重连上限判定。</summary>
        /// <remarks>
        /// 4xx 分片会被跳过而不重试，但同样计入这里：否则"每一片都取不到"永远触发不了上限，
        /// 录制会停在"进行中"却写不出任何数据。用连续值而不是累计值，
        /// 是为了让偶发失败的长时录制不会因为历史累计而提前结束。
        /// </remarks>
        public int ConsecutiveFailures { get; set; }

        /// <summary>最近写入的分片地址。</summary>
        public string? LastSegmentUri { get; set; }

        /// <summary>已写入的总字节数。</summary>
        public long TotalBytes { get; set; }

        /// <summary>已写入的总时长（秒）。</summary>
        public double TotalDurationSeconds { get; set; }
    }

    /// <summary>
    /// 计算可安全写入的区间（从首个同步字节开始的完整 188 字节 TS 包，丢弃前导垃圾与跨分片残留）。
    /// </summary>
    /// <param name="payload">分片原始字节。</param>
    /// <returns>
    /// 可写入区间的起始偏移与长度；不足一个完整 TS 包时返回 <c>(0, 0)</c>。
    /// 调用方必须从 <c>Offset</c> 开始写入，否则会把前导垃圾写进文件。
    /// </returns>
    public static (int Offset, int Length) AlignToPacketBoundary(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < TsPacketSize)
        {
            return (0, 0);
        }

        int firstSync = FindFirstSync(payload);
        if (firstSync < 0)
        {
            return (0, 0);
        }

        int usable = payload.Length - firstSync;
        return (firstSync, usable - (usable % TsPacketSize));
    }

    private static int FindFirstSync(ReadOnlySpan<byte> payload)
    {
        int limit = Math.Min(TsPacketSize, payload.Length);
        for (int index = 0; index < limit; index++)
        {
            if (payload[index] == TsSyncByte)
            {
                return index;
            }
        }

        return -1;
    }

    private (FileStream Stream, string Path) OpenSegment(ResolvedRoom room, string outputDirectory, int segmentIndex)
    {
        string fileName = RecordingFileNaming.BuildFileName(room.Platform, room.Anchor, room.RoomId, _startedAt, segmentIndex, ".ts");
        string path = RecordingFileNaming.ResolveUniquePath(outputDirectory, fileName);
        FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return (stream, path);
    }

    private static async Task<RecordingSegment> CompleteSegmentAsync(
        FileStream stream,
        string path,
        long bytes,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new RecordingSegment
        {
            Index = ParseIndex(path),
            FileName = Path.GetFileName(path),
            Bytes = bytes,
            DurationSeconds = durationSeconds,
        };
    }

    private static int ParseIndex(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int dash = name.LastIndexOf('-');
        if (dash < 0)
        {
            return 0;
        }

        return int.TryParse(name[(dash + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int index)
            ? index
            : 0;
    }

    /// <summary>
    /// 用"停滞超时"包住一次上游请求：超过设定秒数没有任何响应即判定上游停滞。
    /// </summary>
    /// <typeparam name="T">请求结果类型。</typeparam>
    /// <param name="stallTimeoutSeconds">停滞判定秒数。</param>
    /// <param name="operation">环节名，用于失败原因与日志。</param>
    /// <param name="action">实际的请求动作（拿到的令牌带停滞超时）。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <returns>请求结果。</returns>
    /// <remarks>
    /// 只用于**有限正文**（播放列表）的拉取：整段请求从建连到读完正文共用一个上限。
    /// 分片下载不能这样包（会把"持续有数据但很慢"的合法分片误杀），见 <see cref="DownloadSegmentBodyAsync"/>。
    /// </remarks>
    private static async Task<T> WithStallTimeoutAsync<T>(
        int stallTimeoutSeconds,
        string operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        int seconds = Math.Max(MinimumStallTimeoutSeconds, stallTimeoutSeconds);
        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            return await action(stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new Core.Errors.RecordingException(
                Core.Errors.RecordingErrorCategory.MalformedStream,
                operation + " 超过 " + seconds + " 秒没有数据返回，判定上游停滞。");
        }
    }

    /// <summary>
    /// 拉取 media 播放列表；失败时按共享的重连预算重试，预算用尽返回 <see langword="null"/>。
    /// </summary>
    /// <param name="client">HTTP 客户端。</param>
    /// <param name="playlistUri">播放列表绝对地址。</param>
    /// <param name="referer">可选 Referer。</param>
    /// <param name="room">房间信息（日志上下文）。</param>
    /// <param name="state">会话状态（累计重连次数 + 连续失败次数）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>播放列表；重连预算用尽时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 与分片下载共用同一份 <c>MaxReconnectAttempts</c>：过去刷新失败会立刻上抛，
    /// 一次抖动就能让整场录制结束，而分片失败却完全没有上限（SP-06）。
    /// 取消（用户/上层停止）不属于失败，直接按取消上抛。
    /// </remarks>
    private async Task<HlsPlaylist?> FetchMediaPlaylistWithRecoveryAsync(
        HttpClient client,
        Uri playlistUri,
        string? referer,
        ResolvedRoom room,
        TsRecordingState state,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await WithStallTimeoutAsync(
                    _stallTimeoutSeconds,
                    "拉取播放列表",
                    token => FetchMediaPlaylistAsync(client, playlistUri.ToString(), referer, token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Core.Errors.RecordingException exception) when (exception.Category == Core.Errors.RecordingErrorCategory.MalformedStream)
            {
                state.ReconnectCount++;
                state.ConsecutiveFailures++;
                if (state.ConsecutiveFailures > _maxReconnectAttempts)
                {
                    _logger.LogError(LogLevel.Warn, _moduleName, "拉取播放列表连续失败，已达到重连上限，结束录制。", exception, new Dictionary<string, object?>
                    {
                        ["roomId"] = room.RoomId,
                        ["reconnectCount"] = state.ReconnectCount,
                        ["consecutiveFailures"] = state.ConsecutiveFailures,
                        ["maxReconnectAttempts"] = _maxReconnectAttempts,
                    });
                    return null;
                }

                _logger.LogError(LogLevel.Warn, _moduleName, "拉取播放列表失败，准备重试。", exception, new Dictionary<string, object?>
                {
                    ["roomId"] = room.RoomId,
                    ["reconnectCount"] = state.ReconnectCount,
                    ["consecutiveFailures"] = state.ConsecutiveFailures,
                    ["maxReconnectAttempts"] = _maxReconnectAttempts,
                });
                await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>重试前的固定短等待（取消时立即退出）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private static Task DelayBeforeRetryAsync(CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(RetryDelayMilliseconds), cancellationToken);

    private static async Task<HlsPlaylist> FetchMediaPlaylistAsync(
        HttpClient client,
        string playlistUri,
        string? referer,
        CancellationToken cancellationToken)
    {
        string content = await FetchTextAsync(client, playlistUri, referer, cancellationToken).ConfigureAwait(false);
        return HlsPlaylistParser.Parse(content, new Uri(playlistUri, UriKind.Absolute));
    }

    private static async Task<string> FetchTextAsync(
        HttpClient client,
        string uri,
        string? referer,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new Core.Errors.RecordingException(
                Core.Errors.RecordingErrorCategory.MalformedStream,
                $"拉取播放列表失败：HTTP {(int)response.StatusCode}。");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>下载一个分片：成功返回正文，失败返回可重试/不可重试的判定。</summary>
    /// <param name="client">HTTP 客户端。</param>
    /// <param name="uri">分片绝对地址。</param>
    /// <param name="referer">可选 Referer。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>下载结果。</returns>
    /// <remarks>
    /// 等待响应头用停滞窗口兜底；正文按**读取空闲间隔**计时（见 <see cref="DownloadSegmentBodyAsync"/>），
    /// 因此不再用一枚"请求前设置一次"的 <c>CancelAfter</c> 限制整段下载耗时（SP-07）。
    /// </remarks>
    private async Task<SegmentDownloadResult> TryDownloadSegmentAsync(
        HttpClient client,
        string uri,
        string? referer,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateSegmentRequest(uri, referer);
        HttpResponseMessage response;
        try
        {
            using CancellationTokenSource headerStall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerStall.CancelAfter(TimeSpan.FromSeconds(_stallTimeoutSeconds));
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerStall.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SegmentDownloadResult.Transient($"等待响应头超过 {_stallTimeoutSeconds} 秒");
        }
        catch (HttpRequestException exception)
        {
            // 只记错误类别，不记异常原文：原文可能带上完整的分片地址（含签名参数）。
            return SegmentDownloadResult.Transient("请求分片失败（" + exception.HttpRequestError + "）");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // 4xx 视为"这一片确实取不到"（直播切片过期/被清理）：重试没有意义，跳过它继续录。
                // 408（请求超时）与 429（限流）属于临时状态，仍然走重试。
                int status = (int)response.StatusCode;
                bool permanent = status is >= 400 and < 500
                    && response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
                string reason = $"下载分片失败：HTTP {status}";
                return permanent ? SegmentDownloadResult.Permanent(reason) : SegmentDownloadResult.Transient(reason);
            }

            using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await DownloadSegmentBodyAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 读取分片正文：**每次读到新数据都重置**停滞计时，另用独立的总耗时上限兜住"永远很慢"的上游。
    /// </summary>
    /// <param name="body">上游正文流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>下载结果（成功时带完整正文）。</returns>
    /// <remarks>
    /// 过去用一枚请求前设置好、下载中不再刷新的 <c>CancelAfter</c>，实际限制的是**总下载耗时**：
    /// 每 300 毫秒持续到达的合法分片会在约 2 秒时被判成"没有数据返回"并整片丢弃（SP-07）。
    /// 这里按读取空闲间隔计时，持续有数据的分片不会被中断；总上限另用
    /// <see cref="SegmentDownloadTimeoutSeconds"/>，两条边界互不替代。
    /// </remarks>
    private async Task<SegmentDownloadResult> DownloadSegmentBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        using CancellationTokenSource total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(TimeSpan.FromSeconds(_segmentDownloadTimeoutSeconds));
        using MemoryStream buffer = new();
        byte[] chunk = new byte[SegmentReadBufferBytes];
        while (true)
        {
            int read;
            try
            {
                // 每一轮都以"当前时刻"为起点重新计时：只要读到数据，下一个窗口就是完整的 stall 时长。
                using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(_stallTimeoutSeconds));
                read = await body.ReadAsync(chunk, idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return SegmentDownloadResult.Transient(
                    total.IsCancellationRequested
                        ? $"分片下载超过 {_segmentDownloadTimeoutSeconds} 秒仍未结束"
                        : $"分片正文超过 {_stallTimeoutSeconds} 秒没有新数据");
            }
            catch (IOException exception)
            {
                // 连接被上游/中间设备重置：按上游暂时失败处理（可重试），不把整场录制判成失败。
                return SegmentDownloadResult.Transient("读取分片正文失败（" + exception.GetType().Name + "）");
            }

            if (read == 0)
            {
                return SegmentDownloadResult.Success(buffer.ToArray());
            }

            buffer.Write(chunk, 0, read);
        }
    }

    /// <summary>构造分片请求（带浏览器 UA 与可选 Referer）。</summary>
    /// <param name="uri">分片绝对地址。</param>
    /// <param name="referer">可选 Referer。</param>
    /// <returns>请求（调用方负责释放）。</returns>
    private static HttpRequestMessage CreateSegmentRequest(string uri, string? referer)
    {
        HttpRequestMessage request = new(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }

        return request;
    }

    /// <summary>一次分片下载的结果。</summary>
    /// <param name="Payload">成功时的完整正文；失败时为 <see langword="null"/>。</param>
    /// <param name="IsRetryable">失败是否可以重试（超时、连接失败、5xx、408/429 为真；其余 4xx 为假）。</param>
    /// <param name="Reason">失败原因（写日志用，不含签名地址）。</param>
    private readonly record struct SegmentDownloadResult(byte[]? Payload, bool IsRetryable, string Reason)
    {
        /// <summary>构造成功结果。</summary>
        /// <param name="payload">完整正文。</param>
        /// <returns>成功结果。</returns>
        public static SegmentDownloadResult Success(byte[] payload) => new(payload, IsRetryable: false, string.Empty);

        /// <summary>构造"上游暂时失败、可以重试"的结果。</summary>
        /// <param name="reason">失败原因。</param>
        /// <returns>失败结果。</returns>
        public static SegmentDownloadResult Transient(string reason) => new(null, IsRetryable: true, reason);

        /// <summary>构造"这一片确实取不到、重试没有意义"的结果（4xx）。</summary>
        /// <param name="reason">失败原因。</param>
        /// <returns>失败结果。</returns>
        public static SegmentDownloadResult Permanent(string reason) => new(null, IsRetryable: false, reason);
    }

    /// <summary>一次分片下载 + 重试的最终结论。</summary>
    /// <param name="Payload">成功时的完整正文；跳过或失败时为 <see langword="null"/>。</param>
    /// <param name="ReconnectLimitReached">重连预算是否已用尽（调用方据此结束录制并归因）。</param>
    private readonly record struct SegmentDownloadOutcome(byte[]? Payload, bool ReconnectLimitReached);
}
