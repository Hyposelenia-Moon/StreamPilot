namespace StreamPilot.Core.Models;

/// <summary>
/// 单条直播弹幕消息（统一各平台差异后的展示模型）。
/// </summary>
/// <remarks>
/// 本模型只承载<b>展示所需</b>的最小字段，不保留平台原始 JSON：
/// 原始响应可能含用户 ID、IP 归属地等隐私字段，调用方不需要也不应持有。
/// </remarks>
public sealed record DanmakuMessage
{
    /// <summary>消息在本次会话内的递增序号（用于渲染端去重与诊断）。</summary>
    public required long Sequence { get; init; }

    /// <summary>消息类型。</summary>
    public required DanmakuKind Kind { get; init; }

    /// <summary>发送者昵称；系统消息为空字符串。</summary>
    public string User { get; init; } = string.Empty;

    /// <summary>弹幕文本；礼物/互动类消息为已格式化的描述文本。</summary>
    public required string Text { get; init; }

    /// <summary>文本颜色（<c>#RRGGBB</c>）；平台未指定时为 <see langword="null"/>。</summary>
    public string? Color { get; init; }

    /// <summary>粉丝牌名称；无粉丝牌时为 <see langword="null"/>。</summary>
    public string? MedalName { get; init; }

    /// <summary>粉丝牌等级；无粉丝牌时为 0。</summary>
    public int MedalLevel { get; init; }

    /// <summary>用户等级；平台未提供时为 0。</summary>
    public int UserLevel { get; init; }

    /// <summary>是否具有特殊身份（房管、舰长、提督等）。</summary>
    public bool IsPrivileged { get; init; }

    /// <summary>消息产生时间（UTC）。</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>弹幕消息类型。</summary>
public enum DanmakuKind
{
    /// <summary>普通聊天弹幕。</summary>
    Chat = 0,

    /// <summary>礼物（含免费礼物）。</summary>
    Gift = 1,

    /// <summary>进场、关注、分享等互动。</summary>
    Interact = 2,

    /// <summary>系统消息（开播、分区变更、公告等）。</summary>
    System = 3,
}
