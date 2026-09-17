namespace StreamPilot.Parsers.Yy;

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
/// YY 直播解析器。
/// </summary>
/// <remarks>
/// 先抓取 <c>www.yy.com/{房间号}</c> 页面取主播名、标题与分区，再向 stream-manager 播放接口
/// 换取全部 CDN 线路地址。YY 不返回明确的直播状态，因此按"有地址即开播、无地址即未开播"判定
/// （见 docs/adr/0003-parser-contract.md 第 5 节）。
/// </remarks>
internal sealed class YyParser : PlatformParserBase
{
    /// <summary>房间页地址前缀（不含斜杠，便于拼接房间号）。</summary>
    private const string RoomPageBase = "https://www.yy.com";

    /// <summary>stream-manager 播放接口地址。</summary>
    private const string StreamManagerEndpoint = "https://stream-manager.yy.com/v3/channel/streams";

    /// <summary>Web 端固定 uid。</summary>
    private const string StreamManagerUid = "3071000363";

    /// <summary>Web 端固定 appid。</summary>
    private const string StreamManagerAppId = "0";

    /// <summary>Web 端客户端版本号（client_ver / playersdk_ver / streamsdk_ver 共用）。</summary>
    private const string ClientVersion = "5.19.4";

    /// <summary>标识候选地址与抓取页面时使用的 Referer。</summary>
    private const string RoomUrlReferer = "https://www.yy.com/";

    /// <summary>日志模块名。</summary>
    private const string ModuleName = "Parsers.Yy";

    /// <summary>房间页抽取的操作名。</summary>
    private const string RoomPageOperation = "room-page";

    /// <summary>播放接口的操作名。</summary>
    private const string StreamManagerOperation = "stream-manager";

    /// <summary>输入校验的操作名。</summary>
    private const string ValidateOperation = "validate";

    /// <summary>主播名捕获组序号。</summary>
    private const int AnchorGroupIndex = 1;

    /// <summary>标题捕获组序号。</summary>
    private const int TitleGroupIndex = 2;

    /// <summary>分区捕获组序号。</summary>
    private const int CategoryGroupIndex = 3;

    /// <summary>毫秒与秒的换算。</summary>
    private const long MillisecondsPerSecond = 1000;

    /// <summary>请求体中的序号占位符。</summary>
    private const string SequencePlaceholder = "@SEQ@";

    /// <summary>请求体中的发送时间占位符。</summary>
    private const string SendTimePlaceholder = "@SENDTIME@";

    /// <summary>请求体中的房间号占位符。</summary>
    private const string RoomIdPlaceholder = "@ROOMID@";

    /// <summary>请求体中的客户端版本占位符。</summary>
    private const string ClientVersionPlaceholder = "@CLIENTVER@";

    /// <summary>HLS 播放列表后缀（据此把线路判定为 HLS）。</summary>
    private const string M3u8Suffix = ".m3u8";

    /// <summary>无可用播放地址时的提示。</summary>
    private const string NoStreamMessage = "YY 未返回可用播放地址（可能未开播）。";

    /// <summary>响应不是合法 JSON 时的提示。</summary>
    private const string InvalidJsonMessage = "YY 播放接口返回的响应不是合法 JSON。";

    /// <summary>无法从输入确定房间号时的提示。</summary>
    private const string MissingRoomIdMessage = "无法从输入中确定 YY 直播间房间号。";

    /// <summary>房间页关键字段的抽取正则（页面内联脚本片段）。</summary>
    private static readonly Regex RoomPagePattern = new(
        @"nick: ""(.+?)"".+?roomName: decodeURIComponent\(""(.+?)""\).+?stringBiz: ""(.*?)""",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>不写入 BOM 的 UTF-8 编码器（供请求体使用）。</summary>
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>播放接口请求体模板；键名、嵌套与取值必须与 Web 端一致（无签名计算）。</summary>
    private const string StreamRequestBodyTemplate = """
        {"head":{"seq":@SEQ@,"appidstr":"0","bidstr":"120","cidstr":"@ROOMID@","sidstr":"@ROOMID@","uid64":0,"client_type":108,"client_ver":"@CLIENTVER@","stream_sys_ver":1,"app":"yylive_web","playersdk_ver":"@CLIENTVER@","thundersdk_ver":"0","streamsdk_ver":"@CLIENTVER@"},
        "client_attribute":{"client":"web","model":"web0","cpu":"","graphics_card":"","os":"chrome","osversion":"141.0.0.0","vsdk_version":"","app_identify":"","app_version":"","business":"","width":"1536","height":"960","scale":"","client_type":8,"h265":0},
        "avp_parameter":{"version":1,"client_type":8,"service_type":0,"imsi":0,"send_time":@SENDTIME@,"line_seq":-1,"gear":2,"ssl":1,"stream_format":0}}
        """;

    /// <summary>avp_info_res 字段名。</summary>
    private const string AvpInfoResField = "avp_info_res";

    /// <summary>stream_line_addr 字段名。</summary>
    private const string StreamLineAddrField = "stream_line_addr";

    /// <summary>cdn_info 字段名。</summary>
    private const string CdnInfoField = "cdn_info";

    /// <summary>url 字段名。</summary>
    private const string UrlField = "url";

    private readonly HttpTextClient _http;

    /// <summary>初始化解析器。</summary>
    /// <param name="http">带超时与有界重试的 HTTP 客户端。</param>
    /// <param name="logger">结构化日志。</param>
    public YyParser(HttpTextClient http, IStructuredLogger logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <inheritdoc />
    public override PlatformId Platform => PlatformId.Yy;

    /// <inheritdoc />
    public override string DisplayName => "YY";

    /// <inheritdoc />
    public override string RoomUrlPrefix => "https://www.yy.com/";

    /// <inheritdoc />
    protected override async Task<ResolvedRoom> OnParseAsync(RoomQuery query, CancellationToken cancellationToken)
    {
        string roomId = ResolveRoomId(query);
        (string Anchor, string Title, string Category) page =
            await FetchRoomPageInfoAsync(roomId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> addresses =
            await FetchStreamAddressesAsync(roomId, cancellationToken).ConfigureAwait(false);

        StreamCandidateBuilder builder = new(Platform, Logger);
        CollectCandidates(builder, addresses);

        return new ResolvedRoom
        {
            Platform = Platform,
            RoomId = roomId,
            Anchor = page.Anchor,
            Title = page.Title,
            Category = page.Category,
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

    /// <summary>抓取 YY 房间页并抽取主播名、标题与分区。</summary>
    /// <param name="roomId">房间号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>主播名、标题与分区（分区可能为空字符串）。</returns>
    /// <remarks>页面缺少关键字段时抛出带 <c>RoomNotFound</c> 分类的解析异常。</remarks>
    private async Task<(string Anchor, string Title, string Category)> FetchRoomPageInfoAsync(
        string roomId,
        CancellationToken cancellationToken)
    {
        HttpRequestSpec spec = new()
        {
            Url = string.Concat(RoomPageBase, "/", roomId),
            Platform = Platform,
            Operation = RoomPageOperation,
        };

        string html = await _http.GetStringAsync(spec, cancellationToken).ConfigureAwait(false);
        return ParseRoomPage(html);
    }

    /// <summary>从房间页 HTML 中抽取主播名、标题与分区。</summary>
    /// <param name="html">页面 HTML。</param>
    /// <returns>主播名、标题（按 JS <c>decodeURIComponent</c> 语义解码）与分区。</returns>
    private (string Anchor, string Title, string Category) ParseRoomPage(string html)
    {
        Match match = RoomPagePattern.Match(html);
        if (!match.Success)
        {
            throw Fail(ResolveFailure.RoomNotFound, RoomPageOperation, ResolveMessages.RoomNotFound);
        }

        string anchor = match.Groups[AnchorGroupIndex].Value;
        string title = QueryStringParser.DecodeComponentStrict(match.Groups[TitleGroupIndex].Value);
        string category = match.Groups[CategoryGroupIndex].Value;
        return (anchor, title, category);
    }

    /// <summary>调用 stream-manager 接口并取出全部线路地址。</summary>
    /// <param name="roomId">房间号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>线路地址列表（顺序与接口返回一致，可能为空）。</returns>
    private async Task<IReadOnlyList<string>> FetchStreamAddressesAsync(
        string roomId,
        CancellationToken cancellationToken)
    {
        long sequenceMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string body = BuildStreamRequestBody(roomId, sequenceMs);

        HttpRequestSpec spec = new()
        {
            Url = BuildStreamManagerUrl(roomId, sequenceMs),
            Platform = Platform,
            Operation = StreamManagerOperation,
            ContentFactory = () => new StringContent(body, Utf8WithoutBom, "text/plain;charset=UTF-8"),
        };

        string text = await _http.GetStringAsync(spec, cancellationToken).ConfigureAwait(false);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            Logger.LogError(
                LogLevel.Error,
                ModuleName,
                "YY 播放接口返回非法 JSON。",
                exception,
                new Dictionary<string, object?>
                {
                    ["operation"] = StreamManagerOperation,
                });

            throw Fail(ResolveFailure.ParseError, StreamManagerOperation, InvalidJsonMessage, exception);
        }

        using (document)
        {
            return ExtractStreamAddresses(document.RootElement);
        }
    }

    /// <summary>拼接 stream-manager 播放接口地址。</summary>
    /// <param name="roomId">房间号。</param>
    /// <param name="sequenceMs">请求序号（毫秒时间戳）。</param>
    /// <returns>完整请求地址。</returns>
    private static string BuildStreamManagerUrl(string roomId, long sequenceMs) => string.Concat(
        StreamManagerEndpoint,
        "?uid=",
        StreamManagerUid,
        "&cid=",
        roomId,
        "&sid=",
        roomId,
        "&appid=",
        StreamManagerAppId,
        "&sequence=",
        sequenceMs.ToString(CultureInfo.InvariantCulture),
        "&encode=json");

    /// <summary>构造 stream-manager 播放接口的 JSON 请求体。</summary>
    /// <param name="roomId">房间号。</param>
    /// <param name="sequenceMs">请求序号（毫秒时间戳）。</param>
    /// <returns>JSON 请求体文本。</returns>
    /// <remarks>房间号已由基类限定为字母数字，直接内嵌不会破坏 JSON 结构。</remarks>
    private static string BuildStreamRequestBody(string roomId, long sequenceMs)
    {
        long sendTimeSeconds = sequenceMs / MillisecondsPerSecond;
        return StreamRequestBodyTemplate
            .Replace(SequencePlaceholder, sequenceMs.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace(SendTimePlaceholder, sendTimeSeconds.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace(RoomIdPlaceholder, roomId, StringComparison.Ordinal)
            .Replace(ClientVersionPlaceholder, ClientVersion, StringComparison.Ordinal);
    }

    /// <summary>从播放接口响应中取出全部非空线路地址。</summary>
    /// <param name="root">响应根元素。</param>
    /// <returns>线路地址列表；结构缺失时返回空列表。</returns>
    private static IReadOnlyList<string> ExtractStreamAddresses(JsonElement root)
    {
        List<string> addresses = [];
        JsonElement lines = GetNestedProperty(root, AvpInfoResField, StreamLineAddrField);
        if (lines.ValueKind != JsonValueKind.Object)
        {
            return addresses;
        }

        foreach (JsonProperty line in lines.EnumerateObject())
        {
            string? url = ReadString(GetPropertyOrUndefined(line.Value, CdnInfoField), UrlField);
            if (!string.IsNullOrWhiteSpace(url))
            {
                addresses.Add(url);
            }
        }

        return addresses;
    }

    /// <summary>把线路地址按顺序加入候选；一条可用地址都没有时按未开播处理。</summary>
    /// <param name="builder">候选构造器。</param>
    /// <param name="addresses">线路地址列表。</param>
    /// <remarks>无可用地址时抛出带 <c>NotLive</c> 分类的解析异常。</remarks>
    private void CollectCandidates(StreamCandidateBuilder builder, IReadOnlyList<string> addresses)
    {
        foreach (string address in addresses)
        {
            _ = builder.TryAdd(
                address,
                ResolveFormat(address),
                VideoCodec.Avc,
                StreamQuality.Unknown,
                expiresAt: null,
                referer: RoomUrlReferer,
                cdnHost: TryGetHost(address));
        }

        if (builder.Count == 0)
        {
            throw Fail(ResolveFailure.NotLive, StreamManagerOperation, NoStreamMessage);
        }
    }

    /// <summary>判定线路格式：以 <c>.m3u8</c> 结尾的线路按 HLS 处理，其余按 HTTP-FLV 处理。</summary>
    /// <param name="url">线路地址。</param>
    /// <returns>候选的容器格式。</returns>
    private static StreamFormat ResolveFormat(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.AbsolutePath.EndsWith(M3u8Suffix, StringComparison.OrdinalIgnoreCase)
            ? StreamFormat.HlsTs
            : StreamFormat.FlvHttp;

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

    /// <summary>读取字符串属性（校验 <see cref="JsonValueKind.String"/>，不做不安全的类型断言）。</summary>
    /// <param name="element">父元素。</param>
    /// <param name="name">属性名。</param>
    /// <returns>字符串值；缺失或类型不符时返回 <see langword="null"/>。</returns>
    private static string? ReadString(JsonElement element, string name)
    {
        JsonElement value = GetPropertyOrUndefined(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>从地址中推导 CDN 主机名。</summary>
    /// <param name="url">线路地址。</param>
    /// <returns>主机名；地址非法时返回 <see langword="null"/>（由候选构造器兜底）。</returns>
    private static string? TryGetHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host) ? uri.Host : null;
}
