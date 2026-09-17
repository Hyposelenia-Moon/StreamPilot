namespace StreamPilot.Core.Models;

/// <summary>
/// 画质档位。数值越大表示画质越高，用于候选排序。
/// </summary>
public enum StreamQuality
{
    /// <summary>平台未声明画质。</summary>
    Unknown = 0,

    /// <summary>流畅 / 低清。</summary>
    Sd480 = 1,

    /// <summary>高清 720P。</summary>
    Hd720 = 2,

    /// <summary>超清 1080P。</summary>
    Hd1080 = 3,

    /// <summary>1080P 高帧率。</summary>
    Hd1080HighFps = 4,

    /// <summary>2K。</summary>
    Qhd2K = 5,

    /// <summary>4K。</summary>
    Uhd4K = 6,

    /// <summary>杜比（B站最高档，对应 qn=30000）。</summary>
    Dolby = 7,
}
