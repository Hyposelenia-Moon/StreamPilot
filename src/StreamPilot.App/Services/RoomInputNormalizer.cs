namespace StreamPilot.App.Services;

using StreamPilot.Core.Models;

/// <summary>
/// 把用户输入的"分享文案 / 缺 scheme 的链接 / 带标点的链接"整理成解析器能直接接受的房间输入。
/// </summary>
/// <remarks>
/// 平台分享出来的内容经常长这样：<c>【主播开播了】https://www.huya.com/660000 ，快来</c>、
/// <c>live.douyin.com/12345</c>、<c>https://live.bilibili.com/814?from=share#x</c>。
/// 解析器的域名校验只认"以域名开头的完整地址"，因此这类输入此前会被判成
/// "直播间链接的域名不属于 X"。这里只做**纯文本整理**，不联网、不跟随短链跳转：
/// 短链（<c>v.douyin.com</c> / <c>b23.tv</c>）需要解析层跟随 302 才能拿到真实房间号，属独立改动。
/// </remarks>
public static class RoomInputNormalizer
{
    /// <summary>链接协议的起始标记。</summary>
    private const string SchemeMarker = "://";

    /// <summary>https 协议前缀。</summary>
    private const string HttpsScheme = "https://";

    /// <summary>http 协议前缀。</summary>
    private const string HttpScheme = "http://";

    /// <summary>无 scheme 时补上的默认协议。</summary>
    private const string DefaultScheme = "https://";

    /// <summary>裸域名的判定前缀（其余情况一律当作房间号，保持原样）。</summary>
    private static readonly string[] BareDomainHints =
    [
        "www.",
        "live.",
        "m.",
        "v.",
        "b23.tv",
        "bigo.sg",
        "bigo.tv",
    ];

    /// <summary>各平台直播间链接前缀（与解析器的 <c>RoomUrlPrefix</c> 保持一致）。</summary>
    private static readonly Dictionary<PlatformId, string> RoomUrlPrefixes = new()
    {
        [PlatformId.Bilibili] = "https://live.bilibili.com/",
        [PlatformId.Douyu] = "https://www.douyu.com/",
        [PlatformId.Huya] = "https://www.huya.com/",
        [PlatformId.Douyin] = "https://live.douyin.com/",
        [PlatformId.Yy] = "https://www.yy.com/",
        [PlatformId.Bigo] = "https://www.bigo.tv/",
    };

    /// <summary>判断输入是否为"纯房间号"（只有数字）。</summary>
    /// <param name="input">用户输入。</param>
    /// <returns>全部为数字且非空返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 只把纯数字当作房间号：虎牙的房间号也可能是字母组合，贸然转换会拼出打不开的链接；
    /// 字母房间号按原样交给解析器（它们本来就接受"房间号"这种输入）。
    /// </remarks>
    public static bool IsPlainRoomId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string trimmed = input.Trim();
        foreach (char character in trimmed)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 把房间号拼成对应平台的直播间链接。
    /// </summary>
    /// <param name="platform">平台；未知时原样返回房间号。</param>
    /// <param name="roomId">房间号。</param>
    /// <returns>直播间链接；平台未知或房间号为空时返回空字符串。</returns>
    public static string BuildRoomUrl(PlatformId platform, string? roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return string.Empty;
        }

        string id = roomId.Trim();
        return RoomUrlPrefixes.TryGetValue(platform, out string? prefix) ? prefix + id : id;
    }

    /// <summary>链接末尾常见的标点与包裹字符：从右往左一并去掉。</summary>
    private static readonly char[] TrailingNoise =
    [
        ' ', '\t', '\r', '\n', '，', '。', '！', '？', '；', '、', '：',
        ',', '.', '!', '?', ';', ':', ')', ']', '}', '>', '"', '\'',
        '）', '］', '｝', '》', '】', '」', '”', '’',
    ];

    /// <summary>链接两侧可能出现、需要剔除的包裹字符。</summary>
    private static readonly char[] LeadingNoise = ['<', '"', '\'', '（', '(', '【', '「'];

    /// <summary>
    /// 整理房间输入：抽取文案里的第一个链接、补全缺失的 scheme、去掉首尾标点。
    /// </summary>
    /// <param name="input">用户原始输入（房间号、链接或整段分享文案）。</param>
    /// <returns>整理后的输入；输入为空时返回空字符串。</returns>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        string candidate = ExtractUrl(input);
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        candidate = candidate.Trim().Trim(LeadingNoise).TrimEnd(TrailingNoise);
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        return candidate.Contains(SchemeMarker, StringComparison.Ordinal)
            ? candidate
            : AddSchemeForBareDomain(candidate);
    }

    /// <summary>
    /// 判断整理后的输入是否应按"链接"处理（其余一律按房间号处理）。
    /// </summary>
    /// <param name="input">已整理的输入。</param>
    /// <returns>是链接时返回 <see langword="true"/>。</returns>
    public static bool LooksLikeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string value = input.Trim();
        return value.Contains(SchemeMarker, StringComparison.Ordinal)
            || value.Contains('/', StringComparison.Ordinal);
    }

    /// <summary>
    /// 从文本中抽取第一个链接；没有链接时返回去掉首尾空白的原文。
    /// </summary>
    /// <param name="input">原始输入。</param>
    /// <returns>链接或原文。</returns>
    /// <remarks>
    /// 只认 <c>http://</c> 与 <c>https://</c> 这两种协议：输入框里可能残留前一次输入的前缀
    /// （真实机器上出现过 <c>wwwhttps://live.bilibili.com/814</c>），按"任意 ://"抽取会把前缀
    /// 一起吃进去，最后变成"域名不属于 X"这种让人摸不着头脑的报错。
    /// </remarks>
    private static string ExtractUrl(string input)
    {
        int start = FindSchemeStart(input);
        if (start < 0)
        {
            return input.Trim();
        }

        int prefixLength = input.AsSpan(start).StartsWith("https://", StringComparison.Ordinal) ? HttpsScheme.Length : HttpScheme.Length;
        int end = input.Length;
        for (int index = start + prefixLength; index < input.Length; index += 1)
        {
            if (input[index] == ' ' || input[index] == '\t' || input[index] == '\r' || input[index] == '\n')
            {
                end = index;
                break;
            }
        }

        return input[start..end];
    }

    /// <summary>找到第一个 http/https 协议的起始下标。</summary>
    /// <param name="input">原始输入。</param>
    /// <returns>下标；没有协议时返回 <c>-1</c>。</returns>
    private static int FindSchemeStart(string input)
    {
        int https = input.IndexOf(HttpsScheme, StringComparison.OrdinalIgnoreCase);
        int http = input.IndexOf(HttpScheme, StringComparison.OrdinalIgnoreCase);
        if (https < 0)
        {
            return http;
        }

        return http < 0 ? https : Math.Min(https, http);
    }

    /// <summary>
    /// 给"看起来像域名但缺 scheme"的输入补上 <c>https://</c>；不像域名的（房间号）原样返回。
    /// </summary>
    /// <param name="value">已去掉标点的输入。</param>
    /// <returns>补全后的输入。</returns>
    private static string AddSchemeForBareDomain(string value)
    {
        string lowered = value.ToLowerInvariant();
        bool looksLikeDomain = lowered.Contains('.', StringComparison.Ordinal)
            && BareDomainHints.Any(hint => lowered.StartsWith(hint, StringComparison.Ordinal) || lowered.Contains("." + hint, StringComparison.Ordinal));
        if (!looksLikeDomain)
        {
            return value;
        }

        return DefaultScheme + value.TrimStart('/');
    }
}
