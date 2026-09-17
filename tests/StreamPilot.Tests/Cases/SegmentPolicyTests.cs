namespace StreamPilot.Tests.Cases;

using StreamPilot.Recording;
using StreamPilot.Tests.Framework;

/// <summary>
/// <see cref="SegmentPolicy"/> 的分片决策测试。
/// </summary>
[TestClass]
public sealed class SegmentPolicyTests
{
    /// <summary>按大小触发切分（含边界）。</summary>
    [TestMethod("分片策略：按字节上限触发")]
    public void SplitsByBytes()
    {
        SegmentPolicy policy = new(new Core.Configuration.SegmentPolicyOptions
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
        SegmentPolicy policy = new(new Core.Configuration.SegmentPolicyOptions
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

    /// <summary>非法配置被归一化到合法区间。</summary>
    [TestMethod("分片策略：非法配置归一化")]
    public void NormalizesInvalidOptions()
    {
        Core.Configuration.SegmentPolicyOptions normalized = new Core.Configuration.SegmentPolicyOptions
        {
            MaxBytes = 1,
            MaxDurationMinutes = 0,
            MinBytes = -5,
        }.Normalize();

        Assert.Equal(Core.Configuration.SegmentPolicyOptions.Limits.DefaultMaxBytes, normalized.MaxBytes);
        Assert.Equal(Core.Configuration.SegmentPolicyOptions.Limits.DefaultMaxDurationMinutes, normalized.MaxDurationMinutes);
        Assert.Equal(0, normalized.MinBytes);

        Core.Configuration.SegmentPolicyOptions tooLarge = new Core.Configuration.SegmentPolicyOptions
        {
            MaxBytes = long.MaxValue,
            MaxDurationMinutes = int.MaxValue,
        }.Normalize();

        Assert.Equal(Core.Configuration.SegmentPolicyOptions.Limits.MaxSegmentBytes, tooLarge.MaxBytes);
        Assert.Equal(Core.Configuration.SegmentPolicyOptions.Limits.MaxSegmentMinutes, tooLarge.MaxDurationMinutes);
    }

    /// <summary>最小分片字节数判定。</summary>
    [TestMethod("分片策略：最小字节数判定")]
    public void DetectsBelowMinimum()
    {
        SegmentPolicy policy = new(new Core.Configuration.SegmentPolicyOptions { MinBytes = 1024 });
        Assert.True(policy.IsBelowMinimum(1023));
        Assert.False(policy.IsBelowMinimum(1024));
    }
}
