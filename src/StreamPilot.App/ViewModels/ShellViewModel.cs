namespace StreamPilot.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
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

    /// <summary>页面请求预设列表时回传的类型（页面 → 宿主使用 status + 约定文本）。</summary>
    private const string PlayerPresetRequestMarker = "请求预设列表";

    /// <summary>宿主要求页面播放的消息类型。</summary>
    private const string HostPlayType = "play";

    /// <summary>宿主要求页面追帧的消息类型。</summary>
    private const string HostChaseType = "chase";

    /// <summary>宿主要求页面停止的消息类型。</summary>
    private const string HostStopType = "stop";

    /// <summary>宿主下发预设列表的消息类型。</summary>
    private const string HostPresetsType = "presets";

    /// <summary>宿主告知桥接地址的消息类型。</summary>
    private const string HostBridgeInfoType = "bridge-info";

    private readonly IRoomResolver _resolver;
    private readonly IPlaybackCoordinator _playback;
    private readonly IRecordingCoordinator _recording;
    private readonly IPlaybackBridge _bridge;
    private readonly IStructuredLogger _logger;
    private readonly Action<StreamPilotOptions> _saveOptions;
    private readonly string _moduleName = "App.Shell";

    private StreamPilotOptions _options;
    private PlatformId _selectedPlatform;
    private string _roomInput = string.Empty;
    private int _extremeTargetMs;
    private int _volume;
    private bool _isBusy;
    private bool _isPlayerReady;
    private string _statusMessage = "等待解析直播源。";
    private string _roomTitle = "-";
    private string _roomAnchor = "-";
    private string _roomCategory = "-";
    private string _recordingSummary = "未录制";
    private string _playerTelemetry = "-";
    private ResolvedRoom? _currentRoom;
    private IRecordingSession? _recordingSession;
    private int _activeSessionId;

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

        _selectedPlatform = _options.LastPlatform == PlatformId.Unknown ? PlatformId.Bilibili : _options.LastPlatform;
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

        ResolveCommand = new AsyncRelayCommand(_ => ResolveAsync(), HandleCommandErrorAsync, () => !IsBusy);
        PlayCommand = new AsyncRelayCommand(_ => PlayAsync(), HandleCommandErrorAsync, () => _currentRoom is not null && !IsBusy);
        StopCommand = new RelayCommand(_ => StopPlayback(), () => _isPlayerReady);
        ChaseCommand = new RelayCommand(_ => SendToPlayer(new { type = HostChaseType, keepSeconds = 0.08 }), () => _isPlayerReady);
        MpvCommand = new AsyncRelayCommand(_ => PlayWithMpvAsync(), HandleCommandErrorAsync, () => _currentRoom is not null);
        StartRecordingCommand = new AsyncRelayCommand(_ => StartRecordingAsync(), HandleCommandErrorAsync, () => _currentRoom is not null && _recordingSession is null);
        StopRecordingCommand = new AsyncRelayCommand(_ => StopRecordingAsync(), HandleCommandErrorAsync, () => _recordingSession is not null);
        SetTargetCommand = new RelayCommand(parameter => SetTarget(parameter), parameter => parameter is not null);
        OpenRecordingFolderCommand = new RelayCommand(_ => OpenRecordingFolder());
        OpenLogFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.LogDirectory));
        SaveSettingsCommand = new RelayCommand(_ => ApplySettings());
    }

    /// <summary>视图模型依赖集合（由组合根构造）。</summary>
    /// <param name="Resolver">房间解析服务。</param>
    /// <param name="Playback">播放编排服务。</param>
    /// <param name="Recording">录制编排服务。</param>
    /// <param name="Bridge">桥接服务。</param>
    /// <param name="Logger">结构化日志。</param>
    /// <param name="Options">初始配置。</param>
    /// <param name="SaveOptions">配置保存回调。</param>
    public sealed record Dependencies(
        IRoomResolver Resolver,
        IPlaybackCoordinator Playback,
        IRecordingCoordinator Recording,
        IPlaybackBridge Bridge,
        IStructuredLogger Logger,
        StreamPilotOptions Options,
        Action<StreamPilotOptions> SaveOptions);

    /// <summary>页面消息到达事件（宿主 → 页面方向的发送由该事件提供通道）。</summary>
    public event EventHandler<string>? SendMessageRequested;

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

    /// <summary>保存设置命令。</summary>
    public ICommand SaveSettingsCommand { get; }

    /// <summary>当前选中的平台。</summary>
    public PlatformId SelectedPlatform
    {
        get => _selectedPlatform;
        set
        {
            if (SetField(ref _selectedPlatform, value))
            {
                OnPropertyChanged(nameof(RoomUrlHint));
            }
        }
    }

    /// <summary>当前平台的链接前缀提示。</summary>
    public string RoomUrlHint
    {
        get
        {
            foreach (PlatformOption option in Platforms)
            {
                if (option.Id == _selectedPlatform)
                {
                    return "支持房间号或链接，例如：" + option.UrlPrefix;
                }
            }

            return "支持房间号或链接。";
        }
    }

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

    /// <summary>录制输出目录（设置面板展示用）。</summary>
    public string RecordingDirectory
    {
        get
        {
            string configured = _options.Recording.OutputDirectory;
            return string.IsNullOrWhiteSpace(configured) ? AppPaths.DefaultRecordingDirectory : configured;
        }
    }

    /// <summary>B站 Cookie（设置面板；仅本地保存，禁止写日志）。</summary>
    public string BilibiliCookie
    {
        get => _options.Platforms.BilibiliCookie;
        set
        {
            _options = _options with
            {
                Platforms = _options.Platforms with { BilibiliCookie = value },
            };
            OnPropertyChanged();
        }
    }

    /// <summary>mpv 路径（设置面板）。</summary>
    public string MpvPath
    {
        get => _options.Playback.MpvPath;
        set
        {
            _options = _options with
            {
                Playback = _options.Playback with { MpvPath = value },
            };
            OnPropertyChanged();
        }
    }

    /// <summary>录制输出目录（设置面板可编辑）。</summary>
    public string OutputDirectoryInput
    {
        get => _options.Recording.OutputDirectory;
        set
        {
            _options = _options with
            {
                Recording = _options.Recording with { OutputDirectory = value },
            };
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecordingDirectory));
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
                    SendPresets();
                    SendToPlayer(new { type = "volume", value = _volume });
                    break;

                case PlayerTelemetryType:
                    PlayerTelemetry = BuildTelemetrySummary(root);
                    break;

                case PlayerStatusType:
                    StatusMessage = message.Length == 0 ? StatusMessage : message;
                    if (message.Contains(PlayerPresetRequestMarker, StringComparison.Ordinal))
                    {
                        SendPresets();
                    }
                    else
                    {
                        AppendLog(message);
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
            RoomQuery query = BuildQuery();
            StatusMessage = "正在解析…";
            AppendLog("开始解析：" + _roomInput);

            ResolveOutcome outcome = await _resolver.ResolveAsync(query, CancellationToken.None).ConfigureAwait(true);
            if (!outcome.Success || outcome.Room is null)
            {
                _currentRoom = null;
                StatusMessage = outcome.Message;
                RoomTitle = "-";
                RoomAnchor = "-";
                RoomCategory = "-";
                CandidateSummaries.Clear();
                AppendLog("解析失败：" + outcome.Message);
                RaiseCommandStates();
                return;
            }

            _currentRoom = outcome.Room;
            RoomTitle = outcome.Room.Title;
            RoomAnchor = outcome.Room.Anchor;
            RoomCategory = string.IsNullOrWhiteSpace(outcome.Room.Category) ? "-" : outcome.Room.Category;
            StatusMessage = $"解析成功：共 {outcome.Room.Candidates.Count} 条线路。";
            AppendLog($"解析成功：{outcome.Room.Anchor} / {outcome.Room.Title}");

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

    private RoomQuery BuildQuery()
    {
        string input = _roomInput.Trim();
        bool looksLikeUrl = input.Contains("://", StringComparison.Ordinal)
            || input.Contains('/', StringComparison.Ordinal);

        return looksLikeUrl
            ? RoomQuery.FromUrl(_selectedPlatform, input)
            : RoomQuery.FromRoomId(_selectedPlatform, input) with
            {
                BilibiliCookie = _options.Platforms.BilibiliCookie,
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
        AppendLog("追帧档位切换为 " + ExtremeTargetMs + " ms（下次播放生效）");
    }

    private void ApplySettings()
    {
        PersistPlaybackSettings();
        _options = _options with
        {
            Recording = _options.Recording with
            {
                OutputDirectory = _options.Recording.OutputDirectory.Trim(),
            },
        };
        _saveOptions(_options);
        OnPropertyChanged(nameof(RecordingDirectory));
        StatusMessage = "设置已保存。";
        AppendLog("设置已保存");
        SendToPlayer(new { type = "volume", value = _volume });
    }

    private void PersistPlaybackSettings()
    {
        _options = _options with
        {
            LastPlatform = _selectedPlatform,
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
            LastPlatform = _selectedPlatform,
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

    private void SendPresets()
    {
        List<object> items = [];
        SendToPlayer(new
        {
            type = HostPresetsType,
            items,
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
