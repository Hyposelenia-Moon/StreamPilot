namespace StreamPilot.Recording.Flv;

using StreamPilot.Core.Logging;

/// <summary>
/// 单个 FLV 分片的写入器（由 <see cref="FlvStreamRecorder"/> 使用）。
/// </summary>
/// <remarks>
/// 负责三件事，且只做这三件事：
/// <list type="number">
///   <item>写入分片起始的 FLV 文件头与缓存的脚本/序列头标签，使分片可独立播放；</item>
///   <item>把 tag 载荷原样搬运到文件（不改动编码数据）；</item>
///   <item>把时间戳按分片首帧重定基（保证每个分片从 0 开始，且不触碰载荷）。</item>
/// </list>
/// </remarks>
public sealed class FlvSegmentWriter : IAsyncDisposable
{
    private readonly FileStream _fileStream;
    private readonly SegmentPolicy _policy;
    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Recording.Flv";
    private readonly byte[] _headerBuffer = new byte[FlvConstants.TagHeaderSize];
    private readonly byte[] _previousTagSizeBuffer = new byte[FlvConstants.PreviousTagSizeLength];
    private FlvTag? _scriptTag;
    private int _baseTimestampMs;
    private long _lastTimestampMs;
    private long _payloadBytes;
    private bool _awaitingFirstDataTag;
    private bool _disposed;

    /// <summary>初始化分片写入器并立即写入文件头。</summary>
    /// <param name="filePath">分片文件路径（调用方需保证唯一）。</param>
    /// <param name="policy">分片策略。</param>
    /// <param name="logger">结构化日志。</param>
    public FlvSegmentWriter(string filePath, SegmentPolicy policy, IStructuredLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);
        FilePath = filePath;
        _policy = policy;
        _logger = logger;
        _fileStream = new FileStream(
            filePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        WriteFileHeader();
        _policy.BeginSegment(0);
    }

    /// <summary>分片文件路径。</summary>
    public string FilePath { get; }

    /// <summary>已写入的字节数（含文件头与所有标签）。</summary>
    public long BytesWritten => _fileStream.Length;

    /// <summary>已写入的媒体载荷字节数（不含容器头）。</summary>
    public long PayloadBytes => _payloadBytes;

    /// <summary>记录最新的 onMetaData 脚本标签，供后续分片复用。</summary>
    /// <param name="scriptTag">脚本标签。</param>
    public void RememberScriptTag(FlvTag scriptTag)
    {
        ArgumentNullException.ThrowIfNull(scriptTag);
        _scriptTag = scriptTag;
    }

    /// <summary>写入缓存的脚本标签与视频/音频序列头（分片起始时必须调用）。</summary>
    /// <param name="videoHeader">AVC sequence header，可为 <see langword="null"/>。</param>
    /// <param name="audioHeader">AAC sequence header，可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 序列头自身携带的是"会话起始时间戳"（通常接近 0），不能作为分片的时间基准，
    /// 因此这里标记"等待本分片首个数据标签"，由该标签的时间戳决定分片基准，
    /// 保证每个分片的首帧时间戳为 0（可独立播放且分片时长正确）。
    /// </remarks>
    public async Task WriteSequenceHeadersAsync(FlvTag? videoHeader, FlvTag? audioHeader, CancellationToken cancellationToken)
    {
        if (_scriptTag is not null)
        {
            await WriteTagAsync(_scriptTag, cancellationToken).ConfigureAwait(false);
        }

        if (videoHeader is not null)
        {
            await WriteTagAsync(videoHeader, cancellationToken).ConfigureAwait(false);
        }

        if (audioHeader is not null)
        {
            await WriteTagAsync(audioHeader, cancellationToken).ConfigureAwait(false);
        }

        _awaitingFirstDataTag = true;
    }

    /// <summary>
    /// 显式指定本分片的时间基准与分片策略起始时间点。
    /// </summary>
    /// <param name="segmentStartTimestampMs">本分片首帧的绝对时间戳（毫秒）。</param>
    /// <remarks>用于 FLV 录制器在切分后立即把"分片时长"的计时基准对齐到新分片首帧。</remarks>
    public void BeginSegment(long segmentStartTimestampMs)
    {
        _baseTimestampMs = (int)segmentStartTimestampMs;
        _policy.BeginSegment(segmentStartTimestampMs);
    }

    /// <summary>写入一个标签（时间戳按分片首帧重定基）。</summary>
    /// <param name="tag">标签。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task WriteTagAsync(FlvTag tag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_awaitingFirstDataTag)
        {
            BeginSegment(tag.TimestampMs);
            _awaitingFirstDataTag = false;
        }
        else if (_payloadBytes == 0)
        {
            BeginSegment(tag.TimestampMs);
        }

        long rebased = Math.Max(0, tag.TimestampMs - _baseTimestampMs);
        _lastTimestampMs = rebased;

        _headerBuffer[0] = (byte)tag.Type;
        _headerBuffer[1] = (byte)((tag.DataSize >> 16) & 0xFF);
        _headerBuffer[2] = (byte)((tag.DataSize >> 8) & 0xFF);
        _headerBuffer[3] = (byte)(tag.DataSize & 0xFF);
        FlvTimestamp.Write(_headerBuffer, rebased);
        _headerBuffer[8] = 0;
        _headerBuffer[9] = 0;
        _headerBuffer[10] = 0;

        await _fileStream.WriteAsync(_headerBuffer, cancellationToken).ConfigureAwait(false);
        if (tag.Payload.Length > 0)
        {
            await _fileStream.WriteAsync(tag.Payload, cancellationToken).ConfigureAwait(false);
        }

        int previousTagSize = FlvConstants.TagHeaderSize + tag.DataSize;
        _previousTagSizeBuffer[0] = (byte)((previousTagSize >> 24) & 0xFF);
        _previousTagSizeBuffer[1] = (byte)((previousTagSize >> 16) & 0xFF);
        _previousTagSizeBuffer[2] = (byte)((previousTagSize >> 8) & 0xFF);
        _previousTagSizeBuffer[3] = (byte)(previousTagSize & 0xFF);
        await _fileStream.WriteAsync(_previousTagSizeBuffer, cancellationToken).ConfigureAwait(false);
        _payloadBytes += tag.Payload.Length;
    }

    /// <summary>
    /// 结束当前分片：刷新到磁盘并返回分片元数据。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分片元数据。</returns>
    public async Task<Core.Models.RecordingSegment> CompleteAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new Core.Models.RecordingSegment
        {
            Index = ExtractIndex(FilePath),
            FileName = Path.GetFileName(FilePath),
            Bytes = _fileStream.Length,
            DurationSeconds = _lastTimestampMs / 1000.0,
        };
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
            await _fileStream.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            _logger.LogError(LogLevel.Warn, _moduleName, "刷新分片缓冲失败。", exception, new Dictionary<string, object?>
            {
                ["file"] = Path.GetFileName(FilePath),
            });
        }

        await _fileStream.DisposeAsync().ConfigureAwait(false);
    }

    private static int ExtractIndex(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        int dash = name.LastIndexOf('-');
        if (dash < 0 || dash == name.Length - 1)
        {
            return 0;
        }

        return int.TryParse(
            name[(dash + 1)..],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int index)
            ? index
            : 0;
    }

    private void WriteFileHeader()
    {
        byte[] header = new byte[FlvConstants.FileHeaderSize + FlvConstants.PreviousTagSize0Length];
        header[0] = FlvConstants.Signature[0];
        header[1] = FlvConstants.Signature[1];
        header[2] = FlvConstants.Signature[2];
        header[3] = FlvConstants.Version;
        header[4] = FlvConstants.FlagVideo | FlvConstants.FlagAudio;
        header[5] = (byte)((FlvConstants.DataOffset >> 24) & 0xFF);
        header[6] = (byte)((FlvConstants.DataOffset >> 16) & 0xFF);
        header[7] = (byte)((FlvConstants.DataOffset >> 8) & 0xFF);
        header[8] = (byte)(FlvConstants.DataOffset & 0xFF);
        _fileStream.Write(header, 0, header.Length);
    }
}
