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
using StreamPilot.Parsers.Douyin;
using StreamPilot.Parsers.Huya;
using StreamPilot.Parsers.Douyu;
using StreamPilot.Parsers.Yy;
using StreamPilot.Tests.Framework;

/// <summary>
/// 平台解析器中"纯函数式"的判定逻辑测试：不发起任何网络请求。
/// </summary>
/// <remarks>
/// 覆盖曾把可用房间误判为失败的逻辑（B站风控码识别、YY 房间页字段抽取、Bigo 开播判定顺序），
/// 以及各平台的画质档位构造与档位回退规则。
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

    /// <summary>抖音：档位声明优先，码率换算为 kbps，未指定档位时取最高档。</summary>
    [TestMethod("抖音：按 options.qualities 构造档位并取最高档")]
    public void DouyinBuildsQualitiesFromDeclaredOptions()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "stream_url": {
                "live_core_sdk_data": { "pull_data": { "options": { "qualities": [
                  { "sdk_key": "origin", "name": "原画", "v_bit_rate": 8000000 },
                  { "sdk_key": "hd", "name": "超清", "v_bit_rate": 4000000 },
                  { "sdk_key": "ld", "name": "标清", "v_bit_rate": 1000000 } ] } } },
                "flv_pull_url": {
                  "origin": "https://cdn.example/origin.flv",
                  "hd": "https://cdn.example/hd.flv",
                  "ld": "https://cdn.example/ld.flv" } } }
            """);

        DouyinParser parser = CreateDouyinParser();
        (IReadOnlyList<QualityOption> qualities, string? selectedKey) =
            parser.BuildQualityOptions(document.RootElement, preferredQualityKey: null);

        Assert.Equal(3, qualities.Count, "三个档位都能找到地址");
        Assert.Equal("origin", qualities[0].Key);
        Assert.Equal("原画", qualities[0].Label);
        Assert.Equal(8000, qualities[0].BitrateKbps);
        Assert.True(qualities[0].IsBest, "第一项是最高档");
        Assert.Equal(4000, qualities[1].BitrateKbps);
        Assert.Equal("origin", selectedKey, "未指定档位时取最高档");

        (IReadOnlyList<QualityOption> picked, string? pickedKey) =
            parser.BuildQualityOptions(document.RootElement, "hd");

        Assert.Equal("hd", pickedKey, "指定档位必须生效");
        Assert.Equal(3, picked.Count, "档位列表不随选择变化");
    }

    /// <summary>抖音：老式键名排序、未知档位键回退最高档，且候选只保留选中档位的地址。</summary>
    [TestMethod("抖音：未知档位回退最高档且不混入其它档位")]
    public void DouyinFallsBackToBestQuality()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "stream_url": {
                "flv_pull_url": {
                  "FULL_HD1": "https://cdn.example/full.flv",
                  "HD1": "https://cdn.example/hd.flv" },
                "hls_pull_url_map": {
                  "FULL_HD1": "https://cdn.example/full.m3u8",
                  "HD1": "https://cdn.example/hd.m3u8" } } }
            """);

        DouyinParser parser = CreateDouyinParser();
        (IReadOnlyList<QualityOption> qualities, string? selectedKey) =
            parser.BuildQualityOptions(document.RootElement, "NOT_A_QUALITY");

        Assert.Equal(2, qualities.Count);
        Assert.Equal("FULL_HD1", qualities[0].Key, "老式键名按 FULL_HD1 优先");
        Assert.Equal("高清", qualities[0].Label);
        Assert.Equal("标清", qualities[1].Label);
        Assert.Equal("FULL_HD1", selectedKey, "未知档位键回退最高档");

        StreamCandidateBuilder builder = new(PlatformId.Douyin);
        bool added = parser.CollectCandidates(
            builder,
            document.RootElement,
            "room-state",
            "HD1",
            out _,
            out _);

        Assert.True(added, "选中的 HD1 有可用地址");
        Assert.Equal(2, builder.Count, "只加入选中档位的 FLV 与 HLS，不混入其它档位");
        Assert.Equal(StreamFormat.FlvHttp, builder.Build()[0].Format, "FLV 排在 HLS 之前");
        Assert.Equal(StreamFormat.HlsTs, builder.Build()[1].Format);
    }

    /// <summary>斗鱼：档位列表来自 multirates（原画最高），实际档位回读 data.rate。</summary>
    [TestMethod("斗鱼：multirates 构造档位并以 data.rate 回读")]
    public void DouyuBuildsQualitiesFromMultirates()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "rate": 0, "rateSwitch": 1, "multirates": [
                { "rate": 2, "name": "高清", "bitRate": 2000000 },
                { "rate": 0, "name": "原画", "bitRate": 8000000 },
                { "rate": 8, "name": "蓝光8M", "bitRate": 4000000 } ] }
            """);

        (IReadOnlyList<QualityOption> qualities, string? selectedKey) =
            DouyuParser.BuildQualityOptions(document.RootElement);

        Assert.Equal(3, qualities.Count);
        Assert.Equal("0", qualities[0].Key, "原画必须排第一");
        Assert.Equal("原画", qualities[0].Label);
        Assert.True(qualities[0].IsBest, "原画是最高档");
        Assert.Equal(8000, qualities[0].BitrateKbps);
        Assert.Equal("8", qualities[1].Key, "其余档位按 rate 数值从大到小");
        Assert.Equal("2", qualities[2].Key);
        Assert.Equal("0", selectedKey, "SelectedQualityKey 取响应里的 data.rate");
    }

    /// <summary>斗鱼：rateSwitch 非 1 只有原画一档，非法或越界档位键回退原画。</summary>
    [TestMethod("斗鱼：rateSwitch 非 1 与非法档位键都回退原画")]
    public void DouyuFallsBackToOriginalRate()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "rate": 0, "rateSwitch": 0,
              "multirates": [ { "rate": 2, "name": "高清", "bitRate": 2000000 } ] }
            """);

        (IReadOnlyList<QualityOption> qualities, string? selectedKey) =
            DouyuParser.BuildQualityOptions(document.RootElement);

        Assert.Equal(1, qualities.Count, "平台只给原画时只有一档");
        Assert.Equal("0", qualities[0].Key);
        Assert.Equal("原画", qualities[0].Label);
        Assert.Equal("0", selectedKey);

        DouyuParser parser = CreateDouyuParser();
        Assert.Equal(8, parser.ResolveRequestedRate("8"), "合法档位键原样传给接口");
        Assert.Equal(0, parser.ResolveRequestedRate("not-a-rate"), "非法档位键回退原画");
        Assert.Equal(0, parser.ResolveRequestedRate("999"), "越界档位键回退原画");
        Assert.Equal(0, parser.ResolveRequestedRate(null), "未指定档位时取最高档");
    }

    /// <summary>YY：gear 语义未证实，档位列表只放一项默认档。</summary>
    [TestMethod("YY：档位列表只有一项默认档")]
    public void YyExposesSingleDefaultQuality()
    {
        IReadOnlyList<QualityOption> qualities = YyParser.BuildQualityOptions(2);

        Assert.Equal(1, qualities.Count);
        Assert.Equal("2", qualities[0].Key);
        Assert.Equal("默认（平台给定）", qualities[0].Label);
        Assert.True(qualities[0].IsBest);
    }

    /// <summary>YY：非法或越界的 gear 必须回退到默认值 2。</summary>
    [TestMethod("YY：非法 gear 回退默认值")]
    public void YyFallsBackToDefaultGear()
    {
        YyParser parser = CreateYyParser();

        Assert.Equal(5, parser.ResolveGear("5"), "纯数字档位键原样透传");
        Assert.Equal(2, parser.ResolveGear(null), "未指定档位时用默认 gear");
        Assert.Equal(2, parser.ResolveGear("best"), "best 表示平台默认档");
        Assert.Equal(2, parser.ResolveGear("abc"), "非数字档位键回退默认 gear");
        Assert.Equal(2, parser.ResolveGear("999"), "越界档位键回退默认 gear");
    }

    /// <summary>Bigo：只有一路 HLS，档位列表固定为一项 default。</summary>
    [TestMethod("Bigo：档位列表固定为一项默认档")]
    public void BigoExposesSingleDefaultQuality()
    {
        (IReadOnlyList<QualityOption> qualities, string? selectedKey) =
            CreateBigoParser().BuildQualityOptions(preferredQualityKey: null);

        Assert.Equal(1, qualities.Count);
        Assert.Equal("default", qualities[0].Key);
        Assert.Equal("默认（平台自带 HLS）", qualities[0].Label);
        Assert.True(qualities[0].IsBest);
        Assert.Equal("default", selectedKey);
    }

    /// <summary>Bigo：调用方指定的档位键必须被忽略（平台没有档位可选）。</summary>
    [TestMethod("Bigo：忽略调用方指定的档位键")]
    public void BigoIgnoresPreferredQualityKey()
    {
        (IReadOnlyList<QualityOption> qualities, string? selectedKey) =
            CreateBigoParser().BuildQualityOptions("uhd");

        Assert.Equal(1, qualities.Count);
        Assert.Equal("default", qualities[0].Key);
        Assert.Equal("default", selectedKey);
    }

    /// <summary>虎牙：档位来自 liveData.bitRateInfo（JSON 字符串），名称与排序按平台声明。</summary>
    [TestMethod("虎牙：档位来自 bitRateInfo 并支持按档位选择")]
    public void HuyaBuildsQualitiesFromDeclaredBitRates()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "liveData": { "bitRateInfo": "[{\"sDisplayName\":\"蓝光10M\",\"iBitRate\":0},{\"sDisplayName\":\"蓝光4M\",\"iBitRate\":4000},{\"sDisplayName\":\"超清\",\"iBitRate\":2000}]" },
              "stream": { "flv": { "rateArray": [ { "sDisplayName": "流畅", "iBitRate": 500 } ] } } }
            """);

        (IReadOnlyList<QualityOption> qualities, int? selected) =
            HuyaParser.BuildQualityOptions(document.RootElement, preferredKey: null);

        Assert.Equal(3, qualities.Count);
        Assert.Equal("0", qualities[0].Key);
        Assert.Equal("蓝光10M", qualities[0].Label);
        Assert.True(qualities[0].IsBest);
        Assert.Equal("4000", qualities[1].Key);
        Assert.Equal(4000, qualities[1].BitrateKbps);
        Assert.Equal(0, selected);

        (IReadOnlyList<QualityOption> _, int? picked) =
            HuyaParser.BuildQualityOptions(document.RootElement, preferredKey: "2000");
        Assert.Equal(2000, picked);

        (IReadOnlyList<QualityOption> _, int? unknown) =
            HuyaParser.BuildQualityOptions(document.RootElement, preferredKey: "999");
        Assert.Equal(0, unknown);
    }

    /// <summary>虎牙：没有声明档位时退回 flv/hls 的 rateArray。</summary>
    [TestMethod("虎牙：无 bitRateInfo 时退回 rateArray")]
    public void HuyaFallsBackToRateArray()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "stream": { "hls": { "rateArray": [ { "sDisplayName": "蓝光4M", "iBitRate": 4000 }, { "sDisplayName": "流畅", "iBitRate": 500 } ] } } }
            """);

        (IReadOnlyList<QualityOption> qualities, int? selected) =
            HuyaParser.BuildQualityOptions(document.RootElement, preferredKey: null);

        Assert.Equal(2, qualities.Count);
        Assert.Equal("4000", qualities[0].Key);
        Assert.Equal("蓝光4M", qualities[0].Label);
        Assert.Equal(4000, selected);
    }

    private static YyParser CreateYyParser() =>
        new(new HttpTextClient(new HttpClientFactory(new NetworkOptions(), NullStructuredLogger.Instance), NullStructuredLogger.Instance),
            NullStructuredLogger.Instance);

    private static BigoParser CreateBigoParser() =>
        new(new HttpTextClient(new HttpClientFactory(new NetworkOptions(), NullStructuredLogger.Instance), NullStructuredLogger.Instance),
            NullStructuredLogger.Instance);

    /// <summary>创建一个不发起网络请求的抖音解析器（HTTP 客户端只在测试里被构造，不会被调用）。</summary>
    /// <returns>抖音解析器实例。</returns>
    private static DouyinParser CreateDouyinParser() =>
        new(new HttpTextClient(new HttpClientFactory(new NetworkOptions(), NullStructuredLogger.Instance), NullStructuredLogger.Instance),
            NullStructuredLogger.Instance);

    /// <summary>创建一个不发起网络请求的斗鱼解析器（HTTP 客户端只在测试里被构造，不会被调用）。</summary>
    /// <returns>斗鱼解析器实例。</returns>
    private static DouyuParser CreateDouyuParser() =>
        new(new HttpTextClient(new HttpClientFactory(new NetworkOptions(), NullStructuredLogger.Instance), NullStructuredLogger.Instance),
            NullStructuredLogger.Instance);

    /// <summary>
    /// 抖音：状态 JSON 被整体转义进 JS 字符串字面量（React Flight）时，
    /// 大括号配对扫描必须切出合法 JSON，并正确处理 <c>\"</c>、<c>\\uXXXX</c> 与值里的大括号。
    /// </summary>
    [TestMethod("抖音：转义进 JS 字符串的 roomStore 也能切出合法 JSON")]
    public void DouyinExtractsRoomStoreFromEscapedJavaScriptString()
    {
        const string Html = """<html><body><script>self.__pace_f.push([1,"{\"state\":{\"roomStore\":{\"roomInfo\":{\"anchor\":{\"nickname\":\"\\u4e2d\\u6587\"},\"room\":{\"title\":\"a{b}c\"}}}},\"children\":[]}"])</script></body></html>""";

        using JsonDocument? document = CreateDouyinParser().ExtractRoomStoreDocument(Html);

        Assert.NotNull(document, "转义形态的 roomStore 必须能切出 JSON");
        JsonElement roomInfo = document!.RootElement.GetProperty("roomStore").GetProperty("roomInfo");
        Assert.Equal("中文", roomInfo.GetProperty("anchor").GetProperty("nickname").GetString() ?? string.Empty);
        Assert.Equal("a{b}c", roomInfo.GetProperty("room").GetProperty("title").GetString() ?? string.Empty, "字符串值里的大括号不能影响配对");
    }

    /// <summary>抖音：RENDER_DATA 脚本块里的百分号编码 JSON 必须先解码再扫描。</summary>
    [TestMethod("抖音：RENDER_DATA 百分号编码 JSON 可解析")]
    public void DouyinExtractsRoomStoreFromPercentEncodedRenderData()
    {
        const string Html = """<html><body><script id="RENDER_DATA" type="application/json">%7B%22roomStore%22%3A%7B%22roomInfo%22%3A%7B%22anchor%22%3A%7B%22nickname%22%3A%22A%22%7D%2C%22room%22%3A%7B%22title%22%3A%22T%22%7D%7D%7D%7D</script></body></html>""";

        using JsonDocument? document = CreateDouyinParser().ExtractRoomStoreDocument(Html);

        Assert.NotNull(document, "解码后的 RENDER_DATA 必须能切出 JSON");
        JsonElement roomInfo = document!.RootElement.GetProperty("roomStore").GetProperty("roomInfo");
        Assert.Equal("A", roomInfo.GetProperty("anchor").GetProperty("nickname").GetString() ?? string.Empty);
        Assert.Equal("T", roomInfo.GetProperty("room").GetProperty("title").GetString() ?? string.Empty);
    }

    /// <summary>抖音：只是恰好出现 <c>roomStore</c> 字样的片段（字符串值）必须被跳过，继续找真正的状态对象。</summary>
    [TestMethod("抖音：跳过只是出现 roomStore 字样的干扰片段")]
    public void DouyinSkipsDecoyRoomStoreOccurrence()
    {
        const string Html = """<script>var x = "\"roomStore\""; var y = {"roomStore":{"roomInfo":{"anchor":{"nickname":"B"}}}};</script>""";

        using JsonDocument? document = CreateDouyinParser().ExtractRoomStoreDocument(Html);

        Assert.NotNull(document, "第二个 roomStore 才是真正的状态对象");
        JsonElement roomInfo = document!.RootElement.GetProperty("roomStore").GetProperty("roomInfo");
        Assert.Equal("B", roomInfo.GetProperty("anchor").GetProperty("nickname").GetString() ?? string.Empty);
    }

    /// <summary>抖音：页面既可能是未转义写法，也可能是 <c>\"sec_uid\"</c> 转义写法，两种都要取到。</summary>
    [TestMethod("抖音：sec_uid 同时兼容未转义与转义写法")]
    public void DouyinExtractsAnchorSecUidFromBothForms()
    {
        const string SecUid = "MS4wLjABAAAAabcdefghijklmnop";
        const string RawHtml = """<script>{"sec_uid":"MS4wLjABAAAAabcdefghijklmnop","web_rid":"123"}</script>""";
        const string EscapedHtml = """<script>self.__pace_f.push([1,"{\"sec_uid\":\"MS4wLjABAAAAabcdefghijklmnop\",\"web_rid\":\"123\"}"])</script>""";

        Assert.Equal(SecUid, DouyinParser.ExtractAnchorSecUid(RawHtml) ?? string.Empty);
        Assert.Equal(SecUid, DouyinParser.ExtractAnchorSecUid(EscapedHtml) ?? string.Empty);
        Assert.Null(DouyinParser.ExtractAnchorSecUid("<html><body>没有主播标识</body></html>"));
    }

    /// <summary>
    /// 抖音：reflow 备用接口必须带上参考实现里的公开参数（<c>version_code</c>/<c>app_id</c>/
    /// <c>sec_user_id</c>），否则平台返回 status_code=10011（Request params error）。
    /// </summary>
    [TestMethod("抖音：reflow 地址补齐公开参数且不含签名")]
    public void DouyinBuildsReflowUrlWithPublicParameters()
    {
        const string WithSecUid = "https://webcast.amemv.com/webcast/room/reflow/info/?type_id=0&live_id=1&room_id=123456"
            + "&sec_user_id=MS4wLjABAAAAabcdefghijklmnop&version_code=99.99.99&app_id=1128";
        const string WithoutSecUid = "https://webcast.amemv.com/webcast/room/reflow/info/?type_id=0&live_id=1&room_id=123456"
            + "&version_code=99.99.99&app_id=1128";

        string url = DouyinParser.BuildReflowUrl("123456", "MS4wLjABAAAAabcdefghijklmnop");

        Assert.Equal(WithSecUid, url, "已知 sec_uid 时必须带上该参数");
        Assert.Equal(WithoutSecUid, DouyinParser.BuildReflowUrl("123456", null), "无 sec_uid 时必须省略该参数");
        Assert.DoesNotContain("a_bogus", url, "不得实现平台签名");
        Assert.DoesNotContain("ms_token", url, "不得实现平台签名");
        Assert.DoesNotContain("__ac_signature", url, "不得实现平台签名");
    }

    /// <summary>抖音：reflow 的 status_code 非 0 必须归类为"平台拒绝"，并把状态码与平台消息写进 detail。</summary>
    [TestMethod("抖音：reflow 非 0 status_code 归类为平台拒绝")]
    public void DouyinClassifiesReflowRejectionAsRejected()
    {
        DouyinParser parser = CreateDouyinParser();

        using JsonDocument rejected = JsonDocument.Parse(
            """{ "data": { "message": "Request params error" }, "status_code": 10011 }""");
        ResolveException exception = Assert.Throws<ResolveException>(
            () => parser.EnsureReflowAccepted(rejected.RootElement, "123456"));

        Assert.Equal(ResolveFailure.Rejected, exception.Failure);
        Assert.Contains("10011", exception.Message, "detail 必须带 status_code");
        Assert.Contains("Request params error", exception.Message, "detail 必须带平台 message");

        using JsonDocument rejectedAsText = JsonDocument.Parse(
            """{ "data": { "message": "Request params error" }, "status_code": "10011" }""");
        Assert.Equal(
            ResolveFailure.Rejected,
            Assert.Throws<ResolveException>(
                () => parser.EnsureReflowAccepted(rejectedAsText.RootElement, "123456")).Failure,
            "status_code 写成字符串时同样要识别为拒绝");

        using JsonDocument accepted = JsonDocument.Parse("""{ "status_code": 0, "data": { "data": { "room": {} } } }""");
        parser.EnsureReflowAccepted(accepted.RootElement, "123456");
    }

    /// <summary>抖音：已结束（status=4）与缺少 stream_url 都必须归类为"未开播"，而不是解析错误。</summary>
    [TestMethod("抖音：已结束与缺少 stream_url 归类为未开播")]
    public void DouyinClassifiesOfflineRooms()
    {
        DouyinParser parser = CreateDouyinParser();
        StreamCandidateBuilder builder = new(PlatformId.Douyin);

        using JsonDocument ended = JsonDocument.Parse(
            """{ "status": 4, "stream_url": { "flv_pull_url": { "origin": "https://cdn.example/o.flv" } } }""");
        ResolveException endedException = Assert.Throws<ResolveException>(
            () => parser.CollectCandidates(builder, ended.RootElement, "room-state", "origin", out _, out _));
        Assert.Equal(ResolveFailure.NotLive, endedException.Failure);
        Assert.Contains("status=4", endedException.Message, "detail 必须带 room.status");

        using JsonDocument noStream = JsonDocument.Parse("""{ "status": 2, "title": "T" }""");
        ResolveException noStreamException = Assert.Throws<ResolveException>(
            () => parser.CollectCandidates(builder, noStream.RootElement, "reflow-info", "origin", out _, out _));
        Assert.Equal(ResolveFailure.NotLive, noStreamException.Failure);
        Assert.Contains("stream_url", noStreamException.Message, "detail 必须说明缺 stream_url");
    }

    /// <summary>抖音：官方档位顺序固定为 原画 → 蓝光 → 超清 → 高清 → 标清，中文档位名与之一致。</summary>
    [TestMethod("抖音：官方档位顺序与中文档位名")]
    public void DouyinOrdersOfficialQualities()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "stream_url": {
                "flv_pull_url": {
                  "ld": "https://cdn.example/ld.flv",
                  "sd": "https://cdn.example/sd.flv",
                  "hd": "https://cdn.example/hd.flv",
                  "uhd": "https://cdn.example/uhd.flv",
                  "origin": "https://cdn.example/origin.flv" } } }
            """);

        (IReadOnlyList<QualityOption> qualities, string? selected) =
            CreateDouyinParser().BuildQualityOptions(document.RootElement, preferredQualityKey: null);

        Assert.Equal(5, qualities.Count);
        Assert.Equal("origin", qualities[0].Key);
        Assert.Equal("原画", qualities[0].Label);
        Assert.True(qualities[0].IsBest);
        Assert.Equal("uhd", qualities[1].Key);
        Assert.Equal("蓝光", qualities[1].Label);
        Assert.Equal("hd", qualities[2].Key);
        Assert.Equal("超清", qualities[2].Label);
        Assert.Equal("sd", qualities[3].Key);
        Assert.Equal("高清", qualities[3].Label);
        Assert.Equal("ld", qualities[4].Key);
        Assert.Equal("标清", qualities[4].Label);
        Assert.Equal("origin", selected, "未指定档位时取最高档");
    }

    /// <summary>抖音：纯音频档 <c>ao</c> 不能排在有视频档的直播间前面被当成最高档。</summary>
    [TestMethod("抖音：纯音频档不盖过视频档")]
    public void DouyinKeepsVideoQualityAboveAudioOnly()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{ "stream_url": { "flv_pull_url": { "ao": "https://cdn.example/ao.flv", "hd": "https://cdn.example/hd.flv" } } }""");

        (IReadOnlyList<QualityOption> qualities, string? selected) =
            CreateDouyinParser().BuildQualityOptions(document.RootElement, preferredQualityKey: null);

        Assert.Equal(2, qualities.Count);
        Assert.Equal("hd", qualities[0].Key, "有视频档时视频档在前");
        Assert.Equal("hd", selected);
        Assert.Equal("ao", qualities[1].Key);
    }
}
