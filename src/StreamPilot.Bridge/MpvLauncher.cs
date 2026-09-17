namespace StreamPilot.Bridge;

using System.Diagnostics;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Errors;
using StreamPilot.Core.Logging;

/// <summary>
/// mpv 外挂播放启动器。
/// </summary>
/// <remarks>
/// 取代参考项目的外部 PowerShell 脚本（mpv-bridge.ps1）：
/// <list type="bullet">
///   <item>路径解析顺序：显式配置 → 程序目录 tools/ → PATH → 常见安装位置；</item>
///   <item>进程调用处理启动失败、启动超时与异常退出；</item>
///   <item>不等待进程退出（不阻塞 UI），只做启动确认。</item>
/// </list>
/// </remarks>
public sealed class MpvLauncher
{
    /// <summary>启动确认超时（毫秒）。</summary>
    public const int StartupConfirmTimeoutMs = 10000;

    /// <summary>低延迟 mpv 参数（与参考项目经验值一致）。</summary>
    private static readonly string[] LowLatencyArguments =
    [
        "--cache=no",
        "--cache-pause=no",
        "--demuxer-readahead-secs=0",
        "--demuxer-max-bytes=512K",
        "--demuxer-max-back-bytes=0",
        "--speed=1.08",
        "--audio-pitch-correction=yes",
    ];

    /// <summary>通用 mpv 参数。</summary>
    private static readonly string[] CommonArguments =
    [
        "--force-window=yes",
        "--keep-open=no",
    ];

    /// <summary>常见 mpv 安装位置（作为 PATH 之外的回退）。</summary>
    private static readonly string[] FallbackRelativePaths =
    [
        @"tools\mpv\mpv.exe",
        @"tools\mpv.exe",
        @"mpv\mpv.exe",
    ];

    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Bridge.MpvLauncher";

    /// <summary>初始化启动器。</summary>
    /// <param name="logger">结构化日志。</param>
    public MpvLauncher(IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>最近一次成功解析出的 mpv 路径。</summary>
    public string? ResolvedPath { get; private set; }

    /// <summary>
    /// 解析 mpv 可执行文件路径。
    /// </summary>
    /// <param name="configuredPath">配置中显式指定的路径，可为空。</param>
    /// <returns>可执行文件路径；未找到时返回 <see langword="null"/>。</returns>
    public string? ResolveExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            ResolvedPath = configuredPath;
            return ResolvedPath;
        }

        foreach (string relative in FallbackRelativePaths)
        {
            string candidate = Path.Combine(AppPaths.ApplicationDirectory, relative);
            if (File.Exists(candidate))
            {
                ResolvedPath = candidate;
                return ResolvedPath;
            }
        }

        string? fromPath = FindOnPath("mpv.exe");
        if (fromPath is not null)
        {
            ResolvedPath = fromPath;
            return ResolvedPath;
        }

        string[] commonLocations =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "mpv", "mpv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "mpv", "mpv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "mpv.exe"),
            @"C:\mpv\mpv.exe",
            @"D:\mpv\mpv.exe",
        ];

        foreach (string location in commonLocations)
        {
            if (File.Exists(location))
            {
                ResolvedPath = location;
                return ResolvedPath;
            }
        }

        _logger.Warn(_moduleName, "未找到 mpv 可执行文件。", new Dictionary<string, object?>
        {
            ["searchedTools"] = AppPaths.ToolsDirectory,
        });
        return null;
    }

    /// <summary>
    /// 用 mpv 播放指定地址。
    /// </summary>
    /// <param name="url">直播流地址。</param>
    /// <param name="title">窗口标题附加信息，可为 <see langword="null"/>。</param>
    /// <param name="referer">可选 Referer，可为 <see langword="null"/>。</param>
    /// <param name="options">播放配置（mpv 路径、低延迟开关、附加参数）。</param>
    /// <returns>启动成功返回 <see langword="true"/>。</returns>
    public bool Launch(string url, string? title, string? referer, PlaybackOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(options);

        string? executable = ResolveExecutable(options.MpvPath);
        if (executable is null)
        {
            _logger.Error(_moduleName, "未找到 mpv，无法外挂播放。", new Dictionary<string, object?>
            {
                ["tools"] = AppPaths.ToolsDirectory,
            });
            return false;
        }

        List<string> arguments = [];
        arguments.AddRange(CommonArguments);
        if (options.UseLowLatencyMpvArguments)
        {
            arguments.AddRange(LowLatencyArguments);
        }

        arguments.Add("--title=" + BuildTitle(title));
        if (!string.IsNullOrWhiteSpace(referer))
        {
            arguments.Add("--http-header-fields=Referer: " + referer);
        }

        foreach (string extra in SplitExtraArguments(options.MpvExtraArguments))
        {
            arguments.Add(extra);
        }

        arguments.Add("--");
        arguments.Add(url);

        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppPaths.ApplicationDirectory,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.Error(_moduleName, "mpv 进程未能启动（返回空进程对象）。", new Dictionary<string, object?>
                {
                    ["executable"] = Path.GetFileName(executable),
                });
                return false;
            }

            _logger.Info(_moduleName, "已启动 mpv 外挂播放。", new Dictionary<string, object?>
            {
                ["executable"] = Path.GetFileName(executable),
                ["lowLatency"] = options.UseLowLatencyMpvArguments,
            });
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new BridgeException("launch-mpv", $"启动 mpv 失败：{exception.Message}", exception);
        }
    }

    private static string BuildTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "StreamPilot" : "StreamPilot - " + title;

    private static IEnumerable<string> SplitExtraArguments(string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra))
        {
            return [];
        }

        return extra.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? FindOnPath(string fileName)
    {
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH 中存在非法路径片段时跳过该片段。
            }
        }

        return null;
    }
}
