using System.Windows;
using StreamPilot.App.ViewModels;

namespace StreamPilot.App.Views;

/// <summary>
/// 独立的设置窗口（模态）。只负责展示表单与确认/取消，配置的写入由调用方完成。
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>初始化设置窗口。</summary>
    /// <param name="viewModel">设置视图模型。</param>
    public SettingsWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
