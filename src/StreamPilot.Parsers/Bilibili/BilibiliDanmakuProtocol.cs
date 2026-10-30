namespace StreamPilot.Parsers.Bilibili;

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// B站直播间弹幕协议编解码（纯函数，无网络、无状态，可被单元测试完全覆盖）。
/// </summary>
/// <remarks>
/// <para>协议要点（2026-10 实测于房间 21144080，25 秒收到 202 条消息 / 176 条 DANMU_MSG）：</para>
/// <list type="bullet">
///   <item>每个包固定 16 字节头：<c>packet_len(4) + header_len(2) + protover(2) + operation(4) + sequence(4)</c>，均大端；</item>
///   <item><c>protover = 3</c> 且 <c>operation = 5</c> 时，body 是 <b>Brotli 压缩</b>的多个子包，解压后需递归解析；</item>
///   <item>认证包 <c>operation = 7</c>，body 为 JSON（uid / roomid / protover / platform / type / key）；</item>
///   <item>心跳包 <c>operation = 2</c>，每 30 秒一次；心跳回复 <c>operation = 3</c>，body 前 4 字节为在线人气值。</item>
/// </list>
/// <para>
/// 本类只做字节层面的编解码：<b>不发起任何网络请求</b>，也不持有连接状态，
/// 便于在无网络环境下用单元测试锁住协议兼容性。
/// </para>
/// </remarks>
public static class BilibiliDanmakuProtocol
{
    /// <summary>心跳包操作码。</summary>
    public const int OperationHeartbeat = 2;

    /// <summary>心跳回复操作码（body 前 4 字节为在线人气）。</summary>
    public const int OperationHeartbeatReply = 3;

    /// <summary>普通消息操作码（弹幕、礼物、互动等都在这里）。</summary>
    public const int OperationMessage = 5;

    /// <summary>认证包操作码。</summary>
    public const int OperationAuth = 7;

    /// <summary>认证回复操作码（body 为 <c>{"code":0}</c> 表示成功）。</summary>
    public const int OperationAuthReply = 8;

    /// <summary>包头固定长度（字节）。</summary>
    public const int PacketHeaderLength = 16;

    /// <summary>协议版本：明文 JSON。</summary>
    public const short ProtocolVersionPlain = 0;

    /// <summary>协议版本：心跳/认证使用的简单版本。</summary>
    public const short ProtocolVersionSimple = 1;

    /// <summary>协议版本：Brotli 压缩的嵌套包。</summary>
    public const short ProtocolVersionBrotli = 3;

    /// <summary>WBI 混淆表（B站前端固定常量，<b>不可修改</b>）。</summary>
    private static readonly int[] MixinKeyEncTab =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
        37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
        22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52,
    ];

    /// <summary>WBI 签名需要剔除的字符（会破坏查询串语义）。</summary>
    private static readonly char[] WbiForbiddenChars = ['!', '\'', '(', ')', '*'];

    /// <summary>混淆后取前多少个字符作为密钥。</summary>
    private const int MixinKeyLength = 32;

    /// <summary>
    /// 编码一个数据包（16 字节头 + body）。
    /// </summary>
    /// <param name="operation">操作码。</param>
    /// <param name="body">包体（可为空）。</param>
    /// <param name="protocolVersion">协议版本，默认 1。</param>
    /// <returns>可直接写入 WebSocket 的字节数组。</returns>
    public static byte[] EncodePacket(int operation, ReadOnlySpan<byte> body, short protocolVersion = ProtocolVersionSimple)
    {
        int total = PacketHeaderLength + body.Length;
        byte[] packet = new byte[total];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), total);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4, 2), PacketHeaderLength);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(6, 2), protocolVersion);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), operation);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), 1);
        body.CopyTo(packet.AsSpan(PacketHeaderLength));
        return packet;
    }

    /// <summary>编码认证包（<c>operation = 7</c>，body 为 JSON）。</summary>
    /// <param name="token"><c>getDanmuInfo</c> 返回的 token。</param>
    /// <param name="roomId">真实房间号。</param>
    /// <param name="userId">当前用户 UID；匿名连接传 0。</param>
    /// <returns>认证包字节。</returns>
    /// <remarks>
    /// <c>protover</c> 固定 3：服务端据此下发 Brotli 压缩的嵌套包，可显著降低带宽。
    /// </remarks>
    public static byte[] EncodeAuthPacket(string token, long roomId, long userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        string json = string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"uid":{{userId}},"roomid":{{roomId}},"protover":3,"platform":"web","type":2,"key":"{{token}}"}""");
        return EncodePacket(OperationAuth, Encoding.UTF8.GetBytes(json));
    }

    /// <summary>编码心跳包（<c>operation = 2</c>，空 body）。</summary>
    /// <returns>心跳包字节。</returns>
    public static byte[] EncodeHeartbeatPacket() => EncodePacket(OperationHeartbeat, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// 解析一帧数据（可能包含多个包；<c>protover = 3</c> 时会递归解压子包）。
    /// </summary>
    /// <param name="data">WebSocket 收到的原始字节。</param>
    /// <returns>扁平化后的包列表；输入非法时返回空列表（不抛异常）。</returns>
    public static IReadOnlyList<DanmakuPacket> DecodePackets(ReadOnlySpan<byte> data)
    {
        List<DanmakuPacket> packets = [];
        int offset = 0;
        while (offset + PacketHeaderLength <= data.Length)
        {
            int packetLength = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
            if (packetLength < PacketHeaderLength || offset + packetLength > data.Length)
            {
                // 长度字段越界：数据被截断或不是本协议，整帧放弃，避免死循环。
                break;
            }

            short headerLength = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset + 4, 2));
            short protocolVersion = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset + 6, 2));
            int operation = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 8, 4));

            if (headerLength < PacketHeaderLength || headerLength > packetLength)
            {
                break;
            }

            ReadOnlySpan<byte> body = data.Slice(offset + headerLength, packetLength - headerLength);
            if (protocolVersion == ProtocolVersionBrotli && operation == OperationMessage)
            {
                byte[]? decompressed = TryDecompressBrotli(body);
                if (decompressed is not null)
                {
                    packets.AddRange(DecodePackets(decompressed));
                }
            }
            else
            {
                packets.Add(new DanmakuPacket(operation, protocolVersion, body.ToArray()));
            }

            offset += packetLength;
        }

        return packets;
    }

    /// <summary>读取心跳回复中的在线人气值。</summary>
    /// <param name="packet">心跳回复包（<c>operation = 3</c>）。</param>
    /// <returns>人气值；包体不足 4 字节时返回 <see langword="null"/>。</returns>
    public static int? ReadPopularity(DanmakuPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return packet.Body.Length >= 4 ? BinaryPrimitives.ReadInt32BigEndian(packet.Body) : null;
    }

    /// <summary>
    /// 判断认证回复是否成功。
    /// </summary>
    /// <param name="packet">认证回复包（<c>operation = 8</c>）。</param>
    /// <returns>body 中 <c>code = 0</c> 时返回 <see langword="true"/>。</returns>
    public static bool IsAuthSucceeded(DanmakuPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Operation != OperationAuthReply)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(packet.Body);
            return document.RootElement.TryGetProperty("code", out JsonElement code)
                && code.ValueKind == JsonValueKind.Number
                && code.GetInt32() == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从 <c>nav</c> 接口返回的 <c>wbi_img</c> 地址中提取 img_key 与 sub_key。
    /// </summary>
    /// <param name="imgUrl">例如 <c>https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png</c>。</param>
    /// <param name="subUrl">同上，sub 版本。</param>
    /// <returns>两个密钥（不含扩展名）。</returns>
    /// <exception cref="ArgumentException">地址为空或无法提取文件名时抛出。</exception>
    public static (string ImgKey, string SubKey) ExtractWbiKeys(string imgUrl, string subUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imgUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(subUrl);
        return (ExtractKeyFromUrl(imgUrl), ExtractKeyFromUrl(subUrl));
    }

    /// <summary>
    /// 计算 WBI 签名查询串（含 <c>wts</c> 与 <c>w_rid</c>）。
    /// </summary>
    /// <param name="parameters">业务参数（不含 wts / w_rid）。</param>
    /// <param name="imgKey">img_key。</param>
    /// <param name="subKey">sub_key。</param>
    /// <param name="timestampSeconds">Unix 时间戳（秒）。</param>
    /// <returns>可直接拼到 URL 后面的查询串。</returns>
    /// <remarks>
    /// 算法：<c>mixin_key = 混淆表重排(img_key + sub_key)[:32]</c>；
    /// 参数按 key 升序、剔除 <c>!'()*</c>、URL 编码后拼接，
    /// 再计算 <c>md5(查询串 + mixin_key)</c> 得到 <c>w_rid</c>。
    /// </remarks>
    public static string BuildWbiQuery(
        IReadOnlyDictionary<string, string> parameters,
        string imgKey,
        string subKey,
        long timestampSeconds)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(imgKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(subKey);

        string mixinKey = BuildMixinKey(imgKey + subKey);

        List<KeyValuePair<string, string>> ordered = [.. parameters];
        ordered.Add(new KeyValuePair<string, string>("wts", timestampSeconds.ToString(CultureInfo.InvariantCulture)));
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));

        StringBuilder query = new();
        foreach ((string key, string value) in ordered)
        {
            if (query.Length > 0)
            {
                query.Append('&');
            }

            query.Append(Uri.EscapeDataString(key))
                .Append('=')
                .Append(Uri.EscapeDataString(FilterWbiValue(value)));
        }

        string queryString = query.ToString();
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(queryString + mixinKey));
        return queryString + "&w_rid=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ExtractKeyFromUrl(string url)
    {
        int slash = url.LastIndexOf('/');
        string fileName = slash >= 0 ? url[(slash + 1)..] : url;
        int dot = fileName.IndexOf('.', StringComparison.Ordinal);
        string key = dot > 0 ? fileName[..dot] : fileName;
        if (key.Length == 0)
        {
            throw new ArgumentException("WBI 密钥地址中不含文件名。", nameof(url));
        }

        return key;
    }

    private static string BuildMixinKey(string source)
    {
        StringBuilder builder = new(MixinKeyLength);
        for (int index = 0; index < MixinKeyLength && index < MixinKeyEncTab.Length; index++)
        {
            int position = MixinKeyEncTab[index];
            if (position < source.Length)
            {
                builder.Append(source[position]);
            }
        }

        return builder.ToString();
    }

    private static string FilterWbiValue(string value)
    {
        string filtered = value;
        foreach (char forbidden in WbiForbiddenChars)
        {
            filtered = filtered.Replace(forbidden.ToString(), string.Empty, StringComparison.Ordinal);
        }

        return filtered;
    }

    private static byte[]? TryDecompressBrotli(ReadOnlySpan<byte> compressed)
    {
        if (compressed.Length == 0)
        {
            return null;
        }

        try
        {
            using MemoryStream input = new(compressed.ToArray(), writable: false);
            using BrotliStream brotli = new(input, CompressionMode.Decompress);
            using MemoryStream output = new();
            brotli.CopyTo(output);
            return output.Length == 0 ? null : output.ToArray();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 压缩流损坏或运行环境不支持 Brotli：丢弃这一帧，不影响后续消息。
            //
            // 这里刻意**不精确匹配异常类型**：不同 .NET 版本对"损坏的 Brotli 数据"抛出的类型不一致
            // （实测 .NET 10 抛出的既不是 InvalidDataException 也不是 IOException），
            // 精确匹配会让升级运行时后突然失效（表现为"弹幕忽然全没了"）。
            // 本方法是纯函数的降级路径，不涉及业务异常，捕获后返回 null 即设计行为。
            return null;
        }
    }
}

/// <summary>解析后的单个协议包。</summary>
/// <param name="Operation">操作码（2 心跳 / 3 心跳回复 / 5 消息 / 7 认证 / 8 认证回复）。</param>
/// <param name="ProtocolVersion">协议版本（0 明文 / 1 简单 / 3 Brotli）。</param>
/// <param name="Body">包体（Brotli 包已展开，此处为解压后的明文子包体）。</param>
public sealed record DanmakuPacket(int Operation, short ProtocolVersion, byte[] Body);
