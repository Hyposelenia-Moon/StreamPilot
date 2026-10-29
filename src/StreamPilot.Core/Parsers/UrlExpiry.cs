namespace StreamPilot.Core.Parsers;

using StreamPilot.Core.Utilities;

/// <summary>
/// 从直播流地址的查询参数里读取"签名何时过期"。
/// </summary>
/// <remarks>
/// <para>
/// 只有 B站会把 <c>expires</c> 明确写在地址里并由解析器单独读取；抖音、Bigo 等平台同样把
/// 时效写进签名地址（<c>expire</c> / <c>expires</c> 等），但解析器过去一律当作"未知"，
/// 于是失效地址仍会被当作可用候选下发给页面，页面探测、重试、切换后才走到重新解析。
/// </para>
/// <para>
/// 这里只做"尽力而为"的读取：参数名不匹配或值不可解析时返回 <see langword="null"/>，
/// 调用方按"平台未声明有效期"处理（与过去行为一致），不会因为读不到就更保守地拒绝候选。
/// </para>
/// </remarks>
public static class UrlExpiry
{
    /// <summary>表示"过期时间"的查询参数名（不区分大小写）。</summary>
    private static readonly string[] ParameterNames =
    [
        "expire",
        "expires",
        "expires_at",
        "expire_at",
        "expired_at",
        "deadline",
    ];

    /// <summary>合理区间下限：早于此时刻的"过期时间"视为解析错误。</summary>
    private static readonly DateTimeOffset LowerBound = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>合理区间上限：晚于此时刻的"过期时间"视为解析错误。</summary>
    private static readonly DateTimeOffset UpperBound = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 尝试从地址中读取过期时间。
    /// </summary>
    /// <param name="url">带签名的流地址，可为 <see langword="null"/>。</param>
    /// <returns>过期时间（UTC）；未声明或无法解析时返回 <see langword="null"/>。</returns>
    public static DateTimeOffset? TryRead(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        int queryIndex = url.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0 || queryIndex == url.Length - 1)
        {
            return null;
        }

        Dictionary<string, List<string>> query = QueryStringParser.Parse(url[(queryIndex + 1)..]);
        foreach (string name in ParameterNames)
        {
            DateTimeOffset? parsed = TryParseValue(QueryStringParser.GetFirst(query, name));
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>解析单个参数值（Unix 秒 / 毫秒），并过滤明显不合理的取值。</summary>
    /// <param name="value">参数值。</param>
    /// <returns>过期时间；无法解析或超出合理区间时返回 <see langword="null"/>。</returns>
    private static DateTimeOffset? TryParseValue(string? value)
    {
        DateTimeOffset? parsed;
        try
        {
            parsed = QueryStringParser.ParseUnixTimestamp(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 值超出 DateTimeOffset 的可表示范围（例如被截断/拼接的签名参数）：按"未声明"处理。
            return null;
        }

        return parsed is { } expiresAt && expiresAt >= LowerBound && expiresAt <= UpperBound ? expiresAt : null;
    }
}
