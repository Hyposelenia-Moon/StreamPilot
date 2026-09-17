namespace StreamPilot.App.ViewModels;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using StreamPilot.App.Services;
using StreamPilot.Core.Models;

/// <summary>
/// 设置窗口中的单条预设（可编辑，保存时写回 <see cref="PresetStore"/>）。
/// </summary>
public sealed class PresetItem : INotifyPropertyChanged
{
    private string _name;
    private PlatformId _platform;
    private string _roomInput;

    /// <summary>初始化预设项。</summary>
    /// <param name="name">显示名。</param>
    /// <param name="platform">平台。</param>
    /// <param name="roomInput">房间号或链接。</param>
    public PresetItem(string name, PlatformId platform, string roomInput)
    {
        _name = name;
        _platform = platform;
        _roomInput = roomInput;
    }

    /// <summary>从持久化模型创建。</summary>
    /// <param name="preset">持久化预设。</param>
    /// <returns>可编辑项。</returns>
    public static PresetItem FromPreset(RoomPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new PresetItem(preset.Name, preset.Platform, preset.RoomInput);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>显示名（例如主播名）。</summary>
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value ?? string.Empty);
    }

    /// <summary>所属平台。</summary>
    public PlatformId Platform
    {
        get => _platform;
        set => SetField(ref _platform, value);
    }

    /// <summary>房间号或直播间链接。</summary>
    public string RoomInput
    {
        get => _roomInput;
        set => SetField(ref _roomInput, value ?? string.Empty);
    }

    /// <summary>转换为持久化模型；字段不完整时返回 <see langword="null"/>。</summary>
    /// <returns>持久化预设，或 <see langword="null"/>。</returns>
    public RoomPreset? ToPreset()
    {
        string name = Name.Trim();
        string roomInput = RoomInput.Trim();
        if (name.Length == 0 || roomInput.Length == 0)
        {
            return null;
        }

        return new RoomPreset(name, Platform, roomInput);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }
}
