namespace StreamPilot.Parsers.Bilibili;

using System.Globalization;
using System.Text.Json;
using StreamPilot.Core.Errors;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Parsers;
using StreamPilot.Core.Utilities;

/// <summary>
/// B站（哔哩哔哩）直播解析器。
/// </summary>
/// <remarks>
/// 解析流程（见 docs/adr/0003-parser-contract.md 第 5 节）：
/// <list type="number">
/// <item><description>确定数字房间号：输入本身是数字时直接使用，短号先抓取直播间页面解析；</description></item>
/// <item><description>调用 getInfoByRoom 取标题、主播名、分区与封面；</description></item>
/// <item><description>调用 getRoomPlayInfo 取播放线路，按 host + base_url + extra 拼出候选地址。</description></item>
/// </list>
/// 画质由响应中的 current_qn / accept_qn 推导，签名参数 expires 写入候选有效期；
/// Cookie 仅在调用方提供时注入，且永不写入日志（日志只写指纹或脱敏后的地址）。
/// </remarks>
internal sealed class BilibiliParser : PlatformParserBase
{
    /// <summary>日志模块名。</summary>
    private const string ModuleName = "Parsers.Bilibili";

    /// <summary>请求头名称：User-Agent。</summary>
    private const string HeaderNameUserAgent = "User-Agent";

    /// <summary>请求头名称：Cookie。</summary>
    private const string HeaderNameCookie = "Cookie";

    /// <summary>B站直播间页面地址前缀（同时作为 <see cref="RoomUrlPrefix"/>）。</summary>
    private const string LivePageUrlPrefix = "https://live.bilibili.com/";

    /// <summary>getInfoByRoom 接口地址前缀（后接数字房间号）。</summary>
    private const string RoomInfoUrlPrefix =
        "https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?room_id=";

    /// <summary>getRoomBaseInfo 接口地址前缀（后接数字房间号；作为房间信息的风控降级通道）。</summary>
    private const string RoomBaseInfoUrlPrefix =
        "https://api.live.bilibili.com/xlive/web-room/v1/index/getRoomBaseInfo?room_ids=";

    /// <summary>getRoomBaseInfo 的固定查询参数尾部。</summary>
    private const string RoomBaseInfoQuerySuffix = "&req_biz=web_room_componet";

    /// <summary>getRoomPlayInfo 接口地址前缀（不含查询参数）。</summary>
    private const string PlayInfoUrlPrefix =
        "https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo";

    /// <summary>getRoomPlayInfo 的固定查询参数（画质数值紧跟其后）。</summary>
    private const string PlayInfoQueryPrefix = "?protocol=0,1&format=0,1,2&codec=0,1&qn=";

    /// <summary>getRoomPlayInfo 的固定查询参数尾部（后接数字房间号）。</summary>
    private const string PlayInfoQuerySuffix = "&platform=web&ptype=8&dolby=5&panorama=1&room_id=";

    /// <summary>播放候选需要携带的 Referer（B站 CDN 校验来源）。</summary>
    private const string PlaybackReferer = "https://live.bilibili.com/";

    /// <summary>操作名：确定房间号。</summary>
    private const string OperationResolveRoomId = "resolve-room-id";

    /// <summary>操作名：抓取直播间页面。</summary>
    private const string OperationGetRoomPage = "get-room-page";

    /// <summary>操作名：房间信息接口。</summary>
    private const string OperationGetRoomInfo = "getInfoByRoom";

    /// <summary>操作名：房间基础信息接口（房间信息被风控时的降级通道）。</summary>
    private const string OperationGetRoomBaseInfo = "getRoomBaseInfo";

    /// <summary>操作名：播放信息接口。</summary>
    private const string OperationGetPlayInfo = "getRoomPlayInfo";

    /// <summary>协议名：HTTP-FLV。</summary>
    private const string ProtocolHttpStream = "http_stream";

    /// <summary>协议名：HTTP-HLS。</summary>
    private const string ProtocolHttpHls = "http_hls";

    /// <summary>容器名：FLV。</summary>
    private const string FormatNameFlv = "flv";

    /// <summary>容器名：MPEG-TS。</summary>
    private const string FormatNameTs = "ts";

    /// <summary>容器名：fMP4。</summary>
    private const string FormatNameFmp4 = "fmp4";

    /// <summary>编码名：H.264 / AVC。</summary>
    private const string CodecNameAvc = "avc";

    /// <summary>编码名：H.265 / HEVC。</summary>
    private const string CodecNameHevc = "hevc";

    /// <summary>编码名：AV1。</summary>
    private const string CodecNameAv1 = "av1";

    /// <summary>房间号标记的结束字符：双引号。</summary>
    private const char QuoteTerminator = '"';

    /// <summary>房间号标记的结束字符：逗号。</summary>
    private const char CommaTerminator = ',';

    /// <summary>有效期的查询参数名（Unix 秒）。</summary>
    private const string ExpiresParameterName = "expires";

    /// <summary>播放状态字段名。</summary>
    private const string LiveStatusPropertyName = "live_status";

    /// <summary>getInfoByRoom 返回"房间不存在"时的错误码。</summary>
    private const int RoomNotFoundCode = -400;

    /// <summary>B站风控拦截错误码（请求被风控拒绝，不是房间不存在）。</summary>
    private const int RiskControlCode = -352;

    /// <summary>B站请求被拦截错误码。</summary>
    private const int RequestBlockedCode = -412;

    /// <summary>B站请求过于频繁错误码。</summary>
    private const int RequestThrottledCode = -509;

    /// <summary>播放状态取值：未开播。</summary>
    private const int LiveStatusOffline = 0;

    /// <summary>播放状态取值：轮播 / 重播。</summary>
    private const int LiveStatusReplaying = 2;

    /// <summary>页面中数字房间号的候选标记（含值的结束字符），按优先级排列。</summary>
    private static readonly (string Marker, char Terminator)[] RoomIdMarkers =
    [
        ("\"defaultRoomId\":\"", QuoteTerminator),
        ("\"room_id\":", CommaTerminator),
        ("\"roomid\":", CommaTerminator),
        ("\"roomId\":", CommaTerminator),
    ];

    /// <summary>抓取直播间页面时使用的请求头（B站对缺少 UA 的请求会返回精简页面）。</summary>
    private static readonly IReadOnlyDictionary<string, string> PageRequestHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HeaderNameUserAgent] = HttpClientFactory.DefaultUserAgent,
        };

    /// <summary>HTTP 客户端。</summary>
    private readonly HttpTextClient _http;

    /// <summary>初始化 B站解析器。</summary>
    /// <param name="http">HTTP 客户端。</param>
    /// <param name="logger">结构化日志。</param>
    public BilibiliParser(HttpTextClient http, IStructuredLogger logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <inheritdoc />
    public override PlatformId Platform => PlatformId.Bilibili;

    /// <inheritdoc />
    public override string DisplayName => "哔哩哔哩";

    /// <inheritdoc />
    public override string RoomUrlPrefix => LivePageUrlPrefix;

    /// <inheritdoc />
    /// <remarks>
    /// B站房间信息接口（<c>getInfoByRoom</c>）在部分网络环境下会被风控拦截（返回 <c>code=-352</c> 等），
    /// 而播放地址接口仍可正常返回。因此这里把"房间信息失败"设计为**可降级**：
    /// 先尝试房间信息，失败（风控/结构变化）时退回只用播放接口，用直播状态与候选线路判断结果，
    /// 避免把风控拦截误报成"房间号不存在"。
    /// </remarks>
    protected override async Task<ResolvedRoom> OnParseAsync(RoomQuery query, CancellationToken cancellationToken)
    {
        string numericRoomId = await ResolveNumericRoomIdAsync(query, cancellationToken).ConfigureAwait(false);

        RoomInfoSnapshot roomInfo;
        bool roomInfoAvailable = true;
        try
        {
            roomInfo = await FetchRoomInfoAsync(numericRoomId, query.Cookie, cancellationToken).ConfigureAwait(false);
        }
        catch (ResolveException exception) when (exception.Failure is ResolveFailure.Rejected or ResolveFailure.ParseError)
        {
            // getInfoByRoom 被风控时先试 getRoomBaseInfo：它同样给出主播名与标题，
            // 能避免"解析成功但界面上一片未知"。
            RoomInfoSnapshot? fallback =
                await TryFetchRoomBaseInfoAsync(numericRoomId, query.Cookie, cancellationToken).ConfigureAwait(false);

            if (fallback is not null)
            {
                roomInfo = fallback;
                Logger.Warn(ModuleName, "房间信息接口不可用，已改用房间基础信息接口。", new Dictionary<string, object?>
                {
                    ["roomId"] = numericRoomId,
                    ["failure"] = exception.Failure.ToString(),
                });
            }
            else
            {
                roomInfoAvailable = false;
                roomInfo = new RoomInfoSnapshot(numericRoomId, string.Empty, string.Empty, string.Empty, null);
                Logger.Warn(ModuleName, "房间信息接口不可用，退回仅用播放接口解析。", new Dictionary<string, object?>
                {
                    ["roomId"] = numericRoomId,
                    ["failure"] = exception.Failure.ToString(),
                    ["detail"] = exception.Message,
                });
            }
        }

        (IReadOnlyList<StreamCandidate> candidates, int liveStatus, IReadOnlyList<QualityOption> qualities, string? selectedQualityKey) =
            await FetchPlayInfoAsync(
                roomInfo.RoomId,
                query.Cookie,
                roomInfoAvailable,
                cancellationToken,
                ParseQualityNumber(query.PreferredQualityKey)).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            // 播放接口明确给出状态时按状态分类；否则（房间信息也不可用）只能判定为未开播。
            ResolveFailure failure = liveStatus == LiveStatusReplaying ? ResolveFailure.Replaying : ResolveFailure.NotLive;
            throw Fail(failure, OperationGetPlayInfo, ResolveMessages.ForFailure(failure));
        }

        Logger.Info(ModuleName, "B站直播间解析完成。", new Dictionary<string, object?>
        {
            ["roomId"] = roomInfo.RoomId,
            ["candidateCount"] = candidates.Count,
            ["roomInfoAvailable"] = roomInfoAvailable,
            ["quality"] = selectedQualityKey,
        });

        return new ResolvedRoom
        {
            Platform = PlatformId.Bilibili,
            RoomId = roomInfo.RoomId,
            Anchor = string.IsNullOrWhiteSpace(roomInfo.Anchor) ? ResolveMessages.UnknownAnchor : roomInfo.Anchor,
            Title = string.IsNullOrWhiteSpace(roomInfo.Title) ? ResolveMessages.TitleUnavailable : roomInfo.Title,
            Category = roomInfo.Category,
            Candidates = candidates,
            ResolvedAt = DateTimeOffset.UtcNow,
            CoverUrl = roomInfo.CoverUrl,
            Qualities = qualities,
            SelectedQualityKey = selectedQualityKey,
        };
    }

    /// <summary>
    /// 判断 B站错误码是否属于风控/限流（这类错误应重试或降级，而不是判定房间不存在）。
    /// </summary>
    /// <param name="code">接口返回的 code。</param>
    /// <returns>属于风控/限流返回 <see langword="true"/>。</returns>
    internal static bool IsRiskControlCode(int code) =>
        code is RiskControlCode or RequestBlockedCode or RequestThrottledCode;

    /// <summary>
    /// 确定数字房间号：输入是纯数字时直接使用，否则按短号抓取直播间页面解析。
    /// </summary>
    /// <param name="query">房间查询条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>数字房间号。</returns>
    private async Task<string> ResolveNumericRoomIdAsync(RoomQuery query, CancellationToken cancellationToken)
    {
        string? rawRoomId = string.IsNullOrWhiteSpace(query.RoomId)
            ? TryExtractRoomIdFromUrl(query.RoomUrl)
            : query.RoomId.Trim();

        if (string.IsNullOrWhiteSpace(rawRoomId))
        {
            throw Fail(ResolveFailure.InvalidInput, OperationResolveRoomId, "无法从房间号或直播间链接中提取 B站房间号。");
        }

        if (IsAllDigits(rawRoomId))
        {
            return rawRoomId;
        }

        Logger.Debug(ModuleName, "输入为 B站短号，改为从直播间页面解析数字房间号。", new Dictionary<string, object?>
        {
            ["roomId"] = rawRoomId,
        });

        return await FetchNumericRoomIdFromPageAsync(rawRoomId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 抓取直播间页面并从内联数据中解析数字房间号。
    /// </summary>
    /// <param name="idOrShortId">数字房间号或短号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>数字房间号。</returns>
    private async Task<string> FetchNumericRoomIdFromPageAsync(string idOrShortId, CancellationToken cancellationToken)
    {
        HttpRequestSpec spec = new()
        {
            Url = string.Concat(LivePageUrlPrefix, Uri.EscapeDataString(idOrShortId)),
            Platform = PlatformId.Bilibili,
            Operation = OperationGetRoomPage,
            Headers = PageRequestHeaders,
        };

        string html = await _http.GetStringAsync(spec, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
        {
            throw Fail(ResolveFailure.ParseError, OperationGetRoomPage, "无法从直播间页面解析房间号（页面内容为空）。");
        }

        string? numericRoomId = TryExtractNumericRoomIdFromPage(html);
        if (numericRoomId is null)
        {
            throw Fail(ResolveFailure.ParseError, OperationGetRoomPage, "无法从直播间页面解析房间号。");
        }

        Logger.Debug(ModuleName, "已从直播间页面解析出数字房间号。", new Dictionary<string, object?>
        {
            ["roomId"] = numericRoomId,
        });

        return numericRoomId;
    }

    /// <summary>
    /// 按固定顺序在页面内容中查找房间号标记，返回第一个可解析为正整数的值。
    /// </summary>
    /// <param name="html">直播间页面内容。</param>
    /// <returns>数字房间号；全部标记均无法解析时返回 <see langword="null"/>。</returns>
    private string? TryExtractNumericRoomIdFromPage(string html)
    {
        foreach ((string marker, char terminator) in RoomIdMarkers)
        {
            int markerIndex = html.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            string candidate = ReadUntil(html, markerIndex + marker.Length, terminator).Trim();
            if (IsPositiveInteger(candidate))
            {
                return candidate;
            }

            Logger.Debug(ModuleName, "直播间页面中的房间号标记无法解析为数字，尝试下一个标记。", new Dictionary<string, object?>
            {
                ["marker"] = marker,
            });
        }

        return null;
    }

    /// <summary>
    /// 调用 getInfoByRoom 获取房间标题、主播名、分区与封面。
    /// </summary>
    /// <param name="numericRoomId">数字房间号。</param>
    /// <param name="cookie">B站 Cookie，可为 <see langword="null"/>（匿名解析）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>房间信息快照。</returns>
    private async Task<RoomInfoSnapshot> FetchRoomInfoAsync(
        string numericRoomId,
        string? cookie,
        CancellationToken cancellationToken)
    {
        HttpRequestSpec spec = new()
        {
            Url = string.Concat(RoomInfoUrlPrefix, numericRoomId),
            Platform = PlatformId.Bilibili,
            Operation = OperationGetRoomInfo,
            Headers = BuildApiHeaders(cookie),
        };

        using JsonDocument document = await _http.GetJsonAsync(spec, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Fail(ResolveFailure.ParseError, OperationGetRoomInfo, "B站房间信息响应不是 JSON 对象。");
        }

        try
        {
            int code = ReadRequiredInt32(root, "code", OperationGetRoomInfo);
            string message = ReadOptionalString(root, "message") ?? string.Empty;
            bool hasData = TryReadProperty(root, "data", out JsonElement data)
                && data.ValueKind == JsonValueKind.Object;

            if (code == RoomNotFoundCode)
            {
                throw Fail(ResolveFailure.RoomNotFound, OperationGetRoomInfo, ResolveMessages.RoomNotFound);
            }

            // 风控/限流类错误码不是"房间不存在"，必须与真正的 404 区分，否则会把可用房间误报为不存在。
            if (IsRiskControlCode(code))
            {
                throw Fail(ResolveFailure.Rejected, OperationGetRoomInfo, $"{ResolveMessages.Rejected}（code={code}）");
            }

            if (code != 0)
            {
                throw Fail(ResolveFailure.ParseError, OperationGetRoomInfo, $"B站接口返回 code={code}：{message}");
            }

            if (!hasData)
            {
                throw Fail(ResolveFailure.RoomNotFound, OperationGetRoomInfo, ResolveMessages.RoomNotFound);
            }

            if (!TryReadProperty(data, "room_info", out JsonElement roomInfo)
                || roomInfo.ValueKind != JsonValueKind.Object)
            {
                throw Fail(ResolveFailure.ParseError, OperationGetRoomInfo, "B站响应缺少字段 room_info。");
            }

            if (!TryReadProperty(data, "anchor_info", out JsonElement anchorInfo)
                || anchorInfo.ValueKind != JsonValueKind.Object)
            {
                throw Fail(ResolveFailure.ParseError, OperationGetRoomInfo, "B站响应缺少字段 anchor_info。");
            }

            if (!TryReadProperty(anchorInfo, "base_info", out JsonElement baseInfo)
                || baseInfo.ValueKind != JsonValueKind.Object)
            {
                throw Fail(ResolveFailure.ParseError, OperationGetRoomInfo, "B站响应缺少字段 anchor_info.base_info。");
            }

            string title = ReadRequiredString(roomInfo, "title", OperationGetRoomInfo, "room_info.title");
            string anchor = ReadRequiredString(baseInfo, "uname", OperationGetRoomInfo, "anchor_info.base_info.uname");
            string category = ReadOptionalString(roomInfo, "area_name") ?? string.Empty;

            string? coverUrl = ReadOptionalString(roomInfo, "cover");
            if (string.IsNullOrWhiteSpace(coverUrl))
            {
                coverUrl = ReadOptionalString(roomInfo, "keyframe");
            }

            string roomId = TryReadInt64(roomInfo, "room_id", out long reportedRoomId) && reportedRoomId > 0
                ? reportedRoomId.ToString(CultureInfo.InvariantCulture)
                : numericRoomId;

            return new RoomInfoSnapshot(
                roomId,
                title,
                anchor,
                category,
                string.IsNullOrWhiteSpace(coverUrl) ? null : coverUrl);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            throw Fail(
                ResolveFailure.ParseError,
                OperationGetRoomInfo,
                $"B站房间信息响应结构不符合预期：{exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// 尝试用 getRoomBaseInfo 取主播名、标题与分区；任何失败都返回 <see langword="null"/>（不抛出）。
    /// </summary>
    /// <param name="numericRoomId">数字房间号。</param>
    /// <param name="cookie">B站 Cookie，可为 <see langword="null"/>（匿名解析）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>房间信息快照；接口不可用或结构变化时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 该接口是降级通道：失败只意味着"拿不到主播名与标题"，不应该让整个解析失败，
    /// 所以这里吞掉异常但必须记 Warn 日志。
    /// </remarks>
    private async Task<RoomInfoSnapshot?> TryFetchRoomBaseInfoAsync(
        string numericRoomId,
        string? cookie,
        CancellationToken cancellationToken)
    {
        HttpRequestSpec spec = new()
        {
            Url = string.Concat(RoomBaseInfoUrlPrefix, numericRoomId, RoomBaseInfoQuerySuffix),
            Platform = PlatformId.Bilibili,
            Operation = OperationGetRoomBaseInfo,
            Headers = BuildApiHeaders(cookie),
        };

        try
        {
            using JsonDocument document = await _http.GetJsonAsync(spec, cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || ReadRequiredInt32(root, "code", OperationGetRoomBaseInfo) != 0
                || !TryReadProperty(root, "data", out JsonElement data)
                || !TryReadProperty(data, "by_room_ids", out JsonElement byRoomIds)
                || byRoomIds.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (JsonProperty entry in byRoomIds.EnumerateObject())
            {
                JsonElement room = entry.Value;
                if (room.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string roomId = TryReadInt64(room, "room_id", out long reportedRoomId) && reportedRoomId > 0
                    ? reportedRoomId.ToString(CultureInfo.InvariantCulture)
                    : numericRoomId;

                return new RoomInfoSnapshot(
                    roomId,
                    ReadOptionalString(room, "title") ?? string.Empty,
                    ReadOptionalString(room, "uname") ?? string.Empty,
                    ReadOptionalString(room, "area_name") ?? string.Empty,
                    ReadOptionalString(room, "cover"));
            }

            return null;
        }
        catch (Exception exception) when (exception is ResolveException or JsonException or InvalidOperationException)
        {
            Logger.Warn(ModuleName, "房间基础信息接口不可用。", new Dictionary<string, object?>
            {
                ["roomId"] = numericRoomId,
                ["detail"] = exception.Message,
            });
            return null;
        }
    }

    /// <summary>
    /// 调用 getRoomPlayInfo 收集全部播放线路候选，并返回平台声明的直播状态。
    /// </summary>
    /// <param name="numericRoomId">数字房间号。</param>
    /// <param name="cookie">B站 Cookie，可为 <see langword="null"/>（匿名解析）。</param>
    /// <param name="failOnOfflineStatus">
    /// 为 <see langword="true"/> 时，直播状态为未开播/轮播直接抛异常；
    /// 为 <see langword="false"/> 时（房间信息接口也不可用）把状态交回调用方统一判定，
    /// 避免在信息缺失时把"未开播"与"接口不可用"混为一谈。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="requestedQuality">调用方指定的 qn，可为 <see langword="null"/>（最高档）。</param>
    /// <returns>候选流与平台声明的直播状态（未声明时为 -1）。</returns>
    private async Task<(IReadOnlyList<StreamCandidate> Candidates, int LiveStatus, IReadOnlyList<QualityOption> Qualities, string? SelectedKey)> FetchPlayInfoAsync(
        string numericRoomId,
        string? cookie,
        bool failOnOfflineStatus,
        CancellationToken cancellationToken,
        int? requestedQuality = null)
    {
        HttpRequestSpec spec = new()
        {
            Url = BuildPlayInfoUrl(numericRoomId, requestedQuality),
            Platform = PlatformId.Bilibili,
            Operation = OperationGetPlayInfo,
            Headers = BuildApiHeaders(cookie),
        };

        using JsonDocument document = await _http.GetJsonAsync(spec, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Fail(ResolveFailure.ParseError, OperationGetPlayInfo, "B站播放信息响应不是 JSON 对象。");
        }

        try
        {
            int code = ReadRequiredInt32(root, "code", OperationGetPlayInfo);
            string message = ReadOptionalString(root, "message") ?? string.Empty;
            bool hasData = TryReadProperty(root, "data", out JsonElement data)
                && data.ValueKind == JsonValueKind.Object;

            // 先读 live_status：未开播/轮播时接口可能仍返回 code=0，但没有任何可用线路。
            int observedLiveStatus = -1;
            if (hasData && TryReadInt32(data, LiveStatusPropertyName, out int liveStatus))
            {
                observedLiveStatus = liveStatus;
                if (failOnOfflineStatus && liveStatus == LiveStatusOffline)
                {
                    throw Fail(ResolveFailure.NotLive, OperationGetPlayInfo, ResolveMessages.NotLive);
                }

                if (failOnOfflineStatus && liveStatus == LiveStatusReplaying)
                {
                    throw Fail(ResolveFailure.Replaying, OperationGetPlayInfo, ResolveMessages.Replaying);
                }
            }

            if (code != 0)
            {
                throw Fail(ResolveFailure.ParseError, OperationGetPlayInfo, $"B站接口返回 code={code}：{message}");
            }

            if (!hasData)
            {
                throw Fail(ResolveFailure.ParseError, OperationGetPlayInfo, "B站响应缺少字段 data。");
            }

            StreamCandidateBuilder builder = new(PlatformId.Bilibili, Logger);
            IReadOnlyList<QualityOption> qualities = [];
            string? selectedKey = null;
            if (hasData && HasPlayUrlInfo(data))
            {
                CollectCandidates(data, builder);
                (qualities, selectedKey) = BuildQualityOptions(data, requestedQuality);
            }

            // 不在这里判定"未开播"：候选为空的最终分类由调用方结合房间信息是否可用统一决定。
            return (builder.Build(), observedLiveStatus, qualities, selectedKey);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            throw Fail(
                ResolveFailure.ParseError,
                OperationGetPlayInfo,
                $"B站播放信息响应结构不符合预期：{exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// 判断播放信息响应中是否包含线路结构（未开播时接口通常不返回该节点）。
    /// </summary>
    /// <param name="playData">getRoomPlayInfo 响应的 data 节点。</param>
    /// <returns>包含 playurl_info.playurl 时返回 <see langword="true"/>。</returns>
    private static bool HasPlayUrlInfo(JsonElement playData)
    {
        if (!playData.TryGetProperty("playurl_info", out JsonElement playUrlInfo)
            || playUrlInfo.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return playUrlInfo.TryGetProperty("playurl", out JsonElement playUrl)
            && playUrl.ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    /// 遍历 playurl_info.playurl.stream，按 stream → format → codec → url_info 的嵌套顺序收集候选。
    /// </summary>
    /// <param name="playData">getRoomPlayInfo 响应的 data 节点。</param>
    /// <param name="builder">候选构造器（同一房间共用，保证源索引按遍历顺序从 0 递增）。</param>
    private void CollectCandidates(JsonElement playData, StreamCandidateBuilder builder)
    {
        if (!TryReadProperty(playData, "playurl_info", out JsonElement playUrlInfo)
            || playUrlInfo.ValueKind != JsonValueKind.Object)
        {
            throw Fail(ResolveFailure.ParseError, OperationGetPlayInfo, "B站未返回播放线路：响应缺少字段 playurl_info。");
        }

        if (!TryReadProperty(playUrlInfo, "playurl", out JsonElement playUrl)
            || playUrl.ValueKind != JsonValueKind.Object)
        {
            throw Fail(ResolveFailure.ParseError, OperationGetPlayInfo, "B站未返回播放线路：响应缺少字段 playurl_info.playurl。");
        }

        if (!TryReadProperty(playUrl, "stream", out JsonElement streams)
            || streams.ValueKind != JsonValueKind.Array
            || streams.GetArrayLength() == 0)
        {
            throw Fail(ResolveFailure.ParseError, OperationGetPlayInfo, "B站未返回播放线路。");
        }

        foreach (JsonElement stream in streams.EnumerateArray())
        {
            CollectFromStream(stream, builder);
        }
    }

    /// <summary>
    /// 处理单条 stream 节点：读取协议名并下钻到 format 数组。
    /// </summary>
    /// <param name="stream">stream 数组中的单个元素。</param>
    /// <param name="builder">候选构造器。</param>
    private void CollectFromStream(JsonElement stream, StreamCandidateBuilder builder)
    {
        if (stream.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? protocolName = ReadOptionalString(stream, "protocol_name");
        if (!TryReadProperty(stream, "format", out JsonElement formats) || formats.ValueKind != JsonValueKind.Array)
        {
            Logger.Debug(ModuleName, "跳过缺少 format 的 B站播放线路。", new Dictionary<string, object?>
            {
                ["protocol"] = protocolName,
            });
            return;
        }

        foreach (JsonElement format in formats.EnumerateArray())
        {
            CollectFromFormat(protocolName, format, builder);
        }
    }

    /// <summary>
    /// 处理单个 format 节点：映射容器格式并下钻到 codec 数组；未知格式只记 Debug 不抛异常。
    /// </summary>
    /// <param name="protocolName">所属 stream 的 protocol_name。</param>
    /// <param name="format">format 数组中的单个元素。</param>
    /// <param name="builder">候选构造器。</param>
    private void CollectFromFormat(string? protocolName, JsonElement format, StreamCandidateBuilder builder)
    {
        if (format.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? formatName = ReadOptionalString(format, "format_name");
        StreamFormat? streamFormat = MapStreamFormat(protocolName, formatName);
        if (streamFormat is null)
        {
            Logger.Debug(ModuleName, "跳过无法识别的 B站流格式。", new Dictionary<string, object?>
            {
                ["protocol"] = protocolName,
                ["format"] = formatName,
            });
            return;
        }

        if (!TryReadProperty(format, "codec", out JsonElement codecs) || codecs.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement codec in codecs.EnumerateArray())
        {
            CollectFromCodec(streamFormat.Value, codec, builder);
        }
    }

    /// <summary>
    /// 处理单个 codec 节点：判定编码与画质，并下钻到 url_info 数组。
    /// </summary>
    /// <param name="streamFormat">所属 format 已判定的容器/传输格式。</param>
    /// <param name="codec">codec 数组中的单个元素。</param>
    /// <param name="builder">候选构造器。</param>
    private void CollectFromCodec(StreamFormat streamFormat, JsonElement codec, StreamCandidateBuilder builder)
    {
        if (codec.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? baseUrl = ReadOptionalString(codec, "base_url");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Logger.Debug(ModuleName, "跳过缺少 base_url 的 B站编码线路。", new Dictionary<string, object?>
            {
                ["format"] = streamFormat.ToString(),
            });
            return;
        }

        VideoCodec videoCodec = MapVideoCodec(ReadOptionalString(codec, "codec_name"));
        StreamQuality quality = ResolveQuality(codec);

        if (!TryReadProperty(codec, "url_info", out JsonElement urlInfos) || urlInfos.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement urlInfo in urlInfos.EnumerateArray())
        {
            AddCandidate(builder, urlInfo, baseUrl, streamFormat, videoCodec, quality);
        }
    }

    /// <summary>
    /// 处理单条 url_info：按 host + base_url + extra 拼接地址并加入构造器。
    /// </summary>
    /// <param name="builder">候选构造器。</param>
    /// <param name="urlInfo">url_info 数组中的单个元素。</param>
    /// <param name="baseUrl">同一 codec 下的 base_url。</param>
    /// <param name="streamFormat">已判定的容器/传输格式。</param>
    /// <param name="videoCodec">已判定的视频编码。</param>
    /// <param name="quality">已判定的画质档位。</param>
    private void AddCandidate(
        StreamCandidateBuilder builder,
        JsonElement urlInfo,
        string baseUrl,
        StreamFormat streamFormat,
        VideoCodec videoCodec,
        StreamQuality quality)
    {
        if (urlInfo.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? cdnHost = ReadOptionalString(urlInfo, "host");
        if (string.IsNullOrWhiteSpace(cdnHost))
        {
            Logger.Debug(ModuleName, "跳过缺少 CDN host 的 B站流地址。", new Dictionary<string, object?>
            {
                ["format"] = streamFormat.ToString(),
            });
            return;
        }

        string? extra = ReadOptionalString(urlInfo, "extra");
        string url = string.Concat(cdnHost, baseUrl, extra);
        DateTimeOffset? expiresAt = ResolveExpiry(baseUrl, extra);

        if (!builder.TryAdd(url, streamFormat, videoCodec, quality, expiresAt, PlaybackReferer, cdnHost))
        {
            Logger.Debug(ModuleName, "B站候选地址非法，已丢弃。", new Dictionary<string, object?>
            {
                ["format"] = streamFormat.ToString(),
                ["fingerprint"] = SensitiveData.Fingerprint(url),
            });
        }
    }

    /// <summary>
    /// 构造接口请求头：仅当 Cookie 非空时注入（Cookie 值永不写入日志）。
    /// </summary>
    /// <param name="cookie">B站 Cookie，可为 <see langword="null"/>。</param>
    /// <returns>请求头；无需附加头时返回 <see langword="null"/>。</returns>
    private static IReadOnlyDictionary<string, string>? BuildApiHeaders(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HeaderNameCookie] = cookie,
        };
    }

    /// <summary>
    /// 构造 getRoomPlayInfo 请求地址，画质取调用方指定的 qn（默认最高档）。
    /// </summary>
    /// <param name="numericRoomId">数字房间号。</param>
    /// <param name="qualityNumber">qn 数值；为 <see langword="null"/> 时使用最高档。</param>
    /// <returns>完整请求地址。</returns>
    private static string BuildPlayInfoUrl(string numericRoomId, int? qualityNumber)
    {
        int qn = qualityNumber ?? QualityNames.BilibiliMaxQualityNumber;
        return string.Concat(
            PlayInfoUrlPrefix,
            PlayInfoQueryPrefix,
            qn.ToString(CultureInfo.InvariantCulture),
            PlayInfoQuerySuffix,
            numericRoomId);
    }

    /// <summary>
    /// 把调用方给的档位键解析为 qn 数值；不是合法整数时返回 <see langword="null"/>（按最高档处理）。
    /// </summary>
    /// <param name="qualityKey">档位键（B站为 qn 数值字符串）。</param>
    /// <returns>qn 数值，或 <see langword="null"/>。</returns>
    private static int? ParseQualityNumber(string? qualityKey) =>
        int.TryParse(qualityKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out int qn) ? qn : null;

    /// <summary>
    /// 从播放信息响应中枚举可选画质档位（含官方名称与 HDR 标记）。
    /// </summary>
    /// <param name="playData">getRoomPlayInfo 响应的 data 节点。</param>
    /// <param name="requestedQuality">本次请求的 qn，可为 <see langword="null"/>。</param>
    /// <returns>档位列表（按从高到低）与实际生效的档位键。</returns>
    /// <remarks>
    /// 官方把名称放在 <c>g_qn_desc</c>（<c>qn</c> + <c>desc</c> + <c>hdr_desc</c>），
    /// 可用档位放在 <c>codec[].accept_qn</c>；两者都缺失时退化为"只有最高档"。
    /// </remarks>
    private static (IReadOnlyList<QualityOption> Qualities, string? SelectedKey) BuildQualityOptions(
        JsonElement playData,
        int? requestedQuality)
    {
        Dictionary<int, (string Name, string Hdr)> names = ReadQualityNames(playData);
        List<int> available = ReadAcceptedQualities(playData);
        if (available.Count == 0)
        {
            available.AddRange(names.Keys);
        }

        if (available.Count == 0 && requestedQuality is { } requested)
        {
            available.Add(requested);
        }

        available.Sort(static (left, right) => right.CompareTo(left));
        bool has4K = available.Contains(QualityNames.BilibiliQuality4K);
        List<QualityOption> options = [];
        foreach (int qn in available)
        {
            options.Add(new QualityOption
            {
                Key = qn.ToString(CultureInfo.InvariantCulture),
                Label = BuildQualityLabel(qn, names, has4K),
                IsBest = options.Count == 0,
            });
        }

        int? effective = requestedQuality is { } want && available.Contains(want)
            ? want
            : available.Count > 0 ? available[0] : requestedQuality;

        return (options, effective?.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>读取 g_qn_desc 中的官方档位名与 HDR 标记。</summary>
    /// <param name="playData">播放信息 data 节点。</param>
    /// <returns>qn →（名称，HDR 标记）映射。</returns>
    private static Dictionary<int, (string Name, string Hdr)> ReadQualityNames(JsonElement playData)
    {
        Dictionary<int, (string Name, string Hdr)> names = [];
        if (!TryReadProperty(playData, "playurl_info", out JsonElement info)
            || !TryReadProperty(info, "playurl", out JsonElement playUrl)
            || !TryReadProperty(playUrl, "g_qn_desc", out JsonElement descriptions)
            || descriptions.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (JsonElement item in descriptions.EnumerateArray())
        {
            if (!TryReadInt32(item, "qn", out int qn))
            {
                continue;
            }

            names[qn] = (
                ReadOptionalString(item, "desc") ?? string.Empty,
                ReadOptionalString(item, "hdr_desc") ?? string.Empty);
        }

        return names;
    }

    /// <summary>读取 codec[].accept_qn 汇总可用档位（去重）。</summary>
    /// <param name="playData">播放信息 data 节点。</param>
    /// <returns>可用 qn 列表。</returns>
    private static List<int> ReadAcceptedQualities(JsonElement playData)
    {
        List<int> result = [];
        if (!TryReadProperty(playData, "playurl_info", out JsonElement info)
            || !TryReadProperty(info, "playurl", out JsonElement playUrl)
            || !TryReadProperty(playUrl, "stream", out JsonElement streams)
            || streams.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement stream in streams.EnumerateArray())
        {
            if (!TryReadProperty(stream, "format", out JsonElement formats) || formats.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement format in formats.EnumerateArray())
            {
                if (!TryReadProperty(format, "codec", out JsonElement codecs) || codecs.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement codec in codecs.EnumerateArray())
                {
                    if (!TryReadProperty(codec, "accept_qn", out JsonElement accepted) || accepted.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (JsonElement value in accepted.EnumerateArray())
                    {
                        if (value.ValueKind == JsonValueKind.Number
                            && value.TryGetInt32(out int qn)
                            && !result.Contains(qn))
                        {
                            result.Add(qn);
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>生成档位显示名，命名与官方直播间一致（用户可对照）。</summary>
    /// <param name="qn">档位数值。</param>
    /// <param name="names">官方名称表（qn → 名称、HDR 标记）。</param>
    /// <param name="has4K">本房间是否提供 4K 档位（决定 1080P 档叫"原画"还是"高码率"）。</param>
    /// <returns>显示名。</returns>
    /// <remarks>
    /// 命名规则：有 4K 时依次为 4K 原画（高帧率）→ 1080P 高码率（高帧率）→ 1080P 蓝光 → 720P 超清；
    /// 无 4K 时依次为 1080P 原画（高帧率）→ 1080P 蓝光 → 720P 超清。
    /// 平台的 hdr_desc 为 HDR 时，标记写在"高帧率"之前；接口不逐档返回帧率，
    /// 因此高帧率按 B站 的档位语义（1080P 原画 / 4K 原画 / 高码率）判定。
    /// </remarks>
    private static string BuildQualityLabel(int qn, Dictionary<int, (string Name, string Hdr)> names, bool has4K)
    {
        bool hdr = names.TryGetValue(qn, out (string Name, string Hdr) hdrEntry)
            && hdrEntry.Hdr.Contains("HDR", StringComparison.OrdinalIgnoreCase);

        return qn switch
        {
            QualityNames.BilibiliMaxQualityNumber => "杜比原画",
            QualityNames.BilibiliQuality4K => Decorate("4K 原画", hdr, highFrameRate: true),
            QualityNames.BilibiliQuality2K => Decorate("2K 原画", hdr, highFrameRate: true),
            QualityNames.BilibiliQuality1080HighFps => has4K
                ? Decorate("1080P 高码率", hdr, highFrameRate: true)
                : Decorate("1080P 原画", hdr, highFrameRate: true),
            QualityNames.BilibiliQuality1080 => Decorate("1080P 蓝光", hdr, highFrameRate: false),
            QualityNames.BilibiliQuality720 => Decorate("720P 超清", hdr, highFrameRate: false),
            QualityNames.BilibiliQuality480 => Decorate("高清", hdr, highFrameRate: false),
            80 => "流畅",
            _ => names.TryGetValue(qn, out (string Name, string Hdr) entry) && entry.Name.Length > 0
                ? entry.Name
                : $"qn={qn}",
        };
    }

    /// <summary>按需要给档位名补上（HDR 高帧率）后缀。</summary>
    /// <param name="name">档位名（例如「1080P 原画」）。</param>
    /// <param name="hdr">平台是否把该档标记为 HDR。</param>
    /// <param name="highFrameRate">是否属于高帧率档。</param>
    /// <returns>带后缀的显示名；两种标记都没有时原样返回。</returns>
    private static string Decorate(string name, bool hdr, bool highFrameRate)
    {
        List<string> marks = [];
        if (hdr)
        {
            marks.Add("HDR");
        }

        if (highFrameRate)
        {
            marks.Add("高帧率");
        }

        return marks.Count == 0 ? name : name + "（" + string.Join(" ", marks) + "）";
    }

    /// <summary>
    /// 映射 B站的协议名与容器名到内部格式；未知组合返回 <see langword="null"/>。
    /// </summary>
    /// <param name="protocolName">protocol_name 字段值。</param>
    /// <param name="formatName">format_name 字段值。</param>
    /// <returns>内部格式；无法识别时返回 <see langword="null"/>。</returns>
    private static StreamFormat? MapStreamFormat(string? protocolName, string? formatName)
    {
        bool isKnownProtocol = string.Equals(protocolName, ProtocolHttpStream, StringComparison.OrdinalIgnoreCase)
            || string.Equals(protocolName, ProtocolHttpHls, StringComparison.OrdinalIgnoreCase);
        if (!isKnownProtocol)
        {
            return null;
        }

        if (string.Equals(formatName, FormatNameFlv, StringComparison.OrdinalIgnoreCase))
        {
            return StreamFormat.FlvHttp;
        }

        if (string.Equals(formatName, FormatNameTs, StringComparison.OrdinalIgnoreCase))
        {
            return StreamFormat.HlsTs;
        }

        if (string.Equals(formatName, FormatNameFmp4, StringComparison.OrdinalIgnoreCase))
        {
            return StreamFormat.HlsFmp4;
        }

        return null;
    }

    /// <summary>
    /// 映射 B站的编码名到内部编码枚举；未知编码返回 <see cref="VideoCodec.Unknown"/>。
    /// </summary>
    /// <param name="codecName">codec_name 字段值。</param>
    /// <returns>内部编码枚举。</returns>
    private static VideoCodec MapVideoCodec(string? codecName) => codecName?.ToLowerInvariant() switch
    {
        CodecNameAvc => VideoCodec.Avc,
        CodecNameHevc => VideoCodec.Hevc,
        CodecNameAv1 => VideoCodec.Av1,
        _ => VideoCodec.Unknown,
    };

    /// <summary>
    /// 推导画质：优先 current_qn，缺失时取 accept_qn 的首个数值。
    /// </summary>
    /// <param name="codec">codec JSON 节点。</param>
    /// <returns>画质档位；无法判定时返回 <see cref="StreamQuality.Unknown"/>。</returns>
    private static StreamQuality ResolveQuality(JsonElement codec)
    {
        if (TryReadInt32(codec, "current_qn", out int currentQuality))
        {
            return QualityNames.FromBilibiliQualityNumber(currentQuality);
        }

        if (!TryReadProperty(codec, "accept_qn", out JsonElement acceptedQualities)
            || acceptedQualities.ValueKind != JsonValueKind.Array)
        {
            return StreamQuality.Unknown;
        }

        foreach (JsonElement acceptedQuality in acceptedQualities.EnumerateArray())
        {
            if (acceptedQuality.ValueKind == JsonValueKind.Number && acceptedQuality.TryGetInt32(out int qualityNumber))
            {
                return QualityNames.FromBilibiliQualityNumber(qualityNumber);
            }
        }

        return StreamQuality.Unknown;
    }

    /// <summary>
    /// 解析候选地址的有效期：依次在 extra 与 base_url 中查找 expires 查询参数。
    /// </summary>
    /// <param name="baseUrl">codec 的 base_url。</param>
    /// <param name="extra">url_info 的 extra。</param>
    /// <returns>有效期（UTC）；未声明时返回 <see langword="null"/>。</returns>
    private static DateTimeOffset? ResolveExpiry(string? baseUrl, string? extra) =>
        TryParseExpiryFromQuery(extra) ?? TryParseExpiryFromQuery(baseUrl);

    /// <summary>
    /// 在文本中查找所有 <c>?</c> 起始的查询段，返回首个可解析的 expires 时间戳。
    /// </summary>
    /// <param name="source">可能包含查询串的文本（base_url 或 extra）。</param>
    /// <returns>有效期（UTC）；未声明或无法解析时返回 <see langword="null"/>。</returns>
    private static DateTimeOffset? TryParseExpiryFromQuery(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        int queryIndex = source.IndexOf('?');
        while (queryIndex >= 0 && queryIndex < source.Length - 1)
        {
            Dictionary<string, List<string>> query = QueryStringParser.Parse(source[(queryIndex + 1)..]);
            DateTimeOffset? expiresAt = QueryStringParser.ParseUnixTimestamp(
                QueryStringParser.GetFirst(query, ExpiresParameterName));
            if (expiresAt is not null)
            {
                return expiresAt;
            }

            queryIndex = source.IndexOf('?', queryIndex + 1);
        }

        return null;
    }

    /// <summary>
    /// 判断文本是否只由 ASCII 数字组成（B站数字房间号）。
    /// </summary>
    /// <param name="value">待判断文本。</param>
    /// <returns>全部为数字且非空时返回 <see langword="true"/>。</returns>
    private static bool IsAllDigits(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 判断文本是否可解析为正整数（用于页面中的房间号取值）。
    /// </summary>
    /// <param name="value">待判断文本。</param>
    /// <returns>可解析为正整数时返回 <see langword="true"/>。</returns>
    private static bool IsPositiveInteger(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
        && parsed > 0;

    /// <summary>
    /// 读取 <paramref name="startIndex"/> 之后、<paramref name="terminator"/> 之前的文本。
    /// </summary>
    /// <param name="text">源文本。</param>
    /// <param name="startIndex">起始下标。</param>
    /// <param name="terminator">结束字符。</param>
    /// <returns>截取到的文本；越界时返回空字符串。</returns>
    private static string ReadUntil(string text, int startIndex, char terminator)
    {
        if (startIndex >= text.Length)
        {
            return string.Empty;
        }

        int endIndex = text.IndexOf(terminator, startIndex);
        return endIndex < 0 ? text[startIndex..] : text[startIndex..endIndex];
    }

    /// <summary>
    /// 安全读取子节点：仅在父节点是 JSON 对象时才访问其属性，避免对非对象节点取值抛异常。
    /// </summary>
    /// <param name="parent">父节点。</param>
    /// <param name="propertyName">属性名。</param>
    /// <param name="value">属性值。</param>
    /// <returns>属性存在时返回 <see langword="true"/>。</returns>
    private static bool TryReadProperty(JsonElement parent, string propertyName, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(propertyName, out JsonElement found))
        {
            value = found;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// 读取字符串属性；属性缺失或不是字符串时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="parent">父节点。</param>
    /// <param name="propertyName">属性名。</param>
    /// <returns>属性值，或 <see langword="null"/>。</returns>
    private static string? ReadOptionalString(JsonElement parent, string propertyName)
    {
        if (!TryReadProperty(parent, propertyName, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return element.GetString();
    }

    /// <summary>
    /// 读取必填字符串属性；缺失或为空白时按解析错误抛出。
    /// </summary>
    /// <param name="parent">父节点。</param>
    /// <param name="propertyName">属性名。</param>
    /// <param name="operation">操作名（用于失败分类与日志）。</param>
    /// <param name="fieldPath">字段路径（用于错误消息）。</param>
    /// <returns>属性值。</returns>
    private string ReadRequiredString(JsonElement parent, string propertyName, string operation, string fieldPath)
    {
        string? value = ReadOptionalString(parent, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Fail(ResolveFailure.ParseError, operation, $"B站响应缺少字段 {fieldPath}。");
        }

        return value;
    }

    /// <summary>
    /// 读取整数属性；缺失或不是整数时按解析错误抛出。
    /// </summary>
    /// <param name="parent">父节点。</param>
    /// <param name="propertyName">属性名。</param>
    /// <param name="operation">操作名（用于失败分类与日志）。</param>
    /// <returns>属性值。</returns>
    private int ReadRequiredInt32(JsonElement parent, string propertyName, string operation)
    {
        if (TryReadInt32(parent, propertyName, out int value))
        {
            return value;
        }

        throw Fail(ResolveFailure.ParseError, operation, $"B站响应缺少字段 {propertyName} 或其取值不是整数。");
    }

    /// <summary>
    /// 尝试读取 32 位整数属性（兼容数字与数字字符串）。
    /// </summary>
    /// <param name="parent">父节点。</param>
    /// <param name="propertyName">属性名。</param>
    /// <param name="value">属性值。</param>
    /// <returns>读取成功时返回 <see langword="true"/>。</returns>
    private static bool TryReadInt32(JsonElement parent, string propertyName, out int value)
    {
        value = 0;
        if (!TryReadProperty(parent, propertyName, out JsonElement element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt32(out value);
        }

        return element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// 尝试读取 64 位整数属性（兼容数字与数字字符串）。
    /// </summary>
    /// <param name="parent">父节点。</param>
    /// <param name="propertyName">属性名。</param>
    /// <param name="value">属性值。</param>
    /// <returns>读取成功时返回 <see langword="true"/>。</returns>
    private static bool TryReadInt64(JsonElement parent, string propertyName, out long value)
    {
        value = 0;
        if (!TryReadProperty(parent, propertyName, out JsonElement element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out value);
        }

        return element.ValueKind == JsonValueKind.String
            && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>B站房间信息的解析中间结果。</summary>
    /// <param name="RoomId">数字房间号（平台返回值优先）。</param>
    /// <param name="Title">直播间标题。</param>
    /// <param name="Anchor">主播名。</param>
    /// <param name="Category">直播分区名，缺失时为空字符串。</param>
    /// <param name="CoverUrl">封面地址，缺失时为 <see langword="null"/>。</param>
    private sealed record RoomInfoSnapshot(
        string RoomId,
        string Title,
        string Anchor,
        string Category,
        string? CoverUrl);
}
