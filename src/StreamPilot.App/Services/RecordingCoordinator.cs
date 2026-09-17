namespace StreamPilot.App.Services;

using StreamPilot.Core.Configuration;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Services;
using StreamPilot.Recording;

/// <summary>
/// 录制用例编排：维护"每房间一个录制会话"的约束并提供状态查询。
/// </summary>
public sealed class RecordingCoordinator : IRecordingCoordinator
{
    private readonly HttpClientFactory _httpClients;
    private readonly Func<StreamPilotOptions> _optionsProvider;
    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "App.Recording";
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RecordingSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>初始化录制编排器。</summary>
    /// <param name="httpClients">HTTP 客户端工厂。</param>
    /// <param name="optionsProvider">当前配置读取回调。</param>
    /// <param name="logger">结构化日志。</param>
    public RecordingCoordinator(
        HttpClientFactory httpClients,
        Func<StreamPilotOptions> optionsProvider,
        IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClients);
        ArgumentNullException.ThrowIfNull(optionsProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClients = httpClients;
        _optionsProvider = optionsProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IRecordingSession> StartAsync(RecordingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string key = BuildKey(request.Room.Platform, request.Room.RoomId);
        lock (_gate)
        {
            if (_sessions.ContainsKey(key))
            {
                throw new Core.Errors.RecordingException(
                    Core.Errors.RecordingErrorCategory.Unknown,
                    $"{request.Room.Anchor}（{request.Room.RoomId}）已在录制中。");
            }
        }

        StreamPilotOptions options = _optionsProvider();
        RecordingRequest effective = request with
        {
            OutputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory)
                ? options.Recording.OutputDirectory
                : request.OutputDirectory,
            SegmentPolicy = request.SegmentPolicy ?? options.Recording.Segment,
            MaxDurationMinutes = request.MaxDurationMinutes ?? options.Recording.MaxDurationMinutes,
        };

        RecordingSession session = await RecordingSession.StartAsync(
            effective,
            _httpClients,
            options.Recording.StallTimeoutSeconds,
            options.Recording.MaxReconnectAttempts,
            _logger).ConfigureAwait(false);

        lock (_gate)
        {
            _sessions[key] = session;
        }

        _logger.Info(_moduleName, "录制会话已启动。", new Dictionary<string, object?>
        {
            ["platform"] = effective.Room.Platform.ToString(),
            ["roomId"] = effective.Room.RoomId,
            ["anchor"] = effective.Room.Anchor,
            ["output"] = session.OutputDirectory,
        });

        return session;
    }

    /// <inheritdoc />
    public IReadOnlyList<RecordingStatus> ListActive()
    {
        List<RecordingStatus> statuses = [];
        lock (_gate)
        {
            foreach (KeyValuePair<string, RecordingSession> pair in _sessions)
            {
                statuses.Add(pair.Value.Status);
            }
        }

        return statuses;
    }

    /// <inheritdoc />
    public async Task<bool> StopAsync(PlatformId platform, string roomId, CancellationToken cancellationToken)
    {
        string key = BuildKey(platform, roomId);
        RecordingSession? session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out session))
            {
                return false;
            }

            _sessions.Remove(key);
        }

        await session.StopAsync(RecordingStopReason.UserStopped, cancellationToken).ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);

        _logger.Info(_moduleName, "录制会话已停止。", new Dictionary<string, object?>
        {
            ["platform"] = platform.ToString(),
            ["roomId"] = roomId,
            ["metadata"] = Path.GetFileName(session.MetadataPath),
        });

        return true;
    }

    private static string BuildKey(PlatformId platform, string roomId) => $"{(int)platform}:{roomId}";
}
