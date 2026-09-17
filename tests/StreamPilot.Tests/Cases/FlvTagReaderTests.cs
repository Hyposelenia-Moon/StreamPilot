namespace StreamPilot.Tests.Cases;

using StreamPilot.Recording.Flv;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// FLV 标签解析与时间戳处理的单元测试。
/// </summary>
[TestClass]
public sealed class FlvTagReaderTests
{
    /// <summary>正常解析多个标签并保持顺序与载荷。</summary>
    [TestMethod("FLV 解析：正常标签序列")]
    public async Task ParsesTagSequence()
    {
        byte[] header = FlvTestData.BuildFileHeader();
        byte[] first = FlvTestData.BuildTag(FlvTagType.Script, 0, FlvTestData.BuildScriptPayload());
        byte[] second = FlvTestData.BuildTag(FlvTagType.Video, 1000, FlvTestData.BuildAvcSequenceHeaderPayload());
        byte[] third = FlvTestData.BuildTag(FlvTagType.Audio, 1200, FlvTestData.BuildAacSequenceHeaderPayload());
        using MemoryStream stream = new([.. header, .. first, .. second, .. third]);
        FlvTagReader reader = new(stream);

        FlvTag? tag1 = await reader.ReadTagAsync(CancellationToken.None);
        FlvTag? tag2 = await reader.ReadTagAsync(CancellationToken.None);
        FlvTag? tag3 = await reader.ReadTagAsync(CancellationToken.None);
        FlvTag? end = await reader.ReadTagAsync(CancellationToken.None);

        Assert.NotNull(tag1);
        Assert.Equal(FlvTagType.Script, tag1!.Type);
        Assert.Equal(0, tag1.TimestampMs);
        Assert.NotNull(tag2);
        Assert.Equal(1000, tag2!.TimestampMs);
        Assert.True(tag2.IsSequenceHeader());
        Assert.NotNull(tag3);
        Assert.True(tag3!.IsSequenceHeader());
        Assert.Null(end);
    }

    /// <summary>关键帧判定：AVC NALU + FrameType=1。</summary>
    [TestMethod("FLV 解析：关键帧判定")]
    public async Task DetectsKeyFrames()
    {
        byte[] header = FlvTestData.BuildFileHeader();
        byte[] key = FlvTestData.BuildTag(FlvTagType.Video, 10, FlvTestData.BuildAvcNaluPayload(isKeyFrame: true));
        byte[] delta = FlvTestData.BuildTag(FlvTagType.Video, 20, FlvTestData.BuildAvcNaluPayload(isKeyFrame: false));
        byte[] sequence = FlvTestData.BuildTag(FlvTagType.Video, 0, FlvTestData.BuildAvcSequenceHeaderPayload());
        using MemoryStream stream = new([.. header, .. key, .. delta, .. sequence]);
        FlvTagReader reader = new(stream);

        FlvTag? keyTag = await reader.ReadTagAsync(CancellationToken.None);
        FlvTag? deltaTag = await reader.ReadTagAsync(CancellationToken.None);
        FlvTag? sequenceTag = await reader.ReadTagAsync(CancellationToken.None);

        Assert.True(keyTag!.IsVideoKeyFrame());
        Assert.False(deltaTag!.IsVideoKeyFrame());
        Assert.False(sequenceTag!.IsVideoKeyFrame());
    }

    /// <summary>非法 FLV 签名被拒绝（不吞异常）。</summary>
    [TestMethod("FLV 解析：签名不匹配抛异常")]
    public async Task RejectsInvalidSignature()
    {
        byte[] garbage = new byte[FlvConstants.FileHeaderSize + FlvConstants.PreviousTagSize0Length];
        garbage[0] = (byte)'X';
        using MemoryStream stream = new(garbage);

        IOException exception = await Assert.ThrowsAsync<IOException>(() =>
        {
            FlvTagReader reader = new(stream);
            return reader.ReadTagAsync(CancellationToken.None);
        });

        Assert.Contains("FLV", exception.Message);
    }

    /// <summary>未知标签类型被拒绝。</summary>
    [TestMethod("FLV 解析：未知标签类型抛异常")]
    public async Task RejectsUnknownTagType()
    {
        byte[] header = FlvTestData.BuildFileHeader();
        byte[] bogus = FlvTestData.BuildTag((FlvTagType)77, 0, [0x00]);
        using MemoryStream stream = new([.. header, .. bogus]);
        FlvTagReader reader = new(stream);

        await Assert.ThrowsAsync<IOException>(() => reader.ReadTagAsync(CancellationToken.None));
    }

    /// <summary>载荷不完整（直播中断）抛异常而不是静默截断。</summary>
    [TestMethod("FLV 解析：载荷截断抛异常")]
    public async Task RejectsTruncatedPayload()
    {
        byte[] header = FlvTestData.BuildFileHeader();
        byte[] full = FlvTestData.BuildTag(FlvTagType.Video, 0, FlvTestData.BuildAvcNaluPayload(true));
        byte[] truncated = full[..(full.Length - 5)];
        using MemoryStream stream = new([.. header, .. truncated]);
        FlvTagReader reader = new(stream);

        await Assert.ThrowsAsync<IOException>(() => reader.ReadTagAsync(CancellationToken.None));
    }

    /// <summary>时间戳 24 位字段 + 扩展字节的读写一致性（含边界值）。</summary>
    [TestMethod("FLV 时间戳：边界值读写一致")]
    public void TimestampRoundTrip()
    {
        long[] values = [0, 1, 0x7FFFFF, 0xFFFFFF, 0x1000000, 0x00ABCDEF, 0xFFFFFFFF];
        foreach (long value in values)
        {
            byte[] header = new byte[FlvConstants.TagHeaderSize];
            FlvTimestamp.Write(header, value);
            Assert.Equal(value, FlvTimestamp.Read(header), "#" + value);
        }
    }

    /// <summary>
    /// 按 FLV 规范的字面量字节验证字段位置：
    /// 时间戳低 24 位在偏移 4-6，扩展字节在偏移 7（type 1 + dataSize 3 + ts 3 + tsExt 1 + streamId 3）。
    /// </summary>
    [TestMethod("FLV 时间戳：字段偏移符合规范字节布局")]
    public void TimestampLayoutMatchesSpec()
    {
        // 1000 ms = 0x0003E8 → header[4]=0x00, header[5]=0x03, header[6]=0xE8, header[7]=0x00
        byte[] specHeader = new byte[FlvConstants.TagHeaderSize];
        specHeader[4] = 0x00;
        specHeader[5] = 0x03;
        specHeader[6] = 0xE8;
        specHeader[7] = 0x00;
        Assert.Equal(1000, FlvTimestamp.Read(specHeader));

        // 扩展字节参与高 8 位：0x01 0003E8 = 16778216
        byte[] extendedHeader = new byte[FlvConstants.TagHeaderSize];
        extendedHeader[4] = 0x00;
        extendedHeader[5] = 0x03;
        extendedHeader[6] = 0xE8;
        extendedHeader[7] = 0x01;
        Assert.Equal(0x0100_03E8, FlvTimestamp.Read(extendedHeader));

        // 写回时同样落在偏移 4-7。
        byte[] written = new byte[FlvConstants.TagHeaderSize];
        FlvTimestamp.Write(written, 0x0100_03E8);
        Assert.Equal(0x00, written[4]);
        Assert.Equal(0x03, written[5]);
        Assert.Equal(0xE8, written[6]);
        Assert.Equal(0x01, written[7]);
    }

    /// <summary>时间戳写入时按 32 位截断。</summary>
    [TestMethod("FLV 时间戳：超出 32 位按模截断")]
    public void TimestampMasks()
    {
        byte[] header = new byte[FlvConstants.TagHeaderSize];
        FlvTimestamp.Write(header, 0x1_0000_0005L);
        Assert.Equal(5, FlvTimestamp.Read(header));
    }

    /// <summary>标签头长度不足时抛出参数异常。</summary>
    [TestMethod("FLV 时间戳：缓冲区过短抛异常")]
    public void TimestampRejectsShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => FlvTimestamp.Read(new byte[4]));
        Assert.Throws<ArgumentException>(() => FlvTimestamp.Write(new byte[4], 1));
    }
}
