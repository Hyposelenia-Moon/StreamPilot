namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Logging;
using StreamPilot.Tests.Framework;

/// <summary>
/// <see cref="SensitiveData"/> 的脱敏行为测试（安全红线相关）。
/// </summary>
[TestClass]
public sealed class SensitiveDataTests
{
    /// <summary>敏感查询参数被替换为占位符，其他参数保留。</summary>
    [TestMethod("URL 脱敏：签名参数被替换")]
    public void RedactsSensitiveQueryParameters()
    {
        const string url = "https://cdn.example/live.flv?expires=1700000000&wsSecret=abc123&sign=zzz&quality=4";
        string redacted = SensitiveData.RedactUrl(url);

        Assert.Contains("expires=<redacted>", redacted);
        Assert.Contains("wsSecret=<redacted>", redacted);
        Assert.Contains("sign=<redacted>", redacted);
        Assert.Contains("quality=4", redacted);
        Assert.DoesNotContain("abc123", redacted);
        Assert.DoesNotContain("1700000000", redacted);
    }

    /// <summary>虎牙/斗鱼常见的签名参数名（无下划线变体）同样被脱敏。</summary>
    [TestMethod("URL 脱敏：无下划线的签名参数变体")]
    public void RedactsSignatureVariants()
    {
        string redacted = SensitiveData.RedactUrl("https://cdn.example/x.flv?wsSecret=abc&wsTime=deadbeef&txSecret=t&txTime=1&upsig=u");

        Assert.DoesNotContain("abc", redacted);
        Assert.DoesNotContain("deadbeef", redacted);
        Assert.Contains("wsSecret=<redacted>", redacted);
        Assert.Contains("wsTime=<redacted>", redacted);
        Assert.Contains("txSecret=<redacted>", redacted);
        Assert.Contains("txTime=<redacted>", redacted);
        Assert.Contains("upsig=<redacted>", redacted);
    }

    /// <summary>无查询串时原样返回。</summary>
    [TestMethod("URL 脱敏：无查询串与空输入")]
    public void RedactsWithoutQuery()
    {
        Assert.Equal("https://cdn.example/live.flv", SensitiveData.RedactUrl("https://cdn.example/live.flv"));
        Assert.Equal(string.Empty, SensitiveData.RedactUrl(null));
        Assert.Equal(string.Empty, SensitiveData.RedactUrl("   "));
    }

    /// <summary>Cookie 只保留名字。</summary>
    [TestMethod("Cookie 脱敏：只保留键名")]
    public void RedactsCookie()
    {
        string redacted = SensitiveData.RedactCookie("SESSDATA=secret; bili_jct=token; DedeUserID=42");
        Assert.Contains("SESSDATA=<redacted>", redacted);
        Assert.Contains("bili_jct=<redacted>", redacted);
        Assert.DoesNotContain("secret", redacted);
        Assert.DoesNotContain("token", redacted);
    }

    /// <summary>指纹不泄露原文，但能区分不同值。</summary>
    [TestMethod("指纹：长度 + 哈希，不含原文")]
    public void FingerprintHidesContent()
    {
        const string url = "https://cdn.example/a.flv?sign=1";
        string first = SensitiveData.Fingerprint(url);
        string second = SensitiveData.Fingerprint("https://cdn.example/a.flv?sign=2");
        string empty = SensitiveData.Fingerprint(null);

        Assert.Contains($"len={url.Length}", first);
        Assert.True(first != second, "不同 URL 的指纹应当不同");
        Assert.DoesNotContain("sign=1", first);
        Assert.Equal("len=0;fp=-", empty);
    }
}
