namespace StreamPilot.Parsers.Douyin;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Parsers;
using StreamPilot.Core.Utilities;

/// <summary>
/// 抖音直播解析器。
/// </summary>
/// <remarks>
/// <para>
/// 主路径抓取 <c>live.douyin.com/{房间号}</c> 页面，按大括号配对扫描页面内嵌的 <c>roomStore</c>
/// 状态（支持原始 JSON、百分号编码的 <c>RENDER_DATA</c> 脚本块，以及被转义进 JS 字符串字面量的
/// React Flight 片段）；页面结构不可用（风控精简页、结构变更）或未给出可用地址时，回退到
/// webcast reflow 接口。
/// </para>
/// <para>
/// 不实现 <c>a_bogus</c>/<c>ms_token</c>/<c>__ac_signature</c> 等平台签名，也不绕验证码与风控
/// （见 docs/adr/0003-parser-contract.md 第 5 节）。reflow 接口补齐 <c>version_code</c>/<c>app_id</c>
/// 与可选的 <c>sec_user_id</c> 三个公开参数即可工作，无需签名；参数不齐时平台返回
/// <c>status_code=10011</c>（<c>Request params error</c>），本实现把它归类为"平台拒绝"。
/// </para>
/// </remarks>
internal sealed class DouyinParser : PlatformParserBase
{
    /// <summary>房间页地址前缀（不含斜杠，便于拼接房间号）。</summary>
    private const string RoomPageBase = "https://live.douyin.com";

    /// <summary>reflow 回退接口地址。</summary>
    private const string ReflowEndpoint = "https://webcast.amemv.com/webcast/room/reflow/info/";

    /// <summary>
    /// 抖音 Web 直播间进房接口地址（主要回退路径）。
    /// </summary>
    /// <remarks>
    /// 实测（2026-02，房间 745964462470）：该接口在只带 UA + Referer + 首页下发的
    /// <c>ttwid</c> 时返回 <c>status_code=0</c> 与完整房间 JSON；
    /// 而 <see cref="ReflowEndpoint"/> 即便补齐公开参数仍返回
    /// <c>status_code=10011 Request params error</c>。接口不需要任何平台签名。
    /// </remarks>
    private const string RoomEnterEndpoint = "https://live.douyin.com/webcast/room/web/enter/";

    /// <summary>进房接口的固定查询参数（与抖音网页端一致，不含任何签名参数）。</summary>
    private const string RoomEnterQuery =
        "?aid=6383&app_name=douyin_web&live_id=1&device_platform=web&language=zh-CN&enter_from=web_live"
        + "&cookie_enabled=true&screen_width=1920&screen_height=1080&browser_language=zh-CN"
        + "&browser_platform=Win32&browser_name=Chrome&browser_version=131.0.0.0&web_rid=";

    /// <summary>reflow 回退接口的固定查询串。</summary>
    private const string ReflowQuery = "?type_id=0&live_id=1&room_id=";

    /// <summary>reflow 回退接口的主播 <c>sec_uid</c> 参数前缀（未知时可省略）。</summary>
    private const string ReflowSecUidParameter = "&sec_user_id=";

    /// <summary>
    /// reflow 回退接口必需的固定参数。
    /// </summary>
    /// <remarks>
    /// 取自参考实现 MultiLive v10.4.16 的 <c>DouyinLiveResolver.TryResolveViaReflowApiAsync</c>
    /// （<c>MultiLiveLowLatency.dll</c>，IL 里拼装的三段字符串之一）：只补公开参数，
    /// 不做任何平台签名。
    /// </remarks>
    private const string ReflowFixedParameters = "&version_code=99.99.99&app_id=1128";

    /// <summary>抓取房间页与标识候选地址时使用的 Referer。</summary>
    private const string RoomUrlReferer = "https://live.douyin.com/";

    /// <summary>请求头名称：Cookie。</summary>
    private const string CookieHeaderName = "Cookie";

    /// <summary>日志模块名。</summary>
    private const string ModuleName = "Parsers.Douyin";

    /// <summary>房间页状态抽取的操作名。</summary>
    private const string RoomStateOperation = "room-state";

    /// <summary>reflow 回退接口的操作名。</summary>
    private const string ReflowOperation = "reflow-info";

    /// <summary>Web 进房接口的操作名。</summary>
    private const string RoomEnterOperation = "room-enter";

    /// <summary>播放地址抽取的操作名。</summary>
    private const string StreamUrlOperation = "stream-url";

    /// <summary>输入校验的操作名。</summary>
    private const string ValidateOperation = "validate";

    /// <summary>抖音"已结束直播"的房间状态值。</summary>
    private const int EndedRoomStatus = 4;

    /// <summary>内嵌 JSON 的引号字符。</summary>
    private const char QuoteCharacter = '"';

    /// <summary>内嵌 JSON 的转义字符。</summary>
    private const char EscapeCharacter = '\\';

    /// <summary>JSON 对象的开括号。</summary>
    private const char ObjectOpenCharacter = '{';

    /// <summary>JSON 对象的闭括号。</summary>
    private const char ObjectCloseCharacter = '}';

    /// <summary>JS 转义里的正斜杠字符。</summary>
    private const char SlashCharacter = '/';

    /// <summary>JS 转义 <c>\uXXXX</c> 的前导字符。</summary>
    private const char UnicodeEscapePrefix = 'u';

    /// <summary>JS 转义 <c>\uXXXX</c> 的十六进制位数。</summary>
    private const int UnicodeEscapeHexDigits = 4;

    /// <summary>
    /// JS 字符串里"反斜杠 + 引号"的语义周期：每 4 个连续反斜杠重复一次
    /// （<c>\"</c> 是裸引号、<c>\\</c> 是裸反斜杠、<c>\\\"</c> 是转义引号）。
    /// </summary>
    private const int BackslashEscapeCycle = 4;

    /// <summary>找不到外层对象时的返回值。</summary>
    private const int MissingObjectStart = -1;

    /// <summary>RENDER_DATA 脚本块的正则。</summary>
    /// <remarks>与参考实现 MultiLive 的静态正则一致（<c>DouyinLiveResolver::.cctor</c>）。</remarks>
    private static readonly Regex RenderDataPattern = new(
        """<script[^>]+id=["']RENDER_DATA["'][^>]*>(?<data>.*?)</script>""",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>RENDER_DATA 脚本块内容在正则里的捕获组名。</summary>
    private const string RenderDataGroupName = "data";

    /// <summary>
    /// 主播 <c>sec_uid</c> 的抽取正则：同时兼容未转义（<c>"sec_uid":"..."</c>）
    /// 与转义（<c>\"sec_uid\":\"...\"</c>）两种页面写法。
    /// </summary>
    /// <remarks>等价于参考实现里的 <c>"sec_uid":"(?&lt;sec&gt;[A-Za-z0-9_\-]{20,})"</c>。</remarks>
    private static readonly Regex SecUidPattern = new(
        @"sec_uid\\?"":\\?""(?<sec>[A-Za-z0-9_\-]{20,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary><c>sec_uid</c> 在正则里的捕获组名。</summary>
    private const string SecUidGroupName = "sec";

    /// <summary>
    /// 风控/验证码中间页的判别标记。
    /// </summary>
    /// <remarks>
    /// 参考实现（MultiLive v10.4.16 <c>DouyinLiveResolver</c>）用同样两个标记做诊断：
    /// <c>__ac_nonce</c> 与"验证码中间页"文案。这里只用它生成可读的失败原因，
    /// 不参与任何绕过逻辑。
    /// </remarks>
    private static readonly Regex SecurityChallengePattern = new(
        "__ac_nonce|验证码中间页",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>页面与 reflow 接口均不可用时的提示。</summary>
    private const string NoStreamMessage = "抖音直播间页面与 reflow 接口均未返回可用播放地址。";

    /// <summary>页面返回风控/验证码中间页且所有路径都失败时的提示。</summary>
    private const string SecurityChallengeMessage =
        "抖音返回了浏览器安全验证页（含 __ac_nonce），本程序不绕过验证码与风控；请稍后重试或改用官方直播页。";

    /// <summary>两条路径都拿到了状态但没有可用地址时的提示。</summary>
    private const string NoCandidateMessage = "抖音未返回可用播放地址。";

    /// <summary>无法从输入确定房间号时的提示。</summary>
    private const string MissingRoomIdMessage = "无法从输入中确定抖音直播间房间号。";

    /// <summary>主播名缺失时的提示。</summary>
    private const string MissingAnchorMessage = "抖音响应缺少主播名（anchor 或 user.nickname 为空）。";

    /// <summary>房间不存在的提示模板（参数为房间号）。</summary>
    private const string RoomNotFoundDetailFormat = "抖音未找到房间 {0}：响应缺少主播信息，房间可能不存在或已注销。";

    /// <summary>房间页状态显示未开播时的提示。</summary>
    private const string PageNotLiveMessage = "抖音房间页状态显示未开播（roomStore.roomInfo.room 为空）。";

    /// <summary>reflow 响应显示未开播时的提示。</summary>
    private const string ReflowNotLiveMessage = "抖音 reflow 响应显示未开播（room 为空）。";

    /// <summary>房间状态显示已结束时的提示模板（参数为操作名与 room.status）。</summary>
    private const string EndedRoomDetailFormat = "抖音直播间已结束（来源={0}，room.status={1}）。";

    /// <summary>缺少播放地址时的提示模板（参数为操作名）。</summary>
    private const string NotLiveDetailFormat = "抖音响应缺少 stream_url，直播间可能未开播（来源={0}）。";

    /// <summary>平台拒绝 reflow 请求时的提示模板（参数为 status_code 与平台 message）。</summary>
    private const string ReflowRejectedDetailFormat =
        "抖音备用接口拒绝了本次请求：status_code={0}，message={1}。该接口按公开参数调用，不做平台签名。";

    /// <summary>平台拒绝进房请求时的提示模板（参数为 status_code 与平台 message）。</summary>
    private const string RoomEnterRejectedDetailFormat =
        "抖音进房接口拒绝了本次请求：status_code={0}，message={1}。该接口按公开参数调用，不做平台签名。";

    /// <summary>进房接口返回空响应体时的日志说明（实测：缺少 ttwid 时 HTTP 200 但正文为空）。</summary>
    private const string RoomEnterEmptyBodyMessage =
        "抖音进房接口返回空响应体（通常是缺少首页下发的 ttwid 会话 cookie），回退到下一条路径。";

    /// <summary>进房接口显示未开播时的提示。</summary>
    private const string RoomEnterNotLiveMessage = "抖音进房响应显示未开播（room 为空）。";

    /// <summary>平台未给出失败消息时的占位文本。</summary>
    private const string ReflowMessageUnknown = "（平台未给出 message）";

    /// <summary>画质档位键：原画（最高档）。</summary>
    private const string QualityKeyOrigin = "origin";

    /// <summary>画质档位键：真原画。</summary>
    private const string QualityKeyRealOrigin = "real_origin";

    /// <summary>画质档位键：蓝光。</summary>
    private const string QualityKeyUhd = "uhd";

    /// <summary>画质档位键：超清。</summary>
    private const string QualityKeyHd = "hd";

    /// <summary>画质档位键：高清。</summary>
    private const string QualityKeySd = "sd";

    /// <summary>画质档位键：标清。</summary>
    private const string QualityKeyLd = "ld";

    /// <summary>画质档位键：纯音频流（平台声明的档位之一）。</summary>
    private const string QualityKeyAudioOnly = "ao";

    /// <summary>码率单位判定阈值：不小于该值视为 bps（需换算为 kbps）。</summary>
    private const int BitrateBpsThreshold = 1000;

    /// <summary>1 kbps 对应的比特数。</summary>
    private const int BitsPerKilobit = 1000;

    /// <summary>已知拉流档位键（从高到低）：官方新档位键在前，老式分辨率键在后。</summary>
    /// <remarks>
    /// 抖音官方档位从高到低是 原画 → 蓝光 → 超清 → 高清 → 标清
    /// （<c>origin</c>/<c>real_origin</c> → <c>uhd</c> → <c>hd</c> → <c>sd</c> → <c>ld</c>）；
    /// 老式的 <c>FULL_HD1/HD1/SD1/SD2</c> 来自 <c>flv_pull_url</c> 与 <c>hls_pull_url_map</c> 的映射键
    /// （参考实现 <c>DouyinLiveResolver.ReadPreferredFlvQuality</c> 的键序为
    /// <c>ORIGIN → FULL_HD1 → UHD → HD1 → SD1 → SD2</c>）。
    /// 纯音频档 <c>ao</c> 排在最后，避免有视频档位时被当成"最高档"选中；
    /// 不在表内的键排在最后并保持响应中的顺序。
    /// </remarks>
    private static readonly string[] KnownQualityKeyOrder =
    [
        QualityKeyOrigin,
        QualityKeyRealOrigin,
        QualityKeyUhd,
        QualityKeyHd,
        QualityKeySd,
        QualityKeyLd,
        QualityNames.DouyinQualityFullHd1,
        QualityNames.DouyinQualityHd1,
        QualityNames.DouyinQualitySd1,
        QualityNames.DouyinQualitySd2,
        QualityKeyAudioOnly,
    ];

    /// <summary>roomStore 字段名。</summary>
    private const string RoomStoreField = "roomStore";

    /// <summary>roomInfo 字段名。</summary>
    private const string RoomInfoField = "roomInfo";

    /// <summary>anchor 字段名。</summary>
    private const string AnchorField = "anchor";

    /// <summary>room 字段名。</summary>
    private const string RoomField = "room";

    /// <summary>user 字段名（reflow 接口的主播对象）。</summary>
    private const string UserField = "user";

    /// <summary>nickname 字段名。</summary>
    private const string NicknameField = "nickname";

    /// <summary>title 字段名。</summary>
    private const string TitleField = "title";

    /// <summary>status 字段名。</summary>
    private const string StatusField = "status";

    /// <summary>stream_url 字段名。</summary>
    private const string StreamUrlField = "stream_url";

    /// <summary>flv_pull_url 字段名。</summary>
    private const string FlvPullUrlField = "flv_pull_url";

    /// <summary>hls_pull_url_map 字段名。</summary>
    private const string HlsPullUrlMapField = "hls_pull_url_map";

    /// <summary>partition_road_map 字段名。</summary>
    private const string PartitionRoadMapField = "partition_road_map";

    /// <summary>sub_partition 字段名。</summary>
    private const string SubPartitionField = "sub_partition";

    /// <summary>partition 字段名。</summary>
    private const string PartitionField = "partition";

    /// <summary>data 字段名（reflow 接口的外层包装）。</summary>
    private const string DataField = "data";

    /// <summary>message 字段名（reflow 接口的失败原因）。</summary>
    private const string MessageField = "message";

    /// <summary>JSON 字段 status_code（平台拒绝时返回非 0）。</summary>
    private const string StatusCodeField = "status_code";

    /// <summary>options 字段名（档位声明的外层对象）。</summary>
    private const string OptionsField = "options";

    /// <summary>qualities 字段名（档位声明数组）。</summary>
    private const string QualitiesField = "qualities";

    /// <summary>sdk_key 字段名（档位声明里的档位键）。</summary>
    private const string SdkKeyField = "sdk_key";

    /// <summary>name 字段名（档位声明里的官方中文档位名）。</summary>
    private const string QualityNameField = "name";

    /// <summary>v_bit_rate 字段名（档位声明里的视频码率）。</summary>
    private const string VideoBitRateField = "v_bit_rate";

    /// <summary>pull_datas 字段名（双屏/多路场景的档位与地址来源）。</summary>
    private const string PullDatasField = "pull_datas";

    /// <summary>live_core_sdk_data 字段名（常规场景的档位与地址来源）。</summary>
    private const string LiveCoreSdkDataField = "live_core_sdk_data";

    /// <summary>pull_data 字段名（live_core_sdk_data 下的档位与地址对象）。</summary>
    private const string PullDataField = "pull_data";

    /// <summary>抖音链接里可能出现的房间号查询参数名（按优先级）。</summary>
    private static readonly string[] RoomIdQueryParameterNames = ["live_web_rid", "web_rid", "room_id", "roomid"];

    private readonly HttpTextClient _http;
    private readonly DouyinWebSession _session;

    /// <summary>初始化解析器。</summary>
    /// <param name="http">带超时与有界重试的 HTTP 客户端。</param>
    /// <param name="httpClientFactory">HTTP 客户端工厂（用于取得抖音首页下发的会话 cookie）。</param>
    /// <param name="logger">结构化日志。</param>
    public DouyinParser(HttpTextClient http, HttpClientFactory httpClientFactory, IStructuredLogger logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _http = http;
        _session = new DouyinWebSession(httpClientFactory, logger);
    }

    /// <inheritdoc />
    public override PlatformId Platform => PlatformId.Douyin;

    /// <inheritdoc />
    public override string DisplayName => "抖音";

    /// <inheritdoc />
    public override string RoomUrlPrefix => "https://live.douyin.com/";

    /// <summary>页面内嵌 JSON 的转义形态。</summary>
    private enum EmbeddedJsonMode
    {
        /// <summary>原始 JSON：字符串引号就是双引号本身。</summary>
        Raw,

        /// <summary>JSON 被转义进 JS 字符串字面量：字符串引号写作反斜杠加双引号。</summary>
        JavaScriptString,
    }

    /// <inheritdoc />
    protected override async Task<ResolvedRoom> OnParseAsync(RoomQuery query, CancellationToken cancellationToken)
    {
        // 用户自备 Cookie 只作用于本次解析的请求（播放地址与中继都不带它）。
        using IDisposable cookieScope = _http.UseCookie(query.Cookie);
        string roomId = ResolveRoomId(query);
        StreamCandidateBuilder builder = new(Platform, Logger);

        // 页面只抓一次：主路径解析状态、备用路径取 sec_uid 都从这份 HTML 上做。
        string html = await FetchRoomPageAsync(roomId, cancellationToken).ConfigureAwait(false);
        ResolvedRoom? room = TryParseRoomPage(html, roomId, query.PreferredQualityKey, builder);
        room ??= await TryParseRoomEnterAsync(roomId, query.PreferredQualityKey, builder, cancellationToken)
            .ConfigureAwait(false);
        room ??= await TryParseReflowAsync(
            roomId,
            ExtractAnchorSecUid(html),
            query.PreferredQualityKey,
            builder,
            cancellationToken).ConfigureAwait(false);

        if (room is null)
        {
            // 页面是风控/验证码中间页时，原因是"平台拒绝"而不是"接口结构变了"；
            // 两种情况都不绕过验证，只是把失败原因说清楚（来源：参考实现的 hasSecurityChallenge）。
            throw HasSecurityChallenge(html)
                ? Fail(ResolveFailure.Rejected, RoomStateOperation, SecurityChallengeMessage)
                : Fail(ResolveFailure.ParseError, ReflowOperation, NoStreamMessage);
        }

        if (builder.Count == 0)
        {
            throw Fail(ResolveFailure.NotLive, StreamUrlOperation, NoCandidateMessage);
        }

        return room;
    }

    /// <summary>解析输入中的房间号：优先使用房间号，其次从直播间链接提取。</summary>
    /// <param name="query">已校验的房间查询条件。</param>
    /// <returns>房间号。</returns>
    /// <remarks>房间号为空且链接无法提取时，抛出带 <c>InvalidInput</c> 分类的解析异常。</remarks>
    private string ResolveRoomId(RoomQuery query)
    {
        string? roomId = string.IsNullOrWhiteSpace(query.RoomId)
            ? ResolveRoomIdFromUrl(query.RoomUrl)
            : query.RoomId;

        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw Fail(ResolveFailure.InvalidInput, ValidateOperation, MissingRoomIdMessage);
        }

        return roomId;
    }

    /// <summary>
    /// 从抖音链接中取房间号：路径片段优先，其次读查询参数里的 <c>live_web_rid</c>/<c>web_rid</c>/<c>room_id</c>。
    /// </summary>
    /// <param name="roomUrl">直播间链接。</param>
    /// <returns>房间号；取不到时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 抖音分享出来的链接经常是"首页 + 一串参数"的形式（<c>https://live.douyin.com/?live_web_rid=...</c>），
    /// 路径里没有房间号，只能从查询参数取，否则会误报"无法确定房间号"。
    /// </remarks>
    private static string? ResolveRoomIdFromUrl(string? roomUrl)
    {
        string? fromPath = TryExtractRoomIdFromUrl(roomUrl);
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return fromPath;
        }

        if (string.IsNullOrWhiteSpace(roomUrl) || !Uri.TryCreate(roomUrl, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        Dictionary<string, List<string>> parsed = QueryStringParser.Parse(uri.Query);
        foreach (string name in RoomIdQueryParameterNames)
        {
            if (parsed.TryGetValue(name, out List<string>? values) && values.Count > 0)
            {
                string value = values[0].Trim();
                if (value.Length > 0 && IsAllowedRoomIdValue(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>校验从查询参数取到的房间号：只允许数字。</summary>
    /// <param name="value">候选值。</param>
    /// <returns>合法返回 <see langword="true"/>。</returns>
    private static bool IsAllowedRoomIdValue(string value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>抓取抖音直播间页面 HTML。</summary>
    /// <param name="roomId">房间号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>页面 HTML 文本。</returns>
    private async Task<string> FetchRoomPageAsync(string roomId, CancellationToken cancellationToken)
    {
        HttpRequestSpec spec = new()
        {
            Url = string.Concat(RoomPageBase, "/", roomId),
            Platform = Platform,
            Operation = RoomStateOperation,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Referer"] = RoomUrlReferer,
            },
        };

        return await _http.GetStringAsync(spec, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 走主路径：解析房间页内嵌状态并组装结果。
    /// </summary>
    /// <param name="html">房间页 HTML。</param>
    /// <param name="roomId">房间号。</param>
    /// <param name="preferredQualityKey">调用方指定的档位键；为空时取最高档。</param>
    /// <param name="builder">候选构造器。</param>
    /// <returns>解析结果；页面结构不可用或没有可用地址时返回 <see langword="null"/> 以便回退 reflow。</returns>
    private ResolvedRoom? TryParseRoomPage(
        string html,
        string roomId,
        string? preferredQualityKey,
        StreamCandidateBuilder builder)
    {
        using JsonDocument? document = ExtractRoomStoreDocument(html);
        if (document is null)
        {
            Logger.Debug(ModuleName, "抖音房间页未找到可解析的 roomStore 状态，回退 reflow 接口。", new Dictionary<string, object?>
            {
                ["operation"] = RoomStateOperation,
                ["roomId"] = roomId,
            });
            return null;
        }

        JsonElement roomInfo = GetNestedProperty(document.RootElement, RoomStoreField, RoomInfoField);
        JsonElement anchor = GetPropertyOrUndefined(roomInfo, AnchorField);
        if (anchor.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw Fail(
                ResolveFailure.RoomNotFound,
                RoomStateOperation,
                string.Format(CultureInfo.InvariantCulture, RoomNotFoundDetailFormat, roomId));
        }

        string? anchorName = ReadString(anchor, NicknameField);
        if (string.IsNullOrWhiteSpace(anchorName))
        {
            throw Fail(ResolveFailure.ParseError, RoomStateOperation, MissingAnchorMessage);
        }

        JsonElement room = GetPropertyOrUndefined(roomInfo, RoomField);
        if (room.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw Fail(ResolveFailure.NotLive, RoomStateOperation, PageNotLiveMessage);
        }

        (IReadOnlyList<QualityOption> qualities, string? selectedQualityKey) =
            BuildQualityOptions(room, preferredQualityKey);
        bool added = CollectCandidates(
            builder,
            room,
            RoomStateOperation,
            selectedQualityKey,
            out string title,
            out string category);
        return added ? CreateRoom(roomId, anchorName, title, category, builder, qualities, selectedQualityKey) : null;
    }

    /// <summary>从房间页 HTML 中解析出包含 <c>roomStore.roomInfo</c> 的状态文档。</summary>
    /// <param name="html">页面 HTML。</param>
    /// <returns>JSON 文档（调用方负责释放）；页面没有可解析的状态时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 抖音在不同时期把状态放在不同位置：早期的 <c>&lt;script id="RENDER_DATA"&gt;</c>（百分号编码 JSON）、
    /// 现在的 React Flight（<c>self.__pace_f.push(...)</c>，JSON 被转义进 JS 字符串）、
    /// 以及少量直接内联的原始 JSON。这里逐个来源、逐个 <c>roomStore</c> 出现位置尝试，
    /// 只有能解析且真的含有 <c>roomStore.roomInfo</c> 才接受，避免把无关片段当成状态。
    /// </remarks>
    internal JsonDocument? ExtractRoomStoreDocument(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        foreach (string source in EnumerateEmbeddedJsonSources(html))
        {
            JsonDocument? document = ExtractRoomStoreDocumentFrom(source);
            if (document is not null)
            {
                return document;
            }
        }

        return null;
    }

    /// <summary>枚举可能承载 <c>roomStore</c> 状态的文本来源。</summary>
    /// <param name="html">页面 HTML。</param>
    /// <returns>候选文本（整页 HTML 在前，解码后的 RENDER_DATA 在后）。</returns>
    private static IEnumerable<string> EnumerateEmbeddedJsonSources(string html)
    {
        yield return html;

        Match match = RenderDataPattern.Match(html);
        if (!match.Success)
        {
            yield break;
        }

        string decoded = QueryStringParser.DecodeComponentStrict(match.Groups[RenderDataGroupName].Value);
        if (decoded.Length > 0 && !string.Equals(decoded, html, StringComparison.Ordinal))
        {
            yield return decoded;
        }
    }

    /// <summary>在单个文本来源里定位并解析 <c>roomStore</c> 状态对象。</summary>
    /// <param name="source">候选文本（整页 HTML 或解码后的 RENDER_DATA）。</param>
    /// <returns>JSON 文档（调用方负责释放）；文本里没有可用状态时返回 <see langword="null"/>。</returns>
    private JsonDocument? ExtractRoomStoreDocumentFrom(string source)
    {
        int searchFrom = 0;
        while (searchFrom < source.Length)
        {
            int index = source.IndexOf(RoomStoreField, searchFrom, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            searchFrom = index + RoomStoreField.Length;
            string? json = TrySliceRoomStoreJson(source, index);
            if (json is null)
            {
                continue;
            }

            JsonDocument? document = TryParseDocument(json, RoomStateOperation);
            if (document is null)
            {
                continue;
            }

            JsonElement roomInfo = GetNestedProperty(document.RootElement, RoomStoreField, RoomInfoField);
            if (roomInfo.ValueKind == JsonValueKind.Object && HasAnyProperty(roomInfo))
            {
                return document;
            }

            document.Dispose();
        }

        return null;
    }

    /// <summary>
    /// 判断 JSON 对象是否至少含有一个属性。
    /// </summary>
    /// <param name="element">待判断元素。</param>
    /// <returns>含有属性返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 抖音房间页里 <c>roomStore</c> 会出现两次（实测房间 745964462470）：
    /// 前一个在页面早期的 React Flight 片段里，<c>roomInfo</c> 是空对象 <c>{}</c>；
    /// 后一个才是真正的房间状态，<c>roomInfo</c> 含 <c>anchor</c> 与 <c>room</c>。
    /// 若接受空对象就会把可用房间误判成"房间不存在"，因此这里要求非空。
    /// </remarks>
    private static bool HasAnyProperty(JsonElement element)
    {
        foreach (JsonProperty _ in element.EnumerateObject())
        {
            return true;
        }

        return false;
    }

    /// <summary>把 <c>roomStore</c> 键名所在的那个 JSON 对象整段切出来（必要时做一次正确反转义）。</summary>
    /// <param name="source">候选文本。</param>
    /// <param name="keyIndex"><c>roomStore</c> 在文本中的下标。</param>
    /// <returns>可直接交给 <c>JsonDocument.Parse(string)</c> 的 JSON 文本；无法配对时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 不做"正则截一段 + 全局把 <c>\"</c> 换成 <c>"</c>"：那种写法会在片段含二次转义
    /// （<c>\\</c>、<c>\\uXXXX</c>）或值里含引号时破坏 JSON 结构。这里改成按字符状态机做
    /// 大括号配对，并且只对"整段被转义进 JS 字符串"的形态做一次 <see cref="UnescapeJavaScriptString"/>。
    /// </remarks>
    private static string? TrySliceRoomStoreJson(string source, int keyIndex)
    {
        if (!TryGetEmbeddedJsonMode(source, keyIndex, out EmbeddedJsonMode mode))
        {
            return null;
        }

        int start = FindEnclosingObjectStart(source, keyIndex, mode);
        if (start == MissingObjectStart)
        {
            return null;
        }

        string? json = SliceBalancedObject(source, start, mode);
        if (json is null)
        {
            return null;
        }

        return mode == EmbeddedJsonMode.JavaScriptString ? UnescapeJavaScriptString(json) : json;
    }

    /// <summary>判断 <c>roomStore</c> 的出现位置是否是被引号界定的键名，并给出其转义模式。</summary>
    /// <param name="source">候选文本。</param>
    /// <param name="keyIndex"><c>roomStore</c> 在文本中的下标。</param>
    /// <param name="mode">键名所处的转义模式。</param>
    /// <returns>是键名返回 <see langword="true"/>；只是普通文本（例如值或变量名的一部分）时返回 <see langword="false"/>。</returns>
    /// <remarks>
    /// 原始 JSON 写作 <c>"roomStore"</c>；被转义进 JS 字符串的字面量写作 <c>\"roomStore\"</c>，
    /// 此时键名后面的引号前面多一个反斜杠，收尾引号的下标要往后挪一位。
    /// </remarks>
    private static bool TryGetEmbeddedJsonMode(string source, int keyIndex, out EmbeddedJsonMode mode)
    {
        mode = EmbeddedJsonMode.Raw;
        int beforeIndex = keyIndex - 1;
        if (beforeIndex < 0 || source[beforeIndex] != QuoteCharacter)
        {
            return false;
        }

        // 键名前一个引号本身被反斜杠转义，说明整段 JSON 被塞进了 JS 字符串字面量。
        bool escaped = beforeIndex > 0 && source[beforeIndex - 1] == EscapeCharacter;
        int afterIndex = keyIndex + RoomStoreField.Length;
        int afterQuoteIndex = escaped ? afterIndex + 1 : afterIndex;
        if (afterQuoteIndex >= source.Length
            || source[afterQuoteIndex] != QuoteCharacter
            || (escaped && source[afterIndex] != EscapeCharacter))
        {
            return false;
        }

        mode = escaped ? EmbeddedJsonMode.JavaScriptString : EmbeddedJsonMode.Raw;
        return true;
    }

    /// <summary>找到包含指定下标的、最内层的 JSON 对象开括号位置。</summary>
    /// <param name="text">候选文本。</param>
    /// <param name="endIndex">目标下标（键名起点）。</param>
    /// <param name="mode">内嵌 JSON 的转义模式。</param>
    /// <returns>开括号下标；之前没有未闭合的对象时返回 <see cref="MissingObjectStart"/>。</returns>
    private static int FindEnclosingObjectStart(string text, int endIndex, EmbeddedJsonMode mode)
    {
        Stack<int> openBraces = new();
        bool inString = false;
        for (int index = 0; index < endIndex; index++)
        {
            if (IsLogicalQuote(text, index, mode))
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (text[index] == ObjectOpenCharacter)
            {
                openBraces.Push(index);
                continue;
            }

            if (text[index] == ObjectCloseCharacter && openBraces.Count > 0)
            {
                openBraces.Pop();
            }
        }

        return openBraces.Count > 0 ? openBraces.Peek() : MissingObjectStart;
    }

    /// <summary>从对象开括号开始按大括号配对切出整段 JSON。</summary>
    /// <param name="text">候选文本。</param>
    /// <param name="startIndex">对象开括号下标。</param>
    /// <param name="mode">内嵌 JSON 的转义模式。</param>
    /// <returns>配对成功的 JSON 文本（含首尾大括号）；括号不配平时返回 <see langword="null"/>。</returns>
    private static string? SliceBalancedObject(string text, int startIndex, EmbeddedJsonMode mode)
    {
        int depth = 0;
        bool inString = false;
        for (int index = startIndex; index < text.Length; index++)
        {
            if (IsLogicalQuote(text, index, mode))
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            char current = text[index];
            if (current == ObjectOpenCharacter)
            {
                depth++;
                continue;
            }

            if (current != ObjectCloseCharacter)
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return text[startIndex..(index + 1)];
            }
        }

        return null;
    }

    /// <summary>判断某个位置的引号是否是"逻辑引号"（成对界定字符串的那个引号）。</summary>
    /// <param name="text">候选文本。</param>
    /// <param name="index">待判定的下标。</param>
    /// <param name="mode">内嵌 JSON 的转义模式。</param>
    /// <returns>是逻辑引号返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 原始 JSON：前面有偶数个反斜杠的引号才是逻辑引号（<c>\"</c> 是转义引号，不是定界符）。
    /// 被转义进 JS 字符串时，JSON 的引号写作 <c>\"</c>、JSON 的反斜杠写作 <c>\\</c>，
    /// 于是连续 n 个反斜杠加引号的含义按周期 4 变化：<c>n ≡ 1 (mod 4)</c> 才是裸引号（逻辑引号），
    /// <c>n = 0</c> 是 JS 字符串自身的边界，<c>n ≡ 3 (mod 4)</c>（例如 <c>\\\"</c>）是 JSON 里的转义引号。
    /// 这样 <c>\"</c>、<c>\\</c>、<c>\\uXXXX</c> 都能被一致处理，不需要预先 replace。
    /// </remarks>
    private static bool IsLogicalQuote(string text, int index, EmbeddedJsonMode mode)
    {
        if (text[index] != QuoteCharacter)
        {
            return false;
        }

        int backslashes = CountPrecedingBackslashes(text, index);
        int remainder = backslashes % BackslashEscapeCycle;
        return mode == EmbeddedJsonMode.JavaScriptString ? remainder == 1 : remainder == 0;
    }

    /// <summary>统计某个位置之前的连续反斜杠个数。</summary>
    /// <param name="text">候选文本。</param>
    /// <param name="index">目标下标。</param>
    /// <returns>连续反斜杠个数。</returns>
    private static int CountPrecedingBackslashes(string text, int index)
    {
        int count = 0;
        for (int cursor = index - 1; cursor >= 0 && text[cursor] == EscapeCharacter; cursor--)
        {
            count++;
        }

        return count;
    }

    /// <summary>把 JS 字符串字面量里的转义还原为真正的 JSON 文本。</summary>
    /// <param name="value">JS 字符串字面量内容（不含最外层引号）。</param>
    /// <returns>反转义后的文本。</returns>
    /// <remarks>
    /// 只处理 JSON 会用到的转义（<c>\"</c> <c>\\</c> <c>\/</c> <c>\b</c> <c>\f</c> <c>\n</c>
    /// <c>\r</c> <c>\t</c> <c>\uXXXX</c>）：二次转义 <c>\\uXXXX</c> 被还原成 <c>\uXXXX</c> 交给
    /// <see cref="JsonDocument"/> 处理；未知转义按 JS 语义丢掉反斜杠保留字符，
    /// 避免一个怪字符就让整页状态不可用。
    /// </remarks>
    private static string UnescapeJavaScriptString(string value)
    {
        StringBuilder builder = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != EscapeCharacter || index + 1 >= value.Length)
            {
                builder.Append(current);
                continue;
            }

            index++;
            char escape = value[index];
            switch (escape)
            {
                case QuoteCharacter:
                case EscapeCharacter:
                case SlashCharacter:
                    builder.Append(escape);
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case UnicodeEscapePrefix:
                    index = AppendUnicodeEscape(value, index, builder);
                    break;
                default:
                    builder.Append(escape);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>把 <c>\uXXXX</c> 还原成一个字符。</summary>
    /// <param name="value">JS 字符串字面量内容。</param>
    /// <param name="prefixIndex"><c>u</c> 在文本中的下标。</param>
    /// <param name="builder">输出缓冲。</param>
    /// <returns>最后被消费掉的下标；十六进制不完整时只消费 <c>u</c>。</returns>
    private static int AppendUnicodeEscape(string value, int prefixIndex, StringBuilder builder)
    {
        int hexStart = prefixIndex + 1;
        if (hexStart + UnicodeEscapeHexDigits > value.Length)
        {
            builder.Append(UnicodeEscapePrefix);
            return prefixIndex;
        }

        if (!ushort.TryParse(
                value.Substring(hexStart, UnicodeEscapeHexDigits),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out ushort code))
        {
            builder.Append(UnicodeEscapePrefix);
            return prefixIndex;
        }

        builder.Append((char)code);
        return hexStart + UnicodeEscapeHexDigits - 1;
    }

    /// <summary>从房间页 HTML 中取主播的 <c>sec_uid</c>（reflow 备用接口的可选参数）。</summary>
    /// <param name="html">页面 HTML。</param>
    /// <returns><c>sec_uid</c>；页面未给出时返回 <see langword="null"/>。</returns>
    internal static string? ExtractAnchorSecUid(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        Match match = SecUidPattern.Match(html);
        return match.Success ? match.Groups[SecUidGroupName].Value : null;
    }

    /// <summary>判断页面是否是风控/验证码中间页。</summary>
    /// <param name="html">页面 HTML。</param>
    /// <returns>命中风控标记返回 <see langword="true"/>。</returns>
    /// <remarks>只用于把失败原因说清楚，不做任何绕过验证码或风控的处理。</remarks>
    internal static bool HasSecurityChallenge(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return SecurityChallengePattern.IsMatch(html);
    }

    /// <summary>解析 JSON 文本；非法 JSON 只记 Debug 日志并返回 <see langword="null"/>。</summary>
    /// <param name="json">JSON 文本。</param>
    /// <param name="operation">操作名，用于日志上下文。</param>
    /// <returns>JSON 文档（调用方负责释放）；解析失败时返回 <see langword="null"/>。</returns>
    private JsonDocument? TryParseDocument(string json, string operation)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            Logger.Debug(ModuleName, "抖音返回的状态不是合法 JSON。", new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["error"] = exception.Message,
            });
            return null;
        }
    }

    /// <summary>抓取 reflow 接口的响应文本。</summary>
    /// <param name="roomId">房间号。</param>
    /// <param name="secUid">主播 <c>sec_uid</c>；未知时省略该参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应文本。</returns>
    private async Task<string> FetchReflowTextAsync(string roomId, string? secUid, CancellationToken cancellationToken)
    {
        HttpRequestSpec spec = new()
        {
            Url = BuildReflowUrl(roomId, secUid),
            Platform = Platform,
            Operation = ReflowOperation,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Referer"] = RoomUrlReferer,
            },
        };

        return await _http.GetStringAsync(spec, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>拼接 reflow 备用接口的完整地址。</summary>
    /// <param name="roomId">房间号（已由输入校验限定为数字或字母数字短号，无需再转义）。</param>
    /// <param name="secUid">主播 <c>sec_uid</c>；为空时省略该参数。</param>
    /// <returns>完整请求地址。</returns>
    /// <remarks>
    /// <c>version_code=99.99.99&amp;app_id=1128</c> 取自参考实现 MultiLive v10.4.16 的
    /// <c>DouyinLiveResolver.TryResolveViaReflowApiAsync</c>：该接口缺这些参数会返回
    /// <c>status_code=10011</c>（<c>Request params error</c>）。只补公开参数，不做平台签名。
    /// </remarks>
    internal static string BuildReflowUrl(string roomId, string? secUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        string secUidParameter = string.IsNullOrWhiteSpace(secUid)
            ? string.Empty
            : string.Concat(ReflowSecUidParameter, secUid);

        return string.Concat(ReflowEndpoint, ReflowQuery, roomId, secUidParameter, ReflowFixedParameters);
    }

    /// <summary>
    /// 走回退路径：解析 reflow 接口响应并组装结果。
    /// </summary>
    /// <param name="roomId">房间号。</param>
    /// <param name="secUid">主播 <c>sec_uid</c>；未知时为 <see langword="null"/>。</param>
    /// <param name="preferredQualityKey">调用方指定的档位键；为空时取最高档。</param>
    /// <param name="builder">候选构造器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析结果；reflow 响应不可用时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 平台拒绝（<c>status_code</c> 非 0）在这里直接按 <c>Rejected</c> 抛出，与"房间不存在""未开播"
    /// 严格区分；只有响应本身无法解析时才回落到通用的 <c>ParseError</c>。
    /// </remarks>
    private async Task<ResolvedRoom?> TryParseReflowAsync(
        string roomId,
        string? secUid,
        string? preferredQualityKey,
        StreamCandidateBuilder builder,
        CancellationToken cancellationToken)
    {
        string text = await FetchReflowTextAsync(roomId, secUid, cancellationToken).ConfigureAwait(false);
        using JsonDocument? document = TryParseDocument(text, ReflowOperation);
        if (document is null)
        {
            return null;
        }

        EnsureReflowAccepted(document.RootElement, roomId);

        JsonElement data = ResolveReflowData(document.RootElement);
        if (data.ValueKind != JsonValueKind.Object)
        {
            Logger.Debug(ModuleName, "抖音 reflow 接口未返回房间数据对象。", new Dictionary<string, object?>
            {
                ["operation"] = ReflowOperation,
                ["roomId"] = roomId,
            });
            return null;
        }

        JsonElement user = GetPropertyOrUndefined(data, UserField);
        if (user.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw Fail(
                ResolveFailure.RoomNotFound,
                ReflowOperation,
                string.Format(CultureInfo.InvariantCulture, RoomNotFoundDetailFormat, roomId));
        }

        string? anchorName = ReadString(user, NicknameField);
        if (string.IsNullOrWhiteSpace(anchorName))
        {
            throw Fail(ResolveFailure.ParseError, ReflowOperation, MissingAnchorMessage);
        }

        JsonElement room = GetPropertyOrUndefined(data, RoomField);
        if (room.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw Fail(ResolveFailure.NotLive, ReflowOperation, ReflowNotLiveMessage);
        }

        (IReadOnlyList<QualityOption> qualities, string? selectedQualityKey) =
            BuildQualityOptions(room, preferredQualityKey);
        _ = CollectCandidates(builder, room, ReflowOperation, selectedQualityKey, out string title, out string category);
        return CreateRoom(roomId, anchorName, title, category, builder, qualities, selectedQualityKey);
    }

    /// <summary>拼接 Web 进房接口的完整地址。</summary>
    /// <param name="roomId">房间号（已由输入校验限定为数字）。</param>
    /// <returns>完整请求地址（不含任何签名参数）。</returns>
    internal static string BuildRoomEnterUrl(string roomId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        return string.Concat(RoomEnterEndpoint, RoomEnterQuery, roomId);
    }

    /// <summary>
    /// 走 Web 进房接口：用首页下发的 <c>ttwid</c> 会话 cookie 换取房间状态与播放地址。
    /// </summary>
    /// <param name="roomId">房间号。</param>
    /// <param name="preferredQualityKey">调用方指定的档位键；为空时取最高档。</param>
    /// <param name="builder">候选构造器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析结果；会话 cookie 不可得或响应不可用时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 该接口是页面解析失败的兜底：实测房间 745964462470 返回
    /// <c>{"data":{"data":[{...room...}],"user":{"nickname":"..."}},"status_code":0}</c>；
    /// 缺少 <c>ttwid</c> 时返回 <b>HTTP 200 + 空正文</b>，因此这里把空正文当作"该路径不可用"
    /// 而不是解析错误。全程不使用任何平台签名。
    /// </remarks>
    private async Task<ResolvedRoom?> TryParseRoomEnterAsync(
        string roomId,
        string? preferredQualityKey,
        StreamCandidateBuilder builder,
        CancellationToken cancellationToken)
    {
        string? ttwid = await _session.TryGetTtwidAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ttwid))
        {
            Logger.Warn(ModuleName, "未能取得抖音会话 cookie，跳过进房接口。", new Dictionary<string, object?>
            {
                ["operation"] = RoomEnterOperation,
                ["roomId"] = roomId,
            });
            return null;
        }

        HttpRequestSpec spec = new()
        {
            Url = BuildRoomEnterUrl(roomId),
            Platform = Platform,
            Operation = RoomEnterOperation,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Referer"] = string.Concat(RoomUrlReferer, roomId),
                [CookieHeaderName] = string.Concat(DouyinWebSession.CookieName, "=", ttwid),
            },
        };

        string text = await _http.GetStringAsync(spec, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            Logger.Warn(ModuleName, RoomEnterEmptyBodyMessage, new Dictionary<string, object?>
            {
                ["operation"] = RoomEnterOperation,
                ["roomId"] = roomId,
            });
            return null;
        }

        using JsonDocument? document = TryParseDocument(text, RoomEnterOperation);
        if (document is null)
        {
            return null;
        }

        EnsureRoomEnterAccepted(document.RootElement, roomId);

        JsonElement data = GetPropertyOrUndefined(document.RootElement, DataField);
        if (data.ValueKind != JsonValueKind.Object)
        {
            Logger.Debug(ModuleName, "抖音进房接口未返回 data 对象。", new Dictionary<string, object?>
            {
                ["operation"] = RoomEnterOperation,
                ["roomId"] = roomId,
            });
            return null;
        }

        JsonElement user = GetPropertyOrUndefined(data, UserField);
        if (user.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw Fail(
                ResolveFailure.RoomNotFound,
                RoomEnterOperation,
                string.Format(CultureInfo.InvariantCulture, RoomNotFoundDetailFormat, roomId));
        }

        string? anchorName = ReadString(user, NicknameField);
        if (string.IsNullOrWhiteSpace(anchorName))
        {
            throw Fail(ResolveFailure.ParseError, RoomEnterOperation, MissingAnchorMessage);
        }

        JsonElement room = ResolveRoomEnterRoom(data);
        if (room.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw Fail(ResolveFailure.NotLive, RoomEnterOperation, RoomEnterNotLiveMessage);
        }

        (IReadOnlyList<QualityOption> qualities, string? selectedQualityKey) =
            BuildQualityOptions(room, preferredQualityKey);
        _ = CollectCandidates(builder, room, RoomEnterOperation, selectedQualityKey, out string title, out string category);
        return CreateRoom(roomId, anchorName, title, category, builder, qualities, selectedQualityKey);
    }

    /// <summary>定位进房响应里的 room 对象（<c>data.data[0]</c>，兼容 <c>data.room</c>）。</summary>
    /// <param name="data">进房响应的 <c>data</c> 节点。</param>
    /// <returns>room 对象；不存在时返回未定义元素。</returns>
    private static JsonElement ResolveRoomEnterRoom(JsonElement data)
    {
        JsonElement list = GetPropertyOrUndefined(data, DataField);
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    return item;
                }
            }

            return default;
        }

        return GetPropertyOrUndefined(data, RoomField);
    }

    /// <summary>进房响应的 <c>status_code</c> 非 0 时按"平台拒绝"抛出。</summary>
    /// <param name="root">响应根元素。</param>
    /// <param name="roomId">房间号，用于日志与消息上下文。</param>
    internal void EnsureRoomEnterAccepted(JsonElement root, string roomId) =>
        EnsureStatusCodeAccepted(root, roomId, RoomEnterOperation, RoomEnterRejectedDetailFormat);

    /// <summary>reflow 响应的 <c>status_code</c> 非 0 时按"平台拒绝"抛出。</summary>
    /// <param name="root">响应根元素。</param>
    /// <param name="roomId">房间号，用于日志与消息上下文。</param>
    /// <remarks>
    /// 平台把状态码放在根级 <c>status_code</c>、把原因放在 <c>data.message</c>
    /// （例如 10011 对应 <c>Request params error</c>）；两者都写进 detail，
    /// 便于和"房间不存在""未开播"区分（本项目不实现平台签名，见 ADR 0003）。
    /// </remarks>
    internal void EnsureReflowAccepted(JsonElement root, string roomId) =>
        EnsureStatusCodeAccepted(root, roomId, ReflowOperation, ReflowRejectedDetailFormat);

    /// <summary>抖音接口的 <c>status_code</c> 非 0 时按"平台拒绝"抛出。</summary>
    /// <param name="root">响应根元素。</param>
    /// <param name="roomId">房间号，用于日志与消息上下文。</param>
    /// <param name="operation">操作名。</param>
    /// <param name="detailFormat">失败详情模板（参数为 status_code 与平台 message）。</param>
    /// <exception >平台返回非 0 状态码时抛出。</exception>
    private void EnsureStatusCodeAccepted(JsonElement root, string roomId, string operation, string detailFormat)
    {
        int? statusCode = ReadFlexibleInt32(root, StatusCodeField);
        if (statusCode is not { } code || code == 0)
        {
            return;
        }

        string message = ReadString(GetPropertyOrUndefined(root, DataField), MessageField) ?? ReflowMessageUnknown;
        Logger.Warn(ModuleName, "抖音接口返回非 0 状态码。", new Dictionary<string, object?>
        {
            ["operation"] = operation,
            ["roomId"] = roomId,
            ["statusCode"] = code,
            ["message"] = message,
        });
        throw Fail(
            ResolveFailure.Rejected,
            operation,
            string.Format(CultureInfo.InvariantCulture, detailFormat, code, message));
    }

    /// <summary>定位 reflow 响应里的房间数据对象。</summary>
    /// <param name="root">响应根元素。</param>
    /// <returns>房间数据对象；不存在时返回未定义元素。</returns>
    /// <remarks>
    /// 线上同时存在 <c>data.data</c>（webcast 常规包装）与 <c>data</c>（分享落地页同源接口）
    /// 两种形态，两种都接受，避免包装变化时主路径直接判为解析错误。
    /// </remarks>
    private static JsonElement ResolveReflowData(JsonElement root)
    {
        JsonElement nested = GetNestedProperty(root, DataField, DataField);
        return nested.ValueKind == JsonValueKind.Object ? nested : GetPropertyOrUndefined(root, DataField);
    }

    /// <summary>读取抖音 room 对象的状态、标题与分区，并按选中的画质档位加入候选。</summary>
    /// <param name="builder">候选构造器。</param>
    /// <param name="room">抖音返回的 room 对象（页面与 reflow 结构一致）。</param>
    /// <param name="operation">操作名，用于失败分类与日志。</param>
    /// <param name="selectedQualityKey">选中的档位键；为 <see langword="null"/> 时不加入任何候选。</param>
    /// <param name="title">直播间标题，缺失时为空字符串。</param>
    /// <param name="category">直播分区，缺失时为空字符串。</param>
    /// <returns>至少加入一个候选返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 档位与地址绑定：这里只加入选中档位的地址（FLV 在前、HLS 在后），
    /// 不混入其它档位的地址，避免播放页选中的档位被别的档位顶掉。
    /// </remarks>
    internal bool CollectCandidates(
        StreamCandidateBuilder builder,
        JsonElement room,
        string operation,
        string? selectedQualityKey,
        out string title,
        out string category)
    {
        title = ReadString(room, TitleField) ?? string.Empty;
        category = ResolveCategory(room);

        int? status = ReadFlexibleInt32(room, StatusField);
        if (status == EndedRoomStatus)
        {
            throw Fail(
                ResolveFailure.NotLive,
                operation,
                string.Format(CultureInfo.InvariantCulture, EndedRoomDetailFormat, operation, status));
        }

        JsonElement streamUrl = GetPropertyOrUndefined(room, StreamUrlField);
        if (streamUrl.ValueKind != JsonValueKind.Object)
        {
            throw Fail(
                ResolveFailure.NotLive,
                operation,
                string.Format(CultureInfo.InvariantCulture, NotLiveDetailFormat, operation));
        }

        return selectedQualityKey is not null && TryAddSelectedQuality(builder, streamUrl, selectedQualityKey);
    }

    /// <summary>加入选中档位的播放地址：FLV 优先，其次 HLS。</summary>
    /// <param name="builder">候选构造器。</param>
    /// <param name="streamUrl">抖音 room.stream_url 对象。</param>
    /// <param name="selectedQualityKey">选中的档位键。</param>
    /// <returns>成功加入至少一个候选返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 地址来源依次为 <c>flv_pull_url</c>、<c>stream_url</c> 上的直接档位键、<c>hls_pull_url_map</c>；
    /// 选中档位没有可用地址时记 Warn 并返回 <see langword="false"/>（由调用方决定回退路径）。
    /// </remarks>
    private bool TryAddSelectedQuality(
        StreamCandidateBuilder builder,
        JsonElement streamUrl,
        string selectedQualityKey)
    {
        StreamQuality quality = MapDouyinQuality(selectedQualityKey);
        bool added = false;

        string? flvUrl = ReadMappedQualityUrl(streamUrl, FlvPullUrlField, selectedQualityKey)
            ?? ReadDirectQualityUrl(streamUrl, selectedQualityKey);
        if (!string.IsNullOrWhiteSpace(flvUrl))
        {
            added |= builder.TryAdd(
                flvUrl,
                StreamFormat.FlvHttp,
                VideoCodec.Avc,
                quality,
                expiresAt: null,
                referer: RoomUrlReferer,
                cdnHost: TryGetHost(flvUrl));
        }

        string? hlsUrl = ReadMappedQualityUrl(streamUrl, HlsPullUrlMapField, selectedQualityKey);
        if (!string.IsNullOrWhiteSpace(hlsUrl))
        {
            added |= builder.TryAdd(
                hlsUrl,
                StreamFormat.HlsTs,
                VideoCodec.Avc,
                quality,
                expiresAt: null,
                referer: RoomUrlReferer,
                cdnHost: TryGetHost(hlsUrl));
        }

        if (!added)
        {
            Logger.Warn(ModuleName, "抖音选中档位没有可用地址。", new Dictionary<string, object?>
            {
                ["operation"] = StreamUrlOperation,
                ["qualityKey"] = selectedQualityKey,
            });
        }

        return added;
    }

    /// <summary>把抖音的拉流档位键映射为内部画质档位。</summary>
    /// <param name="qualityKey">拉流档位键（<c>origin</c>、<c>FULL_HD1</c> 等）。</param>
    /// <returns>内部画质档位；无法判定时返回 <see cref="StreamQuality.Unknown"/>。</returns>
    /// <remarks>
    /// 老式分辨率键沿用 <see cref="QualityNames.FromDouyinQualityName"/> 的映射，
    /// 新档位键只在内部做粗粒度近似（面向用户的档位名以 <see cref="QualityOption.Label"/> 为准）。
    /// </remarks>
    private static StreamQuality MapDouyinQuality(string qualityKey)
    {
        StreamQuality known = QualityNames.FromDouyinQualityName(qualityKey);
        if (known != StreamQuality.Unknown)
        {
            return known;
        }

        return qualityKey switch
        {
            QualityKeyOrigin => StreamQuality.Hd1080HighFps,
            QualityKeyRealOrigin => StreamQuality.Hd1080HighFps,
            QualityKeyUhd => StreamQuality.Hd1080,
            QualityKeyHd => StreamQuality.Hd1080,
            QualityKeySd => StreamQuality.Hd720,
            QualityKeyLd => StreamQuality.Sd480,
            _ => StreamQuality.Unknown,
        };
    }

    /// <summary>读取直播分区：优先二级分区标题，其次一级分区标题，都缺失时为空字符串。</summary>
    /// <param name="room">抖音返回的 room 对象。</param>
    /// <returns>分区标题。</returns>
    private static string ResolveCategory(JsonElement room)
    {
        string? category = ReadNestedString(room, PartitionRoadMapField, SubPartitionField, PartitionField, TitleField);
        category ??= ReadNestedString(room, PartitionField, TitleField);
        return category ?? string.Empty;
    }

    /// <summary>组装解析结果。</summary>
    /// <param name="roomId">房间号。</param>
    /// <param name="anchor">主播名。</param>
    /// <param name="title">直播间标题。</param>
    /// <param name="category">直播分区。</param>
    /// <param name="builder">候选构造器。</param>
    /// <param name="qualities">本次可选的画质档位。</param>
    /// <param name="selectedQualityKey">候选实际使用的档位键。</param>
    /// <returns>解析结果。</returns>
    private ResolvedRoom CreateRoom(
        string roomId,
        string anchor,
        string title,
        string category,
        StreamCandidateBuilder builder,
        IReadOnlyList<QualityOption> qualities,
        string? selectedQualityKey) => new()
        {
            Platform = Platform,
            RoomId = roomId,
            Anchor = anchor,
            Title = title,
            Category = category,
            Candidates = builder.Build(),
            ResolvedAt = DateTimeOffset.UtcNow,
            Qualities = qualities,
            SelectedQualityKey = selectedQualityKey,
        };

    /// <summary>
    /// 构造抖音的画质档位列表，并决定本次实际使用的档位。
    /// </summary>
    /// <param name="room">抖音返回的 room 对象（房间页与 reflow 结构一致）。</param>
    /// <param name="preferredQualityKey">调用方指定的档位键；为空或 <c>best</c> 时取最高档。</param>
    /// <returns>档位列表（从高到低）与选中的档位键；没有任何可用档位时为（空列表，<see langword="null"/>）。</returns>
    /// <remarks>
    /// 档位声明优先取 <c>stream_url</c> 下的 <c>options.qualities[]</c>（每项含 <c>sdk_key</c>/<c>name</c>/<c>v_bit_rate</c>），
    /// 但只保留能在流地址里找到对应地址的档位；声明不可用或与地址键不同名时退回按
    /// <c>flv_pull_url</c>、<c>hls_pull_url_map</c> 与 <c>stream_url</c> 上的档位键枚举。
    /// 档位键无法命中时回退到最高档并记 Warn（不抛异常）。
    /// </remarks>
    internal (IReadOnlyList<QualityOption> Qualities, string? SelectedKey) BuildQualityOptions(
        JsonElement room,
        string? preferredQualityKey)
    {
        JsonElement streamUrl = GetPropertyOrUndefined(room, StreamUrlField);
        List<DouyinQualityDeclaration> declared = ReadDeclaredQualities(streamUrl);
        List<string> available = [];
        foreach (DouyinQualityDeclaration declaration in declared)
        {
            if (HasQualityUrl(streamUrl, declaration.Key))
            {
                AddQualityKey(available, declaration.Key);
            }
        }

        if (available.Count == 0)
        {
            available = ReadMappedQualityKeys(streamUrl);
        }

        if (available.Count == 0)
        {
            Logger.Warn(ModuleName, "抖音响应没有可用的画质档位。", new Dictionary<string, object?>
            {
                ["operation"] = StreamUrlOperation,
            });

            IReadOnlyList<QualityOption> empty = [];
            return (empty, null);
        }

        List<string> ordered = available
            .OrderByDescending(static key => RankQualityKey(key))
            .ToList();
        List<QualityOption> qualities = [];
        foreach (string key in ordered)
        {
            DouyinQualityDeclaration? declaration = FindDeclaration(declared, key);
            qualities.Add(new QualityOption
            {
                Key = key,
                Label = declaration?.Name is { Length: > 0 } name ? name : DescribeQualityKey(key),
                BitrateKbps = declaration?.BitrateKbps,
                IsBest = qualities.Count == 0,
            });
        }

        string? selected = MatchPreferredQuality(qualities, preferredQualityKey) ?? qualities[0].Key;
        return (qualities, selected);
    }

    /// <summary>
    /// 在档位列表中查找调用方指定的档位键。
    /// </summary>
    /// <param name="qualities">可用档位列表。</param>
    /// <param name="preferredQualityKey">调用方指定的档位键。</param>
    /// <returns>命中的档位键；未指定或无法命中时返回 <see langword="null"/>。</returns>
    /// <remarks>无法命中时记 Warn 并使用最高档，而不是让解析失败。</remarks>
    private string? MatchPreferredQuality(IReadOnlyList<QualityOption> qualities, string? preferredQualityKey)
    {
        if (string.IsNullOrWhiteSpace(preferredQualityKey)
            || string.Equals(preferredQualityKey, QualityOption.BestFlag, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (QualityOption option in qualities)
        {
            if (string.Equals(option.Key, preferredQualityKey, StringComparison.OrdinalIgnoreCase))
            {
                return option.Key;
            }
        }

        Logger.Warn(ModuleName, "抖音档位键无法命中，回退到最高档。", new Dictionary<string, object?>
        {
            ["operation"] = StreamUrlOperation,
            ["preferredQualityKey"] = preferredQualityKey,
            ["fallbackQualityKey"] = qualities[0].Key,
        });

        return null;
    }

    /// <summary>
    /// 读取官方声明的档位列表（<c>options.qualities[]</c>）。
    /// </summary>
    /// <param name="streamUrl">抖音 room.stream_url 对象。</param>
    /// <returns>档位声明列表（按键去重，保持响应顺序）；没有声明时为空列表。</returns>
    /// <remarks>
    /// 两条已知路径：双屏/多路场景的 <c>pull_datas[0].options.qualities</c>，
    /// 与常规场景的 <c>live_core_sdk_data.pull_data.options.qualities</c>；两者都缺失时退回 <c>options.qualities</c>。
    /// </remarks>
    private static List<DouyinQualityDeclaration> ReadDeclaredQualities(JsonElement streamUrl)
    {
        JsonElement qualities = FindDeclaredQualitiesNode(streamUrl);
        List<DouyinQualityDeclaration> declarations = [];
        if (qualities.ValueKind != JsonValueKind.Array)
        {
            return declarations;
        }

        foreach (JsonElement item in qualities.EnumerateArray())
        {
            string? key = ReadString(item, SdkKeyField);
            if (string.IsNullOrWhiteSpace(key) || FindDeclaration(declarations, key) is not null)
            {
                continue;
            }

            declarations.Add(new DouyinQualityDeclaration(
                key,
                ReadString(item, QualityNameField),
                ReadBitrateKbps(item, VideoBitRateField)));
        }

        return declarations;
    }

    /// <summary>按优先级定位档位声明数组。</summary>
    /// <param name="streamUrl">抖音 room.stream_url 对象。</param>
    /// <returns>档位声明数组；均缺失时返回未定义元素。</returns>
    private static JsonElement FindDeclaredQualitiesNode(JsonElement streamUrl)
    {
        JsonElement pullDatas = GetPropertyOrUndefined(streamUrl, PullDatasField);
        if (pullDatas.ValueKind == JsonValueKind.Array && pullDatas.GetArrayLength() > 0)
        {
            JsonElement fromPullDatas = GetNestedProperty(pullDatas[0], OptionsField, QualitiesField);
            if (fromPullDatas.ValueKind == JsonValueKind.Array && fromPullDatas.GetArrayLength() > 0)
            {
                return fromPullDatas;
            }
        }

        JsonElement fromSdkData = GetNestedProperty(
            streamUrl,
            LiveCoreSdkDataField,
            PullDataField,
            OptionsField,
            QualitiesField);
        if (fromSdkData.ValueKind == JsonValueKind.Array && fromSdkData.GetArrayLength() > 0)
        {
            return fromSdkData;
        }

        return GetNestedProperty(streamUrl, OptionsField, QualitiesField);
    }

    /// <summary>
    /// 从流地址映射的键中枚举档位键。
    /// </summary>
    /// <param name="streamUrl">抖音 room.stream_url 对象。</param>
    /// <returns>档位键列表（保持响应中的出现顺序，已按键去重）。</returns>
    private static List<string> ReadMappedQualityKeys(JsonElement streamUrl)
    {
        List<string> keys = [];
        AddQualityKeysFromMap(streamUrl, FlvPullUrlField, keys);
        AddQualityKeysFromMap(streamUrl, HlsPullUrlMapField, keys);
        AddDirectQualityKeys(streamUrl, keys);
        return keys;
    }

    /// <summary>收集"档位键 → 地址"映射里的档位键。</summary>
    /// <param name="streamUrl">抖音 room.stream_url 对象。</param>
    /// <param name="mapName">映射字段名。</param>
    /// <param name="keys">已收集的档位键（原地追加）。</param>
    private static void AddQualityKeysFromMap(JsonElement streamUrl, string mapName, List<string> keys)
    {
        JsonElement map = GetPropertyOrUndefined(streamUrl, mapName);
        if (map.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in map.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                AddQualityKey(keys, property.Name);
            }
        }
    }

    /// <summary>收集 <c>stream_url</c> 上直接给出的已知档位键。</summary>
    /// <param name="streamUrl">抖音 room.stream_url 对象。</param>
    /// <param name="keys">已收集的档位键（原地追加）。</param>
    private static void AddDirectQualityKeys(JsonElement streamUrl, List<string> keys)
    {
        if (streamUrl.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in streamUrl.EnumerateObject())
        {
            if (IsKnownQualityKey(property.Name)
                && property.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                AddQualityKey(keys, property.Name);
            }
        }
    }

    /// <summary>按键去重地追加档位键（忽略大小写）。</summary>
    /// <param name="keys">已收集的档位键。</param>
    /// <param name="qualityKey">待追加的档位键。</param>
    private static void AddQualityKey(List<string> keys, string qualityKey)
    {
        foreach (string existing in keys)
        {
            if (string.Equals(existing, qualityKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        keys.Add(qualityKey);
    }

    /// <summary>在档位声明中查找指定档位键。</summary>
    /// <param name="declarations">档位声明列表。</param>
    /// <param name="qualityKey">档位键。</param>
    /// <returns>命中的声明；未命中时返回 <see langword="null"/>。</returns>
    private static DouyinQualityDeclaration? FindDeclaration(
        List<DouyinQualityDeclaration> declarations,
        string qualityKey)
    {
        foreach (DouyinQualityDeclaration declaration in declarations)
        {
            if (string.Equals(declaration.Key, qualityKey, StringComparison.OrdinalIgnoreCase))
            {
                return declaration;
            }
        }

        return null;
    }

    /// <summary>判断档位在流地址里是否存在可用地址。</summary>
    /// <param name="streamUrl">抖音 room.stream_url 对象。</param>
    /// <param name="qualityKey">档位键。</param>
    /// <returns>FLV、直接键或 HLS 任一来源有地址时返回 <see langword="true"/>。</returns>
    private static bool HasQualityUrl(JsonElement streamUrl, string qualityKey) =>
        !string.IsNullOrWhiteSpace(ReadMappedQualityUrl(streamUrl, FlvPullUrlField, qualityKey))
        || !string.IsNullOrWhiteSpace(ReadDirectQualityUrl(streamUrl, qualityKey))
        || !string.IsNullOrWhiteSpace(ReadMappedQualityUrl(streamUrl, HlsPullUrlMapField, qualityKey));

    /// <summary>读取"档位键 → 地址"映射中的地址。</summary>
    /// <param name="streamUrl">抖音 room.stream_url 对象。</param>
    /// <param name="mapName">映射字段名。</param>
    /// <param name="qualityKey">档位键。</param>
    /// <returns>地址；映射缺失或该档位没有地址时返回 <see langword="null"/>。</returns>
    private static string? ReadMappedQualityUrl(JsonElement streamUrl, string mapName, string qualityKey)
    {
        JsonElement map = GetPropertyOrUndefined(streamUrl, mapName);
        return map.ValueKind == JsonValueKind.Object ? ReadString(map, qualityKey) : null;
    }

    /// <summary>读取 <c>stream_url</c> 上直接以档位键给出的地址。</summary>
    /// <param name="streamUrl">抖音 room.stream_url 对象。</param>
    /// <param name="qualityKey">档位键。</param>
    /// <returns>地址；该键不是字符串时返回 <see langword="null"/>。</returns>
    private static string? ReadDirectQualityUrl(JsonElement streamUrl, string qualityKey) =>
        ReadString(streamUrl, qualityKey);

    /// <summary>判断档位键是否属于已知的拉流档位。</summary>
    /// <param name="qualityKey">档位键。</param>
    /// <returns>属于已知档位返回 <see langword="true"/>。</returns>
    private static bool IsKnownQualityKey(string qualityKey) => RankQualityKey(qualityKey) > 0;

    /// <summary>计算档位排序权重：越靠前的已知档位权重越大，未知档位为 0。</summary>
    /// <param name="qualityKey">档位键。</param>
    /// <returns>排序权重。</returns>
    private static int RankQualityKey(string qualityKey)
    {
        for (int index = 0; index < KnownQualityKeyOrder.Length; index++)
        {
            if (string.Equals(KnownQualityKeyOrder[index], qualityKey, StringComparison.OrdinalIgnoreCase))
            {
                return KnownQualityKeyOrder.Length - index;
            }
        }

        return 0;
    }

    /// <summary>生成档位的显示名：优先响应给出的官方名称，缺失时用内置中文映射。</summary>
    /// <param name="qualityKey">档位键。</param>
    /// <returns>显示名；未知档位键返回键名本身。</returns>
    private static string DescribeQualityKey(string qualityKey) => qualityKey switch
    {
        QualityKeyOrigin => "原画",
        QualityKeyRealOrigin => "真原画",
        QualityKeyUhd => "蓝光",
        QualityKeyHd => "超清",
        QualityKeySd => "高清",
        QualityKeyLd => "标清",
        QualityKeyAudioOnly => "音频流",
        QualityNames.DouyinQualityFullHd1 => "高清",
        QualityNames.DouyinQualityHd1 => "标清",
        QualityNames.DouyinQualitySd1 => "流畅",
        QualityNames.DouyinQualitySd2 => "流畅",
        _ => qualityKey,
    };

    /// <summary>
    /// 读取码率字段并归一化为 kbps。
    /// </summary>
    /// <param name="element">父元素。</param>
    /// <param name="name">字段名。</param>
    /// <returns>kbps 码率；字段缺失或不是正数时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 抖音 <c>v_bit_rate</c> 的单位未经官方确认：不小于 <see cref="BitrateBpsThreshold"/> 时按 bps 换算，
    /// 否则按 kbps 直接使用（两种取值都能得到合理的 kbps 数量级）。
    /// </remarks>
    private static int? ReadBitrateKbps(JsonElement element, string name)
    {
        int? raw = ReadFlexibleInt32(element, name);
        if (raw is not { } value || value <= 0)
        {
            return null;
        }

        return value >= BitrateBpsThreshold ? value / BitsPerKilobit : value;
    }

    /// <summary>官方档位声明。</summary>
    /// <param name="Key">档位键（<c>sdk_key</c>）。</param>
    /// <param name="Name">官方中文档位名；缺失时为 <see langword="null"/>。</param>
    /// <param name="BitrateKbps">归一化后的码率（kbps）；缺失时为 <see langword="null"/>。</param>
    private sealed record DouyinQualityDeclaration(string Key, string? Name, int? BitrateKbps);

    /// <summary>按路径逐层读取对象属性；任意一层缺失或不是对象时返回未定义元素。</summary>
    /// <param name="element">起始元素。</param>
    /// <param name="path">属性名路径。</param>
    /// <returns>命中的元素；未命中时返回 <see cref="JsonValueKind.Undefined"/>。</returns>
    private static JsonElement GetNestedProperty(JsonElement element, params string[] path)
    {
        JsonElement current = element;
        foreach (string name in path)
        {
            current = GetPropertyOrUndefined(current, name);
            if (current.ValueKind == JsonValueKind.Undefined)
            {
                return current;
            }
        }

        return current;
    }

    /// <summary>读取对象属性；元素不是对象或属性缺失时返回未定义元素（不抛异常）。</summary>
    /// <param name="element">父元素。</param>
    /// <param name="name">属性名。</param>
    /// <returns>属性值；未命中时返回 <see cref="JsonValueKind.Undefined"/>。</returns>
    private static JsonElement GetPropertyOrUndefined(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value
            : default;

    /// <summary>按路径读取字符串属性（全程校验 <see cref="JsonValueKind"/>，不做不安全的类型断言）。</summary>
    /// <param name="element">起始元素。</param>
    /// <param name="path">属性名路径。</param>
    /// <returns>字符串值；任意一层缺失或类型不符时返回 <see langword="null"/>。</returns>
    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        JsonElement current = GetNestedProperty(element, path);
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    /// <summary>读取字符串属性（校验 <see cref="JsonValueKind.String"/>）。</summary>
    /// <param name="element">父元素。</param>
    /// <param name="name">属性名。</param>
    /// <returns>字符串值；缺失或类型不符时返回 <see langword="null"/>。</returns>
    private static string? ReadString(JsonElement element, string name)
    {
        JsonElement value = GetPropertyOrUndefined(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>读取 32 位整数属性（校验 <see cref="JsonValueKind.Number"/>）。</summary>
    /// <param name="element">父元素。</param>
    /// <param name="name">属性名。</param>
    /// <returns>整数值；缺失、类型不符或超出范围时返回 <see langword="null"/>。</returns>
    private static int? ReadInt32(JsonElement element, string name)
    {
        JsonElement value = GetPropertyOrUndefined(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;
    }

    /// <summary>读取整数属性，兼容平台把数字写成字符串的情况。</summary>
    /// <param name="element">父元素。</param>
    /// <param name="name">属性名。</param>
    /// <returns>整数值；无法解析时返回 <see langword="null"/>。</returns>
    private static int? ReadFlexibleInt32(JsonElement element, string name)
    {
        int? number = ReadInt32(element, name);
        if (number is not null)
        {
            return number;
        }

        string? text = ReadString(element, name);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }

    /// <summary>从地址中推导 CDN 主机名。</summary>
    /// <param name="url">流地址。</param>
    /// <returns>主机名；地址非法时返回 <see langword="null"/>（由候选构造器兜底）。</returns>
    private static string? TryGetHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host) ? uri.Host : null;
}
