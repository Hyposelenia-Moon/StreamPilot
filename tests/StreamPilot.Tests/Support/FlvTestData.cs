namespace StreamPilot.Tests.Support;

using StreamPilot.Recording.Flv;

/// <summary>
/// FLV 测试数据构造工具：生成最小的合法 FLV 字节流。
/// </summary>
internal static class FlvTestData
{
    /// <summary>构造一个 FLV 文件头（9 字节 + PreviousTagSize0）。</summary>
    /// <returns>13 字节。</returns>
    public static byte[] BuildFileHeader()
    {
        byte[] header = new byte[FlvConstants.FileHeaderSize + FlvConstants.PreviousTagSize0Length];
        header[0] = (byte)'F';
        header[1] = (byte)'L';
        header[2] = (byte)'V';
        header[3] = FlvConstants.Version;
        header[4] = FlvConstants.FlagVideo | FlvConstants.FlagAudio;
        header[8] = FlvConstants.DataOffset;
        return header;
    }

    /// <summary>构造一个 FLV 标签的完整字节（11 字节头 + 载荷 + 4 字节 PreviousTagSize）。</summary>
    /// <param name="type">标签类型。</param>
    /// <param name="timestampMs">时间戳。</param>
    /// <param name="payload">载荷。</param>
    /// <returns>标签字节。</returns>
    public static byte[] BuildTag(FlvTagType type, int timestampMs, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        byte[] buffer = new byte[FlvConstants.TagHeaderSize + payload.Length + FlvConstants.PreviousTagSizeLength];
        buffer[0] = (byte)type;
        buffer[1] = (byte)((payload.Length >> 16) & 0xFF);
        buffer[2] = (byte)((payload.Length >> 8) & 0xFF);
        buffer[3] = (byte)(payload.Length & 0xFF);
        FlvTimestamp.Write(buffer, timestampMs);
        Array.Copy(payload, 0, buffer, FlvConstants.TagHeaderSize, payload.Length);

        int previousTagSize = FlvConstants.TagHeaderSize + payload.Length;
        int sizeOffset = FlvConstants.TagHeaderSize + payload.Length;
        buffer[sizeOffset] = (byte)((previousTagSize >> 24) & 0xFF);
        buffer[sizeOffset + 1] = (byte)((previousTagSize >> 16) & 0xFF);
        buffer[sizeOffset + 2] = (byte)((previousTagSize >> 8) & 0xFF);
        buffer[sizeOffset + 3] = (byte)(previousTagSize & 0xFF);
        return buffer;
    }

    /// <summary>构造一个 AVC sequence header 载荷。</summary>
    /// <returns>载荷字节。</returns>
    public static byte[] BuildAvcSequenceHeaderPayload()
    {
        // FrameType=1(关键帧) | CodecId=7(AVC)；AVCPacketType=0（sequence header）；CompositionTime=0
        return [0x17, 0x00, 0x00, 0x00, 0x00, 0x01, 0x64, 0x00, 0x1F];
    }

    /// <summary>构造一个 AVC NALU 载荷（可作为关键帧使用）。</summary>
    /// <param name="isKeyFrame">是否关键帧。</param>
    /// <returns>载荷字节。</returns>
    public static byte[] BuildAvcNaluPayload(bool isKeyFrame)
    {
        byte frameType = isKeyFrame ? (byte)0x10 : (byte)0x20;
        return [frameType, 0x01, 0x00, 0x00, 0x00, 0x02, 0xAA, 0xBB];
    }

    /// <summary>构造一个 AAC sequence header 载荷。</summary>
    /// <returns>载荷字节。</returns>
    public static byte[] BuildAacSequenceHeaderPayload() => [0xAF, 0x00, 0x12, 0x10];

    /// <summary>构造一个脚本标签载荷（内容无关紧要，仅用于验证搬运）。</summary>
    /// <returns>载荷字节。</returns>
    public static byte[] BuildScriptPayload() => [0x02, 0x00, 0x0A, 0x6F, 0x6E, 0x4D, 0x65, 0x74, 0x61, 0x44, 0x61, 0x74, 0x61];
}
