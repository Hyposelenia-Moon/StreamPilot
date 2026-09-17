namespace StreamPilot.Tests.Cases;

using System.Text.Json;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Errors;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Parsers;
using StreamPilot.Parsers.Bigo;
using StreamPilot.Parsers.Bilibili;
using StreamPilot.Parsers.Yy;
using StreamPilot.Tests.Framework;

/// <summary>
/// 平台解析器中"纯函数式"的判定逻辑测试：不发起任何网络请求。
/// </summary>
/// <remarks>
/// 覆盖三处曾把可用房间误判为失败的逻辑：B站风控码识别、YY 房间页字段抽取、Bigo 开播判定顺序。
/// </remarks>
[TestClass]
public sealed class PlatformParserTests
{
    /// <summary>B站风控码必须与"房间不存在"区分开。</summary>
    [TestMethod("B站：风控错误码不当作房间不存在")]
    public void BilibiliRecognizesRiskControlCodes()
    {
        Assert.True(BilibiliParser.IsRiskControlCode(-352), "code=-352");
        Assert.True(BilibiliParser.IsRiskControlCode(-412), "code=-412");
        Assert.True(BilibiliParser.IsRiskControlCode(-509), "code=-509");
        Assert.False(BilibiliParser.IsRiskControlCode(-400), "code=-400 是房间不存在");
        Assert.False(BilibiliParser.IsRiskControlCode(0), "code=0 是成功");
    }

    /// <summary>YY 房间页：抽取主播名、标题与真实流标识。</summary>
    [TestMethod("YY：房间页抽取主播名、标题与 sid")]
    public void YyExtractsPageInfo()
    {
        const string Html = """
            <script>
            var pageInfo = {
                encode: 'false',
                uid : "480757017",
                nick: "热浪大魔王",
                sid : "95866468",
                ssid : "95866468",
                shortSid : "168",
                bizType: '0',
                biz: '',
                roomName: decodeURIComponent("%e5%a4%9c%e9%97%a8%e5%a4%a9%e4%b8%8b")
            };
            </script>
            """;

        YyParser.YyRoomPage page = CreateYyParser().ParseRoomPage(Html, "168");

        Assert.Equal("热浪大魔王", page.Anchor);
        Assert.Equal("夜门天下", page.Title);
        Assert.Equal("95866468", page.StreamId, "短号必须换回真实 sid");
    }

    /// <summary>YY 房间页：页面没有 sid 时回退用输入的房间号。</summary>
    [TestMethod("YY：缺少 sid 时回退输入房间号")]
    public void YyFallsBackToInputRoomId()
    {
        const string Html = """
            var pageInfo = {
                nick: "测试主播",
                roomName: decodeURIComponent("%E6%B5%8B%E8%AF%95"),
                biz: 'game'
            };
            """;

        YyParser.YyRoomPage page = CreateYyParser().ParseRoomPage(Html, "12345678");

        Assert.Equal("测试主播", page.Anchor);
        Assert.Equal("测试", page.Title);
        Assert.Equal("game", page.Category);
        Assert.Equal("12345678", page.StreamId);
    }

    /// <summary>YY 落在 404 页时必须判定为房间不存在。</summary>
    [TestMethod("YY：404 页面判定为房间不存在")]
    public void YyMissingPageInfoMeansRoomNotFound()
    {
        const string Html = "<html><body><script src=\"//x/yycom_404/pc/js/index.js\"></script></body></html>";

        ResolveException exception = Assert.Throws<ResolveException>(
            () => CreateYyParser().ParseRoomPage(Html, "1350510393"));

        Assert.Equal(ResolveFailure.RoomNotFound, exception.Failure);
    }

    /// <summary>Bigo：匿名响应里 roomStatus 恒为 0，有地址就必须按开播处理。</summary>
    [TestMethod("Bigo：有 HLS 地址即视为开播")]
    public void BigoAcceptsStreamDespiteOfflineStatus()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "hls_src": "https://cdn.example/live/index.m3u8", "roomStatus": 0,
              "roomTopic": "标题", "nick_name": "主播", "needLogin": false }
            """);

        StreamCandidateBuilder builder = new(PlatformId.Bigo);
        CreateBigoParser().CollectCandidates(builder, document.RootElement, out string title, out string anchor);

        Assert.Equal("标题", title);
        Assert.Equal("主播", anchor);
        Assert.Equal(1, builder.Count);
        Assert.Equal(StreamFormat.HlsTs, builder.Build()[0].Format);
    }

    /// <summary>Bigo：接口要求登录时归类为被拒绝，而不是未开播。</summary>
    [TestMethod("Bigo：需要登录时归类为被拒绝")]
    public void BigoReportsLoginRequiredAsRejected()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "hls_src": "", "roomStatus": 0, "roomTopic": "", "nick_name": "", "needLogin": true }
            """);

        StreamCandidateBuilder builder = new(PlatformId.Bigo);
        ResolveException exception = Assert.Throws<ResolveException>(
            () => CreateBigoParser().CollectCandidates(builder, document.RootElement, out _, out _));

        Assert.Equal(ResolveFailure.Rejected, exception.Failure);
    }

    /// <summary>Bigo：既没有地址也不需要登录时按未开播处理。</summary>
    [TestMethod("Bigo：无地址且无需登录按未开播")]
    public void BigoWithoutStreamIsNotLive()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "hls_src": "", "roomStatus": 0, "roomTopic": "", "nick_name": "", "needLogin": false }
            """);

        StreamCandidateBuilder builder = new(PlatformId.Bigo);
        ResolveException exception = Assert.Throws<ResolveException>(
            () => CreateBigoParser().CollectCandidates(builder, document.RootElement, out _, out _));

        Assert.Equal(ResolveFailure.NotLive, exception.Failure);
    }

    private static YyParser CreateYyParser() =>
        new(new HttpTextClient(new HttpClientFactory(new NetworkOptions(), NullStructuredLogger.Instance), NullStructuredLogger.Instance),
            NullStructuredLogger.Instance);

    private static BigoParser CreateBigoParser() =>
        new(new HttpTextClient(new HttpClientFactory(new NetworkOptions(), NullStructuredLogger.Instance), NullStructuredLogger.Instance),
            NullStructuredLogger.Instance);
}
