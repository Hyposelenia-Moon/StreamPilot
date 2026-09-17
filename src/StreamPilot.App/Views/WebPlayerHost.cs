namespace StreamPilot.App.Views;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Logging;

/// <summary>
/// WebView2 播放宿主：把 <c>Web\</c> 目录映射为虚拟主机并承载播放页。
/// </summary>
/// <remarks>
/// 要点（docs/adr/0005-bridge-and-packaging.md）：
/// <list type="bullet">
///   <item>必须使用 <c>https://appassets.local/</c> 这类虚拟主机而不是 <c>file://</c>，
///         否则候选探测的 <c>mode:'cors'</c> 与 Private Network Access 都会失败；</item>
///   <item>页面不暴露任何可被宿主直接调用的 <c>window</c> 方法，控制一律走消息；</item>
///   <item>消息处理不阻塞 UI：收到消息只做轻量解析并转发给 ViewModel。</item>
/// </list>
/// </remarks>
public sealed class WebPlayerHost : UserControl, IAsyncDisposable
{
    /// <summary>虚拟主机名。</summary>
    public const string VirtualHostName = "appassets.local";

    /// <summary>虚拟主机基地址。</summary>
    public const string VirtualHostBase = "https://" + VirtualHostName + "/";

    /// <summary>播放页相对路径。</summary>
    public const string PlayerPage = "player.html";

    /// <summary>
    /// 允许"无用户手势自动播放"的内核启动参数。
    /// </summary>
    /// <remarks>
    /// Chromium 默认策略是 <c>document-user-activation-required</c>：没有用户手势时不允许带声音自动播放，
    /// WebView2 继承该策略，于是播放页的 <c>video.play()</c> 会立刻抛 <c>NotAllowedError</c>，
    /// 用户看到的现象就是"必须先点一下画面才开始播放"。
    /// 显式声明 Chromium 的 <c>autoplay-policy=no-user-gesture-required</c> 开关（命令行前缀由
    /// <see cref="AutoplayBrowserArgument"/> 给出）后，首帧无需点击即可自动播放。
    /// 播放页仍保留"静音起播后立刻恢复音量"的兜底，见 Web/player.html。
    /// </remarks>
    private const string AutoplayBrowserArgument = "--autoplay-policy=no-user-gesture-required";

    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "App.WebPlayer";
    private readonly WebView2 _webView = new();
    private bool _initialized;
    private bool _disposed;

    /// <summary>初始化宿主控件。</summary>
    /// <param name="logger">结构化日志。</param>
    public WebPlayerHost(IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Content = _webView;
    }

    /// <summary>页面 → 宿主消息到达事件（参数为 JSON 文本）。</summary>
    public event EventHandler<string>? MessageReceived;

    /// <summary>内核初始化失败时触发（参数为面向用户的错误说明）。</summary>
    public event EventHandler<string>? InitializationFailed;

    /// <summary>
    /// 页面内的全屏元素出现或消失时触发（参数为当前是否处于全屏）。
    /// </summary>
    /// <remarks>
    /// <see cref="CoreWebView2.ContainsFullScreenElement"/> 由内核维护且只读；宿主只能监听它的变化，
    /// 再自行把播放区域放大到整个窗口，否则页面全屏后画面尺寸不变。
    /// </remarks>
    public event EventHandler<bool>? FullscreenElementChanged;

    /// <summary>页面是否已完成加载。</summary>
    public bool IsPageLoaded { get; private set; }

    /// <summary>
    /// 初始化 WebView2 并导航到播放页。
    /// </summary>
    /// <returns>异步任务。</returns>
    /// <exception cref="InvalidOperationException">WebView2 运行时缺失或播放页目录不存在时抛出。</exception>
    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        string webRoot = AppPaths.WebDirectory;
        if (!Directory.Exists(webRoot))
        {
            string message = "播放页目录不存在：" + webRoot + "。请确认发布包中包含 Web 目录。";
            InitializationFailed?.Invoke(this, message);
            throw new InvalidOperationException(message);
        }

        string userDataFolder = Path.Combine(AppPaths.UserDataDirectory, "WebView2");
        Directory.CreateDirectory(userDataFolder);

        CoreWebView2EnvironmentOptions environmentOptions = new()
        {
            AdditionalBrowserArguments = AutoplayBrowserArgument,
        };

        CoreWebView2Environment environment = await CoreWebView2Environment
            .CreateAsync(browserExecutableFolder: null, userDataFolder: userDataFolder, options: environmentOptions)
            .ConfigureAwait(true);

        await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        CoreWebView2 core = _webView.CoreWebView2;

        // 播放页采用浅色主题，显式声明以免原生控件（滚动条/表单）跟随系统深色主题。
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;

        // 页面首帧渲染前的默认底色，避免加载瞬间闪色（与播放页浅色背景一致）。
        // 注意：WPF 版 WebView2 的 DefaultBackgroundColor 使用 System.Drawing.Color。
        _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xF5, 0xF6, 0xF8);

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ContainsFullScreenElementChanged += OnContainsFullScreenElementChanged;
        _webView.NavigationStarting += OnNavigationStarting;

        _initialized = true;
        core.Navigate(VirtualHostBase + PlayerPage);
        _logger.Info(_moduleName, "播放宿主已初始化。", new Dictionary<string, object?>
        {
            ["url"] = VirtualHostBase + PlayerPage,
            ["webRoot"] = webRoot,
            ["autoplayPolicy"] = "no-user-gesture-required",
        });
    }

    /// <summary>
    /// 向播放页发送 JSON 消息。
    /// </summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>发送成功返回 <see langword="true"/>。</returns>
    public bool SendMessage(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (!_initialized || _webView.CoreWebView2 is null)
        {
            _logger.Warn(_moduleName, "播放宿主尚未初始化，消息被丢弃。");
            return false;
        }

        _webView.CoreWebView2.PostWebMessageAsJson(json);
        return true;
    }

    /// <summary>
    /// 把页面全屏元素的变化转成宿主事件，交由窗口决定如何放大播放区域。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="args">内核事件参数（未使用）。</param>
    private void OnContainsFullScreenElementChanged(object? sender, object args)
    {
        bool isFullscreen = _webView.CoreWebView2?.ContainsFullScreenElement ?? false;
        _logger.Info(_moduleName, "播放页全屏元素状态变化。", new Dictionary<string, object?>
        {
            ["fullscreen"] = isFullscreen,
        });
        FullscreenElementChanged?.Invoke(this, isFullscreen);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_webView.CoreWebView2 is not null)
            {
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                _webView.CoreWebView2.ContainsFullScreenElementChanged -= OnContainsFullScreenElementChanged;
            }

            _webView.NavigationStarting -= OnNavigationStarting;
            _webView.Dispose();
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogError(LogLevel.Debug, _moduleName, "释放 WebView2 时发生异常。", exception);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string json = e.WebMessageAsJson;
            MessageReceived?.Invoke(this, json);
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogError(LogLevel.Warn, _moduleName, "读取播放页消息失败。", exception);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        IsPageLoaded = e.IsSuccess;
        if (e.IsSuccess)
        {
            _logger.Info(_moduleName, "播放页加载完成。");
            return;
        }

        string message = "播放页加载失败：" + e.WebErrorStatus + "。";
        _logger.Error(_moduleName, message);
        InitializationFailed?.Invoke(this, message);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        string target = e.Uri ?? string.Empty;
        if (target.StartsWith(VirtualHostBase, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 禁止离开本地播放页（例如页面内的误点击外链）。
        e.Cancel = true;
        _logger.Warn(_moduleName, "已阻止播放页导航到非本地地址。", new Dictionary<string, object?>
        {
            ["host"] = Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) ? uri.Host : "invalid",
        });
    }
}
