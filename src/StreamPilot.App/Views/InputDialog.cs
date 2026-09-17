using System.Windows;
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

    /// <summary>弹出输入框。</summary>
    /// <param name="owner">父窗口，可为 <see langword="null"/>。</param>
    /// <param name="title">窗口标题。</param>
    /// <param name="prompt">输入提示。</param>
    /// <param name="defaultValue">默认值。</param>
    /// <returns>用户输入；取消时返回 <see langword="null"/>。</returns>
    public static string? Show(Window? owner, string title, string prompt, string defaultValue = "")
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

        TextBlock label = new()
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = (System.Windows.Media.Brush)WpfApplication.Current.Resources["TextBrush"],
        };
        Grid.SetRow(label, 0);

        WpfTextBox input = new() { Text = defaultValue, Margin = new Thickness(0, 0, 0, 14) };
        Grid.SetRow(input, 1);

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
        confirm.Click += (_, _) => dialog.DialogResult = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);

        root.Children.Add(label);
        root.Children.Add(input);
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
}
