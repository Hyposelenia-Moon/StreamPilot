namespace StreamPilot.Tests.Cases;

using StreamPilot.Bridge;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Errors;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Services;
using StreamPilot.Tests.Framework;

/// <summary>
/// 桥接服务的安全边界测试（只监听回环地址）与中继注册表测试。
/// </summary>
[TestClass]
public sealed class BridgeTests
{
    /// <summary>回环地址校验：合法前缀通过。</summary>
    [TestMethod("桥接守卫：合法回环前缀通过")]
    public void AcceptsLoopbackPrefix()
    {
        LoopbackOnlyGuard.EnsureLoopback("http://127.0.0.1:5566/");
        LoopbackOnlyGuard.EnsureLoopback("http://localhost:5566/");
        Assert.Equal("http://127.0.0.1:5566/", LoopbackOnlyGuard.BuildPrefix(5566));
    }

    /// <summary>回环地址校验：通配与其他主机被拒绝。</summary>
    [TestMethod("桥接守卫：通配前缀被拒绝")]
    public void RejectsWildcardPrefixes()
    {
        string[] forbidden =
        [
            "http://+:5566/",
            "http://*:5566/",
            "http://0.0.0.0:5566/",
            "http://[::]:5566/",
            "http://192.168.1.10:5566/",
            "http://example.com:5566/",
        ];

        foreach (string prefix in forbidden)
        {
            Assert.Throws<BridgeException>(() => LoopbackOnlyGuard.EnsureLoopback(prefix), "前缀：" + prefix);
        }
    }

    /// <summary>端口校验：越界端口被拒绝。</summary>
    [TestMethod("桥接守卫：端口越界被拒绝")]
    public void RejectsInvalidPort()
    {
        Assert.Throws<BridgeException>(() => LoopbackOnlyGuard.BuildPrefix(80));
        Assert.Throws<BridgeException>(() => LoopbackOnlyGuard.BuildPrefix(70000));
    }

    /// <summary>中继注册、解析、释放的完整流程。</summary>
    [TestMethod("中继注册表：注册 / 解析 / 释放")]
    public void RegistersAndResolvesRelay()
    {
        RelayRegistry registry = new(NullStructuredLogger.Instance);
        RelayTarget target = new()
        {
            UpstreamUrl = "https://cdn.example/live.flv?sign=secret",
            Referer = "https://live.bilibili.com/",
        };

        string token = registry.Register(target);
        Assert.True(token.Length > 0, "令牌不应为空");
        Assert.Equal(1, registry.Count);

        Assert.True(registry.TryResolve(token, out RelayTarget? resolved));
        Assert.NotNull(resolved);
        Assert.Equal(target.UpstreamUrl, resolved!.UpstreamUrl);
        Assert.Equal(target.Referer, resolved.Referer);

        Assert.True(registry.Release(token));
        Assert.False(registry.TryResolve(token, out _));
        Assert.Equal(0, registry.Count);
    }

    /// <summary>空闲超过阈值的中继注册会被淘汰（基准行为）。</summary>
    [TestMethod("中继注册表：空闲超时后淘汰")]
    public async Task EvictsIdleRelay()
    {
        RelayRegistry registry = new(NullStructuredLogger.Instance, TimeSpan.FromMilliseconds(150));
        string token = registry.Register(new RelayTarget { UpstreamUrl = "https://cdn.example/a.flv" });

        await Task.Delay(250).ConfigureAwait(false);
        // 任何一次注册都会顺带触发一次淘汰扫描。
        _ = registry.Register(new RelayTarget { UpstreamUrl = "https://cdn.example/b.flv" });

        Assert.False(registry.TryResolve(token, out _), "空闲超过阈值的中继必须被淘汰");
    }

    /// <summary>转发期间持续续命的中继不会因为"没有新的 HTTP 请求"被淘汰。</summary>
    /// <remarks>
    /// FLV 只在建连时请求一次 <c>/relay/{token}</c>，之后几小时都没有新请求；
    /// 中继泵每 30 秒调用一次 <see cref="RelayRegistry.Touch"/>，注册必须一直有效。
    /// </remarks>
    [TestMethod("中继注册表：转发期间续命不会被淘汰")]
    public async Task TouchKeepsRelayAlive()
    {
        RelayRegistry registry = new(NullStructuredLogger.Instance, TimeSpan.FromMilliseconds(200));
        string token = registry.Register(new RelayTarget { UpstreamUrl = "https://cdn.example/a.flv" });

        // 总时长超过阈值，但每次间隔都小于阈值。
        for (int index = 0; index < 4; index++)
        {
            await Task.Delay(80).ConfigureAwait(false);
            registry.Touch(token);
        }

        _ = registry.Register(new RelayTarget { UpstreamUrl = "https://cdn.example/b.flv" });
        Assert.True(registry.TryResolve(token, out RelayTarget? alive), "持续续命的长连接注册必须保留");
        Assert.NotNull(alive);

        // 停止续命后仍会被淘汰（续命不是永久豁免）。
        await Task.Delay(350).ConfigureAwait(false);
        _ = registry.Register(new RelayTarget { UpstreamUrl = "https://cdn.example/c.flv" });
        Assert.False(registry.TryResolve(token, out _), "停止续命后必须恢复正常的空闲淘汰");
    }

    /// <summary>播放列表改写出的子注册若从未被请求，试用期过后必须被淘汰。</summary>
    /// <remarks>
    /// 播放页探测 HLS 候选会拉一次播放列表（拿到响应头就 abort），改写过程登记的切片子地址
    /// 不会再被请求。若按普通空闲阈值（10 分钟）淘汰，一次起播就会堆几十条死注册，
    /// 逼近上限时还可能把正在播放的注册挤掉。
    /// </remarks>
    [TestMethod("中继注册表：未被请求的子注册按试用期淘汰")]
    public async Task EvictsUnusedProvisionalChild()
    {
        RelayRegistry registry = new(
            NullStructuredLogger.Instance,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMilliseconds(120));
        string child = registry.Register(new RelayTarget { UpstreamUrl = "https://cdn.example/seg-1.ts" }, provisional: true);

        await Task.Delay(200).ConfigureAwait(false);
        // 任何一次注册都会顺带触发淘汰扫描。
        _ = registry.Register(new RelayTarget { UpstreamUrl = "https://cdn.example/next.flv" });

        Assert.False(registry.TryResolve(child, out _), "从未被请求过的子注册必须在试用期后消失");
    }

    /// <summary>被真实请求过的子注册立即转正，不再受试用期约束。</summary>
    [TestMethod("中继注册表：子注册被请求后转正")]
    public async Task PromotesProvisionalChildOnFirstUse()
    {
        RelayRegistry registry = new(
            NullStructuredLogger.Instance,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMilliseconds(120));
        string child = registry.Register(new RelayTarget { UpstreamUrl = "https://cdn.example/seg-1.ts" }, provisional: true);

        Assert.True(registry.TryResolve(child, out _), "首次请求必须能取到目标");

        await Task.Delay(200).ConfigureAwait(false);
        _ = registry.Register(new RelayTarget { UpstreamUrl = "https://cdn.example/next.flv" });

        Assert.True(registry.TryResolve(child, out _), "被请求过的子注册必须转正，按普通空闲规则保留");
    }

    /// <summary>达到上限时优先淘汰试用期子注册，而不是正在使用的注册。</summary>
    [TestMethod("中继注册表：满额时优先淘汰试用期子注册")]
    public void EvictsProvisionalBeforeActiveWhenFull()
    {
        RelayRegistry registry = new(NullStructuredLogger.Instance);
        string active = registry.Register(new RelayTarget { UpstreamUrl = "https://cdn.example/live.flv" });

        // 填满上限：其中前面那些都是试用期子注册，最后一条是"刚登记的正式注册"。
        for (int index = 0; index < BridgeConstants.MaxRelayRegistrations - 1; index++)
        {
            _ = registry.Register(
                new RelayTarget { UpstreamUrl = "https://cdn.example/seg-" + index + ".ts" },
                provisional: true);
        }

        _ = registry.Register(new RelayTarget { UpstreamUrl = "https://cdn.example/new.flv" });

        Assert.True(registry.TryResolve(active, out _), "正在使用的注册不能被试用期子注册挤出");
    }

    /// <summary>宽限期内的旧一代切片地址不能被回收：慢链路可能还在下载。</summary>
    [TestMethod("播放列表代际：宽限期内保留旧一代")]
    public void KeepsRecentGenerations()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<BridgeHost.PlaylistGeneration> generations =
        [
            new(now, ["/relay/new-1", "/relay/new-2"]),
            new(now.AddSeconds(-5), ["/relay/mid-1"]),
            new(now.AddSeconds(-20), ["/relay/old-1"]),
        ];

        List<string> expired = BridgeHost.TrimGenerations(generations, now);

        Assert.Equal(0, expired.Count, "60 秒内的旧一代必须保留（切片可能仍在下载）");
        Assert.Equal(3, generations.Count);
    }

    /// <summary>超过宽限期的旧一代可以回收，但至少保留两代。</summary>
    [TestMethod("播放列表代际：超过宽限期回收旧一代")]
    public void ReleasesAgedGenerations()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<BridgeHost.PlaylistGeneration> generations =
        [
            new(now, ["/relay/new-1"]),
            new(now.AddSeconds(-30), ["/relay/mid-1"]),
            new(now.AddSeconds(-120), ["/relay/old-1"]),
        ];

        List<string> expired = BridgeHost.TrimGenerations(generations, now);

        Assert.Equal(1, expired.Count, "超过宽限期的旧一代必须被回收");
        Assert.Equal("/relay/old-1", expired[0]);
        Assert.Equal(2, generations.Count, "至少保留两代");
    }

    /// <summary>刷新过密时按硬上限回收，避免注册表无限增长。</summary>
    [TestMethod("播放列表代际：代数上限强制回收")]
    public void CapsGenerationCount()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<BridgeHost.PlaylistGeneration> generations = [];
        for (int index = 0; index < 12; index++)
        {
            generations.Add(new BridgeHost.PlaylistGeneration(now, ["/relay/" + index]));
        }

        List<string> expired = BridgeHost.TrimGenerations(generations, now);

        Assert.Equal(8, generations.Count, "必须裁到硬上限");
        Assert.Equal(4, expired.Count, "被裁掉的代必须交回调用方释放");
        Assert.Equal("/relay/11", expired[0], "从最旧的一代开始回收");
    }

    /// <summary>播放列表嗅探：按内容识别，不依赖 .m3u8 后缀。</summary>
    /// <remarks>
    /// 部分平台给的变体列表地址是 <c>.../playlist/abc123?token=...</c>（没有扩展名）。
    /// 只按后缀判定会把它们当二进制流转发，内部相对切片地址不会被改写，
    /// hls.js 随后拿到 CDN 原始 http 地址被混合内容拦截。
    /// </remarks>
    [TestMethod("中继：按内容识别播放列表")]
    public void DetectsPlaylistByContent()
    {
        Assert.True(BridgeHost.LooksLikePlaylist("#EXTM3U\n#EXT-X-VERSION:3\n"u8), "标准 m3u8 必须被识别");
        Assert.True(BridgeHost.LooksLikePlaylist("#EXTM3U"u8), "只有首行也必须被识别");
        Assert.True(
            BridgeHost.LooksLikePlaylist([0xEF, 0xBB, 0xBF, .. "#EXTM3U\n"u8.ToArray()]),
            "带 UTF-8 BOM 的播放列表必须被识别");
        Assert.True(BridgeHost.LooksLikePlaylist("\r\n  \t#EXTM3U\n"u8), "前导空白必须被跳过");
    }

    /// <summary>播放列表嗅探：普通媒体与错误页面不能被误判成播放列表。</summary>
    [TestMethod("中继：普通媒体不被误判为播放列表")]
    public void DoesNotMistakeMediaForPlaylist()
    {
        byte[] tsPacket = new byte[188];
        tsPacket[0] = 0x47;
        Assert.False(BridgeHost.LooksLikePlaylist(tsPacket), "TS 包不能被当成播放列表");

        Assert.False(BridgeHost.LooksLikePlaylist("FLV\u0001\u0005\u0000\u0000\u0000\u0009"u8), "FLV 头不能被当成播放列表");
        Assert.False(BridgeHost.LooksLikePlaylist("<!DOCTYPE html><html>"u8), "HTML 错误页不能被当成播放列表");
        Assert.False(BridgeHost.LooksLikePlaylist("  #EXT"u8), "只读到半个魔数时不能误判");
        Assert.False(BridgeHost.LooksLikePlaylist(ReadOnlySpan<byte>.Empty), "空响应不能被当成播放列表");
    }

    /// <summary>未知令牌返回失败而不是抛异常。</summary>
    [TestMethod("中继注册表：未知令牌")]
    public void UnknownTokenFails()
    {
        RelayRegistry registry = new(NullStructuredLogger.Instance);
        Assert.False(registry.TryResolve("not-a-token", out RelayTarget? target));
        Assert.Null(target);
        Assert.False(registry.Release("not-a-token"));
    }

    /// <summary>数量上限：超出后淘汰最旧注册。</summary>
    [TestMethod("中继注册表：数量上限淘汰最旧")]
    public void EnforcesCapacity()
    {
        RelayRegistry registry = new(NullStructuredLogger.Instance);
        string firstToken = string.Empty;

        for (int index = 0; index <= BridgeConstants.MaxRelayRegistrations; index++)
        {
            string token = registry.Register(new RelayTarget { UpstreamUrl = "https://cdn.example/" + index + ".flv" });
            if (index == 0)
            {
                firstToken = token;
            }
        }

        Assert.Equal(BridgeConstants.MaxRelayRegistrations, registry.Count);
        Assert.False(registry.TryResolve(firstToken, out _), "最早的注册应被淘汰");
    }

    /// <summary>通过本地地址反解并释放令牌。</summary>
    [TestMethod("中继注册表：按本地地址释放")]
    public void ReleasesByLocalUrl()
    {
        RelayRegistry registry = new(NullStructuredLogger.Instance);
        string token = registry.Register(new RelayTarget { UpstreamUrl = "https://cdn.example/x.flv" });

        registry.ReleaseByLocalUrl($"http://127.0.0.1:5566/relay/{token}");
        Assert.Equal(0, registry.Count);

        // 非法地址不抛异常。
        registry.ReleaseByLocalUrl("not-a-url");
        registry.ReleaseByLocalUrl(string.Empty);
    }

    /// <summary>未启动时注册中继应抛出桥接异常。</summary>
    [TestMethod("桥接服务：未启动时注册中继失败")]
    public async Task RegisterRelayRequiresRunningHost()
    {
        await using BridgeHost host = new(new BridgeOptions(), () => null, NullStructuredLogger.Instance);
        Assert.False(host.IsRunning);
        Assert.Equal(string.Empty, host.BaseAddress);

        BridgeException exception = Assert.Throws<BridgeException>(() =>
            host.RegisterRelay(new RelayTarget { UpstreamUrl = "https://cdn.example/x.flv" }));
        Assert.Contains("未启动", exception.Message);
    }

    /// <summary>
    /// 中继上游请求必须携带浏览器 User-Agent。
    /// </summary>
    /// <remarks>
    /// 回归点（见 docs/adr/0006）：<see cref="HttpClient"/> 默认不发送 UA，
    /// 而 B站 CDN 的部分节点对不带 UA 的请求一律返回 403，
    /// 于是中继的每条线路都 403，"全部候选线路不可用"（room_id=814）。
    /// 这里锁住"至少带 UA"这一条不变量，UA 具体取值只允许等于项目统一的默认 UA。
    /// </remarks>
    [TestMethod("中继：上游请求必须携带 User-Agent")]
    public void UpstreamRequestAlwaysCarriesUserAgent()
    {
        using HttpRequestMessage withReferer = BridgeHost.CreateUpstreamRequest(
            new RelayTarget
            {
                UpstreamUrl = "https://d1--cn-gotcha104.bilivideo.com/live-bvc/x.m3u8?expires=1&sign=secret",
                Referer = "https://live.bilibili.com/",
            },
            "bytes=0-1");

        Assert.Equal(HttpMethod.Get, withReferer.Method);
        Assert.True(
            withReferer.Headers.TryGetValues("User-Agent", out IEnumerable<string>? agentValues),
            "上游请求必须带 User-Agent");
        // 读回时 .NET 会把 User-Agent 按 RFC 拆成若干段（空格分隔），因此按空格拼回后必须与写入值逐字节相同：
        // 这既证明头存在，也证明没有被截断、重复或改写。
        Assert.Equal(
            HttpClientFactory.DefaultUserAgent,
            string.Join(" ", agentValues!),
            "上游请求的 User-Agent 必须与项目默认 UA 逐字节一致");
        Assert.True(withReferer.Headers.Contains("Referer"), "设置了 Referer 时必须带 Referer");
        Assert.True(withReferer.Headers.Contains("Range"), "客户端带了 Range 时必须透传");

        using HttpRequestMessage bare = BridgeHost.CreateUpstreamRequest(
            new RelayTarget { UpstreamUrl = "https://cdn.example/x.flv" },
            null);

        Assert.True(
            bare.Headers.Contains("User-Agent"),
            "没有 Referer / Range 时同样必须带 UA，否则 B站 CDN 会回 403");
        Assert.False(bare.Headers.Contains("Referer"));
        Assert.False(bare.Headers.Contains("Range"));
    }
}
