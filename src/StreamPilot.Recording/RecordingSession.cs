namespace StreamPilot.Recording;

using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Parsers;
using StreamPilot.Core.Services;
using StreamPilot.Recording.Mp4;
using StreamPilot.Recording.Flv;
using StreamPilot.Recording.Hls;
using StreamPilot.Recording.Metadata;
using StreamPilot.Recording.Streams;

/// <summary>
/// 一个正在运行的录制会话（实现 Core 的 <see cref="IRecordingSession"/> 契约）。
/// </summary>
/// <remarks>
/// 会话负责：选择可录制候选、建立第二个独立连接、驱动 FLV 或 TS 录制器、
/// 维护状态快照与元数据侧车文件、响应停止请求。
/// Web 播放与录制互不共用连接（需求明确"互不关联"）。
/// </remarks>
public sealed class RecordingSession : IRecordingSession
{
    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Recording.Session";
    private readonly RecordingRequest _request;
    private readonly StreamCandidate _candidate;
    private readonly string _outputDirectory;
    private readonly RecordingMetadataWriter _metadataWriter;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly int _stallTimeoutSeconds;
    private readonly int _maxReconnectAttempts;
    private Task _worker = Task.CompletedTask;
    private RecordingStatus _status;

    /// <summary>当前分片已写入但尚未结算的字节数（由录制器回报）。</summary>
    private long _liveSegmentBytes;

    /// <summary>整场会话累计的媒体时长（毫秒，含已结算分片；由录制器回报）。</summary>
    private long _liveSessionDurationMs;

    private RecordingStopReason _stopReason;
    private bool _stopRequested;
    private bool _disposed;

    private RecordingSession(
        RecordingRequest request,
        StreamCandidate candidate,
        string outputDirectory,
        RecordingMetadataWriter metadataWriter,
        int stallTimeoutSeconds,
        int maxReconnectAttempts,
        IStructuredLogger logger)
    {
        _request = request;
        _candidate = candidate;
        _outputDirectory = outputDirectory;
        _metadataWriter = metadataWriter;
        _stallTimeoutSeconds = stallTimeoutSeconds;
        _maxReconnectAttempts = maxReconnectAttempts;
        _logger = logger;
        SessionId = Guid.NewGuid();
        _status = BuildStatus(request.Room, isRecording: true, totalBytes: 0, duration: TimeSpan.Zero, segmentIndex: 0, reconnectCount: 0, currentFileName: string.Empty);
    }

    /// <inheritdoc />
    public Guid SessionId { get; }

    /// <summary>
    /// 当前状态快照；录制中把"已写但未结算"的进度叠加进去。
    /// </summary>
    /// <remarks>
    /// 状态字段只在分片**完成后**结算，而分片上限默认 10 GB / 480 分钟——
    /// 只看 <c>_status</c> 会让界面整场都显示"00:00:00 · 0.0 MiB"。
    /// 这里叠加录制器回报的实时进度：字节取当前分片已写量；时长按"会话累计 − 已结算"折算，
    /// 避免与已结算分片重复计时。
    /// </remarks>
    public RecordingStatus Status
    {
        get
        {
            RecordingStatus status = _status;
            if (!status.IsRecording)
            {
                return status;
            }

            long liveBytes = Volatile.Read(ref _liveSegmentBytes);
            long sessionMs = Volatile.Read(ref _liveSessionDurationMs);
            long completedMs = (long)status.Duration.TotalMilliseconds;
            long liveMs = sessionMs > completedMs ? sessionMs - completedMs : 0;
            if (liveBytes <= 0 && liveMs <= 0)
            {
                return status;
            }

            return status with
            {
                TotalBytes = status.TotalBytes + Math.Max(0, liveBytes),
                Duration = status.Duration + TimeSpan.FromMilliseconds(liveMs),
            };
        }
    }

    /// <inheritdoc />
    public RecordingStopReason StopReason => _stopReason;

    /// <summary>元数据侧车文件路径。</summary>
    public string MetadataPath => _metadataWriter.FilePath;

    /// <summary>输出目录。</summary>
    public string OutputDirectory => _outputDirectory;

    /// <summary>
    /// 创建并启动一个录制会话。
    /// </summary>
    /// <param name="request">录制请求。</param>
    /// <param name="httpClients">HTTP 客户端工厂。</param>
    /// <param name="stallTimeoutSeconds">无数据到达判定断流的秒数。</param>
    /// <param name="maxReconnectAttempts">最大重连次数。</param>
    /// <param name="logger">结构化日志。</param>
    /// <returns>已启动的会话；录制在后台任务中执行。</returns>
    /// <exception cref="Core.Errors.RecordingException">没有可录制候选或输出目录不可写时抛出。</exception>
    public static Task<RecordingSession> StartAsync(
        RecordingRequest request,
        HttpClientFactory httpClients,
        int stallTimeoutSeconds,
        int maxReconnectAttempts,
        IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(httpClients);
        ArgumentNullException.ThrowIfNull(logger);

        StreamCandidate candidate = SelectCandidate(request);
        string outputDirectory = PrepareOutputDirectory(request);
        DateTimeOffset startedAt = DateTimeOffset.Now;
        RecordingMetadata metadata = BuildMetadata(request, candidate, startedAt);
        string metadataPath = RecordingFileNaming.BuildMetadataPath(
            outputDirectory,
            request.Room.Anchor,
            request.Room.RoomId,
            startedAt);
        RecordingMetadataWriter writer = new(metadataPath, metadata, logger);
        writer.Flush(force: true);

        RecordingSession session = new(
            request,
            candidate,
            outputDirectory,
            writer,
            stallTimeoutSeconds,
            maxReconnectAttempts,
            logger);
        session._worker = Task.Run(() => session.RunAsync(httpClients), CancellationToken.None);
        return Task.FromResult(session);
    }

    /// <inheritdoc />
    public async Task StopAsync(RecordingStopReason reason, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_status.IsRecording)
        {
            // 尊重调用方传入的停止原因：录制器只能看到"令牌被取消"，无法区分用户停止 / 宿主停止 /
            // 主播下播，调用方给的原因更准确。传 None（"未指定"）时按用户主动停止处理，保持既有默认行为。
            _stopReason = reason == RecordingStopReason.None ? RecordingStopReason.UserStopped : reason;
            _stopRequested = true;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn(_moduleName, "等待录制会话收尾超时。", new Dictionary<string, object?>
            {
                ["roomId"] = _request.Room.RoomId,
            });
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 主动取消属于正常收尾路径。
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private static StreamCandidate SelectCandidate(RecordingRequest request)
    {
        if (request.Candidate is not null)
        {
            if (!request.Candidate.IsRecordable())
            {
                throw new Core.Errors.RecordingException(
                    Core.Errors.RecordingErrorCategory.UnsupportedFormat,
                    $"候选格式 {request.Candidate.Format} 不支持原始流录制。");
            }

            return request.Candidate;
        }

        // 录制独立建连，不共享播放连接；但候选选择要与播放一致地"先按画质排序"，
        // 否则直接取接口原始顺序会命中 AVC 蓝光，HDR 房间就录不到 HEVC/HDR 码流。
        IReadOnlyList<StreamCandidate> ordered = CandidateOrdering.Prioritize(request.Room.Candidates);
        foreach (StreamCandidate candidate in ordered)
        {
            if (candidate.IsRecordable())
            {
                return candidate;
            }
        }

        throw new Core.Errors.RecordingException(
            Core.Errors.RecordingErrorCategory.UnsupportedFormat,
            "该直播间没有可录制（HTTP-FLV / HLS-TS）的候选流。");
    }

    private static string PrepareOutputDirectory(RecordingRequest request)
    {
        string root = string.IsNullOrWhiteSpace(request.OutputDirectory)
            ? Core.Configuration.AppPaths.DefaultRecordingDirectory
            : request.OutputDirectory;
        string safeAnchor = RecordingFileNaming.SanitizeAnchor(request.Room.Anchor);
        string directory = Path.Combine(root, request.Room.Platform.ToString().ToLowerInvariant(), safeAnchor);
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new Core.Errors.RecordingException(
                Core.Errors.RecordingErrorCategory.OutputUnavailable,
                $"录制目录不可写：{directory}",
                exception);
        }

        return directory;
    }

    private static RecordingMetadata BuildMetadata(RecordingRequest request, StreamCandidate candidate, DateTimeOffset startedAt) => new()
    {
        Platform = request.Room.Platform,
        RoomId = request.Room.RoomId,
        Anchor = request.Room.Anchor,
        Title = request.Room.Title,
        Category = request.Room.Category,
        StartedAtUtc = startedAt.ToUniversalTime(),
        Format = candidate.Format == StreamFormat.HlsTs ? "hls-ts" : "flv-http",
        Codec = candidate.Codec.ToString().ToLowerInvariant(),
        CandidateFingerprint = candidate.UrlFingerprint,
    };

    /// <summary>构造录制状态快照。</summary>
    /// <param name="room">房间信息。</param>
    /// <param name="isRecording">是否仍在录制。</param>
    /// <param name="totalBytes">已写入字节数。</param>
    /// <param name="duration">已录制时长。</param>
    /// <param name="segmentIndex">当前分片序号。</param>
    /// <param name="reconnectCount">重连次数。</param>
    /// <param name="currentFileName">当前分片文件名。</param>
    /// <returns>状态快照。</returns>
    private static RecordingStatus BuildStatus(
        ResolvedRoom room,
        bool isRecording,
        long totalBytes,
        TimeSpan duration,
        int segmentIndex,
        int reconnectCount,
        string currentFileName) => new()
        {
            Platform = room.Platform,
            RoomId = room.RoomId,
            Anchor = room.Anchor,
            IsRecording = isRecording,
            TotalBytes = totalBytes,
            Duration = duration,
            CurrentSegmentIndex = segmentIndex,
            ReconnectCount = reconnectCount,
            CurrentFileName = currentFileName,
        };

    private async Task RunAsync(HttpClientFactory httpClients)
    {
        SegmentPolicy policy = new(_request.SegmentPolicy);
        int maxDurationMinutes = _request.MaxDurationMinutes ?? FlvStreamRecorder.DefaultMaxDurationMinutes;

        try
        {
            if (_candidate.Format == StreamFormat.FlvHttp)
            {
                await RunFlvAsync(httpClients, policy, maxDurationMinutes).ConfigureAwait(false);
            }
            else
            {
                await RunTsAsync(httpClients, policy).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _stopReason = _stopReason == RecordingStopReason.None ? RecordingStopReason.UserStopped : _stopReason;
        }
        catch (Core.Errors.RecordingException exception)
        {
            // 输出侧失败（收尾/落盘）与上游侧失败分开描述，便于日志与用户提示区分二者。
            string message = exception.Category == Core.Errors.RecordingErrorCategory.OutputUnavailable
                ? "录制输出/收尾失败，会话结束。"
                : "录制会话异常结束。";
            _logger.LogError(LogLevel.Error, _moduleName, message, exception, new Dictionary<string, object?>
            {
                ["roomId"] = _request.Room.RoomId,
                ["category"] = exception.Category.ToString(),
            });
            _stopReason = RecordingStopReason.Failed;
        }
        catch (Exception exception)
        {
            // 意外失败（非取消、非录制异常，例如候选地址解析失败）同样必须走收尾：
            // 旧实现只捕 OCE / RecordingException，异常会从 worker 漏到 StopAsync / DisposeAsync，
            // 而 sidecar 已被 finally 写成 stopReason=None（读起来像"从未停止过"）。
            _logger.LogError(LogLevel.Error, _moduleName, "录制会话异常结束。", exception, new Dictionary<string, object?>
            {
                ["roomId"] = _request.Room.RoomId,
                ["exceptionType"] = exception.GetType().Name,
            });
            _stopReason = RecordingStopReason.Failed;
        }
        finally
        {
            // 录制器的必达收尾（关闭末分片 + 登记元数据）已在等待其返回时完成，
            // 因此这里写出的 sidecar 必然反映真实产物，不会出现"有分片但 totalBytes=0"（SP-03）。
            _metadataWriter.Complete(_stopReason);
            _status = _status with { IsRecording = false };
        }
    }

    private async Task RunFlvAsync(
        HttpClientFactory httpClients,
        SegmentPolicy policy,
        int maxDurationMinutes)
    {
        // 共享客户端由 HttpClientFactory 拥有，禁止 using 释放；直播流是长连接，必须有专用客户端。
        HttpClient client = GetStreamingClient(httpClients);
        HttpStreamSource source = new(
            client,
            _candidate.Url,
            _candidate.HttpReferer,
            _candidate.UrlFingerprint,
            _logger);

        FlvStreamRecorder recorder = new(policy, DateTimeOffset.Now, _logger);
        FlvRecordingResult result = await recorder.RecordAsync(
            source,
            _request.Room,
            _outputDirectory,
            _candidate.Format,
            maxDurationMinutes,
            _stallTimeoutSeconds,
            _maxReconnectAttempts,
            OnSegmentCompleted,
            _cancellation.Token,
            OnProgress).ConfigureAwait(false);

        ApplyResult(result);
    }

    private async Task RunTsAsync(
        HttpClientFactory httpClients,
        SegmentPolicy policy)
    {
        // 同上：共享客户端不可释放。
        HttpClient client = GetStreamingClient(httpClients);
        Uri playlistUri = new(_candidate.Url, UriKind.Absolute);
        string content = await FetchFirstPlaylistAsync(client, playlistUri, _cancellation.Token).ConfigureAwait(false);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(content, playlistUri);

        TsStreamRecorder recorder = new(
            policy,
            DateTimeOffset.Now,
            _logger,
            stallTimeoutSeconds: _stallTimeoutSeconds,
            maxReconnectAttempts: _maxReconnectAttempts);
        FlvRecordingResult result = await recorder.RecordFragmentsAsync(
            client,
            playlist,
            playlistUri,
            _request.Room,
            _outputDirectory,
            _candidate.HttpReferer,
            OnSegmentCompleted,
            _cancellation.Token,
            OnProgress).ConfigureAwait(false);

        ApplyResult(result);
    }

    /// <summary>
    /// 拉取首个 HLS 播放列表，带**有界**的停滞超时。
    /// </summary>
    /// <param name="client">HTTP 客户端。</param>
    /// <param name="playlistUri">播放列表绝对地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>播放列表正文。</returns>
    /// <exception cref="Core.Errors.RecordingException">上游在停滞窗口内没有任何响应时抛出。</exception>
    /// <remarks>
    /// 首个播放列表请求过去只受"用户取消"约束：上游不回包时录制会一直停在"录制中"，
    /// 计数与重连都是 0，用户只能手动停止（SP-06）。这里与分片下载同口径，
    /// 超过停滞窗口即按上游停滞结束，并归入上游错误分类（区别于输出/收尾失败）。
    /// </remarks>
    private async Task<string> FetchFirstPlaylistAsync(
        HttpClient client,
        Uri playlistUri,
        CancellationToken cancellationToken)
    {
        int seconds = Math.Max(TsStreamRecorder.MinimumStallTimeoutSeconds, _stallTimeoutSeconds);
        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            return await FetchPlaylistAsync(client, playlistUri, _candidate.HttpReferer, stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new Core.Errors.RecordingException(
                Core.Errors.RecordingErrorCategory.MalformedStream,
                $"拉取首个 HLS 播放列表超过 {seconds} 秒没有响应，判定上游停滞。");
        }
    }

    /// <summary>
    /// 取得适合长连接直播流的 HTTP 客户端（无限超时；单次读取的停滞由录制器的 stall 超时保护）。
    /// </summary>
    /// <param name="httpClients">HTTP 客户端工厂。</param>
    /// <returns>共享的流式客户端（所有权属于工厂，调用方不得释放）。</returns>
    private static HttpClient GetStreamingClient(HttpClientFactory httpClients) =>
        httpClients.GetStreamingClient();

    /// <summary>记录录制器回报的实时进度（录制线程调用，读取侧用 <see cref="Volatile"/> 保证可见性）。</summary>
    /// <param name="progress">进度快照。</param>
    private void OnProgress(RecordingProgress progress)
    {
        Volatile.Write(ref _liveSegmentBytes, Math.Max(0, progress.SegmentBytes));
        Volatile.Write(ref _liveSessionDurationMs, Math.Max(0, progress.SessionDurationMs));
    }

    /// <summary>清零实时进度：分片结算后不能继续叠加（否则字节与时长会被重复计算）。</summary>
    private void ResetProgress()
    {
        Volatile.Write(ref _liveSegmentBytes, 0);
        Volatile.Write(ref _liveSessionDurationMs, 0);
    }

    private void ApplyResult(FlvRecordingResult result)
    {
        // 调用方显式停止过（StopAsync(reason)）时以它为准：录制器只知道"令牌被取消"，
        // 一律把它归一成 UserStopped，会把调用方给出的原因（例如宿主判定的流结束）覆盖掉。
        _stopReason = _stopRequested && result.StopReason == RecordingStopReason.UserStopped
            ? _stopReason
            : result.StopReason;
        ResetProgress();
        _status = BuildStatus(
            _request.Room,
            isRecording: false,
            totalBytes: result.TotalBytes,
            duration: TimeSpan.FromSeconds(result.DurationSeconds),
            segmentIndex: Math.Max(0, result.Segments.Count - 1),
            reconnectCount: result.ReconnectCount,
            currentFileName: result.Segments.Count > 0 ? result.Segments[^1].FileName : string.Empty);

        // 录制结束后的收尾：把 FLV 无损搬进 MP4，成功后删除 FLV。
        // 不阻塞停止流程（大文件搬运要几秒到几十秒），失败时保留 FLV 保住数据。
        if (_request.ConvertToMp4)
        {
            _ = Task.Run(() => ConvertSegmentsAsync(result.Segments), CancellationToken.None);
        }
    }

    /// <summary>
    /// 把本次录制的分片逐个转成 MP4，成功的删掉对应 FLV。
    /// </summary>
    /// <param name="segments">本次录制的分片。</param>
    /// <returns>任务。</returns>
    /// <remarks>
    /// 之所以换容器：FLV 的音频头最多只能标到 44 kHz，表达不了 48 kHz（B站推的就是 48 kHz），
    /// 转成 MP4 后 <c>mdhd</c> 会如实写入采样率，工具与编辑器看到的就是 48000 Hz。
    /// 转换是纯封装（不转码、不重采样），失败时**不删** FLV。
    /// </remarks>
    private async Task ConvertSegmentsAsync(IReadOnlyList<RecordingSegment> segments)
    {
        foreach (RecordingSegment segment in segments)
        {
            string flvPath = Path.Combine(_outputDirectory, segment.FileName);
            if (!File.Exists(flvPath))
            {
                continue;
            }

            string mp4Path = Path.ChangeExtension(flvPath, ".mp4");
            Mp4ConversionResult result = await FlvToMp4Converter
                .ConvertAsync(flvPath, mp4Path, _logger, CancellationToken.None)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                _logger.Warn(_moduleName, "分片转为 MP4 失败，保留 FLV。", new Dictionary<string, object?>
                {
                    ["file"] = segment.FileName,
                    ["detail"] = result.Error,
                });
                continue;
            }

            try
            {
                File.Delete(flvPath);
                _logger.Info(_moduleName, "分片已转为 MP4 并删除 FLV。", new Dictionary<string, object?>
                {
                    ["file"] = Path.GetFileName(mp4Path),
                    ["bytes"] = result.OutputBytes,
                    ["audioSampleRate"] = result.AudioSampleRate,
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.Warn(_moduleName, "MP4 已生成，但删除 FLV 失败（可手动删除）。", new Dictionary<string, object?>
                {
                    ["file"] = segment.FileName,
                    ["detail"] = exception.Message,
                });
            }
        }
    }

    private void OnSegmentCompleted(RecordingSegment segment)
    {
        _metadataWriter.RecordSegment(segment);
        ResetProgress();
        _status = _status with
        {
            TotalBytes = _status.TotalBytes + segment.Bytes,
            Duration = _status.Duration + TimeSpan.FromSeconds(segment.DurationSeconds),
            CurrentSegmentIndex = segment.Index,
            CurrentFileName = segment.FileName,
        };

        _logger.Info(_moduleName, "分片写入完成。", new Dictionary<string, object?>
        {
            ["roomId"] = _request.Room.RoomId,
            ["segmentIndex"] = segment.Index,
            ["bytes"] = segment.Bytes,
            ["durationSeconds"] = Math.Round(segment.DurationSeconds, 3),
        });
    }

    private static async Task<string> FetchPlaylistAsync(
        HttpClient client,
        Uri playlistUri,
        string? referer,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, playlistUri);
        if (!string.IsNullOrWhiteSpace(referer))
        {
            // 首个播放列表请求也必须带 Referer：防盗链的 HLS 服务对首个请求返回 403，会让整场录制直接失败。
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new Core.Errors.RecordingException(
                Core.Errors.RecordingErrorCategory.MalformedStream,
                $"拉取 HLS 播放列表失败：HTTP {(int)response.StatusCode}。");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
