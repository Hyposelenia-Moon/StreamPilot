namespace StreamPilot.Recording.Mp4;

/// <summary>
/// MP4 盒子写入器：只负责把大端字段与盒子头按 ISO/IEC 14496-12 写进流。
/// </summary>
/// <remarks>
/// 这里刻意不引入任何 MP4 库：录制工程禁止转码、禁止调用外部进程，
/// 而"FLV 原样搬进 MP4"只需要重排容器，自己写盒子比引入依赖更可控。
/// </remarks>
internal static class Mp4BoxWriter
{
    /// <summary>写 8 位无符号整数。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="value">值。</param>
    public static void WriteUInt8(Stream stream, byte value) => stream.WriteByte(value);

    /// <summary>写 16 位大端无符号整数。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="value">值。</param>
    public static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    /// <summary>写 24 位大端无符号整数。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="value">值。</param>
    public static void WriteUInt24(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    /// <summary>写 32 位大端无符号整数。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="value">值。</param>
    public static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    /// <summary>写 64 位大端无符号整数。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="value">值。</param>
    public static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    /// <summary>写 fourcc（4 个 ASCII 字符）。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="fourCc">四字符码，必须正好 4 字节。</param>
    public static void WriteFourCc(Stream stream, string fourCc)
    {
        if (fourCc.Length != 4)
        {
            throw new ArgumentException("fourcc 必须是 4 个字符。", nameof(fourCc));
        }

        Span<byte> buffer = stackalloc byte[4];
        for (int index = 0; index < 4; index++)
        {
            buffer[index] = (byte)fourCc[index];
        }

        stream.Write(buffer);
    }

    /// <summary>写一个盒子的头部（大小 + 类型）。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="fourCc">盒子类型。</param>
    /// <param name="payloadSize">盒子内容长度（不含 8 字节头）。</param>
    public static void WriteBoxHeader(Stream stream, string fourCc, int payloadSize)
    {
        WriteUInt32(stream, (uint)(payloadSize + 8));
        WriteFourCc(stream, fourCc);
    }

    /// <summary>写 full box 的版本与标志位。</summary>
    /// <param name="stream">目标流。</param>
    /// <param name="version">版本号。</param>
    /// <param name="flags">24 位标志位。</param>
    public static void WriteFullBoxHeader(Stream stream, byte version, uint flags)
    {
        WriteUInt8(stream, version);
        WriteUInt24(stream, flags & 0xFFFFFF);
    }

    /// <summary>写矩阵（单位矩阵，MP4 必需字段）。</summary>
    /// <param name="stream">目标流。</param>
    public static void WriteIdentityMatrix(Stream stream)
    {
        uint[] matrix =
        [
            0x00010000, 0, 0,
            0, 0x00010000, 0,
            0, 0, 0x40000000,
        ];
        foreach (uint value in matrix)
        {
            WriteUInt32(stream, value);
        }
    }

    /// <summary>把内容写进临时缓冲后再整体落盘，用于需要先算长度的盒子（如 moof）。</summary>
    /// <param name="build">写入缓冲的回调。</param>
    /// <returns>盒子字节。</returns>
    public static byte[] Build(Action<Stream> build)
    {
        using MemoryStream buffer = new();
        build(buffer);
        return buffer.ToArray();
    }
}
