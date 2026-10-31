namespace StreamPilot.App.Services;

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Updates;

/// <summary>
/// 更新包的下载、解压与就地替换。
/// </summary>
/// <remarks>
/// Windows 不允许覆盖正在运行的 exe，但**允许重命名**（映像文件以 share-delete 方式打开）。
/// 因此替换流程是：先把当前 exe 改名成 <c>.old</c>，再把新 exe 放到原路径；
/// 旧文件在下次启动时清理。任何一步失败都尝试回滚，保证至少还能启动旧版本。
/// </remarks>
public sealed class UpdateInstaller
{
    private const string ModuleName = "App.Update";
    private const string BackupSuffix = ".old";

    private readonly HttpClientFactory _factory;
    private readonly IStructuredLogger _logger;

    /// <summary>初始化安装器。</summary>
    /// <param name="factory">HTTP 客户端工厂（沿用配置里的代理）。</param>
    /// <param name="logger">结构化日志。</param>
    public UpdateInstaller(HttpClientFactory factory, IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _logger = logger;
    }

    /// <summary>更新包的暂存根目录：<c>%LOCALAPPDATA%\StreamPilot\updates</c>。</summary>
    public static string UpdatesRoot => Path.Combine(AppPaths.UserDataDirectory, "updates");

    /// <summary>
    /// 下载更新包。
    /// </summary>
    /// <param name="release">发布信息。</param>
    /// <param name="mirrorPrefix">下载加速前缀；为空表示直连。</param>
    /// <param name="progress">下载进度（0-1），可为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>下载到本地的 zip 路径。</returns>
    /// <exception cref="InvalidOperationException">下载失败或校验不通过时抛出。</exception>
    public async Task<string> DownloadAsync(
        UpdateReleaseInfo release,
        string mirrorPrefix,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        string url = GitHubReleaseClient.BuildDownloadUrl(release.AssetUrl, mirrorPrefix);
        string directory = Path.Combine(UpdatesRoot, SanitizeTag(release.Tag));
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, release.AssetName);

        // 同版本的残留包直接复用，避免重复下载 80 MB。
        if (File.Exists(target) && new FileInfo(target).Length > 0 && release.AssetSize > 0
            && new FileInfo(target).Length == release.AssetSize
            && (release.Sha256 is null || await VerifySha256Async(target, release.Sha256, cancellationToken).ConfigureAwait(false)))
        {
            _logger.Info(ModuleName, "复用已下载的更新包。", new Dictionary<string, object?>
            {
                ["file"] = target,
            });
            progress?.Report(1);
            return target;
        }

        string temporary = target + ".part";
        try
        {
            using HttpResponseMessage response = await _factory
                .GetStreamingClient()
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (FileStream destination = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (total is > 0)
                    {
                        progress?.Report(Math.Clamp((double)received / total.Value, 0, 1));
                    }
                }
            }

            if (release.AssetSize > 0 && new FileInfo(temporary).Length != release.AssetSize)
            {
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"更新包大小不一致（期望 {release.AssetSize} 字节，实际 {new FileInfo(temporary).Length} 字节）。"));
            }

            if (release.Sha256 is not null && !await VerifySha256Async(temporary, release.Sha256, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("更新包校验失败（SHA-256 不匹配），已放弃安装。");
            }

            File.Move(temporary, target, overwrite: true);
            progress?.Report(1);
            _logger.Info(ModuleName, "更新包下载完成。", new Dictionary<string, object?>
            {
                ["file"] = target,
                ["bytes"] = new FileInfo(target).Length,
                ["url"] = url,
            });
            return target;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            TryDelete(temporary);
            _logger.LogError(LogLevel.Warn, ModuleName, "更新包下载失败。", exception, new Dictionary<string, object?>
            {
                ["url"] = url,
            });
            throw new InvalidOperationException("下载更新包失败：" + exception.Message, exception);
        }
    }

    /// <summary>
    /// 解压更新包并定位其中的可执行文件。
    /// </summary>
    /// <param name="zipPath">更新包路径。</param>
    /// <param name="release">发布信息（用于目录命名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解压目录与其中的 exe 路径。</returns>
    /// <exception cref="InvalidOperationException">包结构不符合预期时抛出。</exception>
    public Task<(string Directory, string Executable)> ExtractAsync(
        string zipPath,
        UpdateReleaseInfo release,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(release);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = Path.Combine(UpdatesRoot, SanitizeTag(release.Tag), "extracted");
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            Directory.CreateDirectory(directory);
            ZipFile.ExtractToDirectory(zipPath, directory, overwriteFiles: true);

            string? executable = Directory
                .EnumerateFiles(directory, $"*{AssetExecutablePrefix}*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => Path.GetFileName(path).StartsWith(AssetExecutablePrefix, StringComparison.OrdinalIgnoreCase));

            if (executable is null)
            {
                throw new InvalidOperationException("更新包里没有找到 StreamPilot 可执行文件。");
            }

            _logger.Info(ModuleName, "更新包已解压。", new Dictionary<string, object?>
            {
                ["directory"] = directory,
                ["executable"] = Path.GetFileName(executable),
            });
            return (directory, executable);
        }, cancellationToken);
    }

    /// <summary>
    /// 把解压出来的文件就地替换到当前安装目录。
    /// </summary>
    /// <param name="stagedDirectory">解压目录（更新包的根）。</param>
    /// <param name="stagedExecutable">新版本 exe。</param>
    /// <returns>替换结果。</returns>
    /// <remarks>
    /// 顺序很重要：先覆盖 Web / tools 等附属文件（失败只会导致资源不完整，可以再更新一次），
    /// 最后替换 exe（关键步骤，失败会回滚）。反过来会出现「新 exe 配旧资源」。
    /// </remarks>
    public UpdateApplyResult Apply(string stagedDirectory, string stagedExecutable)
    {
        string? currentExecutable = Environment.ProcessPath;
        if (currentExecutable is null
            || !Path.GetFileName(currentExecutable).StartsWith(AssetExecutablePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateApplyResult.Failed(
                "当前进程不是 StreamPilot 主程序（可能是开发态运行），无法自动替换。请手动解压覆盖。");
        }

        string installDirectory = Path.GetDirectoryName(currentExecutable)
            ?? throw new InvalidOperationException("无法确定程序所在目录。");

        int copied = 0;
        int failed = 0;
        foreach (string source in Directory.EnumerateFiles(stagedDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(stagedDirectory, source);
            if (string.Equals(Path.GetExtension(source), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                // exe 单独处理，避免复制到一半把主程序弄坏。
                continue;
            }

            string destination = Path.Combine(installDirectory, relative);
            try
            {
                string? parent = Path.GetDirectoryName(destination);
                if (parent is not null)
                {
                    Directory.CreateDirectory(parent);
                }

                File.Copy(source, destination, overwrite: true);
                copied++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed++;
                _logger.Warn(ModuleName, "覆盖附属文件失败。", new Dictionary<string, object?>
                {
                    ["file"] = relative,
                    ["detail"] = exception.Message,
                });
            }
        }

        string backup = currentExecutable + BackupSuffix;
        TryDelete(backup);
        try
        {
            File.Move(currentExecutable, backup);
            try
            {
                File.Copy(stagedExecutable, currentExecutable, overwrite: true);
            }
            catch
            {
                // 回滚：新 exe 放不进去就把旧的改回来，保证还能启动。
                File.Move(backup, currentExecutable);
                throw;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(LogLevel.Warn, ModuleName, "替换主程序失败。", exception);
            return UpdateApplyResult.Failed("替换主程序失败：" + exception.Message + "。请手动解压覆盖。");
        }

        _logger.Info(ModuleName, "更新已就位。", new Dictionary<string, object?>
        {
            ["installDirectory"] = installDirectory,
            ["copiedFiles"] = copied,
            ["failedFiles"] = failed,
        });

        return failed == 0
            ? UpdateApplyResult.Succeeded(installDirectory, copied)
            : UpdateApplyResult.PartiallySucceeded(installDirectory, copied, failed);
    }

    /// <summary>
    /// 启动新版本并退出当前进程。
    /// </summary>
    /// <param name="installDirectory">程序所在目录。</param>
    /// <returns>已成功拉起新进程返回 <see langword="true"/>。</returns>
    public bool Restart(string installDirectory)
    {
        string? executable = Environment.ProcessPath;
        if (executable is null)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = installDirectory,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(LogLevel.Warn, ModuleName, "启动新版本失败。", exception);
            return false;
        }
    }

    /// <summary>
    /// 清理上次更新留下的 <c>.old</c> 备份（启动早期调用，失败只记日志）。
    /// </summary>
    public void CleanupPreviousVersions()
    {
        string? executable = Environment.ProcessPath;
        if (executable is null)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(executable);
        if (directory is null)
        {
            return;
        }

        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory, "*" + BackupSuffix, SearchOption.TopDirectoryOnly))
            {
                if (TryDelete(candidate))
                {
                    _logger.Info(ModuleName, "已清理旧版本备份。", new Dictionary<string, object?>
                    {
                        ["file"] = Path.GetFileName(candidate),
                    });
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warn(ModuleName, "清理旧版本备份失败。", new Dictionary<string, object?>
            {
                ["detail"] = exception.Message,
            });
        }
    }

    private static async Task<bool> VerifySha256Async(string path, string expected, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        string actual = Convert.ToHexStringLower(hash);
        return string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeTag(string tag)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] buffer = new char[tag.Length];
        for (int index = 0; index < tag.Length; index++)
        {
            buffer[index] = Array.IndexOf(invalid, tag[index]) >= 0 ? '_' : tag[index];
        }

        return new string(buffer);
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Debug(ModuleName, "删除文件失败（下次再试）。", new Dictionary<string, object?>
            {
                ["file"] = path,
                ["detail"] = exception.Message,
            });
            return false;
        }
    }

    /// <summary>更新包内主程序的文件名前缀。</summary>
    private const string AssetExecutablePrefix = "StreamPilot-windows-";
}

/// <summary>就地替换的结果。</summary>
/// <param name="Success">是否成功（部分文件失败也算成功，但会给提示）。</param>
/// <param name="Message">给用户看的说明。</param>
/// <param name="InstallDirectory">程序所在目录。</param>
/// <param name="CopiedFiles">已覆盖的附属文件数。</param>
/// <param name="FailedFiles">覆盖失败的附属文件数。</param>
public sealed record UpdateApplyResult(
    bool Success,
    string Message,
    string InstallDirectory,
    int CopiedFiles,
    int FailedFiles)
{
    /// <summary>全部成功。</summary>
    /// <param name="installDirectory">程序所在目录。</param>
    /// <param name="copiedFiles">已覆盖文件数。</param>
    /// <returns>结果。</returns>
    public static UpdateApplyResult Succeeded(string installDirectory, int copiedFiles) =>
        new(true, "更新文件已就位。", installDirectory, copiedFiles, 0);

    /// <summary>主程序已替换，但部分附属文件覆盖失败。</summary>
    /// <param name="installDirectory">程序所在目录。</param>
    /// <param name="copiedFiles">已覆盖文件数。</param>
    /// <param name="failedFiles">失败文件数。</param>
    /// <returns>结果。</returns>
    public static UpdateApplyResult PartiallySucceeded(string installDirectory, int copiedFiles, int failedFiles) =>
        new(true, $"主程序已更新，但有 {failedFiles} 个附属文件未能覆盖（可能被占用），重启后可在下次更新时补齐。",
            installDirectory, copiedFiles, failedFiles);

    /// <summary>失败。</summary>
    /// <param name="message">失败原因。</param>
    /// <returns>结果。</returns>
    public static UpdateApplyResult Failed(string message) => new(false, message, string.Empty, 0, 0);
}
