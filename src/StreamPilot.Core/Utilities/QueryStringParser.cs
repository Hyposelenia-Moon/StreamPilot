namespace StreamPilot.Core.Utilities;

using System.Globalization;
using System.Text;

/// <summary>
/// URL 查询串解析工具（替代 System.Web.HttpUtility，避免额外引用）。
/// </summary>
/// <remarks>
/// 语义与浏览器 <c>URLSearchParams</c> 对齐：<c>+</c> 视为空格，重复键保留全部值。
/// </remarks>
public static class QueryStringParser
{
    /// <summary>解析查询串为"键 → 值列表"的字典（键不区分大小写）。</summary>
    /// <param name="query">不含 <c>?</c> 的查询串，可为 <see langword="null"/>。</param>
    /// <returns>解析结果；输入为空时返回空字典。</returns>
    public static Dictionary<string, List<string>> Parse(string? query)
    {
        Dictionary<string, List<string>> result = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        string normalized = query.StartsWith('?') ? query[1..] : query;
        foreach (string pair in normalized.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string rawName = equals < 0 ? pair : pair[..equals];
            string rawValue = equals < 0 ? string.Empty : pair[(equals + 1)..];
            string name = DecodeComponent(rawName);
            if (name.Length == 0)
            {
                continue;
            }

            if (!result.TryGetValue(name, out List<string>? values))
            {
                values = [];
                result[name] = values;
            }

            values.Add(DecodeComponent(rawValue));
        }

        return result;
    }

    /// <summary>解析查询串并取首个值。</summary>
    /// <param name="query">查询串。</param>
    /// <param name="name">键名。</param>
    /// <returns>首个值；不存在时返回 <see langword="null"/>。</returns>
    public static string? GetFirst(Dictionary<string, List<string>> query, string name)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.TryGetValue(name, out List<string>? values) && values.Count > 0 ? values[0] : null;
    }

    /// <summary>
    /// 按 JS <c>decodeURIComponent</c> 语义解码：先按 UTF-8 百分号解码，再把 <c>+</c> 视为空格。
    /// </summary>
    /// <param name="value">待解码文本。</param>
    /// <returns>解码后的文本；解码失败时原样返回。</returns>
    public static string DecodeComponent(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    /// <summary>
    /// 按严格的 JS <c>decodeURIComponent</c> 语义解码（<c>+</c> 保持不变），用于页面内联数据。
    /// </summary>
    /// <param name="value">待解码文本。</param>
    /// <returns>解码后的文本；解码失败时原样返回。</returns>
    public static string DecodeComponentStrict(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    /// <summary>把"键 → 值"集合编码为查询串（值会被百分号编码）。</summary>
    /// <param name="pairs">有序键值对。</param>
    /// <returns>查询串，不含前导 <c>?</c>。</returns>
    public static string Build(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        StringBuilder builder = new();
        foreach (KeyValuePair<string, string> pair in pairs)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(pair.Key)).Append('=').Append(Uri.EscapeDataString(pair.Value));
        }

        return builder.ToString();
    }

    /// <summary>从 URL 中取出查询串并解析。</summary>
    /// <param name="url">完整 URL。</param>
    /// <returns>解析结果。</returns>
    public static Dictionary<string, List<string>> ParseFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        int queryStart = url.IndexOf('?', StringComparison.Ordinal);
        return queryStart < 0 ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) : Parse(url[(queryStart + 1)..]);
    }

    /// <summary>解析 Unix 秒或毫秒时间戳为 <see cref="DateTimeOffset"/>；无法解析时返回 <see langword="null"/>。</summary>
    /// <param name="value">时间戳文本。</param>
    /// <returns>UTC 时间，或 <see langword="null"/>。</returns>
    public static DateTimeOffset? ParseUnixTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long raw))
        {
            return null;
        }

        // 十位以内按秒解释，更长按毫秒解释（平台签名参数两种都存在）。
        return raw < 100_000_000_000L
            ? DateTimeOffset.FromUnixTimeSeconds(raw)
            : DateTimeOffset.FromUnixTimeMilliseconds(raw);
    }
}
