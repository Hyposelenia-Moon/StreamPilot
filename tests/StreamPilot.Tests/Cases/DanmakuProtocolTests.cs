namespace StreamPilot.Tests.Cases;

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using StreamPilot.Core.Models;
using StreamPilot.Parsers.Bilibili;
using StreamPilot.Tests.Framework;

/// <summary>
/// B站弹幕协议编解码测试。
/// </summary>
/// <remarks>
/// 协议参数来自 2026-10 实测（房间 21144080：25 秒 202 条消息 / 176 条 DANMU_MSG）。
/// 这些用例把「16 字节包头 + Brotli 嵌套 + DANMU_MSG 字段位置」锁死：
/// B站一旦改协议，测试会先失败，而不是等到用户端"弹幕突然不显示"。
/// </remarks>
[TestClass]
public sealed class DanmakuProtocolTests
{
    /// <summary>包头编解码必须可逆，且长度/操作码/版本全部正确。</summary>
    [TestMethod("弹幕协议：包编解码往返")]
    public void EncodesAndDecodesPacket()
    {
        byte[] body = Encoding.UTF8.GetBytes("hello");
        byte[] packet = BilibiliDanmakuProtocol.EncodePacket(BilibiliDanmakuProtocol.OperationHeartbeat, body);

        Assert.Equal(BilibiliDanmakuProtocol.PacketHeaderLength + body.Length, packet.Length, "总长度 = 包头 + body");

        IReadOnlyList<DanmakuPacket> decoded = BilibiliDanmakuProtocol.DecodePackets(packet);
        Assert.Equal(1, decoded.Count);
        Assert.Equal(BilibiliDanmakuProtocol.OperationHeartbeat, decoded[0].Operation);
        Assert.Equal("hello", Encoding.UTF8.GetString(decoded[0].Body));
    }

    /// <summary>心跳包必须是 16 字节、无 body。</summary>
    [TestMethod("弹幕协议：心跳包为固定 16 字节空体")]
    public void EncodesHeartbeatPacket()
    {
        byte[] packet = BilibiliDanmakuProtocol.EncodeHeartbeatPacket();
        Assert.Equal(BilibiliDanmakuProtocol.PacketHeaderLength, packet.Length);
        Assert.Equal(BilibiliDanmakuProtocol.OperationHeartbeat, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(8, 4)));
        Assert.Equal(0, packet.Length - BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(4, 2)), "header_len 必须等于总长");
    }

    /// <summary>认证包 body 必须是合法 JSON，含 uid / roomid / protover=3 / key。</summary>
    [TestMethod("弹幕协议：认证包为合法 JSON")]
    public void EncodesAuthPacketAsJson()
    {
        byte[] packet = BilibiliDanmakuProtocol.EncodeAuthPacket("token-abc", 21144080, 42);
        IReadOnlyList<DanmakuPacket> decoded = BilibiliDanmakuProtocol.DecodePackets(packet);

        Assert.Equal(1, decoded.Count);
        Assert.Equal(BilibiliDanmakuProtocol.OperationAuth, decoded[0].Operation);

        string json = Encoding.UTF8.GetString(decoded[0].Body);
        Assert.True(json.Contains("\"uid\":42", StringComparison.Ordinal), "必须带 uid");
        Assert.True(json.Contains("\"roomid\":21144080", StringComparison.Ordinal), "必须带 roomid");
        Assert.True(json.Contains("\"protover\":3", StringComparison.Ordinal), "protover 必须为 3（Brotli）");
        Assert.True(json.Contains("\"key\":\"token-abc\"", StringComparison.Ordinal), "必须带 token");
    }

    /// <summary>protover=3 的包必须能 Brotli 解压并递归展开成多条消息包。</summary>
    [TestMethod("弹幕协议：Brotli 嵌套包递归展开")]
    public void DecodesBrotliNestedPackets()
    {
        byte[] inner1 = BilibiliDanmakuProtocol.EncodePacket(
            BilibiliDanmakuProtocol.OperationMessage,
            Encoding.UTF8.GetBytes("{\"cmd\":\"A\"}"),
            BilibiliDanmakuProtocol.ProtocolVersionPlain);
        byte[] inner2 = BilibiliDanmakuProtocol.EncodePacket(
            BilibiliDanmakuProtocol.OperationMessage,
            Encoding.UTF8.GetBytes("{\"cmd\":\"B\"}"),
            BilibiliDanmakuProtocol.ProtocolVersionPlain);

        byte[] nested = [.. inner1, .. inner2];
        byte[] compressed = CompressBrotli(nested);
        byte[] outer = BilibiliDanmakuProtocol.EncodePacket(
            BilibiliDanmakuProtocol.OperationMessage,
            compressed,
            BilibiliDanmakuProtocol.ProtocolVersionBrotli);

        IReadOnlyList<DanmakuPacket> decoded = BilibiliDanmakuProtocol.DecodePackets(outer);

        Assert.Equal(2, decoded.Count, "Brotli 内层两包必须被展开");
        Assert.Equal("{\"cmd\":\"A\"}", Encoding.UTF8.GetString(decoded[0].Body));
        Assert.Equal("{\"cmd\":\"B\"}", Encoding.UTF8.GetString(decoded[1].Body));
    }

    /// <summary>损坏的 Brotli 数据必须被静默丢弃，而不是抛异常中断整个连接。</summary>
    [TestMethod("弹幕协议：损坏的 Brotli 包被丢弃")]
    public void DropsCorruptedBrotli()
    {
        byte[] outer = BilibiliDanmakuProtocol.EncodePacket(
            BilibiliDanmakuProtocol.OperationMessage,
            [0xFF, 0xFE, 0xFD, 0xFC],
            BilibiliDanmakuProtocol.ProtocolVersionBrotli);

        IReadOnlyList<DanmakuPacket> decoded = BilibiliDanmakuProtocol.DecodePackets(outer);
        Assert.Equal(0, decoded.Count, "解压失败时返回空列表，不能抛异常");
    }

    /// <summary>长度字段越界的帧必须整帧放弃，不能死循环。</summary>
    [TestMethod("弹幕协议：长度越界的帧被丢弃")]
    public void RejectsOutOfRangeLength()
    {
        byte[] bogus = new byte[BilibiliDanmakuProtocol.PacketHeaderLength];
        BinaryPrimitives.WriteInt32BigEndian(bogus.AsSpan(0, 4), int.MaxValue);
        BinaryPrimitives.WriteInt16BigEndian(bogus.AsSpan(4, 2), BilibiliDanmakuProtocol.PacketHeaderLength);

        Assert.Equal(0, BilibiliDanmakuProtocol.DecodePackets(bogus).Count);
        Assert.Equal(0, BilibiliDanmakuProtocol.DecodePackets(ReadOnlySpan<byte>.Empty).Count);
    }

    /// <summary>WBI 签名必须确定性、包含 wts 与 32 位 w_rid。</summary>
    [TestMethod("弹幕协议：WBI 签名确定且含 wts/w_rid")]
    public void BuildsWbiQuery()
    {
        Dictionary<string, string> parameters = new() { ["id"] = "21144080", ["type"] = "0" };
        string first = BilibiliDanmakuProtocol.BuildWbiQuery(parameters, "7cd084941338484aae1ad9425b84077c", "493c8503737f0bd6e0e1d3b1d8d8a2a9", 1700000000);
        string second = BilibiliDanmakuProtocol.BuildWbiQuery(parameters, "7cd084941338484aae1ad9425b84077c", "493c8503737f0bd6e0e1d3b1d8d8a2a9", 1700000000);

        Assert.Equal(first, second, "相同输入必须产生相同签名");
        Assert.True(first.Contains("wts=1700000000", StringComparison.Ordinal), "必须带 wts");
        Assert.True(first.Contains("w_rid=", StringComparison.Ordinal), "必须带 w_rid");

        int wridIndex = first.IndexOf("w_rid=", StringComparison.Ordinal) + "w_rid=".Length;
        Assert.Equal(32, first.Length - wridIndex, "w_rid 必须是 32 位十六进制");
    }

    /// <summary>WBI 密钥提取必须去掉域名与扩展名。</summary>
    [TestMethod("弹幕协议：WBI 密钥提取")]
    public void ExtractsWbiKeys()
    {
        (string imgKey, string subKey) = BilibiliDanmakuProtocol.ExtractWbiKeys(
            "https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
            "https://i0.hdslb.com/bfs/wbi/493c8503737f0bd6e0e1d3b1d8d8a2a9.png");

        Assert.Equal("7cd084941338484aae1ad9425b84077c", imgKey);
        Assert.Equal("493c8503737f0bd6e0e1d3b1d8d8a2a9", subKey);
    }

    /// <summary>普通弹幕必须解析出文本、昵称、粉丝牌与颜色。</summary>
    [TestMethod("弹幕协议：解析普通弹幕")]
    public void ParsesChatMessage()
    {
        // info[0][3] 是颜色（255 = 蓝色），info[1] 文本，info[2][1] 昵称，info[3] 粉丝牌。
        const string json = """
            {"cmd":"DANMU_MSG","info":[[0,1,25,255],"你好世界",[12345,"测试用户",0],[21,"粉丝牌","主播"]]}
            """;
        DanmakuMessage? message = BilibiliDanmakuMessageParser.Parse(Encoding.UTF8.GetBytes(json), 7);

        Assert.True(message is not null, "必须解析成功");
        Assert.Equal(DanmakuKind.Chat, message!.Kind);
        Assert.Equal("你好世界", message.Text);
        Assert.Equal("测试用户", message.User);
        Assert.Equal(21, message.MedalLevel);
        Assert.Equal("粉丝牌", message.MedalName);
        Assert.Equal("#0000FF", message.Color, "255 必须转成蓝色");
        Assert.Equal(7, message.Sequence);
    }

    /// <summary>白色（16777215）视为不染色。</summary>
    [TestMethod("弹幕协议：白色不染色")]
    public void TreatsWhiteAsNoColor()
    {
        const string json = """
            {"cmd":"DANMU_MSG","info":[[0,1,25,16777215],"白色弹幕",[1,"用户",0],[]]}
            """;
        DanmakuMessage? message = BilibiliDanmakuMessageParser.Parse(Encoding.UTF8.GetBytes(json), 1);
        Assert.True(message?.Color is null, "白色不应输出颜色");
        Assert.True(message?.MedalName is null, "空粉丝牌数组不产生名称");
    }

    /// <summary>礼物与进场消息按类型解析成描述文本。</summary>
    [TestMethod("弹幕协议：解析礼物与互动")]
    public void ParsesGiftAndInteract()
    {
        const string gift = """
            {"cmd":"SEND_GIFT","data":{"uname":"打赏哥","giftName":"小心心","num":3}}
            """;
        DanmakuMessage? giftMessage = BilibiliDanmakuMessageParser.Parse(Encoding.UTF8.GetBytes(gift), 1);
        Assert.Equal(DanmakuKind.Gift, giftMessage?.Kind);
        Assert.Equal("打赏哥", giftMessage!.User);
        Assert.True(giftMessage.Text.Contains("小心心", StringComparison.Ordinal), "文本必须含礼物名");
        Assert.True(giftMessage.Text.Contains("×3", StringComparison.Ordinal), "文本必须含数量");

        const string interact = """
            {"cmd":"INTERACT_WORD","data":{"uname":"路人","msg_type":1}}
            """;
        DanmakuMessage? interactMessage = BilibiliDanmakuMessageParser.Parse(Encoding.UTF8.GetBytes(interact), 2);
        Assert.Equal(DanmakuKind.Interact, interactMessage?.Kind);
        Assert.Equal("进入直播间", interactMessage!.Text);
    }

    /// <summary>cmd 带后缀（DANMU_MSG:4:0:2:2:2:0）时仍按主名解析。</summary>
    [TestMethod("弹幕协议：cmd 带后缀仍可解析")]
    public void HandlesCommandWithSuffix()
    {
        const string json = """
            {"cmd":"DANMU_MSG:4:0:2:2:2:0","info":[[0,1,25,16777215],"带后缀",[1,"用户",0],[]]}
            """;
        DanmakuMessage? message = BilibiliDanmakuMessageParser.Parse(Encoding.UTF8.GetBytes(json), 1);
        Assert.Equal("带后缀", message?.Text);
    }

    /// <summary>畸形输入必须返回 null，不能抛异常。</summary>
    [TestMethod("弹幕协议：畸形输入返回 null")]
    public void RejectsMalformedInput()
    {
        Assert.True(BilibiliDanmakuMessageParser.Parse(ReadOnlySpan<byte>.Empty, 1) is null, "空输入");
        Assert.True(BilibiliDanmakuMessageParser.Parse(Encoding.UTF8.GetBytes("not-json"), 1) is null, "非 JSON");
        Assert.True(BilibiliDanmakuMessageParser.Parse(Encoding.UTF8.GetBytes("[]"), 1) is null, "数组不是对象");
        Assert.True(BilibiliDanmakuMessageParser.Parse(Encoding.UTF8.GetBytes("{\"cmd\":\"UNKNOWN_CMD\"}"), 1) is null, "未知类型");
        Assert.True(BilibiliDanmakuMessageParser.Parse(Encoding.UTF8.GetBytes("{\"cmd\":\"DANMU_MSG\"}"), 1) is null, "缺 info 字段");
        Assert.True(BilibiliDanmakuMessageParser.Parse(Encoding.UTF8.GetBytes("{\"cmd\":\"DANMU_MSG\",\"info\":[]}"), 1) is null, "info 为空");
    }

    /// <summary>认证回复：code=0 视为成功，其余视为失败。</summary>
    [TestMethod("弹幕协议：认证回复判定")]
    public void ChecksAuthReply()
    {
        DanmakuPacket ok = new(BilibiliDanmakuProtocol.OperationAuthReply, 1, Encoding.UTF8.GetBytes("{\"code\":0}"));
        DanmakuPacket failed = new(BilibiliDanmakuProtocol.OperationAuthReply, 1, Encoding.UTF8.GetBytes("{\"code\":-101}"));
        DanmakuPacket garbage = new(BilibiliDanmakuProtocol.OperationAuthReply, 1, Encoding.UTF8.GetBytes("oops"));

        Assert.True(BilibiliDanmakuProtocol.IsAuthSucceeded(ok), "code=0 必须判定成功");
        Assert.False(BilibiliDanmakuProtocol.IsAuthSucceeded(failed), "code=-101 必须判定失败");
        Assert.False(BilibiliDanmakuProtocol.IsAuthSucceeded(garbage), "非 JSON 必须判定失败");
    }

    /// <summary>心跳回复必须读出大端人气值。</summary>
    [TestMethod("弹幕协议：心跳回复读取人气值")]
    public void ReadsPopularity()
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(body, 89211699);
        DanmakuPacket packet = new(BilibiliDanmakuProtocol.OperationHeartbeatReply, 1, body);

        Assert.Equal(89211699, BilibiliDanmakuProtocol.ReadPopularity(packet));
        Assert.True(BilibiliDanmakuProtocol.ReadPopularity(new DanmakuPacket(3, 1, [])) is null, "空 body 返回 null");
    }

    private static byte[] CompressBrotli(byte[] data)
    {
        using MemoryStream output = new();
        using (BrotliStream brotli = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(data);
        }

        return output.ToArray();
    }
}
