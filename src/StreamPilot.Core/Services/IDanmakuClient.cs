namespace StreamPilot.Core.Services;

using StreamPilot.Core.Models;

/// <summary>
/// 直播弹幕客户端契约。
/// </summary>
/// <remarks>
/// <para>
/// 弹幕是**只读**的实时消息流：本接口不提供发送能力。
/// 发送弹幕需要登录态与平台风控交互，触碰项目红线（<c>CLAUDE.md</c> 红线 3），因此永久不实现。
/// </para>
/// <para>
/// 实现必须保证：连接失败、协议解析失败、心跳超时等都**不影响视频播放**——
/// 弹幕是附加能力，任何异常只能降级为「弹幕不可用」。
/// </para>
/// </remarks>
public interface IDanmakuClient : IAsyncDisposable
{
    /// <summary>当前连接状态。</summary>
    DanmakuConnectionState State { get; }

    /// <summary>本次会话累计收到的消息数（含被过滤的类型）。</summary>
    long ReceivedCount { get; }

    /// <summary>批量消息事件（实现侧按固定间隔聚合后触发，避免逐条推送压垮 UI）。</summary>
    event EventHandler<DanmakuBatchEventArgs>? MessagesReceived;

    /// <summary>连接状态变化事件。</summary>
    event EventHandler<DanmakuConnectionState>? StateChanged;

    /// <summary>
    /// 连接到指定房间的弹幕服务器。
    /// </summary>
    /// <param name="request">连接参数（平台、房间号、Cookie 等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接并完成认证后返回的任务。</returns>
    /// <exception cref="DanmakuException">凭证获取失败、WebSocket 连接失败或认证被拒时抛出。</exception>
    Task ConnectAsync(DanmakuConnectRequest request, CancellationToken cancellationToken);

    /// <summary>主动断开连接并释放底层资源（幂等）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>断开完成的任务。</returns>
    Task DisconnectAsync(CancellationToken cancellationToken);
}

/// <summary>弹幕连接状态。</summary>
public enum DanmakuConnectionState
{
    /// <summary>未连接（初始态或已主动断开）。</summary>
    Disconnected = 0,

    /// <summary>正在获取凭证 / 建立 WebSocket / 等待认证回复。</summary>
    Connecting = 1,

    /// <summary>已连接并认证成功，正在接收消息。</summary>
    Connected = 2,

    /// <summary>连接失败（重试耗尽），需要用户手动重试。</summary>
    Failed = 3,
}

/// <summary>弹幕连接参数。</summary>
public sealed record DanmakuConnectRequest
{
    /// <summary>平台标识（当前仅哔哩哔哩支持）。</summary>
    public required PlatformId Platform { get; init; }

    /// <summary>房间号（平台真实房间号，非短号）。</summary>
    public required string RoomId { get; init; }

    /// <summary>该平台的 Cookie（可空；为空时使用匿名身份连接）。</summary>
    public string? Cookie { get; init; }
}

/// <summary>批量弹幕事件参数。</summary>
public sealed class DanmakuBatchEventArgs : EventArgs
{
    /// <summary>构造批量事件参数。</summary>
    /// <param name="messages">本批消息（按时间升序）。</param>
    public DanmakuBatchEventArgs(IReadOnlyList<DanmakuMessage> messages) => Messages = messages;

    /// <summary>本批消息（按时间升序，非空）。</summary>
    public IReadOnlyList<DanmakuMessage> Messages { get; }
}

/// <summary>弹幕子系统异常。</summary>
public sealed class DanmakuException : Exception
{
    /// <summary>构造异常。</summary>
    /// <param name="operation">失败的操作标识（用于日志定位）。</param>
    /// <param name="message">错误描述。</param>
    /// <param name="innerException">内部异常，可为 <see langword="null"/>。</param>
    public DanmakuException(string operation, string message, Exception? innerException = null)
        : base(message, innerException) => Operation = operation;

    /// <summary>失败的操作标识（如 <c>fetch-token</c> / <c>websocket-connect</c> / <c>auth</c>）。</summary>
    public string Operation { get; }
}
