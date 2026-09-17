namespace StreamPilot.Core.Configuration;

/// <summary>
/// 分片策略配置。
/// </summary>
public sealed record SegmentPolicyOptions
{
    /// <summary>单个分片的字节上限（默认 1 GiB）。</summary>
    public long MaxBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>单个分片的时长上限（分钟，默认 30 分钟）。</summary>
    public int MaxDurationMinutes { get; init; } = 30;

    /// <summary>分片最小字节数，避免刚开播就切出大量小文件（默认 8 MiB）。</summary>
    public long MinBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>仅在视频关键帧处切分（FLV 必须为 true）。</summary>
    public bool SplitOnKeyFrameOnly { get; init; } = true;

    /// <summary>
    /// 归一化并校验取值；非法值回退到默认值。
    /// </summary>
    /// <returns>合法的分片策略。</returns>
    public SegmentPolicyOptions Normalize()
    {
        long maxBytes = MaxBytes < Limits.MinSegmentBytes ? Limits.DefaultMaxBytes : Math.Min(MaxBytes, Limits.MaxSegmentBytes);
        int maxMinutes = MaxDurationMinutes < Limits.MinSegmentMinutes
            ? Limits.DefaultMaxDurationMinutes
            : Math.Min(MaxDurationMinutes, Limits.MaxSegmentMinutes);
        long minBytes = Math.Clamp(MinBytes, 0, maxBytes / 2);

        return this with
        {
            MaxBytes = maxBytes,
            MaxDurationMinutes = maxMinutes,
            MinBytes = minBytes,
        };
    }

    /// <summary>分片策略的取值边界（禁止魔法数字）。</summary>
    public static class Limits
    {
        /// <summary>默认分片字节上限：1 GiB。</summary>
        public const long DefaultMaxBytes = 1024L * 1024 * 1024;

        /// <summary>分片字节下限：64 MiB。</summary>
        public const long MinSegmentBytes = 64L * 1024 * 1024;

        /// <summary>分片字节上限：16 GiB。</summary>
        public const long MaxSegmentBytes = 16L * 1024 * 1024 * 1024;

        /// <summary>默认分片时长：30 分钟。</summary>
        public const int DefaultMaxDurationMinutes = 30;

        /// <summary>分片时长下限：1 分钟。</summary>
        public const int MinSegmentMinutes = 1;

        /// <summary>分片时长上限：480 分钟。</summary>
        public const int MaxSegmentMinutes = 480;
    }
}

/// <summary>
/// 桥接服务的常量。
/// </summary>
public static class BridgeConstants
{
    /// <summary>默认监听端口。</summary>
    public const int DefaultPort = 5566;

    /// <summary>端口探测的结束端口。</summary>
    public const int MaxPort = 5575;

    /// <summary>唯一允许绑定的主机地址（禁止改为 0.0.0.0 / + / *）。</summary>
    public const string LoopbackHost = "127.0.0.1";

    /// <summary>中继注册的有效期（分钟），超时未访问即释放。</summary>
    public const int RelayIdleMinutes = 10;

    /// <summary>同时存在的中继注册上限。</summary>
    public const int MaxRelayRegistrations = 64;
}
