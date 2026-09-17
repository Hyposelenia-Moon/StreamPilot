namespace StreamPilot.Parsers.Douyu;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamPilot.Core.Errors;
using StreamPilot.Core.Http;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Parsers;

/// <summary>
/// 斗鱼直播解析器。
/// </summary>
/// <remarks>
/// 解析链路（见 docs/adr/0003-解析器实现.md 第 5 节"斗鱼"行）：
/// 房间页元数据 → <c>/betard/{roomId}</c> 轮播探测 → <c>getEncryption</c> 签名参数 → <c>getH5PlayV1</c> 播放信息 → 候选流组装。
/// 斗鱼主链路是 RTMP，Web 端不可播放；平台同时给出 HTTP-FLV/HLS 时优先产出 Web 可播候选，
/// 否则产出 <see cref="StreamFormat.Rtmp"/> 候选，由上层提示"该房间仅提供 RTMP，请使用 mpv 外挂播放"。
/// <c>auth</c>、<c>enc_data</c> 与带签名的流地址均属敏感信息，日志只写指纹。
/// </remarks>
internal sealed class DouyuParser : PlatformParserBase
{
    /// <summary>斗鱼 Web 端上报的固定设备号（接口约定的客户端标识，非用户凭证）。</summary>
    private const string DeviceId = "10000000000000000000000000003306";

    /// <summary>站点根地址：房间页地址前缀，同时是播放请求所需 Referer 的前缀。</summary>
    private const string DouyuBaseUrl = "https://www.douyu.com/";

    /// <summary>轮播探测接口前缀。</summary>
    private const string BetardUrlPrefix = DouyuBaseUrl + "betard/";

    /// <summary>加密参数接口地址（固定设备号作为 <c>did</c>）。</summary>
    private const string EncryptionUrl =
        DouyuBaseUrl + "wgapi/livenc/liveweb/websec/getEncryption?did=" + DeviceId;

    /// <summary>播放信息接口前缀。</summary>
    private const string PlayInfoUrlPrefix = DouyuBaseUrl + "lapi/live/getH5PlayV1/";

    /// <summary>Referer 请求头名。</summary>
    private const string RefererHeader = "Referer";

    /// <summary>房间页中表示"该房间目前没有开放"的标记。</summary>
    private const string RoomClosedMarker = "<span><p>该房间目前没有开放</p></span>";

    /// <summary>播放接口返回的未开播提示文本。</summary>
    private const string MessageNotLive = "房间未开播";

    /// <summary>播放接口返回的非法请求提示文本（触发风控）。</summary>
    private const string MessageIllegalRequest = "非法请求";

    /// <summary>无法从输入确定房间号时的提示。</summary>
    private const string MissingRoomIdMessage = "无法从输入中识别斗鱼房间号，请提供房间号或直播间链接。";

    /// <summary>房间页未给出房间号时的提示。</summary>
    private const string MissingRoomIdInPageMessage = "无法从斗鱼房间页解析房间号。";

    /// <summary>房间页未给出标题时的提示。</summary>
    private const string MissingTitleMessage = "解析斗鱼直播标题失败。";

    /// <summary>房间页未给出主播名时的提示。</summary>
    private const string MissingAnchorMessage = "解析斗鱼主播名失败。";

    /// <summary>加密参数获取失败时的提示前缀。</summary>
    private const string EncryptionFailurePrefix = "斗鱼加密参数获取失败：";

    /// <summary>加密轮数异常时的提示。</summary>
    private const string InvalidEncryptionRoundsMessage = "斗鱼返回异常的加密轮数。";

    /// <summary>播放信息缺少 <c>data</c> 时的提示。</summary>
    private const string MissingPlayDataMessage = "斗鱼播放信息响应缺少 data 字段。";

    /// <summary>错误码缺失或类型不符时用于日志与提示的占位文本。</summary>
    private const string UnknownErrorCodeText = "unknown";

    /// <summary>加密轮数允许的下限。</summary>
    private const int MinEncryptionRounds = 0;

    /// <summary>加密轮数允许的上限，超出视为平台响应异常。</summary>
    private const int MaxEncryptionRounds = 100;

    /// <summary>播放接口成功错误码。</summary>
    private const int ErrorCodeSuccess = 0;

    /// <summary>播放接口"请求被拒绝"（风控）错误码。</summary>
    private const int ErrorCodeRejected = -15;

    /// <summary>播放接口"未开播"错误码。</summary>
    private const int ErrorCodeNotLive = -5;

    /// <summary>轮播探测中表示正在轮播的 <c>videoLoop</c> 取值。</summary>
    private const int VideoLoopReplaying = 1;

    /// <summary>加密参数 <c>is_special</c> 为 1 时签名不拼接房间号与时间戳。</summary>
    private const int SpecialFlagOn = 1;

    /// <summary>加密参数 <c>is_special</c> 缺失时的默认取值。</summary>
    private const int SpecialFlagOff = 0;

    /// <summary>播放接口 <c>rate</c> 参数：-1 表示请求平台给出的全部清晰度。</summary>
    private const string RateAll = "-1";

    /// <summary>播放接口 <c>hevc</c>/<c>fa</c>/<c>ive</c> 参数：0 表示关闭。</summary>
    private const string FlagOff = "0";

    /// <summary>URL 路径分隔符（RTMP 地址拼接与 HLS 目录形态补全）。</summary>
    private const string PathSeparator = "/";

    /// <summary>HTTP 地址前缀，用于识别平台把 HTTP-FLV 地址放进 RTMP 字段的情况。</summary>
    private const string HttpSchemePrefix = "http";

    /// <summary>无画质键（<c>url</c>/<c>main_url</c> 直接给出地址）时使用的占位键。</summary>
    private const string NoQualityKey = "";

    /// <summary>操作名：输入校验。</summary>
    private const string ValidateOperation = "validate";

    /// <summary>操作名：房间页抓取。</summary>
    private const string RoomPageOperation = "get-room-page";

    /// <summary>操作名：轮播探测。</summary>
    private const string BetardOperation = "get-betard";

    /// <summary>操作名：加密参数获取。</summary>
    private const string EncryptionOperation = "get-encryption";

    /// <summary>操作名：播放信息获取。</summary>
    private const string PlayInfoOperation = "getH5PlayV1";

    /// <summary>操作名：候选流组装。</summary>
    private const string StreamInfoOperation = "stream-info";

    /// <summary>日志模块名。</summary>
    private const string ModuleName = "Parsers.Douyu";

    /// <summary>轮播探测响应的房间对象字段名。</summary>
    private const string RoomField = "room";

    /// <summary>轮播标记字段名。</summary>
    private const string VideoLoopField = "videoLoop";

    /// <summary>业务数据字段名。</summary>
    private const string DataField = "data";

    /// <summary>错误码字段名。</summary>
    private const string ErrorField = "error";

    /// <summary>错误描述字段名。</summary>
    private const string MessageField = "msg";

    /// <summary>加密随机串字段名。</summary>
    private const string RandStringField = "rand_str";

    /// <summary>加密密钥字段名。</summary>
    private const string KeyField = "key";

    /// <summary>加密轮数字段名。</summary>
    private const string EncryptionTimeField = "enc_time";

    /// <summary>特殊加密标记字段名。</summary>
    private const string SpecialFlagField = "is_special";

    /// <summary>加密数据字段名。</summary>
    private const string EncDataField = "enc_data";

    /// <summary>RTMP 地址前缀字段名。</summary>
    private const string RtmpUrlField = "rtmp_url";

    /// <summary>RTMP 流名字段名（含签名）。</summary>
    private const string RtmpLiveField = "rtmp_live";

    /// <summary>HLS 地址字段名。</summary>
    private const string HlsUrlField = "hls_url";

    /// <summary>HLS 画质映射字段名。</summary>
    private const string HlsUrlMapField = "hls_url_map";

    /// <summary>HTTP 流对象字段名。</summary>
    private const string HttpStreamField = "http_stream";

    /// <summary>HTTP-FLV 画质映射字段名。</summary>
    private const string FlvPullUrlField = "flv_pull_url";

    /// <summary>单个流地址字段名。</summary>
    private const string UrlField = "url";

    /// <summary>主地址字段名。</summary>
    private const string MainUrlField = "main_url";

    /// <summary>房间页内嵌最终房间号的正则。</summary>
    private static readonly Regex LegacyRoomIdRegex = new(
        "getLegacyFirstStream\\(\\{\\s*roomID:\\s*(\\d+),",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>房间页标题的正则。</summary>
    private static readonly Regex RoomTitleRegex = new(
        "<h1 class=\"roomName.+?\">(.+?)</h1>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>房间页主播名的正则。</summary>
    private static readonly Regex AnchorNameRegex = new(
        "<h3 class=\"anchorName.+?\">(.+?)</h3>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>房间页分区的正则（可选字段）。</summary>
    private static readonly Regex CategoryRegex = new(
        "<span class=\"Title-categoryArrow\"></span><a class=\"Title-categoryItem\" href=\".+?\" target=\"_blank\" title=\"(.+?)\">",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpTextClient _http;

    /// <summary>初始化解析器。</summary>
    /// <param name="http">带超时与有界重试的 HTTP 客户端。</param>
    /// <param name="logger">结构化日志。</param>
    public DouyuParser(HttpTextClient http, IStructuredLogger logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <inheritdoc />
    public override PlatformId Platform => PlatformId.Douyu;

    /// <inheritdoc />
    public override string DisplayName => "斗鱼";

    /// <inheritdoc />
    public override string RoomUrlPrefix => DouyuBaseUrl;

    /// <inheritdoc />
    protected override async Task<ResolvedRoom> OnParseAsync(RoomQuery query, CancellationToken cancellationToken)
    {
        string? roomId = string.IsNullOrWhiteSpace(query.RoomId)
            ? TryExtractRoomIdFromUrl(query.RoomUrl)
            : query.RoomId;

        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw Fail(ResolveFailure.InvalidInput, ValidateOperation, MissingRoomIdMessage);
        }

        DouyuRoomPage page = await FetchRoomPageAsync(roomId, cancellationToken).ConfigureAwait(false);
        await FetchBetardAsync(page.RoomId, cancellationToken).ConfigureAwait(false);

        DouyuEncryption encryption = await FetchEncryptionAsync(page.RoomId, cancellationToken).ConfigureAwait(false);
        DouyuPlayInfo playInfo = await FetchPlayInfoAsync(page.RoomId, encryption, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StreamCandidate> candidates = CollectCandidates(page.RoomId, playInfo);

        Logger.Info(ModuleName, "斗鱼解析完成。", new Dictionary<string, object?>
        {
            ["roomId"] = page.RoomId,
            ["anchor"] = page.Anchor,
            ["candidateCount"] = candidates.Count,
            ["formats"] = string.Join(",", candidates.Select(static candidate => candidate.Format.ToString())),
        });

        return new ResolvedRoom
        {
            Platform = Platform,
            RoomId = page.RoomId,
            Anchor = page.Anchor,
            Title = page.Title,
            Category = page.Category,
            Candidates = candidates,
            ResolvedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>抓取房间页并抽取最终房间号、标题、主播名与分区。</summary>
    /// <param name="roomId">用户输入或链接中提取的房间号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>房间页元数据。</returns>
    /// <remarks>房间页是唯一能拿到标题与主播名的来源；房间号以页面内嵌的最终值为准（短号会跳转）。</remarks>
    private async Task<DouyuRoomPage> FetchRoomPageAsync(string roomId, CancellationToken cancellationToken)
    {
        HttpRequestSpec spec = new()
        {
            Url = string.Concat(DouyuBaseUrl, roomId),
            Platform = Platform,
            Operation = RoomPageOperation,
        };

        string html = await _http.GetStringAsync(spec, cancellationToken).ConfigureAwait(false);
        if (html.Contains(RoomClosedMarker, StringComparison.Ordinal))
        {
            throw Fail(ResolveFailure.RoomNotFound, RoomPageOperation, ResolveMessages.RoomNotFound);
        }

        Match roomIdMatch = LegacyRoomIdRegex.Match(html);
        if (!roomIdMatch.Success)
        {
            throw Fail(ResolveFailure.ParseError, RoomPageOperation, MissingRoomIdInPageMessage);
        }

        Match titleMatch = RoomTitleRegex.Match(html);
        if (!titleMatch.Success)
        {
            throw Fail(ResolveFailure.ParseError, RoomPageOperation, MissingTitleMessage);
        }

        Match anchorMatch = AnchorNameRegex.Match(html);
        if (!anchorMatch.Success)
        {
            throw Fail(ResolveFailure.ParseError, RoomPageOperation, MissingAnchorMessage);
        }

        Match categoryMatch = CategoryRegex.Match(html);
        string category = categoryMatch.Success ? categoryMatch.Groups[1].Value : string.Empty;

        return new DouyuRoomPage(
            roomIdMatch.Groups[1].Value,
            titleMatch.Groups[1].Value,
            anchorMatch.Groups[1].Value,
            category);
    }

    /// <summary>探测房间是否处于轮播/重播状态，命中时抛 <see cref="ResolveFailure.Replaying"/>。</summary>
    /// <param name="roomId">页面确认后的最终房间号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    /// <exception cref="ResolveException">判定为轮播时抛出。</exception>
    /// <remarks>
    /// 该探测是尽力而为：接口不可用、返回非 JSON 或结构变化时只记 Debug 并继续，
    /// 不阻断整体解析（斗鱼在部分地区不返回 betard 数据，在此失败会让正常直播也无法解析）。
    /// </remarks>
    private async Task FetchBetardAsync(string roomId, CancellationToken cancellationToken)
    {
        int? videoLoop = null;
        try
        {
            HttpRequestSpec spec = new()
            {
                Url = string.Concat(BetardUrlPrefix, roomId),
                Platform = Platform,
                Operation = BetardOperation,
            };

            using JsonDocument document = await _http.GetJsonAsync(spec, cancellationToken).ConfigureAwait(false);
            JsonElement room = GetPropertyOrUndefined(document.RootElement, RoomField);
            if (room.ValueKind == JsonValueKind.Object)
            {
                videoLoop = ReadInt32(room, VideoLoopField);
            }
        }
        catch (ResolveException exception)
        {
            Logger.LogError(LogLevel.Debug, ModuleName, "斗鱼轮播探测失败，跳过轮播判定。", exception, new Dictionary<string, object?>
            {
                ["operation"] = BetardOperation,
                ["roomId"] = roomId,
            });
        }

        if (videoLoop == VideoLoopReplaying)
        {
            throw Fail(ResolveFailure.Replaying, BetardOperation, ResolveMessages.Replaying);
        }

        Logger.Debug(ModuleName, "斗鱼轮播探测完成。", new Dictionary<string, object?>
        {
            ["operation"] = BetardOperation,
            ["roomId"] = roomId,
            ["videoLoop"] = videoLoop,
        });
    }

    /// <summary>获取加密参数并计算播放接口所需的签名。</summary>
    /// <param name="roomId">页面确认后的最终房间号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>签名参数与签名时间戳。</returns>
    /// <exception cref="ResolveException">接口返回错误、缺少 <c>data</c> 或加密轮数异常时抛出。</exception>
    /// <remarks>
    /// 签名算法：<c>enc_time</c> 轮 <c>md5(auth + key)</c>，再做一次 <c>md5(auth + key + signStr)</c>；
    /// <c>is_special</c> 为 1 时 <c>signStr</c> 为空，否则为 <c>房间号 + 时间戳</c>。
    /// </remarks>
    private async Task<DouyuEncryption> FetchEncryptionAsync(string roomId, CancellationToken cancellationToken)
    {
        HttpRequestSpec spec = new()
        {
            Url = EncryptionUrl,
            Platform = Platform,
            Operation = EncryptionOperation,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RefererHeader] = string.Concat(DouyuBaseUrl, roomId),
            },
        };

        using JsonDocument document = await _http.GetJsonAsync(spec, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        string errorMessage = ReadString(root, MessageField) ?? string.Empty;
        int? error = ReadInt32(root, ErrorField);
        if (error != ErrorCodeSuccess)
        {
            throw Fail(
                ResolveFailure.ParseError,
                EncryptionOperation,
                string.Concat(EncryptionFailurePrefix, errorMessage));
        }

        JsonElement data = GetPropertyOrUndefined(root, DataField);
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw Fail(
                ResolveFailure.ParseError,
                EncryptionOperation,
                string.Concat(EncryptionFailurePrefix, errorMessage));
        }

        string randString = ReadString(data, RandStringField) ?? string.Empty;
        string key = ReadString(data, KeyField) ?? string.Empty;
        string encData = ReadString(data, EncDataField) ?? string.Empty;
        int encryptionRounds = ReadInt32(data, EncryptionTimeField) ?? MinEncryptionRounds;
        int specialFlag = ReadInt32(data, SpecialFlagField) ?? SpecialFlagOff;
        if (encryptionRounds < MinEncryptionRounds || encryptionRounds > MaxEncryptionRounds)
        {
            throw Fail(ResolveFailure.ParseError, EncryptionOperation, InvalidEncryptionRoundsMessage);
        }

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signSource = specialFlag == SpecialFlagOn
            ? string.Empty
            : roomId + timestamp.ToString(CultureInfo.InvariantCulture);

        string auth = randString;
        for (int round = 0; round < encryptionRounds; round++)
        {
            auth = Md5Hex(auth + key);
        }

        auth = Md5Hex(auth + key + signSource);

        Logger.Debug(ModuleName, "斗鱼签名参数已生成。", new Dictionary<string, object?>
        {
            ["operation"] = EncryptionOperation,
            ["roomId"] = roomId,
            ["encryptionRounds"] = encryptionRounds,
            ["special"] = specialFlag == SpecialFlagOn,
            ["auth"] = SensitiveData.Fingerprint(auth),
        });

        return new DouyuEncryption(encData, auth, timestamp);
    }

    /// <summary>请求播放信息接口并把响应归一化为 <see cref="DouyuPlayInfo"/>。</summary>
    /// <param name="roomId">页面确认后的最终房间号。</param>
    /// <param name="encryption">加密参数与计算出的签名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>归一化后的播放信息。</returns>
    /// <exception cref="ResolveException">平台拒绝、未开播、错误码未知或响应结构异常时抛出。</exception>
    /// <remarks>
    /// 表单字段与顺序由接口约定固定：<c>rate=-1</c> 取全部清晰度，<c>hevc</c>/<c>fa</c>/<c>ive</c> 全部为 0。
    /// HTTP-FLV/HLS 相关字段可能整体缺失，读取时全部按可选处理。
    /// </remarks>
    private async Task<DouyuPlayInfo> FetchPlayInfoAsync(
        string roomId,
        DouyuEncryption encryption,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<KeyValuePair<string, string>> form =
        [
            new KeyValuePair<string, string>("enc_data", encryption.EncData),
            new KeyValuePair<string, string>("tt", encryption.Timestamp.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("did", DeviceId),
            new KeyValuePair<string, string>("auth", encryption.Auth),
            new KeyValuePair<string, string>("cdn", string.Empty),
            new KeyValuePair<string, string>("rate", RateAll),
            new KeyValuePair<string, string>("hevc", FlagOff),
            new KeyValuePair<string, string>("fa", FlagOff),
            new KeyValuePair<string, string>("ive", FlagOff),
        ];

        HttpRequestSpec spec = new()
        {
            Url = string.Concat(PlayInfoUrlPrefix, roomId),
            Platform = Platform,
            Operation = PlayInfoOperation,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RefererHeader] = string.Concat(DouyuBaseUrl, roomId),
            },
            ContentFactory = () => new FormUrlEncodedContent(form),
        };

        using JsonDocument document = await _http.GetJsonAsync(spec, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        string message = ReadString(root, MessageField) ?? string.Empty;
        int? error = ReadInt32(root, ErrorField);

        if (error == ErrorCodeRejected)
        {
            throw Fail(ResolveFailure.Rejected, PlayInfoOperation, ResolveMessages.Rejected);
        }

        if (error == ErrorCodeNotLive)
        {
            throw Fail(ResolveFailure.NotLive, PlayInfoOperation, ResolveMessages.NotLive);
        }

        if (string.Equals(message, MessageNotLive, StringComparison.Ordinal))
        {
            throw Fail(ResolveFailure.NotLive, PlayInfoOperation, ResolveMessages.NotLive);
        }

        if (string.Equals(message, MessageIllegalRequest, StringComparison.Ordinal))
        {
            throw Fail(ResolveFailure.Rejected, PlayInfoOperation, ResolveMessages.Rejected);
        }

        if (error != ErrorCodeSuccess)
        {
            string errorText = error.HasValue
                ? error.Value.ToString(CultureInfo.InvariantCulture)
                : UnknownErrorCodeText;
            throw Fail(ResolveFailure.ParseError, PlayInfoOperation, $"斗鱼接口返回 error={errorText}：{message}");
        }

        JsonElement data = GetPropertyOrUndefined(root, DataField);
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw Fail(ResolveFailure.ParseError, PlayInfoOperation, MissingPlayDataMessage);
        }

        string rtmpUrl = ReadString(data, RtmpUrlField) ?? string.Empty;
        string rtmpLive = ReadString(data, RtmpLiveField) ?? string.Empty;
        string hlsUrl = ReadString(data, HlsUrlField) ?? string.Empty;
        List<KeyValuePair<string, string>> flvUrls = ReadFlvUrls(data);
        List<KeyValuePair<string, string>> hlsUrls = ReadStringMap(data, HlsUrlMapField);

        Logger.Debug(ModuleName, "斗鱼播放信息已解析。", new Dictionary<string, object?>
        {
            ["operation"] = PlayInfoOperation,
            ["roomId"] = roomId,
            ["flvCount"] = flvUrls.Count,
            ["hlsCount"] = hlsUrls.Count,
            ["hlsUrlPresent"] = hlsUrl.Length > 0,
            ["rtmpUrlPresent"] = rtmpUrl.Length > 0,
            ["rtmpLive"] = SensitiveData.Fingerprint(rtmpLive),
        });

        return new DouyuPlayInfo(rtmpUrl, rtmpLive, hlsUrl, flvUrls, hlsUrls);
    }

    /// <summary>按优先级把播放信息组装为候选流：HTTP-FLV → HLS → RTMP 兜底。</summary>
    /// <param name="roomId">页面确认后的最终房间号，仅用于日志上下文。</param>
    /// <param name="playInfo">归一化后的播放信息。</param>
    /// <returns>候选流列表，下标即 <see cref="StreamCandidate.SourceIndex"/>。</returns>
    /// <exception cref="ResolveException">直播中但没有任何可用流地址时抛出 <see cref="ResolveFailure.NotLive"/>。</exception>
    /// <remarks>
    /// <c>http_stream</c> 与 <c>hls_url_map</c> 的每个画质键各自产出一个候选；
    /// <c>hls_url</c> 按参考实现补一个路径分隔符后加入（平台该字段为目录形态）。
    /// 仅剩 RTMP 时不抛未开播：房间确实在直播，只是 Web 端不可播放。
    /// </remarks>
    private IReadOnlyList<StreamCandidate> CollectCandidates(string roomId, DouyuPlayInfo playInfo)
    {
        StreamCandidateBuilder builder = new(Platform, Logger);

        foreach (KeyValuePair<string, string> entry in playInfo.FlvUrls)
        {
            builder.TryAdd(
                entry.Value,
                StreamFormat.FlvHttp,
                VideoCodec.Avc,
                MapDouyuQuality(entry.Key),
                expiresAt: null,
                referer: DouyuBaseUrl);
        }

        string rtmpAddress = BuildRtmpAddress(playInfo);
        bool hasRtmpAddress = rtmpAddress.Length > 0;
        bool isHttpAddress = hasRtmpAddress
            && rtmpAddress.StartsWith(HttpSchemePrefix, StringComparison.OrdinalIgnoreCase);
        if (isHttpAddress)
        {
            builder.TryAdd(
                rtmpAddress,
                StreamFormat.FlvHttp,
                VideoCodec.Avc,
                StreamQuality.Unknown,
                expiresAt: null,
                referer: DouyuBaseUrl);
        }

        if (!string.IsNullOrWhiteSpace(playInfo.HlsUrl))
        {
            builder.TryAdd(
                playInfo.HlsUrl + PathSeparator,
                StreamFormat.HlsTs,
                VideoCodec.Avc,
                StreamQuality.Unknown,
                expiresAt: null,
                referer: DouyuBaseUrl);
        }

        foreach (KeyValuePair<string, string> entry in playInfo.HlsUrls)
        {
            builder.TryAdd(
                entry.Value,
                StreamFormat.HlsTs,
                VideoCodec.Avc,
                MapDouyuQuality(entry.Key),
                expiresAt: null,
                referer: DouyuBaseUrl);
        }

        if (builder.Count == 0 && hasRtmpAddress && !isHttpAddress)
        {
            builder.TryAdd(
                rtmpAddress,
                StreamFormat.Rtmp,
                VideoCodec.Unknown,
                StreamQuality.Unknown,
                expiresAt: null,
                referer: DouyuBaseUrl);
        }

        if (builder.Count == 0)
        {
            throw Fail(ResolveFailure.NotLive, StreamInfoOperation, ResolveMessages.NotLive);
        }

        IReadOnlyList<StreamCandidate> candidates = builder.Build();
        Logger.Debug(ModuleName, "斗鱼候选流已汇总。", new Dictionary<string, object?>
        {
            ["operation"] = StreamInfoOperation,
            ["roomId"] = roomId,
            ["candidateCount"] = candidates.Count,
            ["fingerprints"] = string.Join(",", candidates.Select(static candidate => candidate.UrlFingerprint)),
        });

        return candidates;
    }

    /// <summary>拼接 <c>rtmp_url</c> 与 <c>rtmp_live</c>。</summary>
    /// <param name="playInfo">归一化后的播放信息。</param>
    /// <returns>拼接后的地址；任一字段为空时返回空字符串。</returns>
    private static string BuildRtmpAddress(DouyuPlayInfo playInfo)
    {
        return string.IsNullOrWhiteSpace(playInfo.RtmpUrl) || string.IsNullOrWhiteSpace(playInfo.RtmpLive)
            ? string.Empty
            : playInfo.RtmpUrl + PathSeparator + playInfo.RtmpLive;
    }

    /// <summary>计算字符串的 MD5 十六进制小写摘要。</summary>
    /// <param name="value">待摘要文本。</param>
    /// <returns>32 位小写十六进制摘要（斗鱼签名算法要求全小写）。</returns>
    private static string Md5Hex(string value)
    {
        byte[] digest = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>把斗鱼的画质键名映射为统一画质档位。</summary>
    /// <param name="qualityKey">画质键名，例如 <c>原画</c>、<c>超清</c>，或清晰度序号。</param>
    /// <returns>画质档位；无法识别时返回 <see cref="StreamQuality.Unknown"/>。</returns>
    private static StreamQuality MapDouyuQuality(string qualityKey)
    {
        if (string.IsNullOrWhiteSpace(qualityKey))
        {
            return StreamQuality.Unknown;
        }

        if (qualityKey.Contains("原画", StringComparison.Ordinal)
            || qualityKey.Contains("0", StringComparison.Ordinal)
            || qualityKey.Contains("超清", StringComparison.Ordinal))
        {
            return StreamQuality.Hd1080;
        }

        if (qualityKey.Contains("高清", StringComparison.Ordinal))
        {
            return StreamQuality.Hd720;
        }

        if (qualityKey.Contains("流畅", StringComparison.Ordinal)
            || qualityKey.Contains("标清", StringComparison.Ordinal))
        {
            return StreamQuality.Sd480;
        }

        return StreamQuality.Unknown;
    }

    /// <summary>读取 HTTP-FLV 候选：优先 <c>http_stream.flv_pull_url</c>，退化到 <c>url</c>/<c>main_url</c>。</summary>
    /// <param name="data">播放信息中的 <c>data</c> 对象。</param>
    /// <returns>画质键与地址的有序集合；无 HTTP 流时为空集合。</returns>
    private static List<KeyValuePair<string, string>> ReadFlvUrls(JsonElement data)
    {
        JsonElement stream = GetPropertyOrUndefined(data, HttpStreamField);
        if (stream.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<KeyValuePair<string, string>> urls = ReadStringMap(stream, FlvPullUrlField);
        if (urls.Count > 0)
        {
            return urls;
        }

        string? directUrl = ReadString(stream, UrlField) ?? ReadString(stream, MainUrlField);
        if (!string.IsNullOrWhiteSpace(directUrl))
        {
            urls.Add(new KeyValuePair<string, string>(NoQualityKey, directUrl));
        }

        return urls;
    }

    /// <summary>读取"键 → 地址"映射（仅接受字符串值，键被忽略类型问题）。</summary>
    /// <param name="element">父元素。</param>
    /// <param name="name">映射字段名。</param>
    /// <returns>键与地址的有序集合；字段缺失或类型不符时为空集合。</returns>
    private static List<KeyValuePair<string, string>> ReadStringMap(JsonElement element, string name)
    {
        List<KeyValuePair<string, string>> entries = [];
        JsonElement map = GetPropertyOrUndefined(element, name);
        if (map.ValueKind != JsonValueKind.Object)
        {
            return entries;
        }

        foreach (JsonProperty property in map.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? url = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                entries.Add(new KeyValuePair<string, string>(property.Name, url));
            }
        }

        return entries;
    }

    /// <summary>读取对象属性；元素不是对象或属性缺失时返回未定义元素（不抛异常）。</summary>
    /// <param name="element">父元素。</param>
    /// <param name="name">属性名。</param>
    /// <returns>属性值；未命中时返回 <see cref="JsonValueKind.Undefined"/>。</returns>
    private static JsonElement GetPropertyOrUndefined(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value
            : default;

    /// <summary>读取字符串属性（校验 <see cref="JsonValueKind.String"/>）。</summary>
    /// <param name="element">父元素。</param>
    /// <param name="name">属性名。</param>
    /// <returns>字符串值；缺失、类型不符或值为 JSON null 时返回 <see langword="null"/>。</returns>
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

    /// <summary>房间页抽取结果。</summary>
    /// <param name="RoomId">页面内嵌的最终房间号。</param>
    /// <param name="Title">直播间标题。</param>
    /// <param name="Anchor">主播名。</param>
    /// <param name="Category">直播分区；页面未提供时为空字符串。</param>
    private sealed record DouyuRoomPage(string RoomId, string Title, string Anchor, string Category);

    /// <summary>加密参数与计算出的签名。</summary>
    /// <param name="EncData">平台下发的加密数据。</param>
    /// <param name="Auth">计算出的签名（敏感，禁止写入日志原文）。</param>
    /// <param name="Timestamp">签名使用的时间戳（Unix 秒）。</param>
    private sealed record DouyuEncryption(string EncData, string Auth, long Timestamp);

    /// <summary>播放信息接口归一化后的结果。</summary>
    /// <param name="RtmpUrl">RTMP 地址前缀。</param>
    /// <param name="RtmpLive">RTMP 流名（含签名，敏感）。</param>
    /// <param name="HlsUrl">HLS 地址；部分房间不返回。</param>
    /// <param name="FlvUrls">HTTP-FLV 画质键与地址。</param>
    /// <param name="HlsUrls">HLS 画质键与地址。</param>
    private sealed record DouyuPlayInfo(
        string RtmpUrl,
        string RtmpLive,
        string HlsUrl,
        IReadOnlyList<KeyValuePair<string, string>> FlvUrls,
        IReadOnlyList<KeyValuePair<string, string>> HlsUrls);
}
