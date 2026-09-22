namespace StreamPilot.Tests.Cases;

using StreamPilot.App.Services;
using StreamPilot.App.ViewModels;
using StreamPilot.Core.Models;
using StreamPilot.Tests.Framework;

/// <summary>
/// <see cref="PlatformDetector"/> 的平台识别用例（纯函数，不联网、不落盘）。
/// </summary>
/// <remarks>
/// 这些用例对着"预设只能加哔哩哔哩"的根因：识别必须认各平台的分享短链与分享文案里的链接
/// （旧实现只认四个直播间页面的完整主机名，其余输入一律落到默认平台 = B 站）；
/// 认不出的输入（纯房间号、别的域名）必须返回 <see langword="null"/>，
/// 由调用方回退到用户在对话框里选的平台或设置里的默认平台。
/// </remarks>
[TestClass]
public sealed class PlatformDetectionTests
{
    /// <summary>六个平台各自的直播间页面链接都能识别（正常）。</summary>
    [TestMethod("平台识别：六个平台的直播间链接")]
    public void DetectsEveryPlatformFromRoomPageUrl()
    {
        Assert.Equal<PlatformId?>(PlatformId.Bilibili, PlatformDetector.Detect("https://live.bilibili.com/21452505"));
        Assert.Equal<PlatformId?>(PlatformId.Douyin, PlatformDetector.Detect("https://live.douyin.com/123456789"));
        Assert.Equal<PlatformId?>(PlatformId.Huya, PlatformDetector.Detect("https://www.huya.com/660000"));
        Assert.Equal<PlatformId?>(PlatformId.Douyu, PlatformDetector.Detect("https://www.douyu.com/9999"));
        Assert.Equal<PlatformId?>(PlatformId.Yy, PlatformDetector.Detect("https://www.yy.com/168"));
        Assert.Equal<PlatformId?>(PlatformId.Bigo, PlatformDetector.Detect("https://www.bigo.tv/abc123"));
    }

    /// <summary>各平台分享出的短链与分享文案同样能识别（正常）。</summary>
    [TestMethod("平台识别：分享短链与分享文案里的链接")]
    public void DetectsPlatformFromShareLinks()
    {
        Assert.Equal<PlatformId?>(PlatformId.Bilibili, PlatformDetector.Detect("https://b23.tv/AbCdEfG"));
        Assert.Equal<PlatformId?>(
            PlatformId.Douyin,
            PlatformDetector.Detect("7.65 复制打开抖音，看看【某主播的直播间】 https://v.douyin.com/iRNBho6u/ 3@4.com"));
        Assert.Equal<PlatformId?>(PlatformId.Huya, PlatformDetector.Detect("https://m.huya.com/660000"));
        Assert.Equal<PlatformId?>(PlatformId.Douyu, PlatformDetector.Detect("https://v.douyu.com/show/abcdef"));
        Assert.Equal<PlatformId?>(PlatformId.Yy, PlatformDetector.Detect("https://yy.com/168"));
        Assert.Equal<PlatformId?>(PlatformId.Bigo, PlatformDetector.Detect("https://bigo.sg/abc123"));

        // 分享文案里链接前面有中文说明时，仍然取链接的主机名而不是文案里的其它字符。
        Assert.Equal<PlatformId?>(
            PlatformId.Huya,
            PlatformDetector.Detect("【虎牙】主播开播了：https://www.huya.com/660000?share=1 快来看"));
    }

    /// <summary>大小写混合与缺少 scheme 的链接都能识别（边界）。</summary>
    [TestMethod("平台识别：大小写与缺 scheme 的链接")]
    public void DetectsPlatformRegardlessOfCaseAndScheme()
    {
        Assert.Equal<PlatformId?>(PlatformId.Huya, PlatformDetector.Detect("HTTPS://WWW.HUYA.COM/660000"));
        Assert.Equal<PlatformId?>(PlatformId.Bilibili, PlatformDetector.Detect("live.bilibili.com/21452505"));
        Assert.Equal<PlatformId?>(PlatformId.Douyin, PlatformDetector.Detect("  live.douyin.com/123456789  "));
        Assert.Equal<PlatformId?>(PlatformId.Bigo, PlatformDetector.Detect("www.bigo.tv/abc123"));
    }

    /// <summary>纯房间号无法识别，回退调用方指定的平台（异常/回退）。</summary>
    [TestMethod("平台识别：房间号回退指定平台")]
    public void FallsBackForBareRoomId()
    {
        Assert.Null(PlatformDetector.Detect("660000"), "纯房间号不属于任何平台");
        Assert.Null(PlatformDetector.Detect("21452505"), "B 站房间号同样无法从数字识别");

        Assert.Equal(PlatformId.Huya, PlatformDetector.DetectOrFallback("660000", PlatformId.Huya));
        Assert.Equal(PlatformId.Douyu, PlatformDetector.DetectOrFallback("9999", PlatformId.Douyu));
        Assert.Equal(PlatformId.Unknown, PlatformDetector.DetectOrFallback("660000", PlatformId.Unknown));
    }

    /// <summary>空输入、空白输入与 <see langword="null"/> 原样回退（异常）。</summary>
    [TestMethod("平台识别：空输入回退默认平台")]
    public void FallsBackForEmptyInput()
    {
        Assert.Null(PlatformDetector.Detect(null));
        Assert.Null(PlatformDetector.Detect(string.Empty));
        Assert.Null(PlatformDetector.Detect("   "));

        Assert.Equal(PlatformId.Bilibili, PlatformDetector.DetectOrFallback(null, PlatformId.Bilibili));
        Assert.Equal(PlatformId.Douyu, PlatformDetector.DetectOrFallback("   ", PlatformId.Douyu));
    }

    /// <summary>长相相近的域名不得误判（边界）。</summary>
    [TestMethod("平台识别：相似域名不误判")]
    public void DoesNotMatchLookAlikeDomains()
    {
        Assert.Null(PlatformDetector.Detect("https://myy.com/168"), "myy.com 不是 YY");
        Assert.Null(PlatformDetector.Detect("https://notbilibili.com/123"), "notbilibili.com 不是 B 站");
        Assert.Null(PlatformDetector.Detect("https://huya.com.evil.example/660000"), "域名后缀出现在路径上不算命中");
        Assert.Null(PlatformDetector.Detect("https://bigo.tvs/abc"), "bigo.tvs 不是 Bigo Live");
        Assert.Null(PlatformDetector.Detect("https://example.com/660000"), "未知域名不猜平台");
        Assert.Null(PlatformDetector.Detect("看看这个直播间 https://example.com/1"), "未知域名不猜平台");
    }

    /// <summary>平台下拉必须给出六个平台且都带可识别域名（边界）。</summary>
    [TestMethod("平台识别：六个平台都带可识别域名")]
    public void EveryPlatformHasMatchDomains()
    {
        PlatformId[] platforms =
        [
            PlatformId.Bilibili, PlatformId.Douyin, PlatformId.Huya,
            PlatformId.Douyu, PlatformId.Yy, PlatformId.Bigo,
        ];

        foreach (PlatformId platform in platforms)
        {
            // 找不到就直接判失败：既断言语义清晰，也避免可空性告警（本仓库警告即错误）。
            PlatformOption option = PlatformOption.Find(platform)
                ?? throw new AssertionFailedException(platform + " 必须在可选平台列表里");
            Assert.True(option.MatchDomains.Count > 0, platform + " 必须有可识别域名");
            Assert.True(option.DisplayName.Length > 0, platform + " 必须有展示名");
        }

        Assert.Null(PlatformOption.Find(PlatformId.Unknown), "Unknown 不属于可选平台");
        Assert.Equal(platforms.Length, PlatformOption.All.Count, "可选平台数量");
    }
}
