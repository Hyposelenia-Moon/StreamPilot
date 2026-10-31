namespace StreamPilot.App.Views;

using System.Diagnostics;
using System.Globalization;
using System.Windows;
using StreamPilot.App.Services;
using StreamPilot.Core.Updates;

/// <summary>
/// 更新窗口：展示版本差异与更新说明，负责「下载 → 解压 → 替换 → 重启」的交互。
/// </summary>
/// <remarks>
/// 这是短生命周期的对话框，状态机只有四步（待下载 / 下载中 / 已就位待重启 / 失败），
/// 直接写在代码后置里比再套一层视图模型更清楚。
/// </remarks>
public partial class UpdateWindow : Window
{
    private readonly UpdateReleaseInfo _release;
    private readonly UpdateInstaller _installer;
    private readonly string _mirrorPrefix;
    private CancellationTokenSource? _cancellation;
    private string? _installDirectory;
    private bool _readyToRestart;
    private bool _busy;

    /// <summary>初始化更新窗口。</summary>
    /// <param name="release">目标版本。</param>
    /// <param name="installer">更新安装器。</param>
    /// <param name="mirrorPrefix">下载加速前缀（可为空）。</param>
    /// <param name="currentVersion">当前版本号文本。</param>
    public UpdateWindow(UpdateReleaseInfo release, UpdateInstaller installer, string mirrorPrefix, string currentVersion)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(installer);
        InitializeComponent();
        _release = release;
        _installer = installer;
        _mirrorPrefix = mirrorPrefix ?? string.Empty;

        CurrentVersionText.Text = currentVersion;
        NewVersionText.Text = release.Version.ToString();
        PublishedText.Text = release.PublishedAt is { } published
            ? published.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : string.Empty;
        NotesViewer.Document = MarkdownDocumentRenderer.Render(
            string.IsNullOrWhiteSpace(release.Notes) ? "（该版本没有填写更新说明）" : release.Notes);

        StatusText.Text = $"更新包 {FormatSize(release.AssetSize)}。点「下载并安装」开始，下载完成后会自动替换文件。";
    }

    /// <summary>更新包在 GitHub 上的发布页地址（供手动下载）。</summary>
    private string ReleasePageUrl =>
        $"https://github.com/{GitHubReleaseClient.DefaultOwner}/{GitHubReleaseClient.DefaultRepository}/releases/tag/{_release.Tag}";

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_readyToRestart)
        {
            RestartNow();
            return;
        }

        await DownloadAndApplyAsync().ConfigureAwait(true);
    }

    private async Task DownloadAndApplyAsync()
    {
        SetBusy(true);
        _cancellation = new CancellationTokenSource();
        try
        {
            Progress<double> progress = new(value => ReportProgress(value));
            StatusText.Text = "正在下载更新包…";
            string zipPath = await _installer
                .DownloadAsync(_release, _mirrorPrefix, progress, _cancellation.Token)
                .ConfigureAwait(true);

            StatusText.Text = "正在解压…";
            (string directory, string executable) = await _installer
                .ExtractAsync(zipPath, _release, _cancellation.Token)
                .ConfigureAwait(true);

            StatusText.Text = "正在替换文件（旧版本会保留为 .old，下次启动自动清理）…";
            UpdateApplyResult result = await Task
                .Run(() => _installer.Apply(directory, executable), _cancellation.Token)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                ShowFailure(result.Message);
                return;
            }

            _installDirectory = result.InstallDirectory;
            _readyToRestart = true;
            DownloadProgress.Value = 1;
            StatusText.Text = result.Message + " 点「立即重启」完成更新。";
            PrimaryButton.Content = "立即重启";
            CloseButton.Content = "稍后";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消下载。";
            PrimaryButton.Content = "重试";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            ShowFailure(exception.Message);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            SetBusy(false);
        }
    }

    private void RestartNow()
    {
        if (_installDirectory is null)
        {
            return;
        }

        if (_installer.Restart(_installDirectory))
        {
            Application.Current.Shutdown();
            return;
        }

        StatusText.Text = "无法自动启动新版本，请手动双击程序目录里的 StreamPilot 启动。";
    }

    private void ShowFailure(string message)
    {
        StatusText.Text = message;
        PrimaryButton.Content = "重试";
        DownloadProgress.Visibility = Visibility.Collapsed;
    }

    private void ReportProgress(double value)
    {
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = value;
        StatusText.Text = string.Create(CultureInfo.CurrentCulture, $"正在下载更新包… {value * 100:0}%");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        PrimaryButton.IsEnabled = !busy;
        OpenReleaseButton.IsEnabled = !busy;
        if (busy)
        {
            PrimaryButton.Content = "下载中…";
            DownloadProgress.Visibility = Visibility.Visible;
            DownloadProgress.Value = 0;
        }
    }

    private void OnOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasePageUrl) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText.Text = "无法打开浏览器：" + exception.Message;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _cancellation?.Cancel();
        base.OnClosed(e);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "大小未知",
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):0.0} MB"),
    };
}
