namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Parsers;
using StreamPilot.Tests.Framework;

/// <summary>
/// 地址时效读取测试：抖音/Bigo 等平台把时效写在签名地址里，没有单独的过期字段。
/// </summary>
/// <remarks>
/// 读不到就等于"永不销毁"，失效地址会被当作可用候选下发；
/// 读错（把签名里的其它数字当成时间）更糟，会误删仍可用的线路，所以这里同时覆盖取值边界。
/// </remarks>
[TestClass]
public sealed class UrlExpiryTests
{
    /// <summary>2030-01-01T00:00:00Z 的 Unix 秒。</summary>
    private const long ExpireSeconds = 1893456000;

    /// <summary>秒级时间戳必须被识别。</summary>
    [TestMethod("地址时效：读取秒级 expire")]
    public void ReadsSeconds()
    {
        DateTimeOffset? expiresAt = UrlExpiry.TryRead("https://cdn.example/live.flv?expire=1893456000&sig=abc");

        Assert.NotNull(expiresAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(ExpireSeconds), expiresAt!.Value);
    }

    /// <summary>毫秒级时间戳与多种参数名都要兼容。</summary>
    [TestMethod("地址时效：兼容毫秒与多种参数名")]
    public void ReadsMillisecondsAndAliases()
    {
        DateTimeOffset expected = DateTimeOffset.FromUnixTimeSeconds(ExpireSeconds);

        foreach (string query in new[]
                 {
                     "?expires=1893456000000",
                     "?expires_at=1893456000",
                     "?expire_at=1893456000",
                     "?deadline=1893456000",
                 })
        {
            DateTimeOffset? expiresAt = UrlExpiry.TryRead("https://cdn.example/live.m3u8" + query);
            Assert.NotNull(expiresAt, "必须识别 " + query);
            Assert.Equal(expected, expiresAt!.Value);
        }
    }

    /// <summary>没有时效参数时按"平台未声明"处理，不能误判成已过期。</summary>
    [TestMethod("地址时效：无参数或无法解析时返回空")]
    public void ReturnsNullWhenAbsent()
    {
        Assert.Null(UrlExpiry.TryRead("https://cdn.example/live.flv"));
        Assert.Null(UrlExpiry.TryRead("https://cdn.example/live.flv?token=abc"));
        Assert.Null(UrlExpiry.TryRead("https://cdn.example/live.flv?expire="));
        Assert.Null(UrlExpiry.TryRead("https://cdn.example/live.flv?expire=not-a-number"));
        Assert.Null(UrlExpiry.TryRead(null));
        Assert.Null(UrlExpiry.TryRead(""));
    }

    /// <summary>明显不合理的取值必须被丢弃，避免把仍然有效的线路判成过期。</summary>
    [TestMethod("地址时效：超出合理区间的时间戳被丢弃")]
    public void RejectsImplausibleTimestamps()
    {
        // 13 位按毫秒解释会落到公元 33658 年；12 位以下按秒解释会落到 1970 年。
        Assert.Null(UrlExpiry.TryRead("https://cdn.example/live.flv?expire=999999999999999"));
        Assert.Null(UrlExpiry.TryRead("https://cdn.example/live.flv?expire=1000"));
        Assert.Null(UrlExpiry.TryRead("https://cdn.example/live.flv?expire=99999999999999999999"));
    }
}
