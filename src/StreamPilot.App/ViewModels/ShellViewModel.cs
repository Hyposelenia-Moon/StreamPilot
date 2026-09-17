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

    /// <summary>宿主要求页面追帧的消息类型。</summary>
    private const string HostChaseType = "chase";

    /// <summary>宿主要求页面停止的消息类型。</summary>
    private const string HostStopType = "stop";

    /// <summary>宿主要求页面热切换追帧档位的消息类型。</summary>
    private const string HostTargetType = "target";
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
    private RoomPreset? _selectedPreset;
    private string _roomInput = string.Empty;
    private int _extremeTargetMs;
    private int _volume;
    private bool _isBusy;
    private bool _isPlayerReady;
    private string _statusMessage = "等待解析直播源。";
    private string _roomTitle = "-";
    private string _roomAnchor = "-";
    private string _roomCategory = "-";
    private string _liveStatus = ResolveMessages.LiveStatusUnknown;
    private string _recordingSummary = "未录制";
    private string _playerTelemetry = "-";
    private ResolvedRoom? _currentRoom;
    private IRecordingSession? _recordingSession;
    private int _activeSessionId;

    /// <summary>为 <see langword="true"/> 时抑制"切换预设即解析"（刷新列表时使用）。</summary>
    private bool _suppressPresetAutoApply;

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
        _volume = Math.Clamp(_options.Playback.Volume, 0, 100);

        Platforms =
        [
            new PlatformOption(PlatformId.Bilibili, "哔哩哔哩", "https://live.bilibili.com/"),
            new PlatformOption(PlatformId.Douyin, "抖音", "https://live.douyin.com/"),
            new PlatformOption(PlatformId.Huya, "虎牙", "https://www.huya.com/"),
            new PlatformOption(PlatformId.Douyu, "斗鱼", "https://www.douyu.com/"),
            new PlatformOption(PlatformId.Yy, "YY", "https://www.yy.com/"),
            new PlatformOption(PlatformId.Bigo, "Bigo Live", "https://www.bigo.tv/"),
        ];

        // 按上次使用的平台预选；找不到时回落到第一个，避免 SelectedItem 绑定拿不到实例。
        PlatformId initialPlatform = _options.LastPlatform == PlatformId.Unknown ? PlatformId.Bilibili : _options.LastPlatform;
        _selectedPlatformOption = Platforms.FirstOrDefault(option => option.Id == initialPlatform) ?? Platforms[0];

        foreach (RoomPreset preset in _presetStore.Items)
        {
            Presets.Add(preset);
        }

        // 启动时只回填选中项，不触发解析：用户点"解析房间"或切换预设才发请求。
        _suppressPresetAutoApply = true;
        try
        {
            SelectedPreset = Presets.Count > 0 ? Presets[0] : null;
        }
        finally
        {
            _suppressPresetAutoApply = false;
        }

        UpdateRoomUrlHint();

        ResolveCommand = new AsyncRelayCommand(_ => ResolveAsync(), HandleCommandErrorAsync, _ => !IsBusy);
        PlayCommand = new AsyncRelayCommand(_ => PlayAsync(), HandleCommandErrorAsync, _ => _currentRoom is not null && !IsBusy);
        StopCommand = new RelayCommand(_ => StopPlayback(), _ => _isPlayerReady);
        ChaseCommand = new RelayCommand(_ => SendToPlayer(new { type = HostChaseType, keepSeconds = 0.08 }), _ => _isPlayerReady);
        MpvCommand = new AsyncRelayCommand(_ => PlayWithMpvAsync(), HandleCommandErrorAsync, _ => _currentRoom is not null);
        StartRecordingCommand = new AsyncRelayCommand(_ => StartRecordingAsync(), HandleCommandErrorAsync, _ => _currentRoom is not null && _recordingSession is null);
        StopRecordingCommand = new AsyncRelayCommand(_ => StopRecordingAsync(), HandleCommandErrorAsync, _ => _recordingSession is not null);
        SetTargetCommand = new RelayCommand(parameter => SetTarget(parameter), parameter => parameter is not null);
        OpenRecordingFolderCommand = new RelayCommand(_ => OpenRecordingFolder());
        OpenLogFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.LogDirectory));
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        DeleteSelectedPresetCommand = new AsyncRelayCommand(_ => DeleteSelectedPresetAsync(), HandleCommandErrorAsync, _ => SelectedPreset is not null);

        // 保存预设刻意**始终可点**：按钮灰着没有任何解释，用户会以为"预设加不了"。
        // 点进去再校验并给出明确提示，比禁用按钮更容易理解。
        SavePresetCommand = new AsyncRelayCommand(_ => SavePresetAsync(), HandleCommandErrorAsync);
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

    /// <summary>已解析的候选摘要。</summary>
    public ObservableCollection<string> CandidateSummaries { get; } = [];

    /// <summary>解析命令。</summary>
    public ICommand ResolveCommand { get; }

    /// <summary>播放命令。</summary>
    public ICommand PlayCommand { get; }

    /// <summary>停止命令。</summary>
    public ICommand StopCommand { get; }

    /// <summary>追帧命令。</summary>
    public ICommand ChaseCommand { get; }

    /// <summary>mpv 外挂播放命令。</summary>
    public ICommand MpvCommand { get; }

    /// <summary>开始录制命令。</summary>
    public ICommand StartRecordingCommand { get; }

    /// <summary>停止录制命令。</summary>
    public ICommand StopRecordingCommand { get; }

    /// <summary>选择追帧档位命令（参数为毫秒字符串）。</summary>
    public ICommand SetTargetCommand { get; }

    /// <summary>打开录制目录命令。</summary>
    public ICommand OpenRecordingFolderCommand { get; }

    /// <summary>打开日志目录命令。</summary>
    public ICommand OpenLogFolderCommand { get; }

    /// <summary>打开设置窗口命令。</summary>
    public ICommand OpenSettingsCommand { get; }

    /// <summary>把当前平台与房间号保存为预设。</summary>
    public ICommand SavePresetCommand { get; }

    /// <summary>删除下拉中选中的预设。</summary>
    public ICommand DeleteSelectedPresetCommand { get; }

    /// <summary>预设列表（下拉可选、可删除）。</summary>
    public ObservableCollection<RoomPreset> Presets { get; } = [];

    /// <summary>当前选中的预设；切换时自动填入平台与房间号并开始解析。</summary>
    public RoomPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetField(ref _selectedPreset, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedPreset));
            if (value is not null && !_suppressPresetAutoApply)
            {
                _ = ApplyPresetAsync(value);
            }
        }
    }

    /// <summary>是否存在选中预设（用于按钮可用性判断）。</summary>
    public bool HasSelectedPreset => _selectedPreset is not null;

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
                UpdateRoomUrlHint();
            }
        }
    }

    /// <summary>当前选中的平台标识。</summary>
    public PlatformId SelectedPlatform => _selectedPlatformOption.Id;

    /// <summary>当前平台的链接前缀提示。</summary>
    public string RoomUrlHint => "支持房间号或链接，例如：" + _selectedPlatformOption.UrlPrefix;

    private void UpdateRoomUrlHint() => OnPropertyChanged(nameof(RoomUrlHint));

    /// <summary>房间号或链接输入。</summary>
    public string RoomInput
    {
        get => _roomInput;
        set => SetField(ref _roomInput, value);
    }

    /// <summary>极限追帧目标延迟（毫秒），仅允许 150/200/250。</summary>
    public int ExtremeTargetMs
    {
        get => _extremeTargetMs;
        set
        {
            int normalized = NormalizeTarget(value);
            if (SetField(ref _extremeTargetMs, normalized))
            {
                OnPropertyChanged(nameof(IsTarget150));
                OnPropertyChanged(nameof(IsTarget200));
                OnPropertyChanged(nameof(IsTarget250));
            }
        }
    }

    /// <summary>是否选中 150ms 档。</summary>
    public bool IsTarget150 => _extremeTargetMs == PlaybackRequest.ExtremeTargetsMs[0];

    /// <summary>是否选中 200ms 档。</summary>
    public bool IsTarget200 => _extremeTargetMs == PlaybackRequest.ExtremeTargetsMs[1];

    /// <summary>是否选中 250ms 档。</summary>
    public bool IsTarget250 => _extremeTargetMs == PlaybackRequest.ExtremeTargetsMs[2];

    /// <summary>播放音量（0-100）。</summary>
    public int Volume
    {
        get => _volume;
        set => SetField(ref _volume, Math.Clamp(value, 0, 100));
    }

    /// <summary>是否正在执行耗时操作。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
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

    /// <summary>状态提示。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
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

    /// <summary>直播状态词（直播中 / 未开播 / 轮播中 / 未知）。</summary>
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

    /// <summary>播放页遥测摘要。</summary>
    public string PlayerTelemetry
    {
        get => _playerTelemetry;
        private set => SetField(ref _playerTelemetry, value);
    }

    /// <summary>桥接服务状态文本。</summary>
    public string BridgeStatus => _bridge.IsRunning
        ? "桥接：" + _bridge.BaseAddress
        : "桥接：未启动（mpv 外挂播放不可用）";

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
                    SendToPlayer(new { type = "volume", value = _volume });
                    break;

                case PlayerTelemetryType:
                    PlayerTelemetry = BuildTelemetrySummary(root);
                    break;

                case PlayerStatusType:
                    StatusMessage = message.Length == 0 ? StatusMessage : message;
                    AppendLog(message);
                    if (message.Contains("播放已开始", StringComparison.Ordinal) || root.TryGetProperty("firstFrameMs", out _))
                    {
                        // 新会话已经出画，这时才回收上一轮中继（否则切换瞬间会黑屏）。
                        _playback.ReleasePreviousRelays();
                    }

                    break;

                case PlayerErrorType:
                    StatusMessage = message;
                    AppendLog("播放错误：" + message);
                    break;

                case PlayerRefreshNeededType:
                    AppendLog("播放页请求重新解析：" + message);
                    _ = ReResolveAsync();
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
            ApplyPlatformFromInput();
            RoomQuery query = BuildQuery();
            StatusMessage = "正在解析…";
            AppendLog("开始解析：" + _roomInput);

            ResolveOutcome outcome = await _resolver.ResolveAsync(query, CancellationToken.None).ConfigureAwait(true);
            if (!outcome.Success || outcome.Room is null)
            {
                _currentRoom = null;
                StatusMessage = ResolveMessages.DescribeFailure(outcome.Failure);
                RoomTitle = "-";
                RoomAnchor = "-";
                RoomCategory = "-";
                LiveStatus = ResolveMessages.DescribeLiveStatus(outcome.Failure);
                CandidateSummaries.Clear();
                AppendLog(StatusMessage);
                RaiseCommandStates();
                return;
            }

            _currentRoom = outcome.Room;
            RoomTitle = outcome.Room.Title;
            RoomAnchor = outcome.Room.Anchor;
            RoomCategory = string.IsNullOrWhiteSpace(outcome.Room.Category) ? "-" : outcome.Room.Category;
            LiveStatus = ResolveMessages.LiveStatusLive;
            StatusMessage = $"解析成功：{outcome.Room.Anchor} / {outcome.Room.Title}"
                + $"（状态：{LiveStatus}，共 {outcome.Room.Candidates.Count} 条线路）";
            AppendLog(StatusMessage);

            CandidateSummaries.Clear();
            foreach (StreamCandidate candidate in outcome.Room.Candidates)
            {
                CandidateSummaries.Add(
                    $"#{candidate.SourceIndex} {candidate.CdnHost} · {candidate.Format} · {ResolveMessages.ForCodec(candidate.Codec)} · {ResolveMessages.ForQuality(candidate.Quality)}");
            }

            PersistLastInput();

            if (_options.Playback.AutoLaunchMpv)
            {
                await PlayWithMpvAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
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
    /// 使用 mpv 外挂播放当前房间的第一个可播放候选。
    /// </summary>
    /// <returns>异步任务。</returns>
    public async Task PlayWithMpvAsync()
    {
        if (_currentRoom is null)
        {
            StatusMessage = "请先解析直播间。";
            return;
        }

        StreamCandidate? candidate = null;
        foreach (StreamCandidate item in _currentRoom.Candidates)
        {
            candidate = item;
            break;
        }

        if (candidate is null)
        {
            StatusMessage = "没有可用线路。";
            return;
        }

        bool started = await _bridge
            .PlayWithMpvAsync(candidate.Url, _currentRoom.Anchor + " · " + _currentRoom.Title, candidate.HttpReferer, CancellationToken.None)
            .ConfigureAwait(true);

        StatusMessage = started
            ? "已用 mpv 外挂播放。"
            : "未找到 mpv，请把 mpv.exe 放到 tools 目录或在设置中指定路径。";
        AppendLog(StatusMessage);
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
    /// 根据输入链接的域名自动切换平台（链接与当前所选平台不一致时）。
    /// </summary>
    /// <remarks>
    /// 用户经常直接粘贴别家平台的链接而忘了换平台，旧行为会直接报"域名不属于当前平台"。
    /// 这里在解析前按域名纠正一次，并在日志里说明。
    /// </remarks>
    private void ApplyPlatformFromInput()
    {
        string input = _roomInput.Trim();
        if (!input.Contains("://", StringComparison.Ordinal)
            || !Uri.TryCreate(input, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        foreach (PlatformOption option in Platforms)
        {
            if (option.Id == SelectedPlatform
                || !Uri.TryCreate(option.UrlPrefix, UriKind.Absolute, out Uri? baseUri))
            {
                continue;
            }

            bool sameHost = uri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + baseUri.Host, StringComparison.OrdinalIgnoreCase);
            if (sameHost)
            {
                SelectedPlatformOption = option;
                AppendLog("已根据链接自动切换到" + option.DisplayName + "平台。");
                return;
            }
        }
    }
    private RoomQuery BuildQuery()
    {
        string input = _roomInput.Trim();
        bool looksLikeUrl = input.Contains("://", StringComparison.Ordinal)
            || input.Contains('/', StringComparison.Ordinal);

        RoomQuery query = looksLikeUrl
            ? RoomQuery.FromUrl(SelectedPlatform, input)
            : RoomQuery.FromRoomId(SelectedPlatform, input);

        // Cookie 只参与解析（换最高画质），播放地址本身不带登录态。
        return query with
        {
            Cookie = _options.Platforms.ForPlatform(SelectedPlatform),
            PreferredQualityKey = _preferredQualityKey,
        };
    }

    private void StopPlayback()
    {
        _activeSessionId = 0;
        _playback.StopActive();
        SendToPlayer(new { type = HostStopType });
        StatusMessage = "已停止播放。";
        AppendLog("已停止播放");
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
        await ResolveAsync().ConfigureAwait(true);
        if (_currentRoom is not null)
        {
            await PlayAsync().ConfigureAwait(true);
        }
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

    private void SetTarget(object? parameter)
    {
        if (parameter is null)
        {
            return;
        }

        if (!int.TryParse(parameter.ToString(), out int target))
        {
            return;
        }

        ExtremeTargetMs = target;
        PersistPlaybackSettings();

        // 正在播放时热切换：把新档位直接下发给页面，而不是等下一次播放才生效。
        if (_isPlayerReady && _activeSessionId != 0)
        {
            SendToPlayer(new { type = HostTargetType, extremeTargetMs = ExtremeTargetMs });
            AppendLog("追帧档位切换为 " + ExtremeTargetMs + " ms（已应用到当前播放）");
            return;
        }

        AppendLog("追帧档位切换为 " + ExtremeTargetMs + " ms（下次播放生效）");
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
            _volume = Math.Clamp(_options.Playback.Volume, 0, 100);
            OnPropertyChanged(nameof(ExtremeTargetMs));
            OnPropertyChanged(nameof(IsTarget150));
            OnPropertyChanged(nameof(IsTarget200));
            OnPropertyChanged(nameof(IsTarget250));
            OnPropertyChanged(nameof(Volume));
            OnPropertyChanged(nameof(RecordingDirectory));

            RefreshPresets();
            SendToPlayer(new { type = "volume", value = _volume });

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

    /// <summary>刷新预设列表（以存储内容为准，且不触发自动解析）。</summary>
    private void RefreshPresets()
    {
        bool previous = _suppressPresetAutoApply;
        _suppressPresetAutoApply = true;
        try
        {
            Presets.Clear();
            foreach (RoomPreset preset in _presetStore.Items)
            {
                Presets.Add(preset);
            }

            SelectedPreset = Presets.Count > 0 ? Presets[0] : null;
            (DeleteSelectedPresetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (SavePresetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
        finally
        {
            _suppressPresetAutoApply = previous;
        }
    }

    /// <summary>切换预设：填入平台与房间号并立即解析。</summary>
    /// <param name="preset">预设。</param>
    /// <returns>异步任务。</returns>
    private async Task ApplyPresetAsync(RoomPreset preset)
    {
        PlatformOption? option = Platforms.FirstOrDefault(item => item.Id == preset.Platform);
        if (option is not null)
        {
            SelectedPlatformOption = option;
        }

        RoomInput = preset.RoomInput;
        AppendLog("载入预设：" + preset.Name + " → " + preset.RoomInput);
        await ResolveAsync().ConfigureAwait(true);
    }

    /// <summary>把当前平台与房间号保存为预设（同名同平台会覆盖）。</summary>
    /// <returns>异步任务。</returns>
    private async Task SavePresetAsync()
    {
        string roomInput = _roomInput.Trim();
        if (roomInput.Length == 0)
        {
            // 明确告诉用户"为什么没加上"，而不是让按钮灰着或静默返回。
            StatusMessage = "新增预设失败：请先填写房间号或直播间链接。";
            AppendLog("新增预设失败：房间号为空");
            return;
        }

        string defaultName = string.IsNullOrWhiteSpace(_roomAnchor) || _roomAnchor == "-"
            ? roomInput
            : _roomAnchor;
        string? name = InputDialog.Show(System.Windows.Application.Current?.MainWindow, "新增预设", "给这个直播间起个名字（存在下拉里方便下次点开）", defaultName);
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "已取消新增预设。";
            return;
        }

        if (!_presetStore.Add(new RoomPreset(name.Trim(), SelectedPlatform, roomInput)))
        {
            StatusMessage = "新增预设失败：名称或房间号不合法（名称最长 60 字）。";
            AppendLog("新增预设失败：" + name);
            return;
        }

        RefreshPresets();
        RoomPreset? saved = Presets.FirstOrDefault(item => string.Equals(item.Name, name.Trim(), StringComparison.Ordinal));
        _suppressPresetAutoApply = true;
        try
        {
            SelectedPreset = saved;
        }
        finally
        {
            _suppressPresetAutoApply = false;
        }

        StatusMessage = $"已新增预设「{name.Trim()}」，下拉里已选中它（当前共 {Presets.Count} 个）。";
        AppendLog("已新增预设：" + name.Trim());
        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>删除下拉中选中的预设。删除后不自动解析下一条。</summary>
    /// <returns>异步任务。</returns>
    private async Task DeleteSelectedPresetAsync()
    {
        RoomPreset? preset = SelectedPreset;
        if (preset is null)
        {
            return;
        }

        bool removed = _presetStore.Remove(preset.Name);
        RefreshPresets();
        StatusMessage = removed ? "已删除预设「" + preset.Name + "」。" : "删除预设失败：" + preset.Name;
        AppendLog(StatusMessage);
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private void PersistPlaybackSettings()
    {
        _options = _options with
        {
            LastPlatform = SelectedPlatform,
            LastRoomInput = _roomInput,
            Playback = _options.Playback with
            {
                ExtremeTargetMs = _extremeTargetMs,
                Volume = _volume,
            },
        };
        _saveOptions(_options);
    }

    private void PersistLastInput()
    {
        _options = _options with
        {
            LastPlatform = SelectedPlatform,
            LastRoomInput = _roomInput,
        };
        _saveOptions(_options);
    }

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

    private static string ReadFlag(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out JsonElement element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean() ? "支持" : "不支持";
        }

        return "未知";
    }

    private static string BuildTelemetrySummary(JsonElement root)
    {
        string buffered = root.TryGetProperty("bufferedAheadMs", out JsonElement bufferElement) && bufferElement.ValueKind == JsonValueKind.Number
            ? bufferElement.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms"
            : "-";
        string rate = root.TryGetProperty("playbackRate", out JsonElement rateElement) && rateElement.ValueKind == JsonValueKind.Number
            ? rateElement.GetDouble().ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "x"
            : "-";
        string dropped = root.TryGetProperty("droppedVideoFrames", out JsonElement dropElement) && dropElement.ValueKind == JsonValueKind.Number
            ? dropElement.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "-";
        return $"缓冲 {buffered} · 倍速 {rate} · 丢帧 {dropped}";
    }

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
        (PlayCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StartRecordingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopRecordingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (MpvCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChaseCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

/// <summary>
/// 平台下拉项。
/// </summary>
/// <param name="Id">平台标识。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="UrlPrefix">直播间链接前缀。</param>
public sealed record PlatformOption(PlatformId Id, string DisplayName, string UrlPrefix);
