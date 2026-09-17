namespace StreamPilot.Core.Errors;

using StreamPilot.Core.Models;

/// <summary>
/// 平台解析失败的统一异常，携带互斥的失败分类与诊断上下文。
/// </summary>
public sealed class ResolveException : Exception
{
    /// <summary>初始化异常。</summary>
    /// <param name="failure">失败分类。</param>
    /// <param name="platform">平台标识。</param>
    /// <param name="operation">操作名（例如 <c>getRoomPlayInfo</c>）。</param>
    /// <param name="detail">面向用户的失败描述（已脱敏）。</param>
    /// <param name="innerException">原始异常，可为 <see langword="null"/>。</param>
    public ResolveException(
        ResolveFailure failure,
        PlatformId platform,
        string operation,
        string detail,
        Exception? innerException = null)
        : base(detail, innerException)
    {
        Failure = failure;
        Platform = platform;
        Operation = string.IsNullOrWhiteSpace(operation) ? "unknown" : operation;
    }

    /// <summary>失败分类。</summary>
    public ResolveFailure Failure { get; }

    /// <summary>平台标识。</summary>
    public PlatformId Platform { get; }

    /// <summary>失败发生的操作名。</summary>
    public string Operation { get; }

    /// <summary>生成结构化日志字段（不含敏感信息）。</summary>
    /// <returns>字段名与值的键值对。</returns>
    public IReadOnlyDictionary<string, object?> ToLogFields() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["failure"] = Failure.ToString(),
        ["platform"] = Platform.ToString(),
        ["operation"] = Operation,
        ["detail"] = Message,
    };
}
