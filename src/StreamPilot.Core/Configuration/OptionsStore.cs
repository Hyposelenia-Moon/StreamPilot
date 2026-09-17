namespace StreamPilot.Core.Configuration;

using System.Text.Json;
using System.Text.Json.Serialization;
using StreamPilot.Core.Logging;

/// <summary>
/// 配置存取契约。
/// </summary>
public interface IOptionsStore
{
    /// <summary>当前生效的配置。</summary>
    StreamPilotOptions Current { get; }

    /// <summary>从磁盘加载配置；文件不存在或损坏时返回默认配置并记录日志。</summary>
    /// <returns>配置对象。</returns>
    StreamPilotOptions Load();

    /// <summary>原子写入配置。</summary>
    /// <param name="options">要保存的配置。</param>
    void Save(StreamPilotOptions options);

    /// <summary>配置文件的完整路径。</summary>
    string FilePath { get; }
}

/// <summary>
/// 基于 JSON 文件的配置存取实现。
/// </summary>
public sealed class JsonOptionsStore : IOptionsStore
{
    /// <summary>配置文件内的 JSON 序列化设置。</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Core.Configuration";
    private StreamPilotOptions _current;

    /// <summary>初始化配置存取。</summary>
    /// <param name="filePath">配置文件完整路径。</param>
    /// <param name="logger">结构化日志。</param>
    public JsonOptionsStore(string filePath, IStructuredLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);
        FilePath = filePath;
        _logger = logger;
        _current = new StreamPilotOptions();
    }

    /// <inheritdoc />
    public StreamPilotOptions Current => _current;

    /// <inheritdoc />
    public string FilePath { get; }

    /// <inheritdoc />
    public StreamPilotOptions Load()
    {
        if (!File.Exists(FilePath))
        {
            _logger.Info(_moduleName, "配置文件不存在，使用默认配置。", new Dictionary<string, object?>
            {
                ["path"] = FilePath,
            });
            _current = new StreamPilotOptions();
            return _current;
        }

        try
        {
            string json = File.ReadAllText(FilePath);
            StreamPilotOptions? parsed = JsonSerializer.Deserialize<StreamPilotOptions>(json, SerializerOptions);
            _current = parsed ?? new StreamPilotOptions();
            _logger.Info(_moduleName, "配置已加载。", new Dictionary<string, object?>
            {
                ["path"] = FilePath,
                ["platform"] = _current.LastPlatform.ToString(),
            });
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(LogLevel.Warn, _moduleName, "配置文件损坏或不可读，回退到默认配置。", exception, new Dictionary<string, object?>
            {
                ["path"] = FilePath,
            });
            _current = new StreamPilotOptions();
        }

        return _current;
    }

    /// <inheritdoc />
    public void Save(StreamPilotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string directory = Path.GetDirectoryName(FilePath) ?? ".";
        Directory.CreateDirectory(directory);

        string temporaryPath = FilePath + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(options, SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, FilePath, overwrite: true);
            _current = options;
            _logger.Info(_moduleName, "配置已保存。", new Dictionary<string, object?>
            {
                ["path"] = FilePath,
            });
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(LogLevel.Error, _moduleName, "保存配置失败。", exception, new Dictionary<string, object?>
            {
                ["path"] = FilePath,
            });
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException deleteFailure)
                {
                    _logger.LogError(LogLevel.Warn, _moduleName, "清理临时配置文件失败。", deleteFailure, new Dictionary<string, object?>
                    {
                        ["path"] = temporaryPath,
                    });
                }
            }
        }
    }
}

/// <summary>
/// 应用路径解析（用户数据与程序目录分离）。
/// </summary>
public static class AppPaths
{
    /// <summary>应用数据目录名。</summary>
    public const string AppFolderName = "StreamPilot";

    /// <summary>用户数据根目录：<c>%LOCALAPPDATA%\StreamPilot</c>。</summary>
    public static string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    /// <summary>配置文件路径。</summary>
    public static string ConfigFile => Path.Combine(UserDataDirectory, "config.json");

    /// <summary>日志目录。</summary>
    public static string LogDirectory => Path.Combine(UserDataDirectory, "logs");

    /// <summary>默认录制输出目录：<c>{视频}\StreamPilot</c>。</summary>
    public static string DefaultRecordingDirectory
    {
        get
        {
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos))
            {
                videos = UserDataDirectory;
            }

            return Path.Combine(videos, AppFolderName);
        }
    }

    /// <summary>程序所在目录（自包含发布时为 exe 目录）。</summary>
    public static string ApplicationDirectory => AppContext.BaseDirectory;

    /// <summary>播放页与第三方 JS 库所在目录。</summary>
    public static string WebDirectory => Path.Combine(ApplicationDirectory, "Web");

    /// <summary>外部工具目录（mpv/ffmpeg，用户自备）。</summary>
    public static string ToolsDirectory => Path.Combine(ApplicationDirectory, "tools");

    /// <summary>确保用户数据目录存在。</summary>
    public static void EnsureUserDataDirectory() => Directory.CreateDirectory(UserDataDirectory);
}
