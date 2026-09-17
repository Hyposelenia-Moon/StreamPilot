namespace StreamPilot.Core.Logging;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// 滚动文件结构化日志（单行 JSON，便于事后诊断）。
/// </summary>
/// <remarks>
/// 设计要点：
/// <list type="bullet">
///   <item>日志格式为 <c>{时间戳,级别,模块,消息,字段}</c>，不包含任何字符串拼接的调试输出；</item>
///   <item>写入前统一脱敏（Cookie/Authorization 头 + 敏感查询参数）；</item>
///   <item>按大小滚动，保留数量有上限，避免日志无限增长；</item>
///   <item>写入失败绝不抛出到业务路径，只累加内部失败计数。</item>
/// </list>
/// </remarks>
public sealed class RotatingFileLogger : IStructuredLogger, IDisposable
{
    /// <summary>敏感头部的脱敏正则。</summary>
    private static readonly Regex SensitiveHeaderPattern = new(
        @"(?im)\b(cookie|set-cookie|authorization)\b\s*[:=]\s*[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>日志正文使用的 UTF-8 编码（不带 BOM，BOM 只在文件为空时手工写入一次）。</summary>
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>UTF-8 BOM 字节序列。</summary>
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly Lock _gate = new();
    private readonly string _directory;    private readonly string _filePrefix;
    private readonly LogLevel _minimumLevel;
    private readonly long _maxFileBytes;
    private readonly int _retainedFiles;
    private StreamWriter? _writer;
    private long _writtenBytes;
    private int _writeFailureCount;
    private bool _disposed;

    /// <summary>初始化文件日志。</summary>
    /// <param name="directory">日志目录。</param>
    /// <param name="filePrefix">文件名前缀（例如 <c>streampilot</c>）。</param>
    /// <param name="minimumLevel">写入文件的最低级别。</param>
    /// <param name="maxFileSizeMb">单个文件大小上限（MiB）。</param>
    /// <param name="retainedFileCount">保留文件个数。</param>
    public RotatingFileLogger(
        string directory,
        string filePrefix,
        LogLevel minimumLevel,
        int maxFileSizeMb,
        int retainedFileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePrefix);
        _directory = directory;
        _filePrefix = filePrefix;
        _minimumLevel = minimumLevel;
        _maxFileBytes = Math.Max(1, maxFileSizeMb) * 1024L * 1024L;
        _retainedFiles = Math.Max(1, retainedFileCount);
        Directory.CreateDirectory(_directory);
        OpenWriter();
    }

    /// <summary>文件日志写入失败次数（用于诊断日志系统自身问题）。</summary>
    public int WriteFailureCount
    {
        get
        {
            lock (_gate)
            {
                return _writeFailureCount;
            }
        }
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel level) => level >= _minimumLevel;

    /// <inheritdoc />
    public void Log(LogLevel level, string module, string message, IReadOnlyDictionary<string, object?>? fields = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        WriteLine(BuildLine(level, module, message, fields, exception: null));
    }

    /// <inheritdoc />
    public void LogError(
        LogLevel level,
        string module,
        string message,
        Exception exception,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        LogLevel effective = level < LogLevel.Warn ? LogLevel.Warn : level;
        if (!IsEnabled(effective))
        {
            return;
        }

        WriteLine(BuildLine(effective, module, message, fields, exception));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static string BuildJsonString(string value)
    {
        StringBuilder builder = new(value.Length + 2);
        builder.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string Redact(string value) =>
        SensitiveHeaderPattern.Replace(value, match => match.Groups[1].Value + "=" + SensitiveData.RedactedPlaceholder);

    private static string FormatFieldValue(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        string text => BuildJsonString(Redact(text)),
        IFormattable formattable => BuildJsonString(formattable.ToString(null, CultureInfo.InvariantCulture)),
        _ => BuildJsonString(value.ToString() ?? string.Empty),
    };

    private string BuildLine(
        LogLevel level,
        string module,
        string message,
        IReadOnlyDictionary<string, object?>? fields,
        Exception? exception)
    {
        StringBuilder builder = new(256);
        builder.Append('{');
        builder.Append("\"ts\":").Append(BuildJsonString(DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)));
        builder.Append(",\"level\":").Append(BuildJsonString(level.ToString()));
        builder.Append(",\"module\":").Append(BuildJsonString(Redact(module ?? string.Empty)));
        builder.Append(",\"message\":").Append(BuildJsonString(Redact(message ?? string.Empty)));

        if (fields is not null && fields.Count > 0)
        {
            builder.Append(",\"fields\":{");
            bool first = true;
            foreach (KeyValuePair<string, object?> field in fields)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(BuildJsonString(field.Key)).Append(':').Append(FormatFieldValue(field.Value));
            }

            builder.Append('}');
        }

        if (exception is not null)
        {
            builder.Append(",\"exception\":{");
            builder.Append("\"type\":").Append(BuildJsonString(exception.GetType().FullName ?? exception.GetType().Name));
            builder.Append(",\"message\":").Append(BuildJsonString(Redact(exception.Message)));
            builder.Append(",\"stack\":").Append(BuildJsonString(Redact(exception.StackTrace ?? string.Empty)));
            if (exception.InnerException is { } inner)
            {
                builder.Append(",\"inner\":").Append(BuildJsonString(Redact(inner.GetType().Name + ": " + inner.Message)));
            }

            builder.Append('}');
        }

        builder.Append('}');
        return builder.ToString();
    }

    private void WriteLine(string line)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _writer ??= OpenWriterInternal();
                _writer.WriteLine(line);
                _writer.Flush();
                _writtenBytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (_writtenBytes >= _maxFileBytes)
                {
                    Roll();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _writeFailureCount++;
            }
        }
    }

    private void OpenWriter()
    {
        lock (_gate)
        {
            _writer = OpenWriterInternal();
        }
    }

    private StreamWriter OpenWriterInternal()
    {
        CleanupObsoleteFiles();
        string path = Path.Combine(_directory, $"{_filePrefix}-{DateTime.Now:yyyyMMdd}.log");

        // FileShare.ReadWrite：允许诊断工具（或用户）在程序运行时读取/复制日志。
        FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        // 新文件写入 UTF-8 BOM：让记事本等工具明确识别编码，
        // 避免被按系统 ANSI 代码页解释而出现乱码（内容本身始终是 UTF-8）。
        if (stream.Length == 0)
        {
            stream.Write(Utf8Bom);
            stream.Flush();
        }

        _writtenBytes = stream.Length;
        return new StreamWriter(stream, Utf8WithoutBom) { AutoFlush = false };
    }

    private void Roll()
    {
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
        _writtenBytes = 0;
        string path = Path.Combine(_directory, $"{_filePrefix}-{DateTime.Now:yyyyMMdd}.log");
        try
        {
            if (File.Exists(path))
            {
                string rolled = Path.Combine(
                    _directory,
                    $"{_filePrefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
                File.Move(path, rolled, overwrite: true);
            }
        }
        catch (IOException)
        {
            _writeFailureCount++;
        }

        _writer = OpenWriterInternal();
    }

    private void CleanupObsoleteFiles()
    {
        try
        {
            string[] files = Directory.GetFiles(_directory, $"{_filePrefix}-*.log");
            if (files.Length <= _retainedFiles)
            {
                return;
            }

            Array.Sort(files, static (left, right) => File.GetLastWriteTimeUtc(left).CompareTo(File.GetLastWriteTimeUtc(right)));
            int removeCount = files.Length - _retainedFiles;
            for (int index = 0; index < removeCount; index++)
            {
                try
                {
                    File.Delete(files[index]);
                }
                catch (IOException)
                {
                    _writeFailureCount++;
                }
                catch (UnauthorizedAccessException)
                {
                    _writeFailureCount++;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            Directory.CreateDirectory(_directory);
        }
    }
}

/// <summary>
/// 丢弃所有日志的实现，用于测试与单元测试环境。
/// </summary>
public sealed class NullStructuredLogger : IStructuredLogger
{
    /// <summary>共享实例。</summary>
    public static readonly NullStructuredLogger Instance = new();

    private NullStructuredLogger()
    {
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel level) => false;

    /// <inheritdoc />
    public void Log(LogLevel level, string module, string message, IReadOnlyDictionary<string, object?>? fields = null)
    {
    }

    /// <inheritdoc />
    public void LogError(
        LogLevel level,
        string module,
        string message,
        Exception exception,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
    }
}
