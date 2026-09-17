using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using WpfApplication = System.Windows.Application;
using WpfButton = System.Windows.Controls.Button;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace StreamPilot.App.Views;

/// <summary>
/// 代码构建对话框共用的排版常量与控件工厂，保证「新增预设」与通用输入框外观一致。
/// </summary>
/// <remarks>
/// 用代码构建（而非 XAML）是为了让对话框不带任何业务状态，全部取值都来自
/// <see cref="WpfApplication.Current"/> 的主题资源，禁止在这里硬编码颜色与字体。
/// </remarks>
internal static class DialogLayout
{
    /// <summary>窗口背景资源键（与主窗口、设置窗口使用同一主题色）。</summary>
    public const string WindowBackgroundBrushKey = "WindowBackgroundBrush";

    /// <summary>正文前景色资源键。</summary>
    public const string TextBrushKey = "TextBrush";

    /// <summary>次要文字前景色资源键。</summary>
    public const string MutedTextBrushKey = "MutedTextBrush";

    /// <summary>错误提示前景色资源键。</summary>
    public const string ErrorBrushKey = "ErrorBrush";

    /// <summary>主操作按钮样式键（蓝底白字，与主窗口保存按钮一致）。</summary>
    public const string PrimaryButtonKey = "PrimaryButton";

    /// <summary>界面字体资源键（与主窗口使用同一字体族）。</summary>
    public const string UiFontFamilyKey = "UiFontFamily";

    /// <summary>对话框统一边框间距。</summary>
    public const double Padding = 16;

    /// <summary>标签与下方控件之间的间距。</summary>
    public const double LabelBottomMargin = 4;

    /// <summary>字段组之间的间距。</summary>
    public const double FieldGap = 12;

    /// <summary>失败提示与按钮行之间的间距。</summary>
    public const double FailureBottomMargin = 10;

    /// <summary>按钮最小宽度。</summary>
    public const double ButtonMinWidth = 84;

    /// <summary>同一行按钮之间的间距。</summary>
    public const double ButtonGap = 8;

    /// <summary>对话框字号（与主窗口正文一致）。</summary>
    public const double FontSize = 14;

    /// <summary>字段标签字号（比正文略小，与主界面次要文字一致）。</summary>
    public const double LabelFontSize = 13;

    /// <summary>校验进行中的按钮文案。</summary>
    public const string ValidatingText = "校验中…";

    /// <summary>创建对话框根面板：统一字号、间距与前景色。</summary>
    /// <param name="padding">四周留白。</param>
    /// <returns>可直接作为窗口内容的面板。</returns>
    public static Grid CreateRoot(double padding)
    {
        Grid root = new() { Margin = new Thickness(padding) };
        root.SetResourceReference(TextBlock.ForegroundProperty, TextBrushKey);
        root.SetResourceReference(TextBlock.FontFamilyProperty, UiFontFamilyKey);
        root.SetResourceReference(TextBlock.FontSizeProperty, FontSize);
        return root;
    }

    /// <summary>创建一个字段输入框（自动化标识用于集成测试定位）。</summary>
    /// <param name="automationId">自动化标识。</param>
    /// <returns>输入框。</returns>
    public static WpfTextBox CreateField(string automationId)
    {
        WpfTextBox box = new();
        AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    /// <summary>创建字段标签（次要文字样式）。</summary>
    /// <param name="label">标签文字。</param>
    /// <returns>标签文本块。</returns>
    public static TextBlock CreateLabel(string label)
    {
        TextBlock text = new()
        {
            Text = label,
            Margin = new Thickness(0, 0, 0, LabelBottomMargin),
            FontSize = LabelFontSize,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, MutedTextBrushKey);
        return text;
    }

    /// <summary>创建失败提示文本（错误色，默认不占位）。</summary>
    /// <returns>提示文本块。</returns>
    public static TextBlock CreateFailureText()
    {
        TextBlock failure = new()
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, FailureBottomMargin),
        };
        failure.SetResourceReference(TextBlock.ForegroundProperty, ErrorBrushKey);
        return failure;
    }

    /// <summary>创建一个对话框按钮。</summary>
    /// <param name="content">按钮文字。</param>
    /// <param name="minWidth">最小宽度。</param>
    /// <param name="isDefault">是否为默认按钮（回车触发）。</param>
    /// <returns>按钮。</returns>
    public static WpfButton CreateButton(string content, double minWidth, bool isDefault)
    {
        WpfButton button = new()
        {
            Content = content,
            MinWidth = minWidth,
            IsDefault = isDefault,
        };
        return button;
    }

    /// <summary>创建右对齐的按钮行。</summary>
    /// <param name="buttons">按钮，按从左到右的顺序排列。</param>
    /// <returns>按钮行面板。</returns>
    public static StackPanel CreateButtonRow(params WpfButton[] buttons)
    {
        StackPanel row = new()
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
        };

        foreach (WpfButton button in buttons)
        {
            row.Children.Add(button);
        }

        return row;
    }

    /// <summary>按资源键取画刷（资源缺失时立即失败，而不是显示成透明背景）。</summary>
    /// <param name="resourceKey">资源键。</param>
    /// <returns>主题画刷。</returns>
    public static System.Windows.Media.Brush ResolveBrush(string resourceKey) =>
        (System.Windows.Media.Brush)WpfApplication.Current.Resources[resourceKey];

    /// <summary>按资源键取图片源（窗口图标）。</summary>
    /// <param name="resourceKey">资源键。</param>
    /// <returns>图片源。</returns>
    public static ImageSource ResolveImage(string resourceKey) =>
        (ImageSource)WpfApplication.Current.Resources[resourceKey];
}
