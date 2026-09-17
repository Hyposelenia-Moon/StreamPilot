namespace StreamPilot.Parsers.Bigo;

using System.Text.Json;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Parsers;

/// <summary>
/// Bigo Live 解析器。
/// </summary>
/// <remarks>
/// 调用官方站点内部工作室接口换取 <c>hls_src</c>：Bigo 只暴露一条 HLS 地址，画质固定为未知。
/// 参考实现解析了 <c>roomStatus</c> 却未使用；此处按"<c>0</c> 或（状态缺失且无地址）视为未开播"判定
/// （见 docs/adr/0003-解析器实现.md 第 5 节）。
/// </remarks>
internal sealed class BigoParser : PlatformParserBase
{
    /// <summary>工作室信息接口地址。</summary>
    private const string StudioInfoEndpoint = "https://ta.bigo.tv/official_website/studio/getInternalStudioInfo";

    /// <summary>表单字段名（房间号）。</summary>
    private const string SiteIdField = "siteId";

    /// <summary>标识请求与候选地址时使用的 Referer。</summary>
    private const string RoomUrlReferer = "https://www.bigo.tv/";

    /// <summary>日志模块名。</summary>
    private const string ModuleName = "Parsers.Bigo";

    /// <summary>工作室信息接口的操作名。</summary>
    private const string StudioInfoOperation = "studio-info";

    /// <summary>输入校验的操作名。</summary>
    private const string ValidateOperation = "validate";

    /// <summary>Bigo 的"未开播"房间状态值。</summary>
    private const int RoomStatusOffline = 0;

    /// <summary>data 字段名。</summary>
    private const string DataField = "data";

    /// <summary>hls_src 字段名。</summary>
    private const string HlsSrcField = "hls_src";

    /// <summary>roomStatus 字段名。</summary>
    private const string RoomStatusField = "roomStatus";

    /// <summary>roomTopic 字段名。</summary>
    private const string RoomTopicField = "roomTopic";

    /// <summary>nickName 字段名。</summary>
    private const string NickNameField = "nickName";

    /// <summary>无 HLS 地址时的提示。</summary>
    private const string NoStreamMessage = "Bigo 未返回 HLS 地址（可能未开播，或该地区被 Bigo 限制）。";

    /// <summary>响应不是合法 JSON 时的提示。</summary>
    private const string InvalidJsonMessage = "Bigo 工作室接口返回的响应不是合法 JSON。";

    /// <summary>无法从输入确定房间号时的提示。</summary>
    private const string MissingRoomIdMessage = "无法从输入中确定 Bigo 直播间房间号。";

    private readonly HttpTextClient _http;

    /// <summary>初始化解析器。</summary>
    /// <param name="http">带超时与有界重试的 HTTP 客户端。</param>
    /// <param name="logger">结构化日志。</param>
    public BigoParser(HttpTextClient http, IStructuredLogger logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <inheritdoc />
    public override PlatformId Platform => PlatformId.Bigo;

    /// <inheritdoc />
    public override string DisplayName => "Bigo Live";

    /// <inheritdoc />
    public override string RoomUrlPrefix => "https://www.bigo.tv/";

    /// <inheritdoc />
    protected override async Task<ResolvedRoom> OnParseAsync(RoomQuery query, CancellationToken cancellationToken)
    {
        string roomId = ResolveRoomId(query);
        using JsonDocument document = await FetchStudioInfoAsync(roomId, cancellationToken).ConfigureAwait(false);

        JsonElement data = GetPropertyOrUndefined(document.RootElement, DataField);
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw Fail(ResolveFailure.RoomNotFound, StudioInfoOperation, ResolveMessages.RoomNotFound);
        }

        StreamCandidateBuilder builder = new(Platform, Logger);
        CollectCandidates(builder, data, out string title, out string anchor);

        return new ResolvedRoom
        {
            Platform = Platform,
            RoomId = roomId,
            Anchor = anchor,
            Title = title,
            Category = string.Empty,
            Candidates = builder.Build(),
            ResolvedAt = DateTimeOffset.UtcNow,
        };
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

    /// <summary>以 <c>siteId</c> 表单调用工作室信息接口并解析 JSON。</summary>
    /// <param name="roomId">房间号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应 JSON 文档（调用方负责释放）。</returns>
    /// <remarks>响应不是合法 JSON 时抛出带 <c>ParseError</c> 分类的解析异常。</remarks>
    private async Task<JsonDocument> FetchStudioInfoAsync(string roomId, CancellationToken cancellationToken)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            [SiteIdField] = roomId,
        };

        HttpRequestSpec spec = new()
        {
            Url = StudioInfoEndpoint,
            Platform = Platform,
            Operation = StudioInfoOperation,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Referer"] = RoomUrlReferer,
            },
            ContentFactory = () => new FormUrlEncodedContent(form),
        };

        string text = await _http.GetStringAsync(spec, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            Logger.LogError(
                LogLevel.Error,
                ModuleName,
                "Bigo 工作室接口返回非法 JSON。",
                exception,
                new Dictionary<string, object?>
                {
                    ["operation"] = StudioInfoOperation,
                });

            throw Fail(ResolveFailure.ParseError, StudioInfoOperation, InvalidJsonMessage, exception);
        }
    }

    /// <summary>读取工作室信息并加入候选；未开播或没有 HLS 地址时按未开播处理。</summary>
    /// <param name="builder">候选构造器。</param>
    /// <param name="studio">响应中的 data 对象。</param>
    /// <param name="title">直播间标题，缺失时为空字符串。</param>
    /// <param name="anchor">主播名，缺失时为空字符串。</param>
    /// <remarks><c>roomStatus</c> 为 <c>0</c>，或状态缺失且 <c>hls_src</c> 为空时，抛出带 <c>NotLive</c> 分类的解析异常。</remarks>
    private void CollectCandidates(
        StreamCandidateBuilder builder,
        JsonElement studio,
        out string title,
        out string anchor)
    {
        title = ReadString(studio, RoomTopicField) ?? string.Empty;
        anchor = ReadString(studio, NickNameField) ?? string.Empty;

        if (ReadInt32(studio, RoomStatusField) == RoomStatusOffline)
        {
            throw Fail(ResolveFailure.NotLive, StudioInfoOperation, ResolveMessages.NotLive);
        }

        string? hlsSrc = ReadString(studio, HlsSrcField);
        if (string.IsNullOrWhiteSpace(hlsSrc))
        {
            throw Fail(ResolveFailure.NotLive, StudioInfoOperation, NoStreamMessage);
        }

        _ = builder.TryAdd(
            hlsSrc,
            StreamFormat.HlsTs,
            VideoCodec.Avc,
            StreamQuality.Unknown,
            expiresAt: null,
            referer: RoomUrlReferer,
            cdnHost: TryGetHost(hlsSrc));
    }

    /// <summary>读取对象属性；元素不是对象或属性缺失时返回未定义元素（不抛异常）。</summary>
    /// <param name="element">父元素。</param>
    /// <param name="name">属性名。</param>
    /// <returns>属性值；未命中时返回 <see cref="JsonValueKind.Undefined"/>。</returns>
    private static JsonElement GetPropertyOrUndefined(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value
            : default;

    /// <summary>读取字符串属性（校验 <see cref="JsonValueKind.String"/>，不做不安全的类型断言）。</summary>
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
