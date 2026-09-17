namespace StreamPilot.Recording.Metadata;

using System.Text.Json;
using System.Text.Json.Serialization;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;

/// <summary>
/// 录制元数据侧车文件的写入器（原子写入 + 周期性增量刷新）。
/// </summary>
/// <remarks>
/// 元数据文件与分片文件同目录，命名为 <c>{主播名}-{房间号}-{开始时间}.meta.json</c>。
/// 写入策略：每 <see cref="FlushIntervalSeconds"/> 秒或每次分片完成时把快照写到临时文件并原子替换，
/// 这样即使进程崩溃也能保留到最近一次刷新的进度。
/// 严禁写入签名 URL 或 Cookie（只写候选指纹）。
/// </remarks>
public sealed class RecordingMetadataWriter
{
    /// <summary>增量刷新间隔（秒）。</summary>
    public const int FlushIntervalSeconds = 30;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Recording.Metadata";
    private readonly RecordingMetadata _metadata;
    private DateTimeOffset _lastFlushAt = DateTimeOffset.MinValue;

    /// <summary>初始化元数据写入器。</summary>
    /// <param name="filePath">元数据文件完整路径。</param>
    /// <param name="metadata">元数据对象（后续由录制会话更新）。</param>
    /// <param name="logger">结构化日志。</param>
    public RecordingMetadataWriter(string filePath, RecordingMetadata metadata, IStructuredLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(logger);
        FilePath = filePath;
        _metadata = metadata;
        _logger = logger;
    }

    /// <summary>元数据文件完整路径。</summary>
    public string FilePath { get; }

    /// <summary>当前元数据快照。</summary>
    public RecordingMetadata Metadata => _metadata;

    /// <summary>立即写入元数据（原子替换）。</summary>
    /// <param name="force">为 <see langword="true"/> 时忽略刷新间隔。</param>
    /// <returns>实际写入返回 <see langword="true"/>。</returns>
    public bool Flush(bool force = false)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!force && now - _lastFlushAt < TimeSpan.FromSeconds(FlushIntervalSeconds))
        {
            return false;
        }

        string directory = Path.GetDirectoryName(FilePath) ?? ".";
        Directory.CreateDirectory(directory);
        string temporaryPath = FilePath + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(_metadata, SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, FilePath, overwrite: true);
            _lastFlushAt = now;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(LogLevel.Warn, _moduleName, "写入录制元数据失败。", exception, new Dictionary<string, object?>
            {
                ["path"] = Path.GetFileName(FilePath),
            });
            return false;
        }
        finally
        {
            TryDeleteTemporary(temporaryPath);
        }
    }

    /// <summary>记录一个已完成的分片并刷新元数据。</summary>
    /// <param name="segment">分片信息。</param>
    public void RecordSegment(RecordingSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        _metadata.Segments.Add(segment);
        _metadata.TotalBytes += segment.Bytes;
        _metadata.DurationSeconds += segment.DurationSeconds;
        Flush();
    }

    /// <summary>标记录制结束并强制刷新。</summary>
    /// <param name="stopReason">停止原因。</param>
    public void Complete(RecordingStopReason stopReason)
    {
        _metadata.EndedAtUtc = DateTimeOffset.UtcNow;
        _metadata.StopReason = stopReason;
        Flush(force: true);
    }

    private void TryDeleteTemporary(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException exception)
        {
            _logger.LogError(LogLevel.Debug, _moduleName, "清理临时元数据文件失败。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogError(LogLevel.Debug, _moduleName, "清理临时元数据文件失败。", exception);
        }
    }
}
