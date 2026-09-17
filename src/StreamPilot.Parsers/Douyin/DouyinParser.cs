namespace StreamPilot.Parsers.Douyin;

using System.Text.Json;
using System.Text.RegularExpressions;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Parsers;

/// <summary>
/// 抖音直播解析器。
/// </summary>
/// <remarks>
/// 主路径抓取 <c>live.douyin.com/{房间号}</c> 页面并抽取内嵌的 roomStore 状态；
/// 页面不可用（风控精简页、结构变更）或未给出可用地址时，回退到 webcast reflow 接口。
/// 不实现 <c>a_bogus</c>/<c>ms_token</c> 等签名与风控绕过（见 docs/adr/0003-解析器实现.md 第 5 节）。
/// </remarks>
internal sealed class DouyinParser : PlatformParserBase
{
    /// <summary>房间页地址前缀（不含斜杠，便于拼接房间号）。</summary>
    private const string RoomPageBase = "https://live.douyin.com";

    /// <summary>reflow 回退接口地址。</summary>
    private const string ReflowEndpoint = "https://webcast.amemv.com/webcast/room/reflow/info/";

    /// <summary>reflow 回退接口的固定查询串。</summary>
    private const string ReflowQuery = "?type_id=0&live_id=1&room_id=";

    /// <summary>抓取房间页与标识候选地址时使用的 Referer。</summary>
    private const string RoomUrlReferer = "https://live.douyin.com/";

    /// <summary>日志模块名。</summary>
    private const string ModuleName = "Parsers.Douyin";

    /// <summary>房间页状态抽取的操作名。</summary>
    private const string RoomStateOperation = "room-state";

    /// <summary>reflow 回退接口的操作名。</summary>
    private const string ReflowOperation = "reflow-info";

    /// <summary>播放地址抽取的操作名。</summary>
    private const string StreamUrlOperation = "stream-url";

    /// <summary>输入校验的操作名。</summary>
    private const string ValidateOperation = "validate";

    /// <summary>状态 JSON 在正则中的捕获组序号。</summary>
    private const int StateJsonGroupIndex = 1;

    /// <summary>抖音"已结束直播"的房间状态值。</summary>
    private const int EndedRoomStatus = 4;

    /// <summary>主播名缺失时的提示。</summary>
    private const string MissingAnchorMessage = "抖音未返回主播名。";

    /// <summary>页面与 reflow 接口均不可用时的提示。</summary>
    private const string NoStreamMessage = "抖音直播间页面与 reflow 接口均未返回可用播放地址。";

    /// <summary>两条路径都拿到了状态但没有可用地址时的提示。</summary>
    private const string NoCandidateMessage = "抖音未返回可用播放地址。";

    /// <summary>无法从输入确定房间号时的提示。</summary>
    private const string MissingRoomIdMessage = "无法从输入中确定抖音直播间房间号。";

    /// <summary>roomStore 状态 JSON 的抽取正则（页面内嵌 JSON 的引号被反斜杠转义）。</summary>
    private static readonly Regex RoomStatePattern = new(
        """\{\\"state\\":(.+?\}),\\"children\\":""",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>画质键名优先级（从高到低），FLV 与 HLS 各自独立选取。</summary>
    private static readonly string[] QualityPreferenceOrder =
    [
        QualityNames.DouyinQualityFullHd1,
        QualityNames.DouyinQualityHd1,
        QualityNames.DouyinQualitySd1,
        QualityNames.DouyinQualitySd2,
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

    private readonly HttpTextClient _http;

    /// <summary>初始化解析器。</summary>
    /// <param name="http">带超时与有界重试的 HTTP 客户端。</param>
    /// <param name="logger">结构化日志。</param>
    public DouyinParser(HttpTextClient http, IStructuredLogger logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <inheritdoc />
    public override PlatformId Platform => PlatformId.Douyin;

    /// <inheritdoc />
    public override string DisplayName => "抖音";

    /// <inheritdoc />
    public override string RoomUrlPrefix => "https://live.douyin.com/";

    /// <inheritdoc />
    protected override async Task<ResolvedRoom> OnParseAsync(RoomQuery query, CancellationToken cancellationToken)
    {
        string roomId = ResolveRoomId(query);
        StreamCandidateBuilder builder = new(Platform, Logger);

        ResolvedRoom? room = await TryParseRoomPageAsync(roomId, builder, cancellationToken).ConfigureAwait(false);
        room ??= await TryParseReflowAsync(roomId, builder, cancellationToken).ConfigureAwait(false);

        if (room is null)
        {
            throw Fail(ResolveFailure.ParseError, ReflowOperation, NoStreamMessage);
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
            ? TryExtractRoomIdFromUrl(query.RoomUrl)
            : query.RoomId;

        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw Fail(ResolveFailure.InvalidInput, ValidateOperation, MissingRoomIdMessage);
        }

        return roomId;
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
    /// <param name="roomId">房间号。</param>
    /// <param name="builder">候选构造器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析结果；页面结构不可用或没有可用地址时返回 <see langword="null"/> 以便回退 reflow。</returns>
    private async Task<ResolvedRoom?> TryParseRoomPageAsync(
        string roomId,
        StreamCandidateBuilder builder,
        CancellationToken cancellationToken)
    {
        string html = await FetchRoomPageAsync(roomId, cancellationToken).ConfigureAwait(false);
        string? stateJson = ExtractRoomStateJson(html);
        if (stateJson is null)
        {
            Logger.Debug(ModuleName, "抖音房间页未找到 roomStore 状态，回退 reflow 接口。", new Dictionary<string, object?>
            {
                ["operation"] = RoomStateOperation,
                ["roomId"] = roomId,
            });
            return null;
        }

        using JsonDocument? document = TryParseDocument(stateJson, RoomStateOperation);
        if (document is null)
        {
            return null;
        }

        JsonElement roomInfo = GetNestedProperty(document.RootElement, RoomStoreField, RoomInfoField);
        if (roomInfo.ValueKind != JsonValueKind.Object)
        {
            Logger.Debug(ModuleName, "抖音房间页状态缺少 roomInfo，回退 reflow 接口。", new Dictionary<string, object?>
            {
                ["operation"] = RoomStateOperation,
                ["roomId"] = roomId,
            });
            return null;
        }

        JsonElement anchor = GetPropertyOrUndefined(roomInfo, AnchorField);
        if (anchor.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw Fail(ResolveFailure.RoomNotFound, RoomStateOperation, ResolveMessages.RoomNotFound);
        }

        string? anchorName = ReadString(anchor, NicknameField);
        if (string.IsNullOrWhiteSpace(anchorName))
        {
            throw Fail(ResolveFailure.ParseError, RoomStateOperation, MissingAnchorMessage);
        }

        JsonElement room = GetPropertyOrUndefined(roomInfo, RoomField);
        if (room.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw Fail(ResolveFailure.NotLive, RoomStateOperation, ResolveMessages.NotLive);
        }

        bool added = CollectCandidates(builder, room, RoomStateOperation, out string title, out string category);
        return added ? CreateRoom(roomId, anchorName, title, category, builder) : null;
    }

    /// <summary>从房间页 HTML 中抽取 roomStore 状态 JSON 文本。</summary>
    /// <param name="html">页面 HTML。</param>
    /// <returns>反转义后的 JSON 文本；正则未命中时返回 <see langword="null"/>。</returns>
    private static string? ExtractRoomStateJson(string html)
    {
        Match match = RoomStatePattern.Match(html);
        if (!match.Success)
        {
            return null;
        }

        // 页面内嵌 JSON 位于脚本字符串中，反斜杠与引号被转义：
        // 先把 \\\" 还原为 \"（保留字符串内的转义引号），再单独把 \" 还原为 " 是不安全的，
        // 因此这里只做一次"转义双引号 → 双引号"的还原（足够覆盖 roomStore 状态片段；
        // 若片段内确实含有转义引号，JsonDocument 会拒绝解析并走 reflow 回退路径）。
        string json = match.Groups[StateJsonGroupIndex].Value
            .Replace("\\\"", "\"");

        return string.IsNullOrWhiteSpace(json) ? null : json;
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
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应文本。</returns>
    private async Task<string> FetchReflowTextAsync(string roomId, CancellationToken cancellationToken)
    {
        HttpRequestSpec spec = new()
        {
            Url = string.Concat(ReflowEndpoint, ReflowQuery, roomId),
            Platform = Platform,
            Operation = ReflowOperation,
        };

        return await _http.GetStringAsync(spec, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 走回退路径：解析 reflow 接口响应并组装结果。
    /// </summary>
    /// <param name="roomId">房间号。</param>
    /// <param name="builder">候选构造器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析结果；reflow 响应不可用时返回 <see langword="null"/>。</returns>
    private async Task<ResolvedRoom?> TryParseReflowAsync(
        string roomId,
        StreamCandidateBuilder builder,
        CancellationToken cancellationToken)
    {
        string text = await FetchReflowTextAsync(roomId, cancellationToken).ConfigureAwait(false);
        using JsonDocument? document = TryParseDocument(text, ReflowOperation);
        if (document is null)
        {
            return null;
        }

        JsonElement data = GetNestedProperty(document.RootElement, DataField, DataField);
        if (data.ValueKind != JsonValueKind.Object)
        {
            Logger.Debug(ModuleName, "抖音 reflow 接口未返回 data.data。", new Dictionary<string, object?>
            {
                ["operation"] = ReflowOperation,
                ["roomId"] = roomId,
            });
            return null;
        }

        JsonElement user = GetPropertyOrUndefined(data, UserField);
        if (user.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw Fail(ResolveFailure.RoomNotFound, ReflowOperation, ResolveMessages.RoomNotFound);
        }

        string? anchorName = ReadString(user, NicknameField);
        if (string.IsNullOrWhiteSpace(anchorName))
        {
            throw Fail(ResolveFailure.ParseError, ReflowOperation, MissingAnchorMessage);
        }

        JsonElement room = GetPropertyOrUndefined(data, RoomField);
        if (room.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw Fail(ResolveFailure.NotLive, ReflowOperation, ResolveMessages.NotLive);
        }

        _ = CollectCandidates(builder, room, ReflowOperation, out string title, out string category);
        return CreateRoom(roomId, anchorName, title, category, builder);
    }

    /// <summary>读取抖音 room 对象的状态、标题与分区，并按画质优先级加入候选。</summary>
    /// <param name="builder">候选构造器。</param>
    /// <param name="room">抖音返回的 room 对象（页面与 reflow 结构一致）。</param>
    /// <param name="operation">操作名，用于失败分类与日志。</param>
    /// <param name="title">直播间标题，缺失时为空字符串。</param>
    /// <param name="category">直播分区，缺失时为空字符串。</param>
    /// <returns>至少加入一个候选返回 <see langword="true"/>。</returns>
    private bool CollectCandidates(
        StreamCandidateBuilder builder,
        JsonElement room,
        string operation,
        out string title,
        out string category)
    {
        title = ReadString(room, TitleField) ?? string.Empty;
        category = ResolveCategory(room);

        if (ReadInt32(room, StatusField) == EndedRoomStatus)
        {
            throw Fail(ResolveFailure.NotLive, operation, ResolveMessages.NotLive);
        }

        JsonElement streamUrl = GetPropertyOrUndefined(room, StreamUrlField);
        if (streamUrl.ValueKind != JsonValueKind.Object)
        {
            throw Fail(ResolveFailure.NotLive, operation, ResolveMessages.NotLive);
        }

        bool addedFlv = TryAddPreferredQuality(builder, GetPropertyOrUndefined(streamUrl, FlvPullUrlField), StreamFormat.FlvHttp);
        bool addedHls = TryAddPreferredQuality(builder, GetPropertyOrUndefined(streamUrl, HlsPullUrlMapField), StreamFormat.HlsTs);
        return addedFlv || addedHls;
    }

    /// <summary>按画质优先级从"画质键名 → 地址"映射中挑选唯一的最佳地址并加入候选。</summary>
    /// <param name="builder">候选构造器。</param>
    /// <param name="urlMap">画质键名到地址的映射。</param>
    /// <param name="format">候选的容器格式。</param>
    /// <returns>成功加入返回 <see langword="true"/>；映射缺失或全部为空字符串时返回 <see langword="false"/>。</returns>
    private static bool TryAddPreferredQuality(StreamCandidateBuilder builder, JsonElement urlMap, StreamFormat format)
    {
        if (urlMap.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (string qualityName in QualityPreferenceOrder)
        {
            if (!urlMap.TryGetProperty(qualityName, out JsonElement urlElement)
                || urlElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? url = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            bool added = builder.TryAdd(
                url,
                format,
                VideoCodec.Avc,
                QualityNames.FromDouyinQualityName(qualityName),
                expiresAt: null,
                referer: RoomUrlReferer,
                cdnHost: TryGetHost(url));

            if (added)
            {
                return true;
            }
        }

        return false;
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
    /// <returns>解析结果。</returns>
    private ResolvedRoom CreateRoom(
        string roomId,
        string anchor,
        string title,
        string category,
        StreamCandidateBuilder builder) => new()
        {
            Platform = Platform,
            RoomId = roomId,
            Anchor = anchor,
            Title = title,
            Category = category,
            Candidates = builder.Build(),
            ResolvedAt = DateTimeOffset.UtcNow,
        };

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

    /// <summary>从地址中推导 CDN 主机名。</summary>
    /// <param name="url">流地址。</param>
    /// <returns>主机名；地址非法时返回 <see langword="null"/>（由候选构造器兜底）。</returns>
    private static string? TryGetHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host) ? uri.Host : null;
}
