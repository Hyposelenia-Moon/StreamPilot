namespace StreamPilot.Core.Models;

/// <summary>
/// 支持的直播平台标识。
/// </summary>
/// <remarks>
/// 数值一旦发布即不可变更（用于配置与历史记录的持久化），新增平台只能追加新值。
/// </remarks>
public enum PlatformId
{
    /// <summary>未知或被禁用的平台。</summary>
    Unknown = 0,

    /// <summary>哔哩哔哩直播（P0）。</summary>
    Bilibili = 1,

    /// <summary>抖音直播（P0）。</summary>
    Douyin = 2,

    /// <summary>虎牙直播（P0）。</summary>
    Huya = 3,

    /// <summary>斗鱼直播（P1）。</summary>
    Douyu = 4,

    /// <summary>YY 直播（P1）。</summary>
    Yy = 5,

    /// <summary>Bigo Live（P2）。</summary>
    Bigo = 6,
}
