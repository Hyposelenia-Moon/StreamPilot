namespace StreamPilot.Core.Models;

/// <summary>
/// 解析请求的输入。房间号与直播间链接二选一，两者都提供时以 <see cref="RoomId"/> 为准。
/// </summary>
/// <remarks>
/// 所有外部输入必须校验：<see cref="RoomId"/> 只允许数字与大写字母组成的短号，
/// <see cref="RoomUrl"/> 必须是 http/https 且主机属于目标平台。
/// </remarks>
public sealed record RoomQuery
{
    /// <summary>房间号（数字房间号，或平台允许的字母数字短号）。</summary>
    public string? RoomId { get; init; }

    /// <summary>直播间链接。</summary>
    public string? RoomUrl { get; init; }

    /// <summary>平台标识，用于选择解析器。</summary>
    public required PlatformId Platform { get; init; }

    /// <summary>
    /// 用户自备的该平台 Cookie（可选）；为空时走匿名解析。
    /// </summary>
    /// <remarks>
    /// Cookie 只用于解析请求（换取最高画质/多档位），解析出的播放地址不带登录态，
    /// 播放过程也不会把 Cookie 发给 CDN，因此主播看不到"这位观众"。
    /// </remarks>
    public string? Cookie { get; init; }

    /// <summary>
    /// 期望的画质档位键（来自 <see cref="QualityOption.Key"/>）；为空或 <c>best</c> 时取平台最高档。
    /// </summary>
    public string? PreferredQualityKey { get; init; }

    /// <summary>创建一个仅含房间号的查询。</summary>
    /// <param name="platform">平台标识。</param>
    /// <param name="roomId">房间号。</param>
    /// <returns>查询对象。</returns>
    public static RoomQuery FromRoomId(PlatformId platform, string roomId) => new()
    {
        Platform = platform,
        RoomId = roomId,
    };

    /// <summary>创建一个仅含链接的查询。</summary>
    /// <param name="platform">平台标识。</param>
    /// <param name="roomUrl">直播间链接。</param>
    /// <returns>查询对象。</returns>
    public static RoomQuery FromUrl(PlatformId platform, string roomUrl) => new()
    {
        Platform = platform,
        RoomUrl = roomUrl,
    };
}
