namespace StreamPilot.Core.Models;

/// <summary>
/// 视频编码类型。
/// </summary>
public enum VideoCodec
{
    /// <summary>未知编码。</summary>
    Unknown = 0,

    /// <summary>H.264 / AVC（WebView2 与绝大多数系统均支持）。</summary>
    Avc = 1,

    /// <summary>H.265 / HEVC（依赖系统 HEVC 解码器，Web 端可能不可用）。</summary>
    Hevc = 2,

    /// <summary>AV1。</summary>
    Av1 = 3,
}
