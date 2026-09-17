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

    /// <summary>房间页内联脚本里 pageInfo 对象的捕获组名。</summary>
    private const string PageInfoGroup = "body";

    /// <summary>房间页字段值的捕获组名。</summary>
    private const string FieldValueGroup = "value";

    /// <summary>毫秒与秒的换算。</summary>
    private const long MillisecondsPerSecond = 1000;

    /// <summary>默认的 <c>gear</c> 取值（与 Web 端播放器一致的请求口径）。</summary>
    private const int DefaultGear = 2;

    /// <summary><c>gear</c> 允许的下限（防御性校验，语义未经证实）。</summary>
    private const int MinGear = 0;

    /// <summary><c>gear</c> 允许的上限（防御性校验，语义未经证实）。</summary>
    private const int MaxGear = 100;

    /// <summary>唯一档位的显示名。</summary>
    private const string DefaultQualityLabel = "默认（平台给定）";

    /// <summary>请求体中的序号占位符。</summary>
    private const string SequencePlaceholder = "@SEQ@";

    /// <summary>请求体中的 <c>gear</c> 占位符。</summary>
    private const string GearPlaceholder = "@GEAR@";

    /// <summary>请求体中的发送时间占位符。</summary>
    private const string SendTimePlaceholder = "@SENDTIME@";

    /// <summary>请求体中的流标识占位符。</summary>
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

    /// <summary>房间页缺少 pageInfo（例如落到 404 页）时的提示。</summary>
    private const string RoomPageMissingMessage = "YY 房间页缺少直播信息，房间号可能不存在。";

    /// <summary>房间页内联脚本里的 pageInfo 对象。</summary>
    /// <remarks>
    /// YY 页面结构变动过多次，这里只锁定 <c>var pageInfo = { ... };</c> 这一稳定外壳，
    /// 字段再逐个抽取，避免"一个字段挪位就整页解析失败"。
    /// </remarks>
    private static readonly Regex PageInfoPattern = new(
        @"var\s+pageInfo\s*=\s*\{(?<body>.+?)\r?\n\s*\};",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>主播名（nick 字段）。</summary>
    private static readonly Regex AnchorPattern = new(
        @"(?<![A-Za-z])nick\s*:\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>房间标题（roomName 字段，按 decodeURIComponent 语义解码）。</summary>
    private static readonly Regex TitlePattern = new(
        @"roomName\s*:\s*decodeURIComponent\(""(?<value>[^""]*)""\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>分区业务标识（biz 字段，可能为空）。</summary>
    private static readonly Regex CategoryPattern = new(
        @"(?<![A-Za-z])biz\s*:\s*'(?<value>[^']*)'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>真实流标识（sid 字段）；短号房间必须用它调用播放接口。</summary>
    private static readonly Regex StreamIdPattern = new(
        @"(?<![A-Za-z])sid\s*:\s*""(?<value>\d+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>不写入 BOM 的 UTF-8 编码器（供请求体使用）。</summary>
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 播放接口请求体的媒体类型（不含参数）。
    /// </summary>
    /// <remarks>
    /// <c>StringContent</c> 的 mediaType 参数只接受纯媒体类型，带 <c>;charset=</c> 会被
    /// <c>MediaTypeHeaderValue</c> 判为非法格式并抛 <c>FormatException</c>；
    /// 字符集由传入的编码自动补上。
    /// </remarks>
    private const string PlainTextContentType = "text/plain";

    /// <summary>播放接口请求体模板；键名、嵌套与取值必须与 Web 端一致（无签名计算）。</summary>
    private const string StreamRequestBodyTemplate = """
        {"head":{"seq":@SEQ@,"appidstr":"0","bidstr":"120","cidstr":"@ROOMID@","sidstr":"@ROOMID@","uid64":0,"client_type":108,"client_ver":"@CLIENTVER@","stream_sys_ver":1,"app":"yylive_web","playersdk_ver":"@CLIENTVER@","thundersdk_ver":"0","streamsdk_ver":"@CLIENTVER@"},
        "client_attribute":{"client":"web","model":"web0","cpu":"","graphics_card":"","os":"chrome","osversion":"141.0.0.0","vsdk_version":"","app_identify":"","app_version":"","business":"","width":"1536","height":"960","scale":"","client_type":8,"h265":0},
        "avp_parameter":{"version":1,"client_type":8,"service_type":0,"imsi":0,"send_time":@SENDTIME@,"line_seq":-1,"gear":@GEAR@,"ssl":1,"stream_format":0}}
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
    /// <remarks>
    /// 档位由请求体里的 <c>gear</c> 决定：YY 的响应里没有档位表，<c>gear</c> 的取值语义也未经证实，
    /// 因此这里只把调用方给的数值原样透传（缺失或非法时回退到与 Web 端一致的默认值），
    /// 并且不对外声明任何一个 <c>gear</c> 等于某个画质档位。
    /// </remarks>
    protected override async Task<ResolvedRoom> OnParseAsync(RoomQuery query, CancellationToken cancellationToken)
    {
        // 用户自备 Cookie 只作用于本次解析的请求（播放地址与中继都不带它）。
        using IDisposable cookieScope = _http.UseCookie(query.Cookie);
        string roomId = ResolveRoomId(query);
        int gear = ResolveGear(query.PreferredQualityKey);
        YyRoomPage page = await FetchRoomPageAsync(roomId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> addresses =
            await FetchStreamAddressesAsync(page.StreamId, gear, cancellationToken).ConfigureAwait(false);

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
            Qualities = BuildQualityOptions(gear),
            SelectedQualityKey = gear.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// 把调用方指定的档位键解析为请求体的 <c>gear</c> 取值。
    /// </summary>
    /// <param name="preferredQualityKey">档位键（纯数字的 <c>gear</c> 文本）；为空时取默认值。</param>
    /// <returns>请求使用的 <c>gear</c> 数值。</returns>
    /// <remarks>
    /// YY 的 <c>gear</c> 取值语义未经证实，这里只做防御性校验：不是纯数字或超出
    /// <see cref="MinGear"/> 到 <see cref="MaxGear"/> 的范围时回退到 <see cref="DefaultGear"/> 并记 Warn，
    /// 不会因为档位键异常而让解析失败。
    /// </remarks>
    internal int ResolveGear(string? preferredQualityKey)
    {
        if (string.IsNullOrWhiteSpace(preferredQualityKey)
            || string.Equals(preferredQualityKey, QualityOption.BestFlag, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultGear;
        }

        if (int.TryParse(preferredQualityKey, NumberStyles.None, CultureInfo.InvariantCulture, out int gear)
            && gear >= MinGear
            && gear <= MaxGear)
        {
            return gear;
        }

        Logger.Warn(ModuleName, "YY 档位键无效，回退默认 gear。", new Dictionary<string, object?>
        {
            ["preferredQualityKey"] = preferredQualityKey,
            ["gear"] = DefaultGear,
        });

        return DefaultGear;
    }

    /// <summary>
    /// 构造 YY 的画质档位列表。
    /// </summary>
    /// <param name="gear">本次请求使用的 <c>gear</c> 数值。</param>
    /// <returns>只含一项的档位列表（档位键即当前 <c>gear</c>）。</returns>
    /// <remarks>
    /// 响应里没有档位表，<c>gear</c> 与画质的对应关系未经证实，因此只暴露当前取值本身的"默认档"，
    /// 不推测、也不标注它等于某个画质。
    /// </remarks>
    internal static IReadOnlyList<QualityOption> BuildQualityOptions(int gear)
    {
        IReadOnlyList<QualityOption> qualities =
        [
            new QualityOption
            {
                Key = gear.ToString(CultureInfo.InvariantCulture),
                Label = DefaultQualityLabel,
                IsBest = true,
            },
        ];

        return qualities;
    }

    /// <summary>YY 房间页中抽取到的直播信息。</summary>
    /// <param name="Anchor">主播名。</param>
    /// <param name="Title">房间标题。</param>
    /// <param name="Category">分区业务标识（可能为空）。</param>
    /// <param name="StreamId">真实流标识（sid）；播放接口必须使用它而不是短号。</param>
    internal readonly record struct YyRoomPage(string Anchor, string Title, string Category, string StreamId);

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

    /// <summary>抓取 YY 房间页并抽取主播名、标题、分区与真实流标识。</summary>
    /// <param name="roomId">房间号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>房间页信息。</returns>
    /// <remarks>页面缺少 pageInfo（房间不存在或已下线）时抛出带 <c>RoomNotFound</c> 分类的解析异常。</remarks>
    private async Task<YyRoomPage> FetchRoomPageAsync(string roomId, CancellationToken cancellationToken)
    {
        HttpRequestSpec spec = new()
        {
            Url = string.Concat(RoomPageBase, "/", roomId),
            Platform = Platform,
            Operation = RoomPageOperation,
        };

        string html = await _http.GetStringAsync(spec, cancellationToken).ConfigureAwait(false);
        return ParseRoomPage(html, roomId);
    }

    /// <summary>从房间页 HTML 中抽取主播名、标题、分区与真实流标识。</summary>
    /// <param name="html">页面 HTML。</param>
    /// <param name="roomId">输入的房间号（页面未给出 sid 时作为回退）。</param>
    /// <returns>房间页信息。</returns>
    internal YyRoomPage ParseRoomPage(string html, string roomId)
    {
        Match page = PageInfoPattern.Match(html);
        if (!page.Success)
        {
            throw Fail(ResolveFailure.RoomNotFound, RoomPageOperation, RoomPageMissingMessage);
        }

        string body = page.Groups[PageInfoGroup].Value;
        string anchor = ReadField(AnchorPattern, body);
        string title = QueryStringParser.DecodeComponentStrict(ReadField(TitlePattern, body));
        string category = ReadField(CategoryPattern, body);
        string streamId = ReadField(StreamIdPattern, body);

        return new YyRoomPage(
            anchor.Length == 0 ? ResolveMessages.UnknownAnchor : anchor,
            title.Length == 0 ? ResolveMessages.TitleUnavailable : title,
            category,
            streamId.Length == 0 ? roomId : streamId);
    }

    /// <summary>读取单个字段；字段缺失时返回空字符串。</summary>
    /// <param name="pattern">字段正则。</param>
    /// <param name="body">pageInfo 对象文本。</param>
    /// <returns>字段值。</returns>
    private static string ReadField(Regex pattern, string body)
    {
        Match match = pattern.Match(body);
        return match.Success ? match.Groups[FieldValueGroup].Value.Trim() : string.Empty;
    }

    /// <summary>调用 stream-manager 接口并取出全部线路地址。</summary>
    /// <param name="streamId">真实流标识（页面 pageInfo.sid）。</param>
    /// <param name="gear">请求体里的档位取值（<c>gear</c>，语义未证实，由调用方决定）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>线路地址列表（顺序与接口返回一致，可能为空）。</returns>
    private async Task<IReadOnlyList<string>> FetchStreamAddressesAsync(
        string streamId,
        int gear,
        CancellationToken cancellationToken)
    {
        long sequenceMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string body = BuildStreamRequestBody(streamId, sequenceMs, gear);

        HttpRequestSpec spec = new()
        {
            Url = BuildStreamManagerUrl(streamId, sequenceMs),
            Platform = Platform,
            Operation = StreamManagerOperation,
            ContentFactory = () => new StringContent(body, Utf8WithoutBom, PlainTextContentType),
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
    /// <param name="streamId">真实流标识（页面 pageInfo.sid）。</param>
    /// <param name="sequenceMs">请求序号（毫秒时间戳）。</param>
    /// <returns>完整请求地址。</returns>
    private static string BuildStreamManagerUrl(string streamId, long sequenceMs) => string.Concat(
        StreamManagerEndpoint,
        "?uid=",
        StreamManagerUid,
        "&cid=",
        streamId,
        "&sid=",
        streamId,
        "&appid=",
        StreamManagerAppId,
        "&sequence=",
        sequenceMs.ToString(CultureInfo.InvariantCulture),
        "&encode=json");

    /// <summary>构造 stream-manager 播放接口的 JSON 请求体。</summary>
    /// <param name="streamId">真实流标识（页面 pageInfo.sid）。</param>
    /// <param name="sequenceMs">请求序号（毫秒时间戳）。</param>
    /// <param name="gear">请求体里的档位取值（<c>gear</c>）。</param>
    /// <returns>JSON 请求体文本。</returns>
    /// <remarks>
    /// 流标识已由基类限定为字母数字，直接内嵌不会破坏 JSON 结构；
    /// <c>gear</c> 由调用方保证为整数，写入的是十进制文本。
    /// </remarks>
    private static string BuildStreamRequestBody(string streamId, long sequenceMs, int gear)
    {
        long sendTimeSeconds = sequenceMs / MillisecondsPerSecond;
        return StreamRequestBodyTemplate
            .Replace(SequencePlaceholder, sequenceMs.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace(SendTimePlaceholder, sendTimeSeconds.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace(RoomIdPlaceholder, streamId, StringComparison.Ordinal)
            .Replace(ClientVersionPlaceholder, ClientVersion, StringComparison.Ordinal)
            .Replace(GearPlaceholder, gear.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
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
