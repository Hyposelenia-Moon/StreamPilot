namespace StreamPilot.Parsers.Bilibili;

using System.Globalization;
using System.Text.Json;
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
    protected override async Task<ResolvedRoom> OnParseAsync(RoomQuery query, CancellationToken cancellationToken)
    {
        string numericRoomId = await ResolveNumericRoomIdAsync(query, cancellationToken).ConfigureAwait(false);
        RoomInfoSnapshot room = await FetchRoomInfoAsync(numericRoomId, query.BilibiliCookie, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<StreamCandidate> candidates = await FetchPlayInfoAsync(room.RoomId, query.BilibiliCookie, cancellationToken)
            .ConfigureAwait(false);

        Logger.Info(ModuleName, "B站直播间解析完成。", new Dictionary<string, object?>
        {
            ["roomId"] = room.RoomId,
            ["candidateCount"] = candidates.Count,
        });

        return new ResolvedRoom
        {
            Platform = PlatformId.Bilibili,
            RoomId = room.RoomId,
            Anchor = room.Anchor,
            Title = room.Title,
            Category = room.Category,
            Candidates = candidates,
            ResolvedAt = DateTimeOffset.UtcNow,
            CoverUrl = room.CoverUrl,
        };
    }

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

            if (code == RoomNotFoundCode || (code != 0 && !hasData))
            {
                throw Fail(ResolveFailure.RoomNotFound, OperationGetRoomInfo, ResolveMessages.RoomNotFound);
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
    /// 调用 getRoomPlayInfo 收集全部播放线路候选。
    /// </summary>
    /// <param name="numericRoomId">数字房间号。</param>
    /// <param name="cookie">B站 Cookie，可为 <see langword="null"/>（匿名解析）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按平台返回顺序排列的候选流。</returns>
    private async Task<IReadOnlyList<StreamCandidate>> FetchPlayInfoAsync(
        string numericRoomId,
        string? cookie,
        CancellationToken cancellationToken)
    {
        HttpRequestSpec spec = new()
        {
            Url = BuildPlayInfoUrl(numericRoomId),
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
            if (hasData && TryReadInt32(data, LiveStatusPropertyName, out int liveStatus))
            {
                if (liveStatus == LiveStatusOffline)
                {
                    throw Fail(ResolveFailure.NotLive, OperationGetPlayInfo, ResolveMessages.NotLive);
                }

                if (liveStatus == LiveStatusReplaying)
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
            CollectCandidates(data, builder);
            if (builder.Count == 0)
            {
                throw Fail(
                    ResolveFailure.NotLive,
                    OperationGetPlayInfo,
                    "B站未返回可用播放线路，直播间可能未开播或处于断流状态。");
            }

            return builder.Build();
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
    /// 构造 getRoomPlayInfo 请求地址，画质取 <see cref="QualityNames.BilibiliMaxQualityNumber"/>。
    /// </summary>
    /// <param name="numericRoomId">数字房间号。</param>
    /// <returns>完整请求地址。</returns>
    private static string BuildPlayInfoUrl(string numericRoomId)
    {
        string maxQualityNumber = QualityNames.BilibiliMaxQualityNumber.ToString(CultureInfo.InvariantCulture);
        return $"{PlayInfoUrlPrefix}{PlayInfoQueryPrefix}{maxQualityNumber}{PlayInfoQuerySuffix}{numericRoomId}";
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
