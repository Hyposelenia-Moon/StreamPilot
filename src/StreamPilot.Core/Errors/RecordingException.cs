namespace StreamPilot.Core.Errors;

/// <summary>
/// 录制过程中的可分类错误。
/// </summary>
public sealed class RecordingException : Exception
{
    /// <summary>初始化异常。</summary>
    /// <param name="category">错误分类。</param>
    /// <param name="detail">面向用户的失败描述。</param>
    /// <param name="innerException">原始异常，可为 <see langword="null"/>。</param>
    public RecordingException(RecordingErrorCategory category, string detail, Exception? innerException = null)
        : base(detail, innerException)
    {
        Category = category;
    }

    /// <summary>错误分类。</summary>
    public RecordingErrorCategory Category { get; }

    /// <summary>生成结构化日志字段。</summary>
    /// <returns>字段名与值的键值对。</returns>
    public IReadOnlyDictionary<string, object?> ToLogFields() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["category"] = Category.ToString(),
        ["detail"] = Message,
    };
}

/// <summary>
/// 录制错误分类。
/// </summary>
public enum RecordingErrorCategory
{
    /// <summary>未分类错误。</summary>
    Unknown = 0,

    /// <summary>候选流不支持录制（例如 RTMP 或 fMP4）。</summary>
    UnsupportedFormat = 1,

    /// <summary>输出目录不可写或磁盘空间不足。</summary>
    OutputUnavailable = 2,

    /// <summary>上游流返回了非法数据（容器头损坏）。</summary>
    MalformedStream = 3,

    /// <summary>重连次数或时长达到上限。</summary>
    RetryExhausted = 4,

    /// <summary>用户或上层取消了录制。</summary>
    Cancelled = 5,
}
