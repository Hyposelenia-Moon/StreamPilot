namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Configuration;
using StreamPilot.Core.Models;
using StreamPilot.Recording;
using StreamPilot.Recording.Flv;
using StreamPilot.Tests.Framework;

/// <summary>
/// <see cref="SegmentPolicy"/> 的分片决策测试，以及录制默认值与"无上限"行为的测试。
/// </summary>
[TestClass]
public sealed class SegmentPolicyTests
{
    /// <summary>按大小触发切分（含边界）。</summary>
    [TestMethod("分片策略：按字节上限触发")]
    public void SplitsByBytes()
    {
        SegmentPolicy policy = new(new SegmentPolicyOptions
        {
            MaxBytes = 100 * 1024 * 1024,
            MaxDurationMinutes = 30,
            MinBytes = 0,
        });
        policy.BeginSegment(0);

        Assert.False(policy.ShouldSplit(99 * 1024 * 1024, 1000));
        Assert.True(policy.ShouldSplit(100 * 1024 * 1024, 1000));
        Assert.True(policy.ShouldSplit(200 * 1024 * 1024, 1000));
    }

    /// <summary>按时长触发切分（含边界）。</summary>
    [TestMethod("分片策略：按分片时长触发")]
    public void SplitsByDuration()
    {
        SegmentPolicy policy = new(new SegmentPolicyOptions
        {
            MaxBytes = long.MaxValue / 2,
            MaxDurationMinutes = 1,
            MinBytes = 0,
        });
        policy.BeginSegment(5_000);

        Assert.False(policy.ShouldSplit(1, 5_000 + 59_999));
        Assert.True(policy.ShouldSplit(1, 5_000 + 60_000));
        Assert.True(policy.ShouldSplit(1, 5_000 + 120_000));
    }

    /// <summary>未开始分片前不切分；时间戳回退不产生负时长。</summary>
    [TestMethod("分片策略：未开始与时间戳回退")]
    public void HandlesNotStartedAndBackwardsTimestamps()
    {
        SegmentPolicy policy = new();
        Assert.False(policy.ShouldSplit(1024, 0));

        policy.BeginSegment(10_000);
        Assert.False(policy.ShouldSplit(1024, 9_000));
        Assert.EqualDouble(0, policy.GetSegmentDurationSeconds(9_000));
    }

    /// <summary>非法配置被归一化到默认值（0、负数、小到无法切分的值）。</summary>
    [TestMethod("分片策略：非法配置归一化")]
    public void NormalizesInvalidOptions()
    {
        SegmentPolicyOptions normalized = new SegmentPolicyOptions
        {
            MaxBytes = 1,
            MaxDurationMinutes = 0,
            MinBytes = -5,
        }.Normalize();

        Assert.Equal(RecordingLimits.DefaultSegmentMaxBytes, normalized.MaxBytes);
        Assert.Equal(RecordingLimits.DefaultSegmentMaxDurationMinutes, normalized.MaxDurationMinutes);
        Assert.Equal(0, normalized.MinBytes);
    }

    /// <summary>分片上限没有最大值：很大的取值被原样保留，不再被夹取。</summary>
    [TestMethod("分片策略：超大取值不被夹取（无上限）")]
    public void KeepsLargeValuesWithoutUpperBound()
    {
        long hugeBytes = 512L * RecordingLimits.BytesPerGibibyte;
        int hugeMinutes = 200_000;
        SegmentPolicyOptions normalized = new SegmentPolicyOptions
        {
            MaxBytes = hugeBytes,
            MaxDurationMinutes = hugeMinutes,
        }.Normalize();

        Assert.Equal(hugeBytes, normalized.MaxBytes, "分片字节上限必须原样保留");
        Assert.Equal(hugeMinutes, normalized.MaxDurationMinutes, "分片时长上限必须原样保留");
        Assert.Equal(long.MaxValue, new SegmentPolicyOptions { MaxBytes = long.MaxValue }.Normalize().MaxBytes);
        Assert.Equal(int.MaxValue, new SegmentPolicyOptions { MaxDurationMinutes = int.MaxValue }.Normalize().MaxDurationMinutes);
    }

    /// <summary>默认分片上限为 10 GiB / 480 分钟，且默认策略生效于决策器。</summary>
    [TestMethod("分片策略：默认值 10 GiB 与 8 小时")]
    public void UsesNewDefaults()
    {
        SegmentPolicyOptions defaults = new();

        Assert.Equal(10L * RecordingLimits.BytesPerGibibyte, defaults.MaxBytes);
        Assert.Equal(10_737_418_240L, defaults.MaxBytes);
        Assert.Equal(480, defaults.MaxDurationMinutes);
        Assert.Equal(8 * RecordingLimits.MinutesPerHour, defaults.MaxDurationMinutes);
        Assert.Equal(8L * RecordingLimits.BytesPerMebibyte, defaults.MinBytes);

        SegmentPolicy policy = new();
        Assert.Equal(defaults.MaxBytes, policy.Options.MaxBytes);
        Assert.Equal(defaults.MaxDurationMinutes, policy.Options.MaxDurationMinutes);
        policy.BeginSegment(0);
        Assert.False(policy.ShouldSplit(defaults.MaxBytes - 1, 0), "未达到 10 GiB 不切分");
        Assert.True(policy.ShouldSplit(defaults.MaxBytes, 0), "达到 10 GiB 即切分");

        policy.BeginSegment(0);
        Assert.False(policy.ShouldSplit(1, 480L * 60_000L - 1), "未满 8 小时不切分");
        Assert.True(policy.ShouldSplit(1, 480L * 60_000L), "满 8 小时即切分");
    }

    /// <summary>最长录制时长默认 8 小时，且与录制器常量一致。</summary>
    [TestMethod("录制设置：最长录制时长默认 8 小时")]
    public void UsesNewMaxRecordingDurationDefault()
    {
        RecordingOptions options = new();

        Assert.Equal(480, options.MaxDurationMinutes);
        Assert.Equal(8 * RecordingLimits.MinutesPerHour, options.MaxDurationMinutes);
        Assert.Equal(RecordingLimits.DefaultMaxRecordingMinutes, RecordingLimits.DefaultMaxRecordingMinutes);
    }

    /// <summary>最小分片字节数判定。</summary>
    [TestMethod("分片策略：最小字节数判定")]
    public void DetectsBelowMinimum()
    {
        SegmentPolicy policy = new(new SegmentPolicyOptions { MinBytes = 1024 });
        Assert.True(policy.IsBelowMinimum(1023));
        Assert.False(policy.IsBelowMinimum(1024));
    }
}
