using System.Text.Json;
using System.Windows;
using StreamPilot.App.Services;
using StreamPilot.App.ViewModels;
using StreamPilot.App.Views;
using StreamPilot.Bridge;
using StreamPilot.Core.Caching;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Parsers;
using StreamPilot.Core.Runtime;
using StreamPilot.Core.Services;
using StreamPilot.Parsers;
using StreamPilot.Parsers.Bigo;
using StreamPilot.Parsers.Bilibili;
using StreamPilot.Parsers.Douyin;
using StreamPilot.Parsers.Douyu;
using StreamPilot.Parsers.Huya;
using StreamPilot.Parsers.Yy;

namespace StreamPilot.App;

/// <summary>
/// 应用入口与组合根：在这里完成配置、日志、HTTP、解析、录制、桥接与 UI 的装配。
/// </summary>
/// <remarks>
/// 组合根是唯一允许引用全部工程的位置（见 docs/adr/0002-架构分层.md）。
/// 所有耗时初始化都在窗口显示后进行，避免在 UI 线程执行阻塞操作。
/// </remarks>
public partial class App : Application
{
    /// <summary>JSON 序列化设置（宿主 ↔ 页面消息与配置统一使用 camelCase）。</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private JsonOptionsStore? _optionsStore;
    private RotatingFileLogger? _fileLogger;
    private IStructuredLogger _logger = NullStructuredLogger.Instance;
    private ServiceRegistry? _services;
    private BridgeHost? _bridge;
    private HttpClientFactory? _httpClients;

    /// <summary>当前生效的配置（供 UI 与桥接读取最新值）。</summary>
    internal StreamPilotOptions Options => _optionsStore?.Current ?? new StreamPilotOptions();

    /// <summary>服务注册表（供窗口解析服务）。</summary>
    internal ServiceRegistry Services => _services
        ?? throw new InvalidOperationException("服务容器尚未初始化。");

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            AppPaths.EnsureUserDataDirectory();
            _optionsStore = new JsonOptionsStore(AppPaths.ConfigFile, NullStructuredLogger.Instance);
            StreamPilotOptions options = _optionsStore.Load();

            _fileLogger = new RotatingFileLogger(
                AppPaths.LogDirectory,
                "streampilot",
                options.Logging.VerboseDiagnostics ? LogLevel.Trace : options.Logging.FileLevel,
                options.Logging.MaxFileSizeMb,
                options.Logging.RetainedFileCount);
            _logger = _fileLogger;
            _logger.Info("App", "StreamPilot 启动。", new Dictionary<string, object?>
            {
                ["version"] = AppVersion.Current,
                ["config"] = AppPaths.ConfigFile,
            });

            _services = BuildServices(options);

            WebPlayerHost playerHost = new(_logger);
            MainWindow window = new(playerHost)
            {
                DataContext = new ShellViewModel(BuildShellDependencies(options)),
            };
            MainWindow = window;
            window.Show();

            StartBridgeIfEnabled(options);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(LogLevel.Error, "App", "启动失败。", exception);
            MessageBox.Show(
                "StreamPilot 启动失败：" + exception.Message + Environment.NewLine + "详细日志位于：" + AppPaths.LogDirectory,
                "StreamPilot",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        if (_httpClients is not null)
        {
            _httpClients.Dispose();
        }

        _fileLogger?.Dispose();
        base.OnExit(e);
    }

    /// <summary>把最新配置写回磁盘。</summary>
    /// <param name="options">新配置。</param>
    internal void SaveOptions(StreamPilotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _optionsStore?.Save(options);
    }

    /// <summary>解析服务实例（供窗口在初始化阶段使用）。</summary>
    /// <typeparam name="TService">服务契约类型。</typeparam>
    /// <returns>服务实例。</returns>
    internal TService Resolve<TService>()
        where TService : class => Services.GetRequired<TService>();

    private ShellViewModel.Dependencies BuildShellDependencies(StreamPilotOptions options) => new(
        Resolve<IRoomResolver>(),
        Resolve<IPlaybackCoordinator>(),
        Resolve<IRecordingCoordinator>(),
        Resolve<IPlaybackBridge>(),
        _logger,
        options,
        SaveOptions);

    private ServiceRegistry BuildServices(StreamPilotOptions options)
    {
        ServiceRegistry registry = new();
        registry.RegisterInstance(_logger);

        // 日志与配置
        registry.RegisterInstance(_optionsStore!);
        registry.RegisterSingleton<IOptionsStore>(static context => context.GetRequired<JsonOptionsStore>());

        // HTTP
        HttpClientFactory factory = new(options.Network, _logger);
        _httpClients = factory;
        registry.RegisterInstance(factory);
        HttpTextClient textClient = new(factory, _logger);
        registry.RegisterInstance(textClient);

        // 解析层（Parsers 工程实现 Core 契约）
        registry.RegisterSingleton<IPlatformParserFactory>(_ => new PlatformParserFactory(
        [
            new BilibiliParser(textClient, _logger),
            new DouyinParser(textClient, _logger),
            new HuyaParser(textClient, _logger),
            new DouyuParser(textClient, _logger),
            new YyParser(textClient, _logger),
            new BigoParser(textClient, _logger),
        ]));
        registry.RegisterSingleton(_ => new StreamCache());
        registry.RegisterSingleton<IRoomResolver>(context => new RoomResolver(
            context.GetRequired<IPlatformParserFactory>(),
            context.GetRequired<StreamCache>(),
            _logger));

        // 桥接（可能因端口占用而启动失败，失败不应影响主功能）
        BridgeHost bridge = new(options.Bridge, () => Options.Playback, _logger);
        _bridge = bridge;
        registry.RegisterInstance<IPlaybackBridge>(bridge);

        // 播放与录制编排
        registry.RegisterSingleton<ICandidateValidity>(_ => new CandidateValidity(_logger));
        registry.RegisterSingleton<IPlaybackCoordinator>(context => new PlaybackCoordinator(
            context.GetRequired<IPlaybackBridge>(),
            context.GetRequired<ICandidateValidity>(),
            () => Options,
            _logger));
        registry.RegisterSingleton<IRecordingCoordinator>(_ => new RecordingCoordinator(
            factory,
            () => Options,
            _logger));

        return registry;
    }

    private void StartBridgeIfEnabled(StreamPilotOptions options)
    {
        if (_bridge is null || !options.Bridge.AutoStart)
        {
            return;
        }

        try
        {
            _bridge.Start();
        }
        catch (Core.Errors.BridgeException exception)
        {
            _logger.LogError(LogLevel.Warn, "App", "桥接服务启动失败，mpv 外挂播放将不可用。", exception);
        }
    }
}
