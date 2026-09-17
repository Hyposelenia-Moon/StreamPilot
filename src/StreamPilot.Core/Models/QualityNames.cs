namespace StreamPilot.Core.Models;

/// <summary>平台画质名称与内部画质档位的映射。</summary>
public static class QualityNames
{
    /// <summary>B站最高画质请求值（杜比原画）。</summary>
    public const int BilibiliMaxQualityNumber = 30000;

    /// <summary>B站 4K 画质值。</summary>
    public const int BilibiliQuality4K = 20000;

    /// <summary>B站 2K 画质值。</summary>
    public const int BilibiliQuality2K = 15000;

    /// <summary>B站 1080P 高帧率画质值。</summary>
    public const int BilibiliQuality1080HighFps = 10000;

    /// <summary>B站 1080P 画质值。</summary>
    public const int BilibiliQuality1080 = 400;

    /// <summary>B站 720P 画质值。</summary>
    public const int BilibiliQuality720 = 250;

    /// <summary>B站 480P 画质值。</summary>
    public const int BilibiliQuality480 = 150;

    /// <summary>抖音最高画质键名。</summary>
    public const string DouyinQualityFullHd1 = "FULL_HD1";

    /// <summary>抖音高清画质键名。</summary>
    public const string DouyinQualityHd1 = "HD1";

    /// <summary>抖音标清画质键名。</summary>
    public const string DouyinQualitySd1 = "SD1";

    /// <summary>抖音低清画质键名。</summary>
    public const string DouyinQualitySd2 = "SD2";

    /// <summary>把 B站的 qn 数值映射为内部画质档位。</summary>
    /// <param name="qualityNumber">B站画质数值（来自 <c>current_qn</c>/<c>accept_qn</c>）。</param>
    /// <returns>对应的画质档位，未知时返回 <see cref="StreamQuality.Unknown"/>。</returns>
    public static StreamQuality FromBilibiliQualityNumber(int qualityNumber) => qualityNumber switch
    {
        BilibiliMaxQualityNumber => StreamQuality.Dolby,
        BilibiliQuality4K => StreamQuality.Uhd4K,
        BilibiliQuality2K => StreamQuality.Qhd2K,
        BilibiliQuality1080HighFps => StreamQuality.Hd1080HighFps,
        BilibiliQuality1080 => StreamQuality.Hd1080,
        BilibiliQuality720 => StreamQuality.Hd720,
        BilibiliQuality480 => StreamQuality.Sd480,
        _ => StreamQuality.Unknown,
    };

    /// <summary>把抖音画质键名映射为内部画质档位。</summary>
    /// <param name="qualityName">抖音 <c>flv_pull_url</c>/<c>hls_pull_url_map</c> 的键名。</param>
    /// <returns>对应的画质档位，未知时返回 <see cref="StreamQuality.Unknown"/>。</returns>
    public static StreamQuality FromDouyinQualityName(string? qualityName) => qualityName switch
    {
        DouyinQualityFullHd1 => StreamQuality.Hd1080,
        DouyinQualityHd1 => StreamQuality.Hd720,
        DouyinQualitySd1 => StreamQuality.Sd480,
        DouyinQualitySd2 => StreamQuality.Sd480,
        _ => StreamQuality.Unknown,
    };
}
