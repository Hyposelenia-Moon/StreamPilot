using System.Windows;
using WpfMessageBox = System.Windows.MessageBox;
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

    /// <summary>左侧控制面板展开时的宽度（像素）。</summary>
    private const double ControlPanelWidth = 380;

    /// <summary>
    /// 左侧控制面板收起后的窄条宽度（像素）。
    /// </summary>
    /// <remarks>
    /// 收起后不能把列宽设成 0：恢复按钮必须留在左侧列里，
    /// 否则只能浮在播放区上，而 WebView2 是原生 HWND（HwndHost），会把 WPF 浮层盖住。
    /// </remarks>
    private const double ControlPanelCollapsedWidth = 36;

    /// <summary>播放宿主（在 <c>Loaded</c> 时挂载到占位容器）。</summary>
    private readonly WebPlayerHost _playerHost;

    private readonly DispatcherTimer _statusTimer;
    private ShellViewModel? _viewModel;
    private bool _isFullscreen;
    private bool _panelCollapsed;
    private WindowState _restoreWindowState = WindowState.Normal;
    private WindowStyle _restoreWindowStyle = WindowStyle.SingleBorderWindow;

    /// <summary>初始化主窗口。</summary>
    /// <param name="playerHost">播放宿主（由组合根构造并注入日志）。</param>
    public MainWindow(WebPlayerHost playerHost)
    {
        ArgumentNullException.ThrowIfNull(playerHost);
        InitializeComponent();
        _playerHost = playerHost;
        _playerHost.MessageReceived += OnPlayerMessageReceived;
        _playerHost.InitializationFailed += OnPlayerInitializationFailed;
        _playerHost.FullscreenElementChanged += OnPlayerFullscreenElementChanged;
        _restoreWindowStyle = WindowStyle;
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
            _viewModel.FullscreenChanged -= OnFullscreenChanged;
        }

        _viewModel = e.NewValue as ShellViewModel;
        if (_viewModel is not null)
        {
            _viewModel.SendMessageRequested += OnSendMessageRequested;
            _viewModel.FullscreenChanged += OnFullscreenChanged;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 状态刷新与播放内核无关：即使 WebView2 初始化失败，录制时长/字节也要照常更新。
        _statusTimer.Start();
        try
        {
            PlayerHostContainer.Children.Add(_playerHost);
            await _playerHost.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            WpfMessageBox.Show(
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
            // 先取消后台的预设开播检测，避免回调打到已经开始释放的界面。
            _viewModel.CancelPresetChecks();
            _viewModel.Shutdown();
            _viewModel.SendMessageRequested -= OnSendMessageRequested;
            _viewModel.FullscreenChanged -= OnFullscreenChanged;
        }

        _playerHost.MessageReceived -= OnPlayerMessageReceived;
        _playerHost.InitializationFailed -= OnPlayerInitializationFailed;
        _playerHost.FullscreenElementChanged -= OnPlayerFullscreenElementChanged;
    }

    private void OnSendMessageRequested(object? sender, string json) => _playerHost.SendMessage(json);

    private void OnPlayerMessageReceived(object? sender, string json) => _viewModel?.OnPlayerMessage(json);

    /// <summary>
    /// 页面报告全屏状态变化时切换宿主布局。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="isFullscreen">是否全屏。</param>
    private void OnFullscreenChanged(object? sender, bool isFullscreen) => ApplyFullscreen(isFullscreen);

    /// <summary>
    /// 内核报告全屏元素出现或消失时切换宿主布局（覆盖用户按 Esc 退出全屏的情况）。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="isFullscreen">是否全屏。</param>
    private void OnPlayerFullscreenElementChanged(object? sender, bool isFullscreen)
    {
        if (isFullscreen || _isFullscreen)
        {
            ApplyFullscreen(isFullscreen);
        }
    }

    /// <summary>
    /// 进入或退出全屏：进入时隐藏左侧控制面板并让窗口铺满屏幕，退出时还原。
    /// </summary>
    /// <param name="isFullscreen">是否全屏。</param>
    /// <remarks>
    /// WebView2 只把页面元素放大到宿主控件范围内，所以"铺满屏幕"必须由窗口自己完成。
    /// </remarks>
    private void ApplyFullscreen(bool isFullscreen)
    {
        if (_isFullscreen == isFullscreen)
        {
            return;
        }

        _isFullscreen = isFullscreen;
        if (isFullscreen)
        {
            _restoreWindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            _restoreWindowStyle = WindowStyle;
            ControlPanel.Visibility = Visibility.Collapsed;
            ControlColumn.Width = new GridLength(0);
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = _restoreWindowStyle;
            WindowState = _restoreWindowState;
            ApplyControlPanelLayout();
        }

        _viewModel?.NotifyFullscreenChanged(isFullscreen);
    }

    /// <summary>
    /// 顶栏「◀」：收起左侧控制面板（把宽度让给画面），不进入全屏。
    /// </summary>
    private void OnToggleControlPanel(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen)
        {
            return;
        }

        SetControlPanelCollapsed(!_panelCollapsed);
    }

    /// <summary>折叠后浮在画面上方的「☰ 控制面板」：恢复展开。</summary>
    private void OnRestoreControlPanel(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen)
        {
            return;
        }

        SetControlPanelCollapsed(false);
    }

    /// <summary>
    /// 展开或收起左侧控制面板，并同步折叠后画面上的「展开」入口。
    /// </summary>
    /// <param name="collapsed">是否收起。</param>
    private void SetControlPanelCollapsed(bool collapsed)
    {
        if (_panelCollapsed == collapsed)
        {
            return;
        }

        _panelCollapsed = collapsed;
        ApplyControlPanelLayout();
    }

    /// <summary>按当前折叠状态摆放左侧面板列与「展开」入口。</summary>
    /// <remarks>
    /// 收起时保留 <see cref="ControlPanelCollapsedWidth"/> 的窄条而不是收成 0：
    /// 恢复按钮必须留在左侧列内，浮在播放区上的 WPF 按钮会被 WebView2 的原生窗口盖住，
    /// 表现就是"点了收起之后再也恢复不了"。
    /// </remarks>
    private void ApplyControlPanelLayout()
    {
        ControlPanel.Visibility = Visibility.Visible;
        if (_panelCollapsed)
        {
            ControlColumn.Width = new GridLength(ControlPanelCollapsedWidth);
            ControlPanelContent.Visibility = Visibility.Collapsed;
            CollapsedRail.Visibility = Visibility.Visible;
        }
        else
        {
            ControlColumn.Width = new GridLength(ControlPanelWidth);
            ControlPanelContent.Visibility = Visibility.Visible;
            CollapsedRail.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPlayerInitializationFailed(object? sender, string message)
    {
        _viewModel?.OnPlayerMessage(
            "{\"type\":\"error\",\"message\":" + System.Text.Json.JsonSerializer.Serialize(message) + "}");
    }

    private void OnStatusTimerTick(object? sender, EventArgs e) => _viewModel?.RefreshRecordingStatus();
}
