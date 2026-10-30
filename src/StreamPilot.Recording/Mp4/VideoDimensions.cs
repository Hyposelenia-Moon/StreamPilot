namespace StreamPilot.Recording.Mp4;

/// <summary>
/// 从解码器配置记录里取出 SPS 并解出显示分辨率。
/// </summary>
/// <remarks>
/// MP4 的 <c>tkhd</c>/<c>avc1</c>/<c>hvc1</c> 都要写宽高；FLV 的 onMetaData 通常带 width/height，
/// 但那是"平台声明的值"，缺省时只能回到码流本身。这里只解析到宽高为止，不做完整 SPS 解释，
/// 因此代码量可控，也不引入任何解码依赖。
/// </remarks>
internal static class VideoDimensions
{
    /// <summary>无法解析时的返回值。</summary>
    public static readonly (int Width, int Height) Unknown = (0, 0);

    /// <summary>从 AVC 的 AVCDecoderConfigurationRecord 里取宽高。</summary>
    /// <param name="config">AVCDecoderConfigurationRecord（不含 FLV 头）。</param>
    /// <returns>宽高；解析失败返回 <see cref="Unknown"/>。</returns>
    public static (int Width, int Height) FromAvcConfig(ReadOnlySpan<byte> config)
    {
        foreach (byte[] sps in ReadSpsUnits(config, isHevc: false))
        {
            (int width, int height) = ParseAvcSps(sps);
            if (width > 0 && height > 0)
            {
                return (width, height);
            }
        }

        return Unknown;
    }

    /// <summary>从 HEVC 的 HEVCDecoderConfigurationRecord 里取宽高。</summary>
    /// <param name="config">HEVCDecoderConfigurationRecord（不含 FLV 头）。</param>
    /// <returns>宽高；解析失败返回 <see cref="Unknown"/>。</returns>
    public static (int Width, int Height) FromHevcConfig(ReadOnlySpan<byte> config)
    {
        foreach (byte[] sps in ReadSpsUnits(config, isHevc: true))
        {
            (int width, int height) = ParseHevcSps(sps);
            if (width > 0 && height > 0)
            {
                return (width, height);
            }
        }

        return Unknown;
    }

    /// <summary>取出配置记录里的 SPS NAL（AVC 只取 SPS；HEVC 按数组类型筛 33）。</summary>
    /// <param name="config">解码器配置记录。</param>
    /// <param name="isHevc">是否为 HEVC 配置记录。</param>
    /// <returns>SPS 字节列表（每项含 NAL 头）。</returns>
    private static List<byte[]> ReadSpsUnits(ReadOnlySpan<byte> config, bool isHevc)
    {
        List<byte[]> units = [];
        if (isHevc)
        {
            if (config.Length < 23)
            {
                return units;
            }

            int offset = 23;
            int arrays = config[22];
            for (int arrayIndex = 0; arrayIndex < arrays && offset + 3 <= config.Length; arrayIndex++)
            {
                int nalType = config[offset] & 0x3F;
                int count = (config[offset + 1] << 8) | config[offset + 2];
                offset += 3;
                for (int index = 0; index < count && offset + 2 <= config.Length; index++)
                {
                    int length = (config[offset] << 8) | config[offset + 1];
                    offset += 2;
                    if (length <= 0 || offset + length > config.Length)
                    {
                        return units;
                    }

                    if (nalType == 33)
                    {
                        units.Add(config.Slice(offset, length).ToArray());
                    }

                    offset += length;
                }
            }

            return units;
        }

        if (config.Length < 7)
        {
            return units;
        }

        int position = 5;
        int spsCount = config[position++] & 0x1F;
        for (int index = 0; index < spsCount && position + 2 <= config.Length; index++)
        {
            int length = (config[position] << 8) | config[position + 1];
            position += 2;
            if (length <= 0 || position + length > config.Length)
            {
                return units;
            }

            units.Add(config.Slice(position, length).ToArray());
            position += length;
        }

        return units;
    }

    /// <summary>解析 AVC SPS 的宽高。</summary>
    private static (int Width, int Height) ParseAvcSps(ReadOnlySpan<byte> sps)
    {
        if (sps.Length < 4)
        {
            return Unknown;
        }

        // 跳过 NAL 头（1 字节）后去防竞争字节
        byte[] rbsp = RemoveEmulationPrevention(sps[1..]);
        BitReader reader = new(rbsp);
        try
        {
            int profileIdc = reader.ReadBits(8);
            reader.SkipBits(8);   // constraint flags
            reader.SkipBits(8);   // level_idc
            reader.ReadUnsignedExpGolomb(); // seq_parameter_set_id

            if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                int chromaFormatIdc = reader.ReadUnsignedExpGolomb();
                if (chromaFormatIdc == 3)
                {
                    reader.ReadBits(1);
                }

                reader.ReadUnsignedExpGolomb(); // bit_depth_luma_minus8
                reader.ReadUnsignedExpGolomb(); // bit_depth_chroma_minus8
                reader.ReadBits(1);             // qpprime_y_zero_transform_bypass_flag
                if (reader.ReadBits(1) == 1)
                {
                    int lists = chromaFormatIdc != 3 ? 8 : 12;
                    for (int index = 0; index < lists; index++)
                    {
                        if (reader.ReadBits(1) == 1)
                        {
                            SkipScalingList(reader, index < 6 ? 16 : 64);
                        }
                    }
                }
            }

            reader.ReadUnsignedExpGolomb(); // log2_max_frame_num_minus4
            int picOrderCntType = reader.ReadUnsignedExpGolomb();
            if (picOrderCntType == 0)
            {
                reader.ReadUnsignedExpGolomb();
            }
            else if (picOrderCntType == 1)
            {
                reader.ReadBits(1);
                reader.ReadSignedExpGolomb();
                reader.ReadSignedExpGolomb();
                int cycle = reader.ReadUnsignedExpGolomb();
                for (int index = 0; index < cycle; index++)
                {
                    reader.ReadSignedExpGolomb();
                }
            }

            reader.ReadUnsignedExpGolomb(); // max_num_ref_frames
            reader.ReadBits(1);             // gaps_in_frame_num_value_allowed_flag
            int widthMbs = reader.ReadUnsignedExpGolomb() + 1;
            int heightMapUnits = reader.ReadUnsignedExpGolomb() + 1;
            bool frameMbsOnly = reader.ReadBits(1) == 1;
            if (!frameMbsOnly)
            {
                reader.ReadBits(1);
            }

            reader.ReadBits(1); // direct_8x8_inference_flag
            int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
            if (reader.ReadBits(1) == 1)
            {
                cropLeft = reader.ReadUnsignedExpGolomb();
                cropRight = reader.ReadUnsignedExpGolomb();
                cropTop = reader.ReadUnsignedExpGolomb();
                cropBottom = reader.ReadUnsignedExpGolomb();
            }

            int width = widthMbs * 16 - (cropLeft + cropRight) * 2;
            int height = heightMapUnits * 16 * (frameMbsOnly ? 1 : 2) - (cropTop + cropBottom) * 2 * (frameMbsOnly ? 1 : 2);
            return width > 0 && height > 0 ? (width, height) : Unknown;
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return Unknown;
        }
    }

    /// <summary>解析 HEVC SPS 的宽高（含一致性窗口裁剪）。</summary>
    private static (int Width, int Height) ParseHevcSps(ReadOnlySpan<byte> sps)
    {
        if (sps.Length < 4)
        {
            return Unknown;
        }

        byte[] rbsp = RemoveEmulationPrevention(sps[2..]);
        BitReader reader = new(rbsp);
        try
        {
            reader.SkipBits(4);                       // sps_video_parameter_set_id
            int maxSubLayersMinusOne = reader.ReadBits(3);
            reader.SkipBits(1);                       // sps_temporal_id_nesting_flag

            // profile_tier_level(1, sps_max_sub_layers_minus1)
            reader.SkipBits(2 + 1 + 5 + 32 + 48 + 8);
            bool[] subLayerProfile = new bool[maxSubLayersMinusOne];
            bool[] subLayerLevel = new bool[maxSubLayersMinusOne];
            for (int index = 0; index < maxSubLayersMinusOne; index++)
            {
                subLayerProfile[index] = reader.ReadBits(1) == 1;
                subLayerLevel[index] = reader.ReadBits(1) == 1;
            }

            if (maxSubLayersMinusOne > 0)
            {
                for (int index = maxSubLayersMinusOne; index < 8; index++)
                {
                    reader.SkipBits(2);
                }
            }

            for (int index = 0; index < maxSubLayersMinusOne; index++)
            {
                if (subLayerProfile[index])
                {
                    reader.SkipBits(2 + 1 + 5 + 32 + 48);
                }

                if (subLayerLevel[index])
                {
                    reader.SkipBits(8);
                }
            }

            reader.ReadUnsignedExpGolomb();           // sps_seq_parameter_set_id
            int chromaFormatIdc = reader.ReadUnsignedExpGolomb();
            if (chromaFormatIdc == 3)
            {
                reader.SkipBits(1);
            }

            int width = reader.ReadUnsignedExpGolomb();
            int height = reader.ReadUnsignedExpGolomb();
            int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
            if (reader.ReadBits(1) == 1)
            {
                cropLeft = reader.ReadUnsignedExpGolomb();
                cropRight = reader.ReadUnsignedExpGolomb();
                cropTop = reader.ReadUnsignedExpGolomb();
                cropBottom = reader.ReadUnsignedExpGolomb();
            }

            (int subWidth, int subHeight) = chromaFormatIdc switch
            {
                1 => (2, 2),
                2 => (2, 1),
                _ => (1, 1),
            };
            int displayWidth = width - (cropLeft + cropRight) * subWidth;
            int displayHeight = height - (cropTop + cropBottom) * subHeight;
            return displayWidth > 0 && displayHeight > 0 ? (displayWidth, displayHeight) : (width, height);
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return Unknown;
        }
    }

    /// <summary>跳过一条 scaling list。</summary>
    private static void SkipScalingList(BitReader reader, int size)
    {
        int lastScale = 8;
        int nextScale = 8;
        for (int index = 0; index < size; index++)
        {
            if (nextScale != 0)
            {
                int delta = reader.ReadSignedExpGolomb();
                nextScale = (lastScale + delta + 256) % 256;
            }

            if (nextScale != 0)
            {
                lastScale = nextScale;
            }
        }
    }

    /// <summary>去掉 H.264/H.265 的防竞争字节（00 00 03 → 00 00）。</summary>
    /// <param name="data">原始字节。</param>
    /// <returns>RBSP 字节。</returns>
    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> data)
    {
        byte[] result = new byte[data.Length];
        int written = 0;
        int zeros = 0;
        foreach (byte value in data)
        {
            if (zeros >= 2 && value == 0x03)
            {
                zeros = 0;
                continue;
            }

            result[written++] = value;
            zeros = value == 0x00 ? zeros + 1 : 0;
        }

        return result.AsSpan(0, written).ToArray();
    }

    /// <summary>按位读取（大端）。</summary>
    private sealed class BitReader(byte[] data)
    {
        private int _bitPosition;

        public int ReadBits(int count)
        {
            int value = 0;
            for (int index = 0; index < count; index++)
            {
                int byteIndex = _bitPosition >> 3;
                if (byteIndex >= data.Length)
                {
                    throw new IndexOutOfRangeException("SPS 数据在读取过程中结束。");
                }

                int bit = (data[byteIndex] >> (7 - (_bitPosition & 7))) & 1;
                value = (value << 1) | bit;
                _bitPosition++;
            }

            return value;
        }

        public void SkipBits(int count) => _bitPosition += count;

        public int ReadUnsignedExpGolomb()
        {
            int leadingZeros = 0;
            while (ReadBits(1) == 0)
            {
                leadingZeros++;
                if (leadingZeros > 32)
                {
                    throw new InvalidOperationException("Exp-Golomb 前缀过长。");
                }
            }

            if (leadingZeros == 0)
            {
                return 0;
            }

            return (1 << leadingZeros) - 1 + ReadBits(leadingZeros);
        }

        public int ReadSignedExpGolomb()
        {
            int value = ReadUnsignedExpGolomb();
            int sign = (value & 1) == 1 ? -1 : 1;
            return sign * ((value + 1) / 2);
        }
    }
}
