namespace StreamPilot.App.Services;

using System.Windows.Forms;
using System.Windows.Resources;
using StreamPilot.Core.Logging;

/// <summary>
/// 系统托盘气泡通知（零第三方依赖）。
/// </summary>
/// <remarks>
/// v0.2.2 采用托盘气泡而非 Win10/11 原生 Toast：原生 Toast 需要为未打包桌面应用注册 AUMID
/// 并创建开始菜单快捷方式（还要引入 Windows App SDK），实现重、收益低；托盘气泡用
/// <see cref="System.Windows.Forms.NotifyIcon"/> 即可，样式旧但稳定，且随程序常驻托盘。
/// </remarks>
public sealed class TrayNotifier : IDisposable
{
    /// <summary>气泡提示显示时长（毫秒）。</summary>
    private const int BalloonTimeoutMs = 5000;

    private readonly NotifyIcon _icon;

    /// <summary>初始化托盘图标与气泡通知。</summary>
    /// <param name="logger">结构化日志（仅用于图标加载失败的降级记录）。</param>
    public TrayNotifier(IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _icon = new NotifyIcon
        {
            Text = "StreamPilot",
            Visible = true,
        };

        try
        {
            StreamResourceInfo? resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/streampilot.ico"));
            if (resource?.Stream is not null)
            {
                _icon.Icon = new System.Drawing.Icon(resource.Stream);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _icon.Icon = System.Drawing.SystemIcons.Application;
            logger.LogError(LogLevel.Warn, "App.TrayNotifier", "加载托盘图标失败，已回退到系统图标。", exception);
        }
    }

    /// <summary>弹一条"主播开播"气泡通知。</summary>
    /// <param name="anchorName">主播 / 预设名。</param>
    /// <param name="detail">补充说明（如平台）。</param>
    public void NotifyLive(string anchorName, string detail) =>
        _icon.ShowBalloonTip(BalloonTimeoutMs, anchorName + " 开播了", detail, ToolTipIcon.Info);

    /// <summary>弹一条"录制已开始"气泡通知。</summary>
    /// <param name="anchorName">主播 / 预设名。</param>
    public void NotifyRecordingStarted(string anchorName) =>
        _icon.ShowBalloonTip(BalloonTimeoutMs, "已自动开始录制", anchorName, ToolTipIcon.Info);

    /// <summary>弹一条"发现新版本"气泡通知。</summary>
    /// <param name="version">新版本号。</param>
    public void NotifyUpdate(string version) =>
        _icon.ShowBalloonTip(BalloonTimeoutMs, "发现新版本 " + version, "点主界面左上角控制面板里的「更新」即可查看并安装。", ToolTipIcon.Info);

    /// <inheritdoc />
    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
