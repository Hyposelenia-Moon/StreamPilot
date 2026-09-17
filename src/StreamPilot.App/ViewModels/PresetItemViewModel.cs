namespace StreamPilot.App.ViewModels;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using StreamPilot.App.Services;
using StreamPilot.Core.Models;

/// <summary>
/// 一条预设的开播检查状态。
/// </summary>
public enum PresetLiveState
{
    /// <summary>尚未检查，或正在检查中。</summary>
    Unknown = 0,

    /// <summary>检查中。</summary>
    Checking = 1,

    /// <summary>正在开播。</summary>
    Live = 2,

    /// <summary>未开播。</summary>
    Offline = 3,

    /// <summary>检查失败（网络、风控、解析错误等）。</summary>
    Failed = 4,
}

/// <summary>
/// 主界面预设列表中的一项：承载平台徽标、状态词与"点击即解析"命令。
/// </summary>
/// <remarks>
/// 状态词由开播检查写入（<see cref="ApplyLive"/> / <see cref="ApplyOffline"/> / <see cref="ApplyFailure"/>），
/// 界面只做展示，不做任何网络判断（CLAUDE.md：UI 层不写业务逻辑）。
/// </remarks>
public sealed class PresetItemViewModel : INotifyPropertyChanged
{
    /// <summary>平台徽标底色（自绘圆角小方块，不使用任何图片资源）。</summary>
    private static readonly SolidColorBrush BilibiliBadgeBrush = CreateFrozen(0xFB, 0x72, 0x99);

    /// <summary>抖音徽标底色。</summary>
    private static readonly SolidColorBrush DouyinBadgeBrush = CreateFrozen(0x1F, 0x23, 0x29);

    /// <summary>虎牙徽标底色。</summary>
    private static readonly SolidColorBrush HuyaBadgeBrush = CreateFrozen(0xF6, 0x76, 0x2C);

    /// <summary>斗鱼徽标底色。</summary>
    private static readonly SolidColorBrush DouyuBadgeBrush = CreateFrozen(0xFF, 0x5D, 0x23);

    /// <summary>YY 徽标底色。</summary>
    private static readonly SolidColorBrush YyBadgeBrush = CreateFrozen(0x2C, 0x6B, 0xE4);

    /// <summary>Bigo Live 徽标底色。</summary>
    private static readonly SolidColorBrush BigoBadgeBrush = CreateFrozen(0x00, 0xA0, 0xE9);

    /// <summary>未知平台的徽标底色（中性灰，避免浅底白字看不清）。</summary>
    private static readonly SolidColorBrush UnknownBadgeBrush = CreateFrozen(0x5B, 0x64, 0x72);

    /// <summary>未开播、未检查时的状态文字色。</summary>
    private static readonly SolidColorBrush IdleStateBrush = CreateFrozen(0x5B, 0x64, 0x72);

    /// <summary>开播中的状态文字色。</summary>
    private static readonly SolidColorBrush LiveStateBrush = CreateFrozen(0x1F, 0x8A, 0x4C);

    /// <summary>检查中的状态文字色。</summary>
    private static readonly SolidColorBrush CheckingStateBrush = CreateFrozen(0xB4, 0x53, 0x09);

    /// <summary>检查失败的状态文字色。</summary>
    private static readonly SolidColorBrush FailedStateBrush = CreateFrozen(0xC0, 0x39, 0x2B);

    /// <summary>未开播时状态词的展示文本。</summary>
    private const string OfflineStateText = "未开播";

    /// <summary>开播中状态词的展示文本。</summary>
    private const string LiveStateText = "开播中";

    /// <summary>检查中状态词的展示文本。</summary>
    private const string CheckingStateText = "检查中…";

    /// <summary>检查失败状态词的展示文本。</summary>
    private const string FailedStateText = "检查失败";

    /// <summary>尚未检查状态词的展示文本。</summary>
    private const string UnknownStateText = "待检查";

    /// <summary>解析失败原因的分隔符（仅用于界面文本拼接）。</summary>
    private const string StatusSeparator = " / ";

    private PresetLiveState _state = PresetLiveState.Unknown;
    private string _platformAnchor = string.Empty;
    private string _failureReason = string.Empty;

    /// <summary>初始化预设项。</summary>
    /// <param name="preset">预设数据。</param>
    /// <param name="applyCommand">点击该项时执行的命令。</param>
    public PresetItemViewModel(RoomPreset preset, ICommand applyCommand)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(applyCommand);
        Preset = preset;
        ApplyCommand = applyCommand;
        _platformAnchor = preset.Name;
        BadgeText = ResolveBadgeText(preset.Platform);
        BadgeBrush = ResolveBadgeBrush(preset.Platform);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>预设数据（持久化内容）。</summary>
    public RoomPreset Preset { get; }

    /// <summary>点击本项时执行的命令（参数为本项自身）。</summary>
    public ICommand ApplyCommand { get; }

    /// <summary>平台徽标文字，例如 B / 抖 / 虎 / 斗 / YY / BG。</summary>
    public string BadgeText { get; }

    /// <summary>平台徽标底色。</summary>
    public Brush BadgeBrush { get; }

    /// <summary>预设所属平台。</summary>
    public PlatformId Platform => Preset.Platform;

    /// <summary>房间号或直播间链接。</summary>
    public string RoomInput => Preset.RoomInput;

    /// <summary>
    /// 展示用的名称：**用户设定的名称（<see cref="RoomPreset.Name"/>）始终优先**，
    /// 只有用户没设名称时才用平台返回的主播名兜底。
    /// </summary>
    /// <remarks>
    /// 解析结果只写进 <see cref="PlatformAnchor"/>，绝不覆盖用户设定值：
    /// 早期实现让 <c>ApplyLive</c> 直接改写本属性，于是"用户在预设里设的名字"在开播检查之后
    /// 显示成了平台主播名（列表行显示的与用户设定的不一致），而悬浮提示仍用预设名，两处口径也不一致。
    /// </remarks>
    public string AnchorName => string.IsNullOrWhiteSpace(Preset.Name) ? _platformAnchor : Preset.Name;

    /// <summary>平台返回的主播名：仅作为用户未设名称时的兜底展示与提示信息，不写回用户设定值。</summary>
    private string PlatformAnchor
    {
        get => _platformAnchor;
        set
        {
            if (SetField(ref _platformAnchor, value))
            {
                OnPropertyChanged(nameof(AnchorName));
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    /// <summary>当前开播检查状态。</summary>
    public PresetLiveState State
    {
        get => _state;
        private set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(StateBrush));
            }
        }
    }

    /// <summary>状态词（检查中… / 开播中 / 未开播 / 检查失败 / 待检查）。</summary>
    public string StateText => State switch
    {
        PresetLiveState.Checking => CheckingStateText,
        PresetLiveState.Live => LiveStateText,
        PresetLiveState.Offline => OfflineStateText,
        PresetLiveState.Failed => FailedStateText,
        _ => UnknownStateText,
    };

    /// <summary>状态文字颜色（与状态词语义一致）。</summary>
    public Brush StateBrush => State switch
    {
        PresetLiveState.Live => LiveStateBrush,
        PresetLiveState.Checking => CheckingStateBrush,
        PresetLiveState.Failed => FailedStateBrush,
        _ => IdleStateBrush,
    };

    /// <summary>整行展示文本：未开播时形如"主播名 / 未开播"。</summary>
    public string DisplayText => AnchorName + StatusSeparator + StateText;

    /// <summary>检查失败的原因（供 ToolTip 展示）；非失败状态为空字符串。</summary>
    public string FailureReason
    {
        get => _failureReason;
        private set
        {
            if (SetField(ref _failureReason, value))
            {
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    /// <summary>整行悬浮提示文本。</summary>
    public string ToolTipText => FailureReason.Length == 0
        ? BuildToolTip()
        : FailureReason + " · 点击仍可尝试解析。";

    /// <summary>
    /// 拼装常规悬浮提示：以用户设定的名称为准，平台主播名与它不同时一并列出（便于核对房间是否找对）。
    /// </summary>
    /// <returns>悬浮提示文本。</returns>
    private string BuildToolTip()
    {
        string text = Preset.Name + "（" + BadgeText + "）";
        if (!string.IsNullOrWhiteSpace(_platformAnchor)
            && !string.Equals(_platformAnchor, Preset.Name, StringComparison.Ordinal))
        {
            text += StatusSeparator + "平台主播名：" + _platformAnchor;
        }

        return text + " · 点击即解析该预设。";
    }

    /// <summary>把本项标记为"检查中"。</summary>
    public void ApplyChecking()
    {
        FailureReason = string.Empty;
        State = PresetLiveState.Checking;
    }

    /// <summary>
    /// 把本项标记为"开播中"，并记下平台返回的主播名（只作兜底展示，不覆盖用户设定的名称）。
    /// </summary>
    /// <param name="anchor">平台返回的主播名。</param>
    public void ApplyLive(string anchor)
    {
        FailureReason = string.Empty;
        PlatformAnchor = string.IsNullOrWhiteSpace(anchor) ? Preset.Name : anchor;
        State = PresetLiveState.Live;
    }

    /// <summary>把本项标记为"未开播"。</summary>
    /// <param name="anchor">平台返回的主播名；无可信名称时传预设名。</param>
    public void ApplyOffline(string anchor)
    {
        FailureReason = string.Empty;
        PlatformAnchor = string.IsNullOrWhiteSpace(anchor) ? Preset.Name : anchor;
        State = PresetLiveState.Offline;
    }

    /// <summary>把本项标记为"检查失败"并记录原因。</summary>
    /// <param name="reason">失败原因（面向用户的中文描述）。</param>
    public void ApplyFailure(string reason)
    {
        FailureReason = string.IsNullOrWhiteSpace(reason) ? FailedStateText : reason;
        State = PresetLiveState.Failed;
    }

    private static SolidColorBrush CreateFrozen(byte red, byte green, byte blue)
    {
        SolidColorBrush brush = new(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static string ResolveBadgeText(PlatformId platform) => platform switch
    {
        PlatformId.Bilibili => "B",
        PlatformId.Douyin => "抖",
        PlatformId.Huya => "虎",
        PlatformId.Douyu => "斗",
        PlatformId.Yy => "YY",
        PlatformId.Bigo => "BG",
        _ => "?",
    };

    private static SolidColorBrush ResolveBadgeBrush(PlatformId platform) => platform switch
    {
        PlatformId.Bilibili => BilibiliBadgeBrush,
        PlatformId.Douyin => DouyinBadgeBrush,
        PlatformId.Huya => HuyaBadgeBrush,
        PlatformId.Douyu => DouyuBadgeBrush,
        PlatformId.Yy => YyBadgeBrush,
        PlatformId.Bigo => BigoBadgeBrush,
        _ => UnknownBadgeBrush,
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
