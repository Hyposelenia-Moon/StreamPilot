namespace StreamPilot.Recording;

using StreamPilot.Core.Configuration;

/// <summary>
/// 分片决策器：根据已写入字节数与分片时长判断是否应当切分。
/// </summary>
/// <remarks>
/// 纯逻辑（无 IO），因此可被单元测试完整覆盖（正常 / 边界 / 不满足）。
/// 决策只依赖"下一个关键帧"的位置，调用方负责只在关键帧处询问。
/// </remarks>
public sealed class SegmentPolicy
{
    private readonly SegmentPolicyOptions _options;
    private long _startTimestampMs;
    private bool _started;

    /// <summary>初始化分片策略。</summary>
    /// <param name="options">分片配置；为 <see langword="null"/> 时使用默认值。</param>
    public SegmentPolicy(SegmentPolicyOptions? options = null)
    {
        _options = (options ?? new SegmentPolicyOptions()).Normalize();
    }

    /// <summary>当前生效的配置。</summary>
    public SegmentPolicyOptions Options => _options;

    /// <summary>开始一个新分片。</summary>
    /// <param name="timestampMs">分片首帧时间戳（毫秒）。</param>
    public void BeginSegment(long timestampMs)
    {
        _startTimestampMs = timestampMs;
        _started = true;
    }

    /// <summary>
    /// 判断是否应当切分。
    /// </summary>
    /// <param name="currentSegmentBytes">当前分片已写入字节数（含容器头）。</param>
    /// <param name="timestampMs">候选切分点（关键帧）的时间戳。</param>
    /// <returns>应当切分返回 <see langword="true"/>。</returns>
    public bool ShouldSplit(long currentSegmentBytes, long timestampMs)
    {
        if (!_started)
        {
            return false;
        }

        if (currentSegmentBytes >= _options.MaxBytes)
        {
            return true;
        }

        long elapsedMs = Math.Max(0, timestampMs - _startTimestampMs);
        return elapsedMs >= (long)_options.MaxDurationMinutes * RecordingLimits.MillisecondsPerMinute;
    }

    /// <summary>
    /// 判断当前分片是否小到"不值得单独保留"（用于避免刚开播就切出小文件）。
    /// </summary>
    /// <param name="currentSegmentBytes">当前分片字节数。</param>
    /// <returns>低于最小字节数返回 <see langword="true"/>。</returns>
    public bool IsBelowMinimum(long currentSegmentBytes) => currentSegmentBytes < _options.MinBytes;

    /// <summary>计算分片时长（秒）。</summary>
    /// <param name="timestampMs">当前时间戳。</param>
    /// <returns>分片已持续的秒数。</returns>
    public double GetSegmentDurationSeconds(long timestampMs) =>
        Math.Max(0, timestampMs - _startTimestampMs) / 1000.0;
}
