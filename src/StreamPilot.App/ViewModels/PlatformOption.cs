namespace StreamPilot.App.ViewModels;

using StreamPilot.Core.Models;

/// <summary>
/// 平台下拉项：把平台标识、展示名与直播间链接前缀绑在一起，避免界面各处硬编码。
/// </summary>
/// <param name="Id">平台标识。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="UrlPrefix">直播间链接前缀。</param>
public sealed record PlatformOption(PlatformId Id, string DisplayName, string UrlPrefix)
{
    /// <summary>可用的平台列表（顺序即界面展示顺序）。</summary>
    public static IReadOnlyList<PlatformOption> All { get; } =
    [
        new PlatformOption(PlatformId.Bilibili, "哔哩哔哩", "https://live.bilibili.com/"),
        new PlatformOption(PlatformId.Douyin, "抖音", "https://live.douyin.com/"),
        new PlatformOption(PlatformId.Huya, "虎牙", "https://www.huya.com/"),
        new PlatformOption(PlatformId.Douyu, "斗鱼", "https://www.douyu.com/"),
        new PlatformOption(PlatformId.Yy, "YY", "https://www.yy.com/"),
        new PlatformOption(PlatformId.Bigo, "Bigo Live", "https://www.bigo.tv/"),
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
