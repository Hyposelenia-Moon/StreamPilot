namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Logging;
using StreamPilot.Recording;
using StreamPilot.Recording.Flv;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// <see cref="FlvSegmentWriter"/> 的字节级写入测试（验证"不转码、只搬运"与时间戳重定基）。
/// </summary>
[TestClass]
public sealed class FlvSegmentWriterTests
{
    /// <summary>新分片必须写入完整 FLV 文件头。</summary>
    [TestMethod("FLV 分片：文件头字节正确")]
    public async Task WritesFileHeader()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "seg-000.flv");
        await using (FlvSegmentWriter writer = new(path, new SegmentPolicy(), NullStructuredLogger.Instance))
        {
            await writer.WriteTagAsync(
                Tag(FlvTagType.Video, 0, FlvTestData.BuildAvcSequenceHeaderPayload()),
                CancellationToken.None);
        }

        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.SequenceEqual([(byte)'F', (byte)'L', (byte)'V', 0x01, 0x05, 0x00, 0x00, 0x00, 0x09], bytes.AsSpan(0, 9));
        Assert.SequenceEqual([0x00, 0x00, 0x00, 0x00], bytes.AsSpan(9, 4));
    }

    /// <summary>载荷逐字节原样写入（不转码的直接证据）。</summary>
    [TestMethod("FLV 分片：载荷逐字节一致")]
    public async Task CopiesPayloadVerbatim()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "seg-000.flv");
        byte[] payload = [0x27, 0x01, 0x00, 0x00, 0x00, 0x02, 0xDE, 0xAD, 0xBE, 0xEF];

        await using (FlvSegmentWriter writer = new(path, new SegmentPolicy(), NullStructuredLogger.Instance))
        {
            await writer.WriteTagAsync(Tag(FlvTagType.Video, 500, payload), CancellationToken.None);
        }

        byte[] bytes = await File.ReadAllBytesAsync(path);
        int payloadOffset = FlvConstants.FileHeaderSize + FlvConstants.PreviousTagSize0Length + FlvConstants.TagHeaderSize;
        Assert.SequenceEqual(payload, bytes.AsSpan(payloadOffset, payload.Length));

        // PreviousTagSize = 11 + 载荷长度
        int sizeOffset = payloadOffset + payload.Length;
        int stored = (bytes[sizeOffset] << 24) | (bytes[sizeOffset + 1] << 16) | (bytes[sizeOffset + 2] << 8) | bytes[sizeOffset + 3];
        Assert.Equal(FlvConstants.TagHeaderSize + payload.Length, stored);
    }

    /// <summary>时间戳按分片首帧重定基为 0 起始。</summary>
    [TestMethod("FLV 分片：时间戳重定基")]
    public async Task RebasesTimestamps()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "seg-000.flv");

        await using (FlvSegmentWriter writer = new(path, new SegmentPolicy(), NullStructuredLogger.Instance))
        {
            await writer.WriteTagAsync(Tag(FlvTagType.Video, 60_000, FlvTestData.BuildAvcNaluPayload(true)), CancellationToken.None);
            await writer.WriteTagAsync(Tag(FlvTagType.Video, 60_500, FlvTestData.BuildAvcNaluPayload(false)), CancellationToken.None);
        }

        FlvTag[] tags = await ReadTagsAsync(path);
        Assert.Equal(2, tags.Length);
        Assert.Equal(0, tags[0].TimestampMs);
        Assert.Equal(500, tags[1].TimestampMs);
    }

    /// <summary>先写入的 sequence header 与 onMetaData 会被缓存并重放到新分片，使分片可独立播放。</summary>
    [TestMethod("FLV 分片：序列头重放")]
    public async Task ReplaysSequenceHeaders()
    {
        using TempDirectory temp = new();
        string firstPath = Path.Combine(temp.Path, "seg-000.flv");
        string secondPath = Path.Combine(temp.Path, "seg-001.flv");
        FlvTag script = Tag(FlvTagType.Script, 0, FlvTestData.BuildScriptPayload());
        FlvTag videoHeader = Tag(FlvTagType.Video, 0, FlvTestData.BuildAvcSequenceHeaderPayload());
        FlvTag audioHeader = Tag(FlvTagType.Audio, 0, FlvTestData.BuildAacSequenceHeaderPayload());

        await using (FlvSegmentWriter writer = new(firstPath, new SegmentPolicy(), NullStructuredLogger.Instance))
        {
            writer.RememberScriptTag(script);
            await writer.WriteSequenceHeadersAsync(videoHeader, audioHeader, CancellationToken.None);
            await writer.WriteTagAsync(Tag(FlvTagType.Video, 100, FlvTestData.BuildAvcNaluPayload(true)), CancellationToken.None);
        }

        await using (FlvSegmentWriter writer = new(secondPath, new SegmentPolicy(), NullStructuredLogger.Instance))
        {
            writer.RememberScriptTag(script);
            await writer.WriteSequenceHeadersAsync(videoHeader, audioHeader, CancellationToken.None);
        }

        FlvTag[] tags = await ReadTagsAsync(secondPath);
        Assert.Equal(3, tags.Length);
        Assert.Equal(FlvTagType.Script, tags[0].Type);
        Assert.True(tags[1].IsSequenceHeader());
        Assert.True(tags[2].IsSequenceHeader());
    }

    /// <summary>分片元数据：字节数与时长。</summary>
    [TestMethod("FLV 分片：完成时返回字节数与时长")]
    public async Task CompletesWithMetadata()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "seg-003.flv");

        await using FlvSegmentWriter writer = new(path, new SegmentPolicy(), NullStructuredLogger.Instance);
        await writer.WriteTagAsync(Tag(FlvTagType.Video, 1_000, FlvTestData.BuildAvcNaluPayload(true)), CancellationToken.None);
        await writer.WriteTagAsync(Tag(FlvTagType.Video, 4_000, FlvTestData.BuildAvcNaluPayload(false)), CancellationToken.None);

        Core.Models.RecordingSegment segment = await writer.CompleteAsync(CancellationToken.None);
        Assert.Equal(3, segment.Index);
        Assert.Equal("seg-003.flv", segment.FileName);
        Assert.EqualDouble(3.0, segment.DurationSeconds, 1e-6);
        Assert.Equal(new FileInfo(path).Length, segment.Bytes);
    }

    /// <summary>写入已释放的写入器会抛出 ObjectDisposedException。</summary>
    [TestMethod("FLV 分片：释放后写入被拒绝")]
    public async Task RejectsWriteAfterDispose()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "seg-000.flv");
        FlvSegmentWriter writer = new(path, new SegmentPolicy(), NullStructuredLogger.Instance);
        await writer.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            writer.WriteTagAsync(Tag(FlvTagType.Video, 0, FlvTestData.BuildAvcNaluPayload(true)), CancellationToken.None));
    }

    private static FlvTag Tag(FlvTagType type, int timestampMs, byte[] payload) => new()
    {
        Type = type,
        TimestampMs = timestampMs,
        DataSize = payload.Length,
        Payload = payload,
    };

    private static async Task<FlvTag[]> ReadTagsAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        FlvTagReader reader = new(stream);
        List<FlvTag> tags = [];
        while (true)
        {
            FlvTag? tag = await reader.ReadTagAsync(CancellationToken.None);
            if (tag is null)
            {
                break;
            }

            tags.Add(tag);
        }

        return [.. tags];
    }
}
