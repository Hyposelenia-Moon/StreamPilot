namespace StreamPilot.Core.Models;

/// <summary>
/// 流的传输/容器格式。
/// </summary>
public enum StreamFormat
{
    /// <summary>格式未知，不可播放也不可录制。</summary>
    Unknown = 0,

    /// <summary>HTTP-FLV（B站/抖音主链路，支持 Web 播放与原始流录制）。</summary>
    FlvHttp = 1,

    /// <summary>HLS + MPEG-TS 分片（Web 播放与原始流录制均支持）。</summary>
    HlsTs = 2,

    /// <summary>HLS + fMP4 分片（Web 可播放，首版不支持录制）。</summary>
    HlsFmp4 = 3,

    /// <summary>RTMP（Web 端不可播放；首版不支持录制，仅可交给 mpv）。</summary>
    Rtmp = 4,
}
