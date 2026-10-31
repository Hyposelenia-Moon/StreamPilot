namespace StreamPilot.Core.Configuration;

using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;

/// <summary>
/// 用户配置（持久化为 <c>%LOCALAPPDATA%\StreamPilot\config.json</c>）。
/// </summary>
/// <remarks>
/// 用户数据与程序目录分离（CLAUDE.md 安全规则），因此配置一律写入本地应用数据目录。
/// 所有字段都有安全默认值，配置文件缺失或损坏时可回退到默认值继续运行。
/// </remarks>
public sealed record StreamPilotOptions
{
    /// <summary>配置格式版本，用于未来迁移。</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>上次使用的平台（用于启动时预选）。</summary>
    public PlatformId LastPlatform { get; init; } = PlatformId.Bilibili;

    /// <summary>用户指定的默认平台：仅当输入无法从链接域名识别平台时使用。</summary>
    /// <remarks>
    /// 旧配置文件里没有该字段，反序列化时保持默认值（哔哩哔哩），因此天然向后兼容。
    /// </remarks>
    public PlatformId DefaultPlatform { get; init; } = PlatformId.Bilibili;

    /// <summary>上次输入的房间号/链接。</summary>
    public string LastRoomInput { get; init; } = string.Empty;

    /// <summary>播放相关设置。</summary>
    public PlaybackOptions Playback { get; init; } = new();

    /// <summary>弹幕相关设置。</summary>
    public DanmakuOptions Danmaku { get; init; } = new();

    /// <summary>录制相关设置。</summary>
    public RecordingOptions Recording { get; init; } = new();

    /// <summary>桥接服务设置。</summary>
    public BridgeOptions Bridge { get; init; } = new();

    /// <summary>网络设置。</summary>
    public NetworkOptions Network { get; init; } = new();

    /// <summary>平台凭据（B站 Cookie）。</summary>
    public PlatformOptions Platforms { get; init; } = new();

    /// <summary>日志设置。</summary>
    public LoggingOptions Logging { get; init; } = new();

    /// <summary>更新设置。</summary>
    public UpdateOptions Update { get; init; } = new();
}

/// <summary>播放设置。</summary>
public sealed record PlaybackOptions
{
    /// <summary>首次运行时的默认音量（0-100）。</summary>
    /// <remarks>
    /// 用户已保存的 <c>config.json</c> 里若有 <c>playback.volume</c>，反序列化会保留用户值，
    /// 这里的默认值只影响"首次运行 / 配置里没有该字段"的场景，不会强改用户已保存的音量。
    /// </remarks>
    public const int DefaultVolume = 30;

    /// <summary>默认播放模式。</summary>
    public PlaybackMode Mode { get; init; } = PlaybackMode.Stable;

    /// <summary>默认极限追帧目标延迟（毫秒），仅允许 150/200/250。</summary>
    public int ExtremeTargetMs { get; init; } = PlaybackRequest.DefaultExtremeTargetMs;

    /// <summary>默认音量（0-100）。</summary>
    public int Volume { get; init; } = DefaultVolume;

    /// <summary>是否自动外挂 mpv 播放（false 表示仅在用户点击时启动）。</summary>
    public bool AutoLaunchMpv { get; init; }

    /// <summary>mpv 可执行文件路径；为空时按工具查找顺序自动探测。</summary>
    public string MpvPath { get; init; } = string.Empty;

    /// <summary>解析成功后是否自动开始播放。</summary>
    public bool AutoPlayOnResolve { get; init; } = true;

    /// <summary>紧凑控制条（旧版一行）：为 true 时播放页把主控层与参数层摊平成一行。</summary>
    public bool CompactControls { get; init; }

    /// <summary>播放中鼠标静止时自动隐藏控制条（默认开启；全屏下最需要）。</summary>
    public bool AutoHideControls { get; init; } = true;
}

/// <summary>弹幕设置。</summary>
/// <remarks>
/// 默认<b>关闭</b>（v0.2.2 评审结论）：首次使用需用户在播放页主动开启，
/// 避免弹幕未经允许就遮挡画面。
/// </remarks>
public sealed record DanmakuOptions
{
    /// <summary>默认同屏弹幕上限。</summary>
    public const int DefaultMaxOnScreen = 60;

    /// <summary>默认字号（像素）。</summary>
    public const int DefaultFontSize = 24;

    /// <summary>默认横穿屏幕耗时（秒）。</summary>
    public const int DefaultSpeedSeconds = 8;

    /// <summary>默认合并重复弹幕的时间窗口（秒）。</summary>
    public const int DefaultMergeWindowSeconds = 3;

    /// <summary>不透明度下限。</summary>
    public const double MinOpacity = 0.1;

    /// <summary>字号下限（像素）。</summary>
    public const int MinFontSize = 12;

    /// <summary>字号上限（像素）。</summary>
    public const int MaxFontSize = 48;

    /// <summary>横穿屏幕耗时下限（秒）。</summary>
    public const int MinSpeedSeconds = 4;

    /// <summary>横穿屏幕耗时上限（秒）。</summary>
    public const int MaxSpeedSeconds = 20;

    /// <summary>同屏上限的最小值。</summary>
    public const int MinMaxOnScreen = 20;

    /// <summary>同屏上限的最大值。</summary>
    public const int MaxMaxOnScreen = 200;

    /// <summary>合并窗口下限（秒）。</summary>
    public const int MinMergeWindowSeconds = 1;

    /// <summary>合并窗口上限（秒）。</summary>
    public const int MaxMergeWindowSeconds = 10;

    /// <summary>全局开关；默认关闭。</summary>
    public bool Enabled { get; init; }

    /// <summary>上次会话的可见性（用户开启后记录，下次沿用）。</summary>
    public bool Visible { get; init; } = true;

    /// <summary>消息类型过滤（决定显示哪些类型）。</summary>
    public DanmakuFilterMode Filter { get; init; } = DanmakuFilterMode.ChatOnly;

    /// <summary>显示模式（决定单条弹幕显示多少信息）。</summary>
    public DanmakuDisplayMode DisplayMode { get; init; } = DanmakuDisplayMode.Compact;

    /// <summary>弹幕不透明度（0.1-1.0）。</summary>
    public double Opacity { get; init; } = 0.9;

    /// <summary>字号（像素，12-48）。</summary>
    public int FontSize { get; init; } = DefaultFontSize;

    /// <summary>横穿屏幕耗时（秒，4-20）。</summary>
    public int SpeedSeconds { get; init; } = DefaultSpeedSeconds;

    /// <summary>同屏弹幕上限（20-200）。</summary>
    public int MaxOnScreen { get; init; } = DefaultMaxOnScreen;

    /// <summary>
    /// 屏蔽词列表：弹幕文本**包含**任一关键词（不区分大小写）时不显示。
    /// </summary>
    /// <remarks>
    /// 在渲染前过滤（配置改动立即生效，无需重连）。空字符串与纯空白关键词会被忽略。
    /// </remarks>
    public IReadOnlyList<string> BlockedKeywords { get; init; } = [];

    /// <summary>是否合并重复弹幕：相同文本在 <see cref="MergeWindowSeconds"/> 内只显示一条并累加次数。</summary>
    public bool MergeDuplicates { get; init; } = true;

    /// <summary>合并重复弹幕的时间窗口（秒，1-10）。</summary>
    public int MergeWindowSeconds { get; init; } = DefaultMergeWindowSeconds;

    /// <summary>
    /// 把配置收敛到合法范围：越界的数值夹到边界，屏蔽词去空去重。
    /// </summary>
    /// <returns>收敛后的配置（本身不可变）。</returns>
    /// <remarks>
    /// 配置文件可能被手工改过、也可能来自更早的版本；渲染侧不该再为"0 秒横穿""字号 0"这类
    /// 非法值写防御代码，读写两侧都在这里收敛一次即可。
    /// </remarks>
    public DanmakuOptions Normalize() => this with
    {
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, MinOpacity, 1.0) : 0.9,
        FontSize = Math.Clamp(FontSize, MinFontSize, MaxFontSize),
        SpeedSeconds = Math.Clamp(SpeedSeconds, MinSpeedSeconds, MaxSpeedSeconds),
        MaxOnScreen = Math.Clamp(MaxOnScreen, MinMaxOnScreen, MaxMaxOnScreen),
        MergeWindowSeconds = Math.Clamp(MergeWindowSeconds, MinMergeWindowSeconds, MaxMergeWindowSeconds),
        BlockedKeywords = DanmakuKeywordList.Parse(DanmakuKeywordList.Format(BlockedKeywords)),
    };
}

/// <summary>弹幕显示模式（决定单条弹幕显示多少信息）。</summary>
public enum DanmakuDisplayMode
{
    /// <summary>精简版：只显示「昵称 + 文本」，视觉最轻、信息密度最高。</summary>
    Compact = 0,

    /// <summary>完整版：额外显示粉丝牌、用户等级与弹幕颜色。</summary>
    Full = 1,
}

/// <summary>弹幕消息类型过滤（决定显示哪些类型）。</summary>
public enum DanmakuFilterMode
{
    /// <summary>只看普通弹幕。</summary>
    ChatOnly = 0,

    /// <summary>弹幕 + 礼物。</summary>
    WithGift = 1,

    /// <summary>全部（含进场互动与系统消息）。</summary>
    All = 2,
}

/// <summary>录制设置。</summary>
public sealed record RecordingOptions
{
    /// <summary>录制输出根目录；为空时使用"视频"目录下的 StreamPilot 子目录。</summary>
    public string OutputDirectory { get; init; } = string.Empty;

    /// <summary>分片策略。</summary>
    public SegmentPolicyOptions Segment { get; init; } = new();

    /// <summary>最长录制时长（分钟，默认 480 分钟即 8 小时）。</summary>
    public int MaxDurationMinutes { get; init; } = RecordingLimits.DefaultMaxRecordingMinutes;

    /// <summary>无数据到达多少秒后判定断流。</summary>
    public int StallTimeoutSeconds { get; init; } = 12;

    /// <summary>最多重连次数。</summary>
    public int MaxReconnectAttempts { get; init; } = 8;

    /// <summary>开始录制前是否需要确认（避免误触）。</summary>
    public bool ConfirmOnStart { get; init; }
}

/// <summary>桥接服务设置。</summary>
public sealed record BridgeOptions
{
    /// <summary>首选监听端口（仅 127.0.0.1）。</summary>
    public int PreferredPort { get; init; } = BridgeConstants.DefaultPort;

    /// <summary>端口被占用时允许尝试的最大端口号。</summary>
    public int MaxPort { get; init; } = BridgeConstants.MaxPort;

    /// <summary>是否在应用启动时自动开启桥接服务。</summary>
    public bool AutoStart { get; init; } = true;
}

/// <summary>网络设置。</summary>
public sealed record NetworkOptions
{
    /// <summary>HTTP 代理地址（例如 <c>http://127.0.0.1:10809</c>）；为空表示不使用代理。</summary>
    public string Proxy { get; init; } = string.Empty;

    /// <summary>单次 HTTP 请求超时（秒）。</summary>
    public int RequestTimeoutSeconds { get; init; } = 8;

    /// <summary>HTTP 请求最大尝试次数（含首次）。</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>覆盖默认 User-Agent；为空时使用内置 Windows Chrome UA。</summary>
    public string UserAgent { get; init; } = string.Empty;
}

/// <summary>平台凭据设置。</summary>
/// <remarks>
/// Cookie 只用于"解析"请求（换取最高画质与多档位），播放地址本身不带登录态、
/// 播放过程中也不会把 Cookie 发给 CDN，因此主播看不到这位观众；
/// 所有 Cookie 只写入本机配置文件，日志一律脱敏。
/// </remarks>
public sealed record PlatformOptions
{
    /// <summary>B站 Cookie（可留空，留空时匿名解析）。</summary>
    public string BilibiliCookie { get; init; } = string.Empty;

    /// <summary>抖音 Cookie（可留空）。</summary>
    public string DouyinCookie { get; init; } = string.Empty;

    /// <summary>虎牙 Cookie（可留空；部分房间的最高码率需要登录态）。</summary>
    public string HuyaCookie { get; init; } = string.Empty;

    /// <summary>斗鱼 Cookie（可留空）。</summary>
    public string DouyuCookie { get; init; } = string.Empty;

    /// <summary>YY Cookie（已停用，仅为兼容旧配置保留字段）。</summary>
    public string YyCookie { get; init; } = string.Empty;

    /// <summary>Bigo Cookie（已停用，仅为兼容旧配置保留字段）。</summary>
    public string BigoCookie { get; init; } = string.Empty;

    /// <summary>按平台取对应的 Cookie。</summary>
    /// <param name="platform">平台标识。</param>
    /// <returns>Cookie 文本；未配置时为空字符串。</returns>
    public string ForPlatform(PlatformId platform) => platform switch
    {
        PlatformId.Bilibili => BilibiliCookie,
        PlatformId.Douyin => DouyinCookie,
        PlatformId.Huya => HuyaCookie,
        PlatformId.Douyu => DouyuCookie,
        PlatformId.Yy => YyCookie,
        PlatformId.Bigo => BigoCookie,
        _ => string.Empty,
    };
}

/// <summary>日志设置。</summary>
public sealed record LoggingOptions
{
    /// <summary>文件日志最低级别。</summary>
    public LogLevel FileLevel { get; init; } = LogLevel.Info;

    /// <summary>单个日志文件大小上限（MiB）。</summary>
    public int MaxFileSizeMb { get; init; } = 8;

    /// <summary>保留的日志文件个数。</summary>
    public int RetainedFileCount { get; init; } = 5;

    /// <summary>是否记录 Trace 级日志（含 URL 指纹，永不含签名原文）。</summary>
    public bool VerboseDiagnostics { get; init; }
}

/// <summary>软件内更新设置。</summary>
public sealed record UpdateOptions
{
    /// <summary>启动后是否自动检查更新（延迟若干秒，失败不打扰）。</summary>
    public bool CheckOnStartup { get; init; } = true;

    /// <summary>是否把预发布版本（prerelease）也算作可更新版本。</summary>
    public bool IncludePrerelease { get; init; }

    /// <summary>
    /// 下载加速前缀（例如 <c>https://ghfast.top/</c>），拼在 GitHub 下载地址前面；为空表示直连。
    /// </summary>
    /// <remarks>
    /// 只影响更新包的下载，不改变检查更新所用的 API 地址：镜像站通常只代理
    /// <c>github.com</c> 的文件下载，不代理 <c>api.github.com</c>。
    /// 检查不通时应该配「网络代理」，而不是镜像前缀。
    /// </remarks>
    public string MirrorPrefix { get; init; } = string.Empty;
}
