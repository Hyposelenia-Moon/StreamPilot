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
/// <para>
/// 原子替换必须**有界重试**：Windows 上读者（备份/索引/播放页刷新）以 <c>FileShare.Read</c> 打开侧车时，
/// <c>File.Move(overwrite: true)</c> 会以共享冲突失败；收尾那次 <see cref="Complete"/> 只调用一次，
/// 不重试就等于"结束时间与停止原因永久丢失"（复审里 ENDLIST 用例 10 秒超时的根因）。
/// </para>
/// </remarks>
public sealed class RecordingMetadataWriter
{
    /// <summary>增量刷新间隔（秒）。</summary>
    public const int FlushIntervalSeconds = 30;

    /// <summary>原子替换的最大尝试次数（含首次）。</summary>
    /// <remarks>
    /// 读者持文件的时间通常是毫秒级（杀毒软件实时扫描可能到几百毫秒），10 次 × 50 毫秒
    /// 足以跨过这个窗口；上限存在的意义是不让一次落盘把录制线程拖住太久。
    /// </remarks>
    public const int ReplaceAttempts = 10;

    /// <summary>两次原子替换之间的等待（毫秒）。</summary>
    private const int ReplaceRetryDelayMilliseconds = 50;

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
            ReplaceAtomically(temporaryPath);
            _lastFlushAt = now;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // 重试后仍失败属于"元数据可能丢失"：用 Error 记，方便用户与日志排查（不再只是 Warn）。
            _logger.LogError(LogLevel.Error, _moduleName, "写入录制元数据失败（原子替换重试后仍失败）。", exception, new Dictionary<string, object?>
            {
                ["path"] = Path.GetFileName(FilePath),
                ["replaceAttempts"] = ReplaceAttempts,
            });
            return false;
        }
        finally
        {
            TryDeleteTemporary(temporaryPath);
        }
    }

    /// <summary>
    /// 把临时文件原子替换到侧车路径，遇共享冲突时**有界重试**。
    /// </summary>
    /// <param name="temporaryPath">已写好的临时文件。</param>
    /// <exception cref="IOException">重试全部失败（读者长时间持文件或磁盘错误）时抛出。</exception>
    /// <exception cref="UnauthorizedAccessException">重试全部失败（权限问题）时抛出。</exception>
    /// <remarks>
    /// <para>
    /// 读者以 <c>FileShare.Read</c> 打开文件会拒绝 <c>File.Move(overwrite: true)</c> 所需的删除权限，
    /// 表现为共享冲突。Windows 上这个错误经 .NET 映射后**不是** <see cref="IOException"/>，
    /// 而是 <see cref="UnauthorizedAccessException"/>（HRESULT <c>0x80070005</c>），所以两者都要重试。
    /// </para>
    /// <para>
    /// 重试发生在录制工作线程上，有界（最多 <see cref="ReplaceAttempts"/> 次）因此不会拖住会话收尾；
    /// 真正的权限问题最差多等一个重试窗口后仍按失败返回。
    /// </para>
    /// </remarks>
    private void ReplaceAtomically(string temporaryPath)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, FilePath, overwrite: true);
                return;
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && attempt < ReplaceAttempts)
            {
                Thread.Sleep(ReplaceRetryDelayMilliseconds);
            }
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
