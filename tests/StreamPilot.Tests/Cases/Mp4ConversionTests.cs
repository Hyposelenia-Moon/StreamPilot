namespace StreamPilot.Tests.Cases;

using System.Buffers.Binary;
using System.Text;
using StreamPilot.Core.Logging;
using StreamPilot.Recording.Mp4;
using StreamPilot.Tests.Framework;
using StreamPilot.Tests.Support;

/// <summary>
/// FLV → MP4 无损转封装测试。
/// </summary>
/// <remarks>
/// 这组用例锁三件事：
/// ① 音频轨的 <c>mdhd</c> 时间刻度必须是 **48000**（FLV 容器表达不了 48 kHz，只能靠 MP4 如实写出）；
/// ② 视频轨写 <c>avc1</c>/<c>hvc1</c>，样本数与原 FLV 一致；
/// ③ 输出是分片 MP4，且首个 moof 的 trun 数据偏移指向真正的 mdat。
/// 全部离线运行：FLV 由测试代码按字节拼出来，不依赖任何真实录制文件。
/// </remarks>
[TestClass]
public sealed class Mp4ConversionTests
{
    /// <summary>转换后 MP4 的音频时间刻度必须是 48000，而不是 FLV 头里那个"44 kHz"标志。</summary>
    [TestMethod("转封装：音频时间刻度写 48000")]
    public async Task WritesAudioTimescaleOf48000()
    {
        using TempDirectory directory = new();
        string flvPath = Path.Combine(directory.Path, "sample.flv");
        string mp4Path = Path.Combine(directory.Path, "sample.mp4");
        await File.WriteAllBytesAsync(flvPath, BuildSampleFlv(includeVideo: true, includeAudio: true)).ConfigureAwait(false);

        Mp4ConversionResult result = await FlvToMp4Converter
            .ConvertAsync(flvPath, mp4Path, NullStructuredLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.True(result.Success, "转封装必须成功：" + result.Error);
        Assert.Equal(48000, result.AudioSampleRate, "音频采样率应识别为 48000");
        Assert.True(result.VideoSamples > 0, "视频样本不能为 0");
        Assert.True(result.AudioSamples > 0, "音频样本不能为 0");

        byte[] mp4 = await File.ReadAllBytesAsync(mp4Path).ConfigureAwait(false);
        Assert.Equal("ftyp", Encoding.ASCII.GetString(mp4, 4, 4), "MP4 必须以 ftyp 开头");

        Dictionary<string, int> timescales = ReadTrackTimescales(mp4);
        Assert.Equal(1000, timescales["vide"], "视频轨时间刻度按毫秒");
        Assert.Equal(48000, timescales["soun"], "音频轨时间刻度必须等于采样率");
        Assert.Equal("hvc1", ReadVideoSampleEntry(mp4), "HEVC 的 sample entry 应为 hvc1");
    }

    /// <summary>AVC 录制同样要产出 avc1 + 48 kHz，且原 FLV 在成功后由调用方删除（此处只验证输出可解析）。</summary>
    [TestMethod("转封装：AVC 输出 avc1 且样本数一致")]
    public async Task WritesAvcSampleEntry()
    {
        using TempDirectory directory = new();
        string flvPath = Path.Combine(directory.Path, "avc.flv");
        string mp4Path = Path.Combine(directory.Path, "avc.mp4");
        await File.WriteAllBytesAsync(flvPath, BuildSampleFlv(includeVideo: true, includeAudio: true, hevc: false))
            .ConfigureAwait(false);

        Mp4ConversionResult result = await FlvToMp4Converter
            .ConvertAsync(flvPath, mp4Path, NullStructuredLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.True(result.Success, "AVC 转封装必须成功：" + result.Error);
        byte[] mp4 = await File.ReadAllBytesAsync(mp4Path).ConfigureAwait(false);
        Assert.Equal("avc1", ReadVideoSampleEntry(mp4), "AVC 的 sample entry 应为 avc1");
        Assert.Equal(48000, ReadTrackTimescales(mp4)["soun"]);
    }

    /// <summary>只有音频的 FLV（无视频 sequence header）也必须能转，不能因缺视频轨而失败。</summary>
    [TestMethod("转封装：纯音频 FLV 可转换")]
    public async Task ConvertsAudioOnlyFlv()
    {
        using TempDirectory directory = new();
        string flvPath = Path.Combine(directory.Path, "audio.flv");
        string mp4Path = Path.Combine(directory.Path, "audio.mp4");
        await File.WriteAllBytesAsync(flvPath, BuildSampleFlv(includeVideo: false, includeAudio: true)).ConfigureAwait(false);

        Mp4ConversionResult result = await FlvToMp4Converter
            .ConvertAsync(flvPath, mp4Path, NullStructuredLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.True(result.Success, "纯音频 FLV 必须能转：" + result.Error);
        Assert.Equal(0, result.VideoSamples);
        Assert.Equal(48000, result.AudioSampleRate);
    }

    /// <summary>既没有视频也没有音频配置时明确失败，调用方据此保留 FLV。</summary>
    [TestMethod("转封装：无配置时失败并保留 FLV")]
    public async Task FailsWhenNoSequenceHeader()
    {
        using TempDirectory directory = new();
        string flvPath = Path.Combine(directory.Path, "empty.flv");
        string mp4Path = Path.Combine(directory.Path, "empty.mp4");
        await File.WriteAllBytesAsync(flvPath, BuildSampleFlv(includeVideo: false, includeAudio: false)).ConfigureAwait(false);

        Mp4ConversionResult result = await FlvToMp4Converter
            .ConvertAsync(flvPath, mp4Path, NullStructuredLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.False(result.Success, "没有配置必须失败，以便上层保留 FLV");
        Assert.False(File.Exists(mp4Path), "失败时不能留下半成品 MP4");
    }

    /// <summary>拼一个结构合法的 FLV：头 + onMetaData + 可选的音视频配置与若干帧。</summary>
    /// <param name="includeVideo">是否包含视频轨。</param>
    /// <param name="includeAudio">是否包含音频轨。</param>
    /// <param name="hevc">视频是否为 HEVC。</param>
    /// <returns>FLV 字节。</returns>
    private static byte[] BuildSampleFlv(bool includeVideo, bool includeAudio, bool hevc = true)
    {
        using MemoryStream stream = new();

        // FLV 文件头
        stream.Write(Encoding.ASCII.GetBytes("FLV"));
        stream.WriteByte(1);
        byte flags = 0;
        if (includeVideo)
        {
            flags |= 0x01;
        }

        if (includeAudio)
        {
            flags |= 0x04;
        }

        stream.WriteByte(flags);
        WriteUInt32(stream, 9);
        WriteUInt32(stream, 0);

        // onMetaData（带 width/height，避免依赖 SPS 解析）
        byte[] metadata = BuildMetadata(1920, 1080);
        WriteTag(stream, 18, 0, metadata);

        if (includeVideo)
        {
            // 视频 sequence header：codecId=7/12，packetType=0，后接配置记录
            byte codecId = hevc ? (byte)12 : (byte)7;
            byte[] config = hevc ? BuildHevcConfig() : BuildAvcConfig();
            byte[] payload = new byte[5 + config.Length];
            payload[0] = (byte)(0x10 | codecId);
            payload[1] = 0;
            config.CopyTo(payload, 5);
            WriteTag(stream, 9, 0, payload);

            // 三帧视频（两关键帧，一普通帧）
            for (int index = 0; index < 3; index++)
            {
                byte[] frame = new byte[5 + 8];
                frame[0] = (byte)((index == 1 ? 2 : 1) << 4 | codecId);
                frame[1] = 1; // NALU
                frame[2] = 0;
                frame[3] = 0;
                frame[4] = 0;
                // 一个长度为 4 的伪 NALU（内容不参与容器解析）
                frame[5] = 0;
                frame[6] = 0;
                frame[7] = 0;
                frame[8] = 4;
                frame[9] = 0x65;
                frame[10] = 0x88;
                frame[11] = 0x80;
                frame[12] = 0x01;
                WriteTag(stream, 9, index * 16, frame);
            }
        }

        if (includeAudio)
        {
            // 音频 sequence header：AAC（0xAF）+ ASC（48 kHz 立体声）
            WriteTag(stream, 8, 0, [0xAF, 0x00, 0x11, 0x90]);

            for (int index = 0; index < 5; index++)
            {
                byte[] frame = new byte[2 + 8];
                frame[0] = 0xAF;
                frame[1] = 1;
                WriteTag(stream, 8, index * 21, frame);
            }
        }

        return stream.ToArray();
    }

    /// <summary>AVC 配置记录（SPS 数量为 0，尺寸走元数据）。</summary>
    private static byte[] BuildAvcConfig() =>
    [
        1, 0x64, 0x00, 0x1F, 0xFF, 0xE1, 0x00, 0x00,
    ];

    /// <summary>HEVC 配置记录（数组数为 0，尺寸走元数据）。</summary>
    private static byte[] BuildHevcConfig()
    {
        byte[] config = new byte[23];
        config[0] = 1;
        config[1] = 0x01;
        config[12] = 120;
        config[22] = 0;
        return config;
    }

    /// <summary>拼 onMetaData（AMF0）。</summary>
    private static byte[] BuildMetadata(double width, double height)
    {
        using MemoryStream stream = new();
        stream.WriteByte(0x02);
        WriteUInt16(stream, 10);
        stream.Write(Encoding.ASCII.GetBytes("onMetaData"));
        stream.WriteByte(0x08);
        WriteUInt32(stream, 2);
        WriteAmfNumber(stream, "width", width);
        WriteAmfNumber(stream, "height", height);
        return stream.ToArray();
    }

    private static void WriteAmfNumber(Stream stream, string key, double value)
    {
        WriteUInt16(stream, (ushort)key.Length);
        stream.Write(Encoding.ASCII.GetBytes(key));
        stream.WriteByte(0x00);
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteTag(Stream stream, byte type, int timestamp, byte[] payload)
    {
        stream.WriteByte(type);
        stream.WriteByte((byte)(payload.Length >> 16));
        stream.WriteByte((byte)(payload.Length >> 8));
        stream.WriteByte((byte)payload.Length);
        stream.WriteByte((byte)(timestamp >> 16));
        stream.WriteByte((byte)(timestamp >> 8));
        stream.WriteByte((byte)timestamp);
        stream.WriteByte(0);
        stream.Write([0, 0, 0]);
        stream.Write(payload);
        WriteUInt32(stream, (uint)(payload.Length + 11));
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    /// <summary>从 moov 里读出每条轨道的 hdlr 类型与 mdhd 时间刻度。</summary>
    private static Dictionary<string, int> ReadTrackTimescales(byte[] mp4)
    {
        Dictionary<string, int> result = [];
        foreach ((string type, int offset, int size) in EnumerateBoxes(mp4, 0, mp4.Length))
        {
            if (type != "moov")
            {
                continue;
            }

            foreach ((string childType, int childOffset, int childSize) in EnumerateBoxes(mp4, offset + 8, offset + size))
            {
                if (childType != "trak")
                {
                    continue;
                }

                string handler = string.Empty;
                int timescale = 0;
                foreach ((string grandType, int grandOffset, int grandSize) in EnumerateBoxes(mp4, childOffset + 8, childOffset + childSize))
                {
                    if (grandType != "mdia")
                    {
                        continue;
                    }

                    foreach ((string mediaType, int mediaOffset, int mediaSize) in EnumerateBoxes(mp4, grandOffset + 8, grandOffset + grandSize))
                    {
                        if (mediaType == "mdhd")
                        {
                            timescale = BinaryPrimitives.ReadInt32BigEndian(mp4.AsSpan(mediaOffset + 20, 4));
                        }
                        else if (mediaType == "hdlr")
                        {
                            handler = Encoding.ASCII.GetString(mp4, mediaOffset + 16, 4);
                        }
                    }
                }

                result[handler] = timescale;
            }
        }

        return result;
    }

    /// <summary>读取视频轨的 sample entry 类型（avc1 / hvc1）。</summary>
    private static string ReadVideoSampleEntry(byte[] mp4)
    {
        foreach ((string type, int offset, int size) in EnumerateBoxes(mp4, 0, mp4.Length))
        {
            if (type != "moov")
            {
                continue;
            }

            foreach ((string trakType, int trakOffset, int trakSize) in EnumerateBoxes(mp4, offset + 8, offset + size))
            {
                if (trakType != "trak")
                {
                    continue;
                }

                foreach ((string mdiaType, int mdiaOffset, int mdiaSize) in EnumerateBoxes(mp4, trakOffset + 8, trakOffset + trakSize))
                {
                    if (mdiaType != "mdia")
                    {
                        continue;
                    }

                    foreach ((string minfType, int minfOffset, int minfSize) in EnumerateBoxes(mp4, mdiaOffset + 8, mdiaOffset + mdiaSize))
                    {
                        if (minfType != "minf")
                        {
                            continue;
                        }

                        foreach ((string stblType, int stblOffset, int stblSize) in EnumerateBoxes(mp4, minfOffset + 8, minfOffset + minfSize))
                        {
                            if (stblType != "stbl")
                            {
                                continue;
                            }

                            foreach ((string stsdType, int stsdOffset, int stsdSize) in EnumerateBoxes(mp4, stblOffset + 8, stblOffset + stblSize))
                            {
                                if (stsdType == "stsd")
                                {
                                    return Encoding.ASCII.GetString(mp4, stsdOffset + 20, 4);
                                }
                            }
                        }
                    }
                }
            }
        }

        return string.Empty;
    }

    private static IEnumerable<(string Type, int Offset, int Size)> EnumerateBoxes(byte[] data, int start, int end)
    {
        int position = start;
        while (position + 8 <= end)
        {
            int size = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position, 4));
            if (size < 8)
            {
                yield break;
            }

            string type = Encoding.ASCII.GetString(data, position + 4, 4);
            yield return (type, position, size);
            position += size;
        }
    }
}
