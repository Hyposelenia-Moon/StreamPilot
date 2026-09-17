namespace StreamPilot.Recording.Hls;

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

    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Recording.Hls";
    private readonly SegmentPolicy _policy;
    private readonly DateTimeOffset _startedAt;

    /// <summary>初始化 TS 录制器。</summary>
    /// <param name="segmentPolicy">分片策略。</param>
    /// <param name="startedAt">录制开始时间。</param>
    /// <param name="logger">结构化日志。</param>
    public TsStreamRecorder(SegmentPolicy segmentPolicy, DateTimeOffset startedAt, IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(segmentPolicy);
        ArgumentNullException.ThrowIfNull(logger);
        _policy = segmentPolicy;
        _startedAt = startedAt;
        _logger = logger;
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
    /// <exception cref="Core.Errors.RecordingException">播放列表或分片下载失败时抛出。</exception>
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

        if (playlist.IsMasterPlaylist && playlist.VariantUris.Count > 0)
        {
            // master 播放列表：切换到第一个 variant 的 media 播放列表，
            // 之后周期性刷新该 variant 的播放列表（分片地址由解析器解析为绝对地址）。
            playlistUri = new Uri(playlist.VariantUris[0], UriKind.Absolute);
            playlist = await FetchMediaPlaylistAsync(client, playlistUri.ToString(), referer, cancellationToken).ConfigureAwait(false);
        }

        Directory.CreateDirectory(outputDirectory);

        TsRecordingState state = new();

        // 只刷新 media 播放列表；含 ENDLIST 的播放列表（点播/已结束）读完即结束，不刷新。
        bool refreshable = !playlist.IsMasterPlaylist && !playlist.IsEndList;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AppendPlaylistAsync(client, playlist, room, outputDirectory, referer, state, onSegmentCompleted, cancellationToken).ConfigureAwait(false);

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

            await Task.Delay(TimeSpan.FromSeconds(PlaylistRefreshSeconds), cancellationToken).ConfigureAwait(false);

            HlsPlaylist refreshed = await FetchMediaPlaylistAsync(client, playlistUri.ToString(), referer, cancellationToken).ConfigureAwait(false);
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

        RecordingSegment? closing = await CloseCurrentSegmentAsync(state, onSegmentCompleted, cancellationToken).ConfigureAwait(false);
        _ = closing;

        // refreshable 为 true 说明是直播流被用户/上层取消；为 false 说明播放列表已 ENDLIST（直播结束）。
        RecordingStopReason reason = refreshable ? RecordingStopReason.UserStopped : RecordingStopReason.StreamEnded;
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
    private async Task AppendPlaylistAsync(
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

            byte[] payload;
            try
            {
                payload = await DownloadAsync(client, segmentUri, referer, cancellationToken).ConfigureAwait(false);
            }
            catch (Core.Errors.RecordingException exception)
            {
                state.ReconnectCount++;
                _logger.LogError(LogLevel.Warn, _moduleName, "HLS 分片下载失败，跳过该分片。", exception, new Dictionary<string, object?>
                {
                    ["roomId"] = room.RoomId,
                    ["reconnectCount"] = state.ReconnectCount,
                });
                continue;
            }

            int usableBytes = AlignToPacketBoundary(payload);
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

            await state.FileStream!.WriteAsync(payload.AsMemory(0, usableBytes), cancellationToken).ConfigureAwait(false);
            state.CurrentBytes += usableBytes;
            state.CurrentDurationSeconds += segmentDuration;
            state.TimestampMs += (long)Math.Round(segmentDuration * 1000);
            state.LastSegmentUri = segmentUri;
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

    /// <summary>结束当前分片（写盘并登记元数据）。</summary>
    /// <param name="state">会话状态。</param>
    /// <param name="onSegmentCompleted">分片完成回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已完成的分片；没有正在写入的分片时返回 <see langword="null"/>。</returns>
    private async Task<RecordingSegment?> CloseCurrentSegmentAsync(
        TsRecordingState state,
        Action<RecordingSegment>? onSegmentCompleted,
        CancellationToken cancellationToken)
    {
        if (state.FileStream is null)
        {
            return null;
        }

        RecordingSegment segment = await CompleteSegmentAsync(
            state.FileStream,
            state.CurrentPath,
            state.CurrentBytes,
            state.CurrentDurationSeconds,
            cancellationToken).ConfigureAwait(false);

        state.Segments.Add(segment);
        state.TotalBytes += segment.Bytes;
        state.TotalDurationSeconds += segment.DurationSeconds;
        onSegmentCompleted?.Invoke(segment);

        await state.FileStream.DisposeAsync().ConfigureAwait(false);
        state.FileStream = null;
        state.CurrentPath = string.Empty;
        state.CurrentBytes = 0;
        state.CurrentDurationSeconds = 0;
        return segment;
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

        /// <summary>分片下载失败次数。</summary>
        public int ReconnectCount { get; set; }

        /// <summary>最近写入的分片地址。</summary>
        public string? LastSegmentUri { get; set; }

        /// <summary>已写入的总字节数。</summary>
        public long TotalBytes { get; set; }

        /// <summary>已写入的总时长（秒）。</summary>
        public double TotalDurationSeconds { get; set; }
    }

    /// <summary>
    /// 计算可安全写入的字节数（只保留完整的 188 字节 TS 包，丢弃跨分片残留）。
    /// </summary>
    /// <param name="payload">分片原始字节。</param>
    /// <returns>可写入的字节数。</returns>
    public static int AlignToPacketBoundary(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < TsPacketSize)
        {
            return 0;
        }

        int firstSync = FindFirstSync(payload);
        if (firstSync < 0)
        {
            return 0;
        }

        int usable = payload.Length - firstSync;
        return usable - (usable % TsPacketSize);
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
        string fileName = RecordingFileNaming.BuildFileName(room, _startedAt, segmentIndex, ".ts");
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

    private static async Task<byte[]> DownloadAsync(
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
                $"下载分片失败：HTTP {(int)response.StatusCode}。");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}
