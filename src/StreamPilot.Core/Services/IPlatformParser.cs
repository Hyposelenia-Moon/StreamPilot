namespace StreamPilot.Core.Services;

using StreamPilot.Core.Models;

/// <summary>
/// 平台解析器工厂：把平台标识映射为解析器实例。
/// </summary>
/// <remarks>
/// 由 <c>StreamPilot.Parsers</c> 提供实现，UI 层只依赖本接口。
/// </remarks>
public interface IPlatformParserFactory
{
    /// <summary>判断指定平台是否已实现解析器。</summary>
    /// <param name="platform">平台标识。</param>
    /// <returns>已实现返回 <see langword="true"/>。</returns>
    bool IsSupported(PlatformId platform);

    /// <summary>列出所有已实现的平台。</summary>
    /// <returns>平台列表（按 <see cref="PlatformId"/> 数值升序）。</returns>
    IReadOnlyList<PlatformId> SupportedPlatforms { get; }

    /// <summary>取得指定平台的解析器。</summary>
    /// <param name="platform">平台标识。</param>
    /// <returns>解析器实例。</returns>
    /// <exception cref="Errors.ResolveException">平台不受支持时抛出。</exception>
    IPlatformParser GetParser(PlatformId platform);
}

/// <summary>
/// 单一平台的解析器契约。所有平台实现必须实现本接口。
/// </summary>
public interface IPlatformParser
{
    /// <summary>本解析器负责的平台。</summary>
    PlatformId Platform { get; }

    /// <summary>平台的展示名（中文）。</summary>
    string DisplayName { get; }

    /// <summary>平台直播间链接前缀，例如 <c>https://live.bilibili.com/</c>。</summary>
    string RoomUrlPrefix { get; }

    /// <summary>
    /// 执行解析。实现方在失败时必须抛出 <see cref="Errors.ResolveException"/> 并给出准确的失败分类。
    /// </summary>
    /// <param name="query">房间查询条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>房间信息与候选流。</returns>
    /// <exception cref="Errors.ResolveException">解析失败时抛出。</exception>
    Task<ResolvedRoom> ParseAsync(RoomQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// 平台解析器的可选能力：批量查询多个房间的开播状态。
/// </summary>
/// <remarks>
/// 只有提供批量状态接口的平台（如 B站 <c>getRoomBaseInfo?room_ids=a,b,c</c>）实现本接口；
/// 其余平台的开播检测回退到逐房间完整解析。查询失败时返回带错误原因的 <see cref="RoomStatus"/>，
/// 不抛异常（单个房间失败不应拖垮整批）。
/// </remarks>
public interface IBatchStatusParser
{
    /// <summary>
    /// 批量查询多个数字房间号的开播状态。
    /// </summary>
    /// <param name="roomIds">数字房间号列表（去重、去空后传入）。</param>
    /// <param name="cookie">平台 Cookie，可为 <see langword="null"/>（匿名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>状态快照；与 <paramref name="roomIds"/> 对应，单个房间失败时返回错误结果。</returns>
    Task<IReadOnlyList<RoomStatus>> QueryRoomStatusesAsync(
        IReadOnlyList<string> roomIds,
        string? cookie,
        CancellationToken cancellationToken);
}
