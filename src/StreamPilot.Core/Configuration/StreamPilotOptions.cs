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

    /// <summary>上次输入的房间号/链接。</summary>
    public string LastRoomInput { get; init; } = string.Empty;

    /// <summary>播放相关设置。</summary>
    public PlaybackOptions Playback { get; init; } = new();

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
}

/// <summary>播放设置。</summary>
public sealed record PlaybackOptions
{
    /// <summary>默认播放模式。</summary>
    public PlaybackMode Mode { get; init; } = PlaybackMode.Extreme;

    /// <summary>默认极限追帧目标延迟（毫秒），仅允许 150/200/250。</summary>
    public int ExtremeTargetMs { get; init; } = PlaybackRequest.DefaultExtremeTargetMs;

    /// <summary>默认音量（0-100）。</summary>
    public int Volume { get; init; } = 70;

    /// <summary>是否自动外挂 mpv 播放（false 表示仅在用户点击时启动）。</summary>
    public bool AutoLaunchMpv { get; init; }

    /// <summary>mpv 可执行文件路径；为空时按工具查找顺序自动探测。</summary>
    public string MpvPath { get; init; } = string.Empty;

    /// <summary>mpv 附加参数（在默认参数之后追加）。</summary>
    public string MpvExtraArguments { get; init; } = string.Empty;
}

/// <summary>录制设置。</summary>
public sealed record RecordingOptions
{
    /// <summary>录制输出根目录；为空时使用"视频"目录下的 StreamPilot 子目录。</summary>
    public string OutputDirectory { get; init; } = string.Empty;

    /// <summary>分片策略。</summary>
    public SegmentPolicyOptions Segment { get; init; } = new();

    /// <summary>最长录制时长（分钟）。</summary>
    public int MaxDurationMinutes { get; init; } = 360;

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

    /// <summary>启动 mpv 时是否使用低延迟参数。</summary>
    public bool UseLowLatencyMpvArguments { get; init; } = true;
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
public sealed record PlatformOptions
{
    /// <summary>B站 Cookie（可留空，留空时匿名解析）。</summary>
    public string BilibiliCookie { get; init; } = string.Empty;
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
