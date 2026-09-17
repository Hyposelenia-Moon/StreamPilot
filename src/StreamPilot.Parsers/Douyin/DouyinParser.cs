namespace StreamPilot.Parsers.Douyin;

using System.Globalization;
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
/// 不实现 <c>a_bogus</c>/<c>ms_token</c> 等签名与风控绕过（见 docs/adr/0003-parser-contract.md 第 5 节）。
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

    /// <summary>档位键：原画（最高档）。</summary>
    private const string QualityKeyOrigin = "origin";

    /// <summary>档位键：蓝光。</summary>
    private const string QualityKeyUhd = "uhd";

    /// <summary>档位键：超清。</summary>
    private const string QualityKeyHd = "hd";

    /// <summary>档位键：高清。</summary>
    private const string QualityKeySd = "sd";

    /// <summary>档位键：标清。</summary>
    private const string QualityKeyLd = "ld";

    /// <summary>档位键：纯音频流（平台声明的档位之一）。</summary>
    private const string QualityKeyAudioOnly = "ao";

    /// <summary>档位键：真原画（平台声明的档位之一）。</summary>
    private const string QualityKeyRealOrigin = "real_origin";

    /// <summary>码率单位判定阈值：不小于该值视为 bps（需换算为 kbps）。</summary>
    private const int BitrateBpsThreshold = 1000;

    /// <summary>1 kbps 对应的比特数。</summary>
    private const int BitsPerKilobit = 1000;

    /// <summary>已知拉流档位键（从高到低）：新档位键在前，老式分辨率键在后。</summary>
    /// <remarks>
    /// 两套键名在不同接口与不同年代共存：<c>origin/uhd/hd/sd/ld/ao/real_origin</c> 来自官方
    /// <c>options.qualities[].sdk_key</c>，<c>FULL_HD1/HD1/SD1/SD2</c> 来自老式的
    /// <c>flv_pull_url</c> 与 <c>hls_pull_url_map</c> 映射键；不在表内的键排在最后并保持响应中的顺序。
    /// </remarks>
    private static readonly string[] KnownQualityKeyOrder =
    [
        QualityKeyOrigin,
        QualityKeyUhd,
        QualityKeyHd,
        QualityKeySd,
        QualityKeyLd,
        QualityKeyAudioOnly,
        QualityKeyRealOrigin,
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
        // 用户自备 Cookie 只作用于本次解析的请求（播放地址与中继都不带它）。
        using IDisposable cookieScope = _http.UseCookie(query.Cookie);
        string roomId = ResolveRoomId(query);
        StreamCandidateBuilder builder = new(Platform, Logger);

        ResolvedRoom? room = await TryParseRoomPageAsync(roomId, query.PreferredQualityKey, builder, cancellationToken)
            .ConfigureAwait(false);
        room ??= await TryParseReflowAsync(roomId, query.PreferredQualityKey, builder, cancellationToken)
            .ConfigureAwait(false);

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
    /// <param name="preferredQualityKey">调用方指定的档位键；为空时取最高档。</param>
    /// <param name="builder">候选构造器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析结果；页面结构不可用或没有可用地址时返回 <see langword="null"/> 以便回退 reflow。</returns>
    private async Task<ResolvedRoom?> TryParseRoomPageAsync(
        string roomId,
        string? preferredQualityKey,
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
    /// <param name="preferredQualityKey">调用方指定的档位键；为空时取最高档。</param>
    /// <param name="builder">候选构造器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析结果；reflow 响应不可用时返回 <see langword="null"/>。</returns>
    private async Task<ResolvedRoom?> TryParseReflowAsync(
        string roomId,
        string? preferredQualityKey,
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

        (IReadOnlyList<QualityOption> qualities, string? selectedQualityKey) =
            BuildQualityOptions(room, preferredQualityKey);
        _ = CollectCandidates(builder, room, ReflowOperation, selectedQualityKey, out string title, out string category);
        return CreateRoom(roomId, anchorName, title, category, builder, qualities, selectedQualityKey);
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

        if (ReadInt32(room, StatusField) == EndedRoomStatus)
        {
            throw Fail(ResolveFailure.NotLive, operation, ResolveMessages.NotLive);
        }

        JsonElement streamUrl = GetPropertyOrUndefined(room, StreamUrlField);
        if (streamUrl.ValueKind != JsonValueKind.Object)
        {
            throw Fail(ResolveFailure.NotLive, operation, ResolveMessages.NotLive);
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
        QualityKeyUhd => "蓝光",
        QualityKeyHd => "超清",
        QualityKeySd => "高清",
        QualityKeyLd => "标清",
        QualityKeyAudioOnly => "音频流",
        QualityKeyRealOrigin => "真原画",
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
        int? raw = ReadInt32(element, name);
        if (raw is null)
        {
            string? text = ReadString(element, name);
            raw = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : null;
        }

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

    /// <summary>从地址中推导 CDN 主机名。</summary>
    /// <param name="url">流地址。</param>
    /// <returns>主机名；地址非法时返回 <see langword="null"/>（由候选构造器兜底）。</returns>
    private static string? TryGetHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host) ? uri.Host : null;
}
