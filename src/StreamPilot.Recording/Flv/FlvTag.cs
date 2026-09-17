namespace StreamPilot.Recording.Flv;

/// <summary>
/// FLV 标签类型。
/// </summary>
public enum FlvTagType : byte
{
    /// <summary>音频标签。</summary>
    Audio = 8,

    /// <summary>视频标签。</summary>
    Video = 9,

    /// <summary>脚本数据标签（onMetaData）。</summary>
    Script = 18,
}

/// <summary>
/// 一个 FLV 标签（含完整头部与载荷）。
/// </summary>
/// <remarks>
/// 载荷以字节数组持有，录制路径只做搬运与少量头部改写，绝不触碰编码数据。
/// </remarks>
public sealed record FlvTag
{
    /// <summary>标签类型。</summary>
    public required FlvTagType Type { get; init; }

    /// <summary>时间戳（毫秒，已含扩展字节）。</summary>
    public required int TimestampMs { get; init; }

    /// <summary>原始 DataSize 字段（载荷长度）。</summary>
    public required int DataSize { get; init; }

    /// <summary>载荷。</summary>
    public required byte[] Payload { get; init; }

    /// <summary>标签总长度（11 字节头部 + 载荷 + 4 字节 PreviousTagSize）。</summary>
    public int TotalSize => FlvConstants.TagHeaderSize + DataSize + FlvConstants.PreviousTagSizeLength;

    /// <summary>
    /// 判断该标签是否为需要缓存的"解码器初始化标签"（AVC sequence header / AAC sequence header）。
    /// </summary>
    /// <returns>是初始化标签返回 <see langword="true"/>。</returns>
    public bool IsSequenceHeader()
    {
        if (Type == FlvTagType.Video && Payload.Length >= FlvConstants.VideoPacketTypeOffset + 1)
        {
            return Payload[FlvConstants.VideoPacketTypeOffset] == FlvConstants.AvcSequenceHeaderPacketType;
        }

        if (Type == FlvTagType.Audio && Payload.Length >= FlvConstants.AudioPacketTypeOffset + 1)
        {
            return Payload[FlvConstants.AudioPacketTypeOffset] == FlvConstants.AacSequenceHeaderPacketType;
        }

        return false;
    }

    /// <summary>
    /// 判断该标签是否为视频关键帧（AVC NALU，FrameType == 1 且 AVCPacketType == 1）。
    /// </summary>
    /// <returns>是关键帧返回 <see langword="true"/>。</returns>
    public bool IsVideoKeyFrame()
    {
        if (Type != FlvTagType.Video || Payload.Length <= FlvConstants.VideoPacketTypeOffset)
        {
            return false;
        }

        int frameType = Payload[0] >> 4;
        bool isAvcNalu = Payload[FlvConstants.VideoPacketTypeOffset] == FlvConstants.AvcNaluPacketType;
        return frameType == FlvConstants.KeyFrameType && isAvcNalu;
    }

    /// <summary>
    /// 判断两个标签的载荷是否完全相同（用于检测编码参数变化）。
    /// </summary>
    /// <param name="other">另一个标签。</param>
    /// <returns>相同返回 <see langword="true"/>。</returns>
    public bool HasSamePayload(FlvTag? other)
    {
        if (other is null || other.Payload.Length != Payload.Length)
        {
            return false;
        }

        return Payload.AsSpan().SequenceEqual(other.Payload);
    }
}

/// <summary>
/// FLV 容器常量（全部集中定义，禁止在调用点出现魔法数字）。
/// </summary>
public static class FlvConstants
{
    /// <summary>FLV 标签头长度。</summary>
    public const int TagHeaderSize = 11;

    /// <summary>PreviousTagSize 字段长度。</summary>
    public const int PreviousTagSizeLength = 4;

    /// <summary>FLV 文件头长度（签名 3 + 版本 1 + 标志 1 + DataOffset 4）。</summary>
    public const int FileHeaderSize = 9;

    /// <summary>文件头之后的第一个 PreviousTagSize0 长度。</summary>
    public const int PreviousTagSize0Length = 4;

    /// <summary>FLV 签名。</summary>
    public static readonly byte[] Signature = [(byte)'F', (byte)'L', (byte)'V'];

    /// <summary>FLV 版本号。</summary>
    public const byte Version = 1;

    /// <summary>标志位：含视频。</summary>
    public const byte FlagVideo = 0x01;

    /// <summary>标志位：含音频。</summary>
    public const byte FlagAudio = 0x04;

    /// <summary>DataOffset（固定 9）。</summary>
    public const int DataOffset = 9;

    /// <summary>视频标签载荷中 AVCPacketType 的偏移。</summary>
    public const int VideoPacketTypeOffset = 1;

    /// <summary>音频标签载荷中 AACPacketType 的偏移。</summary>
    public const int AudioPacketTypeOffset = 1;

    /// <summary>AVCPacketType：sequence header。</summary>
    public const byte AvcSequenceHeaderPacketType = 0;

    /// <summary>AVCPacketType：NALU。</summary>
    public const byte AvcNaluPacketType = 1;

    /// <summary>AACPacketType：sequence header。</summary>
    public const byte AacSequenceHeaderPacketType = 0;

    /// <summary>视频 FrameType：关键帧。</summary>
    public const int KeyFrameType = 1;

    /// <summary>时间戳扩展字节的位移。</summary>
    public const int TimestampExtendedShift = 24;

    /// <summary>时间戳高 8 位（扩展字节）在头部中的偏移。</summary>
    public const int TimestampExtendedOffset = 7;

    /// <summary>时间戳低 16 位中高 8 位在头部中的偏移。</summary>
    public const int TimestampLowOffset = 4;

    /// <summary>时间戳低 16 位中低 8 位在头部中的偏移。</summary>
    public const int TimestampMidOffset = 5;

    /// <summary>时间戳最大有效值（32 位无符号上限）。</summary>
    public const long MaxTimestampMs = 0xFFFF_FFFFL;
}

/// <summary>
/// FLV 时间戳读写（处理 24 位字段 + 1 字节扩展，扩展到 32 位）。
/// </summary>
public static class FlvTimestamp
{
    /// <summary>从标签头读取时间戳（毫秒）。</summary>
    /// <param name="header">至少 11 字节的标签头。</param>
    /// <returns>时间戳；越界时按 32 位取模。</returns>
    public static long Read(ReadOnlySpan<byte> header)
    {
        if (header.Length < FlvConstants.TagHeaderSize)
        {
            throw new ArgumentException("FLV 标签头长度不足。", nameof(header));
        }

        long low = ((long)header[FlvConstants.TimestampLowOffset] << 16)
            | ((long)header[FlvConstants.TimestampMidOffset] << 8)
            | header[FlvConstants.TimestampMidOffset + 1];
        long extended = (long)header[FlvConstants.TimestampExtendedOffset] << FlvConstants.TimestampExtendedShift;
        return (low | extended) & FlvConstants.MaxTimestampMs;
    }

    /// <summary>把时间戳写入标签头的 4 个字节。</summary>
    /// <param name="header">标签头缓冲区。</param>
    /// <param name="timestampMs">时间戳（毫秒）。</param>
    public static void Write(Span<byte> header, long timestampMs)
    {
        if (header.Length < FlvConstants.TagHeaderSize)
        {
            throw new ArgumentException("FLV 标签头长度不足。", nameof(header));
        }

        long value = timestampMs & FlvConstants.MaxTimestampMs;
        header[FlvConstants.TimestampExtendedOffset] = (byte)((value >> FlvConstants.TimestampExtendedShift) & 0xFF);
        header[FlvConstants.TimestampLowOffset] = (byte)((value >> 16) & 0xFF);
        header[FlvConstants.TimestampMidOffset] = (byte)((value >> 8) & 0xFF);
        header[FlvConstants.TimestampMidOffset + 1] = (byte)(value & 0xFF);
    }
}
