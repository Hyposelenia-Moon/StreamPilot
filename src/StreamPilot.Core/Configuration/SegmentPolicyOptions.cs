namespace StreamPilot.Core.Configuration;

/// <summary>
/// 分片策略配置。
/// </summary>
/// <remarks>
/// 分片大小与分片时长都**没有上限**：用户可以把上限设成任意正数，
/// 归一化只把"非法值"（0、负数、过小到无法切分的值）回退到默认值，不再做上限夹取。
/// </remarks>
public sealed record SegmentPolicyOptions
{
    /// <summary>单个分片的字节上限（默认 10 GiB）。</summary>
    public long MaxBytes { get; init; } = RecordingLimits.DefaultSegmentMaxBytes;

    /// <summary>单个分片的时长上限（分钟，默认 480 分钟即 8 小时）。</summary>
    public int MaxDurationMinutes { get; init; } = RecordingLimits.DefaultSegmentMaxDurationMinutes;

    /// <summary>分片最小字节数，避免刚开播就切出大量小文件（默认 8 MiB）。</summary>
    public long MinBytes { get; init; } = RecordingLimits.DefaultSegmentMinBytes;

    /// <summary>仅在视频关键帧处切分（FLV 必须为 true）。</summary>
    public bool SplitOnKeyFrameOnly { get; init; } = true;

    /// <summary>
    /// 归一化并校验取值；非法值回退到默认值。
    /// </summary>
    /// <returns>合法的分片策略。</returns>
    /// <remarks>
    /// 只做"下限/合法性"处理：小于最小可切分字节数的上限按非法回退默认值，
    /// 更大的值一律原样保留（不设上限），因此用户可以按需把分片调到很大。
    /// </remarks>
    public SegmentPolicyOptions Normalize()
    {
        long maxBytes = MaxBytes < RecordingLimits.MinSegmentBytes ? RecordingLimits.DefaultSegmentMaxBytes : MaxBytes;
        int maxMinutes = MaxDurationMinutes < RecordingLimits.MinSegmentMinutes
            ? RecordingLimits.DefaultSegmentMaxDurationMinutes
            : MaxDurationMinutes;
        long minBytes = Math.Clamp(MinBytes, 0, maxBytes / 2);

        return this with
        {
            MaxBytes = maxBytes,
            MaxDurationMinutes = maxMinutes,
            MinBytes = minBytes,
        };
    }
}

/// <summary>
/// 录制相关配置的默认值与取值边界（禁止魔法数字）。
/// </summary>
/// <remarks>
/// 这里集中定义默认值与单位换算系数，供 <see cref="RecordingOptions"/>、
/// <see cref="SegmentPolicyOptions"/> 与设置界面共用；两个默认值必须与
/// <c>StreamPilot.Recording.FlvStreamRecorder.DefaultMaxDurationMinutes</c> 保持一致。
/// </remarks>
public static class RecordingLimits
{
    /// <summary>每 GiB 的字节数（1024 的 3 次方）。</summary>
    public const long BytesPerGibibyte = 1024L * 1024 * 1024;

    /// <summary>每 MiB 的字节数。</summary>
    public const long BytesPerMebibyte = 1024L * 1024;

    /// <summary>每小时的分片数（分钟）。</summary>
    public const int MinutesPerHour = 60;

    /// <summary>每分钟的毫秒数。</summary>
    public const int MillisecondsPerMinute = 60_000;

    /// <summary>默认分片字节上限：10 GiB。</summary>
    public const long DefaultSegmentMaxBytes = 10L * BytesPerGibibyte;

    /// <summary>分片自动切分的最小可切分字节数：64 MiB。</summary>
    public const long MinSegmentBytes = 64L * BytesPerMebibyte;

    /// <summary>默认分片时长上限：480 分钟（8 小时）。</summary>
    public const int DefaultSegmentMaxDurationMinutes = 480;

    /// <summary>分片时长下限：1 分钟。</summary>
    public const int MinSegmentMinutes = 1;

    /// <summary>默认分片最小字节数：8 MiB。</summary>
    public const long DefaultSegmentMinBytes = 8L * BytesPerMebibyte;

    /// <summary>默认最长录制时长：480 分钟（8 小时）。</summary>
    public const int DefaultMaxRecordingMinutes = 480;

    /// <summary>最长录制时长的下限：1 分钟。</summary>
    public const int MinRecordingMinutes = 1;
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

    /// <summary>
    /// 播放列表改写产生的子注册的"试用期"（秒）：从未被真正请求过的子地址在该时限后淘汰。
    /// </summary>
    /// <remarks>
    /// 播放页探测 HLS 候选时会拉一次播放列表（拿到响应头就 abort），改写过程会为每个切片登记子注册，
    /// 但探测不会再请求它们。若按普通空闲阈值（10 分钟）淘汰，一次起播就能堆几十条死注册；
    /// 首个真实请求会把注册"转正"，之后按普通规则续命。
    /// </remarks>
    public const int ProvisionalRelaySeconds = 90;

    /// <summary>
    /// 同时存在的中继注册上限。
    /// </summary>
    /// <remarks>
    /// HLS 每次刷新播放列表都会重新登记切片（保留最近两代），8 条候选 × 两条列表 × 每列表若干切片
    /// 已经接近 100 条；旧上限 64 会在起播后不久触发淘汰，把仍在播放的注册挤掉（表现为播放中 404）。
    /// </remarks>
    public const int MaxRelayRegistrations = 256;
}
