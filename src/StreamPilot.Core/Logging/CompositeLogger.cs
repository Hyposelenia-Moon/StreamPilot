namespace StreamPilot.Core.Logging;

/// <summary>
/// 同时写入多个日志器的组合日志。
/// </summary>
public sealed class CompositeLogger : IStructuredLogger
{
    private readonly IStructuredLogger[] _loggers;

    /// <summary>初始化组合日志。</summary>
    /// <param name="loggers">子日志器（空数组表示不写任何日志）。</param>
    public CompositeLogger(params IStructuredLogger[] loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        _loggers = loggers;
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel level)
    {
        foreach (IStructuredLogger logger in _loggers)
        {
            if (logger.IsEnabled(level))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public void Log(LogLevel level, string module, string message, IReadOnlyDictionary<string, object?>? fields = null)
    {
        foreach (IStructuredLogger logger in _loggers)
        {
            if (logger.IsEnabled(level))
            {
                logger.Log(level, module, message, fields);
            }
        }
    }

    /// <inheritdoc />
    public void LogError(
        LogLevel level,
        string module,
        string message,
        Exception exception,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        foreach (IStructuredLogger logger in _loggers)
        {
            if (logger.IsEnabled(level) || level >= LogLevel.Warn)
            {
                logger.LogError(level, module, message, exception, fields);
            }
        }
    }
}
