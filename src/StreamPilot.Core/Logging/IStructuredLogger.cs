namespace StreamPilot.Core.Logging;

/// <summary>
/// 结构化日志抽象。禁止字符串拼接，调用方通过 <c>fields</c> 参数传递结构化字段。
/// </summary>
/// <remarks>
/// 敏感信息（Cookie、Token、签名 URL）禁止写入日志；写入前必须脱敏（见 <see cref="SensitiveData"/>）。
/// </remarks>
public interface IStructuredLogger
{
    /// <summary>判断指定级别是否会真正写出，用于避免昂贵参数的构造。</summary>
    /// <param name="level">日志级别。</param>
    /// <returns>会写出返回 <see langword="true"/>。</returns>
    bool IsEnabled(LogLevel level);

    /// <summary>写入一条结构化日志。</summary>
    /// <param name="level">日志级别。</param>
    /// <param name="module">模块名（例如 <c>Parsers.Bilibili</c>）。</param>
    /// <param name="message">消息模板，使用 <c>{Name}</c> 占位符。</param>
    /// <param name="fields">结构化字段。</param>
    void Log(LogLevel level, string module, string message, IReadOnlyDictionary<string, object?>? fields = null);

    /// <summary>写入一条带异常的日志。</summary>
    /// <param name="level">日志级别。</param>
    /// <param name="module">模块名。</param>
    /// <param name="message">消息模板。</param>
    /// <param name="exception">异常对象，可为 <see langword="null"/>。</param>
    /// <param name="fields">结构化字段。</param>
    void LogError(
        LogLevel level,
        string module,
        string message,
        Exception exception,
        IReadOnlyDictionary<string, object?>? fields = null);
}

/// <summary>
/// <see cref="IStructuredLogger"/> 的便捷扩展方法。
/// </summary>
public static class StructuredLoggerExtensions
{
    /// <summary>写入 Trace 级日志。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="module">模块名。</param>
    /// <param name="message">消息模板。</param>
    /// <param name="fields">结构化字段。</param>
    public static void Trace(
        this IStructuredLogger logger,
        string module,
        string message,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(LogLevel.Trace, module, message, fields);
    }

    /// <summary>写入 Debug 级日志。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="module">模块名。</param>
    /// <param name="message">消息模板。</param>
    /// <param name="fields">结构化字段。</param>
    public static void Debug(
        this IStructuredLogger logger,
        string module,
        string message,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(LogLevel.Debug, module, message, fields);
    }

    /// <summary>写入 Info 级日志。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="module">模块名。</param>
    /// <param name="message">消息模板。</param>
    /// <param name="fields">结构化字段。</param>
    public static void Info(
        this IStructuredLogger logger,
        string module,
        string message,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(LogLevel.Info, module, message, fields);
    }

    /// <summary>写入 Warn 级日志。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="module">模块名。</param>
    /// <param name="message">消息模板。</param>
    /// <param name="fields">结构化字段。</param>
    public static void Warn(
        this IStructuredLogger logger,
        string module,
        string message,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(LogLevel.Warn, module, message, fields);
    }

    /// <summary>写入 Error 级日志（无异常）。</summary>
    /// <param name="logger">日志器。</param>
    /// <param name="module">模块名。</param>
    /// <param name="message">消息模板。</param>
    /// <param name="fields">结构化字段。</param>
    public static void Error(
        this IStructuredLogger logger,
        string module,
        string message,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Log(LogLevel.Error, module, message, fields);
    }
}
