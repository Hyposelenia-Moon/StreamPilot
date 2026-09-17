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

    /// <summary>
    /// B站：高帧率后缀必须来自接口声明（<c>g_qn_desc[].media_base_desc.detail_desc.tag</c>），
    /// 不能按档位语义硬判。
    /// </summary>
    /// <remarks>
    /// 两份响应取自真实房间：814（有 60 帧，<c>tag=["高帧率"]</c>）与 1868871278（无 60 帧，<c>tag</c> 缺失）。
    /// </remarks>
    [TestMethod("B站：高帧率后缀只认接口声明的 detail_desc.tag")]
    public void BilibiliReadsHighFrameRateFromDeclaration()
    {
        using JsonDocument highFrameRate = JsonDocument.Parse(
            """
            { "playurl_info": { "playurl": {
                "g_qn_desc": [
                  { "qn": 10000, "desc": "原画", "hdr_desc": "",
                    "media_base_desc": { "detail_desc": { "desc": "1080P 原画", "tag": ["高帧率"] },
                                         "brief_desc": { "desc": "1080P", "badge": "原画" } } },
                  { "qn": 400, "desc": "蓝光", "hdr_desc": "",
                    "media_base_desc": { "detail_desc": { "desc": "1080P 蓝光" } } } ],
                "stream": [ { "format": [ { "codec": [ { "accept_qn": [10000, 400] } ] } ] } ] } } }
            """);

        (IReadOnlyList<QualityOption> declared, string? declaredKey) =
            CreateBilibiliParser().BuildQualityOptions(highFrameRate.RootElement, requestedQuality: null);

        Assert.Equal(2, declared.Count);
        Assert.Equal("1080P 原画（高帧率）", declared[0].Label);
        Assert.Equal("1080P 蓝光", declared[1].Label);
        Assert.Equal("10000", declaredKey);

        using JsonDocument normal = JsonDocument.Parse(
            """
            { "playurl_info": { "playurl": {
                "g_qn_desc": [
                  { "qn": 10000, "desc": "原画", "hdr_desc": "",
                    "media_base_desc": { "detail_desc": { "desc": "1080P 原画" },
                                         "brief_desc": { "desc": "1080P", "badge": "原画" } } } ],
                "stream": [ { "format": [ { "codec": [ { "accept_qn": [10000] } ] } ] } ] } } }
            """);

        (IReadOnlyList<QualityOption> plain, _) =
            CreateBilibiliParser().BuildQualityOptions(normal.RootElement, requestedQuality: null);

        Assert.Equal(1, plain.Count);
        Assert.Equal("1080P 原画", plain[0].Label, "接口没有声明高帧率时不得加后缀");
    }

    /// <summary>B站：完全没有档位声明时同样不能凭空标注高帧率。</summary>
    [TestMethod("B站：缺少 g_qn_desc 时不标注高帧率")]
    public void BilibiliDoesNotGuessHighFrameRateWithoutDeclaration()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "playurl_info": { "playurl": {
                "stream": [ { "format": [ { "codec": [ { "accept_qn": [10000, 400, 250] } ] } ] } ] } } }
            """);

        (IReadOnlyList<QualityOption> qualities, _) =
            CreateBilibiliParser().BuildQualityOptions(document.RootElement, requestedQuality: null);

        Assert.Equal(3, qualities.Count);
        Assert.Equal("1080P 原画", qualities[0].Label);
        Assert.Equal("1080P 蓝光", qualities[1].Label);
        Assert.Equal("720P 超清", qualities[2].Label);
    }

    /// <summary>
    /// B站：<c>g_qn_desc</c> 与 <c>accept_qn</c> 都为空时，用响应里的 <c>current_qn</c>
    /// 兜底生成一条当前档位，保证播放页的画质下拉不会空掉。
    /// </summary>
    [TestMethod("B站：档位声明全缺失时用 current_qn 兜底")]
    public void BilibiliFallsBackToCurrentQualityNumber()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "playurl_info": { "playurl": {
                "stream": [ { "format": [ { "codec": [
                  { "current_qn": 10000, "accept_qn": [],
                    "base_url": "/live/x.flv",
                    "url_info": [ { "host": "https://cdn.example", "extra": "?token=1" } ] } ] } ] } ] } } }
            """);

        (IReadOnlyList<QualityOption> qualities, string? selectedKey) =
            CreateBilibiliParser().BuildQualityOptions(document.RootElement, requestedQuality: null);

        Assert.Equal(1, qualities.Count, "至少要有当前这一档");
        Assert.Equal("10000", qualities[0].Key);
        Assert.Equal("1080P 原画", qualities[0].Label, "名称回退到内置命名");
        Assert.True(qualities[0].IsBest);
        Assert.Equal("10000", selectedKey);
    }

    /// <summary>B站：调用方显式请求的档位在没有其它声明时也必须出现在列表里。</summary>
    [TestMethod("B站：请求档位在无声明时也要出现在列表")]
    public void BilibiliKeepsRequestedQualityWhenNothingDeclared()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{ "playurl_info": { "playurl": { "stream": [ { "format": [ { "codec": [ { } ] } ] } ] } } }""");

        (IReadOnlyList<QualityOption> qualities, string? selectedKey) =
            CreateBilibiliParser().BuildQualityOptions(document.RootElement, requestedQuality: 400);

        Assert.Equal(1, qualities.Count);
        Assert.Equal("400", qualities[0].Key);
        Assert.Equal("1080P 蓝光", qualities[0].Label);
        Assert.Equal("400", selectedKey);
    }

    /// <summary>B站：hdr_type 非 0 或 detail_desc.tag 含 HDR 时补 HDR 标记。</summary>
    [TestMethod("B站：HDR 标记来自 hdr_type 与 tag 声明")]
    public void BilibiliReadsHdrFromDeclaration()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "playurl_info": { "playurl": {
                "g_qn_desc": [
                  { "qn": 10000, "desc": "原画", "hdr_desc": "", "hdr_type": 1,
                    "media_base_desc": { "detail_desc": { "desc": "1080P 原画", "tag": ["高帧率", "HDR"] } } } ],
                "stream": [ { "format": [ { "codec": [ { "accept_qn": [10000] } ] } ] } ] } } }
            """);

        (IReadOnlyList<QualityOption> qualities, _) =
            CreateBilibiliParser().BuildQualityOptions(document.RootElement, requestedQuality: null);

        Assert.Equal(1, qualities.Count);
        Assert.Equal("1080P 原画（HDR 高帧率）", qualities[0].Label);
    }

    /// <summary>
    /// 抖音：房间页里 <c>roomStore</c> 会出现两次，前一个是空的 <c>roomInfo:{}</c> 外壳，
    /// 后一个才是真正的房间状态；必须跳过空壳继续扫描。
    /// </summary>
    [TestMethod("抖音：跳过空的 roomStore 外壳")]
    public void DouyinSkipsEmptyRoomStoreShell()
    {
        const string Html = """
            <script>self.__pace_f.push([1,"{\"state\":{\"roomStore\":{\"roomInfo\":{},\"liveStatus\":\"normal\"}}}"])</script>
            <script>self.__pace_f.push([1,"{\"state\":{\"roomStore\":{\"roomInfo\":{\"anchor\":{\"nickname\":\"A\"},\"room\":{\"status\":2}}}}}"])</script>
            """;

        using JsonDocument? document = CreateDouyinParser().ExtractRoomStoreDocument(Html);

        Assert.NotNull(document, "必须跳过空壳取到真正的房间状态");
        JsonElement roomInfo = document!.RootElement.GetProperty("roomStore").GetProperty("roomInfo");
        Assert.Equal("A", roomInfo.GetProperty("anchor").GetProperty("nickname").GetString() ?? string.Empty);
    }

    /// <summary>抖音：页面只有空壳时不能当成房间状态，必须返回 null 交给下一条路径。</summary>
    [TestMethod("抖音：只有空 roomStore 外壳时返回 null")]
    public void DouyinIgnoresOnlyEmptyRoomStoreShell()
    {
        const string Html = """<script>self.__pace_f.push([1,"{\"roomStore\":{\"roomInfo\":{}}}"])</script>""";

        using JsonDocument? document = CreateDouyinParser().ExtractRoomStoreDocument(Html);

        Assert.Null(document);
    }

    /// <summary>抖音：进房接口地址只带公开参数，不含任何平台签名。</summary>
    [TestMethod("抖音：进房接口地址不含签名")]
    public void DouyinBuildsRoomEnterUrlWithoutSignature()
    {
        string url = DouyinParser.BuildRoomEnterUrl("745964462470");

        Assert.Contains("live.douyin.com/webcast/room/web/enter/", url, "必须走实测可用的进房接口");
        Assert.Contains("web_rid=745964462470", url, "房间号必须带上");
        Assert.Contains("aid=6383", url, "公开参数缺失会被平台拒绝");
        Assert.Contains("app_name=douyin_web", url, "公开参数缺失会被平台拒绝");
        Assert.DoesNotContain("a_bogus", url, "不得实现平台签名");
        Assert.DoesNotContain("ms_token", url, "不得实现平台签名");
        Assert.DoesNotContain("__ac_signature", url, "不得实现平台签名");
    }

    /// <summary>抖音：进房接口的 status_code 非 0 同样归类为"平台拒绝"。</summary>
    [TestMethod("抖音：进房接口非 0 status_code 归类为平台拒绝")]
    public void DouyinClassifiesRoomEnterRejectionAsRejected()
    {
        DouyinParser parser = CreateDouyinParser();

        using JsonDocument rejected = JsonDocument.Parse(
            """{ "data": { "message": "Request params error" }, "status_code": 10011 }""");
        ResolveException exception = Assert.Throws<ResolveException>(
            () => parser.EnsureRoomEnterAccepted(rejected.RootElement, "745964462470"));

        Assert.Equal(ResolveFailure.Rejected, exception.Failure);
        Assert.Contains("10011", exception.Message, "detail 必须带 status_code");

        using JsonDocument accepted = JsonDocument.Parse("""{ "status_code": 0, "data": { "data": [] } }""");
        parser.EnsureRoomEnterAccepted(accepted.RootElement, "745964462470");
    }

    /// <summary>
    /// 抖音：进房接口的 Cookie 头必须把用户自备 Cookie 与首页会话 cookie（<c>ttwid</c>）合并。
    /// </summary>
    /// <remarks>
    /// 实测根因：请求头里只能有一个 Cookie，过去直接写 <c>ttwid=...</c> 会把用户登录态整段丢掉，
    /// 于是需要登录才下发的最高档（<c>origin</c> 原画）永远枚举不到。
    /// </remarks>
    [TestMethod("抖音：进房 Cookie 合并用户登录态与会话 cookie")]
    public void DouyinMergesUserCookieWithSessionCookie()
    {
        string merged = DouyinParser.BuildRoomEnterCookie("session-value");

        Assert.Equal("ttwid=session-value", merged, "没有用户 Cookie 时只带会话 cookie");
        Assert.DoesNotContain("__ac_signature", merged, "不得引入任何签名");
        Assert.DoesNotContain("a_bogus", merged, "不得引入任何签名");
        Assert.DoesNotContain("ms_token", merged, "不得引入任何签名");
    }

    /// <summary>抖音：作用域里有用户 Cookie 时必须合并（只留一个 Cookie 头，两者都生效）。</summary>
    [TestMethod("抖音：用户 Cookie 与会话 cookie 合并成一个头")]
    public void DouyinKeepsUserCookieWhenSessionCookiePresent()
    {
        HttpTextClient client = new(new HttpClientFactory(new NetworkOptions(), NullStructuredLogger.Instance), NullStructuredLogger.Instance);
        using (client.UseCookie("sessionid=abc; ttwid=stale; passport_csrf_token=xyz"))
        {
            string merged = DouyinParser.BuildRoomEnterCookie("fresh");
            Assert.Contains("sessionid=abc", merged, "用户登录态必须保留");
            Assert.Contains("passport_csrf_token=xyz", merged, "用户登录态必须保留");
            Assert.Contains("ttwid=fresh", merged, "首页下发的会话 cookie 必须保留");
            Assert.DoesNotContain("ttwid=stale", merged, "同名 cookie 只保留首页下发的值");
        }

        Assert.Equal("ttwid=fresh", DouyinParser.BuildRoomEnterCookie("fresh"), "作用域释放后不再带入用户 Cookie");
    }

    private static YyParser CreateYyParser() =>
        new(new HttpTextClient(new HttpClientFactory(new NetworkOptions(), NullStructuredLogger.Instance), NullStructuredLogger.Instance),
            NullStructuredLogger.Instance);

    /// <summary>创建一个不发起网络请求的 B站解析器（HTTP 客户端只在测试里被构造，不会被调用）。</summary>
    /// <returns>B站解析器实例。</returns>
    private static BilibiliParser CreateBilibiliParser() =>
        new(new HttpTextClient(new HttpClientFactory(new NetworkOptions(), NullStructuredLogger.Instance), NullStructuredLogger.Instance),
            NullStructuredLogger.Instance);

    private static BigoParser CreateBigoParser() =>
        new(new HttpTextClient(new HttpClientFactory(new NetworkOptions(), NullStructuredLogger.Instance), NullStructuredLogger.Instance),
            NullStructuredLogger.Instance);

    /// <summary>创建一个不发起网络请求的抖音解析器（HTTP 客户端只在测试里被构造，不会被调用）。</summary>
    /// <returns>抖音解析器实例。</returns>
    private static DouyinParser CreateDouyinParser()
    {
        HttpClientFactory factory = new(new NetworkOptions(), NullStructuredLogger.Instance);
        return new DouyinParser(new HttpTextClient(factory, NullStructuredLogger.Instance), factory, NullStructuredLogger.Instance);
    }

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

    /// <summary>
    /// 抖音：键优先级固定为 origin → real_origin → uhd → hd → sd → ld → FULL_HD1 → HD1 → SD1 → SD2 → ao，
    /// 「原画」这一档只能来自 <c>origin</c>。
    /// </summary>
    [TestMethod("抖音：档位键优先级与「原画」来源")]
    public void DouyinOrdersQualitiesByDocumentedPriority()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            { "stream_url": {
                "flv_pull_url": {
                  "ao": "https://cdn.example/ao.flv",
                  "SD2": "https://cdn.example/sd2.flv",
                  "SD1": "https://cdn.example/sd1.flv",
                  "HD1": "https://cdn.example/hd1.flv",
                  "FULL_HD1": "https://cdn.example/full.flv",
                  "ld": "https://cdn.example/ld.flv",
                  "sd": "https://cdn.example/sd.flv",
                  "hd": "https://cdn.example/hd.flv",
                  "uhd": "https://cdn.example/uhd.flv",
                  "real_origin": "https://cdn.example/real.flv",
                  "origin": "https://cdn.example/origin.flv" } } }
            """);

        (IReadOnlyList<QualityOption> qualities, string? selected) =
            CreateDouyinParser().BuildQualityOptions(document.RootElement, preferredQualityKey: null);

        string[] expected =
        [
            "origin", "real_origin", "uhd", "hd", "sd", "ld",
            "FULL_HD1", "HD1", "SD1", "SD2", "ao",
        ];

        Assert.Equal(expected.Length, qualities.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], qualities[index].Key, "第 " + index + " 项必须符合文档给出的优先级");
        }

        Assert.Equal("原画", qualities[0].Label, "「原画」只能来自 origin");
        Assert.Equal("真原画", qualities[1].Label, "real_origin 不得使用「原画」标签");
        Assert.Equal("origin", selected);
    }

    /// <summary>抖音：平台没有给出 <c>origin</c> 时，最高档如实落在下一优先级，不伪造「原画」。</summary>
    [TestMethod("抖音：缺少 origin 时不伪造原画档")]
    public void DouyinDoesNotInventOriginQuality()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{ "stream_url": { "flv_pull_url": { "uhd": "https://cdn.example/uhd.flv", "hd": "https://cdn.example/hd.flv" } } }""");

        (IReadOnlyList<QualityOption> qualities, string? selected) =
            CreateDouyinParser().BuildQualityOptions(document.RootElement, preferredQualityKey: null);

        Assert.Equal(2, qualities.Count);
        Assert.Equal("uhd", qualities[0].Key);
        Assert.Equal("蓝光", qualities[0].Label, "没有 origin 时最高档就是 uhd 蓝光");
        Assert.Equal("uhd", selected);
        foreach (QualityOption option in qualities)
        {
            Assert.False(string.Equals("原画", option.Label, StringComparison.Ordinal), "平台没给 origin 就不得出现「原画」");
        }
    }

    /// <summary>抖音：风控/验证码中间页只用于把失败原因说清楚，不触发任何绕过逻辑。</summary>
    [TestMethod("抖音：识别风控验证页但不绕过")]
    public void DouyinDetectsSecurityChallengePage()
    {
        Assert.True(
            DouyinParser.HasSecurityChallenge("""<html><script>document.cookie="__ac_nonce=0123abc";</script></html>"""),
            "__ac_nonce 是风控中间页标记");
        Assert.True(
            DouyinParser.HasSecurityChallenge("<html><title>验证码中间页</title></html>"),
            "验证码中间页文案同样是标记");
        Assert.False(
            DouyinParser.HasSecurityChallenge("""<script>self.__pace_f.push([1,"{\"roomStore\":{}}"])</script>"""),
            "正常房间页不能被误判为风控页");
    }
}
