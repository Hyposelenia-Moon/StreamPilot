namespace StreamPilot.Core.Models;

/// <summary>
/// 直播间的开播状态（轻量查询结果，用于预设开播检测，不携带播放地址）。
/// </summary>
public enum LiveStatusKind
{
    /// <summary>未知（接口未给出或查询失败）。</summary>
    Unknown = 0,

    /// <summary>正在直播。</summary>
    Live = 1,

    /// <summary>轮播 / 重播。</summary>
    Replaying = 2,

    /// <summary>未开播。</summary>
    Offline = 3,
}

/// <summary>
/// 单个房间的开播状态快照。
/// </summary>
/// <remarks>
/// 与 <see cref="ResolvedRoom"/> 不同，这里不包含任何播放候选地址：开播检测只需要知道
/// "在不在播"，没必要为每个预设都拉一次带时效签名的播放地址（浪费请求、也加重解析开销）。
/// </remarks>
public sealed record RoomStatus
{
    /// <summary>请求时使用的房间号或链接（用于把结果对齐回对应的预设）。</summary>
    public required string RoomId { get; init; }

    /// <summary>开播状态。</summary>
    public required LiveStatusKind Status { get; init; }

    /// <summary>平台返回的主播名（缺失时为 <see langword="null"/>）。</summary>
    public string? Anchor { get; init; }

    /// <summary>平台返回的标题（缺失时为 <see langword="null"/>）。</summary>
    public string? Title { get; init; }

    /// <summary>查询失败原因；成功时为空字符串。</summary>
    public string? Error { get; init; }

    /// <summary>创建一条"查询失败"的结果。</summary>
    /// <param name="roomId">请求时的房间号或链接。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>失败状态快照。</returns>
    public static RoomStatus FromError(string roomId, string error) => new()
    {
        RoomId = roomId,
        Status = LiveStatusKind.Unknown,
        Error = error,
    };
}
