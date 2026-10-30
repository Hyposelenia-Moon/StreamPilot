namespace StreamPilot.Recording.Mp4;

using System.Buffers.Binary;
using System.Text;
using StreamPilot.Core.Logging;
using StreamPilot.Recording.Flv;

/// <summary>
/// FLV 无损转封装为 MP4（fragmented MP4）的结果。
/// </summary>
/// <param name="Success">是否成功。</param>
/// <param name="OutputBytes">输出文件字节数。</param>
/// <param name="VideoSamples">写入的视频样本数。</param>
/// <param name="AudioSamples">写入的音频样本数。</param>
/// <param name="AudioSampleRate">音频采样率（Hz），未检测到时为 0。</param>
/// <param name="Error">失败原因（成功时为 <see langword="null"/>）。</param>
public sealed record Mp4ConversionResult(
    bool Success,
    long OutputBytes,
    int VideoSamples,
    int AudioSamples,
    int AudioSampleRate,
    string? Error);

/// <summary>
/// 把录制好的 FLV 原样搬进 MP4 容器（不转码、不重采样、不调用外部进程）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要换容器：FLV 的音频头只有 5.5/11/22/44 kHz 四个取值，**表达不了 48000 Hz**。
/// B站推的 AAC 实际就是 48 kHz（ASC 的 samplingFrequencyIndex=3），但 FLV 里只能写"44 kHz"标志位，
/// 于是转封装工具、播放器信息面板都会显示 44100——这就是"只能录到 44.1k"的真正原因。
/// MP4 的 <c>mdhd</c> 直接记采样率，48 kHz 可以如实写出。
/// </para>
/// <para>
/// 输出为分片 MP4（ftyp + moov + 若干 moof/mdat）：录制中断时已写入的分片仍可播放，
/// 不像传统 MP4 那样必须等 moov 落盘才能打开。
/// </para>
/// </remarks>
public static class FlvToMp4Converter
{
    /// <summary>视频轨时间刻度（与 FLV 的毫秒时间戳一一对应，避免取整误差）。</summary>
    private const uint VideoTimescale = 1000;

    /// <summary>配置阶段最多缓存多少个媒体标签。</summary>
    private const int MaxPendingTags = 500;

    /// <summary>配置阶段最多缓存多少字节媒体数据（约 16 MB）。</summary>
    private const int MaxPendingBytes = 16 * 1024 * 1024;

    /// <summary>每隔多少毫秒落一个分片。</summary>
    private const int FragmentMilliseconds = 2000;

    /// <summary>AAC 一帧固定 1024 个采样（LC 编码）。</summary>
    private const int AacSamplesPerFrame = 1024;

    /// <summary>把 FLV 转成 MP4。</summary>
    /// <param name="flvPath">源 FLV 路径。</param>
    /// <param name="mp4Path">目标 MP4 路径（先写 .part，成功后再改名）。</param>
    /// <param name="logger">结构化日志。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>转换结果。</returns>
    public static async Task<Mp4ConversionResult> ConvertAsync(
        string flvPath,
        string mp4Path,
        IStructuredLogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string temporaryPath = mp4Path + ".part";
        try
        {
            await using FileStream input = new(flvPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            FlvTagReader reader = new(input);

            VideoConfig? video = null;
            AudioConfig? audio = null;
            int metadataWidth = 0;
            int metadataHeight = 0;

            // 先读到"配置齐全"为止：sequence header 与 onMetaData 都在文件开头。
            List<FlvTag> pending = [];
            int pendingBytes = 0;
            while (true)
            {
                FlvTag? tag = await reader.ReadTagAsync(cancellationToken).ConfigureAwait(false);
                if (tag is null)
                {
                    break;
                }

                if (tag.Type == FlvTagType.Script)
                {
                    (metadataWidth, metadataHeight) = FlvMetadataReader.ReadDimensions(tag.Payload);
                    continue;
                }

                if (tag.IsSequenceHeader())
                {
                    if (tag.Type == FlvTagType.Video)
                    {
                        video ??= VideoConfig.TryCreate(tag);
                    }
                    else
                    {
                        audio ??= AudioConfig.TryCreate(tag);
                    }

                    if (video is not null && audio is not null)
                    {
                        break;
                    }

                    continue;
                }

                // 媒体标签先缓存：有的流音频 sequence header 会晚于第一批视频帧到达，
                // 但 moov 必须同时拿到音视频配置才能写，所以先攒着（有上限，避免无音频流把整文件读进内存）。
                pending.Add(tag);
                pendingBytes += tag.Payload.Length;
                if (pending.Count >= MaxPendingTags || pendingBytes >= MaxPendingBytes)
                {
                    break;
                }
            }

            if (video is null && audio is null)
            {
                return new Mp4ConversionResult(false, 0, 0, 0, 0, "FLV 里没有可用的音视频配置（sequence header）。");
            }

            if (video is not null && !video.IsSupported)
            {
                return new Mp4ConversionResult(false, 0, 0, 0, 0, $"不支持的视频编码（codecId={video.CodecId}），保留 FLV。");
            }

            (int width, int height) = video is not null ? video.ResolveDimensions(metadataWidth, metadataHeight) : (0, 0);

            long bytes;
            int videoSamples;
            int audioSamples;
            await using (FileStream output = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                WriteInitSegment(output, video, audio, width, height);
                Mp4Writer writer = new(output, video, audio);
                foreach (FlvTag tag in pending)
                {
                    await writer.AddAsync(tag, cancellationToken).ConfigureAwait(false);
                }

                while (true)
                {
                    FlvTag? tag = await reader.ReadTagAsync(cancellationToken).ConfigureAwait(false);
                    if (tag is null)
                    {
                        break;
                    }

                    await writer.AddAsync(tag, cancellationToken).ConfigureAwait(false);
                }

                await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                bytes = output.Length;
                videoSamples = writer.VideoSampleCount;
                audioSamples = writer.AudioSampleCount;
            }

            // 必须先关掉输出流再改名：Windows 上"移动一个仍被自己打开的文件"会报共享冲突。
            File.Move(temporaryPath, mp4Path, overwrite: true);
            {
                logger.Info("Recording.Mp4", "转封装完成。", new Dictionary<string, object?>
                {
                    ["source"] = Path.GetFileName(flvPath),
                    ["target"] = Path.GetFileName(mp4Path),
                    ["bytes"] = bytes,
                    ["videoSamples"] = videoSamples,
                    ["audioSamples"] = audioSamples,
                    ["audioSampleRate"] = audio?.SampleRate ?? 0,
                    ["width"] = width,
                    ["height"] = height,
                });

                return new Mp4ConversionResult(
                    true,
                    bytes,
                    videoSamples,
                    audioSamples,
                    audio?.SampleRate ?? 0,
                    null);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            logger.LogError(LogLevel.Warn, "Recording.Mp4", "转封装失败，保留 FLV。", exception);
            TryDelete(temporaryPath);
            return new Mp4ConversionResult(false, 0, 0, 0, 0, exception.Message);
        }
    }

    /// <summary>删除可能残留的半成品文件。</summary>
    /// <param name="path">文件路径。</param>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响主流程。
        }
    }

    /// <summary>写 ftyp + moov（初始化段）。</summary>
    private static void WriteInitSegment(Stream output, VideoConfig? video, AudioConfig? audio, int width, int height)
    {
        // ftyp
        Mp4BoxWriter.WriteBoxHeader(output, "ftyp", 4 + 4 + 16);
        Mp4BoxWriter.WriteFourCc(output, "isom");
        Mp4BoxWriter.WriteUInt32(output, 512);
        foreach (string brand in new[] { "isom", "iso5", "iso6", "mp41" })
        {
            Mp4BoxWriter.WriteFourCc(output, brand);
        }

        byte[] moov = Mp4BoxWriter.Build(buffer => WriteMovieBox(buffer, video, audio, width, height));
        output.Write(moov);
    }

    /// <summary>写 moov。</summary>
    private static void WriteMovieBox(Stream buffer, VideoConfig? video, AudioConfig? audio, int width, int height)
    {
        byte[] mvhd = Mp4BoxWriter.Build(WriteMovieHeader);
        byte[] videoTrak = video is null ? [] : Mp4BoxWriter.Build(b => WriteVideoTrack(b, video, width, height));
        byte[] audioTrak = audio is null ? [] : Mp4BoxWriter.Build(b => WriteAudioTrack(b, audio));
        byte[] mvex = Mp4BoxWriter.Build(b => WriteMovieExtends(b, video is not null, audio is not null));
        Mp4BoxWriter.WriteBoxHeader(buffer, "moov", mvhd.Length + videoTrak.Length + audioTrak.Length + mvex.Length);
        buffer.Write(mvhd);
        buffer.Write(videoTrak);
        buffer.Write(audioTrak);
        buffer.Write(mvex);
    }

    /// <summary>写 mvhd。</summary>
    private static void WriteMovieHeader(Stream buffer)
    {
        Mp4BoxWriter.WriteBoxHeader(buffer, "mvhd", 100);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 0);          // creation_time
        Mp4BoxWriter.WriteUInt32(buffer, 0);          // modification_time
        Mp4BoxWriter.WriteUInt32(buffer, 1000);       // timescale
        Mp4BoxWriter.WriteUInt32(buffer, 0);          // duration（分片 MP4 允许为 0）
        Mp4BoxWriter.WriteUInt32(buffer, 0x00010000); // rate 1.0
        Mp4BoxWriter.WriteUInt16(buffer, 0x0100);     // volume 1.0
        Mp4BoxWriter.WriteUInt16(buffer, 0);          // reserved
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        Mp4BoxWriter.WriteIdentityMatrix(buffer);
        for (int index = 0; index < 6; index++)
        {
            Mp4BoxWriter.WriteUInt32(buffer, 0);      // pre_defined
        }

        Mp4BoxWriter.WriteUInt32(buffer, 3);          // next_track_ID
    }

    /// <summary>写视频轨。</summary>
    private static void WriteVideoTrack(Stream buffer, VideoConfig video, int width, int height)
    {
        byte[] tkhd = Mp4BoxWriter.Build(b => WriteTrackHeader(b, trackId: 1, isAudio: false, width, height));
        byte[] mdia = Mp4BoxWriter.Build(b => WriteVideoMedia(b, video, width, height));
        Mp4BoxWriter.WriteBoxHeader(buffer, "trak", tkhd.Length + mdia.Length);
        buffer.Write(tkhd);
        buffer.Write(mdia);
    }

    /// <summary>写音频轨。</summary>
    private static void WriteAudioTrack(Stream buffer, AudioConfig audio)
    {
        byte[] tkhd = Mp4BoxWriter.Build(b => WriteTrackHeader(b, trackId: 2, isAudio: true, 0, 0));
        byte[] mdia = Mp4BoxWriter.Build(b => WriteAudioMedia(b, audio));
        Mp4BoxWriter.WriteBoxHeader(buffer, "trak", tkhd.Length + mdia.Length);
        buffer.Write(tkhd);
        buffer.Write(mdia);
    }

    /// <summary>写 tkhd。</summary>
    private static void WriteTrackHeader(Stream buffer, uint trackId, bool isAudio, int width, int height)
    {
        Mp4BoxWriter.WriteBoxHeader(buffer, "tkhd", 84);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 0x000007); // enabled | in_movie | in_preview
        Mp4BoxWriter.WriteUInt32(buffer, 0);                  // creation_time
        Mp4BoxWriter.WriteUInt32(buffer, 0);                  // modification_time
        Mp4BoxWriter.WriteUInt32(buffer, trackId);
        Mp4BoxWriter.WriteUInt32(buffer, 0);                  // reserved
        Mp4BoxWriter.WriteUInt32(buffer, 0);                  // duration
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        Mp4BoxWriter.WriteUInt16(buffer, 0);                  // layer
        Mp4BoxWriter.WriteUInt16(buffer, 0);                  // alternate_group
        Mp4BoxWriter.WriteUInt16(buffer, (ushort)(isAudio ? 0x0100 : 0));
        Mp4BoxWriter.WriteUInt16(buffer, 0);                  // reserved
        Mp4BoxWriter.WriteIdentityMatrix(buffer);
        Mp4BoxWriter.WriteUInt32(buffer, (uint)Math.Max(0, width) << 16);
        Mp4BoxWriter.WriteUInt32(buffer, (uint)Math.Max(0, height) << 16);
    }

    /// <summary>写视频 mdia。</summary>
    private static void WriteVideoMedia(Stream buffer, VideoConfig video, int width, int height)
    {
        byte[] mdhd = Mp4BoxWriter.Build(b => WriteMediaHeader(b, VideoTimescale));
        byte[] hdlr = Mp4BoxWriter.Build(b => WriteHandler(b, "vide", "VideoHandler"));
        byte[] stsd = Mp4BoxWriter.Build(b => WriteVideoSampleDescription(b, video, width, height));
        byte[] stblTables = Mp4BoxWriter.Build(WriteEmptySampleTables);
        byte[] stbl = Mp4BoxWriter.Build(b =>
        {
            Mp4BoxWriter.WriteBoxHeader(b, "stbl", stsd.Length + stblTables.Length);
            b.Write(stsd);
            b.Write(stblTables);
        });
        byte[] minf = Mp4BoxWriter.Build(b =>
        {
            byte[] vmhd = Mp4BoxWriter.Build(WriteVideoMediaHeader);
            byte[] dinf = Mp4BoxWriter.Build(WriteDataInformation);
            Mp4BoxWriter.WriteBoxHeader(b, "minf", vmhd.Length + dinf.Length + stbl.Length);
            b.Write(vmhd);
            b.Write(dinf);
            b.Write(stbl);
        });

        Mp4BoxWriter.WriteBoxHeader(buffer, "mdia", mdhd.Length + hdlr.Length + minf.Length);
        buffer.Write(mdhd);
        buffer.Write(hdlr);
        buffer.Write(minf);
    }

    /// <summary>写音频 mdia。</summary>
    private static void WriteAudioMedia(Stream buffer, AudioConfig audio)
    {
        byte[] mdhd = Mp4BoxWriter.Build(b => WriteMediaHeader(b, (uint)audio.SampleRate));
        byte[] hdlr = Mp4BoxWriter.Build(b => WriteHandler(b, "soun", "SoundHandler"));
        byte[] stsd = Mp4BoxWriter.Build(b => WriteAudioSampleDescription(b, audio));
        byte[] stblTables = Mp4BoxWriter.Build(WriteEmptySampleTables);
        byte[] stbl = Mp4BoxWriter.Build(b =>
        {
            Mp4BoxWriter.WriteBoxHeader(b, "stbl", stsd.Length + stblTables.Length);
            b.Write(stsd);
            b.Write(stblTables);
        });
        byte[] minf = Mp4BoxWriter.Build(b =>
        {
            byte[] smhd = Mp4BoxWriter.Build(WriteSoundMediaHeader);
            byte[] dinf = Mp4BoxWriter.Build(WriteDataInformation);
            Mp4BoxWriter.WriteBoxHeader(b, "minf", smhd.Length + dinf.Length + stbl.Length);
            b.Write(smhd);
            b.Write(dinf);
            b.Write(stbl);
        });

        Mp4BoxWriter.WriteBoxHeader(buffer, "mdia", mdhd.Length + hdlr.Length + minf.Length);
        buffer.Write(mdhd);
        buffer.Write(hdlr);
        buffer.Write(minf);
    }

    /// <summary>写 mdhd。</summary>
    private static void WriteMediaHeader(Stream buffer, uint timescale)
    {
        Mp4BoxWriter.WriteBoxHeader(buffer, "mdhd", 24);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        Mp4BoxWriter.WriteUInt32(buffer, timescale);
        Mp4BoxWriter.WriteUInt32(buffer, 0);   // duration（分片 MP4 由 moof 决定）
        Mp4BoxWriter.WriteUInt16(buffer, 0x55C4); // language = und
        Mp4BoxWriter.WriteUInt16(buffer, 0);
    }

    /// <summary>写 hdlr。</summary>
    private static void WriteHandler(Stream buffer, string handlerType, string name)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        Mp4BoxWriter.WriteBoxHeader(buffer, "hdlr", 24 + nameBytes.Length + 1);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        Mp4BoxWriter.WriteFourCc(buffer, handlerType);
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        buffer.Write(nameBytes);
        buffer.WriteByte(0);
    }

    /// <summary>写 vmhd。</summary>
    private static void WriteVideoMediaHeader(Stream buffer)
    {
        Mp4BoxWriter.WriteBoxHeader(buffer, "vmhd", 12);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 1);
        for (int index = 0; index < 4; index++)
        {
            Mp4BoxWriter.WriteUInt16(buffer, 0);
        }
    }

    /// <summary>写 smhd。</summary>
    private static void WriteSoundMediaHeader(Stream buffer)
    {
        Mp4BoxWriter.WriteBoxHeader(buffer, "smhd", 8);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 0);
        Mp4BoxWriter.WriteUInt16(buffer, 0);
        Mp4BoxWriter.WriteUInt16(buffer, 0);
    }

    /// <summary>写 dinf/dref/url（自包含数据）。</summary>
    private static void WriteDataInformation(Stream buffer)
    {
        byte[] url = Mp4BoxWriter.Build(b =>
        {
            Mp4BoxWriter.WriteBoxHeader(b, "url ", 4);
            Mp4BoxWriter.WriteFullBoxHeader(b, 0, 1);
        });
        byte[] dref = Mp4BoxWriter.Build(b =>
        {
            Mp4BoxWriter.WriteBoxHeader(b, "dref", 8 + url.Length);
            Mp4BoxWriter.WriteFullBoxHeader(b, 0, 0);
            Mp4BoxWriter.WriteUInt32(b, 1);
            b.Write(url);
        });
        Mp4BoxWriter.WriteBoxHeader(buffer, "dinf", dref.Length);
        buffer.Write(dref);
    }

    /// <summary>写分片 MP4 的空样本表（真实样本都在 moof/traf 里）。</summary>
    private static void WriteEmptySampleTables(Stream buffer)
    {
        Mp4BoxWriter.WriteBoxHeader(buffer, "stts", 8);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        Mp4BoxWriter.WriteBoxHeader(buffer, "stsc", 8);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        Mp4BoxWriter.WriteBoxHeader(buffer, "stsz", 12);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 0);
        Mp4BoxWriter.WriteBoxHeader(buffer, "stco", 8);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 0);
    }

    /// <summary>写视频 stsd（avc1 / hvc1）。</summary>
    private static void WriteVideoSampleDescription(Stream buffer, VideoConfig video, int width, int height)
    {
        byte[] entry = Mp4BoxWriter.Build(b =>
        {
            // SampleEntry
            for (int index = 0; index < 6; index++)
            {
                Mp4BoxWriter.WriteUInt8(b, 0);
            }

            Mp4BoxWriter.WriteUInt16(b, 1);           // data_reference_index
            Mp4BoxWriter.WriteUInt16(b, 0);           // pre_defined
            Mp4BoxWriter.WriteUInt16(b, 0);           // reserved
            for (int index = 0; index < 3; index++)
            {
                Mp4BoxWriter.WriteUInt32(b, 0);       // pre_defined[3]
            }

            Mp4BoxWriter.WriteUInt16(b, (ushort)Math.Max(0, width));
            Mp4BoxWriter.WriteUInt16(b, (ushort)Math.Max(0, height));
            Mp4BoxWriter.WriteUInt32(b, 0x00480000);  // 72 dpi
            Mp4BoxWriter.WriteUInt32(b, 0x00480000);
            Mp4BoxWriter.WriteUInt32(b, 0);           // reserved
            Mp4BoxWriter.WriteUInt16(b, 1);           // frame_count
            for (int index = 0; index < 32; index++)
            {
                Mp4BoxWriter.WriteUInt8(b, 0);        // compressorname
            }

            Mp4BoxWriter.WriteUInt16(b, 0x0018);      // depth
            Mp4BoxWriter.WriteUInt16(b, 0xFFFF);      // pre_defined = -1

            Mp4BoxWriter.WriteBoxHeader(b, video.ConfigBoxType, video.Config.Length);
            b.Write(video.Config);
        });

        Mp4BoxWriter.WriteBoxHeader(buffer, "stsd", 8 + 8 + entry.Length);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 1);
        Mp4BoxWriter.WriteBoxHeader(buffer, video.SampleEntryType, entry.Length);
        buffer.Write(entry);
    }

    /// <summary>写音频 stsd（mp4a + esds）。</summary>
    private static void WriteAudioSampleDescription(Stream buffer, AudioConfig audio)
    {
        byte[] esds = Mp4BoxWriter.Build(b => WriteEsds(b, audio));
        byte[] entry = Mp4BoxWriter.Build(b =>
        {
            for (int index = 0; index < 6; index++)
            {
                Mp4BoxWriter.WriteUInt8(b, 0);
            }

            Mp4BoxWriter.WriteUInt16(b, 1);                 // data_reference_index
            Mp4BoxWriter.WriteUInt32(b, 0);
            Mp4BoxWriter.WriteUInt32(b, 0);
            Mp4BoxWriter.WriteUInt16(b, (ushort)Math.Max(1, audio.Channels));
            Mp4BoxWriter.WriteUInt16(b, 16);                // samplesize
            Mp4BoxWriter.WriteUInt16(b, 0);                 // pre_defined
            Mp4BoxWriter.WriteUInt16(b, 0);                 // reserved
            Mp4BoxWriter.WriteUInt32(b, (uint)audio.SampleRate << 16);
            b.Write(esds);
        });

        Mp4BoxWriter.WriteBoxHeader(buffer, "stsd", 8 + 8 + entry.Length);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 0);
        Mp4BoxWriter.WriteUInt32(buffer, 1);
        Mp4BoxWriter.WriteBoxHeader(buffer, "mp4a", entry.Length);
        buffer.Write(entry);
    }

    /// <summary>写 esds（含 AudioSpecificConfig）。</summary>
    private static void WriteEsds(Stream buffer, AudioConfig audio)
    {
        byte[] dsi = audio.AudioSpecificConfig;
        byte[] decoderSpecific = Mp4BoxWriter.Build(b =>
        {
            Mp4BoxWriter.WriteUInt8(b, 0x05);
            Mp4BoxWriter.WriteUInt8(b, (byte)dsi.Length);
            b.Write(dsi);
        });
        byte[] decoderConfig = Mp4BoxWriter.Build(b =>
        {
            Mp4BoxWriter.WriteUInt8(b, 0x04);
            Mp4BoxWriter.WriteUInt8(b, (byte)(13 + decoderSpecific.Length));
            Mp4BoxWriter.WriteUInt8(b, 0x40);      // MPEG-4 audio
            Mp4BoxWriter.WriteUInt8(b, 0x15);      // audio stream
            Mp4BoxWriter.WriteUInt24(b, 0);        // bufferSizeDB
            Mp4BoxWriter.WriteUInt32(b, 0);        // maxBitrate
            Mp4BoxWriter.WriteUInt32(b, 0);        // avgBitrate
            b.Write(decoderSpecific);
        });
        byte[] slConfig = Mp4BoxWriter.Build(b =>
        {
            Mp4BoxWriter.WriteUInt8(b, 0x06);
            Mp4BoxWriter.WriteUInt8(b, 1);
            Mp4BoxWriter.WriteUInt8(b, 0x02);
        });
        byte[] es = Mp4BoxWriter.Build(b =>
        {
            Mp4BoxWriter.WriteUInt8(b, 0x03);
            Mp4BoxWriter.WriteUInt8(b, (byte)(3 + decoderConfig.Length + slConfig.Length));
            Mp4BoxWriter.WriteUInt16(b, 0);        // ES_ID
            Mp4BoxWriter.WriteUInt8(b, 0);         // flags
            b.Write(decoderConfig);
            b.Write(slConfig);
        });

        Mp4BoxWriter.WriteBoxHeader(buffer, "esds", 4 + es.Length);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 0);
        buffer.Write(es);
    }

    /// <summary>写 mvex/trex。</summary>
    private static void WriteMovieExtends(Stream buffer, bool hasVideo, bool hasAudio)
    {
        byte[] trexVideo = hasVideo ? Mp4BoxWriter.Build(b => WriteTrackExtends(b, 1)) : [];
        byte[] trexAudio = hasAudio ? Mp4BoxWriter.Build(b => WriteTrackExtends(b, 2)) : [];
        Mp4BoxWriter.WriteBoxHeader(buffer, "mvex", trexVideo.Length + trexAudio.Length);
        buffer.Write(trexVideo);
        buffer.Write(trexAudio);
    }

    /// <summary>写 trex。</summary>
    private static void WriteTrackExtends(Stream buffer, uint trackId)
    {
        Mp4BoxWriter.WriteBoxHeader(buffer, "trex", 24);
        Mp4BoxWriter.WriteFullBoxHeader(buffer, 0, 0);
        Mp4BoxWriter.WriteUInt32(buffer, trackId);
        Mp4BoxWriter.WriteUInt32(buffer, 1);   // default_sample_description_index
        Mp4BoxWriter.WriteUInt32(buffer, 0);   // default_sample_duration
        Mp4BoxWriter.WriteUInt32(buffer, 0);   // default_sample_size
        Mp4BoxWriter.WriteUInt32(buffer, 0);   // default_sample_flags
    }

    /// <summary>视频解码配置。</summary>
    private sealed record VideoConfig(byte CodecId, bool IsHevc, byte[] Config)
    {
        /// <summary>是否支持转封装（只支持 AVC 与 HEVC）。</summary>
        public bool IsSupported => CodecId is 7 or 12;

        /// <summary>sample entry 类型。</summary>
        public string SampleEntryType => IsHevc ? "hvc1" : "avc1";

        /// <summary>解码配置盒类型。</summary>
        public string ConfigBoxType => IsHevc ? "hvcC" : "avcC";

        /// <summary>从 FLV 视频 sequence header 构造。</summary>
        /// <param name="tag">sequence header 标签。</param>
        /// <returns>配置；载荷不合法时为 <see langword="null"/>。</returns>
        public static VideoConfig? TryCreate(FlvTag tag)
        {
            if (tag.Payload.Length <= 5)
            {
                return null;
            }

            byte codecId = (byte)(tag.Payload[0] & 0x0F);
            byte[] config = tag.Payload[5..];
            return new VideoConfig(codecId, codecId == 12, config);
        }

        /// <summary>解析分辨率：优先 FLV 元数据，缺失时回到 SPS。</summary>
        /// <param name="metadataWidth">元数据宽度。</param>
        /// <param name="metadataHeight">元数据高度。</param>
        /// <returns>宽高。</returns>
        public (int Width, int Height) ResolveDimensions(int metadataWidth, int metadataHeight)
        {
            if (metadataWidth > 0 && metadataHeight > 0)
            {
                return (metadataWidth, metadataHeight);
            }

            return IsHevc ? VideoDimensions.FromHevcConfig(Config) : VideoDimensions.FromAvcConfig(Config);
        }
    }

    /// <summary>音频解码配置。</summary>
    private sealed record AudioConfig(byte[] AudioSpecificConfig, int SampleRate, int Channels)
    {
        /// <summary>从 FLV 音频 sequence header 构造。</summary>
        /// <param name="tag">sequence header 标签。</param>
        /// <returns>配置；载荷不合法时为 <see langword="null"/>。</returns>
        public static AudioConfig? TryCreate(FlvTag tag)
        {
            if (tag.Payload.Length <= 2)
            {
                return null;
            }

            byte[] asc = tag.Payload[2..];
            (int rate, int channels) = ParseAudioSpecificConfig(asc);
            return rate > 0 ? new AudioConfig(asc, rate, channels) : null;
        }

        /// <summary>解析 AudioSpecificConfig 的采样率与声道数。</summary>
        /// <param name="asc">AudioSpecificConfig 字节。</param>
        /// <returns>采样率与声道数。</returns>
        private static (int SampleRate, int Channels) ParseAudioSpecificConfig(byte[] asc)
        {
            if (asc.Length < 2)
            {
                return (0, 0);
            }

            int bitPosition = 0;
            int ReadBits(int count)
            {
                int value = 0;
                for (int index = 0; index < count; index++)
                {
                    int byteIndex = bitPosition >> 3;
                    if (byteIndex >= asc.Length)
                    {
                        return -1;
                    }

                    value = (value << 1) | ((asc[byteIndex] >> (7 - (bitPosition & 7))) & 1);
                    bitPosition++;
                }

                return value;
            }

            // audioObjectType 可能占 5 位 + 6 位扩展（AOT=31 时）
            int audioObjectType = ReadBits(5);
            if (audioObjectType == 31)
            {
                audioObjectType = 32 + ReadBits(6);
            }

            if (audioObjectType is 5 or 29)
            {
                return (0, 0); // 显式 SBR/PS：转封装不支持，保持 FLV
            }

            int frequencyIndex = ReadBits(4);
            int sampleRate = frequencyIndex switch
            {
                0 => 96000,
                1 => 88200,
                2 => 64000,
                3 => 48000,
                4 => 44100,
                5 => 32000,
                6 => 24000,
                7 => 22050,
                8 => 16000,
                9 => 12000,
                10 => 11025,
                11 => 8000,
                12 => 7350,
                13 or 14 => 0,
                _ => 0,
            };

            if (frequencyIndex == 15)
            {
                sampleRate = ReadBits(24);
            }

            int channelConfiguration = ReadBits(4);
            return (sampleRate, channelConfiguration switch
            {
                0 => 0,
                7 => 8,
                _ => channelConfiguration,
            });
        }
    }

    /// <summary>分片写入器：把标签转成样本并定期落 moof/mdat。</summary>
    private sealed class Mp4Writer
    {
        private readonly Stream _output;
        private readonly VideoConfig? _video;
        private readonly AudioConfig? _audio;
        private readonly List<Sample> _videoSamples = [];
        private readonly List<Sample> _audioSamples = [];
        private Sample? _pendingVideo;
        private uint _videoDecodeTime;
        private ulong _audioDecodeTime;
        private long _windowStartMs = -1;

        public Mp4Writer(Stream output, VideoConfig? video, AudioConfig? audio)
        {
            _output = output;
            _video = video;
            _audio = audio;
        }

        public int VideoSampleCount { get; private set; }

        public int AudioSampleCount { get; private set; }

        /// <summary>处理一个 FLV 标签，并按时间窗口切分片。</summary>
        /// <param name="tag">标签。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public async Task AddAsync(FlvTag tag, CancellationToken cancellationToken)
        {
            if (tag.Type == FlvTagType.Video && _video is not null)
            {
                AddVideoSample(tag);
            }
            else if (tag.Type == FlvTagType.Audio && _audio is not null)
            {
                AddAudioSample(tag);
            }
            else
            {
                return;
            }

            if (_windowStartMs < 0)
            {
                _windowStartMs = tag.TimestampMs;
            }

            if (tag.TimestampMs - _windowStartMs >= FragmentMilliseconds)
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
                _windowStartMs = tag.TimestampMs;
            }
        }

        /// <summary>收尾：把剩余样本全部写完。</summary>
        /// <param name="cancellationToken">取消令牌。</param>
        public async Task CompleteAsync(CancellationToken cancellationToken) =>
            await FlushAsync(cancellationToken).ConfigureAwait(false);

        private void AddVideoSample(FlvTag tag)
        {
            // 只处理 NALU 包：sequence header 已经写进 avcC/hvcC，end-of-sequence 直接丢弃。
            if (tag.Payload.Length <= 5 || tag.Payload[1] != 1)
            {
                return;
            }

            bool isKeyFrame = (tag.Payload[0] >> 4) == 1;
            int compositionOffset = (tag.Payload[2] << 16) | (tag.Payload[3] << 8) | tag.Payload[4];
            if ((compositionOffset & 0x800000) != 0)
            {
                compositionOffset -= 0x1000000;
            }

            // 新样本到达时才能结算前一个样本的时长（FLV 只给时间戳，不给时长）。
            if (_pendingVideo is { } previous)
            {
                int duration = Math.Max(1, (int)(tag.TimestampMs - previous.TimestampMs));
                _videoSamples.Add(previous with { Duration = duration });
            }

            _pendingVideo = new Sample(tag.Payload[5..], 0, compositionOffset, isKeyFrame, tag.TimestampMs);
        }

        private void AddAudioSample(FlvTag tag)
        {
            if (tag.Payload.Length <= 2 || tag.Payload[1] != 1)
            {
                return;
            }

            _audioSamples.Add(new Sample(tag.Payload[2..], AacSamplesPerFrame, 0, true, tag.TimestampMs));
        }

        private async Task FlushAsync(CancellationToken cancellationToken)
        {
            await FlushVideoAsync(cancellationToken).ConfigureAwait(false);
            await FlushAudioAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task FlushVideoAsync(CancellationToken cancellationToken)
        {
            if (_pendingVideo is { } pending)
            {
                _videoSamples.Add(pending with { Duration = 0 });
                _pendingVideo = null;
            }

            if (_videoSamples.Count == 0)
            {
                return;
            }

            // 分片末尾那个样本没有后继时间戳：用已写样本的平均间隔补齐，避免时长 0 导致播放器卡住。
            if (_videoSamples[^1].Duration <= 0)
            {
                int average = VideoSampleCount > 0
                    ? Math.Max(1, (int)(_videoDecodeTime / Math.Max(1, VideoSampleCount)))
                    : 33;
                _videoSamples[^1] = _videoSamples[^1] with { Duration = average };
            }

            await WriteFragmentAsync(1, _videoSamples, _videoDecodeTime, cancellationToken).ConfigureAwait(false);
            foreach (Sample sample in _videoSamples)
            {
                _videoDecodeTime += (uint)sample.Duration;
            }

            VideoSampleCount += _videoSamples.Count;
            _videoSamples.Clear();
        }

        private async Task FlushAudioAsync(CancellationToken cancellationToken)
        {
            if (_audioSamples.Count == 0)
            {
                return;
            }

            await WriteFragmentAsync(2, _audioSamples, (uint)_audioDecodeTime, cancellationToken).ConfigureAwait(false);
            _audioDecodeTime += (ulong)_audioSamples.Count * AacSamplesPerFrame;
            AudioSampleCount += _audioSamples.Count;
            _audioSamples.Clear();
        }

        private async Task WriteFragmentAsync(uint trackId, List<Sample> samples, uint baseDecodeTime, CancellationToken cancellationToken)
        {
            byte[] moof = BuildFragmentHeader(trackId, samples, baseDecodeTime);
            await _output.WriteAsync(moof, cancellationToken).ConfigureAwait(false);

            long totalSize = 0;
            foreach (Sample sample in samples)
            {
                totalSize += sample.Data.Length;
            }

            Mp4BoxWriter.WriteUInt32(_output, (uint)(totalSize + 8));
            Mp4BoxWriter.WriteFourCc(_output, "mdat");
            foreach (Sample sample in samples)
            {
                await _output.WriteAsync(sample.Data, cancellationToken).ConfigureAwait(false);
            }
        }

        private static byte[] BuildFragmentHeader(uint trackId, List<Sample> samples, uint baseDecodeTime)
        {
            // trun 的 data_offset 相对 moof 起点；mdat 紧跟 moof，所以先构建一次拿到长度再回填偏移。
            int Build(long dataOffset, out byte[] result)
            {
                result = Mp4BoxWriter.Build(buffer =>
                {
                    byte[] mfhd = Mp4BoxWriter.Build(b =>
                    {
                        Mp4BoxWriter.WriteBoxHeader(b, "mfhd", 8);
                        Mp4BoxWriter.WriteFullBoxHeader(b, 0, 0);
                        Mp4BoxWriter.WriteUInt32(b, 1);
                    });
                    byte[] tfdt = Mp4BoxWriter.Build(b =>
                    {
                        Mp4BoxWriter.WriteBoxHeader(b, "tfdt", 8);
                        Mp4BoxWriter.WriteFullBoxHeader(b, 0, 0);
                        Mp4BoxWriter.WriteUInt32(b, baseDecodeTime);
                    });
                    byte[] tfhd = Mp4BoxWriter.Build(b =>
                    {
                        Mp4BoxWriter.WriteBoxHeader(b, "tfhd", 8);
                        Mp4BoxWriter.WriteFullBoxHeader(b, 0, 0x020000); // default-base-is-moof
                        Mp4BoxWriter.WriteUInt32(b, trackId);
                    });
                    byte[] trun = Mp4BoxWriter.Build(b =>
                    {
                        Mp4BoxWriter.WriteBoxHeader(b, "trun", 12 + (samples.Count * 16));
                        Mp4BoxWriter.WriteFullBoxHeader(b, 1, 0x000F01); // data-offset | duration | size | flags | cts
                        Mp4BoxWriter.WriteUInt32(b, (uint)samples.Count);
                        Mp4BoxWriter.WriteUInt32(b, (uint)dataOffset);
                        foreach (Sample sample in samples)
                        {
                            Mp4BoxWriter.WriteUInt32(b, (uint)sample.Duration);
                            Mp4BoxWriter.WriteUInt32(b, (uint)sample.Data.Length);
                            Mp4BoxWriter.WriteUInt32(b, sample.IsKey ? 0x02000000u : 0x01010000u);
                            Mp4BoxWriter.WriteUInt32(b, (uint)sample.CompositionOffset);
                        }
                    });
                    byte[] traf = Mp4BoxWriter.Build(b =>
                    {
                        Mp4BoxWriter.WriteBoxHeader(b, "traf", tfhd.Length + tfdt.Length + trun.Length);
                        b.Write(tfhd);
                        b.Write(tfdt);
                        b.Write(trun);
                    });
                    Mp4BoxWriter.WriteBoxHeader(buffer, "moof", mfhd.Length + traf.Length);
                    buffer.Write(mfhd);
                    buffer.Write(traf);
                });
                return result.Length;
            }

            int length = Build(0, out _);
            Build(length + 8, out byte[] moof);
            return moof;
        }

        /// <summary>一个待写入的样本。</summary>
        private sealed record Sample(byte[] Data, int Duration, int CompositionOffset, bool IsKey, long TimestampMs);
    }

    /// <summary>AMF0 onMetaData 里读取宽高。</summary>
    private static class FlvMetadataReader
    {
        /// <summary>读取 width/height。</summary>
        /// <param name="payload">script 标签载荷。</param>
        /// <returns>宽高；缺失时为 0。</returns>
        public static (int Width, int Height) ReadDimensions(byte[] payload)
        {
            int width = 0;
            int height = 0;
            try
            {
                int position = 0;
                if (payload.Length < 3 || payload[position] != 0x02)
                {
                    return (0, 0);
                }

                position++;
                int nameLength = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(position));
                position += 2 + nameLength;
                if (position >= payload.Length || payload[position] != 0x08)
                {
                    return (0, 0);
                }

                position++;
                int count = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(position));
                position += 4;
                for (int index = 0; index < count && position + 3 <= payload.Length; index++)
                {
                    int keyLength = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(position));
                    position += 2;
                    if (position + keyLength > payload.Length)
                    {
                        break;
                    }

                    string key = Encoding.ASCII.GetString(payload, position, keyLength);
                    position += keyLength;
                    if (position >= payload.Length)
                    {
                        break;
                    }

                    byte type = payload[position++];
                    switch (type)
                    {
                        case 0x00:
                            if (position + 8 > payload.Length)
                            {
                                return (width, height);
                            }

                            double number = BinaryPrimitives.ReadDoubleBigEndian(payload.AsSpan(position));
                            position += 8;
                            if (key == "width")
                            {
                                width = (int)Math.Round(number);
                            }
                            else if (key == "height")
                            {
                                height = (int)Math.Round(number);
                            }

                            break;
                        case 0x01:
                            position += 1;
                            break;
                        case 0x02:
                            int length = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(position));
                            position += 2 + length;
                            break;
                        default:
                            return (width, height);
                    }
                }
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                return (width, height);
            }

            return (width, height);
        }
    }
}
