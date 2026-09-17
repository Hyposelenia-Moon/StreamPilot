namespace StreamPilot.Core.Models;

/// <summary>
/// 解析失败的互斥分类。UI 依据该分类给出不同的用户提示。
/// </summary>
public enum ResolveFailure
{
    /// <summary>未分类的内部错误。</summary>
    Unknown = 0,

    /// <summary>房间存在但当前未开播。</summary>
    NotLive = 1,

    /// <summary>房间号不存在或房间已注销。</summary>
    RoomNotFound = 2,

    /// <summary>房间正在轮播/重播，本程序不解析重播源。</summary>
    Replaying = 3,

    /// <summary>响应结构变化或关键字段缺失导致的解析错误。</summary>
    ParseError = 4,

    /// <summary>网络错误：连接失败、超时、HTTP 4xx/5xx、响应体截断。</summary>
    NetworkError = 5,

    /// <summary>请求被平台拒绝（风控 / 非法请求）。不重试、不绕过。</summary>
    Rejected = 6,

    /// <summary>调用方输入非法（房间号格式错误、URL 域名不匹配等）。</summary>
    InvalidInput = 7,

    /// <summary>平台不受支持或该平台的必要能力缺失。</summary>
    Unsupported = 8,
}
