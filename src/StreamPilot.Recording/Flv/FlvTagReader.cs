namespace StreamPilot.Recording.Flv;

/// <summary>
/// FLV 标签流读取器：从字节流中逐个解析标签。
/// </summary>
/// <remarks>
/// 只负责"解析 + 原样返回载荷"，不做任何编码相关处理。
/// 遇到损坏的头部（签名不符 / DataSize 非法）时抛出 <see cref="IOException"/>，
/// 由录制会话决定是重连还是结束（不吞异常）。
/// </remarks>
public sealed class FlvTagReader
{
    /// <summary>单个标签载荷的合理上限（16 MiB），用于识别损坏的 DataSize。</summary>
    private const int MaxPlausiblePayloadSize = 16 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _headerBuffer = new byte[FlvConstants.TagHeaderSize];

    /// <summary>初始化读取器并可选地校验 FLV 文件头。</summary>
    /// <param name="stream">可读流。</param>
    /// <param name="validateFlvHeader">是否要求流以合法 FLV 文件头开始。</param>
    public FlvTagReader(Stream stream, bool validateFlvHeader = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        if (validateFlvHeader)
        {
            SkipFileHeader();
        }
    }

    /// <summary>读取下一个标签；流结束时返回 <see langword="null"/>。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标签，或流结束时的 <see langword="null"/>。</returns>
    /// <exception cref="IOException">流内容不是合法 FLV 时抛出。</exception>
    public async Task<FlvTag?> ReadTagAsync(CancellationToken cancellationToken)
    {
        if (!await ReadExactlyOrEofAsync(_headerBuffer, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        byte type = _headerBuffer[0];
        if (type is not ((byte)FlvTagType.Audio) and not ((byte)FlvTagType.Video) and not ((byte)FlvTagType.Script))
        {
            throw new IOException($"未知的 FLV 标签类型：{type}。");
        }

        int dataSize = (_headerBuffer[1] << 16) | (_headerBuffer[2] << 8) | _headerBuffer[3];
        if (dataSize is < 0 or > MaxPlausiblePayloadSize)
        {
            throw new IOException($"非法的 FLV 标签长度：{dataSize}。");
        }

        byte[] payload = new byte[dataSize];
        if (dataSize > 0 && !await ReadExactlyOrEofAsync(payload, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("FLV 标签载荷不完整（直播流在标签中途结束）。");
        }

        byte[] previousTagSize = new byte[FlvConstants.PreviousTagSizeLength];
        if (!await ReadExactlyOrEofAsync(previousTagSize, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("FLV 标签缺少 PreviousTagSize 字段。");
        }

        return new FlvTag
        {
            Type = (FlvTagType)type,
            TimestampMs = (int)FlvTimestamp.Read(_headerBuffer),
            DataSize = dataSize,
            Payload = payload,
        };
    }

    private async Task<bool> ReadExactlyOrEofAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await _stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new IOException("直播流在读取过程中结束（数据不完整）。");
            }

            offset += read;
        }

        return true;
    }

    private void SkipFileHeader()
    {
        byte[] header = new byte[FlvConstants.FileHeaderSize + FlvConstants.PreviousTagSize0Length];
        int offset = 0;
        while (offset < header.Length)
        {
            int read = _stream.Read(header, offset, header.Length - offset);
            if (read == 0)
            {
                throw new IOException("直播流在 FLV 文件头阶段结束。");
            }

            offset += read;
        }

        if (header[0] != FlvConstants.Signature[0]
            || header[1] != FlvConstants.Signature[1]
            || header[2] != FlvConstants.Signature[2])
        {
            throw new IOException("数据不是 FLV 格式（签名不匹配）。");
        }
    }
}
