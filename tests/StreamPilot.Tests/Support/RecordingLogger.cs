namespace StreamPilot.Tests.Support;

using StreamPilot.Core.Logging;

/// <summary>
/// 只记录日志级别的测试替身：供"失败必须留下日志"这类断言使用，不写任何文件。
/// </summary>
internal sealed class RecordingLogger : IStructuredLogger
{
    private readonly List<LogLevel> _levels = [];

    /// <inheritdoc />
    public bool IsEnabled(LogLevel level) => true;

    /// <summary>判断是否记录过指定级别。</summary>
    /// <param name="level">日志级别。</param>
    /// <returns>记录过返回 <see langword="true"/>。</returns>
    public bool HasLevel(LogLevel level) => _levels.Contains(level);

    /// <inheritdoc />
    public void Log(LogLevel level, string module, string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        _levels.Add(level);

    /// <inheritdoc />
    public void LogError(
        LogLevel level,
        string module,
        string message,
        Exception exception,
        IReadOnlyDictionary<string, object?>? fields = null) =>
        _levels.Add(level);
}
