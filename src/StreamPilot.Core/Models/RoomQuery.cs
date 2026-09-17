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

    /// <summary>B站 Cookie（可选，仅 B站 使用；为空时走匿名解析）。</summary>
    public string? BilibiliCookie { get; init; }

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
