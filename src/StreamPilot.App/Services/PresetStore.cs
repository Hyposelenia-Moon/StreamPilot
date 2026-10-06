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
/// <remarks>
/// 预设的**身份**是 <see cref="Platform"/> + <see cref="Name"/>：同一个主播可能同时在多个平台开播，
/// 因此"同名"不等于"同一条"；而 <see cref="RoomInput"/> 会随房间号变化（短号跳转、用户换房间），
/// 不能参与身份判定。新增/覆盖/删除/选中恢复都用同一套身份口径，否则同名跨平台会互相串台。
/// </remarks>
public sealed record RoomPreset(string Name, PlatformId Platform, string RoomInput)
{
    /// <summary>
    /// 判断两条预设是否为同一条（身份 = 平台 + 名称）。
    /// </summary>
    /// <param name="other">另一条预设。</param>
    /// <returns>指向同一条预设返回 <see langword="true"/>。</returns>
    public bool HasSameIdentity(RoomPreset? other) =>
        other is not null
        && Platform == other.Platform
        && string.Equals(Name, other.Name, StringComparison.Ordinal);
}

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
        string name = NormalizeName(preset.Name);
        string roomInput = preset.RoomInput.Trim();
        if (name.Length == 0 || roomInput.Length == 0)
        {
            return false;
        }

        RoomPreset normalized = new(name, preset.Platform, roomInput);
        lock (_gate)
        {
            // 覆盖口径与删除口径必须完全一致（平台 + 名称），否则同名跨平台会互相误覆盖 / 误删。
            _presets.RemoveAll(item => item.HasSameIdentity(normalized));
            while (_presets.Count >= MaxPresets)
            {
                _presets.RemoveAt(0);
            }

            _presets.Add(normalized);
        }

        return Save();
    }

    /// <summary>删除指定预设：按完整身份（平台 + 名称）匹配。</summary>
    /// <param name="preset">目标预设；只有平台与名称参与匹配，房间号不参与。</param>
    /// <returns>确实删掉了一条返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 不能只按名称删：同一个主播可能同时在多个平台开播（列表里两条同名预设），
    /// 只按名称删会把另一个平台的那条一起删掉；房间号会随短号跳转而变化，所以也不参与匹配。
    /// </remarks>
    public bool Remove(RoomPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        string name = NormalizeName(preset.Name);
        if (name.Length == 0)
        {
            return false;
        }

        RoomPreset identity = preset with { Name = name };
        bool removed;
        lock (_gate)
        {
            removed = _presets.RemoveAll(item => item.HasSameIdentity(identity)) > 0;
        }

        return removed && Save();
    }

    /// <summary>把显示名归一化到存储口径（去首尾空白、超长截断）。</summary>
    /// <param name="name">原始显示名。</param>
    /// <returns>归一化后的显示名；空白输入返回空串。</returns>
    private static string NormalizeName(string name)
    {
        string trimmed = name.Trim();
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
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
