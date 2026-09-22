namespace StreamPilot.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using StreamPilot.App.Services;
using StreamPilot.App.Views;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Services;

/// <summary>
/// 主窗口视图模型：承载直播 / 录制 / 设置三个面板的状态与命令。
/// </summary>
/// <remarks>
/// 约束（CLAUDE.md）：
/// <list type="bullet">
///   <item>UI 层只通过 Core 的服务接口调用业务能力，不 new 任何解析器；</item>
///   <item>所有网络/IO 都在 async 命令中执行，绝不在 UI 线程阻塞；</item>
///   <item>所有 async void 仅用于事件处理器，且内部必须 try/catch 并上报错误。</item>
/// </list>
/// </remarks>
public sealed class ShellViewModel : INotifyPropertyChanged
{
    /// <summary>页面握手消息类型。</summary>
    private const string PlayerReadyType = "ready";

    /// <summary>页面遥测消息类型。</summary>
    private const string PlayerTelemetryType = "telemetry";

    /// <summary>页面状态消息类型。</summary>
    private const string PlayerStatusType = "status";

    /// <summary>页面错误消息类型。</summary>
    private const string PlayerErrorType = "error";

    /// <summary>页面请求重新解析的消息类型。</summary>
    private const string PlayerRefreshNeededType = "refresh-needed";
    /// <summary>宿主要求页面播放的消息类型。</summary>
    private const string HostPlayType = "play";

    /// <summary>宿主要求页面暂停或继续播放的消息类型。</summary>
    private const string HostPauseType = "pause";

    /// <summary>页面日志消息类型（页面不再显示日志面板，日志统一进宿主日志）。</summary>
    private const string PlayerLogType = "log";

    /// <summary>宿主状态消息类型（宿主 → 页面）：把状态提示推到画面下方的状态行。</summary>
    private const string HostStatusType = "host-status";

    /// <summary>页面消息里的级别字段名（`log` 与 `host-status` 共用）。</summary>
    private const string LevelFieldName = "level";

    /// <summary>消息级别：信息（画面下方状态行用主题强调蓝）。</summary>
    private const string LevelInfo = "info";

    /// <summary>消息级别：警告（画面下方状态行用警示色）。</summary>
    private const string LevelWarn = "warn";

    /// <summary>消息级别：错误（画面下方状态行用错误色）。</summary>
    private const string LevelError = "error";

    /*
     * 状态级别判定标记。
     *
     * `StatusMessage` 的赋值点有几十处（解析、播放、录制、预设、设置），逐个手写级别既啰嗦又容易漏，
     * 因此集中在 setter 下发时按文本标记推断，错误优先于警告；未命中的文本按信息级处理
     * （宁可用低调的蓝色，也不要把普通操作反馈染成告警色）。
     */

    /// <summary>错误级状态标记：失败原因要用错误色，用户一眼就知道这次操作没有成功。</summary>
    private static readonly string[] StatusErrorMarkers =
    [
        "解析失败", "失败", "错误", "找不到", "未找到", "拒绝", "无法", "不受支持", "没有可用", "没有可在",
    ];

    /// <summary>警告级状态标记：需要用户先做点什么，或者宿主已经放弃了自动重试。</summary>
    private static readonly string[] StatusWarnMarkers =
    [
        "请先", "请点击", "尚未", "已停止自动重试", "不是合法",
    ];

    /// <summary>页面请求宿主开始播放的消息类型。</summary>
    private const string PlayerRequestPlayType = "request-play";

    /// <summary>页面请求宿主在暂停与继续之间切换的消息类型。</summary>
    private const string PlayerTogglePauseType = "toggle-pause";

    /// <summary>页面请求停止播放（销毁播放器、释放会话）的消息类型。</summary>
    private const string PlayerRequestStopType = "request-stop";

    /// <summary>宿主下发消息里的暂停状态字段名。</summary>
    private const string PausedFieldName = "paused";

    /// <summary>
    /// 追帧档位消息类型（页面 → 宿主）：档位入口只在播放页底部，宿主只记住该值供下次播放沿用。
    /// </summary>
    private const string TargetType = "target";

    /// <summary>预设开播检查的最大并发数（避免同时打满平台接口）。</summary>
    private const int PresetCheckConcurrency = 2;

    /// <summary>播放页档位消息里的目标延迟字段名。</summary>
    private const string TargetFieldName = "extremeTargetMs";

    /// <summary>单个预设开播检查的超时时间。</summary>
    private static readonly TimeSpan PresetCheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>「新增预设」对话框里解析直播链接的超时时间。</summary>
    private static readonly TimeSpan PresetLinkResolveTimeout = TimeSpan.FromSeconds(20);

    /// <summary>「新增预设」对话框里平台为空时的提示（对话内显示，不关闭）。</summary>
    private const string PresetPlatformHint = "无法识别平台：请在对话框的「平台」里选择该直播间所属的平台。";

    /// <summary>「新增预设」解析失败提示的前缀。</summary>
    private const string PresetResolveFailurePrefix = "解析失败：";

    /// <summary>「新增预设」解析失败提示的后缀（说明为什么没有保存）。</summary>
    private const string PresetNotSavedSuffix = "（未保存该预设，请检查链接后重试）";

    /// <summary>「新增预设」解析超时提示。</summary>
    private const string PresetResolveTimeoutHint = "解析超时（20 秒）";

    /// <summary>
    /// 同一房间"页面请求重新解析 → 重新解析 → 再下发播放"的最大连续自动重试次数。
    /// </summary>
    /// <remarks>
    /// 播放页在每个会话里最多请求一次重新解析，但每次重新解析都会开一个新会话，
    /// 于是"候选只有 1 条且地址已失效"的房间（实测斗鱼部分房间如此）会无限循环重连。
    /// 这里给出上限：达到上限后停止自动重试并给出可操作的提示，避免无休止重连。
    /// </remarks>
    private const int MaxAutomaticReResolves = 2;

    /// <summary>遥测里需要落盘的数字字段（消息字段名 → 日志字段名）。</summary>
    private static readonly (string Message, string Log)[] TelemetryNumberFields =
    [
        ("bufferedAheadMs", "bufferedAheadMs"),
        ("secondsSinceProgress", "secondsSinceProgress"),
        ("droppedVideoFrames", "droppedVideoFrames"),
        ("totalVideoFrames", "totalVideoFrames"),
        ("extremeTargetMs", "extremeTargetMs"),
        ("reconnects", "reconnects"),
        ("runawayChaseAttempts", "runawayChaseAttempts"),
    ];

    /// <summary>
    /// 遥测里需要落盘的布尔字段（消息字段名 → 日志字段名）。
    /// </summary>
    /// <remarks>
    /// `paused` 是媒体元素自身的状态，`pausedByUser` 才是页面的用户意图。
    /// 真机实测的"缓冲失控"坏状态里两者不一致（元素停了、用户没点暂停），
    /// 排查"画面 80 秒不前进却不重连"必须能在日志里直接区分这两个字段。
    /// </remarks>
    private static readonly (string Message, string Log)[] TelemetryBooleanFields =
    [
        ("paused", "paused"),
        ("pausedByUser", "pausedByUser"),
        ("elementPaused", "elementPaused"),
    ];

    /// <summary>自动识别平台失败时的提示（状态行使用）。</summary>
    private const string PlatformDetectionHint = "无法识别平台：请输入直播间链接，或在设置里指定默认平台。";

    /// <summary>房间信息缺失时的占位文本（「当前直播」卡片使用）。</summary>
    private const string PlaceholderText = "-";

    /// <summary>页面进入全屏的消息类型。</summary>
    private const string PlayerFullscreenEnterType = "fullscreen-enter";

    /// <summary>页面退出全屏的消息类型。</summary>
    private const string PlayerFullscreenExitType = "fullscreen-exit";

    /// <summary>页面请求切换画质的消息类型。</summary>
    private const string PlayerQualityType = "quality";

    /// <summary>宿主告知桥接地址的消息类型。</summary>
    private const string HostBridgeInfoType = "bridge-info";

    private readonly IRoomResolver _resolver;
    private readonly IPlaybackCoordinator _playback;
    private readonly IRecordingCoordinator _recording;
    private readonly IPlaybackBridge _bridge;
    private readonly IStructuredLogger _logger;
    private readonly Action<StreamPilotOptions> _saveOptions;
    private readonly PresetStore _presetStore;
    private readonly Func<string?, string?> _resolveMpvPath;
    private readonly Func<SettingsViewModel, bool> _showSettingsDialog;
    private readonly string _moduleName = "App.Shell";

    private StreamPilotOptions _options;
    private PlatformOption _selectedPlatformOption = null!;

    /// <summary>最近一次解析实际使用的平台；识别不出时为 <see cref="PlatformId.Unknown"/>。</summary>
    private PlatformId _effectivePlatform = PlatformId.Unknown;

    /// <summary>
    /// 点击预设时记下的"平台 + 当时的输入文本"。
    /// </summary>
    /// <remarks>
    /// 预设里存的是平台返回的房间号（例如 <c>660000</c>），单看房间号无法识别平台，
    /// 所以点预设后必须按预设自己的平台解析，否则会落到设置里的默认平台（默认是 B 站）。
    /// 记下输入文本是为了让这条记录随用户改动输入自动失效（见 <see cref="PresetPlatformForInput"/>）。
    /// </remarks>
    private (PlatformId Platform, string Input)? _presetPlatformOverride;

    private bool _isCheckingPresets;
    private CancellationTokenSource? _presetCheckCancellation;

    /// <summary>已订阅状态变化的预设项（重建列表时逐个退订，避免事件悬挂）。</summary>
    private readonly List<PresetItemViewModel> _presetItemSubscriptions = [];

    /// <summary>
    /// 最近一次"新增预设"解析失败的原因（供对话框回调与日志使用，取用后清空）。
    /// </summary>
    private string _presetResolveFailure = string.Empty;

    /// <summary>预设列表当前选中项（见 <see cref="SelectedPresetItem"/>）。</summary>
    private PresetItemViewModel? _selectedPresetItem;
    private string _roomInput = string.Empty;
    private int _extremeTargetMs;
    private bool _isBusy;
    private bool _isPlayerReady;
    private string _statusMessage = "等待解析直播源。";
    private string _roomTitle = "-";
    private string _roomAnchor = "-";
    private string _roomCategory = "-";
    private string _liveStatus = ResolveMessages.LiveStatusUnknown;
    private string _recordingSummary = "未录制";
    private string _presetSummary = "暂无预设";
    private ResolvedRoom? _currentRoom;
    private IRecordingSession? _recordingSession;
    private int _activeSessionId;

    /// <summary>连续自动重新解析的次数（播放出画后清零）。</summary>
    private int _automaticReResolveCount;

    /// <summary>播放页是否处于用户暂停状态（页面遥测里的 paused 是权威状态，暂停不销毁会话）。</summary>
    private bool _isPlaybackPaused;

    /// <summary>用户选择的画质档位键；为空表示取平台最高档。</summary>
    private string? _preferredQualityKey;

    /// <summary>初始化视图模型。</summary>
    /// <param name="dependencies">组合根注入的依赖。</param>
    public ShellViewModel(Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _resolver = dependencies.Resolver;
        _playback = dependencies.Playback;
        _recording = dependencies.Recording;
        _bridge = dependencies.Bridge;
        _logger = dependencies.Logger;
        _options = dependencies.Options;
        _saveOptions = dependencies.SaveOptions;
        _presetStore = dependencies.Presets;
        _resolveMpvPath = dependencies.ResolveMpvPath;
        _showSettingsDialog = dependencies.ShowSettingsDialog;

        _roomInput = _options.LastRoomInput;
        _extremeTargetMs = NormalizeTarget(_options.Playback.ExtremeTargetMs);
        
        Platforms = PlatformOption.All;

        // 按配置的默认平台预选；找不到时回落到第一个，避免 SelectedItem 绑定拿不到实例。
        PlatformId initialPlatform = _options.DefaultPlatform == PlatformId.Unknown
            ? PlatformId.Bilibili
            : _options.DefaultPlatform;
        _selectedPlatformOption = PlatformOption.Find(initialPlatform) ?? Platforms[0];

        ResolveCommand = new AsyncRelayCommand(_ => ResolveFromUserAsync(), HandleCommandErrorAsync, _ => !IsBusy);
        MpvCommand = new AsyncRelayCommand(_ => PlayWithMpvAsync(), HandleCommandErrorAsync, _ => _currentRoom is not null);
        StartRecordingCommand = new AsyncRelayCommand(_ => StartRecordingAsync(), HandleCommandErrorAsync, _ => _currentRoom is not null && _recordingSession is null);
        StopRecordingCommand = new AsyncRelayCommand(_ => StopRecordingAsync(), HandleCommandErrorAsync, _ => _recordingSession is not null);
        OpenRecordingFolderCommand = new RelayCommand(_ => OpenRecordingFolder());
        OpenLogFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.LogDirectory));
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        ApplyPresetCommand = new AsyncRelayCommand(parameter => ApplyPresetAsync(parameter as PresetItemViewModel), HandleCommandErrorAsync);
        DeletePresetCommand = new AsyncRelayCommand(parameter => DeletePresetAsync(parameter as PresetItemViewModel), HandleCommandErrorAsync, parameter => parameter is PresetItemViewModel);

        // 保存预设刻意**始终可点**：按钮灰着没有任何解释，用户会以为"预设加不了"。
        // 点进去再校验并给出明确提示，比禁用按钮更容易理解。
        SavePresetCommand = new AsyncRelayCommand(_ => SavePresetAsync(), HandleCommandErrorAsync);

        // 状态刷新按钮在检查期间禁用，避免并发堆积。
        RefreshPresetStatusCommand = new AsyncRelayCommand(_ => RefreshPresetStatusAsync(), HandleCommandErrorAsync, _ => !_isCheckingPresets);

        // 全部命令就绪后再建预设项（预设项持有 ApplyPresetCommand），最后触发一次后台检测。
        RefreshPresetItems();

        // 启动后立即在后台检测一遍所有预设是否开播；不等待、不阻塞 UI。
        _ = RefreshPresetStatusAsync();
    }

    /// <summary>视图模型依赖集合（由组合根构造）。</summary>
    /// <param name="Resolver">房间解析服务。</param>
    /// <param name="Playback">播放编排服务。</param>
    /// <param name="Recording">录制编排服务。</param>
    /// <param name="Bridge">桥接服务。</param>
    /// <param name="Presets">预设存储。</param>
    /// <param name="ResolveMpvPath">mpv 路径解析回调（返回绝对路径，未找到返回 null）。</param>
    /// <param name="Logger">结构化日志。</param>
    /// <param name="Options">初始配置。</param>
    /// <param name="SaveOptions">配置保存回调。</param>
    /// <param name="ShowSettingsDialog">打开设置窗口的回调（返回 true 表示用户点了保存）。</param>
    public sealed record Dependencies(
        IRoomResolver Resolver,
        IPlaybackCoordinator Playback,
        IRecordingCoordinator Recording,
        IPlaybackBridge Bridge,
        PresetStore Presets,
        Func<string?, string?> ResolveMpvPath,
        IStructuredLogger Logger,
        StreamPilotOptions Options,
        Action<StreamPilotOptions> SaveOptions,
        Func<SettingsViewModel, bool> ShowSettingsDialog);

    /// <summary>页面消息到达事件（宿主 → 页面方向的发送由该事件提供通道）。</summary>
    public event EventHandler<string>? SendMessageRequested;

    /// <summary>播放页进入/退出全屏（宿主据此放大播放区域）。</summary>
    public event EventHandler<bool>? FullscreenChanged;

    /// <summary>
    /// 由宿主回传最终生效的全屏状态，便于界面提示与页面保持一致。
    /// </summary>
    /// <param name="isFullscreen">是否全屏。</param>
    public void NotifyFullscreenChanged(bool isFullscreen) => FullscreenChanged?.Invoke(this, isFullscreen);

    /// <summary>属性变更通知。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>可用平台列表。</summary>
    public IReadOnlyList<PlatformOption> Platforms { get; }

    /// <summary>日志行（供界面展示最近事件）。</summary>
    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>解析命令。</summary>
    public ICommand ResolveCommand { get; }

    /// <summary>mpv 外挂播放命令。</summary>
    public ICommand MpvCommand { get; }

    /// <summary>开始录制命令。</summary>
    public ICommand StartRecordingCommand { get; }

    /// <summary>停止录制命令。</summary>
    public ICommand StopRecordingCommand { get; }

    /// <summary>打开录制目录命令。</summary>
    public ICommand OpenRecordingFolderCommand { get; }

    /// <summary>打开日志目录命令。</summary>
    public ICommand OpenLogFolderCommand { get; }

    /// <summary>打开设置窗口命令。</summary>
    public ICommand OpenSettingsCommand { get; }

    /// <summary>把当前平台与房间号保存为预设。</summary>
    public ICommand SavePresetCommand { get; }

    /// <summary>点击预设：填入平台与房间号并自动解析（参数为 <see cref="PresetItemViewModel"/>）。</summary>
    public ICommand ApplyPresetCommand { get; }

    /// <summary>删除指定预设（参数为 <see cref="PresetItemViewModel"/>）。</summary>
    public ICommand DeletePresetCommand { get; }

    /// <summary>手动重新检测全部预设的开播状态。</summary>
    public ICommand RefreshPresetStatusCommand { get; }

    /// <summary>主界面预设列表（每项自带平台徽标、状态词与点击命令）。</summary>
    public ObservableCollection<PresetItemViewModel> PresetItems { get; } = [];

    /// <summary>
    /// 预设列表当前选中项。
    /// </summary>
    /// <remarks>
    /// 新增（或同平台同名覆盖）预设后会把它设为选中项，用户能立刻看到"确实加进去了"。
    /// </remarks>
    public PresetItemViewModel? SelectedPresetItem
    {
        get => _selectedPresetItem;
        set => SetField(ref _selectedPresetItem, value);
    }

    /// <summary>是否存在预设（供空列表占位文本使用）。</summary>
    public bool HasPresets => PresetItems.Count > 0;

    /// <summary>预设列表摘要文本。</summary>
    public string PresetSummary => _presetSummary;

    /// <summary>是否正在检测预设开播状态。</summary>
    public bool IsCheckingPresets => _isCheckingPresets;

    /// <summary>当前选中的平台选项（下拉直接绑定对象，避免 SelectedValue 更新时序问题）。</summary>
    public PlatformOption SelectedPlatformOption
    {
        get => _selectedPlatformOption;
        set
        {
            if (value is null)
            {
                return;
            }

            if (SetField(ref _selectedPlatformOption, value))
            {
                // 换平台后旧的档位键没有意义，回到"平台最高档"。
                _preferredQualityKey = null;
                OnPropertyChanged(nameof(SelectedPlatform));
            }
        }
    }

    /// <summary>当前选中的平台标识（界面已不再提供平台下拉，仅作为"上次使用的平台"）。</summary>
    public PlatformId SelectedPlatform => _selectedPlatformOption.Id;

    /// <summary>最近一次解析实际使用的平台；未解析或识别失败时为 <see cref="PlatformId.Unknown"/>。</summary>
    public PlatformId EffectivePlatform => _effectivePlatform;

    /// <summary>平台状态显示文本，例如"平台：自动识别（当前 虎牙）"。</summary>
    public string PlatformStatusText => _effectivePlatform == PlatformId.Unknown
        ? "平台：未识别"
        : "平台：自动识别（当前 " + ResolvePlatformName(_effectivePlatform) + "）";

    private void RaisePlatformChanged()
    {
        OnPropertyChanged(nameof(EffectivePlatform));
        OnPropertyChanged(nameof(PlatformStatusText));
    }

    private static string ResolvePlatformName(PlatformId platform)
    {
        PlatformOption? option = PlatformOption.Find(platform);
        return option is null ? platform.ToString() : option.DisplayName;
    }

    /// <summary>房间号或链接输入。</summary>
    public string RoomInput
    {
        get => _roomInput;
        set => SetField(ref _roomInput, value);
    }

    /// <summary>是否已经解析出可播放的直播间（播放页的「开始播放」按钮据此启用）。</summary>
    public bool PlayAvailable => _currentRoom is not null && !_isBusy;

    /// <summary>是否正在执行耗时操作。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(PlayAvailable));
                RaiseCommandStates();
            }
        }
    }

    /// <summary>播放页是否已完成握手。</summary>
    public bool IsPlayerReady
    {
        get => _isPlayerReady;
        private set
        {
            if (SetField(ref _isPlayerReady, value))
            {
                RaiseCommandStates();
            }
        }
    }

    /// <summary>状态提示（同时下发给播放页，显示在画面下方的状态行）。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetField(ref _statusMessage, value))
            {
                PublishStatusToPlayer(value);
            }
        }
    }

    /// <summary>当前直播间标题。</summary>
    public string RoomTitle
    {
        get => _roomTitle;
        private set => SetField(ref _roomTitle, value);
    }

    /// <summary>当前主播名。</summary>
    public string RoomAnchor
    {
        get => _roomAnchor;
        private set => SetField(ref _roomAnchor, value);
    }

    /// <summary>当前分区。</summary>
    public string RoomCategory
    {
        get => _roomCategory;
        private set => SetField(ref _roomCategory, value);
    }

    /// <summary>
    /// 当前房间的开播状态词（直播中 / 未开播 / 轮播中 / 未知）。
    /// </summary>
    /// <remarks>
    /// 取值来自解析结果：解析成功即为"直播中"（解析器只会为在播房间产出候选），
    /// 失败时由 <see cref="ResolveMessages.DescribeLiveStatus"/> 把失败分类映射成状态词；
    /// 「停止播放」与「删除预设」后由 <see cref="ClearRoomInfo"/> 复位为"未知"（初始态），
    /// 不残留上一房间的开播状态。
    /// </remarks>
    public string LiveStatus
    {
        get => _liveStatus;
        private set => SetField(ref _liveStatus, value);
    }

    /// <summary>录制状态摘要。</summary>
    public string RecordingSummary
    {
        get => _recordingSummary;
        private set => SetField(ref _recordingSummary, value);
    }

    /// <summary>桥接服务状态文本。</summary>
    public string BridgeStatus => _bridge.IsRunning
        ? "桥接：" + _bridge.BaseAddress
        : "桥接：未启动";

    /// <summary>刷新桥接状态文本（桥接在窗口显示之后才启动，需要主动通知一次）。</summary>
    public void RefreshBridgeStatus() => OnPropertyChanged(nameof(BridgeStatus));

    /// <summary>录制输出目录（主界面展示用）。</summary>
    public string RecordingDirectory
    {
        get
        {
            string configured = _options.Recording.OutputDirectory;
            return string.IsNullOrWhiteSpace(configured) ? AppPaths.DefaultRecordingDirectory : configured;
        }
    }

    /// <summary>
    /// 处理来自播放页的消息。
    /// </summary>
    /// <param name="json">页面发来的 JSON 文本。</param>
    public void OnPlayerMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out JsonElement typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            string type = typeElement.GetString() ?? string.Empty;
            string message = root.TryGetProperty("message", out JsonElement messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString() ?? string.Empty
                : string.Empty;
            int sessionId = root.TryGetProperty("sessionId", out JsonElement sessionElement) && sessionElement.ValueKind == JsonValueKind.Number
                ? sessionElement.GetInt32()
                : 0;

            if (sessionId != 0 && _activeSessionId != 0 && sessionId != _activeSessionId)
            {
                _logger.Debug(_moduleName, "忽略过期播放会话消息。", new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId,
                    ["activeSessionId"] = _activeSessionId,
                });
                return;
            }

            switch (type)
            {
                case PlayerReadyType:
                    IsPlayerReady = true;
                    StatusMessage = "播放器已就绪。";
                    AppendLog("播放器内核已就绪（HEVC: " + ReadFlag(root, "hevc") + "，H.264: " + ReadFlag(root, "avc") + "）");
                    SendBridgeInfo();
                    SendToPlayer(new { type = "volume", value = _options.Playback.Volume });
                    break;

                case PlayerTelemetryType:
                    // 遥测里的 paused 是页面的权威状态：用户直接点画面或播放页按钮都会反映到这里。
                    _isPlaybackPaused = ReadPausedFlag(root) ?? _isPlaybackPaused;

                    // 结构化落盘：排查"卡顿/断流"时按 10 秒粒度统计缓冲、丢帧与重连次数。
                    _logger.Info(_moduleName, "播放遥测。", BuildTelemetryFields(root));
                    break;

                case PlayerStatusType:
                    // 页面自己的状态文案已经显示在它自己的状态行上，这里只更新宿主，不回推（避免状态行回声）。
                    SetPlayerReportedStatus(message.Length == 0 ? StatusMessage : message);
                    AppendLog(message);
                    if (message.Contains("播放已开始", StringComparison.Ordinal) || root.TryGetProperty("firstFrameMs", out _))
                    {
                        // 新会话已经出画，这时才回收上一轮中继（否则切换瞬间会黑屏）。
                        _playback.ReleasePreviousRelays();

                        // 真的看到了画面，之前的自动重试计数作废。
                        _automaticReResolveCount = 0;
                        _isPlaybackPaused = false;
                    }
                    else if (message.Contains("已暂停播放", StringComparison.Ordinal))
                    {
                        _isPlaybackPaused = true;
                    }
                    else if (message.Contains("已继续播放", StringComparison.Ordinal))
                    {
                        _isPlaybackPaused = false;
                    }

                    break;

                case PlayerErrorType:
                    StatusMessage = message;
                    AppendLog("播放错误：" + message);
                    break;

                case PlayerLogType:
                    // 页面日志全部进宿主日志：页面不再有日志面板，这里只负责落盘与"最近事件"。
                    HandlePlayerLog(root);
                    break;

                case PlayerRefreshNeededType:
                    AppendLog("播放页请求重新解析：" + message);
                    HandleRefreshNeeded(message);
                    break;

                case PlayerFullscreenEnterType:
                    FullscreenChanged?.Invoke(this, true);
                    break;

                case PlayerFullscreenExitType:
                    FullscreenChanged?.Invoke(this, false);
                    break;

                case PlayerQualityType:
                    _ = ApplyQualityAsync(ReadQualityKey(root));
                    break;

                case TargetType:
                    if (ReadTargetMs(root) is int targetMs)
                    {
                        ApplyPlayerTarget(targetMs);
                    }

                    break;

                case PlayerRequestPlayType:
                    AppendLog("播放页请求开始播放。");
                    _automaticReResolveCount = 0;
                    _ = PlayWithErrorHandlingAsync();
                    break;

                case PlayerTogglePauseType:
                    TogglePause();
                    break;

                case PlayerRequestStopType:
                    StopPlaybackFromPlayer();
                    break;

                default:
                    if (message.Length > 0)
                    {
                        AppendLog(message);
                    }

                    break;
            }
        }
        catch (JsonException exception)
        {
            _logger.LogError(LogLevel.Warn, _moduleName, "解析播放页消息失败。", exception);
        }
    }

    /// <summary>
    /// 解析当前输入的房间。
    /// </summary>
    /// <returns>异步任务。</returns>
    public async Task ResolveAsync()
    {
        if (string.IsNullOrWhiteSpace(_roomInput))
        {
            StatusMessage = "请先输入房间号或直播间链接。";
            return;
        }

        IsBusy = true;
        try
        {
            /*
              平台优先级：预设里显式保存的平台 > 按输入域名识别 > 设置里的「默认平台」。
              预设的平台是用户当初明确选的（对话框里手选或按链接识别后确认），识别只是"默认值"，
              不能反过来覆盖用户的选择；输入被改动后预设记录自动失效，优先级自然回到识别。
            */
            PlatformId? presetPlatform = PresetPlatformForInput(_roomInput);
            PlatformId effective = presetPlatform ?? PlatformDetector.Detect(_roomInput) ?? _options.DefaultPlatform;
            if (effective == PlatformId.Unknown)
            {
                _effectivePlatform = PlatformId.Unknown;
                RaisePlatformChanged();
                ClearRoomInfo();
                StatusMessage = PlatformDetectionHint;
                AppendLog("解析取消：" + PlatformDetectionHint);
                return;
            }

            ApplyPlatform(effective);
            RoomQuery? query = BuildQuery(effective);
            if (query is null)
            {
                return;
            }

            StatusMessage = "正在解析…";
            AppendLog("开始解析：" + _roomInput);
            ClearRoomInfo();

            ResolveOutcome outcome = await _resolver.ResolveAsync(query, CancellationToken.None).ConfigureAwait(true);
            if (!outcome.Success || outcome.Room is null)
            {
                _currentRoom = null;
                StatusMessage = ResolveMessages.DescribeFailure(outcome.Failure);
                ClearRoomInfo(ResolveMessages.DescribeLiveStatus(outcome.Failure));
                AppendLog(StatusMessage);
                RaiseCommandStates();
                return;
            }

            _currentRoom = outcome.Room;
            ShowCurrentRoomInfo();
            StatusMessage = $"解析成功：{outcome.Room.Anchor} / {outcome.Room.Title}"
                + $"（状态：{LiveStatus}，共 {outcome.Room.Candidates.Count} 条线路）";
            AppendLog(StatusMessage);

            PersistLastInput();

            // 自动开始播放（是否播放由设置里的开关决定；mpv 外挂播放同属"开始播放"）。
            await PlayAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    /// <summary>
    /// 把「当前直播」卡片重置为初始占位（解析前 / 解析失败 / 停止播放 / 删除预设时使用）。
    /// </summary>
    /// <param name="liveStatus">要显示的开播状态词；缺省为"未知"。</param>
    /// <remarks>
    /// 解析一开始就清空，避免上一轮解析残留的主播名/状态被当成这一轮的结果；
    /// 「停止播放」与「删除预设」也用它把卡片清空为初始态（不残留上一房间的信息）；
    /// 状态词由调用方给出（失败时来自失败分类），不在 UI 层做任何判断。
    /// </remarks>
    private void ClearRoomInfo(string? liveStatus = null)
    {
        RoomTitle = PlaceholderText;
        RoomAnchor = PlaceholderText;
        RoomCategory = PlaceholderText;
        LiveStatus = liveStatus ?? ResolveMessages.LiveStatusUnknown;
    }

    /// <summary>
    /// 把「当前直播」卡片刷新为已解析房间（<see cref="_currentRoom"/>）的信息。
    /// </summary>
    /// <remarks>
    /// 「停止播放」与「删除预设」都会把卡片清空（见 <see cref="ClearRoomInfo"/>），
    /// 重新下发播放前必须把卡片恢复成该房间的信息，否则会出现"画面在播、卡片却是空的"。
    /// </remarks>
    private void ShowCurrentRoomInfo()
    {
        if (_currentRoom is null)
        {
            ClearRoomInfo();
            return;
        }

        RoomTitle = _currentRoom.Title;
        RoomAnchor = _currentRoom.Anchor;
        RoomCategory = string.IsNullOrWhiteSpace(_currentRoom.Category) ? PlaceholderText : _currentRoom.Category;
        LiveStatus = ResolveMessages.LiveStatusLive;
    }

    /// <summary>判断某个预设是否就是「当前直播」卡片里的那个房间（按平台与房间号比对）。</summary>
    /// <param name="preset">待判断的预设。</param>
    /// <returns>是当前房间时返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 「删除预设」只清空它自己对应的卡片：删掉别的预设时，用户正在看的房间信息不该被一起抹掉。
    /// 预设里存的是用户输入的原文（可能是 URL 或短号），所以既做等值比较也做包含比较。
    /// </remarks>
    private bool IsCurrentRoomFromPreset(RoomPreset preset)
    {
        if (_currentRoom is null || _currentRoom.Platform != preset.Platform)
        {
            return false;
        }

        string input = preset.RoomInput.Trim();
        return input.Length > 0
            && (string.Equals(input, _currentRoom.RoomId, StringComparison.OrdinalIgnoreCase)
                || input.Contains(_currentRoom.RoomId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 用户点「开始播放」：视为手动重试，先清空自动重试计数再下发播放。
    /// </summary>
    /// <returns>异步任务。</returns>
    private async Task PlayFromUserAsync()
    {
        _automaticReResolveCount = 0;
        await PlayAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 用户点「解析房间」：视为手动重试，先清空自动重试计数再解析。
    /// </summary>
    /// <returns>异步任务。</returns>
    private async Task ResolveFromUserAsync()
    {
        _automaticReResolveCount = 0;
        await ResolveAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 把当前房间交给播放页播放。
    /// </summary>
    /// <returns>异步任务。</returns>
    public async Task PlayAsync()
    {
        if (_currentRoom is null)
        {
            StatusMessage = "请先解析直播间。";
            return;
        }

        /*
          「停止播放」与「删除预设」都会把「当前直播」卡片清空，这里在下发播放前恢复它：
          卡片描述的是"这次要看的房间"，与是否真的出画无关，用户点了播放就该看到房间信息。
        */
        ShowCurrentRoomInfo();

        // 解析成功后由本方法统一决定"是否开始播放"，设置里的自动播放开关在这里生效。
        if (!_options.Playback.AutoPlayOnResolve)
        {
            StatusMessage = "已解析完成；设置里已关闭「解析成功后自动播放」，点「开始播放」即可观看。";
            return;
        }

        // mpv 外挂优先：它不依赖播放页；启动成功就不再往页面下发候选线路。
        if (_options.Playback.AutoLaunchMpv && await PlayWithMpvCoreAsync(updateStatus: false).ConfigureAwait(true))
        {
            StatusMessage = "已用 mpv 外挂播放。";
            AppendLog(StatusMessage);
            return;
        }

        if (!_isPlayerReady)
        {
            StatusMessage = "播放器内核尚未就绪，请稍候。";
            return;
        }

        IsBusy = true;
        try
        {
            PlaybackRequest request = new()
            {
                Room = _currentRoom,
                Mode = PlaybackMode.Extreme,
                ExtremeTargetMs = _extremeTargetMs,
                AllowRelay = true,
            };

            PlaybackPlan plan = await _playback.PrepareAsync(request, CancellationToken.None).ConfigureAwait(true);
            _activeSessionId = plan.SessionId;

            // 新会话一定是从播放状态开始，按钮回到「暂停播放」。
            _isPlaybackPaused = false;

            // 档位入口在播放页底部，播放计划里的目标值即"当前档位"，记下来供下次播放沿用。
            if (plan.ExtremeTargetMs != _extremeTargetMs)
            {
                _extremeTargetMs = NormalizeTarget(plan.ExtremeTargetMs);
                PersistPlaybackSettings();
            }

            StatusMessage = plan.Hint ?? $"已下发 {plan.Candidates.Count} 条候选线路（{plan.Mode} {plan.ExtremeTargetMs}ms）。";

            SendToPlayer(new
            {
                type = HostPlayType,
                sessionId = plan.SessionId,
                mode = plan.Mode,
                extremeTargetMs = plan.ExtremeTargetMs,
                title = plan.Room.Title,
                candidates = plan.Candidates,
                qualities = plan.Qualities,
                selectedQualityKey = plan.SelectedQualityKey,
            });

            if (plan.HasUnsupportedCodec)
            {
                AppendLog("提示：存在 HEVC 候选，若系统不支持 HEVC 解码，请使用「mpv 播放」。");
            }

            if (plan.Hint is not null)
            {
                AppendLog(plan.Hint);
            }
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    /// <summary>
    /// 用户点「mpv 播放」：用 mpv 外挂播放当前房间的第一个可播放候选。
    /// </summary>
    /// <returns>异步任务。</returns>
    public async Task PlayWithMpvAsync()
    {
        if (_currentRoom is null)
        {
            StatusMessage = "请先解析直播间。";
            return;
        }

        await PlayWithMpvCoreAsync(updateStatus: true).ConfigureAwait(true);
    }

    /// <summary>
    /// 调用桥接服务用 mpv 播放当前房间的第一个候选。
    /// </summary>
    /// <param name="updateStatus">是否直接改写状态提示（自动播放路径由调用方统一改写）。</param>
    /// <returns>mpv 已成功启动返回 <see langword="true"/>。</returns>
    private async Task<bool> PlayWithMpvCoreAsync(bool updateStatus)
    {
        if (_currentRoom is null)
        {
            if (updateStatus)
            {
                StatusMessage = "请先解析直播间。";
            }

            return false;
        }

        StreamCandidate? candidate = null;
        foreach (StreamCandidate item in _currentRoom.Candidates)
        {
            candidate = item;
            break;
        }

        if (candidate is null)
        {
            if (updateStatus)
            {
                StatusMessage = "没有可用线路。";
            }

            return false;
        }

        bool started = await _bridge
            .PlayWithMpvAsync(candidate.Url, _currentRoom.Anchor + " · " + _currentRoom.Title, candidate.HttpReferer, CancellationToken.None)
            .ConfigureAwait(true);

        if (updateStatus)
        {
            StatusMessage = started
                ? "已用 mpv 外挂播放。"
                : "未找到 mpv，请把 mpv.exe 放到 tools 目录或在设置中指定路径。";
            AppendLog(StatusMessage);
        }

        return started;
    }

    /// <summary>
    /// 开始录制当前房间。
    /// </summary>
    /// <returns>异步任务。</returns>
    public async Task StartRecordingAsync()
    {
        if (_currentRoom is null)
        {
            StatusMessage = "请先解析直播间。";
            return;
        }

        RecordingRequest request = new()
        {
            Room = _currentRoom,
            SegmentPolicy = _options.Recording.Segment,
            MaxDurationMinutes = _options.Recording.MaxDurationMinutes,
        };

        _recordingSession = await _recording.StartAsync(request, CancellationToken.None).ConfigureAwait(true);
        RecordingSummary = $"录制中：{_recordingSession.Status.CurrentFileName}";
        StatusMessage = "录制已开始，输出目录：" + RecordingDirectory;
        AppendLog("录制已开始：" + RecordingDirectory);
        RaiseCommandStates();
    }

    /// <summary>
    /// 停止当前录制。
    /// </summary>
    /// <returns>异步任务。</returns>
    public async Task StopRecordingAsync()
    {
        if (_currentRoom is null || _recordingSession is null)
        {
            return;
        }

        bool stopped = await _recording
            .StopAsync(_currentRoom.Platform, _currentRoom.RoomId, CancellationToken.None)
            .ConfigureAwait(true);
        _recordingSession = null;
        RecordingSummary = stopped ? "录制已停止" : "未录制";
        StatusMessage = "录制已停止。";
        AppendLog(StatusMessage);
        RaiseCommandStates();
    }

    /// <summary>
    /// 刷新录制状态摘要（由界面定时器调用）。
    /// </summary>
    public void RefreshRecordingStatus()
    {
        if (_recordingSession is null)
        {
            return;
        }

        RecordingStatus status = _recordingSession.Status;
        RecordingSummary = status.IsRecording
            ? $"录制中 {status.Duration:hh\\:mm\\:ss} · {status.TotalBytes / (1024.0 * 1024.0):F1} MiB · 分片 {status.CurrentSegmentIndex + 1} · 重连 {status.ReconnectCount}"
            : "录制已结束（" + _recordingSession.StopReason + "）";

        if (!status.IsRecording)
        {
            _recordingSession = null;
            RaiseCommandStates();
        }
    }

    private async Task ReResolveAsync()
    {
        try
        {
            await ResolveAsync().ConfigureAwait(true);
            await PlayAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            await HandleCommandErrorAsync(exception).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 处理播放页的"所有候选都连不上"请求：有上限地自动重新解析，超过上限就停下来提示用户。
    /// </summary>
    /// <param name="reason">播放页给出的原因。</param>
    /// <remarks>
    /// 斗鱼等平台的部分房间只会解析出 1 条候选，且该地址带时效签名；
    /// 地址一过期就会"解析成功 → 连不上 → 请求重新解析 → 又拿到同一条地址"，
    /// 表现为界面一直显示重连。这里限制连续自动重试次数（见 <see cref="MaxAutomaticReResolves"/>），
    /// 到上限后交给用户手动重试或改用 mpv，不再无限循环。
    /// </remarks>
    private void HandleRefreshNeeded(string reason)
    {
        if (_automaticReResolveCount >= MaxAutomaticReResolves)
        {
            StatusMessage = $"已停止自动重试（连续 {_automaticReResolveCount} 次重新解析都未能出画）。"
                + "请点「开始播放」手动重试，或改用「mpv 播放」。原因：" + reason;
            AppendLog(StatusMessage);
            return;
        }

        _automaticReResolveCount++;
        AppendLog($"自动重新解析第 {_automaticReResolveCount}/{MaxAutomaticReResolves} 次。");
        _ = ReResolveAsync();
    }

    /// <summary>
    /// 取"点击预设"记下的平台。
    /// </summary>
    /// <param name="input">当前房间输入。</param>
    /// <returns>预设对应的平台；没有记录、或输入已被用户改动时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 只在输入与预设内容逐字一致时生效：用户改了输入框就不再沿用该预设的平台，
    /// 回到"按域名识别，识别不出用默认平台"的常规路径。
    /// </remarks>
    private PlatformId? PresetPlatformForInput(string input)
    {
        if (_presetPlatformOverride is not { } preset)
        {
            return null;
        }

        return string.Equals(preset.Input, input.Trim(), StringComparison.Ordinal) ? preset.Platform : null;
    }

    /// <summary>切换当前平台并刷新界面提示。</summary>
    /// <param name="platform">生效的平台。</param>
    private void ApplyPlatform(PlatformId platform)
    {
        PlatformOption? option = PlatformOption.Find(platform);
        if (option is not null && option.Id != _selectedPlatformOption.Id)
        {
            SelectedPlatformOption = option;
            AppendLog("已根据链接自动切换到" + option.DisplayName + "平台。");
        }

        _effectivePlatform = platform;
        RaisePlatformChanged();
    }

    /// <summary>按指定平台构造解析请求。</summary>
    /// <param name="platform">生效的平台。</param>
    /// <returns>解析请求；输入为空时返回 <see langword="null"/>。</returns>
    private RoomQuery? BuildQuery(PlatformId platform)
    {
        string input = _roomInput.Trim();
        if (input.Length == 0)
        {
            StatusMessage = "请先输入房间号或直播间链接。";
            return null;
        }

        bool looksLikeUrl = input.Contains("://", StringComparison.Ordinal)
            || input.Contains('/', StringComparison.Ordinal);

        RoomQuery query = looksLikeUrl
            ? RoomQuery.FromUrl(platform, input)
            : RoomQuery.FromRoomId(platform, input);

        // Cookie 只参与解析（换最高画质），播放地址本身不带登录态。
        return query with
        {
            Cookie = _options.Platforms.ForPlatform(platform),
            PreferredQualityKey = _preferredQualityKey,
        };
    }

    /// <summary>为指定平台构造解析请求（预设点击路径，平台来自预设本身）。</summary>
    /// <param name="platform">预设的平台。</param>
    /// <param name="input">预设的房间号或链接。</param>
    /// <returns>解析请求。</returns>
    private RoomQuery BuildQueryFor(PlatformId platform, string input)
    {
        string trimmed = input.Trim();
        bool looksLikeUrl = trimmed.Contains("://", StringComparison.Ordinal)
            || trimmed.Contains('/', StringComparison.Ordinal);

        RoomQuery query = looksLikeUrl
            ? RoomQuery.FromUrl(platform, trimmed)
            : RoomQuery.FromRoomId(platform, trimmed);

        return query with
        {
            Cookie = _options.Platforms.ForPlatform(platform),
            PreferredQualityKey = _preferredQualityKey,
        };
    }

    /// <summary>
    /// 暂停或继续播放。
    /// </summary>
    /// <remarks>
    /// 暂停等价于"原地停住"：不结束播放会话、不释放中继、不清空当前地址，
    /// 播放页保留播放器与缓冲，继续播放时从当前位置恢复。
    /// </remarks>
    private void TogglePause()
    {
        bool paused = !_isPlaybackPaused;
        _isPlaybackPaused = paused;
        SendToPlayer(new { type = HostPauseType, paused = paused });
        StatusMessage = paused ? "已暂停播放（会话与地址保留）。" : "已继续播放。";
        AppendLog(StatusMessage);
    }

    /// <summary>
    /// 播放页点了「停止播放」：画面已由页面关闭，宿主负责释放本轮中继并回到"未播放"状态。
    /// </summary>
    /// <remarks>
    /// 与「暂停播放」的区别：暂停保留会话、地址与中继，可原地继续；停止等同于结束本次观看，
    /// 中继地址是宿主注册的，页面无权释放，所以必须由这里调用
    /// <see cref="IPlaybackCoordinator.StopActive"/> 回收新旧两轮中继，否则它们会一直挂到进程退出。
    /// </remarks>
    private void StopPlaybackFromPlayer()
    {
        _playback.StopActive();
        _activeSessionId = 0;
        _isPlaybackPaused = false;

        // 本次观看已结束：左侧「当前直播」卡片回到初始态，不残留上一房间的开播状态 / 主播 / 标题 / 分区。
        ClearRoomInfo();
        StatusMessage = "已停止播放（画面已关闭，中继已释放）。";
        AppendLog(StatusMessage);
    }

    /// <summary>
    /// 播放页请求开始播放：走与用户点「开始播放」完全相同的路径，并统一上报异常。
    /// </summary>
    /// <returns>异步任务。</returns>
    private async Task PlayWithErrorHandlingAsync()
    {
        try
        {
            await PlayFromUserAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            await HandleCommandErrorAsync(exception).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 播放页切换画质档位：记住档位键，按该档位重新解析并重新下发播放计划。
    /// </summary>
    /// <param name="qualityKey">档位键；<see langword="null"/> 表示平台最高档。</param>
    /// <returns>异步任务。</returns>
    private async Task ApplyQualityAsync(string? qualityKey)
    {
        if (_currentRoom is null || string.Equals(qualityKey, _preferredQualityKey, StringComparison.Ordinal))
        {
            return;
        }

        _preferredQualityKey = qualityKey;
        AppendLog("切换画质：" + (qualityKey ?? QualityOption.BestFlag));
        _automaticReResolveCount = 0;
        await ResolveAsync().ConfigureAwait(true);
        if (_currentRoom is not null)
        {
            await PlayAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 处理播放页日志：按级别落盘，并把文本追加到界面的"最近事件"。
    /// </summary>
    /// <param name="root">日志消息根元素。</param>
    /// <remarks>
    /// 页面不再显示日志面板，页面侧的所有诊断文本都走这条通道；
    /// 级别只影响落盘级别与 WARN 前缀，避免"页面里的提示语"影响界面状态行。
    /// </remarks>
    private void HandlePlayerLog(JsonElement root)
    {
        if (!root.TryGetProperty("message", out JsonElement messageElement) || messageElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string text = messageElement.GetString() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        string level = root.TryGetProperty(LevelFieldName, out JsonElement levelElement) && levelElement.ValueKind == JsonValueKind.String
            ? levelElement.GetString() ?? LevelInfo
            : LevelInfo;
        Dictionary<string, object?> fields = new(StringComparer.Ordinal)
        {
            ["source"] = "player-page",
            ["level"] = level,
            ["message"] = text,
        };

        switch (level)
        {
            case LevelError:
                _logger.Error(_moduleName, "播放页日志。", fields);
                break;
            case LevelWarn:
                _logger.Warn(_moduleName, "播放页日志。", fields);
                break;
            default:
                _logger.Debug(_moduleName, "播放页日志。", fields);
                break;
        }

        AppendLog(text);
    }

    /// <summary>读取播放页传来的画质档位键。</summary>
    /// <param name="root">消息根元素。</param>
    /// <returns>档位键；缺省或"最高档"时返回 <see langword="null"/>。</returns>
    private static string? ReadQualityKey(JsonElement root)
    {
        if (!root.TryGetProperty("key", out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? key = element.GetString();
        return string.IsNullOrWhiteSpace(key) || string.Equals(key, QualityOption.BestFlag, StringComparison.OrdinalIgnoreCase)
            ? null
            : key;
    }

    /// <summary>
    /// 播放页在底部档位控件里改变追帧目标时同步到宿主：记住档位供下次播放使用。
    /// </summary>
    /// <param name="targetMs">页面报告的目标延迟（毫秒）。</param>
    private void ApplyPlayerTarget(int targetMs)
    {
        int normalized = NormalizeTarget(targetMs);
        if (normalized == _extremeTargetMs)
        {
            return;
        }

        _extremeTargetMs = normalized;
        PersistPlaybackSettings();
        AppendLog("播放页把追帧档位改为 " + normalized + " ms（下次播放沿用）");
    }

    /// <summary>读取播放页传来的追帧目标延迟。</summary>
    /// <param name="root">消息根元素。</param>
    /// <returns>目标延迟（毫秒）；字段缺失或非法时返回 <see langword="null"/>。</returns>
    private static int? ReadTargetMs(JsonElement root)
    {
        if (!root.TryGetProperty(TargetFieldName, out JsonElement element) || element.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return element.TryGetInt32(out int value) ? value : null;
    }

    /// <summary>读取播放页遥测里的暂停标记。</summary>
    /// <param name="root">消息根元素。</param>
    /// <returns>暂停返回 <see langword="true"/>、播放返回 <see langword="false"/>；字段缺失时返回 <see langword="null"/>。</returns>
    private static bool? ReadPausedFlag(JsonElement root)
    {
        if (!root.TryGetProperty(PausedFieldName, out JsonElement element)
            || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        return element.GetBoolean();
    }

    /// <summary>打开独立的设置窗口，保存后立即生效并把新音量下发给播放页。</summary>
    private void OpenSettings()
    {
        try
        {
            SettingsViewModel settings = new(_resolveMpvPath, _logger, LogLines);
            settings.Load(_options);
            AppendLog("打开设置窗口。");

            if (!_showSettingsDialog(settings))
            {
                StatusMessage = "已取消设置。";
                return;
            }

            if (!settings.TryBuildOptions(_options, out StreamPilotOptions updated))
            {
                StatusMessage = settings.StatusMessage;
                AppendLog("设置未保存：" + settings.StatusMessage);
                return;
            }

            _options = updated;
            _saveOptions(_options);

            _extremeTargetMs = NormalizeTarget(_options.Playback.ExtremeTargetMs);
                        OnPropertyChanged(nameof(RecordingDirectory));

            ApplyPlatformChangeFromSettings();
            RefreshBridgeStatus();
            SendToPlayer(new { type = "volume", value = _options.Playback.Volume });

            StatusMessage = "设置已保存。";
            AppendLog("设置已保存");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(LogLevel.Error, _moduleName, "打开或保存设置失败。", exception);
            StatusMessage = "设置操作失败：" + exception.Message;
            AppendLog("设置操作失败：" + exception.Message);
        }
    }

    /// <summary>设置窗口保存后同步默认平台与界面提示。</summary>
    private void ApplyPlatformChangeFromSettings()
    {
        PlatformOption? option = PlatformOption.Find(_options.DefaultPlatform);
        if (option is not null && option.Id != _selectedPlatformOption.Id)
        {
            SelectedPlatformOption = option;
        }

        _effectivePlatform = option is null ? PlatformId.Unknown : option.Id;
        RaisePlatformChanged();
        UpdatePresetSummary();
    }

    /// <summary>按存储内容重建预设列表（不触发开播检查）。</summary>
    /// <param name="selectName">重建后要选中的预设显示名；为空时尽量保持原有选中项。</param>
    private void RefreshPresetItems(string? selectName = null)
    {
        string? keepName = selectName ?? _selectedPresetItem?.Preset.Name;

        foreach (PresetItemViewModel previous in _presetItemSubscriptions)
        {
            previous.PropertyChanged -= OnPresetItemPropertyChanged;
        }

        _presetItemSubscriptions.Clear();
        PresetItems.Clear();
        PresetItemViewModel? restored = null;
        foreach (RoomPreset preset in _presetStore.Items)
        {
            PresetItemViewModel item = new(preset, ApplyPresetCommand);
            item.PropertyChanged += OnPresetItemPropertyChanged;
            _presetItemSubscriptions.Add(item);
            PresetItems.Add(item);
            if (restored is null && keepName is not null && string.Equals(preset.Name, keepName, StringComparison.Ordinal))
            {
                restored = item;
            }
        }

        SelectedPresetItem = restored;
        OnPropertyChanged(nameof(HasPresets));
        UpdatePresetSummary();
        (DeletePresetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SavePresetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>预设项状态变化时同步摘要（开播数量、失败数量）。</summary>
    /// <param name="sender">变化的事件源。</param>
    /// <param name="e">属性变化参数。</param>
    private void OnPresetItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(PresetItemViewModel.State), StringComparison.Ordinal))
        {
            UpdatePresetSummary();
        }
    }

    /// <summary>刷新预设摘要文本。</summary>
    private void UpdatePresetSummary()
    {
        int total = PresetItems.Count;
        int live = 0;
        int failed = 0;
        foreach (PresetItemViewModel item in PresetItems)
        {
            if (item.State == PresetLiveState.Live)
            {
                live++;
            }
            else if (item.State == PresetLiveState.Failed)
            {
                failed++;
            }
        }

        _presetSummary = total == 0
            ? "暂无预设"
            : $"共 {total} 个预设 · 开播 {live} 个 · 检查失败 {failed} 个";
        OnPropertyChanged(nameof(PresetSummary));
    }

    /// <summary>
    /// 依次检测所有预设是否开播。
    /// </summary>
    /// <remarks>
    /// 并发上限 <see cref="PresetCheckConcurrency"/>，单个预设 10 秒超时；检查失败的预设只标记自己，
    /// 不影响其它预设。整个流程可被 <see cref="CancelPresetChecks"/> 取消，绝不在 UI 线程阻塞。
    /// </remarks>
    /// <returns>异步任务。</returns>
    private async Task RefreshPresetStatusAsync()
    {
        if (PresetItems.Count == 0)
        {
            UpdatePresetSummary();
            return;
        }

        // 已有检测在跑时先取消它：避免两轮检测同时改写同一批列表项。
        _presetCheckCancellation?.Cancel();

        SetPresetChecking(true);
        CancellationTokenSource cancellation = new();
        _presetCheckCancellation = cancellation;
        try
        {
            using SemaphoreSlim gate = new(PresetCheckConcurrency, PresetCheckConcurrency);
            List<Task> checks = [];
            foreach (PresetItemViewModel item in PresetItems)
            {
                checks.Add(CheckPresetCoreAsync(item, gate, cancellation.Token));
            }

            await Task.WhenAll(checks).ConfigureAwait(true);
            AppendLog($"预设开播检测完成（共 {PresetItems.Count} 个）。");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            await HandleCommandErrorAsync(exception).ConfigureAwait(true);
        }
        finally
        {
            _presetCheckCancellation = null;
            cancellation.Dispose();
            SetPresetChecking(false);
            UpdatePresetSummary();
        }
    }

    /// <summary>
    /// 取消进行中的预设开播检测。
    /// </summary>
    /// <remarks>窗口关闭时由宿主调用，避免检测回调打到已经开始释放的界面。</remarks>
    public void CancelPresetChecks() => _presetCheckCancellation?.Cancel();

    /// <summary>切换"检查中"状态并刷新相关命令的可用性。</summary>
    /// <param name="isChecking">是否正在检查。</param>
    private void SetPresetChecking(bool isChecking)
    {
        _isCheckingPresets = isChecking;
        OnPropertyChanged(nameof(IsCheckingPresets));
        (RefreshPresetStatusCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>在并发闸门与超时保护下检测单个预设；单个预设的失败不会影响其它预设。</summary>
    /// <param name="item">预设项。</param>
    /// <param name="gate">并发闸门。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task CheckPresetCoreAsync(PresetItemViewModel item, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(PresetCheckTimeout);
                await CheckPresetAsync(item, timeout.Token).ConfigureAwait(true);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            item.ApplyFailure("检查已取消。");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            item.ApplyFailure("检查失败：" + exception.Message);
            _logger.LogError(LogLevel.Warn, _moduleName, "预设开播检测任务异常。", exception, new Dictionary<string, object?>
            {
                ["preset"] = item.Preset.Name,
                ["platform"] = item.Platform.ToString(),
            });
        }
    }

    /// <summary>检测单个预设是否开播，并把结果写回列表项。</summary>
    /// <param name="item">预设项。</param>
    /// <param name="cancellationToken">带超时的取消令牌。</param>
    /// <returns>异步任务。</returns>
    private async Task CheckPresetAsync(PresetItemViewModel item, CancellationToken cancellationToken)
    {
        item.ApplyChecking();
        try
        {
            ResolveOutcome outcome = await _resolver
                .ResolveAsync(BuildQueryFor(item.Platform, item.RoomInput), cancellationToken)
                .ConfigureAwait(true);

            if (outcome.Success && outcome.Room is not null)
            {
                // 平台主播名只作兜底展示：预设里用户设定的名称始终优先（见 PresetItemViewModel.AnchorName）。
                item.ApplyLive(outcome.Room.Anchor);
                return;
            }

            if (outcome.Failure == ResolveFailure.NotLive)
            {
                // 未开播时平台不返回主播名：传预设名，展示仍以用户设定的名称为准。
                item.ApplyOffline(item.Preset.Name);
                return;
            }

            string reason = outcome.Message.Length > 0 ? outcome.Message : ResolveMessages.DescribeFailure(outcome.Failure);
            item.ApplyFailure(reason);
            _logger.Warn(_moduleName, "预设开播检测失败。", new Dictionary<string, object?>
            {
                ["preset"] = item.Preset.Name,
                ["platform"] = item.Platform.ToString(),
                ["failure"] = outcome.Failure.ToString(),
            });
        }
        catch (OperationCanceledException)
        {
            item.ApplyFailure("检查超时或已取消（可在网络空闲时点「刷新状态」重试）。");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            item.ApplyFailure("检查失败：" + exception.Message);
            _logger.LogError(LogLevel.Warn, _moduleName, "预设开播检测异常。", exception, new Dictionary<string, object?>
            {
                ["preset"] = item.Preset.Name,
                ["platform"] = item.Platform.ToString(),
            });
        }
    }

    /// <summary>点击预设：按预设自己的平台解析该房间（自动播放由设置里的开关决定，不再重复下发）。</summary>
    /// <param name="item">被点击的预设项。</param>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 预设里存的是房间号（例如虎牙的 <c>660000</c>），单看房间号无法识别平台，
    /// 因此这里先记下"该预设的平台 + 输入文本"，交给 <see cref="ResolveAsync"/> 使用；
    /// 不这样做的话，非 B 站的预设会被当成 B 站房间去解析。
    /// </remarks>
    private async Task ApplyPresetAsync(PresetItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        PlatformOption? option = PlatformOption.Find(item.Platform);
        if (option is not null)
        {
            SelectedPlatformOption = option;
        }

        _presetPlatformOverride = (item.Platform, item.RoomInput);
        RoomInput = item.RoomInput;
        AppendLog("载入预设：" + item.Preset.Name + " → " + item.RoomInput
            + "（" + ResolvePlatformName(item.Platform) + "）");
        _automaticReResolveCount = 0;
        await ResolveAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 新增预设：弹出三字段对话框（主播名称 + 直播链接 + 平台），解析成功后保存并在列表里选中它。
    /// </summary>
    /// <remarks>
    /// 预设内容全部来自对话框，不读取「直播源」卡片里当前的输入；平台取对话框里选中的值
    /// （链接能识别出平台时对话框会自动改选，用户也可以手动指定），
    /// 解析走与「解析房间」相同的 <see cref="IRoomResolver"/> 流程。
    /// 解析失败或超时一律不保存：地址不可用的预设只会让用户在列表里反复点到解析失败。
    /// </remarks>
    /// <returns>异步任务。</returns>
    private async Task SavePresetAsync()
    {
        PresetDialog.Show(System.Windows.Application.Current?.MainWindow, SelectedPlatform, AddPresetFromDialogAsync);
        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>
    /// 「新增预设」对话框的校验回调：按对话框选定的平台解析直播链接，成功则保存预设并返回 <see langword="null"/>。
    /// </summary>
    /// <param name="anchorName">主播名称（作为预设名）。</param>
    /// <param name="link">直播链接或房间号。</param>
    /// <param name="platform">用户在对话框里选定的平台。</param>
    /// <returns>失败原因；成功返回 <see langword="null"/>。</returns>
    private async Task<string?> AddPresetFromDialogAsync(string anchorName, string link, PlatformId platform)
    {
        string name = anchorName.Trim();
        string input = link.Trim();
        if (name.Length == 0)
        {
            return "请填写主播名称。";
        }

        if (input.Length == 0)
        {
            return "请填写直播链接（可粘贴直播间网址或房间号）。";
        }

        if (name.Length > PresetStore.MaxNameLength)
        {
            return $"主播名称最长 {PresetStore.MaxNameLength} 字，请缩短。";
        }

        ResolvedRoom? room = await ResolvePresetLinkAsync(input, platform).ConfigureAwait(true);
        if (room is null)
        {
            return PresetResolveFailureMessage();
        }

        RoomPreset preset = new(name, room.Platform, BuildPresetRoomInput(room));
        if (!_presetStore.Add(preset))
        {
            return $"保存预设失败：请检查名称（最长 {PresetStore.MaxNameLength} 字）与链接。";
        }

        // 重建列表并把新预设设为选中项，用户在列表里能立刻看到它。
        RefreshPresetItems(name);
        StatusMessage = $"已新增预设「{name}」（{ResolvePlatformName(room.Platform)}），"
            + $"正在后台检测它是否开播（当前共 {PresetItems.Count} 个）。";
        AppendLog("已新增预设：" + name + " → " + preset.RoomInput);
        _ = RefreshPresetStatusAsync();
        return null;
    }

    /// <summary>解析"新增预设"对话框里的直播链接；带超时保护，绝不无限等待。</summary>
    /// <param name="input">直播链接或房间号。</param>
    /// <param name="platform">用户选定的平台。</param>
    /// <returns>解析出的房间；失败或超时返回 <see langword="null"/>。</returns>
    private async Task<ResolvedRoom?> ResolvePresetLinkAsync(string input, PlatformId platform)
    {
        RoomQuery? query = BuildPresetQuery(input, platform);
        if (query is null)
        {
            return null;
        }

        using CancellationTokenSource timeout = new(PresetLinkResolveTimeout);
        try
        {
            ResolveOutcome outcome = await _resolver.ResolveAsync(query, timeout.Token).ConfigureAwait(true);
            if (outcome.Success && outcome.Room is not null)
            {
                return outcome.Room;
            }

            _presetResolveFailure = outcome.Message.Length > 0
                ? outcome.Message
                : ResolveMessages.DescribeFailure(outcome.Failure);
            _logger.Warn(_moduleName, "新增预设解析失败。", new Dictionary<string, object?>
            {
                ["platform"] = query.Platform.ToString(),
                ["failure"] = outcome.Failure.ToString(),
            });
            return null;
        }
        catch (OperationCanceledException)
        {
            _presetResolveFailure = PresetResolveTimeoutHint;
            _logger.Warn(_moduleName, "新增预设解析超时。", new Dictionary<string, object?>
            {
                ["platform"] = query.Platform.ToString(),
            });
            return null;
        }
    }

    /// <summary>
    /// 构造"新增预设"对话框里的解析请求。
    /// </summary>
    /// <param name="input">直播链接或房间号。</param>
    /// <param name="platform">用户在对话框里选定的平台；为 <see cref="PlatformId.Unknown"/> 时按链接域名识别并回退默认平台。</param>
    /// <returns>解析请求；平台无法确定时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 用户选定的平台优先：对话框已经按链接域名自动改选过，手动改选则是用户的明确意图，
    /// 此时不再用识别结果覆盖它；只有拿不到任何平台（含默认平台也没配）时才在对话框内提示。
    /// </remarks>
    private RoomQuery? BuildPresetQuery(string input, PlatformId platform)
    {
        PlatformId effective = platform == PlatformId.Unknown
            ? PlatformDetector.DetectOrFallback(input, _options.DefaultPlatform)
            : platform;
        if (effective == PlatformId.Unknown)
        {
            _presetResolveFailure = PresetPlatformHint;
            return null;
        }

        bool looksLikeUrl = input.Contains("://", StringComparison.Ordinal)
            || input.Contains('/', StringComparison.Ordinal);

        RoomQuery query = looksLikeUrl
            ? RoomQuery.FromUrl(effective, input)
            : RoomQuery.FromRoomId(effective, input);

        return query with
        {
            Cookie = _options.Platforms.ForPlatform(effective),
        };
    }

    /// <summary>构造预设里保存的房间输入：用平台返回的最终房间号（短号跳转后也稳定）。</summary>
    /// <param name="room">解析出的房间。</param>
    /// <returns>房间号。</returns>
    private static string BuildPresetRoomInput(ResolvedRoom room) => room.RoomId;

    /// <summary>拼装"新增预设"解析失败的提示文本（说明为什么没有保存）。</summary>
    /// <returns>面向用户的提示文本。</returns>
    private string PresetResolveFailureMessage()
    {
        string reason = _presetResolveFailure.Length > 0 ? _presetResolveFailure : ResolveMessages.DescribeFailure(ResolveFailure.Unknown);
        _presetResolveFailure = string.Empty;
        AppendLog("新增预设解析失败：" + reason + "（未保存）");
        return PresetResolveFailurePrefix + reason + PresetNotSavedSuffix;
    }

    /// <summary>删除指定预设（不从界面发起解析）。</summary>
    /// <param name="item">要删除的预设项。</param>
    /// <returns>异步任务。</returns>
    private async Task DeletePresetAsync(PresetItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        bool removed = _presetStore.Remove(item.Preset.Name);
        RefreshPresetItems();

        // 删掉的正是当前卡片对应的房间时才清空：卡片显示的是"当前正在看的房间"，
        // 删掉它却还留着开播状态，读起来就像这个房间仍在播放；删别的预设则不能牵连当前房间。
        if (IsCurrentRoomFromPreset(item.Preset))
        {
            ClearRoomInfo();
        }
        StatusMessage = removed ? "已删除预设「" + item.Preset.Name + "」。" : "删除预设失败：" + item.Preset.Name;
        AppendLog(StatusMessage);
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private void PersistPlaybackSettings()
    {
        _options = _options with
        {
            LastPlatform = ResolveLastPlatform(),
            LastRoomInput = _roomInput,
            Playback = _options.Playback with
            {
                ExtremeTargetMs = _extremeTargetMs,
            },
        };
        _saveOptions(_options);
    }

    private void PersistLastInput()
    {
        _options = _options with
        {
            LastPlatform = ResolveLastPlatform(),
            LastRoomInput = _roomInput,
        };
        _saveOptions(_options);
    }

    /// <summary>解析要写入配置的"上次使用的平台"。</summary>
    /// <returns>平台标识；从未成功识别过时返回 Unknown，避免覆盖用户已有的记录。</returns>
    private PlatformId ResolveLastPlatform() =>
        _effectivePlatform == PlatformId.Unknown ? _options.LastPlatform : _effectivePlatform;

    private void SendBridgeInfo()
    {
        SendToPlayer(new
        {
            type = HostBridgeInfoType,
            baseAddress = _bridge.BaseAddress,
        });
    }

    private void SendToPlayer(object payload)
    {
        string json = JsonSerializer.Serialize(payload, App.JsonOptions);
        SendMessageRequested?.Invoke(this, json);
    }

    /// <summary>
    /// 记录由播放页上报的状态文案（`status` 消息）：只更新宿主自己的字段，不再回推给页面。
    /// </summary>
    /// <param name="message">状态文案。</param>
    /// <remarks>
    /// 页面上报的文案本来就显示在页面自己的状态行上；回推一次会让状态行出现回声，
    /// 并把"播放中 · 稳定缓冲"这类页面状态推迟到宿主消息保留期结束之后才显示。
    /// </remarks>
    private void SetPlayerReportedStatus(string message) =>
        SetField(ref _statusMessage, message, nameof(StatusMessage));

    /// <summary>
    /// 把状态提示推送到播放页的画面下方状态行。
    /// </summary>
    /// <param name="message">状态文本。</param>
    /// <remarks>
    /// 左栏「当前直播」卡片删除后，宿主的状态文字在窗口里已无显示位置，画面下方是它唯一的显示位。
    /// 级别由 <see cref="ClassifyStatusLevel"/> 按文本推断；页面据此上色并按级别保留若干秒，
    /// 超时后回落到页面自己的播放状态（规则见 docs/architecture/player-message-contract.md 第 1.9 节）。
    /// </remarks>
    private void PublishStatusToPlayer(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        SendToPlayer(new
        {
            type = HostStatusType,
            message,
            level = ClassifyStatusLevel(message),
        });
    }

    /// <summary>
    /// 把状态文本归类为状态行级别。
    /// </summary>
    /// <param name="message">状态文本。</param>
    /// <returns><see cref="LevelError"/>、<see cref="LevelWarn"/> 或 <see cref="LevelInfo"/>。</returns>
    private static string ClassifyStatusLevel(string message)
    {
        if (ContainsAnyMarker(message, StatusErrorMarkers))
        {
            return LevelError;
        }

        return ContainsAnyMarker(message, StatusWarnMarkers) ? LevelWarn : LevelInfo;
    }

    /// <summary>判断文本是否命中任一标记片段。</summary>
    /// <param name="text">待判定文本。</param>
    /// <param name="markers">标记片段集合。</param>
    /// <returns>命中返回 true。</returns>
    private static bool ContainsAnyMarker(string text, string[] markers)
    {
        foreach (string marker in markers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadFlag(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out JsonElement element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean() ? "支持" : "不支持";
        }

        return "未知";
    }

    /// <summary>
    /// 把一条播放遥测转成结构化日志字段（用于排查卡顿：缓冲、丢帧、饥饿样本、重连次数）。
    /// </summary>
    /// <param name="root">遥测消息根元素。</param>
    /// <returns>结构化字段字典（缺失字段不写入，避免落盘一堆占位符）。</returns>
    private static Dictionary<string, object?> BuildTelemetryFields(JsonElement root)
    {
        Dictionary<string, object?> fields = new(StringComparer.Ordinal)
        {
            ["mode"] = ReadTelemetryText(root, "mode"),
        };

        foreach ((string field, string name) in TelemetryNumberFields)
        {
            if (root.TryGetProperty(field, out JsonElement element) && element.ValueKind == JsonValueKind.Number)
            {
                fields[name] = element.TryGetInt32(out int value) ? value : element.GetDouble();
            }
        }

        foreach ((string field, string name) in TelemetryBooleanFields)
        {
            if (root.TryGetProperty(field, out JsonElement element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                fields[name] = element.GetBoolean();
            }
        }

        return fields;
    }

    /// <summary>读取遥测里的字符串字段。</summary>
    /// <param name="root">遥测消息根元素。</param>
    /// <param name="name">字段名。</param>
    /// <returns>字段值；缺失时返回空字符串。</returns>
    private static string ReadTelemetryText(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        LogLines.Add(DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + text);
        while (LogLines.Count > 200)
        {
            LogLines.RemoveAt(0);
        }

        _logger.Info(_moduleName, "界面事件。", new Dictionary<string, object?>
        {
            ["message"] = text,
        });
    }

    private async Task HandleCommandErrorAsync(Exception exception)
    {
        StatusMessage = "操作失败：" + exception.Message;
        AppendLog("操作失败：" + exception.Message);
        _logger.LogError(LogLevel.Error, _moduleName, "界面命令执行失败。", exception);
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private void OpenRecordingFolder() => OpenFolder(RecordingDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("打开目录失败：" + exception.Message, "StreamPilot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RaiseCommandStates()
    {
        (ResolveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StartRecordingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopRecordingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (MpvCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(PlayAvailable));
    }

    private static int NormalizeTarget(int value)
    {
        foreach (int candidate in PlaybackRequest.ExtremeTargetsMs)
        {
            if (candidate == value)
            {
                return candidate;
            }
        }

        return PlaybackRequest.DefaultExtremeTargetMs;
    }

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
