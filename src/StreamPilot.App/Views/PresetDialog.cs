using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace StreamPilot.App.Views;

/// <summary>
/// 「新增预设」专用对话框：一次填写主播名称与直播链接，确定时先校验（解析）再保存。
/// </summary>
/// <remarks>
/// 不依赖"直播源"卡片里的输入，预设内容完全来自本对话框的两个字段。
/// 校验由调用方以异步回调提供：返回 <see langword="null"/> 表示通过并关闭对话框，
/// 返回提示文本表示不通过（对话框保持打开，用户可修正后重试）。
/// 外观（背景、字号、间距、按钮样式）与 <see cref="InputDialog"/> 共用
/// <see cref="DialogLayout"/> / <see cref="DialogWindow"/>，避免两个对话框风格不一致。
/// </remarks>
public static class PresetDialog
{
    /// <summary>对话框宽度。</summary>
    private const double PresetDialogWidth = 460;

    /// <summary>字段数量（主播名称 + 直播链接）。</summary>
    private const int FieldCount = 2;

    /// <summary>确定按钮在校验期间的文案。</summary>
    private const string ConfirmingText = "解析中…";

    /// <summary>确定按钮的常规文案。</summary>
    private const string ConfirmText = "确定";

    /// <summary>取消按钮的文案。</summary>
    private const string CancelText = "取消";

    /// <summary>主播名称输入框的自动化标识。</summary>
    private const string AnchorNameAutomationId = "PresetAnchorNameBox";

    /// <summary>直播链接输入框的自动化标识。</summary>
    private const string LinkAutomationId = "PresetLinkBox";

    /// <summary>主播名称字段的标签。</summary>
    private const string AnchorNameLabel = "主播名称";

    /// <summary>直播链接字段的标签。</summary>
    private const string LinkLabel = "直播链接";

    /// <summary>确定按钮的自动化标识。</summary>
    private const string ConfirmAutomationId = "PresetConfirmButton";

    /// <summary>取消按钮的自动化标识。</summary>
    private const string CancelAutomationId = "PresetCancelButton";

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

        Window dialog = DialogWindow.Create(owner, "新增预设", PresetDialogWidth);

        Grid root = DialogLayout.CreateRoot(DialogLayout.Padding);
        for (int index = 0; index <= FieldCount + 1; index++)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        WpfTextBox nameBox = AddField(root, 0, AnchorNameLabel, AnchorNameAutomationId);
        WpfTextBox linkBox = AddField(root, 1, LinkLabel, LinkAutomationId);

        TextBlock failure = DialogLayout.CreateFailureText();
        Grid.SetRow(failure, FieldCount);

        WpfButton confirm = DialogLayout.CreateButton(ConfirmText, DialogLayout.ButtonMinWidth, isDefault: true);
        confirm.SetResourceReference(FrameworkElement.StyleProperty, DialogLayout.PrimaryButtonKey);
        AutomationProperties.SetAutomationId(confirm, ConfirmAutomationId);
        WpfButton cancel = DialogLayout.CreateButton(CancelText, DialogLayout.ButtonMinWidth, isDefault: false);
        cancel.Margin = new Thickness(DialogLayout.ButtonGap, 0, 0, 0);
        cancel.IsCancel = true;
        AutomationProperties.SetAutomationId(cancel, CancelAutomationId);

        cancel.Click += (_, _) => dialog.DialogResult = false;
        confirm.Click += async (_, _) => await ConfirmAsync(dialog, confirm, cancel, nameBox, linkBox, failure, validator);

        StackPanel buttons = DialogLayout.CreateButtonRow(confirm, cancel);
        Grid.SetRow(buttons, FieldCount + 1);

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
    /// <returns>创建出的输入框。</returns>
    private static WpfTextBox AddField(Grid root, int row, string label, string automationId)
    {
        WpfTextBox box = DialogLayout.CreateField(automationId);

        StackPanel field = new() { Margin = new Thickness(0, 0, 0, DialogLayout.FieldGap) };
        field.Children.Add(DialogLayout.CreateLabel(label));
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
