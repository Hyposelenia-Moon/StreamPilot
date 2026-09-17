using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using WpfApplication = System.Windows.Application;
using WpfButton = System.Windows.Controls.Button;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace StreamPilot.App.Views;

/// <summary>
/// 通用单行输入对话框（用于"保存预设"等需要用户命名的场景）。
/// </summary>
public static class InputDialog
{
    /// <summary>对话框左右统一的边距。</summary>
    private const double DialogPadding = 16;

    /// <summary>按钮最小宽度。</summary>
    private const double ButtonMinWidth = 84;

    /// <summary>
    /// 弹出输入框。
    /// </summary>
    /// <param name="owner">父窗口，可为 <see langword="null"/>。</param>
    /// <param name="title">窗口标题。</param>
    /// <param name="prompt">输入提示。</param>
    /// <param name="defaultValue">默认值。</param>
    /// <param name="validator">
    /// 可选的异步校验回调：返回 <see langword="null"/> 表示通过并关闭对话框，
    /// 返回提示文本表示不通过（对话框保持打开并显示该文本）。为 <see langword="null"/> 时不校验。
    /// </param>
    /// <returns>用户输入；取消时返回 <see langword="null"/>。</returns>
    public static string? Show(
        Window? owner,
        string title,
        string prompt,
        string defaultValue = "",
        Func<string, Task<string?>>? validator = null)
    {
        Window dialog = new()
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)WpfApplication.Current.Resources["WindowBackgroundBrush"],
            Icon = (System.Windows.Media.ImageSource)WpfApplication.Current.Resources["AppIcon"],
        };

        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        Grid root = new() { Margin = new Thickness(DialogPadding) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock label = new()
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = (System.Windows.Media.Brush)WpfApplication.Current.Resources["TextBrush"],
        };
        Grid.SetRow(label, 0);

        WpfTextBox input = new() { Text = defaultValue, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(input, "InputDialogTextBox");
        Grid.SetRow(input, 1);

        TextBlock failure = new()
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        failure.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        Grid.SetRow(failure, 2);

        StackPanel buttons = new()
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
        };

        WpfButton confirm = new() { Content = "确定", MinWidth = ButtonMinWidth, IsDefault = true };
        WpfButton cancel = new()
        {
            Content = "取消",
            MinWidth = ButtonMinWidth,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);

        if (validator is null)
        {
            confirm.Click += (_, _) => dialog.DialogResult = true;
        }
        else
        {
            confirm.Click += async (_, _) => await ConfirmWithValidationAsync(dialog, confirm, cancel, input, failure, validator);
        }

        root.Children.Add(label);
        root.Children.Add(input);
        root.Children.Add(failure);
        root.Children.Add(buttons);
        dialog.Content = root;

        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        bool? result = dialog.ShowDialog();
        return result == true ? input.Text : null;
    }

    /// <summary>
    /// 校验输入后再决定是否关闭对话框：校验期间禁用按钮并显示"校验中…"，避免重复提交。
    /// </summary>
    /// <param name="dialog">对话框。</param>
    /// <param name="confirm">确定按钮。</param>
    /// <param name="cancel">取消按钮。</param>
    /// <param name="input">输入框。</param>
    /// <param name="failure">失败提示文本。</param>
    /// <param name="validator">校验回调。</param>
    /// <returns>异步任务。</returns>
    private static async Task ConfirmWithValidationAsync(
        Window dialog,
        WpfButton confirm,
        WpfButton cancel,
        WpfTextBox input,
        TextBlock failure,
        Func<string, Task<string?>> validator)
    {
        failure.Text = string.Empty;
        confirm.IsEnabled = false;
        cancel.IsEnabled = false;
        confirm.Content = "校验中…";
        try
        {
            string? error = await validator(input.Text ?? string.Empty).ConfigureAwait(true);
            if (error is null)
            {
                dialog.DialogResult = true;
                return;
            }

            failure.Text = error;
        }
        finally
        {
            confirm.Content = "确定";
            confirm.IsEnabled = true;
            cancel.IsEnabled = true;
        }
    }
}
