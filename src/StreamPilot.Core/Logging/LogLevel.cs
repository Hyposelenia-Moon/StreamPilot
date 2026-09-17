namespace StreamPilot.Core.Logging;

/// <summary>
/// 日志级别。级别语义见 CLAUDE.md：Trace/Debug 供开发，Info 记录关键流程，
/// Warn 表示可恢复异常，Error 表示需要人工介入。
/// </summary>
public enum LogLevel
{
    /// <summary>开发期调试细节。</summary>
    Trace = 0,

    /// <summary>开发期诊断信息。</summary>
    Debug = 1,

    /// <summary>关键流程节点。</summary>
    Info = 2,

    /// <summary>可恢复的异常情况。</summary>
    Warn = 3,

    /// <summary>需要人工介入的错误。</summary>
    Error = 4,
}
