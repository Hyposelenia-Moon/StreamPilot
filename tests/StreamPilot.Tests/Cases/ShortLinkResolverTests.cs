using System.Net;
using StreamPilot.App.Services;
using StreamPilot.Tests.Framework;

namespace StreamPilot.Tests.Cases;

/// <summary>
/// 分享短链解析的用例：主机名精确匹配、跟随跳转拿到最终地址、失败时原样降级。
/// </summary>
/// <remarks>
/// 回归背景：短链（<c>v.douyin.com</c> / <c>b23.tv</c>）里没有房间号，必须先跳到真实房间地址
/// 再解析，否则只会得到"房间不存在"；而把短链域名直接加进白名单会更糟。
/// </remarks>
[TestClass]
public sealed class ShortLinkResolverTests
{
    /// <summary>短链主机名必须精确匹配，子域或仿冒域不算。</summary>
    [TestMethod("短链：只认 v.douyin.com 与 b23.tv 精确主机名")]
    public void DetectsShortLinkHosts()
    {
        Assert.True(ShortLinkResolver.IsShortLink("https://v.douyin.com/iRNBabc123/"));
        Assert.True(ShortLinkResolver.IsShortLink("https://b23.tv/AbCdEf"));
        Assert.True(ShortLinkResolver.IsShortLink("HTTPS://B23.TV/AbCdEf"), "主机名大小写不敏感");

        Assert.False(ShortLinkResolver.IsShortLink("https://live.douyin.com/123456"), "直播页不是短链");
        Assert.False(ShortLinkResolver.IsShortLink("https://b23.tv.evil.example/x"), "仿冒子域不算短链");
        Assert.False(ShortLinkResolver.IsShortLink("https://evil.example/b23.tv"), "路径里出现短链域名不算");
        Assert.False(ShortLinkResolver.IsShortLink("b23.tv/AbCdEf"), "缺 scheme 时由规范化层负责，不在这里猜");
        Assert.False(ShortLinkResolver.IsShortLink(null));
        Assert.False(ShortLinkResolver.IsShortLink("   "));
        Assert.False(ShortLinkResolver.IsShortLink("814"));
    }

    /// <summary>非短链输入必须原样返回，且不发起任何 HTTP 请求。</summary>
    [TestMethod("短链：非短链输入不发起请求")]
    public async Task SkipsHttpForNonShortLink()
    {
        RedirectStub stub = new("https://live.douyin.com/123456");
        using ShortLinkResolver resolver = new(null, stub);

        string result = await resolver.ResolveAsync("https://live.douyin.com/123456", CancellationToken.None);

        Assert.Equal("https://live.douyin.com/123456", result);
        Assert.Equal(0, stub.CallCount, "非短链不应产生网络请求");
    }

    /// <summary>短链跳转成功后返回最终绝对地址。</summary>
    [TestMethod("短链：跳转后返回最终房间地址")]
    public async Task ResolvesToFinalUrl()
    {
        RedirectStub stub = new("https://live.douyin.com/745964462470");
        using ShortLinkResolver resolver = new(null, stub);

        string result = await resolver.ResolveAsync("https://v.douyin.com/iRNBabc123/", CancellationToken.None);

        Assert.Equal("https://live.douyin.com/745964462470", result);
        Assert.Equal(1, stub.CallCount);
    }

    /// <summary>跳转失败（网络异常）时必须原样返回输入，绝不抛给界面。</summary>
    [TestMethod("短链：跳转失败时原样降级")]
    public async Task FallsBackOnHttpFailure()
    {
        RedirectStub stub = new(null, new HttpRequestException("连接被重置"));
        using ShortLinkResolver resolver = new(null, stub);

        string result = await resolver.ResolveAsync("https://b23.tv/AbCdEf", CancellationToken.None);

        Assert.Equal("https://b23.tv/AbCdEf", result);
        Assert.Equal(1, stub.CallCount);
    }

    /// <summary>没有最终地址（响应缺少 RequestMessage）时同样原样降级。</summary>
    [TestMethod("短链：拿不到最终地址时原样降级")]
    public async Task FallsBackWithoutFinalUri()
    {
        RedirectStub stub = new(null);
        using ShortLinkResolver resolver = new(null, stub);

        string result = await resolver.ResolveAsync("https://b23.tv/AbCdEf", CancellationToken.None);

        Assert.Equal("https://b23.tv/AbCdEf", result);
    }
}

/// <summary>
/// 模拟"短链跟随跳转后的响应"：把最终地址塞进 <see cref="HttpResponseMessage.RequestMessage"/>，
/// 与真实 <see cref="HttpClient"/> 在跟随跳转后提供的形态一致，从而无需联网即可断言解析结果。
/// </summary>
internal sealed class RedirectStub : HttpMessageHandler
{
    private readonly string? _finalUrl;
    private readonly Exception? _error;

    /// <summary>构造替身。</summary>
    /// <param name="finalUrl">最终地址；为 <see langword="null"/> 时模拟"没有最终地址"。</param>
    /// <param name="error">需要抛出的异常，可为 <see langword="null"/>。</param>
    public RedirectStub(string? finalUrl, Exception? error = null)
    {
        _finalUrl = finalUrl;
        _error = error;
    }

    /// <summary>被调用的次数。</summary>
    public int CallCount { get; private set; }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount += 1;
        if (_error is not null)
        {
            throw _error;
        }

        HttpResponseMessage response = new(HttpStatusCode.OK);
        if (_finalUrl is not null)
        {
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, _finalUrl);
        }

        return Task.FromResult(response);
    }
}
