namespace StreamPilot.Core.Models;

/// <summary>
/// 解析失败分类与画质档位的中文展示文本。
/// </summary>
/// <remarks>
/// UI 提示统一从这里取文案，避免各平台解析器各自拼字符串导致措辞不一致。
/// </remarks>
public static class ResolveMessages
{
    /// <summary>未开播的提示。</summary>
    public const string NotLive = "该直播间当前未开播。";

    /// <summary>房间不存在的提示。</summary>
    public const string RoomNotFound = "房间号不存在，请检查后重试。";

    /// <summary>轮播/重播的提示。</summary>
    public const string Replaying = "该直播间正在轮播/重播，本程序不解析重播源。";

    /// <summary>网络错误的提示。</summary>
    public const string NetworkError = "网络请求失败，请检查网络后重试。";

    /// <summary>平台拒绝请求的提示。</summary>
    public const string Rejected = "平台拒绝了本次请求（可能触发风控），请稍后重试。";

    /// <summary>输入非法的提示。</summary>
    public const string InvalidInput = "输入的房间号或链接不合法。";

    /// <summary>平台不支持的提示。</summary>
    public const string Unsupported = "该平台暂不受支持。";

    /// <summary>解析错误的通用提示。</summary>
    public const string ParseError = "解析平台响应失败，接口可能已变更。";

    /// <summary>把失败分类转换为面向用户的提示。</summary>
    /// <param name="failure">失败分类。</param>
    /// <returns>中文提示。</returns>
    public static string ForFailure(ResolveFailure failure) => failure switch
    {
        ResolveFailure.NotLive => NotLive,
        ResolveFailure.RoomNotFound => RoomNotFound,
        ResolveFailure.Replaying => Replaying,
        ResolveFailure.NetworkError => NetworkError,
        ResolveFailure.Rejected => Rejected,
        ResolveFailure.InvalidInput => InvalidInput,
        ResolveFailure.Unsupported => Unsupported,
        ResolveFailure.ParseError => ParseError,
        _ => "解析失败，请稍后重试。",
    };

    /// <summary>获取画质档位的中文名。</summary>
    /// <param name="quality">画质档位。</param>
    /// <returns>中文名。</returns>
    public static string ForQuality(StreamQuality quality) => quality switch
    {
        StreamQuality.Dolby => "杜比原画",
        StreamQuality.Uhd4K => "4K",
        StreamQuality.Qhd2K => "2K",
        StreamQuality.Hd1080HighFps => "1080P 高帧率",
        StreamQuality.Hd1080 => "1080P",
        StreamQuality.Hd720 => "720P",
        StreamQuality.Sd480 => "流畅",
        _ => "未知画质",
    };

    /// <summary>获取编码的中文名。</summary>
    /// <param name="codec">编码。</param>
    /// <returns>中文名。</returns>
    public static string ForCodec(VideoCodec codec) => codec switch
    {
        VideoCodec.Avc => "H.264",
        VideoCodec.Hevc => "H.265 (HEVC)",
        VideoCodec.Av1 => "AV1",
        _ => "未知编码",
    };
}
