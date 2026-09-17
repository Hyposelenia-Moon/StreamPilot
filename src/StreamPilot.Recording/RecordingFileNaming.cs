namespace StreamPilot.Recording;

using System.Globalization;
using System.Text;
using StreamPilot.Core.Models;

/// <summary>
/// 录制文件命名：<c>{主播名}-{房间号}-{开始时间}-{分片序号}.{扩展名}</c>。
/// </summary>
/// <remarks>
/// 主播名来自平台，属于不可信输入，必须过滤非法文件名字符、去掉路径分隔符、
/// 截断超长名称，并防御"路径穿越"（<c>..</c>、绝对路径、盘符）。
/// </remarks>
public static class RecordingFileNaming
{
    /// <summary>主播名最大长度。</summary>
    public const int MaxAnchorLength = 40;

    /// <summary>分片序号宽度（零填充）。</summary>
    public const int IndexWidth = 3;

    /// <summary>时间戳格式（本地时间，便于用户查找）。</summary>
    public const string StartTimeFormat = "yyyyMMdd-HHmmss";

    /// <summary>主播名为空时使用的占位名。</summary>
    public const string UnknownAnchor = "unknown";

    /// <summary>文件名冲突时追加的后缀序号上限。</summary>
    private const int MaxCollisionSuffix = 100;

    /// <summary>
    /// 清洗主播名，得到可安全用作文件名的片段。
    /// </summary>
    /// <param name="anchor">原始主播名。</param>
    /// <returns>清洗后的名称。</returns>
    public static string SanitizeAnchor(string? anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor))
        {
            return UnknownAnchor;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder builder = new(anchor.Length);
        foreach (char character in anchor)
        {
            bool isInvalid = false;
            foreach (char candidate in invalid)
            {
                if (character == candidate)
                {
                    isInvalid = true;
                    break;
                }
            }

            if (isInvalid || character is '.' or '\\' or '/' or ':')
            {
                builder.Append('_');
                continue;
            }

            builder.Append(character);
        }

        string cleaned = builder.ToString().Trim();
        cleaned = cleaned.Trim('_');
        if (cleaned.Length == 0 || cleaned == "..")
        {
            return UnknownAnchor;
        }

        return cleaned.Length <= MaxAnchorLength ? cleaned : cleaned[..MaxAnchorLength];
    }

    /// <summary>
    /// 组装分片文件名（不含目录）。
    /// </summary>
    /// <param name="room">房间信息。</param>
    /// <param name="startedAt">录制开始时间（本地时间用于命名）。</param>
    /// <param name="segmentIndex">分片序号，从 0 开始。</param>
    /// <returns>文件名。</returns>
    public static string BuildFileName(ResolvedRoom room, DateTimeOffset startedAt, int segmentIndex)
    {
        ArgumentNullException.ThrowIfNull(room);
        string extension = ExtensionFor(room.Candidates.Count > 0 ? room.Candidates[0].Format : StreamFormat.Unknown);
        return BuildFileName(room.Platform, room.Anchor, room.RoomId, startedAt, segmentIndex, extension);
    }

    /// <summary>
    /// 组装分片文件名（不含目录）。
    /// </summary>
    /// <param name="platform">平台标识。</param>
    /// <param name="anchor">主播名。</param>
    /// <param name="roomId">房间号。</param>
    /// <param name="startedAt">录制开始时间。</param>
    /// <param name="segmentIndex">分片序号。</param>
    /// <param name="extension">扩展名（含点，例如 <c>.flv</c>）。</param>
    /// <returns>文件名。</returns>
    public static string BuildFileName(
        PlatformId platform,
        string anchor,
        string roomId,
        DateTimeOffset startedAt,
        int segmentIndex,
        string extension)
    {
        _ = platform;
        string index = Math.Max(0, segmentIndex).ToString("D" + IndexWidth.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        string time = startedAt.ToString(StartTimeFormat, CultureInfo.InvariantCulture);
        string safeRoomId = SanitizeAnchor(roomId);
        return $"{SanitizeAnchor(anchor)}-{safeRoomId}-{time}-{index}{extension}";
    }

    /// <summary>取得流格式对应的文件扩展名。</summary>
    /// <param name="format">流格式。</param>
    /// <returns>扩展名（含点）。</returns>
    public static string ExtensionFor(StreamFormat format) => format switch
    {
        StreamFormat.FlvHttp => ".flv",
        StreamFormat.HlsTs => ".ts",
        _ => ".bin",
    };

    /// <summary>取得元数据侧车文件路径。</summary>
    /// <param name="directory">输出目录。</param>
    /// <param name="anchor">主播名。</param>
    /// <param name="roomId">房间号。</param>
    /// <param name="startedAt">录制开始时间。</param>
    /// <returns>元数据文件的完整路径。</returns>
    public static string BuildMetadataPath(string directory, string anchor, string roomId, DateTimeOffset startedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string name = $"{SanitizeAnchor(anchor)}-{SanitizeAnchor(roomId)}-{startedAt.ToString(StartTimeFormat, CultureInfo.InvariantCulture)}.meta.json";
        return Path.Combine(directory, name);
    }

    /// <summary>
    /// 解析出唯一的输出路径，冲突时追加 <c>-1</c>、<c>-2</c> 后缀。
    /// </summary>
    /// <param name="directory">输出目录（必须已存在或可创建）。</param>
    /// <param name="fileName">期望的文件名。</param>
    /// <returns>当前未被占用的完整路径。</returns>
    /// <exception cref="IOException">连续冲突次数超过上限时抛出。</exception>
    public static string ResolveUniquePath(string directory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        Directory.CreateDirectory(directory);
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int suffix = 1; suffix <= MaxCollisionSuffix; suffix++)
        {
            string attempt = Path.Combine(
                directory,
                $"{stem}-{suffix.ToString(CultureInfo.InvariantCulture)}{extension}");
            if (!File.Exists(attempt))
            {
                return attempt;
            }
        }

        throw new IOException($"输出目录中同名文件过多：{fileName}");
    }
}
