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
