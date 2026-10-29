using StreamPilot.App.Services;
using StreamPilot.Core.Models;
using StreamPilot.Tests.Framework;

namespace StreamPilot.Tests.Cases;

/// <summary>
/// 房间输入规范化的用例：分享文案、缺 scheme 的链接、包裹标点与房间号原样保留。
/// </summary>
/// <remarks>
/// 回归背景：解析器只认"以域名开头的完整地址"，用户粘贴整段分享文案时会被判成
/// "直播间链接的域名不属于 X"（历史日志里抖音 18/24、虎牙 8/8 都是这个原因）。
/// </remarks>
[TestClass]
public sealed class RoomInputNormalizerTests
{
    /// <summary>纯链接应原样保留（只去掉首尾空白）。</summary>
    [TestMethod("输入规范化：完整链接原样保留")]
    public void KeepsCompleteUrl()
    {
        Assert.Equal(
            "https://live.bilibili.com/814",
            RoomInputNormalizer.Normalize("  https://live.bilibili.com/814  "));
        Assert.Equal(
            "https://live.douyin.com/123456?from=share",
            RoomInputNormalizer.Normalize("https://live.douyin.com/123456?from=share"));
    }

    /// <summary>分享文案里夹带了链接时，只抽出链接。</summary>
    [TestMethod("输入规范化：从分享文案里抽出链接")]
    public void ExtractsUrlFromShareText()
    {
        Assert.Equal(
            "https://www.huya.com/660000",
            RoomInputNormalizer.Normalize("【主播开播了】https://www.huya.com/660000 ，快来看"));
        Assert.Equal(
            "https://live.douyin.com/745964462470",
            RoomInputNormalizer.Normalize("复制打开抖音，看看【某某】的直播 https://live.douyin.com/745964462470 关注主播"));
    }

    /// <summary>链接两侧的括号、引号、句号等包裹字符要被去掉。</summary>
    [TestMethod("输入规范化：去掉链接两侧的包裹字符")]
    public void TrimsWrappingPunctuation()
    {
        Assert.Equal(
            "https://www.douyu.com/1811143",
            RoomInputNormalizer.Normalize("（https://www.douyu.com/1811143）。"));
        Assert.Equal(
            "https://www.huya.com/152792",
            RoomInputNormalizer.Normalize("<https://www.huya.com/152792>"));
    }

    /// <summary>输入框残留前一次输入的前缀时，只保留真正的 http/https 链接。</summary>
    [TestMethod("输入规范化：丢弃链接前的残留前缀")]
    public void DropsGarbagePrefix()
    {
        Assert.Equal(
            "https://live.bilibili.com/814",
            RoomInputNormalizer.Normalize("wwwhttps://live.bilibili.com/814"));
        Assert.Equal(
            "https://www.huya.com/660000",
            RoomInputNormalizer.Normalize("abc123https://www.huya.com/660000"));
    }

    /// <summary>缺 scheme 的裸域链接补上 https://。</summary>
    [TestMethod("输入规范化：缺 scheme 的链接补全协议")]
    public void AddsSchemeForBareDomain()
    {
        Assert.Equal("https://live.bilibili.com/814", RoomInputNormalizer.Normalize("live.bilibili.com/814"));
        Assert.Equal("https://www.huya.com/660000", RoomInputNormalizer.Normalize("www.huya.com/660000"));
    }

    /// <summary>纯房间号必须原样保留，不得被补成链接。</summary>
    [TestMethod("输入规范化：房间号原样保留")]
    public void KeepsRoomIdUntouched()
    {
        Assert.Equal("814", RoomInputNormalizer.Normalize("814"));
        Assert.Equal("745964462470", RoomInputNormalizer.Normalize(" 745964462470 "));
        Assert.Equal("abc123", RoomInputNormalizer.Normalize("abc123"));
    }

    /// <summary>空输入与纯空白返回空字符串，由调用方给出"请先输入"提示。</summary>
    [TestMethod("输入规范化：空输入返回空字符串")]
    public void ReturnsEmptyForBlankInput()
    {
        Assert.Equal(string.Empty, RoomInputNormalizer.Normalize(null));
        Assert.Equal(string.Empty, RoomInputNormalizer.Normalize("   "));
        Assert.Equal(string.Empty, RoomInputNormalizer.Normalize("\t\r\n"));
    }

    /// <summary>纯数字房间号按平台拼成直播间链接，字母房间号与空输入不转换。</summary>
    /// <remarks>
    /// 界面上只保留"直播间链接"一种输入形态：用户输入 814 时转成
    /// <c>https://live.bilibili.com/814</c>，解析器、预设与日志都不再多态。
    /// 但"字母房间号"不能猜前缀（虎牙/YY 都有字母号），必须原样返回。
    /// </remarks>
    [TestMethod("输入规范化：房间号转直播间链接")]
    public void BuildsRoomUrlFromRoomId()
    {
        Assert.Equal("https://live.bilibili.com/814", RoomInputNormalizer.BuildRoomUrl(PlatformId.Bilibili, "814"));
        Assert.Equal("https://www.douyu.com/9999", RoomInputNormalizer.BuildRoomUrl(PlatformId.Douyu, "9999"));
        Assert.Equal("https://www.huya.com/660000", RoomInputNormalizer.BuildRoomUrl(PlatformId.Huya, "660000"));
        Assert.Equal("https://live.douyin.com/123456", RoomInputNormalizer.BuildRoomUrl(PlatformId.Douyin, "123456"));
        Assert.Equal("https://www.yy.com/168", RoomInputNormalizer.BuildRoomUrl(PlatformId.Yy, "168"));
        Assert.Equal("https://www.bigo.tv/abc123", RoomInputNormalizer.BuildRoomUrl(PlatformId.Bigo, "abc123"));
        Assert.Equal("814", RoomInputNormalizer.BuildRoomUrl(PlatformId.Unknown, "814"), "平台未知时不能猜前缀");
        Assert.Equal(string.Empty, RoomInputNormalizer.BuildRoomUrl(PlatformId.Bilibili, " "));
    }

    /// <summary>只有纯数字才算"房间号"，用于决定是否自动转换。</summary>
    [TestMethod("输入规范化：纯数字房间号判定")]
    public void DetectsPlainRoomId()
    {
        Assert.True(RoomInputNormalizer.IsPlainRoomId("814"));
        Assert.True(RoomInputNormalizer.IsPlainRoomId(" 745964462470 "));
        Assert.False(RoomInputNormalizer.IsPlainRoomId("abc123"), "字母房间号不能自动转换");
        Assert.False(RoomInputNormalizer.IsPlainRoomId("814a"));
        Assert.False(RoomInputNormalizer.IsPlainRoomId("https://live.bilibili.com/814"));
        Assert.False(RoomInputNormalizer.IsPlainRoomId(string.Empty));
        Assert.False(RoomInputNormalizer.IsPlainRoomId(null));
    }

    /// <summary>链接判定：有 scheme 或含路径分隔符时按链接处理，房间号不算链接。</summary>
    [TestMethod("输入规范化：链接与房间号的判定")]
    public void DetectsUrlShape()
    {
        Assert.True(RoomInputNormalizer.LooksLikeUrl("https://live.bilibili.com/814"));
        Assert.True(RoomInputNormalizer.LooksLikeUrl("live.bilibili.com/814"));
        Assert.False(RoomInputNormalizer.LooksLikeUrl("814"));
        Assert.False(RoomInputNormalizer.LooksLikeUrl(string.Empty));
        Assert.False(RoomInputNormalizer.LooksLikeUrl(null));
    }
}
