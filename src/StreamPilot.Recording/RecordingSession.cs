namespace StreamPilot.Recording;

using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Services;
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
    private RecordingStopReason _stopReason;
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

    /// <inheritdoc />
    public RecordingStatus Status => _status;

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
        _stopReason = reason;
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

        foreach (StreamCandidate candidate in request.Room.Candidates)
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
            _logger.LogError(LogLevel.Error, _moduleName, "录制会话异常结束。", exception, new Dictionary<string, object?>
            {
                ["roomId"] = _request.Room.RoomId,
                ["category"] = exception.Category.ToString(),
            });
            _stopReason = RecordingStopReason.Failed;
        }
        finally
        {
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
            _cancellation.Token).ConfigureAwait(false);

        ApplyResult(result);
    }

    private async Task RunTsAsync(
        HttpClientFactory httpClients,
        SegmentPolicy policy)
    {
        // 同上：共享客户端不可释放。
        HttpClient client = GetStreamingClient(httpClients);
        Uri playlistUri = new(_candidate.Url, UriKind.Absolute);
        string content = await FetchPlaylistAsync(client, playlistUri, _cancellation.Token).ConfigureAwait(false);
        HlsPlaylist playlist = HlsPlaylistParser.Parse(content, playlistUri);

        TsStreamRecorder recorder = new(policy, DateTimeOffset.Now, _logger);
        FlvRecordingResult result = await recorder.RecordFragmentsAsync(
            client,
            playlist,
            playlistUri,
            _request.Room,
            _outputDirectory,
            _candidate.HttpReferer,
            OnSegmentCompleted,
            _cancellation.Token).ConfigureAwait(false);

        ApplyResult(result);
    }

    /// <summary>
    /// 取得适合长连接直播流的 HTTP 客户端（无限超时；单次读取的停滞由录制器的 stall 超时保护）。
    /// </summary>
    /// <param name="httpClients">HTTP 客户端工厂。</param>
    /// <returns>共享的流式客户端（所有权属于工厂，调用方不得释放）。</returns>
    private static HttpClient GetStreamingClient(HttpClientFactory httpClients) =>
        httpClients.GetStreamingClient();

    private void ApplyResult(FlvRecordingResult result)
    {
        _stopReason = result.StopReason;
        _status = BuildStatus(
            _request.Room,
            isRecording: false,
            totalBytes: result.TotalBytes,
            duration: TimeSpan.FromSeconds(result.DurationSeconds),
            segmentIndex: Math.Max(0, result.Segments.Count - 1),
            reconnectCount: result.ReconnectCount,
            currentFileName: result.Segments.Count > 0 ? result.Segments[^1].FileName : string.Empty);
    }

    private void OnSegmentCompleted(RecordingSegment segment)
    {
        _metadataWriter.RecordSegment(segment);
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

    private static async Task<string> FetchPlaylistAsync(HttpClient client, Uri playlistUri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, playlistUri);
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
