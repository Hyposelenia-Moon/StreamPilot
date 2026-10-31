namespace StreamPilot.Core.Updates;

using System.Globalization;

/// <summary>
/// 语义化版本（<c>major.minor.patch[-preRelease]</c>），用于比较「当前版本」与 Release 的 <c>tag_name</c>。
/// </summary>
/// <remarks>
/// 只实现更新判断需要的那部分规则：数字段比较 + 正式版高于同号预发布版。
/// 预发布标识之间按序数比较（<c>beta.2</c> 与 <c>beta.10</c> 的差别对「要不要提示升级」没有影响）。
/// </remarks>
public readonly record struct SemanticVersion : IComparable<SemanticVersion>
{
    /// <summary>初始化语义化版本。</summary>
    /// <param name="major">主版本号。</param>
    /// <param name="minor">次版本号。</param>
    /// <param name="patch">修订号。</param>
    /// <param name="preRelease">预发布标识（不含前导连字符）；空表示正式版。</param>
    public SemanticVersion(int major, int minor, int patch, string preRelease = "")
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease ?? string.Empty;
    }

    /// <summary>主版本号。</summary>
    public int Major { get; }

    /// <summary>次版本号。</summary>
    public int Minor { get; }

    /// <summary>修订号。</summary>
    public int Patch { get; }

    /// <summary>预发布标识；空字符串表示正式版。</summary>
    public string PreRelease { get; }

    /// <summary>是否为预发布版。</summary>
    public bool IsPreRelease => PreRelease.Length > 0;

    /// <summary>
    /// 解析版本字符串。接受 <c>v0.2.2</c>、<c>0.2</c>（补零）、<c>0.2.2-beta.1</c>、<c>0.2.2+build</c>。
    /// </summary>
    /// <param name="text">待解析文本。</param>
    /// <param name="version">解析结果。</param>
    /// <returns>解析成功返回 <see langword="true"/>。</returns>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();

        // 允许前缀 v / V（GitHub tag 常见写法）。
        if (span.Length > 1 && (span[0] == 'v' || span[0] == 'V') && char.IsAsciiDigit(span[1]))
        {
            span = span[1..];
        }

        // 丢弃构建元数据（+ 之后的内容）。
        int plusIndex = span.IndexOf('+');
        if (plusIndex >= 0)
        {
            span = span[..plusIndex];
        }

        string preRelease = string.Empty;
        int dashIndex = span.IndexOf('-');
        if (dashIndex >= 0)
        {
            preRelease = span[(dashIndex + 1)..].Trim().ToString();
            span = span[..dashIndex];
        }

        int[] parts = [0, 0, 0];
        int partIndex = 0;
        int start = 0;
        while (start <= span.Length)
        {
            if (partIndex >= 3)
            {
                // 多于三段（例如 0.2.2.1）不是本项目的版本格式，视为非法。
                return false;
            }

            int dot = span[start..].IndexOf('.');
            ReadOnlySpan<char> segment = dot < 0 ? span[start..] : span.Slice(start, dot);
            if (segment.IsEmpty || !int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                return false;
            }

            parts[partIndex] = number;
            partIndex++;

            if (dot < 0)
            {
                break;
            }

            start += dot + 1;
        }

        if (partIndex == 0)
        {
            return false;
        }

        version = new SemanticVersion(parts[0], parts[1], parts[2], preRelease);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(SemanticVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        // 同号版本：正式版 > 预发布版（0.2.2 > 0.2.2-beta.1）。
        if (!IsPreRelease && !other.IsPreRelease)
        {
            return 0;
        }

        if (!IsPreRelease)
        {
            return 1;
        }

        if (!other.IsPreRelease)
        {
            return -1;
        }

        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    /// <summary>判断当前版本是否比另一个版本更新。</summary>
    /// <param name="other">对比版本。</param>
    /// <returns>当前版本更新时返回 <see langword="true"/>。</returns>
    public bool IsNewerThan(SemanticVersion other) => CompareTo(other) > 0;

    /// <inheritdoc />
    public override string ToString() =>
        IsPreRelease
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{PreRelease}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
