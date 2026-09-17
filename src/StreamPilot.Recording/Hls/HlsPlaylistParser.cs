namespace StreamPilot.Recording.Hls;

/// <summary>
/// HLS 媒体播放列表（m3u8）的解析结果。
/// </summary>
/// <param name="IsMasterPlaylist">是否为 master 播放列表（包含 variant 流）。</param>
/// <param name="VariantUris">master 播放列表中的 variant 地址（按出现顺序）。</param>
/// <param name="SegmentUris">media 播放列表中的分片地址（按出现顺序）。</param>
/// <param name="SegmentDurationsSeconds">与 <paramref name="SegmentUris"/> 一一对应的分片时长（秒）。</param>
/// <param name="TargetDurationSeconds">#EXT-X-TARGETDURATION 值；缺失时为 0。</param>
/// <param name="IsEndList">是否包含 #EXT-X-ENDLIST（直播结束）。</param>
/// <param name="MediaSequence">#EXT-X-MEDIA-SEQUENCE 值；缺失时为 0。</param>
public sealed record HlsPlaylist(
    bool IsMasterPlaylist,
    IReadOnlyList<string> VariantUris,
    IReadOnlyList<string> SegmentUris,
    IReadOnlyList<double> SegmentDurationsSeconds,
    int TargetDurationSeconds,
    bool IsEndList,
    long MediaSequence)
{
    /// <summary>所有分片的累计时长（秒）。</summary>
    public double TotalDurationSeconds
    {
        get
        {
            double total = 0;
            foreach (double duration in SegmentDurationsSeconds)
            {
                total += duration;
            }

            return total;
        }
    }
}

/// <summary>
/// HLS 播放列表解析器（纯函数，便于单元测试）。
/// </summary>
/// <remarks>
/// 只支持标准 m3u8 语法中录制需要的部分：master 的 <c>#EXT-X-STREAM-INF</c>、
/// media 的 <c>#EXTINF</c>/<c>#EXT-X-TARGETDURATION</c>/<c>#EXT-X-MEDIA-SEQUENCE</c>/<c>#EXT-X-ENDLIST</c>。
/// 相对地址按播放列表自身的基准地址解析为绝对地址。
/// </remarks>
public static class HlsPlaylistParser
{
    /// <summary>master 播放列表标记。</summary>
    private const string StreamInfTag = "#EXT-X-STREAM-INF";

    /// <summary>分片时长标记。</summary>
    private const string ExtInfTag = "#EXTINF";

    /// <summary>目标时长标记。</summary>
    private const string TargetDurationTag = "#EXT-X-TARGETDURATION";

    /// <summary>媒体序号标记。</summary>
    private const string MediaSequenceTag = "#EXT-X-MEDIA-SEQUENCE";

    /// <summary>播放列表结束标记。</summary>
    private const string EndListTag = "#EXT-X-ENDLIST";

    /// <summary>解析 m3u8 文本。</summary>
    /// <param name="content">m3u8 文本。</param>
    /// <param name="baseUri">播放列表的绝对地址（用于把相对地址转绝对）。</param>
    /// <returns>解析结果。</returns>
    /// <exception cref="ArgumentException">内容为空时抛出。</exception>
    public static HlsPlaylist Parse(string content, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("播放列表内容为空。", nameof(content));
        }

        bool isMaster = false;
        int targetDuration = 0;
        long mediaSequence = 0;
        bool isEndList = false;
        List<string> variants = [];
        List<string> segments = [];
        List<double> durations = [];
        bool pendingStreamInf = false;
        bool pendingSegment = false;
        double pendingDuration = 0;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith(StreamInfTag, StringComparison.Ordinal))
                {
                    isMaster = true;
                    pendingStreamInf = true;
                }
                else if (line.StartsWith(ExtInfTag, StringComparison.Ordinal))
                {
                    pendingSegment = true;
                    pendingDuration = ParseExtInfDuration(line);
                }
                else if (line.StartsWith(TargetDurationTag, StringComparison.Ordinal))
                {
                    targetDuration = ParseIntAfterColon(line, TargetDurationTag.Length);
                }
                else if (line.StartsWith(MediaSequenceTag, StringComparison.Ordinal))
                {
                    mediaSequence = ParseIntAfterColon(line, MediaSequenceTag.Length);
                }
                else if (line.StartsWith(EndListTag, StringComparison.Ordinal))
                {
                    isEndList = true;
                }

                continue;
            }

            string resolved = ResolveUri(line, baseUri);
            if (pendingStreamInf)
            {
                variants.Add(resolved);
                pendingStreamInf = false;
                continue;
            }

            if (pendingSegment)
            {
                segments.Add(resolved);
                durations.Add(pendingDuration > 0 ? pendingDuration : targetDuration);
                pendingSegment = false;
                continue;
            }

            // 无标签的裸地址：按分片处理（部分直播源会省略 EXTINF），时长回退为目标时长。
            segments.Add(resolved);
            durations.Add(targetDuration);
        }

        return new HlsPlaylist(isMaster, variants, segments, durations, targetDuration, isEndList, mediaSequence);
    }

    /// <summary>解析 <c>#EXTINF:&lt;duration&gt;,...</c> 的时长部分。</summary>
    /// <param name="line">EXTINF 行。</param>
    /// <returns>时长（秒）；无法解析时返回 0。</returns>
    private static double ParseExtInfDuration(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return 0;
        }

        string value = line[(colon + 1)..].Trim();
        int comma = value.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            value = value[..comma];
        }

        return double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double parsed) && parsed > 0
            ? parsed
            : 0;
    }

    private static string ResolveUri(string uri, Uri baseUri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? absolute))
        {
            return absolute.ToString();
        }

        return new Uri(baseUri, uri).ToString();
    }

    private static int ParseIntAfterColon(string line, int tagLength)
    {
        int colon = line.IndexOf(':', tagLength);
        if (colon < 0)
        {
            return 0;
        }

        string value = line[(colon + 1)..].Trim();
        int comma = value.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            value = value[..comma];
        }

        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
    }
}
