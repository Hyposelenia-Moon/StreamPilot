namespace StreamPilot.Tests.Cases;

using StreamPilot.App.Services;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Services;
using StreamPilot.Tests.Framework;

/// <summary>
/// 播放编排测试：候选筛选、中继回退与"没有可播线路"的提示。
/// </summary>
/// <remarks>
/// 桥接同时承担内置播放器的中继与 mpv 外挂播放。它没起来时页面源是 https，
/// 直连 http CDN 会被混合内容拦截；过去这种候选仍会被下发给页面，
/// 用户看到的是"所有线路都不可用"，而日志与提示都指向 mpv，误导排查方向。
/// </remarks>
[TestClass]
public sealed class PlaybackCoordinatorTests
{
    /// <summary>桥接未启动时，http 候选必须被剔除，提示必须指向桥接。</summary>
    [TestMethod("播放编排：桥接未启动时跳过 http 候选")]
    public async Task SkipsHttpCandidatesWhenBridgeUnavailable()
    {
        PlaybackCoordinator coordinator = Create(new StubBridge(running: false));
        PlaybackPlan plan = await coordinator.PrepareAsync(
            new PlaybackRequest
            {
                Room = BuildRoom(Candidate("http://cdn.example.com/live/stream.flv")),
                Mode = PlaybackMode.Stable,
            },
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(0, plan.Candidates.Count, "http 流没有中继就没有可播路径，不能算作可用候选");
        Assert.NotNull(plan.Hint);
        Assert.True(
            plan.Hint!.Contains("桥接", StringComparison.Ordinal),
            "提示必须说清真正的原因是桥接未启动，而不是只提 mpv");
    }

    /// <summary>桥接未启动时，https 候选仍可直连下发。</summary>
    [TestMethod("播放编排：桥接未启动时 https 候选仍可直连")]
    public async Task KeepsHttpsCandidatesWithoutBridge()
    {
        PlaybackCoordinator coordinator = Create(new StubBridge(running: false));
        const string Url = "https://cdn.example.com/live/stream.flv";
        PlaybackPlan plan = await coordinator.PrepareAsync(
            new PlaybackRequest
            {
                Room = BuildRoom(Candidate(Url)),
                Mode = PlaybackMode.Stable,
            },
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(1, plan.Candidates.Count, "https 候选不依赖中继，必须保留");
        Assert.Equal(Url, plan.Candidates[0].Url);
    }

    /// <summary>桥接运行时 http 候选走本地中继。</summary>
    [TestMethod("播放编排：桥接运行时 http 候选走中继")]
    public async Task RelaysHttpCandidatesWhenBridgeRunning()
    {
        StubBridge bridge = new(running: true);
        PlaybackCoordinator coordinator = Create(bridge);
        PlaybackPlan plan = await coordinator.PrepareAsync(
            new PlaybackRequest
            {
                Room = BuildRoom(Candidate("http://cdn.example.com/live/stream.flv")),
                Mode = PlaybackMode.Stable,
            },
            CancellationToken.None).ConfigureAwait(false);

        Assert.Equal(1, plan.Candidates.Count);
        Assert.Equal(1, bridge.RegisterCount, "http 候选必须注册中继");
        Assert.True(
            plan.Candidates[0].Url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal),
            "下发给页面的必须是本地中继地址");
    }

    /// <summary>构造被测对象。</summary>
    /// <param name="bridge">桥接替身。</param>
    /// <returns>播放编排器。</returns>
    private static PlaybackCoordinator Create(IPlaybackBridge bridge) => new(
        bridge,
        new CandidateValidity(NullStructuredLogger.Instance),
        static () => new StreamPilotOptions(),
        NullStructuredLogger.Instance);

    /// <summary>构造房间。</summary>
    /// <param name="candidates">候选。</param>
    /// <returns>房间。</returns>
    private static ResolvedRoom BuildRoom(params StreamCandidate[] candidates) => new()
    {
        Platform = PlatformId.Bilibili,
        RoomId = "1",
        Anchor = "probe",
        Title = "probe",
        Candidates = candidates,
        ResolvedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>构造候选。</summary>
    /// <param name="url">流地址。</param>
    /// <returns>候选。</returns>
    private static StreamCandidate Candidate(string url) => new()
    {
        SourceIndex = 0,
        Url = url,
        Format = StreamFormat.FlvHttp,
        CdnHost = "cdn.example.com",
        Codec = VideoCodec.Avc,
        Quality = StreamQuality.Hd1080,
        UrlFingerprint = "probe",
    };

    /// <summary>桥接替身。</summary>
    private sealed class StubBridge : IPlaybackBridge
    {
        private int _registerCount;

        /// <summary>初始化替身。</summary>
        /// <param name="running">是否模拟"正在监听"。</param>
        public StubBridge(bool running) => IsRunning = running;

        /// <summary>注册中继的次数。</summary>
        public int RegisterCount => _registerCount;

        /// <inheritdoc />
        public bool IsRunning { get; }

        /// <inheritdoc />
        public int Port => IsRunning ? 5566 : 0;

        /// <inheritdoc />
        public string BaseAddress => IsRunning ? "http://127.0.0.1:5566" : string.Empty;

        /// <inheritdoc />
        public string RegisterRelay(RelayTarget target)
        {
            Assert.True(IsRunning, "桥接未运行时不应被调用");
            _registerCount++;
            return "http://127.0.0.1:5566/relay/token-" + _registerCount;
        }

        /// <inheritdoc />
        public void ReleaseRelay(string localUrl)
        {
            // 替身不维护注册表。
        }

        /// <inheritdoc />
        public Task<bool> PlayWithMpvAsync(string url, string? title, string? referer, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
