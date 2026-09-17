namespace StreamPilot.App.Services;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;

/// <summary>
/// 一条直播间预设。
/// </summary>
/// <param name="Name">显示名（例如主播名）。</param>
/// <param name="Platform">所属平台。</param>
/// <param name="RoomInput">房间号或直播间链接。</param>
public sealed record RoomPreset(string Name, PlatformId Platform, string RoomInput);

/// <summary>
/// 预设列表的持久化存取（<c>%LOCALAPPDATA%\StreamPilot\presets.json</c>）。
/// </summary>
/// <remarks>
/// 与 <see cref="JsonOptionsStore"/> 相同的原子写入策略：先写临时文件再替换，
/// 文件损坏时回退空列表并记录 Warn，不影响主流程。
/// </remarks>
public sealed class PresetStore
{
    /// <summary>预设数量上限（避免列表无限增长）。</summary>
    public const int MaxPresets = 200;

    /// <summary>显示名最大长度。</summary>
    public const int MaxNameLength = 60;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "App.PresetStore";
    private readonly Lock _gate = new();
    private List<RoomPreset> _presets = [];

    /// <summary>初始化预设存取。</summary>
    /// <param name="logger">结构化日志。</param>
    public PresetStore(IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>预设文件完整路径。</summary>
    public string FilePath { get; init; } = Path.Combine(AppPaths.UserDataDirectory, "presets.json");

    /// <summary>当前预设列表的只读快照。</summary>
    public IReadOnlyList<RoomPreset> Items
    {
        get
        {
            lock (_gate)
            {
                return _presets.ToArray();
            }
        }
    }

    /// <summary>从磁盘加载预设。</summary>
    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                _presets = [];
                return;
            }

            try
            {
                string json = File.ReadAllText(FilePath);
                _presets = JsonSerializer.Deserialize<List<RoomPreset>>(json, SerializerOptions) ?? [];
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogError(LogLevel.Warn, _moduleName, "预设文件损坏或不可读，已回退为空列表。", exception, new Dictionary<string, object?>
                {
                    ["path"] = FilePath,
                });
                _presets = [];
            }

            _logger.Info(_moduleName, "预设已加载。", new Dictionary<string, object?>
            {
                ["count"] = _presets.Count,
            });
        }
    }

    /// <summary>新增或覆盖同平台的同名预设。</summary>
    /// <param name="preset">预设。</param>
    /// <returns>写入成功返回 <see langword="true"/>。</returns>
    public bool Add(RoomPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        string name = preset.Name.Trim();
        string roomInput = preset.RoomInput.Trim();
        if (name.Length == 0 || roomInput.Length == 0)
        {
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength];
        }

        lock (_gate)
        {
            _presets.RemoveAll(item => item.Platform == preset.Platform && string.Equals(item.Name, name, StringComparison.Ordinal));
            while (_presets.Count >= MaxPresets)
            {
                _presets.RemoveAt(0);
            }

            _presets.Add(new RoomPreset(name, preset.Platform, roomInput));
        }

        return Save();
    }

    /// <summary>删除指定名称的预设。</summary>
    /// <param name="name">显示名。</param>
    /// <returns>删除成功返回 <see langword="true"/>。</returns>
    public bool Remove(string name)
    {
        bool removed;
        lock (_gate)
        {
            removed = _presets.RemoveAll(item => string.Equals(item.Name, name, StringComparison.Ordinal)) > 0;
        }

        return removed && Save();
    }

    private bool Save()
    {
        List<RoomPreset> snapshot;
        lock (_gate)
        {
            snapshot = [.. _presets];
        }

        string directory = Path.GetDirectoryName(FilePath) ?? ".";
        Directory.CreateDirectory(directory);
        string temporaryPath = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, SerializerOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(LogLevel.Error, _moduleName, "保存预设失败。", exception, new Dictionary<string, object?>
            {
                ["path"] = FilePath,
            });
            return false;
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
                    _logger.LogError(LogLevel.Debug, _moduleName, "清理临时预设文件失败。", deleteFailure);
                }
            }
        }
    }
}
