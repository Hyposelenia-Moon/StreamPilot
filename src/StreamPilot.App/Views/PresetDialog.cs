using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using StreamPilot.App.Services;
using StreamPilot.App.ViewModels;
using StreamPilot.Core.Models;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace StreamPilot.App.Views;

/// <summary>
/// 「新增预设」专用对话框：一次填写主播名称、直播链接与平台，确定时先校验（解析）再保存。
/// </summary>
/// <remarks>
/// 不依赖"直播源"卡片里的输入，预设内容完全来自本对话框的三个字段。
/// 平台字段的初值是当前生效平台，粘贴直播链接时按域名自动改选（见 <see cref="PlatformDetector"/>），
/// 用户手动改过之后不再被自动识别覆盖，因此"房间号 + 手选平台"也能存成非 B 站的预设。
/// 校验由调用方以异步回调提供：返回 <see langword="null"/> 表示通过并关闭对话框，
/// 返回提示文本表示不通过（对话框保持打开，用户可修正后重试）。
/// 外观（背景、字号、间距、按钮样式）与 <see cref="InputDialog"/> 共用
/// <see cref="DialogLayout"/> / <see cref="DialogWindow"/>，避免两个对话框风格不一致。
/// </remarks>
public static class PresetDialog
{
    /// <summary>对话框宽度。</summary>
    private const double PresetDialogWidth = 460;

    /// <summary>主播名称字段所在行。</summary>
    private const int AnchorNameRow = 0;

    /// <summary>直播链接字段所在行。</summary>
    private const int LinkRow = 1;

    /// <summary>平台字段所在行。</summary>
    private const int PlatformRow = 2;

    /// <summary>失败提示所在行。</summary>
    private const int FailureRow = 3;

    /// <summary>按钮行所在行。</summary>
    private const int ButtonRow = 4;

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

    /// <summary>平台下拉框的自动化标识。</summary>
    private const string PlatformAutomationId = "PresetPlatformBox";

    /// <summary>主播名称字段的标签。</summary>
    private const string AnchorNameLabel = "主播名称";

    /// <summary>直播链接字段的标签。</summary>
    private const string LinkLabel = "直播链接";

    /// <summary>平台字段的标签。</summary>
    private const string PlatformLabel = "平台";

    /// <summary>平台下拉框的提示。</summary>
    private const string PlatformToolTip = "粘贴直播链接后自动识别平台，也可以在这里手动改选";

    /// <summary>确定按钮的自动化标识。</summary>
    private const string ConfirmAutomationId = "PresetConfirmButton";

    /// <summary>取消按钮的自动化标识。</summary>
    private const string CancelAutomationId = "PresetCancelButton";

    /// <summary>
    /// 弹出「新增预设」对话框。
    /// </summary>
    /// <param name="owner">父窗口，可为 <see langword="null"/>。</param>
    /// <param name="initialPlatform">平台字段的初值（通常取当前生效平台）。</param>
    /// <param name="validator">
    /// 校验并保存回调：入参为（主播名称，直播链接，平台），返回 <see langword="null"/> 表示已保存并关闭对话框，
    /// 返回提示文本表示校验失败（对话框保持打开）。
    /// </param>
    public static void Show(Window? owner, PlatformId initialPlatform, Func<string, string, PlatformId, Task<string?>> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);

        Window dialog = DialogWindow.Create(owner, "新增预设", PresetDialogWidth);

        Grid root = DialogLayout.CreateRoot(DialogLayout.Padding);
        for (int index = 0; index <= ButtonRow; index++)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        WpfTextBox nameBox = AddField(root, AnchorNameRow, AnchorNameLabel, AnchorNameAutomationId);
        WpfTextBox linkBox = AddField(root, LinkRow, LinkLabel, LinkAutomationId);
        PlatformPicker picker = AddPlatformField(root, PlatformRow, initialPlatform);

        TextBlock failure = DialogLayout.CreateFailureText();
        Grid.SetRow(failure, FailureRow);

        WpfButton confirm = DialogLayout.CreateButton(ConfirmText, DialogLayout.ButtonMinWidth, isDefault: true);
        confirm.SetResourceReference(FrameworkElement.StyleProperty, DialogLayout.PrimaryButtonKey);
        AutomationProperties.SetAutomationId(confirm, ConfirmAutomationId);
        WpfButton cancel = DialogLayout.CreateButton(CancelText, DialogLayout.ButtonMinWidth, isDefault: false);
        cancel.Margin = new Thickness(DialogLayout.ButtonGap, 0, 0, 0);
        cancel.IsCancel = true;
        AutomationProperties.SetAutomationId(cancel, CancelAutomationId);

        cancel.Click += (_, _) => dialog.DialogResult = false;
        confirm.Click += async (_, _) => await ConfirmAsync(dialog, confirm, cancel, nameBox, linkBox, picker, failure, validator);

        StackPanel buttons = DialogLayout.CreateButtonRow(confirm, cancel);
        Grid.SetRow(buttons, ButtonRow);

        root.Children.Add(failure);
        root.Children.Add(buttons);
        dialog.Content = root;

        // 链接是"平台从哪来"的唯一客观依据：边打边识别，用户手动改过之后就不再覆盖他的选择。
        linkBox.TextChanged += (_, _) => picker.ApplyDetectedPlatform(linkBox.Text);

        dialog.Loaded += (_, _) =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        };

        dialog.ShowDialog();
    }

    /// <summary>追加一个"标签行 + 输入框行"文本字段。</summary>
    /// <param name="root">对话框根面板。</param>
    /// <param name="row">该字段要占用的网格行（标签与输入框同处一行内的堆叠面板）。</param>
    /// <param name="label">字段标签。</param>
    /// <param name="automationId">输入框的自动化标识。</param>
    /// <returns>创建出的输入框。</returns>
    private static WpfTextBox AddField(Grid root, int row, string label, string automationId)
    {
        WpfTextBox box = DialogLayout.CreateField(automationId);
        AddFieldRow(root, row, label, box);
        return box;
    }

    /// <summary>追加一个"标签行 + 平台下拉框行"字段。</summary>
    /// <param name="root">对话框根面板。</param>
    /// <param name="row">该字段要占用的网格行。</param>
    /// <param name="initialPlatform">下拉框初值平台。</param>
    /// <returns>平台选择器。</returns>
    private static PlatformPicker AddPlatformField(Grid root, int row, PlatformId initialPlatform)
    {
        WpfComboBox box = DialogLayout.CreateComboBox(PlatformAutomationId);
        box.ToolTip = PlatformToolTip;
        AddFieldRow(root, row, PlatformLabel, box);
        return new PlatformPicker(box, initialPlatform);
    }

    /// <summary>把标签与控件放进一行字段组并加进根面板。</summary>
    /// <param name="root">对话框根面板。</param>
    /// <param name="row">该字段要占用的网格行。</param>
    /// <param name="label">字段标签。</param>
    /// <param name="control">字段控件（输入框或下拉框）。</param>
    private static void AddFieldRow(Grid root, int row, string label, FrameworkElement control)
    {
        StackPanel field = new() { Margin = new Thickness(0, 0, 0, DialogLayout.FieldGap) };
        field.Children.Add(DialogLayout.CreateLabel(label));
        field.Children.Add(control);
        Grid.SetRow(field, row);
        root.Children.Add(field);
    }

    /// <summary>校验并保存：校验期间禁用按钮，失败时把原因显示在对话框里并保持打开。</summary>
    /// <param name="dialog">对话框。</param>
    /// <param name="confirm">确定按钮。</param>
    /// <param name="cancel">取消按钮。</param>
    /// <param name="nameBox">主播名称输入框。</param>
    /// <param name="linkBox">直播链接输入框。</param>
    /// <param name="picker">平台选择器。</param>
    /// <param name="failure">失败提示文本。</param>
    /// <param name="validator">校验并保存回调。</param>
    /// <returns>异步任务。</returns>
    private static async Task ConfirmAsync(
        Window dialog,
        WpfButton confirm,
        WpfButton cancel,
        WpfTextBox nameBox,
        WpfTextBox linkBox,
        PlatformPicker picker,
        TextBlock failure,
        Func<string, string, PlatformId, Task<string?>> validator)
    {
        failure.Text = string.Empty;
        confirm.IsEnabled = false;
        cancel.IsEnabled = false;
        confirm.Content = ConfirmingText;
        try
        {
            string? error = await validator(
                nameBox.Text ?? string.Empty,
                linkBox.Text ?? string.Empty,
                picker.SelectedPlatform).ConfigureAwait(true);
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

    /// <summary>
    /// 对话框里的平台选择：下拉项是平台展示名（与设置里的「默认平台」下拉同一份列表），
    /// 链接变化时按域名自动改选，用户手动改过之后不再自动覆盖。
    /// </summary>
    private sealed class PlatformPicker
    {
        private readonly WpfComboBox _box;
        private bool _chosenManually;
        private bool _applyingDetection;

        /// <summary>初始化平台选择控件。</summary>
        /// <param name="box">平台下拉框。</param>
        /// <param name="initialPlatform">初值平台；不在可用列表里时取第一项。</param>
        public PlatformPicker(WpfComboBox box, PlatformId initialPlatform)
        {
            _box = box;
            box.ItemsSource = BuildDisplayNames();
            box.SelectedIndex = IndexOfOrDefault(initialPlatform);

            // 订阅必须放在初值设置之后：初值本身不是"用户手动改的"。
            // 用 lambda 而非具名方法，避免与 WinForms 的同名事件参数类型产生歧义（App 同时全局引用了 WinForms）。
            box.SelectionChanged += (_, _) =>
            {
                if (!_applyingDetection)
                {
                    _chosenManually = true;
                }
            };
        }

        /// <summary>当前选中的平台。</summary>
        /// <remarks>下拉框始终有一项被选中；理论上取不到时返回 <see cref="PlatformId.Unknown"/>，由调用方回退默认平台。</remarks>
        public PlatformId SelectedPlatform
        {
            get
            {
                int index = _box.SelectedIndex;
                return index < 0 || index >= PlatformOption.All.Count
                    ? PlatformId.Unknown
                    : PlatformOption.All[index].Id;
            }
        }

        /// <summary>按输入文本自动识别平台并改选（用户手动改过时不生效）。</summary>
        /// <param name="input">直播链接或房间号。</param>
        public void ApplyDetectedPlatform(string? input)
        {
            if (_chosenManually)
            {
                return;
            }

            PlatformId? detected = PlatformDetector.Detect(input);
            if (detected is null || detected.Value == SelectedPlatform)
            {
                return;
            }

            _applyingDetection = true;
            _box.SelectedIndex = IndexOfOrDefault(detected.Value);
            _applyingDetection = false;
        }

        /// <summary>下拉项文案（顺序与 <see cref="PlatformOption.All"/> 一致）。</summary>
        /// <returns>展示名数组。</returns>
        private static string[] BuildDisplayNames()
        {
            string[] names = new string[PlatformOption.All.Count];
            for (int index = 0; index < PlatformOption.All.Count; index++)
            {
                names[index] = PlatformOption.All[index].DisplayName;
            }

            return names;
        }

        /// <summary>取平台在列表中的下标。</summary>
        /// <param name="platform">平台标识。</param>
        /// <returns>下标；不在列表里时返回第一项的下标。</returns>
        private static int IndexOfOrDefault(PlatformId platform)
        {
            for (int index = 0; index < PlatformOption.All.Count; index++)
            {
                if (PlatformOption.All[index].Id == platform)
                {
                    return index;
                }
            }

            return 0;
        }
    }
}
