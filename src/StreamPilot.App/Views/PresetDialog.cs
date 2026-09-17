using System.Threading.Tasks;
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
/// 「新增预设」专用对话框：一次填写主播名称与直播链接，确定时先校验（解析）再保存。
/// </summary>
/// <remarks>
/// 不依赖"直播源"卡片里的输入，预设内容完全来自本对话框的两个字段。
/// 校验由调用方以异步回调提供：返回 <see langword="null"/> 表示通过并关闭对话框，
/// 返回提示文本表示不通过（对话框保持打开，用户可修正后重试）。
/// </remarks>
public static class PresetDialog
{
    /// <summary>对话框左右统一的边距。</summary>
    private const double DialogPadding = 16;

    /// <summary>字段标签与控件之间的间距。</summary>
    private const double LabelBottomMargin = 4;

    /// <summary>字段组之间的间距。</summary>
    private const double FieldGap = 12;

    /// <summary>按钮最小宽度。</summary>
    private const double ButtonMinWidth = 84;

    /// <summary>确定按钮在校验期间的文案。</summary>
    private const string ConfirmingText = "解析中…";

    /// <summary>确定按钮的常规文案。</summary>
    private const string ConfirmText = "确定";

    /// <summary>标签文字字号（比正文略小，与主界面的次要文字一致）。</summary>
    private const double LabelFontSize = 13;

    /// <summary>
    /// 弹出「新增预设」对话框。
    /// </summary>
    /// <param name="owner">父窗口，可为 <see langword="null"/>。</param>
    /// <param name="validator">
    /// 校验并保存回调：入参为（主播名称，直播链接），返回 <see langword="null"/> 表示已保存并关闭对话框，
    /// 返回提示文本表示校验失败（对话框保持打开）。
    /// </param>
    public static void Show(Window? owner, Func<string, string, Task<string?>> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);

        Window dialog = new()
        {
            Title = "新增预设",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)WpfApplication.Current.Resources["WindowBackgroundSystem.Windows.Media.Brush"],
            Icon = (ImageSource)WpfApplication.Current.Resources["AppIcon"],
        };

        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        Grid root = new() { Margin = new Thickness(DialogPadding) };
        for (int index = 0; index < 4; index++)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        WpfTextBox nameBox = AddField(root, 0, "主播名称", "PresetAnchorNameBox", FieldGap);
        WpfTextBox linkBox = AddField(root, 1, "直播链接", "PresetLinkBox", FieldGap);

        TextBlock failure = new()
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = (System.Windows.Media.Brush)WpfApplication.Current.Resources["ErrorSystem.Windows.Media.Brush"],
        };
        Grid.SetRow(failure, 2);

        StackPanel buttons = new()
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
        };

        WpfButton confirm = new()
        {
            Content = ConfirmText,
            MinWidth = ButtonMinWidth,
            IsDefault = true,
            Style = WpfApplication.Current.TryFindResource("PrimaryButton") as Style,
        };
        AutomationProperties.SetAutomationId(confirm, "PresetConfirmButton");
        WpfButton cancel = new()
        {
            Content = "取消",
            MinWidth = ButtonMinWidth,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };
        AutomationProperties.SetAutomationId(cancel, "PresetCancelButton");

        cancel.Click += (_, _) => dialog.DialogResult = false;
        confirm.Click += async (_, _) => await ConfirmAsync(dialog, confirm, cancel, nameBox, linkBox, failure, validator);
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);

        root.Children.Add(failure);
        root.Children.Add(buttons);
        dialog.Content = root;

        dialog.Loaded += (_, _) =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        };

        dialog.ShowDialog();
    }

    /// <summary>追加一个"标签行 + 输入框行"字段。</summary>
    /// <param name="root">对话框根面板。</param>
    /// <param name="row">该字段要占用的网格行（标签与输入框同处一行内的堆叠面板）。</param>
    /// <param name="label">字段标签。</param>
    /// <param name="automationId">输入框的自动化标识。</param>
    /// <param name="bottomMargin">字段底部间距。</param>
    /// <returns>创建出的输入框。</returns>
    private static WpfTextBox AddField(Grid root, int row, string label, string automationId, double bottomMargin)
    {
        TextBlock labelText = new()
        {
            Text = label,
            Margin = new Thickness(0, 0, 0, LabelBottomMargin),
            FontSize = LabelFontSize,
            Foreground = (System.Windows.Media.Brush)WpfApplication.Current.Resources["MutedTextSystem.Windows.Media.Brush"],
        };

        WpfTextBox box = new();
        AutomationProperties.SetAutomationId(box, automationId);

        StackPanel field = new() { Margin = new Thickness(0, 0, 0, bottomMargin) };
        field.Children.Add(labelText);
        field.Children.Add(box);
        Grid.SetRow(field, row);

        root.Children.Add(field);
        return box;
    }

    /// <summary>校验并保存：校验期间禁用按钮，失败时把原因显示在对话框里并保持打开。</summary>
    /// <param name="dialog">对话框。</param>
    /// <param name="confirm">确定按钮。</param>
    /// <param name="cancel">取消按钮。</param>
    /// <param name="nameBox">主播名称输入框。</param>
    /// <param name="linkBox">直播链接输入框。</param>
    /// <param name="failure">失败提示文本。</param>
    /// <param name="validator">校验并保存回调。</param>
    /// <returns>异步任务。</returns>
    private static async Task ConfirmAsync(
        Window dialog,
        WpfButton confirm,
        WpfButton cancel,
        WpfTextBox nameBox,
        WpfTextBox linkBox,
        TextBlock failure,
        Func<string, string, Task<string?>> validator)
    {
        failure.Text = string.Empty;
        confirm.IsEnabled = false;
        cancel.IsEnabled = false;
        confirm.Content = ConfirmingText;
        try
        {
            string? error = await validator(nameBox.Text ?? string.Empty, linkBox.Text ?? string.Empty).ConfigureAwait(true);
            if (error is null)
            {
                dialog.DialogResult = true;
                return;
            }

            failure.Text = error;
        }
        finally
        {
            confirm.Content = ConfirmText;
            confirm.IsEnabled = true;
            cancel.IsEnabled = true;
        }
    }
}
