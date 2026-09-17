namespace StreamPilot.Core.Services;

using StreamPilot.Core.Models;

/// <summary>
/// 解析房间并产出候选流的统一入口。UI 层只依赖本接口，不得直接依赖解析层实现类型。
/// </summary>
public interface IRoomResolver
{
    /// <summary>
    /// 解析指定平台的房间，返回统一的候选流结果。
    /// </summary>
    /// <param name="query">房间查询条件（房间号或直播间链接）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析结果；失败时返回带失败分类的结果对象，不抛异常。</returns>
    Task<ResolveOutcome> ResolveAsync(RoomQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// 清空解析缓存，强制下一次解析重新请求平台。
    /// </summary>
    void InvalidateCache();
}

/// <summary>
/// 播放用例的编排入口。负责把解析结果转换为 Web 播放页可消费的候选列表。
/// </summary>
public interface IPlaybackCoordinator
{
    /// <summary>
    /// 为一次播放准备候选（校验有效期、按画质排序、必要时注册桥接中继）。
    /// </summary>
    /// <param name="request">播放请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>播放计划；无可用候选时 <see cref="PlaybackPlan.Candidates"/> 为空。</returns>
    Task<PlaybackPlan> PrepareAsync(PlaybackRequest request, CancellationToken cancellationToken);

    /// <summary>当前活动的播放会话标识；无会话时为 0。</summary>
    int ActiveSessionId { get; }

    /// <summary>
    /// 释放"上一轮"的中继注册。
    /// </summary>
    /// <remarks>
    /// 准备新会话时**不能**立刻释放旧中继：切画质/切线路时页面仍在播旧地址，
    /// 立刻释放会让正在播放的画面直接黑屏。改由宿主在收到"播放已开始"后调用本方法回收。
    /// </remarks>
    void ReleasePreviousRelays();

    /// <summary>停止当前播放会话（同时释放新旧两轮中继）。</summary>
    void StopActive();
}

/// <summary>
/// 录制用例的编排入口。
/// </summary>
public interface IRecordingCoordinator
{
    /// <summary>
    /// 开始录制；同一 <c>(平台, 房间号)</c> 已存在录制会话时抛出 <see cref="Errors.RecordingException"/>。
    /// </summary>
    /// <param name="request">录制请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>会话句柄，可用于查询状态与停止。</returns>
    Task<IRecordingSession> StartAsync(RecordingRequest request, CancellationToken cancellationToken);

    /// <summary>列出所有进行中的录制会话。</summary>
    /// <returns>状态快照列表。</returns>
    IReadOnlyList<RecordingStatus> ListActive();

    /// <summary>停止指定房间的录制。</summary>
    /// <param name="platform">平台标识。</param>
    /// <param name="roomId">房间号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>停止成功返回 <see langword="true"/>；无该会话返回 <see langword="false"/>。</returns>
    Task<bool> StopAsync(PlatformId platform, string roomId, CancellationToken cancellationToken);
}

/// <summary>
/// 一个进行中的录制会话。
/// </summary>
public interface IRecordingSession : IAsyncDisposable
{
    /// <summary>会话标识。</summary>
    Guid SessionId { get; }

    /// <summary>当前状态快照。</summary>
    RecordingStatus Status { get; }

    /// <summary>会话结束原因；未结束时为 <see cref="RecordingStopReason.None"/>。</summary>
    RecordingStopReason StopReason { get; }

    /// <summary>请求停止录制并等待落盘收尾。</summary>
    /// <param name="reason">停止原因。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成收尾的任务。</returns>
    Task StopAsync(RecordingStopReason reason, CancellationToken cancellationToken);
}

/// <summary>
/// 播放页与宿主之间的桥接服务（mpv 外挂、本地流中继）。
/// </summary>
public interface IPlaybackBridge
{
    /// <summary>桥接服务是否正在监听。</summary>
    bool IsRunning { get; }

    /// <summary>实际监听的端口；未启动时为 0。</summary>
    int Port { get; }

    /// <summary>本地回环基地址（形如 <c>http://127.0.0.1:5566</c>）；未启动时为空字符串。</summary>
    string BaseAddress { get; }

    /// <summary>注册一个待中继的上游流地址，返回本地可播放地址。</summary>
    /// <param name="target">中继目标。</param>
    /// <returns>本地中继地址。</returns>
    string RegisterRelay(RelayTarget target);

    /// <summary>释放指定的中继注册。</summary>
    /// <param name="localUrl">注册时返回的本地地址。</param>
    void ReleaseRelay(string localUrl);

    /// <summary>请求用 mpv 播放指定地址。</summary>
    /// <param name="url">直播流地址。</param>
    /// <param name="title">窗口标题附加信息。</param>
    /// <param name="referer">可选 Referer。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功启动返回 <see langword="true"/>。</returns>
    Task<bool> PlayWithMpvAsync(string url, string? title, string? referer, CancellationToken cancellationToken);
}

/// <summary>
/// 桥接中继目标描述。
/// </summary>
public sealed record RelayTarget
{
    /// <summary>上游流地址（含签名，禁止写日志）。</summary>
    public required string UpstreamUrl { get; init; }

    /// <summary>请求上游时注入的 Referer，可为 <see langword="null"/>。</summary>
    public string? Referer { get; init; }

    /// <summary>上游响应的内容类型（用于中继响应头），可为 <see langword="null"/>。</summary>
    public string? ContentType { get; init; }

    /// <summary>是否在客户端断开后立即取消上游请求。</summary>
    public bool CancelUpstreamOnDisconnect { get; init; } = true;

    /// <summary>中继类型：字节流或 HLS 播放列表。</summary>
    /// <remarks>
    /// 播放列表必须逐行改写（切片地址换成本地中继地址），否则页面仍会直连 CDN，
    /// 遇到 <c>http://</c> CDN 会被混合内容策略拦下。
    /// </remarks>
    public RelayKind Kind { get; init; } = RelayKind.Stream;
}

/// <summary>
/// 中继类型。
/// </summary>
public enum RelayKind
{
    /// <summary>普通字节流（FLV / TS 切片）。</summary>
    Stream,

    /// <summary>HLS 播放列表（m3u8），需要改写内部地址后再返回。</summary>
    HlsPlaylist,
}
