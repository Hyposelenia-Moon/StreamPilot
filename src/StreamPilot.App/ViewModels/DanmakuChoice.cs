namespace StreamPilot.App.ViewModels;

using StreamPilot.Core.Configuration;

/// <summary>
/// 弹幕下拉项：把枚举值、展示名与一句解释绑在一起，避免界面里散落数字与硬编码文案。
/// </summary>
/// <param name="Value">枚举的数值（与 <see cref="DanmakuDisplayMode"/> / <see cref="DanmakuFilterMode"/> 对应）。</param>
/// <param name="DisplayName">下拉里显示的文案。</param>
/// <param name="Hint">悬停提示：说明这一档的取舍。</param>
public sealed record DanmakuChoice(int Value, string DisplayName, string Hint)
{
    /// <summary>显示模式选项（精简版 / 完整版）。</summary>
    public static IReadOnlyList<DanmakuChoice> DisplayModes { get; } =
    [
        new DanmakuChoice(
            (int)DanmakuDisplayMode.Compact,
            "精简版",
            "只显示「昵称：内容」，信息密度最高，适合屏幕拥挤时观看"),
        new DanmakuChoice(
            (int)DanmakuDisplayMode.Full,
            "完整版",
            "额外显示粉丝牌与等级，信息更全但每条更宽"),
    ];

    /// <summary>消息类型过滤选项（只看弹幕 / 含礼物 / 全部）。</summary>
    public static IReadOnlyList<DanmakuChoice> Filters { get; } =
    [
        new DanmakuChoice(
            (int)DanmakuFilterMode.ChatOnly,
            "只看弹幕",
            "只显示普通聊天弹幕，画面最干净"),
        new DanmakuChoice(
            (int)DanmakuFilterMode.WithGift,
            "弹幕 + 礼物",
            "额外显示礼物与上舰提示"),
        new DanmakuChoice(
            (int)DanmakuFilterMode.All,
            "全部",
            "额外显示进场互动与系统消息，信息最全也最吵"),
    ];

    /// <summary>按枚举值找选项；找不到时返回第一项（避免下拉出现空白）。</summary>
    /// <param name="choices">候选列表。</param>
    /// <param name="value">目标数值。</param>
    /// <returns>命中的选项。</returns>
    public static DanmakuChoice Find(IReadOnlyList<DanmakuChoice> choices, int value)
    {
        ArgumentNullException.ThrowIfNull(choices);
        foreach (DanmakuChoice choice in choices)
        {
            if (choice.Value == value)
            {
                return choice;
            }
        }

        return choices[0];
    }
}
