namespace StreamPilot.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Runtime;
using StreamPilot.Core.Updates;
using WinFormsDialogResult = System.Windows.Forms.DialogResult;
using WinFormsFolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using WinFormsOpenFileDialog = System.Windows.Forms.OpenFileDialog;

/// <summary>
/// 设置窗口的视图模型：承载全部可编辑配置项与 mpv 路径探测。
/// </summary>
/// <remarks>
/// 只负责表单状态与校验，不做任何持久化（保存由调用方通过 <see cref="TryBuildOptions"/> 取回结果后写入），
/// 因此"取消"天然不会产生副作用。
/// </remarks>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly Func<string?, string?> _resolveMpvPath;
    private readonly IStructuredLogger _logger;
    private readonly Func<Task<UpdateCheckResult>>? _checkUpdate;
    private readonly string _moduleName = "App.Settings";

    /// <summary>分片最小字节数的界面输入上限（MiB）：只是输入框的合理范围，不是分片上限。</summary>
    private const int MaxSegmentMinMegabytes = 1024;

    private string _mpvPath = string.Empty;
    private int _extremeTargetMs = PlaybackRequest.DefaultExtremeTargetMs;
    private PlatformOption _selectedDefaultPlatform = PlatformOption.All[0];
    private int _volume = PlaybackOptions.DefaultVolume;
    private bool _autoPlayOnResolve = true;
    private bool _compactControls;
    private bool _autoHideControls = true;
    private bool _extremePlayback;
    private bool _autoLaunchMpv;
    private bool _danmakuEnabled;
    private DanmakuChoice _selectedDanmakuDisplayMode = DanmakuChoice.DisplayModes[0];
    private DanmakuChoice _selectedDanmakuFilter = DanmakuChoice.Filters[0];
    private string _danmakuBlockedKeywords = string.Empty;
    private bool _danmakuMergeDuplicates = true;
    private int _danmakuMergeWindowSeconds = DanmakuOptions.DefaultMergeWindowSeconds;
    private int _danmakuOpacityPercent = 90;
    private int _danmakuFontSize = DanmakuOptions.DefaultFontSize;
    private int _danmakuSpeedSeconds = DanmakuOptions.DefaultSpeedSeconds;
    private int _danmakuMaxOnScreen = DanmakuOptions.DefaultMaxOnScreen;
    private string _bilibiliCookie = string.Empty;
    private string _douyinCookie = string.Empty;
    private string _huyaCookie = string.Empty;
    private string _douyuCookie = string.Empty;
    private string _recordingDirectory = string.Empty;
    private string _segmentMaxGibibytes = RecordingLimitUnits.FormatGibibytes(RecordingLimits.DefaultSegmentMaxBytes);
    private string _segmentMaxHours = RecordingLimitUnits.FormatHours(RecordingLimits.DefaultSegmentMaxDurationMinutes);
    private int _segmentMinMegabytes = (int)(RecordingLimits.DefaultSegmentMinBytes / RecordingLimits.BytesPerMebibyte);
    private string _maxDurationHours = RecordingLimitUnits.FormatHours(RecordingLimits.DefaultMaxRecordingMinutes);
    private int _stallTimeoutSeconds = 12;
    private int _maxReconnectAttempts = 8;
    private string _proxy = string.Empty;
    private int _requestTimeoutSeconds = 8;
    private int _maxAttempts = 3;
    private string _userAgent = string.Empty;
    private int _bridgePort = BridgeConstants.DefaultPort;
    private bool _bridgeAutoStart = true;
    private bool _verboseDiagnostics;
    private int _logRetainDays = 5;
    private bool _checkUpdateOnStartup = true;
    private bool _includePrerelease;
    private string _updateMirrorPrefix = string.Empty;
    private string _updateCheckStatus = string.Empty;
    private bool _isCheckingUpdate;
    private string _statusMessage = string.Empty;

    /// <summary>初始化设置视图模型。</summary>
    /// <param name="resolveMpvPath">mpv 路径解析函数（返回解析后的绝对路径，未找到返回 <see langword="null"/>）。</param>
    /// <param name="logger">结构化日志。</param>
    /// <param name="logLines">与主界面共享的"最近事件"滚动列表，可为 <see langword="null"/>。</param>
    /// <param name="checkUpdate">手动检查更新的回调（由主视图模型提供），可为 <see langword="null"/>。</param>
    public SettingsViewModel(
        Func<string?, string?> resolveMpvPath,
        IStructuredLogger logger,
        ObservableCollection<string>? logLines = null,
        Func<Task<UpdateCheckResult>>? checkUpdate = null)
    {
        ArgumentNullException.ThrowIfNull(resolveMpvPath);
        ArgumentNullException.ThrowIfNull(logger);
        _resolveMpvPath = resolveMpvPath;
        _logger = logger;
        _checkUpdate = checkUpdate;

        LogLines = logLines ?? [];

        BrowseMpvCommand = new RelayCommand(_ => BrowseMpv());
        DetectMpvCommand = new RelayCommand(_ => DetectMpv());
        ClearMpvCommand = new RelayCommand(_ => ClearMpv());
        ClearLogLinesCommand = new RelayCommand(_ => LogLines.Clear(), _ => LogLines.Count > 0);
        BrowseRecordingDirectoryCommand = new RelayCommand(_ => BrowseRecordingDirectory());
        OpenConfigFolderCommand = new RelayCommand(_ => OpenFolder(Path.GetDirectoryName(AppPaths.ConfigFile) ?? AppPaths.UserDataDirectory));
        OpenLogFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.LogDirectory));
        OpenRecordingFolderCommand = new RelayCommand(_ => OpenFolder(EffectiveRecordingDirectory));
        CheckUpdateCommand = new AsyncRelayCommand(
            _ => CheckUpdateAsync(),
            HandleCheckUpdateErrorAsync,
            _ => !_isCheckingUpdate && _checkUpdate is not null);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>最近事件（与主界面共享同一份滚动列表）。</summary>
    public ObservableCollection<string> LogLines { get; }

    /// <summary>浏览 mpv 可执行文件。</summary>
    public RelayCommand BrowseMpvCommand { get; }

    /// <summary>自动探测 mpv 并填入绝对路径。</summary>
    public RelayCommand DetectMpvCommand { get; }

    /// <summary>清空 mpv 路径（回到自动探测）。</summary>
    public RelayCommand ClearMpvCommand { get; }

    /// <summary>清空最近事件列表。</summary>
    public RelayCommand ClearLogLinesCommand { get; }

    /// <summary>选择录制输出目录。</summary>
    public RelayCommand BrowseRecordingDirectoryCommand { get; }

    /// <summary>打开配置目录。</summary>
    public RelayCommand OpenConfigFolderCommand { get; }

    /// <summary>打开日志目录。</summary>
    public RelayCommand OpenLogFolderCommand { get; }

    /// <summary>打开录制目录。</summary>
    public RelayCommand OpenRecordingFolderCommand { get; }

    /// <summary>手动检查更新命令。</summary>
    public AsyncRelayCommand CheckUpdateCommand { get; }

    /// <summary>当前版本号（只读展示）。</summary>
    public string CurrentVersion => AppVersion.Current;

    /// <summary>启动后自动检查更新。</summary>
    public bool CheckUpdateOnStartup
    {
        get => _checkUpdateOnStartup;
        set => SetField(ref _checkUpdateOnStartup, value);
    }

    /// <summary>是否把预发布版本也算作可更新版本。</summary>
    public bool IncludePrerelease
    {
        get => _includePrerelease;
        set => SetField(ref _includePrerelease, value);
    }

    /// <summary>更新包下载加速前缀（留空直连 GitHub）。</summary>
    public string UpdateMirrorPrefix
    {
        get => _updateMirrorPrefix;
        set => SetField(ref _updateMirrorPrefix, value ?? string.Empty);
    }

    /// <summary>检查更新的结果提示。</summary>
    public string UpdateCheckStatus
    {
        get => _updateCheckStatus;
        private set => SetField(ref _updateCheckStatus, value);
    }

    /// <summary>实际生效的录制目录（留空时为默认目录）。</summary>
    public string EffectiveRecordingDirectory =>
        string.IsNullOrWhiteSpace(RecordingDirectory) ? AppPaths.DefaultRecordingDirectory : RecordingDirectory;

    /// <summary>窗口底部状态提示。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    /// <summary>mpv 可执行文件路径。</summary>
    public string MpvPath
    {
        get => _mpvPath;
        set => SetField(ref _mpvPath, value ?? string.Empty);
    }

    /// <summary>可用平台列表（供"默认平台"下拉绑定）。</summary>
    public IReadOnlyList<PlatformOption> DefaultPlatformChoices { get; } = PlatformOption.All;

    /// <summary>
    /// 默认平台：仅当输入无法从链接域名识别平台时使用。
    /// </summary>
    public PlatformOption SelectedDefaultPlatform
    {
        get => _selectedDefaultPlatform;
        set
        {
            if (value is not null)
            {
                SetField(ref _selectedDefaultPlatform, value);
            }
        }
    }

    /// <summary>默认追帧档位（毫秒）。</summary>
    /// <remarks>
    /// 界面上不再提供第二处档位选择（档位入口只在主界面），此值在保存时按原配置透传，
    /// 避免"设置里忘了改就把用户的档位改回默认值"。
    /// </remarks>
    public int ExtremeTargetMs
    {
        get => _extremeTargetMs;
        set => SetField(ref _extremeTargetMs, value);
    }

    /// <summary>默认音量。</summary>
    public int Volume
    {
        get => _volume;
        set => SetField(ref _volume, value);
    }

    /// <summary>
    /// 是否使用「极限追帧」模式（默认关闭 = 稳定缓冲）。
    /// </summary>
    /// <remarks>
    /// 极限档把延迟压到 250 ms 以内，代价是缓冲一超过阈值就硬跳、弱网下持续跳帧；
    /// 它适合网络稳定且必须盯直播边缘的场景。默认走稳定缓冲，避免"一开播就卡"的第一印象。
    /// </remarks>
    public bool ExtremePlayback
    {
        get => _extremePlayback;
        set => SetField(ref _extremePlayback, value);
    }

    /// <summary>解析成功后自动播放。</summary>
    public bool AutoPlayOnResolve
    {
        get => _autoPlayOnResolve;
        set => SetField(ref _autoPlayOnResolve, value);
    }

    /// <summary>紧凑控制条（旧版一行）。</summary>
    public bool CompactControls
    {
        get => _compactControls;
        set => SetField(ref _compactControls, value);
    }

    /// <summary>播放中鼠标静止时自动隐藏控制条。</summary>
    public bool AutoHideControls
    {
        get => _autoHideControls;
        set => SetField(ref _autoHideControls, value);
    }

    /// <summary>解析成功后自动外挂 mpv。</summary>
    public bool AutoLaunchMpv
    {
        get => _autoLaunchMpv;
        set => SetField(ref _autoLaunchMpv, value);
    }

    /// <summary>
    /// 是否启用弹幕（总开关）。
    /// </summary>
    /// <remarks>
    /// 默认关闭：弹幕会遮挡画面，未经用户同意不该自动出现。
    /// 播放页的「弹幕」按钮首次开启时也会把这个值置为 <see langword="true"/> 并落盘，
    /// 所以两处入口是同一个开关，不会互相打架。
    /// </remarks>
    public bool DanmakuEnabled
    {
        get => _danmakuEnabled;
        set => SetField(ref _danmakuEnabled, value);
    }

    /// <summary>显示模式选项（精简版 / 完整版）。</summary>
    public IReadOnlyList<DanmakuChoice> DanmakuDisplayModeChoices { get; } = DanmakuChoice.DisplayModes;

    /// <summary>当前显示模式。</summary>
    public DanmakuChoice SelectedDanmakuDisplayMode
    {
        get => _selectedDanmakuDisplayMode;
        set
        {
            if (value is not null)
            {
                SetField(ref _selectedDanmakuDisplayMode, value);
            }
        }
    }

    /// <summary>消息类型过滤选项。</summary>
    public IReadOnlyList<DanmakuChoice> DanmakuFilterChoices { get; } = DanmakuChoice.Filters;

    /// <summary>当前消息类型过滤。</summary>
    public DanmakuChoice SelectedDanmakuFilter
    {
        get => _selectedDanmakuFilter;
        set
        {
            if (value is not null)
            {
                SetField(ref _selectedDanmakuFilter, value);
            }
        }
    }

    /// <summary>
    /// 屏蔽词（每行一条）。
    /// </summary>
    /// <remarks>
    /// 也接受逗号、顿号、分号、竖线分隔；解析在保存时统一完成（见 <see cref="DanmakuKeywordList"/>）。
    /// </remarks>
    public string DanmakuBlockedKeywords
    {
        get => _danmakuBlockedKeywords;
        set => SetField(ref _danmakuBlockedKeywords, value ?? string.Empty);
    }

    /// <summary>是否合并重复弹幕。</summary>
    public bool DanmakuMergeDuplicates
    {
        get => _danmakuMergeDuplicates;
        set => SetField(ref _danmakuMergeDuplicates, value);
    }

    /// <summary>合并重复弹幕的时间窗口（秒）。</summary>
    public int DanmakuMergeWindowSeconds
    {
        get => _danmakuMergeWindowSeconds;
        set => SetField(ref _danmakuMergeWindowSeconds, value);
    }

    /// <summary>弹幕不透明度（百分比 10-100）。</summary>
    /// <remarks>界面用整数百分比表示，配置里存 0.1-1.0 的小数，避免滑块绑定浮点带来的取整噪声。</remarks>
    public int DanmakuOpacityPercent
    {
        get => _danmakuOpacityPercent;
        set => SetField(ref _danmakuOpacityPercent, value);
    }

    /// <summary>弹幕字号（像素）。</summary>
    public int DanmakuFontSize
    {
        get => _danmakuFontSize;
        set => SetField(ref _danmakuFontSize, value);
    }

    /// <summary>弹幕横穿屏幕耗时（秒）。</summary>
    public int DanmakuSpeedSeconds
    {
        get => _danmakuSpeedSeconds;
        set => SetField(ref _danmakuSpeedSeconds, value);
    }

    /// <summary>同屏弹幕上限。</summary>
    public int DanmakuMaxOnScreen
    {
        get => _danmakuMaxOnScreen;
        set => SetField(ref _danmakuMaxOnScreen, value);
    }

    /// <summary>B站 Cookie（可留空）。</summary>
    public string BilibiliCookie
    {
        get => _bilibiliCookie;
        set => SetField(ref _bilibiliCookie, value ?? string.Empty);
    }

    /// <summary>抖音 Cookie（可留空）。</summary>
    public string DouyinCookie
    {
        get => _douyinCookie;
        set => SetField(ref _douyinCookie, value ?? string.Empty);
    }

    /// <summary>虎牙 Cookie（可留空；部分房间的最高码率需要登录态）。</summary>
    public string HuyaCookie
    {
        get => _huyaCookie;
        set => SetField(ref _huyaCookie, value ?? string.Empty);
    }

    /// <summary>斗鱼 Cookie（可留空）。</summary>
    public string DouyuCookie
    {
        get => _douyuCookie;
        set => SetField(ref _douyuCookie, value ?? string.Empty);
    }

    /// <summary>录制输出根目录。</summary>
    public string RecordingDirectory
    {
        get => _recordingDirectory;
        set => SetField(ref _recordingDirectory, value ?? string.Empty);
    }

    /// <summary>分片大小上限（GiB；界面按 GiB 填写，保存时换算为字节，无上限）。</summary>
    public string SegmentMaxGibibytes
    {
        get => _segmentMaxGibibytes;
        set => SetField(ref _segmentMaxGibibytes, value ?? string.Empty);
    }

    /// <summary>分片时长上限（小时；界面按小时填写，保存时换算为分钟，无上限）。</summary>
    public string SegmentMaxHours
    {
        get => _segmentMaxHours;
        set => SetField(ref _segmentMaxHours, value ?? string.Empty);
    }

    /// <summary>分片最小字节数（MiB），避免切出大量小文件。</summary>
    public int SegmentMinMegabytes
    {
        get => _segmentMinMegabytes;
        set => SetField(ref _segmentMinMegabytes, value);
    }

    /// <summary>最长录制时长（小时；界面按小时填写，保存时换算为分钟，无上限）。</summary>
    public string MaxDurationHours
    {
        get => _maxDurationHours;
        set => SetField(ref _maxDurationHours, value ?? string.Empty);
    }

    /// <summary>断流判定超时（秒）。</summary>
    public int StallTimeoutSeconds
    {
        get => _stallTimeoutSeconds;
        set => SetField(ref _stallTimeoutSeconds, value);
    }

    /// <summary>最大重连次数。</summary>
    public int MaxReconnectAttempts
    {
        get => _maxReconnectAttempts;
        set => SetField(ref _maxReconnectAttempts, value);
    }

    /// <summary>HTTP 代理地址（留空表示不使用）。</summary>
    public string Proxy
    {
        get => _proxy;
        set => SetField(ref _proxy, value ?? string.Empty);
    }

    /// <summary>HTTP 请求超时（秒）。</summary>
    public int RequestTimeoutSeconds
    {
        get => _requestTimeoutSeconds;
        set => SetField(ref _requestTimeoutSeconds, value);
    }

    /// <summary>HTTP 最大尝试次数（含首次）。</summary>
    public int MaxAttempts
    {
        get => _maxAttempts;
        set => SetField(ref _maxAttempts, value);
    }

    /// <summary>自定义 User-Agent（留空使用内置值）。</summary>
    public string UserAgent
    {
        get => _userAgent;
        set => SetField(ref _userAgent, value ?? string.Empty);
    }

    /// <summary>桥接服务首选端口。</summary>
    public int BridgePort
    {
        get => _bridgePort;
        set => SetField(ref _bridgePort, value);
    }

    /// <summary>启动时自动开启桥接服务。</summary>
    public bool BridgeAutoStart
    {
        get => _bridgeAutoStart;
        set => SetField(ref _bridgeAutoStart, value);
    }

    /// <summary>是否记录 Trace 级日志。</summary>
    public bool VerboseDiagnostics
    {
        get => _verboseDiagnostics;
        set => SetField(ref _verboseDiagnostics, value);
    }

    /// <summary>日志保留文件个数。</summary>
    public int LogRetainDays
    {
        get => _logRetainDays;
        set => SetField(ref _logRetainDays, value);
    }

    /// <summary>从现有配置载入表单。</summary>
    /// <param name="options">当前配置。</param>
    public void Load(StreamPilotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        MpvPath = options.Playback.MpvPath;
        ExtremeTargetMs = NormalizeTarget(options.Playback.ExtremeTargetMs);
        SelectedDefaultPlatform = PlatformOption.Find(options.DefaultPlatform) ?? PlatformOption.All[0];
        Volume = Clamp(options.Playback.Volume, 0, 100, PlaybackOptions.DefaultVolume);
        AutoPlayOnResolve = options.Playback.AutoPlayOnResolve;
        CompactControls = options.Playback.CompactControls;
        AutoHideControls = options.Playback.AutoHideControls;
        ExtremePlayback = options.Playback.Mode == PlaybackMode.Extreme;
        AutoLaunchMpv = options.Playback.AutoLaunchMpv;

        DanmakuOptions danmaku = options.Danmaku.Normalize();
        DanmakuEnabled = danmaku.Enabled;
        SelectedDanmakuDisplayMode = DanmakuChoice.Find(DanmakuChoice.DisplayModes, (int)danmaku.DisplayMode);
        SelectedDanmakuFilter = DanmakuChoice.Find(DanmakuChoice.Filters, (int)danmaku.Filter);
        DanmakuBlockedKeywords = DanmakuKeywordList.Format(danmaku.BlockedKeywords);
        DanmakuMergeDuplicates = danmaku.MergeDuplicates;
        DanmakuMergeWindowSeconds = Clamp(
            danmaku.MergeWindowSeconds,
            DanmakuOptions.MinMergeWindowSeconds,
            DanmakuOptions.MaxMergeWindowSeconds,
            DanmakuOptions.DefaultMergeWindowSeconds);
        DanmakuOpacityPercent = Clamp(
            (int)Math.Round(danmaku.Opacity * 100, MidpointRounding.AwayFromZero),
            (int)(DanmakuOptions.MinOpacity * 100),
            100,
            90);
        DanmakuFontSize = Clamp(
            danmaku.FontSize,
            DanmakuOptions.MinFontSize,
            DanmakuOptions.MaxFontSize,
            DanmakuOptions.DefaultFontSize);
        DanmakuSpeedSeconds = Clamp(
            danmaku.SpeedSeconds,
            DanmakuOptions.MinSpeedSeconds,
            DanmakuOptions.MaxSpeedSeconds,
            DanmakuOptions.DefaultSpeedSeconds);
        DanmakuMaxOnScreen = Clamp(
            danmaku.MaxOnScreen,
            DanmakuOptions.MinMaxOnScreen,
            DanmakuOptions.MaxMaxOnScreen,
            DanmakuOptions.DefaultMaxOnScreen);

        BilibiliCookie = options.Platforms.BilibiliCookie;
        DouyinCookie = options.Platforms.DouyinCookie;
        HuyaCookie = options.Platforms.HuyaCookie;
        DouyuCookie = options.Platforms.DouyuCookie;

        RecordingDirectory = options.Recording.OutputDirectory;
        SegmentPolicyOptions segment = options.Recording.Segment.Normalize();
        SegmentMaxGibibytes = RecordingLimitUnits.FormatGibibytes(segment.MaxBytes);
        SegmentMaxHours = RecordingLimitUnits.FormatHours(segment.MaxDurationMinutes);
        SegmentMinMegabytes = (int)Math.Clamp(segment.MinBytes / RecordingLimits.BytesPerMebibyte, 0, MaxSegmentMinMegabytes);
        MaxDurationHours = RecordingLimitUnits.FormatHours(UseOrFallback(
            options.Recording.MaxDurationMinutes,
            RecordingLimits.DefaultMaxRecordingMinutes));
        StallTimeoutSeconds = Clamp(options.Recording.StallTimeoutSeconds, 2, 120, 12);
        MaxReconnectAttempts = Clamp(options.Recording.MaxReconnectAttempts, 1, 50, 8);

        Proxy = options.Network.Proxy;
        RequestTimeoutSeconds = Clamp(options.Network.RequestTimeoutSeconds, 1, 120, 8);
        MaxAttempts = Clamp(options.Network.MaxAttempts, 1, 5, 3);
        UserAgent = options.Network.UserAgent;

        CheckUpdateOnStartup = options.Update.CheckOnStartup;
        IncludePrerelease = options.Update.IncludePrerelease;
        UpdateMirrorPrefix = options.Update.MirrorPrefix;
        UpdateCheckStatus = string.Empty;

        BridgePort = Clamp(options.Bridge.PreferredPort, 1024, 65535, BridgeConstants.DefaultPort);
        BridgeAutoStart = options.Bridge.AutoStart;

        VerboseDiagnostics = options.Logging.VerboseDiagnostics;
        LogRetainDays = Clamp(options.Logging.RetainedFileCount, 1, 90, 5);

        // 底部提示初始为空：只有"保存失败 / 探测结果"这类需要用户知道的结论才出现，
        // 常驻的"修改后点保存生效"属于界面说明，放在这里只会占位并和真正的失败提示混在一起。
        StatusMessage = string.Empty;
    }

    /// <summary>
    /// 校验并构造新的配置对象。
    /// </summary>
    /// <param name="baseOptions">作为基础的原配置（保留未在界面暴露的字段）。</param>
    /// <param name="result">构造出的配置。</param>
    /// <returns>校验通过返回 <see langword="true"/>。</returns>
    public bool TryBuildOptions(StreamPilotOptions baseOptions, out StreamPilotOptions result)
    {
        ArgumentNullException.ThrowIfNull(baseOptions);
        result = baseOptions;

        if (Proxy.Length > 0 && !Uri.TryCreate(Proxy.Trim(), UriKind.Absolute, out _))
        {
            StatusMessage = "代理地址不是合法 URL（例如 http://127.0.0.1:10809）。已保留原值。";
            return false;
        }

        string recordingDirectory = RecordingDirectory.Trim();
        string resolvedMpv = MpvPath.Trim();

        if (!RecordingLimitUnits.TryParseLimits(
                SegmentMaxGibibytes,
                SegmentMaxHours,
                MaxDurationHours,
                out RecordingLimitInput? limits,
                out string? limitError)
            || limits is null)
        {
            StatusMessage = limitError ?? "录制上限不合法，请检查后重试。";
            return false;
        }

        IReadOnlyList<string> blockedKeywords = DanmakuKeywordList.Parse(DanmakuBlockedKeywords);
        if (!DanmakuKeywordList.IsWithinLimit(blockedKeywords))
        {
            StatusMessage = $"屏蔽词最多 {DanmakuKeywordList.MaxKeywordCount} 条，请精简后重试。";
            return false;
        }

        result = baseOptions with
        {
            DefaultPlatform = SelectedDefaultPlatform.Id,
            Playback = baseOptions.Playback with
            {
                MpvPath = resolvedMpv,
                ExtremeTargetMs = NormalizeTarget(ExtremeTargetMs),
                Mode = ExtremePlayback ? PlaybackMode.Extreme : PlaybackMode.Stable,
                Volume = Clamp(Volume, 0, 100, PlaybackOptions.DefaultVolume),
                AutoPlayOnResolve = AutoPlayOnResolve,
                AutoLaunchMpv = AutoLaunchMpv,
                CompactControls = CompactControls,
                AutoHideControls = AutoHideControls,
            },
            Danmaku = (baseOptions.Danmaku with
            {
                Enabled = DanmakuEnabled,
                // 打开总开关时同步把"可见"置为 true：否则用户勾了开关却看不到任何弹幕。
                Visible = DanmakuEnabled || baseOptions.Danmaku.Visible,
                DisplayMode = (DanmakuDisplayMode)SelectedDanmakuDisplayMode.Value,
                Filter = (DanmakuFilterMode)SelectedDanmakuFilter.Value,
                BlockedKeywords = blockedKeywords,
                MergeDuplicates = DanmakuMergeDuplicates,
                MergeWindowSeconds = DanmakuMergeWindowSeconds,
                Opacity = DanmakuOpacityPercent / 100d,
                FontSize = DanmakuFontSize,
                SpeedSeconds = DanmakuSpeedSeconds,
                MaxOnScreen = DanmakuMaxOnScreen,
            }).Normalize(),
            Recording = baseOptions.Recording with
            {
                OutputDirectory = recordingDirectory,
                Segment = new SegmentPolicyOptions
                {
                    MaxBytes = limits.SegmentMaxBytes,
                    MaxDurationMinutes = limits.SegmentMaxMinutes,
                    MinBytes = SegmentMinMegabytes * RecordingLimits.BytesPerMebibyte,
                    SplitOnKeyFrameOnly = true,
                },
                MaxDurationMinutes = limits.MaxRecordingMinutes,
                StallTimeoutSeconds = StallTimeoutSeconds,
                MaxReconnectAttempts = MaxReconnectAttempts,
            },
            Network = baseOptions.Network with
            {
                Proxy = Proxy.Trim(),
                RequestTimeoutSeconds = RequestTimeoutSeconds,
                MaxAttempts = MaxAttempts,
                UserAgent = UserAgent.Trim(),
            },
            Platforms = baseOptions.Platforms with
            {
                BilibiliCookie = BilibiliCookie.Trim(),
                DouyinCookie = DouyinCookie.Trim(),
                HuyaCookie = HuyaCookie.Trim(),
                DouyuCookie = DouyuCookie.Trim(),
            },
            Bridge = baseOptions.Bridge with
            {
                PreferredPort = BridgePort,
                MaxPort = Math.Max(BridgePort, BridgePort + (BridgeConstants.MaxPort - BridgeConstants.DefaultPort)),
                AutoStart = BridgeAutoStart,
            },
            Logging = baseOptions.Logging with
            {
                VerboseDiagnostics = VerboseDiagnostics,
                RetainedFileCount = LogRetainDays,
            },
            Update = baseOptions.Update with
            {
                CheckOnStartup = CheckUpdateOnStartup,
                IncludePrerelease = IncludePrerelease,
                MirrorPrefix = UpdateMirrorPrefix.Trim(),
            },
        };

        return true;
    }

    private void ClearMpv()
    {
        MpvPath = string.Empty;
        StatusMessage = "已清空 mpv 路径，播放时按默认顺序自动探测。";
    }

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

    private void BrowseMpv()
    {
        using WinFormsOpenFileDialog dialog = new()
        {
            Title = "选择 mpv 可执行文件",
            Filter = "mpv (mpv.exe)|mpv.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        string? started = string.IsNullOrWhiteSpace(MpvPath) ? null : Path.GetDirectoryName(MpvPath);
        if (!string.IsNullOrWhiteSpace(started) && Directory.Exists(started))
        {
            dialog.InitialDirectory = started;
        }

        if (dialog.ShowDialog() == WinFormsDialogResult.OK)
        {
            MpvPath = dialog.FileName;
            StatusMessage = "已选择 mpv：" + dialog.FileName;
        }
    }

    private void DetectMpv()
    {
        string? resolved = _resolveMpvPath(MpvPath);
        if (resolved is null)
        {
            StatusMessage = "未找到 mpv。请把 mpv.exe 放到 tools\\mpv\\ 或手动选择路径。";
            _logger.Warn(_moduleName, "自动探测 mpv 失败。", new Dictionary<string, object?>
            {
                ["configured"] = MpvPath,
            });
            return;
        }

        MpvPath = resolved;
        StatusMessage = "已定位 mpv：" + resolved;
    }

    private void BrowseRecordingDirectory()
    {
        using WinFormsFolderBrowserDialog dialog = new()
        {
            Description = "选择录制输出根目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        string current = RecordingDirectory.Trim();
        if (current.Length > 0 && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog() == WinFormsDialogResult.OK)
        {
            RecordingDirectory = dialog.SelectedPath;
            StatusMessage = "录制目录：" + dialog.SelectedPath;
        }
    }

    private static int Clamp(int value, int min, int max, int fallback) =>
        value < min || value > max ? fallback : value;

    /// <summary>取配置值；缺失或非法（0、负数）时回退到默认值。</summary>
    /// <param name="value">配置里的值。</param>
    /// <param name="fallback">默认值。</param>
    /// <returns>可用的值。</returns>
    private static int UseOrFallback(int value, int fallback) => value > 0 ? value : fallback;

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

    /// <summary>
    /// 手动检查更新：结果直接显示在设置页（启动检查是静默的，这里要给出明确回执）。
    /// </summary>
    /// <returns>异步任务。</returns>
    private async Task CheckUpdateAsync()
    {
        if (_checkUpdate is null)
        {
            return;
        }

        _isCheckingUpdate = true;
        UpdateCheckStatus = "正在检查…";
        CheckUpdateCommand.RaiseCanExecuteChanged();
        try
        {
            UpdateCheckResult result = await _checkUpdate().ConfigureAwait(true);
            UpdateCheckStatus = result.Message;
        }
        finally
        {
            _isCheckingUpdate = false;
            CheckUpdateCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>检查更新命令的异常处理：只把失败写进设置页，不弹窗打断。</summary>
    /// <param name="exception">异常。</param>
    /// <returns>已完成的任务。</returns>
    private Task HandleCheckUpdateErrorAsync(Exception exception)
    {
        _logger.LogError(LogLevel.Warn, _moduleName, "手动检查更新失败。", exception);
        UpdateCheckStatus = "检查更新失败：" + exception.Message;
        return Task.CompletedTask;
    }
}
