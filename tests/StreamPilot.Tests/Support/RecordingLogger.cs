namespace StreamPilot.Tests.Support;

using StreamPilot.Core.Logging;

/// <summary>
/// 只记录日志级别与消息的测试替身：供"失败必须留下日志"这类断言使用，不写任何文件。
/// </summary>
internal sealed class RecordingLogger : IStructuredLogger
{
    private readonly List<LogLevel> _levels = [];
    private readonly List<string> _messages = [];

    /// <inheritdoc />
    public bool IsEnabled(LogLevel level) => true;

    /// <summary>判断是否记录过指定级别。</summary>
    /// <param name="level">日志级别。</param>
    /// <returns>记录过返回 <see langword="true"/>。</returns>
    public bool HasLevel(LogLevel level) => _levels.Contains(level);

    /// <summary>判断是否存在包含指定片段的消息。</summary>
    /// <param name="fragment">消息片段。</param>
    /// <returns>存在返回 <see langword="true"/>。</returns>
    public bool HasMessageContaining(string fragment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fragment);
        foreach (string message in _messages)
        {
            if (message.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public void Log(LogLevel level, string module, string message, IReadOnlyDictionary<string, object?>? fields = null)
    {
        _levels.Add(level);
        _messages.Add(message);
    }

    /// <inheritdoc />
    public void LogError(
        LogLevel level,
        string module,
        string message,
        Exception exception,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        _levels.Add(level);
        _messages.Add(message);
    }
}
