namespace StreamPilot.Parsers.Huya;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamPilot.Core.Errors;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Parsers;
using StreamPilot.Core.Utilities;

/// <summary>
/// 虎牙直播解析器。
/// </summary>
/// <remarks>
/// 解析链路：房间页取真实数字房间号 → profileRoom 接口取直播状态与线路 → 匿名登录取 uid → anticode 签名 → 产出 FLV/HLS 候选。
/// 端点与签名算法见 <c>docs/adr/0003-parser-contract.md</c> 第 5 节虎牙行；参考实现的 <c>unreachable!()</c> panic 已改为互斥的失败分类。
/// 签名后的完整地址与 wsSecret 属于敏感信息，禁止写入日志；日志只使用 <see cref="SensitiveData.Fingerprint"/>。
/// </remarks>
internal sealed class HuyaParser : PlatformParserBase
{
    /// <summary>日志模块名。</summary>
    private const string ModuleName = "Parsers.Huya";

    /// <summary>虎牙站点根地址，同时用作所有请求的 Referer。</summary>
    private const string HuyaReferer = "https://www.huya.com/";

    /// <summary>房间页地址模板，参数为房间号或短号。</summary>
    private const string RoomPageUrlTemplate = "https://www.huya.com/{0}";

    /// <summary>profileRoom 接口地址模板，参数为真实数字房间号。</summary>
    private const string ProfileRoomUrlTemplate = "https://mp.huya.com/cache.php?m=Live&do=profileRoom&roomid={0}";

    /// <summary>匿名登录接口地址。</summary>
    private const string AnonymousLoginUrl = "https://udblgn.huya.com/web/anonymousLogin";

    /// <summary>匿名登录请求体模板（appId/byPass/version 由命名常量填充）。</summary>
    private const string AnonymousLoginBodyTemplate =
        "{{\"appId\":{0},\"byPass\":{1},\"context\":\"\",\"version\":\"{2}\",\"data\":{{}}}}";

    /// <summary>匿名登录 appId。</summary>
    private const int AnonymousAppId = 5002;

    /// <summary>匿名登录 byPass。</summary>
    private const int AnonymousByPass = 3;

    /// <summary>匿名登录协议版本。</summary>
    private const string AnonymousLoginVersion = "2.4";

    /// <summary>JSON 媒体类型。</summary>
    private const string JsonMediaType = "application/json";

    /// <summary>JSON 请求头取值（接口要求大写 UTF-8）。</summary>
    private const string JsonContentTypeHeader = "application/json; charset=UTF-8";

    /// <summary>Referer 头名。</summary>
    private const string HeaderReferer = "Referer";

    /// <summary>Content-Type 头名。</summary>
    private const string HeaderContentType = "Content-Type";

    /// <summary>档位参数名（虎牙用码率指定线路档位，签名不覆盖该参数）。</summary>
    private const string BitRateParameterName = "ratio=";

    /// <summary>追加档位参数时的分隔符。</summary>
    private const string BitRateParameterSeparator = "&";

    /// <summary>输入校验阶段的操作名。</summary>
    private const string ValidateOperation = "validate";

    /// <summary>房间页抓取阶段的操作名。</summary>
    private const string GetRoomPageOperation = "get-room-page";

    /// <summary>profileRoom 接口阶段的操作名。</summary>
    private const string GetProfileRoomOperation = "get-profile-room";

    /// <summary>匿名登录阶段的操作名。</summary>
    private const string AnonymousLoginOperation = "anonymous-login";

    /// <summary>anticode 签名阶段的操作名。</summary>
    private const string AnticodeOperation = "anticode";

    /// <summary>房间页内直播信息的起始标记。</summary>
    private const string StreamJsonStartMarker = "stream: ";

    /// <summary>房间页内直播信息的结束标记（其后补一个右花括号即为完整 JSON）。</summary>
    private const string StreamJsonEndMarker = ",\"iFrameRate\"";

    /// <summary>补全截断 JSON 用的右花括号。</summary>
    private const string StreamJsonClosingBrace = "}";

    /// <summary>接口成功状态码。</summary>
    private const int ProfileStatusOk = 200;

    /// <summary>接口"房间不存在"状态码。</summary>
    private const int ProfileStatusRoomNotFound = 422;

    /// <summary>开播状态。</summary>
    private const string LiveStatusOn = "ON";

    /// <summary>未开播状态。</summary>
    private const string LiveStatusOff = "OFF";

    /// <summary>轮播/重播状态。</summary>
    private const string LiveStatusReplay = "REPLAY";

    /// <summary>字段缺失时用于拼装诊断文本的占位符。</summary>
    private const string UnknownValuePlaceholder = "<未提供>";

    /// <summary>anticode 的 ver 键名。</summary>
    private const string AnticodeVerKey = "ver";

    /// <summary>anticode 的 ver 固定值。</summary>
    private const string AnticodeVerValue = "1";

    /// <summary>anticode 的 sv 键名。</summary>
    private const string AnticodeSvKey = "sv";

    /// <summary>anticode 的 sv 固定值。</summary>
    private const string AnticodeSvValue = "2110211124";

    /// <summary>anticode 的 seqid 键名。</summary>
    private const string AnticodeSeqIdKey = "seqid";

    /// <summary>anticode 的 uid 键名。</summary>
    private const string AnticodeUidKey = "uid";

    /// <summary>anticode 的 uuid 键名。</summary>
    private const string AnticodeUuidKey = "uuid";

    /// <summary>anticode 的 wsSecret 键名。</summary>
    private const string AnticodeWsSecretKey = "wsSecret";

    /// <summary>anticode 的 wsTime 键名。</summary>
    private const string AnticodeWsTimeKey = "wsTime";

    /// <summary>anticode 的 ctype 键名。</summary>
    private const string AnticodeCtypeKey = "ctype";

    /// <summary>anticode 的 t 键名。</summary>
    private const string AnticodeTimeKey = "t";

    /// <summary>anticode 的 fm 键名。</summary>
    private const string AnticodeFmKey = "fm";

    /// <summary>anticode 的 txyp 键名（签名后必须移除）。</summary>
    private const string AnticodeTxypKey = "txyp";

    /// <summary>fm 中的 uid 占位符。</summary>
    private const string AnticodeUidPlaceholder = "$0";

    /// <summary>fm 中的流名称占位符。</summary>
    private const string AnticodeStreamNamePlaceholder = "$1";

    /// <summary>fm 中的 ss 占位符。</summary>
    private const string AnticodeSecretPlaceholder = "$2";

    /// <summary>fm 中的 wsTime 占位符。</summary>
    private const string AnticodeTimePlaceholder = "$3";

    /// <summary>ss 原文的分隔符。</summary>
    private const string AnticodeSecretSeparator = "|";

    /// <summary>签名结果中键值对之间的分隔符。</summary>
    private const string AnticodePairSeparator = "&";

    /// <summary>uuid 计算使用的时间窗口（毫秒）。</summary>
    private const long UuidTimeWindowMilliseconds = 10_000_000_000L;

    /// <summary>uuid 计算使用的毫秒放大倍数。</summary>
    private const long UuidMillisecondsScale = 1000L;

    /// <summary>uuid 计算使用的随机数上界（不含）。</summary>
    private const int UuidRandomUpperBound = 1000;

    /// <summary>uuid 计算的取模值。</summary>
    private const long UuidModulus = 4_294_967_295L;

    /// <summary>1080P 高帧率码率阈值（bps）。</summary>
    private const int HighFrameRate1080BitRateThreshold = 8_000_000;

    /// <summary>1080P 码率阈值（bps）。</summary>
    private const int FullHdBitRateThreshold = 4_000_000;

    /// <summary>720P 码率阈值（bps）。</summary>
    private const int HighDefinitionBitRateThreshold = 2_000_000;

    /// <summary>JSON 字段 data。</summary>
    private const string JsonData = "data";

    /// <summary>JSON 字段 status。</summary>
    private const string JsonStatus = "status";

    /// <summary>JSON 字段 message。</summary>
    private const string JsonMessage = "message";

    /// <summary>JSON 字段 liveStatus。</summary>
    private const string JsonLiveStatus = "liveStatus";

    /// <summary>JSON 字段 liveData。</summary>
    private const string JsonLiveData = "liveData";

    /// <summary>JSON 字段 nick。</summary>
    private const string JsonNick = "nick";

    /// <summary>JSON 字段 introduction。</summary>
    private const string JsonIntroduction = "introduction";

    /// <summary>JSON 字段 gameFullName。</summary>
    private const string JsonGameFullName = "gameFullName";

    /// <summary>JSON 字段 stream。</summary>
    private const string JsonStream = "stream";

    /// <summary>JSON 字段 baseSteamInfoList（上游拼写，保持兼容）。</summary>
    private const string JsonBaseStreamInfoList = "baseSteamInfoList";

    /// <summary>JSON 字段 baseStreamInfoList（上游修正拼写后的回退键）。</summary>
    private const string JsonBaseStreamInfoListFallback = "baseStreamInfoList";

    /// <summary>JSON 字段 gameLiveInfo。</summary>
    private const string JsonGameLiveInfo = "gameLiveInfo";

    /// <summary>JSON 字段 profileRoom。</summary>
    private const string JsonProfileRoom = "profileRoom";

    /// <summary>JSON 字段 uid。</summary>
    private const string JsonUid = "uid";

    /// <summary>JSON 字段 sStreamName。</summary>
    private const string JsonStreamName = "sStreamName";

    /// <summary>JSON 字段 sFlvUrl。</summary>
    private const string JsonFlvUrl = "sFlvUrl";

    /// <summary>JSON 字段 sFlvAntiCode。</summary>
    private const string JsonFlvAnticode = "sFlvAntiCode";

    /// <summary>JSON 字段 sFlvUrlSuffix。</summary>
    private const string JsonFlvUrlSuffix = "sFlvUrlSuffix";

    /// <summary>JSON 字段 sHlsUrl。</summary>
    private const string JsonHlsUrl = "sHlsUrl";

    /// <summary>JSON 字段 sHlsAntiCode。</summary>
    private const string JsonHlsAnticode = "sHlsAntiCode";

    /// <summary>JSON 字段 sHlsUrlSuffix。</summary>
    private const string JsonHlsUrlSuffix = "sHlsUrlSuffix";

    /// <summary>JSON 字段 iBitRate。</summary>
    private const string JsonBitRate = "iBitRate";

    /// <summary>JSON 字段 bitRateInfo（内容是一段 JSON 字符串，声明可选档位）。</summary>
    private const string JsonBitRateInfo = "bitRateInfo";

    /// <summary>JSON 字段 sDisplayName（官方档位名）。</summary>
    private const string JsonDisplayName = "sDisplayName";

    /// <summary>JSON 字段 flv。</summary>
    private const string JsonStreamFlv = "flv";

    /// <summary>JSON 字段 hls。</summary>
    private const string JsonStreamHls = "hls";

    /// <summary>JSON 字段 rateArray（每个格式下声明的档位）。</summary>
    private const string JsonRateArray = "rateArray";

    /// <summary>房间页结构变化时的失败描述。</summary>
    private const string RoomPageParseFailedDetail = "虎牙房间页结构已变化，无法提取直播信息。";

    /// <summary>房间页缺少房间号时的失败描述。</summary>
    private const string ProfileRoomIdMissingDetail = "无法从虎牙房间页解析房间号。";

    /// <summary>profileRoom 响应缺少 data 字段时的失败描述。</summary>
    private const string ProfileRoomDataMissingDetail = "虎牙接口响应缺少 data 字段。";

    /// <summary>profileRoom 响应不是合法 JSON 时的失败描述。</summary>
    private const string ProfileRoomJsonInvalidDetail = "虎牙接口响应不是合法 JSON。";

    /// <summary>接口未返回可用线路时的失败描述。</summary>
    private const string NoStreamLinesDetail = "虎牙未返回可用线路。";

    /// <summary>匿名登录响应不是合法 JSON 时的失败描述。</summary>
    private const string AnonymousLoginJsonInvalidDetail = "虎牙匿名登录响应不是合法 JSON。";

    /// <summary>匿名登录未返回 uid 时的失败描述。</summary>
    private const string AnonymousUidMissingDetail = "虎牙匿名登录未返回 uid。";

    /// <summary>匿名登录返回的 uid 非法时的失败描述。</summary>
    private const string AnonymousUidInvalidDetail = "虎牙匿名登录返回的 uid 非法。";

    /// <summary>anticode 缺少 ctype/t 时的失败描述。</summary>
    private const string AnticodeMissingParamsDetail = "虎牙签名参数缺少 ctype/t。";

    /// <summary>anticode 缺少 wsTime 时的失败描述。</summary>
    private const string AnticodeMissingWsTimeDetail = "虎牙签名参数缺少 wsTime。";

    /// <summary>anticode 的 fm 解码失败时的失败描述。</summary>
    private const string AnticodeFmDecodeFailedDetail = "虎牙签名参数 fm 解码失败。";

    /// <summary>全部线路都签名失败时的失败描述。</summary>
    private const string AllLinesSignedFailedDetail = "虎牙所有线路签名失败。";

    /// <summary>共享 HTTP 客户端。</summary>
    private readonly HttpTextClient _http;

    /// <summary>初始化解析器。</summary>
    /// <param name="http">带超时与重试的 HTTP 客户端。</param>
    /// <param name="logger">结构化日志。</param>
    public HuyaParser(HttpTextClient http, IStructuredLogger logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>本解析器负责的平台。</summary>
    public override PlatformId Platform => PlatformId.Huya;

    /// <summary>平台展示名。</summary>
    public override string DisplayName => "虎牙";

    /// <summary>虎牙直播间链接前缀。</summary>
    public override string RoomUrlPrefix => "https://www.huya.com/";

    /// <summary>
    /// 执行虎牙解析：房间号 → profileRoom → 直播状态与线路 → 匿名 uid → 签名候选。
    /// </summary>
    /// <param name="query">已校验的房间查询条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>房间信息与候选流。</returns>
    /// <exception cref="ResolveException">输入非法、未开播、轮播、房间不存在或响应结构变化时抛出。</exception>
    protected override async Task<ResolvedRoom> OnParseAsync(RoomQuery query, CancellationToken cancellationToken)
    {
        // 用户自备 Cookie 只作用于本次解析的请求（播放地址与中继都不带它）。
        using IDisposable cookieScope = _http.UseCookie(query.Cookie);
        string? roomIdOrShortId = string.IsNullOrWhiteSpace(query.RoomId)
            ? TryExtractRoomIdFromUrl(query.RoomUrl)
            : query.RoomId;

        if (string.IsNullOrWhiteSpace(roomIdOrShortId))
        {
            throw Fail(ResolveFailure.InvalidInput, ValidateOperation, "无法从输入中确定虎牙房间号。");
        }

        int profileRoom = await FetchRoomPageAsync(roomIdOrShortId, cancellationToken).ConfigureAwait(false);
        using JsonDocument profile = await FetchProfileRoomAsync(profileRoom, cancellationToken).ConfigureAwait(false);
        JsonElement data = ReadRequiredObject(profile.RootElement, JsonData, GetProfileRoomOperation, ProfileRoomDataMissingDetail);

        string liveStatus = ReadOptionalString(data, JsonLiveStatus) ?? string.Empty;
        if (string.Equals(liveStatus, LiveStatusOff, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail(ResolveFailure.NotLive, GetProfileRoomOperation, ResolveMessages.NotLive);
        }

        if (string.Equals(liveStatus, LiveStatusReplay, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail(ResolveFailure.Replaying, GetProfileRoomOperation, ResolveMessages.Replaying);
        }

        if (!string.Equals(liveStatus, LiveStatusOn, StringComparison.OrdinalIgnoreCase))
        {
            string statusText = liveStatus.Length == 0 ? UnknownValuePlaceholder : liveStatus;
            throw Fail(ResolveFailure.ParseError, GetProfileRoomOperation, $"虎牙返回未知直播状态：{statusText}");
        }

        JsonElement liveData = ReadOptionalObject(data, JsonLiveData);
        string anchor = ReadOptionalString(liveData, JsonNick) ?? string.Empty;
        string title = ReadOptionalString(liveData, JsonIntroduction) ?? string.Empty;
        string category = ReadOptionalString(liveData, JsonGameFullName) ?? string.Empty;

        JsonElement lines = ReadStreamLines(data);
        string uid = await FetchAnonymousUidAsync(cancellationToken).ConfigureAwait(false);

        // 虎牙把每个码率作为一条独立线路返回（各自带流名），因此先枚举档位，再只取选中的那一条。
        (IReadOnlyList<QualityOption> qualities, int? selectedBitRate) =
            BuildQualityOptions(data, query.PreferredQualityKey);
        IReadOnlyList<StreamCandidate> candidates = BuildCandidates(lines, uid, selectedBitRate);

        Logger.Info(ModuleName, "虎牙解析成功。", new Dictionary<string, object?>
        {
            ["roomId"] = profileRoom,
            ["liveStatus"] = liveStatus,
            ["candidates"] = candidates.Count,
            ["quality"] = selectedBitRate,
        });

        return new ResolvedRoom
        {
            Platform = Platform,
            RoomId = profileRoom.ToString(CultureInfo.InvariantCulture),
            Anchor = anchor,
            Title = title,
            Category = category,
            Candidates = candidates,
            ResolvedAt = DateTimeOffset.UtcNow,
            Qualities = qualities,
            SelectedQualityKey = selectedBitRate?.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// 枚举虎牙可选码率档位，并决定本次实际使用的档位。
    /// </summary>
    /// <param name="data">profileRoom 响应的 data 节点。</param>
    /// <param name="preferredKey">调用方指定的档位键（码率数值）；为空或无效时取最高档。</param>
    /// <returns>档位列表（从高到低）与选中的码率。</returns>
    /// <remarks>
    /// 虎牙的档位**不在线路对象里**（线路只有 CDN 维度），而是由
    /// <c>data.bitRateInfo</c>（JSON 字符串）或 <c>data.stream.flv/hls.rateArray[]</c> 声明：
    /// 每项含官方档位名 <c>sDisplayName</c> 与码率 <c>iBitRate</c>。
    /// <c>-1</c> 是"真原画"、<c>0</c> 是"原画/平台自选"，都排在高码率之前，而不是当成最小档。
    /// 档位通过地址上的 <c>&amp;ratio={iBitRate}</c> 生效（正码率才追加）。
    /// </remarks>
    internal static (IReadOnlyList<QualityOption> Qualities, int? SelectedBitRate) BuildQualityOptions(
        JsonElement data,
        string? preferredKey)
    {
        List<(int BitRate, string Label)> rates = ReadDeclaredRates(data);
        rates.Sort(static (left, right) => RankBitRate(right.BitRate).CompareTo(RankBitRate(left.BitRate)));

        List<QualityOption> options = [];
        List<int> known = [];
        foreach ((int bitRate, string label) in rates)
        {
            if (known.Contains(bitRate))
            {
                continue;
            }

            known.Add(bitRate);
            options.Add(new QualityOption
            {
                Key = bitRate.ToString(CultureInfo.InvariantCulture),
                Label = label.Length > 0 ? label : DescribeBitRate(bitRate),
                BitrateKbps = bitRate > 0 ? bitRate / 1000 : null,
                IsBest = options.Count == 0,
            });
        }

        int? requested = int.TryParse(preferredKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
        int? selected = requested is { } want && known.Contains(want)
            ? want
            : known.Count > 0 ? known[0] : requested;

        return (options, selected);
    }

    /// <summary>读取平台声明的码率档位（bitRateInfo 优先，其次是 flv/hls 的 rateArray）。</summary>
    /// <param name="data">profileRoom 响应的 data 节点。</param>
    /// <returns>（码率，官方档位名）列表；平台未声明时为空。</returns>
    private static List<(int BitRate, string Label)> ReadDeclaredRates(JsonElement data)
    {
        List<(int BitRate, string Label)> rates = [];
        ReadRatesFromBitRateInfo(data, rates);
        if (rates.Count > 0)
        {
            return rates;
        }

        if (TryGetProperty(data, JsonStream, out JsonElement stream) && stream.ValueKind == JsonValueKind.Object)
        {
            ReadRatesFromArray(stream, JsonStreamFlv, rates);
            ReadRatesFromArray(stream, JsonStreamHls, rates);
        }

        return rates;
    }

    /// <summary>解析 bitRateInfo（该字段是"JSON 字符串"，需要二次解析）。</summary>
    /// <param name="data">profileRoom 响应的 data 节点。</param>
    /// <param name="rates">收集结果的列表。</param>
    /// <remarks>实测该字段位于 <c>data.liveData.bitRateInfo</c>，个别响应也可能直接挂在 <c>data</c> 上，两处都尝试。</remarks>
    private static void ReadRatesFromBitRateInfo(JsonElement data, List<(int BitRate, string Label)> rates)
    {
        if (TryReadBitRateInfo(data, rates))
        {
            return;
        }

        JsonElement liveData = ReadOptionalObject(data, JsonLiveData);
        if (liveData.ValueKind == JsonValueKind.Object)
        {
            _ = TryReadBitRateInfo(liveData, rates);
        }
    }

    /// <summary>从指定节点的 bitRateInfo 字符串读取档位。</summary>
    /// <param name="parent">父节点。</param>
    /// <param name="rates">收集结果的列表。</param>
    /// <returns>读到至少一项返回 <see langword="true"/>。</returns>
    private static bool TryReadBitRateInfo(JsonElement parent, List<(int BitRate, string Label)> rates)
    {
        if (!TryGetProperty(parent, JsonBitRateInfo, out JsonElement info) || info.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? json = info.GetString();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            ReadRatesFromElements(document.RootElement, rates);
            return rates.Count > 0;
        }
        catch (JsonException)
        {
            // 平台偶尔给出非法片段：忽略该通道，继续尝试 rateArray。
            return false;
        }
    }

    /// <summary>从 stream.flv / stream.hls 的 rateArray 读取档位。</summary>
    /// <param name="stream">data.stream 节点。</param>
    /// <param name="formatName">flv 或 hls。</param>
    /// <param name="rates">收集结果的列表。</param>
    private static void ReadRatesFromArray(JsonElement stream, string formatName, List<(int BitRate, string Label)> rates)
    {
        if (!TryGetProperty(stream, formatName, out JsonElement format) || format.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (TryGetProperty(format, JsonRateArray, out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            ReadRatesFromElements(array, rates);
        }
    }

    /// <summary>从档位数组元素里读取 (iBitRate, sDisplayName)。</summary>
    /// <param name="array">档位数组。</param>
    /// <param name="rates">收集结果的列表。</param>
    private static void ReadRatesFromElements(JsonElement array, List<(int BitRate, string Label)> rates)
    {
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || ReadOptionalInt32(item, JsonBitRate) is not { } bitRate)
            {
                continue;
            }

            string label = ReadOptionalString(item, JsonDisplayName) ?? string.Empty;
            rates.Add((bitRate, label));
        }
    }

    /// <summary>码率排序权重：真原画（-1）最高，其次"平台自选/原画"（0），再按码率数值。</summary>
    /// <param name="bitRate">码率。</param>
    /// <returns>排序权重。</returns>
    private static int RankBitRate(int bitRate) => bitRate switch
    {
        < 0 => int.MaxValue,
        0 => int.MaxValue - 1,
        _ => bitRate,
    };

    /// <summary>生成码率档位的显示名。</summary>
    /// <param name="bitRate">码率（虎牙 iBitRate）。</param>
    /// <returns>显示名。</returns>
    private static string DescribeBitRate(int bitRate) => bitRate switch
    {
        20000 => "蓝光20M",
        14100 => "2K HDR",
        10000 => "蓝光10M",
        8000 => "蓝光8M",
        4200 => "HDR（10M）",
        4000 => "蓝光4M",
        -1 => "真原画",
        0 => "原画",
        _ => bitRate >= 1000
            ? (bitRate / 1000).ToString(CultureInfo.InvariantCulture) + "M 码率"
            : bitRate.ToString(CultureInfo.InvariantCulture) + "K 码率",
    };

    /// <summary>
    /// 抓取房间页并从内联直播信息中取出真实数字房间号。
    /// </summary>
    /// <param name="roomIdOrShortId">房间号或短号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>真实数字房间号。</returns>
    /// <exception cref="ResolveException">网络失败或页面结构变化时抛出。</exception>
    private async Task<int> FetchRoomPageAsync(string roomIdOrShortId, CancellationToken cancellationToken)
    {
        HttpRequestSpec spec = new()
        {
            Url = string.Format(CultureInfo.InvariantCulture, RoomPageUrlTemplate, roomIdOrShortId),
            Platform = Platform,
            Operation = GetRoomPageOperation,
        };

        string html = await _http.GetStringAsync(spec, cancellationToken).ConfigureAwait(false);
        int startMarker = html.IndexOf(StreamJsonStartMarker, StringComparison.Ordinal);
        int sliceStart = startMarker < 0 ? 0 : startMarker + StreamJsonStartMarker.Length;
        int endMarker = startMarker < 0
            ? -1
            : html.IndexOf(StreamJsonEndMarker, sliceStart, StringComparison.Ordinal);
        if (endMarker < 0)
        {
            throw Fail(ResolveFailure.ParseError, GetRoomPageOperation, RoomPageParseFailedDetail);
        }

        string streamJson = html[sliceStart..endMarker] + StreamJsonClosingBrace;
        using JsonDocument document = ParseJsonDocument(streamJson, GetRoomPageOperation, RoomPageParseFailedDetail);
        return ReadProfileRoomId(document.RootElement);
    }

    /// <summary>
    /// 从房间页内联 JSON 读取 <c>data[0].gameLiveInfo.profileRoom</c>。
    /// </summary>
    /// <param name="root">内联 JSON 的根元素。</param>
    /// <returns>真实数字房间号。</returns>
    /// <exception cref="ResolveException">字段缺失或类型不符时抛出。</exception>
    private int ReadProfileRoomId(JsonElement root)
    {
        if (!TryGetProperty(root, JsonData, out JsonElement data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0
            || !TryGetProperty(data[0], JsonGameLiveInfo, out JsonElement gameLiveInfo)
            || !TryGetProperty(gameLiveInfo, JsonProfileRoom, out JsonElement profileRoom))
        {
            throw Fail(ResolveFailure.ParseError, GetRoomPageOperation, ProfileRoomIdMissingDetail);
        }

        if (profileRoom.ValueKind == JsonValueKind.Number
            && profileRoom.TryGetInt32(out int number)
            && number > 0)
        {
            return number;
        }

        if (profileRoom.ValueKind == JsonValueKind.String
            && int.TryParse(profileRoom.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && parsed > 0)
        {
            return parsed;
        }

        throw Fail(ResolveFailure.ParseError, GetRoomPageOperation, ProfileRoomIdMissingDetail);
    }

    /// <summary>
    /// 抓取 profileRoom 接口并校验业务状态码。
    /// </summary>
    /// <param name="profileRoom">真实数字房间号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已通过状态校验的响应文档，调用方负责释放。</returns>
    /// <exception cref="ResolveException">房间不存在、网络失败或响应结构变化时抛出。</exception>
    private async Task<JsonDocument> FetchProfileRoomAsync(int profileRoom, CancellationToken cancellationToken)
    {
        HttpRequestSpec spec = new()
        {
            Url = string.Format(CultureInfo.InvariantCulture, ProfileRoomUrlTemplate, profileRoom),
            Platform = Platform,
            Operation = GetProfileRoomOperation,
            AllowNonSuccessStatus = true,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HeaderReferer] = HuyaReferer,
            },
        };

        HttpTextResponse response = await _http.SendAsync(spec, cancellationToken).ConfigureAwait(false);
        JsonDocument document = ParseJsonDocument(response.Text, GetProfileRoomOperation, ProfileRoomJsonInvalidDetail);
        try
        {
            ValidateProfileStatus(document.RootElement, (int)response.Status);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 校验 profileRoom 响应的业务状态：422 视为房间不存在，其余非 200 视为解析失败。
    /// </summary>
    /// <param name="root">响应根元素。</param>
    /// <param name="httpStatus">HTTP 状态码。</param>
    /// <exception cref="ResolveException">状态非 200 时抛出。</exception>
    private void ValidateProfileStatus(JsonElement root, int httpStatus)
    {
        int? status = ReadOptionalInt32(root, JsonStatus);
        string message = ReadOptionalString(root, JsonMessage) ?? string.Empty;

        if (status == ProfileStatusRoomNotFound || httpStatus == ProfileStatusRoomNotFound)
        {
            throw Fail(ResolveFailure.RoomNotFound, GetProfileRoomOperation, ResolveMessages.RoomNotFound);
        }

        if (status != ProfileStatusOk)
        {
            string statusText = status?.ToString(CultureInfo.InvariantCulture) ?? UnknownValuePlaceholder;
            throw Fail(ResolveFailure.ParseError, GetProfileRoomOperation, $"虎牙接口返回 status={statusText}：{message}");
        }
    }

    /// <summary>
    /// 匿名登录取 uid（anticode 签名需要）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>十进制 uid 文本。</returns>
    /// <exception cref="ResolveException">网络失败、响应结构变化或 uid 缺失/非法时抛出。</exception>
    private async Task<string> FetchAnonymousUidAsync(CancellationToken cancellationToken)
    {
        string body = string.Format(
            CultureInfo.InvariantCulture,
            AnonymousLoginBodyTemplate,
            AnonymousAppId,
            AnonymousByPass,
            AnonymousLoginVersion);

        HttpRequestSpec spec = new()
        {
            Url = AnonymousLoginUrl,
            Platform = Platform,
            Operation = AnonymousLoginOperation,
            ContentFactory = () => new StringContent(body, Encoding.UTF8, JsonMediaType),
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HeaderContentType] = JsonContentTypeHeader,
                [HeaderReferer] = HuyaReferer,
            },
        };

        string text = await _http.GetStringAsync(spec, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = ParseJsonDocument(text, AnonymousLoginOperation, AnonymousLoginJsonInvalidDetail);
        JsonElement data = ReadRequiredObject(
            document.RootElement,
            JsonData,
            AnonymousLoginOperation,
            AnonymousUidMissingDetail);

        string? uid = ReadOptionalString(data, JsonUid);
        if (string.IsNullOrWhiteSpace(uid))
        {
            throw Fail(ResolveFailure.ParseError, AnonymousLoginOperation, AnonymousUidMissingDetail);
        }

        if (!long.TryParse(uid, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw Fail(ResolveFailure.ParseError, AnonymousLoginOperation, AnonymousUidInvalidDetail);
        }

        return uid;
    }

    /// <summary>
    /// 取直播线路列表（兼容上游 <c>baseSteamInfoList</c> 拼写与 <c>baseStreamInfoList</c> 回退键）。
    /// </summary>
    /// <param name="data">profileRoom 响应的 data 对象。</param>
    /// <returns>线路数组元素。</returns>
    /// <exception cref="ResolveException">未返回任何线路时抛出。</exception>
    private JsonElement ReadStreamLines(JsonElement data)
    {
        JsonElement stream = ReadOptionalObject(data, JsonStream);
        if (stream.ValueKind == JsonValueKind.Object)
        {
            if ((TryGetProperty(stream, JsonBaseStreamInfoList, out JsonElement lines)
                    || TryGetProperty(stream, JsonBaseStreamInfoListFallback, out lines))
                && lines.ValueKind == JsonValueKind.Array
                && lines.GetArrayLength() > 0)
            {
                return lines;
            }
        }

        throw Fail(ResolveFailure.NotLive, GetProfileRoomOperation, NoStreamLinesDetail);
    }

    /// <summary>
    /// 逐条线路生成候选：先 FLV 后 HLS；单条线路签名失败只跳过该线路。
    /// </summary>
    /// <param name="lines">线路数组元素。</param>
    /// <param name="uid">匿名登录返回的 uid。</param>
    /// <param name="selectedBitRate">选中档位的码率（写进地址的 ratio）；<c>null</c>/<c>0</c>/<c>-1</c> 不追加。</param>
    /// <returns>候选流列表。</returns>
    /// <remarks>
    /// 线路数组是 **CDN 维度**（同一档位的多个 CDN），不是档位维度，因此这里不做过滤，
    /// 档位由地址上的 <c>ratio</c> 参数决定。
    /// </remarks>
    /// <exception cref="ResolveException">所有线路都失败时抛出。</exception>
    private IReadOnlyList<StreamCandidate> BuildCandidates(JsonElement lines, string uid, int? selectedBitRate)
    {
        StreamCandidateBuilder builder = new(Platform, Logger);
        foreach (JsonElement line in lines.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            try
            {
                AddCandidatesForLine(builder, line, uid, selectedBitRate);
            }
            catch (ResolveException exception)
            {
                Logger.Warn(ModuleName, "虎牙线路签名失败，已跳过该线路。", new Dictionary<string, object?>
                {
                    ["operation"] = exception.Operation,
                    ["failure"] = exception.Failure.ToString(),
                    ["reason"] = exception.Message,
                });
            }
        }

        if (builder.Count == 0)
        {
            throw Fail(ResolveFailure.ParseError, AnticodeOperation, AllLinesSignedFailedDetail);
        }

        return builder.Build();
    }

    /// <summary>
    /// 为一条线路生成 FLV 与 HLS 候选（FLV 优先）。
    /// </summary>
    /// <param name="builder">候选构造器。</param>
    /// <param name="line">线路 JSON 对象。</param>
    /// <param name="uid">匿名登录返回的 uid。</param>
    /// <param name="selectedBitRate">选中档位的码率（写入地址的 ratio）。</param>
    private void AddCandidatesForLine(StreamCandidateBuilder builder, JsonElement line, string uid, int? selectedBitRate)
    {
        string streamName = ReadOptionalString(line, JsonStreamName) ?? string.Empty;
        if (streamName.Length == 0)
        {
            Logger.Warn(ModuleName, "虎牙线路缺少流名称，已跳过该线路。", new Dictionary<string, object?>
            {
                ["operation"] = GetProfileRoomOperation,
            });
            return;
        }

        // 线路对象只有 CDN 维度、没有码率字段，档位来自调用方选中的声明档位。
        StreamQuality quality = MapQuality(selectedBitRate);
        int? bitRate = selectedBitRate;

        AddFormatCandidate(
            builder,
            baseUrl: ReadOptionalString(line, JsonFlvUrl),
            anticode: ReadOptionalString(line, JsonFlvAnticode),
            suffix: ReadOptionalString(line, JsonFlvUrlSuffix),
            streamName: streamName,
            uid: uid,
            quality: quality,
            format: StreamFormat.FlvHttp,
            bitRate: bitRate);

        AddFormatCandidate(
            builder,
            baseUrl: ReadOptionalString(line, JsonHlsUrl),
            anticode: ReadOptionalString(line, JsonHlsAnticode),
            suffix: ReadOptionalString(line, JsonHlsUrlSuffix),
            streamName: streamName,
            uid: uid,
            quality: quality,
            format: StreamFormat.HlsTs,
            bitRate: bitRate);
    }

    /// <summary>
    /// 为一个"基地址 + anticode + 后缀"组合签名并加入候选。
    /// </summary>
    /// <param name="builder">候选构造器。</param>
    /// <param name="baseUrl">该格式的基地址。</param>
    /// <param name="anticode">该格式的 anticode 串。</param>
    /// <param name="suffix">该格式的地址后缀（例如 flv、m3u8）。</param>
    /// <param name="streamName">流名称。</param>
    /// <param name="uid">匿名登录返回的 uid。</param>
    /// <param name="quality">画质档位。</param>
    /// <param name="format">容器/传输格式。</param>
    /// <param name="bitRate">该线路的码率（kbps）；<c>null</c> 或 <c>&lt;=0</c> 时不追加 ratio。</param>
    /// <exception cref="ResolveException">签名参数缺失或 fm 解码失败时抛出。</exception>
    private void AddFormatCandidate(
        StreamCandidateBuilder builder,
        string? baseUrl,
        string? anticode,
        string? suffix,
        string streamName,
        string uid,
        StreamQuality quality,
        StreamFormat format,
        int? bitRate)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(anticode)
            || string.IsNullOrWhiteSpace(suffix))
        {
            return;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            Logger.Warn(ModuleName, "虎牙线路基地址非法，已丢弃该候选。", new Dictionary<string, object?>
            {
                ["format"] = format.ToString(),
                ["fingerprint"] = SensitiveData.Fingerprint(baseUrl),
            });
            return;
        }

        string signedAnticode = BuildAnticode(anticode, streamName, uid);

        // 正码率需要显式带 ratio 才是该档位；-1（真原画）与 0（原画）不带 ratio。
        // 签名串里偶尔已带 ratio，重复追加会被 CDN 判为参数冲突，这里先判重。
        bool hasRatio = signedAnticode.Contains(BitRateParameterName, StringComparison.OrdinalIgnoreCase);
        string query = bitRate is > 0 && !hasRatio
            ? string.Concat(signedAnticode, BitRateParameterSeparator, BitRateParameterName, bitRate.Value.ToString(CultureInfo.InvariantCulture))
            : signedAnticode;
        string url = $"{baseUrl}/{streamName}.{suffix}?{query}";
        builder.TryAdd(
            url,
            format,
            VideoCodec.Avc,
            quality,
            referer: HuyaReferer,
            cdnHost: baseUri.Host);
    }

    /// <summary>
    /// 按虎牙 anticode 算法签名：ss=md5(seqid|ctype|t)、wsSecret=md5(fm 替换 $0..$3)，并移除 fm/txyp。
    /// </summary>
    /// <param name="anticode">原始 anticode 串。</param>
    /// <param name="streamName">流名称。</param>
    /// <param name="uid">匿名登录返回的 uid。</param>
    /// <returns>签名后的查询串（键值对保持原顺序，值不做 URL 编码）。</returns>
    /// <exception cref="ResolveException">必要参数缺失或 fm 无法解码时抛出。</exception>
    private string BuildAnticode(string anticode, string streamName, string uid)
    {
        Dictionary<string, List<string>> parsed = QueryStringParser.Parse(anticode);
        List<KeyValuePair<string, string>> pairs = [];
        foreach (KeyValuePair<string, List<string>> entry in parsed)
        {
            if (entry.Value.Count > 0)
            {
                pairs.Add(new KeyValuePair<string, string>(entry.Key, entry.Value[0]));
            }
        }

        string? ctype = QueryStringParser.GetFirst(parsed, AnticodeCtypeKey);
        string? time = QueryStringParser.GetFirst(parsed, AnticodeTimeKey);
        if (string.IsNullOrEmpty(ctype) || string.IsNullOrEmpty(time))
        {
            throw Fail(ResolveFailure.ParseError, AnticodeOperation, AnticodeMissingParamsDetail);
        }

        Upsert(pairs, AnticodeVerKey, AnticodeVerValue);
        Upsert(pairs, AnticodeSvKey, AnticodeSvValue);

        long sequenceId = long.Parse(uid, NumberStyles.Integer, CultureInfo.InvariantCulture)
            + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string sequenceIdText = sequenceId.ToString(CultureInfo.InvariantCulture);
        Upsert(pairs, AnticodeSeqIdKey, sequenceIdText);
        Upsert(pairs, AnticodeUidKey, uid);

        long nowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long uuid = ((nowMilliseconds % UuidTimeWindowMilliseconds * UuidMillisecondsScale)
            + Random.Shared.Next(0, UuidRandomUpperBound)) % UuidModulus;
        Upsert(pairs, AnticodeUuidKey, uuid.ToString(CultureInfo.InvariantCulture));

        string secret = Md5Hex($"{sequenceIdText}{AnticodeSecretSeparator}{ctype}{AnticodeSecretSeparator}{time}");

        string? wsTime = QueryStringParser.GetFirst(parsed, AnticodeWsTimeKey);
        if (string.IsNullOrEmpty(wsTime))
        {
            throw Fail(ResolveFailure.ParseError, AnticodeOperation, AnticodeMissingWsTimeDetail);
        }

        string? fm = QueryStringParser.GetFirst(parsed, AnticodeFmKey);
        if (string.IsNullOrEmpty(fm))
        {
            throw Fail(ResolveFailure.ParseError, AnticodeOperation, AnticodeFmDecodeFailedDetail);
        }

        string replacedFm = DecodeFmPlaceholders(fm, uid, streamName, secret, wsTime);
        Upsert(pairs, AnticodeWsSecretKey, Md5Hex(replacedFm));

        RemoveKey(pairs, AnticodeFmKey);
        RemoveKey(pairs, AnticodeTxypKey);

        StringBuilder builder = new();
        foreach (KeyValuePair<string, string> pair in pairs)
        {
            if (builder.Length > 0)
            {
                builder.Append(AnticodePairSeparator);
            }

            builder.Append(pair.Key).Append('=').Append(pair.Value);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 解码 fm 并依次替换 <c>$0</c>（uid）、<c>$1</c>（流名称）、<c>$2</c>（ss）、<c>$3</c>（wsTime）。
    /// </summary>
    /// <param name="fm">原始 fm 值（Base64）。</param>
    /// <param name="uid">匿名登录返回的 uid。</param>
    /// <param name="streamName">流名称。</param>
    /// <param name="secret">由 seqid/ctype/t 计算出的 ss。</param>
    /// <param name="wsTime">anticode 中的 wsTime。</param>
    /// <returns>替换后的 fm 文本。</returns>
    /// <exception cref="ResolveException">fm 不是合法 Base64 时抛出。</exception>
    private string DecodeFmPlaceholders(string fm, string uid, string streamName, string secret, string wsTime)
    {
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(fm));
        }
        catch (FormatException exception)
        {
            Logger.LogError(
                LogLevel.Error,
                ModuleName,
                "虎牙签名参数 fm 解码失败。",
                exception,
                new Dictionary<string, object?>
                {
                    ["operation"] = AnticodeOperation,
                    ["fingerprint"] = SensitiveData.Fingerprint(fm),
                });

            throw Fail(ResolveFailure.ParseError, AnticodeOperation, AnticodeFmDecodeFailedDetail, exception);
        }

        return decoded
            .Replace(AnticodeUidPlaceholder, uid, StringComparison.Ordinal)
            .Replace(AnticodeStreamNamePlaceholder, streamName, StringComparison.Ordinal)
            .Replace(AnticodeSecretPlaceholder, secret, StringComparison.Ordinal)
            .Replace(AnticodeTimePlaceholder, wsTime, StringComparison.Ordinal);
    }

    /// <summary>
    /// 计算文本的 MD5 十六进制小写摘要。
    /// </summary>
    /// <param name="text">待摘要文本（按 UTF-8 编码）。</param>
    /// <returns>32 位十六进制小写摘要。</returns>
    private static string Md5Hex(string text)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 把码率映射为画质档位；码率缺失时返回 <see cref="StreamQuality.Unknown"/>。
    /// </summary>
    /// <param name="bitRate">接口返回的 iBitRate（bps），可为 <see langword="null"/>。</param>
    /// <returns>画质档位。</returns>
    private static StreamQuality MapQuality(int? bitRate)
    {
        if (bitRate is null)
        {
            return StreamQuality.Unknown;
        }

        // -1 是"真原画"哨兵值，不是最小码率。
        if (bitRate < 0)
        {
            return StreamQuality.Hd1080HighFps;
        }

        if (bitRate == 0)
        {
            return StreamQuality.Hd1080;
        }

        if (bitRate >= HighFrameRate1080BitRateThreshold)
        {
            return StreamQuality.Hd1080HighFps;
        }

        if (bitRate >= FullHdBitRateThreshold)
        {
            return StreamQuality.Hd1080;
        }

        if (bitRate >= HighDefinitionBitRateThreshold)
        {
            return StreamQuality.Hd720;
        }

        return StreamQuality.Hd1080;
    }

    /// <summary>
    /// 有序写入键值：键已存在（忽略大小写）则原地覆盖，否则追加到末尾。
    /// </summary>
    /// <param name="pairs">有序键值对列表。</param>
    /// <param name="key">键名。</param>
    /// <param name="value">键值。</param>
    private static void Upsert(List<KeyValuePair<string, string>> pairs, string key, string value)
    {
        for (int index = 0; index < pairs.Count; index++)
        {
            if (string.Equals(pairs[index].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                pairs[index] = new KeyValuePair<string, string>(key, value);
                return;
            }
        }

        pairs.Add(new KeyValuePair<string, string>(key, value));
    }

    /// <summary>
    /// 删除指定键（忽略大小写）的全部键值对。
    /// </summary>
    /// <param name="pairs">有序键值对列表。</param>
    /// <param name="key">键名。</param>
    private static void RemoveKey(List<KeyValuePair<string, string>> pairs, string key)
    {
        for (int index = pairs.Count - 1; index >= 0; index--)
        {
            if (string.Equals(pairs[index].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                pairs.RemoveAt(index);
            }
        }
    }

    /// <summary>
    /// 解析 JSON 文本；失败时记录日志并抛出 <see cref="ResolveFailure.ParseError"/>。
    /// </summary>
    /// <param name="text">待解析文本。</param>
    /// <param name="operation">操作名。</param>
    /// <param name="detail">失败描述。</param>
    /// <returns>JSON 文档，调用方负责释放。</returns>
    /// <exception cref="ResolveException">文本不是合法 JSON 时抛出。</exception>
    private JsonDocument ParseJsonDocument(string text, string operation, string detail)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            Logger.LogError(
                LogLevel.Error,
                ModuleName,
                "虎牙响应 JSON 解析失败。",
                exception,
                new Dictionary<string, object?>
                {
                    ["operation"] = operation,
                    ["fingerprint"] = SensitiveData.Fingerprint(text),
                });

            throw Fail(ResolveFailure.ParseError, operation, detail, exception);
        }
    }

    /// <summary>
    /// 读取必需的对象字段；缺失或类型不符时抛出 <see cref="ResolveFailure.ParseError"/>。
    /// </summary>
    /// <param name="parent">父元素。</param>
    /// <param name="name">字段名。</param>
    /// <param name="operation">操作名。</param>
    /// <param name="detail">失败描述。</param>
    /// <returns>字段值（必定为对象）。</returns>
    /// <exception cref="ResolveException">字段缺失或类型不符时抛出。</exception>
    private JsonElement ReadRequiredObject(JsonElement parent, string name, string operation, string detail)
    {
        if (!TryGetProperty(parent, name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            throw Fail(ResolveFailure.ParseError, operation, detail);
        }

        return value;
    }

    /// <summary>
    /// 安全读取对象属性：父元素不是对象或字段缺失时返回 <see langword="false"/>。
    /// </summary>
    /// <param name="parent">父元素。</param>
    /// <param name="name">字段名。</param>
    /// <param name="value">字段值；失败时为 <see langword="default"/>。</param>
    /// <returns>读取成功返回 <see langword="true"/>。</returns>
    private static bool TryGetProperty(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value);
    }

    /// <summary>
    /// 读取可选字符串字段；字段缺失或类型不符时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="parent">父元素。</param>
    /// <param name="name">字段名。</param>
    /// <returns>字符串值或 <see langword="null"/>。</returns>
    private static string? ReadOptionalString(JsonElement parent, string name)
    {
        return TryGetProperty(parent, name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>
    /// 读取可选整数字段（兼容数字与数字字符串）；字段缺失或类型不符时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="parent">父元素。</param>
    /// <param name="name">字段名。</param>
    /// <returns>整数值或 <see langword="null"/>。</returns>
    private static int? ReadOptionalInt32(JsonElement parent, string name)
    {
        if (!TryGetProperty(parent, name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// 读取可选对象字段；字段缺失或类型不符时返回 <see langword="default"/>（<see cref="JsonValueKind.Undefined"/>）。
    /// </summary>
    /// <param name="parent">父元素。</param>
    /// <param name="name">字段名。</param>
    /// <returns>对象元素或 <see langword="default"/>。</returns>
    private static JsonElement ReadOptionalObject(JsonElement parent, string name)
    {
        return TryGetProperty(parent, name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;
    }
}
