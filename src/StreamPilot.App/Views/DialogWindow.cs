using System.Windows;
using System.Windows.Media;
using WpfApplication = System.Windows.Application;

namespace StreamPilot.App.Views;

/// <summary>
/// 代码构建对话框的窗口工厂：统一背景、图标、尺寸策略与所属关系。
/// </summary>
/// <remarks>
/// 背景一律取主题资源 <see cref="DialogLayout.WindowBackgroundBrushKey"/>，
/// 与主窗口、设置窗口保持一致；资源缺失时直接抛出，避免出现透明或纯白背景。
/// </remarks>
internal static class DialogWindow
{
    /// <summary>窗口图标资源键。</summary>
    private const string AppIconKey = "AppIcon";

    /// <summary>
    /// 创建并配置一个无边框调整的模态对话框窗口。
    /// </summary>
    /// <param name="owner">父窗口，可为 <see langword="null"/>（此时居中于屏幕）。</param>
    /// <param name="title">窗口标题。</param>
    /// <param name="width">窗口宽度（像素）。</param>
    /// <returns>配置好的窗口；调用方负责设置内容并调用 <c>ShowDialog</c>。</returns>
    public static Window Create(Window? owner, string title, double width)
    {
        Window dialog = new()
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = DialogLayout.ResolveBrush(DialogLayout.WindowBackgroundBrushKey),
            Foreground = DialogLayout.ResolveBrush(DialogLayout.TextBrushKey),
            FontFamily = (System.Windows.Media.FontFamily)WpfApplication.Current.Resources[DialogLayout.UiFontFamilyKey],
            FontSize = DialogLayout.FontSize,
            Icon = DialogLayout.ResolveImage(AppIconKey),
        };

        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        return dialog;
    }
}
