namespace StreamPilot.Core.Configuration;

/// <summary>
/// 屏蔽词列表的解析与格式化（设置界面与配置之间的转换）。
/// </summary>
/// <remarks>
/// 屏蔽词由用户在一行里随手输入，分隔符不会统一：中文逗号、顿号、分号、换行、
/// 空格混用都很常见。这里统一按「换行 / 逗号 / 分号 / 顿号 / 竖线」切分，其它字符原样保留
/// （空格是词的一部分，比如「你 好」这种梗，不能当分隔符吃掉）。
/// </remarks>
public static class DanmakuKeywordList
{
    /// <summary>屏蔽词条数上限。</summary>
    /// <remarks>
    /// 只是防止配置被写成几千条导致每条弹幕都要跑一遍长循环；正常使用远达不到。
    /// </remarks>
    public const int MaxKeywordCount = 200;

    /// <summary>分隔符：换行、半角逗号/分号、全角逗号/分号/顿号、竖线。</summary>
    private static readonly char[] Separators = ['\r', '\n', ',', '，', ';', '；', '、', '|'];

    /// <summary>
    /// 把用户输入的文本切成屏蔽词列表（去空白、去空项、忽略大小写去重）。
    /// </summary>
    /// <param name="text">用户输入的多行/多分隔符文本，可为 <see langword="null"/>。</param>
    /// <returns>屏蔽词列表；输入为空时返回空列表。</returns>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<string> keywords = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            string keyword = part.Trim();
            if (keyword.Length == 0 || !seen.Add(keyword))
            {
                continue;
            }

            keywords.Add(keyword);
        }

        return keywords;
    }

    /// <summary>
    /// 把屏蔽词列表格式化成界面文本框的内容（每行一条）。
    /// </summary>
    /// <param name="keywords">屏蔽词列表，可为 <see langword="null"/>。</param>
    /// <returns>每行一条的文本；列表为空时返回空字符串。</returns>
    public static string Format(IEnumerable<string>? keywords)
    {
        if (keywords is null)
        {
            return string.Empty;
        }

        List<string> lines = [];
        foreach (string keyword in keywords)
        {
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                lines.Add(keyword.Trim());
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 校验条数是否超过 <see cref="MaxKeywordCount"/>。
    /// </summary>
    /// <param name="keywords">已解析的屏蔽词。</param>
    /// <returns>超过上限返回 <see langword="false"/>。</returns>
    public static bool IsWithinLimit(IReadOnlyCollection<string> keywords)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        return keywords.Count <= MaxKeywordCount;
    }
}
