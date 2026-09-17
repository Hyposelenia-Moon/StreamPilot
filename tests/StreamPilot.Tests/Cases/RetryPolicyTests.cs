namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Http;
using StreamPilot.Core.Models;
using StreamPilot.Tests.Framework;

/// <summary>
/// <see cref="RetryPolicy"/> 的单元测试。
/// </summary>
[TestClass]
public sealed class RetryPolicyTests
{
    /// <summary>退避序列与抖动范围。</summary>
    [TestMethod("重试退避：300/900/2000 毫秒与抖动上下界")]
    public void BackoffSequence()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(300), RetryPolicy.GetDelay(1, 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(900), RetryPolicy.GetDelay(2, 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), RetryPolicy.GetDelay(3, 0.5));

        // jitterSource = 0 → 系数 0.8；jitterSource = 1 → 系数 1.2
        Assert.Equal(240, (int)RetryPolicy.GetDelay(1, 0.0).TotalMilliseconds);
        Assert.Equal(360, (int)RetryPolicy.GetDelay(1, 1.0).TotalMilliseconds);
    }

    /// <summary>超出序列长度不再重试。</summary>
    [TestMethod("重试退避：越界返回零延迟")]
    public void BackoffOutOfRange()
    {
        Assert.Equal(TimeSpan.Zero, RetryPolicy.GetDelay(0));
        Assert.Equal(TimeSpan.Zero, RetryPolicy.GetDelay(4));
    }

    /// <summary>可重试状态码集合。</summary>
    [TestMethod("可重试状态码：408/409/425/429/5xx")]
    public void RetryableStatusCodes()
    {
        Assert.True(RetryPolicy.IsRetryableStatusCode(408));
        Assert.True(RetryPolicy.IsRetryableStatusCode(409));
        Assert.True(RetryPolicy.IsRetryableStatusCode(425));
        Assert.True(RetryPolicy.IsRetryableStatusCode(429));
        Assert.True(RetryPolicy.IsRetryableStatusCode(500));
        Assert.True(RetryPolicy.IsRetryableStatusCode(503));
        Assert.False(RetryPolicy.IsRetryableStatusCode(400));
        Assert.False(RetryPolicy.IsRetryableStatusCode(403));
        Assert.False(RetryPolicy.IsRetryableStatusCode(404));
        Assert.False(RetryPolicy.IsRetryableStatusCode(200));
    }

    /// <summary>可重试异常类型。</summary>
    [TestMethod("可重试异常：超时与网络异常")]
    public void RetryableExceptions()
    {
        Assert.True(RetryPolicy.IsRetryableException(new TimeoutException()));
        Assert.True(RetryPolicy.IsRetryableException(new TaskCanceledException()));
        Assert.True(RetryPolicy.IsRetryableException(new HttpRequestException("boom")));
        Assert.True(RetryPolicy.IsRetryableException(new IOException("boom")));
        Assert.False(RetryPolicy.IsRetryableException(new InvalidOperationException("boom")));
        Assert.False(RetryPolicy.IsRetryableException(null));
    }

    /// <summary>尝试次数上限判定（含边界）。</summary>
    [TestMethod("重试次数上限：默认 3 次含首次")]
    public void AttemptLimits()
    {
        Assert.Equal(3, RetryPolicy.DefaultMaxAttempts);
        Assert.True(RetryPolicy.CanRetry(1, 3));
        Assert.True(RetryPolicy.CanRetry(2, 3));
        Assert.False(RetryPolicy.CanRetry(3, 3));
        Assert.False(RetryPolicy.CanRetry(4, 3));
    }

    /// <summary>状态码到失败分类的映射。</summary>
    [TestMethod("状态码映射失败分类")]
    public void FailureMapping()
    {
        Assert.Equal(ResolveFailure.Rejected, RetryPolicy.ToFailure(403));
        Assert.Equal(ResolveFailure.Rejected, RetryPolicy.ToFailure(429));
        Assert.Equal(ResolveFailure.RoomNotFound, RetryPolicy.ToFailure(404));
        Assert.Equal(ResolveFailure.RoomNotFound, RetryPolicy.ToFailure(410));
        Assert.Equal(ResolveFailure.NetworkError, RetryPolicy.ToFailure(502));
        Assert.Equal(ResolveFailure.ParseError, RetryPolicy.ToFailure(400));
    }
}
