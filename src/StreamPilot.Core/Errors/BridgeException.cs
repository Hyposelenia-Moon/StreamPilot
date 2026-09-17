namespace StreamPilot.Core.Errors;

/// <summary>
/// 桥接服务错误（端口占用、监听失败、协议非法等）。
/// </summary>
public sealed class BridgeException : Exception
{
    /// <summary>初始化异常。</summary>
    /// <param name="operation">操作名（例如 <c>start-listener</c>）。</param>
    /// <param name="detail">失败描述。</param>
    /// <param name="innerException">原始异常，可为 <see langword="null"/>。</param>
    public BridgeException(string operation, string detail, Exception? innerException = null)
        : base(detail, innerException)
    {
        Operation = operation;
    }

    /// <summary>操作名。</summary>
    public string Operation { get; }
}
