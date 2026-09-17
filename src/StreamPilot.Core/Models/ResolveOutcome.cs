namespace StreamPilot.Core.Models;

/// <summary>
/// 解析结果：成功携带 <see cref="Room"/>，失败携带 <see cref="Failure"/> 与 <see cref="Message"/>。
/// </summary>
/// <remarks>
/// 采用"结果对象"而非抛异常，便于 UI 直接展示失败原因，也便于批量解析时聚合结果。
/// 解析层内部仍可能抛 <see cref="Errors.ResolveException"/>，由 <c>IRoomResolver</c> 统一转换为本对象。
/// </remarks>
public sealed record ResolveOutcome
{
    /// <summary>是否成功。</summary>
    public required bool Success { get; init; }

    /// <summary>成功时的房间信息。</summary>
    public ResolvedRoom? Room { get; init; }

    /// <summary>失败分类。</summary>
    public ResolveFailure Failure { get; init; } = ResolveFailure.Unknown;

    /// <summary>面向用户的失败描述（中文，已脱敏）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>失败发生的操作名（例如 <c>getRoomPlayInfo</c>），用于诊断。</summary>
    public string? Operation { get; init; }

    /// <summary>构造一个成功结果。</summary>
    /// <param name="room">房间信息。</param>
    /// <returns>结果对象。</returns>
    public static ResolveOutcome FromSuccess(ResolvedRoom room) => new()
    {
        Success = true,
        Room = room,
    };

    /// <summary>构造一个失败结果。</summary>
    /// <param name="failure">失败分类。</param>
    /// <param name="message">面向用户的描述。</param>
    /// <param name="operation">操作名。</param>
    /// <returns>结果对象。</returns>
    public static ResolveOutcome FromFailure(ResolveFailure failure, string message, string? operation = null) => new()
    {
        Success = false,
        Failure = failure,
        Message = message,
        Operation = operation,
    };
}
