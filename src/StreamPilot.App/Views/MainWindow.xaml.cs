using System.Windows;
using System.Windows.Threading;
using StreamPilot.App.ViewModels;

namespace StreamPilot.App.Views;

/// <summary>
/// 主窗口：只负责装配播放宿主、转发消息与驱动界面刷新定时器，不含业务逻辑。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>录制状态刷新间隔（毫秒）。</summary>
    private const int StatusRefreshIntervalMs = 1000;

    /// <summary>播放宿主（在 <c>Loaded</c> 时挂载到占位容器）。</summary>
    private readonly WebPlayerHost _playerHost;

    private readonly DispatcherTimer _statusTimer;
    private ShellViewModel? _viewModel;

    /// <summary>初始化主窗口。</summary>
    /// <param name="playerHost">播放宿主（由组合根构造并注入日志）。</param>
    public MainWindow(WebPlayerHost playerHost)
    {
        ArgumentNullException.ThrowIfNull(playerHost);
        InitializeComponent();
        _playerHost = playerHost;
        _playerHost.MessageReceived += OnPlayerMessageReceived;
        _playerHost.InitializationFailed += OnPlayerInitializationFailed;
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(StatusRefreshIntervalMs),
        };
        _statusTimer.Tick += OnStatusTimerTick;
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.SendMessageRequested -= OnSendMessageRequested;
        }

        _viewModel = e.NewValue as ShellViewModel;
        if (_viewModel is not null)
        {
            _viewModel.SendMessageRequested += OnSendMessageRequested;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            PlayerHostContainer.Children.Add(_playerHost);
            await _playerHost.InitializeAsync().ConfigureAwait(true);
            _statusTimer.Start();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            MessageBox.Show(
                "初始化播放内核失败：" + exception.Message + Environment.NewLine
                + "请确认已安装 WebView2 运行时（Windows 10/11 通常自带）。",
                "StreamPilot",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        if (_viewModel is not null)
        {
            _viewModel.SendMessageRequested -= OnSendMessageRequested;
        }

        _playerHost.MessageReceived -= OnPlayerMessageReceived;
        _playerHost.InitializationFailed -= OnPlayerInitializationFailed;
    }

    private void OnSendMessageRequested(object? sender, string json) => _playerHost.SendMessage(json);

    private void OnPlayerMessageReceived(object? sender, string json) => _viewModel?.OnPlayerMessage(json);

    private void OnPlayerInitializationFailed(object? sender, string message)
    {
        _viewModel?.OnPlayerMessage(
            "{\"type\":\"error\",\"message\":" + System.Text.Json.JsonSerializer.Serialize(message) + "}");
    }

    private void OnStatusTimerTick(object? sender, EventArgs e) => _viewModel?.RefreshRecordingStatus();
}
