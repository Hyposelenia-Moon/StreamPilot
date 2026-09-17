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
