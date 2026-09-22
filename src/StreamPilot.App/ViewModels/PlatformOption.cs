namespace StreamPilot.App.ViewModels;

using StreamPilot.Core.Models;

/// <summary>
/// 平台下拉项：把平台标识、展示名与该平台可识别的域名绑在一起，避免界面各处硬编码。
/// </summary>
/// <param name="Id">平台标识。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="MatchDomains">
/// 本平台可识别的域名（含各平台分享出的短链域名），第一项为该平台直播间页面域名。
/// 命中规则是"主机名等于域名或是它的子域"，由 <c>PlatformDetector</c> 实现；
/// 这里必须把分享短链域名一并列出，否则用户粘贴分享链接时会被当成别的平台。
/// </param>
public sealed record PlatformOption(PlatformId Id, string DisplayName, IReadOnlyList<string> MatchDomains)
{
    /// <summary>哔哩哔哩直播间域名。</summary>
    private const string BilibiliDomain = "bilibili.com";

    /// <summary>哔哩哔哩分享短链域名（官方 App 分享出的是 b23.tv 链接）。</summary>
    private const string BilibiliShortLinkDomain = "b23.tv";

    /// <summary>抖音域名（直播页 live.douyin.com 与分享短链 v.douyin.com 同属该域）。</summary>
    private const string DouyinDomain = "douyin.com";

    /// <summary>虎牙域名（www / m / v 等子域同属该域）。</summary>
    private const string HuyaDomain = "huya.com";

    /// <summary>斗鱼域名。</summary>
    private const string DouyuDomain = "douyu.com";

    /// <summary>YY 域名。</summary>
    private const string YyDomain = "yy.com";

    /// <summary>Bigo Live 主域名。</summary>
    private const string BigoDomain = "bigo.tv";

    /// <summary>Bigo Live 东南亚域名。</summary>
    private const string BigoSoutheastAsiaDomain = "bigo.sg";

    /// <summary>可用的平台列表（顺序即界面展示顺序，也是平台识别的优先级顺序）。</summary>
    public static IReadOnlyList<PlatformOption> All { get; } =
    [
        new PlatformOption(PlatformId.Bilibili, "哔哩哔哩", [BilibiliDomain, BilibiliShortLinkDomain]),
        new PlatformOption(PlatformId.Douyin, "抖音", [DouyinDomain]),
        new PlatformOption(PlatformId.Huya, "虎牙", [HuyaDomain]),
        new PlatformOption(PlatformId.Douyu, "斗鱼", [DouyuDomain]),
        new PlatformOption(PlatformId.Yy, "YY", [YyDomain]),
        new PlatformOption(PlatformId.Bigo, "Bigo Live", [BigoDomain, BigoSoutheastAsiaDomain]),
    ];

    /// <summary>按平台标识查找展示项。</summary>
    /// <param name="platform">平台标识。</param>
    /// <returns>找到时返回对应项，否则返回 <see langword="null"/>。</returns>
    public static PlatformOption? Find(PlatformId platform)
    {
        foreach (PlatformOption option in All)
        {
            if (option.Id == platform)
            {
                return option;
            }
        }

        return null;
    }
}
