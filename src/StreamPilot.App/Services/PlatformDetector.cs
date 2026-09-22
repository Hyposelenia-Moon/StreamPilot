namespace StreamPilot.App.Services;

using StreamPilot.App.ViewModels;
using StreamPilot.Core.Models;

/// <summary>
/// 平台识别的纯函数实现：只解析输入文本里的域名，不发起任何网络请求、不读文件、不依赖界面。
/// </summary>
/// <remarks>
/// 「新增预设」与「解析房间」都用它把用户粘贴的内容落到具体平台。可识别的域名集中在
/// <see cref="PlatformOption.All"/> 的 <c>MatchDomains</c> 里维护（含分享短链域名），
/// 这里不写任何平台专属的网络逻辑（CLAUDE.md：UI 层不得直接读平台 API）。
/// 输入里找不到可识别域名时返回 <see langword="null"/>，由调用方回退到用户指定的平台
/// （对话框里选的平台，或设置里的「默认平台」）。
/// 纯函数没有状态与 IO，因此可被单元测试直接调用（见 <c>tests/StreamPilot.Tests/Cases/PlatformDetectionTests.cs</c>）。
/// </remarks>
public static class PlatformDetector
{
    /// <summary>链接里 scheme 与主机名之间的分隔标记。</summary>
    private const string SchemeMarker = "://";

    /// <summary>域名标签分隔符。</summary>
    private const char DomainLabelSeparator = '.';

    /// <summary>域名里允许的连字符。</summary>
    private const char HyphenCharacter = '-';

    /// <summary>域名里允许的下划线（部分短链域名使用）。</summary>
    private const char UnderscoreCharacter = '_';

    /// <summary>
    /// 识别输入文本所属的平台。
    /// </summary>
    /// <param name="input">房间号、直播间链接，或含链接的分享文案。</param>
    /// <returns>识别出的平台；无法识别时返回 <see langword="null"/>。</returns>
    public static PlatformId? Detect(string? input)
    {
        string text = input is null ? string.Empty : input.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        foreach (string host in ExtractCandidateHosts(text))
        {
            PlatformId? platform = MatchHost(host);
            if (platform is not null)
            {
                return platform;
            }
        }

        return null;
    }

    /// <summary>
    /// 识别输入文本所属的平台，识别不出时回退到调用方给定的平台。
    /// </summary>
    /// <param name="input">房间号、直播间链接，或含链接的分享文案。</param>
    /// <param name="fallback">识别不出时使用的平台（对话框里选的平台，或设置里的「默认平台」）。</param>
    /// <returns>识别出的平台，或 <paramref name="fallback"/>。</returns>
    public static PlatformId DetectOrFallback(string? input, PlatformId fallback) => Detect(input) ?? fallback;

    /// <summary>按域名匹配平台。</summary>
    /// <param name="host">已规范化的主机名（小写、不含端口与路径）。</param>
    /// <returns>命中的平台；没有任何平台命中时返回 <see langword="null"/>。</returns>
    private static PlatformId? MatchHost(string host)
    {
        foreach (PlatformOption option in PlatformOption.All)
        {
            foreach (string domain in option.MatchDomains)
            {
                if (IsSameOrSubDomain(host, domain))
                {
                    return option.Id;
                }
            }
        }

        return null;
    }

    /// <summary>判断主机名是否等于该域名，或是它的子域（避免 "myy.com" 这类相似域名误判）。</summary>
    /// <param name="host">已规范化的主机名。</param>
    /// <param name="domain">平台域名。</param>
    /// <returns>命中返回 <see langword="true"/>。</returns>
    private static bool IsSameOrSubDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(DomainLabelSeparator + domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按出现顺序抽取文本里可能的主机名。
    /// </summary>
    /// <param name="text">已去除首尾空白的输入文本。</param>
    /// <returns>候选主机名（小写）。</returns>
    /// <remarks>
    /// 先按 <c>://</c> 找链接（分享文案里链接前后常带中文说明），
    /// 文本里完全没有 scheme 时才把开头当成裸域名（例如 <c>live.bilibili.com/123</c>）处理。
    /// </remarks>
    private static IEnumerable<string> ExtractCandidateHosts(string text)
    {
        bool foundScheme = false;
        int searchFrom = 0;
        while (searchFrom < text.Length)
        {
            int marker = text.IndexOf(SchemeMarker, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                break;
            }

            int hostStart = marker + SchemeMarker.Length;
            searchFrom = hostStart;
            string host = ReadHost(text, hostStart);
            if (host.Length == 0)
            {
                continue;
            }

            foundScheme = true;
            yield return host;
        }

        if (foundScheme)
        {
            yield break;
        }

        string bareHost = ReadHost(text, 0);
        if (bareHost.Length > 0)
        {
            yield return bareHost;
        }
    }

    /// <summary>从指定位置读取连续的主机名字符并规范化为小写。</summary>
    /// <param name="text">输入文本。</param>
    /// <param name="start">起始下标。</param>
    /// <returns>主机名；该位置不是主机名字符时返回空字符串。</returns>
    private static string ReadHost(string text, int start)
    {
        int end = start;
        while (end < text.Length && IsHostCharacter(text[end]))
        {
            end++;
        }

        return end == start ? string.Empty : text[start..end].ToLowerInvariant();
    }

    /// <summary>判断字符是否属于主机名（字母、数字、点、连字符、下划线）。</summary>
    /// <param name="value">待判定字符。</param>
    /// <returns>属于主机名返回 <see langword="true"/>。</returns>
    private static bool IsHostCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value)
        || value == DomainLabelSeparator
        || value == HyphenCharacter
        || value == UnderscoreCharacter;
}
