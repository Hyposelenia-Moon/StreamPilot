namespace StreamPilot.App.Services;

using StreamPilot.Core.Configuration;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Services;

/// <summary>
/// 播放用例编排：把解析结果转换为 Web 播放页可消费的候选列表。
/// </summary>
/// <remarks>
/// 职责：
/// <list type="bullet">
///   <item>校验候选有效期与协议合法性（<see cref="ICandidateValidity"/>）；</item>
///   <item>按参考项目的经验做 Web 端可播性判断（HEVC 需要 mpv，RTMP 不可播）；</item>
///   <item>需要 Referer 的流交由桥接中继，避免 WebView2 跨域与 Referer 限制；</item>
///   <item>分配播放会话号，供宿主丢弃过期消息。</item>
/// </list>
/// </remarks>
public sealed class PlaybackCoordinator : IPlaybackCoordinator
{
    /// <summary>每个房间最多交给页面的候选数量（防止探测请求过多）。</summary>
    public const int MaxCandidatesPerRoom = 8;

    private readonly IPlaybackBridge _bridge;
    private readonly ICandidateValidity _validity;
    private readonly Func<StreamPilotOptions> _optionsProvider;
    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "App.Playback";
    private readonly Lock _relayGate = new();
    private readonly List<string> _activeRelayUrls = [];
    private readonly List<string> _previousRelayUrls = [];
    private int _nextSessionId;

    /// <summary>初始化播放编排器。</summary>
    /// <param name="bridge">桥接服务（可能未启动）。</param>
    /// <param name="validity">候选有效期校验。</param>
    /// <param name="optionsProvider">当前配置读取回调。</param>
    /// <param name="logger">结构化日志。</param>
    public PlaybackCoordinator(
        IPlaybackBridge bridge,
        ICandidateValidity validity,
        Func<StreamPilotOptions> optionsProvider,
        IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(validity);
        ArgumentNullException.ThrowIfNull(optionsProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _bridge = bridge;
        _validity = validity;
        _optionsProvider = optionsProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public int ActiveSessionId { get; private set; }

    /// <inheritdoc />
    public Task<PlaybackPlan> PrepareAsync(PlaybackRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        int sessionId = Interlocked.Increment(ref _nextSessionId);
        ActiveSessionId = sessionId;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StreamPilotOptions options = _optionsProvider();

        // 新一轮播放开始前把"上一轮"挪到待回收位置，而不是立刻释放：
        // 切画质/重解析时页面可能还在播旧地址，立刻释放会让画面黑屏。
        RetireActiveRelays();

        List<WebPlayerCandidate> playable = [];
        bool hasUnsupportedCodec = false;
        bool usedRelay = false;

        foreach (StreamCandidate candidate in request.Room.Candidates)
        {
            if (playable.Count >= MaxCandidatesPerRoom)
            {
                break;
            }

            if (!candidate.IsWebPlayable())
            {
                continue;
            }

            try
            {
                _validity.EnsureUsable(candidate, now);
            }
            catch (Core.Errors.ResolveException exception)
            {
                _logger.Warn(_moduleName, "跳过不可用候选。", exception.ToLogFields());
                continue;
            }

            string url = candidate.Url;
            if (request.AllowRelay && options.Bridge.AutoStart && _bridge.IsRunning)
            {
                try
                {
                    // 一律走本地中继：页面源是 https，而很多平台只给 http 流或要求 Referer，
                    // 直连会被混合内容 / CORS / 防盗链三重拦住（表现为"所有候选线路均不可用"）。
                    url = _bridge.RegisterRelay(new RelayTarget
                    {
                        UpstreamUrl = candidate.Url,
                        Referer = candidate.HttpReferer,
                        Kind = IsPlaylist(candidate.Format) ? RelayKind.HlsPlaylist : RelayKind.Stream,
                    });
                    usedRelay = true;
                    lock (_relayGate)
                    {
                        _activeRelayUrls.Add(url);
                    }
                }
                catch (Core.Errors.BridgeException exception)
                {
                    _logger.LogError(LogLevel.Warn, _moduleName, "注册中继失败，回退直连。", exception);
                    url = candidate.Url;
                }
            }

            playable.Add(new WebPlayerCandidate
            {
                SourceIndex = candidate.SourceIndex,
                Url = url,
                Format = FormatName(candidate.Format),
                Codec = candidate.Codec.ToString().ToLowerInvariant(),
                Host = candidate.CdnHost,
                UrlFingerprint = candidate.UrlFingerprint,
                Referer = candidate.HttpReferer,
                Label = ResolveMessages.ForQuality(candidate.Quality),
            });
        }

        foreach (StreamCandidate candidate in request.Room.Candidates)
        {
            if (candidate.Codec == VideoCodec.Hevc && candidate.IsWebPlayable())
            {
                hasUnsupportedCodec = true;
                break;
            }
        }

        string? hint = null;
        if (playable.Count == 0)
        {
            hint = "没有可在 Web 端播放的候选线路";
            if (request.Room.Candidates.Count > 0 && request.Room.Candidates[0].Format == StreamFormat.Rtmp)
            {
                hint = "该直播间仅提供 RTMP 流，Web 端无法播放，请点击「mpv 播放」。";
            }
        }
        else if (usedRelay)
        {
            hint = "部分线路已通过本地中继加载。";
        }

        PlaybackPlan plan = new()
        {
            SessionId = sessionId,
            Room = request.Room,
            Mode = request.Mode == PlaybackMode.Extreme ? "extreme" : "stable",
            ExtremeTargetMs = request.NormalizeExtremeTargetMs(),
            Candidates = playable,
            HasUnsupportedCodec = hasUnsupportedCodec,
            Hint = hint,
            Qualities = request.Room.Qualities,
            SelectedQualityKey = request.Room.SelectedQualityKey,
        };

        _logger.Info(_moduleName, "播放计划已准备。", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["platform"] = request.Room.Platform.ToString(),
            ["roomId"] = request.Room.RoomId,
            ["mode"] = plan.Mode,
            ["extremeTargetMs"] = plan.ExtremeTargetMs,
            ["candidates"] = playable.Count,
            ["relay"] = usedRelay,
            ["unsupportedCodec"] = hasUnsupportedCodec,
        });

        return Task.FromResult(plan);
    }

    /// <inheritdoc />
    public void ReleasePreviousRelays()
    {
        string[] urls;
        lock (_relayGate)
        {
            if (_previousRelayUrls.Count == 0)
            {
                return;
            }

            urls = [.. _previousRelayUrls];
            _previousRelayUrls.Clear();
        }

        foreach (string url in urls)
        {
            _bridge.ReleaseRelay(url);
        }
    }

    /// <inheritdoc />
    public void StopActive()
    {
        ActiveSessionId = 0;
        ReleasePreviousRelays();
        ReleaseActiveRelays();
    }

    /// <summary>把当前会话的中继挪到"上一轮"待回收列表（新会话起播后由宿主回收）。</summary>
    private void RetireActiveRelays()
    {
        ReleasePreviousRelays();
        lock (_relayGate)
        {
            if (_activeRelayUrls.Count == 0)
            {
                return;
            }

            _previousRelayUrls.AddRange(_activeRelayUrls);
            _activeRelayUrls.Clear();
        }
    }

    /// <summary>释放当前记录的所有中继注册。</summary>
    private void ReleaseActiveRelays()
    {
        string[] urls;
        lock (_relayGate)
        {
            if (_activeRelayUrls.Count == 0)
            {
                return;
            }

            urls = [.. _activeRelayUrls];
            _activeRelayUrls.Clear();
        }

        foreach (string url in urls)
        {
            _bridge.ReleaseRelay(url);
        }
    }

    /// <summary>
    /// 判断候选格式是否需要按 HLS 播放列表中继（需要逐行改写内部地址）。
    /// </summary>
    /// <param name="format">候选格式。</param>
    /// <returns>播放列表格式返回 <see langword="true"/>。</returns>
    private static bool IsPlaylist(StreamFormat format) =>
        format is StreamFormat.HlsTs or StreamFormat.HlsFmp4;

    private static string FormatName(StreamFormat format) => format switch
    {
        StreamFormat.FlvHttp => "flv",
        StreamFormat.HlsTs => "ts",
        StreamFormat.HlsFmp4 => "fmp4",
        StreamFormat.Rtmp => "rtmp",
        _ => "unknown",
    };
}
