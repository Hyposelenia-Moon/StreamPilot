namespace StreamPilot.Core.Updates;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>Markdown 块级元素类型。</summary>
public enum MarkdownBlockKind
{
    /// <summary>标题（<c>#{1,6}</c>）。</summary>
    Heading,

    /// <summary>普通段落。</summary>
    Paragraph,

    /// <summary>列表项（无序或有序）。</summary>
    ListItem,

    /// <summary>代码块（三反引号围栏）。</summary>
    CodeBlock,

    /// <summary>引用（<c>&gt;</c>）。</summary>
    Quote,

    /// <summary>分割线。</summary>
    Rule,
}

/// <summary>Markdown 行内元素类型。</summary>
public enum MarkdownInlineKind
{
    /// <summary>普通文本。</summary>
    Text,

    /// <summary>粗体（<c>**text**</c>）。</summary>
    Bold,

    /// <summary>斜体（<c>*text*</c>）。</summary>
    Italic,

    /// <summary>行内代码（反引号）。</summary>
    Code,

    /// <summary>链接（<c>[text](url)</c>）。</summary>
    Link,
}

/// <summary>一个 Markdown 块。</summary>
/// <param name="Kind">块类型。</param>
/// <param name="Text">块的纯文本内容（不含标记）。</param>
/// <param name="Level">标题级别（1-6；其它块为 0）。</param>
/// <param name="Ordered">列表项是否为有序列表。</param>
/// <param name="Language">代码块的语言标识（可为空）。</param>
public sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    string Text,
    int Level = 0,
    bool Ordered = false,
    string Language = "");

/// <summary>一段行内内容。</summary>
/// <param name="Text">文本（链接时为链接文字）。</param>
/// <param name="Kind">行内类型。</param>
/// <param name="Url">链接地址（仅 <see cref="MarkdownInlineKind.Link"/> 有值）。</param>
public sealed record MarkdownInline(
    string Text,
    MarkdownInlineKind Kind = MarkdownInlineKind.Text,
    string Url = "");

/// <summary>
/// 极简 Markdown 解析器：只覆盖 Release 说明里会用到的语法。
/// </summary>
/// <remarks>
/// 项目基线没有可用的 NuGet 源，所以不能引入 Markdig；而更新说明不需要完整 CommonMark，
/// 只需要标题、列表、粗体、行内代码、代码块、引用与分割线这几样。
/// 解析结果是与 UI 无关的块 / 行内结构，渲染由界面层完成——这样解析逻辑可以被单元测试覆盖。
/// </remarks>
public static class MarkdownParser
{
    /// <summary>行内标记：粗体 / 斜体 / 行内代码 / 链接。</summary>
    /// <remarks>
    /// 顺序很重要：<c>**</c> 必须排在单星号前面，否则 <c>**粗体**</c> 会被当成两个斜体标记。
    /// 斜体两侧加负向断言，避免吃掉粗体的星号。
    /// </remarks>
    private static readonly Regex InlinePattern = new(
        @"\*\*(?<bold>.+?)\*\*|(?<!\*)\*(?<italic>[^*\n]+)\*(?!\*)|`(?<code>[^`\n]+)`|\[(?<linkText>[^\]\n]+)\]\((?<linkUrl>[^)\s]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HeadingPattern = new(
        @"^(?<hashes>#{1,6})\s+(?<text>.+?)\s*#*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OrderedItemPattern = new(
        @"^\s*(?<number>\d{1,3})[.)]\s+(?<text>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UnorderedItemPattern = new(
        @"^\s*[-*+]\s+(?<text>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RulePattern = new(
        @"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 把 Markdown 文本解析成块列表。
    /// </summary>
    /// <param name="markdown">Markdown 原文（可为空）。</param>
    /// <returns>块列表；空输入返回空列表。</returns>
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        List<MarkdownBlock> blocks = [];
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return blocks;
        }

        string normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalized.Split('\n');

        StringBuilder paragraph = new();
        StringBuilder code = new();
        bool inCode = false;
        string language = string.Empty;

        void FlushParagraph()
        {
            if (paragraph.Length == 0)
            {
                return;
            }

            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, paragraph.ToString().Trim()));
            paragraph.Clear();
        }

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd();

            if (inCode)
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.CodeBlock, code.ToString().TrimEnd(), Language: language));
                    code.Clear();
                    language = string.Empty;
                    inCode = false;
                }
                else
                {
                    code.AppendLine(line);
                }

                continue;
            }

            string trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                inCode = true;
                language = trimmedStart[3..].Trim();
                continue;
            }

            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (RulePattern.IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Rule, string.Empty));
                continue;
            }

            Match heading = HeadingPattern.Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(
                    MarkdownBlockKind.Heading,
                    heading.Groups["text"].Value,
                    heading.Groups["hashes"].Length));
                continue;
            }

            if (trimmedStart.StartsWith('>'))
            {
                FlushParagraph();
                string quoted = trimmedStart[1..].TrimStart();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Quote, quoted));
                continue;
            }

            Match ordered = OrderedItemPattern.Match(line);
            if (ordered.Success)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.ListItem, ordered.Groups["text"].Value, Ordered: true));
                continue;
            }

            Match unordered = UnorderedItemPattern.Match(line);
            if (unordered.Success)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.ListItem, unordered.Groups["text"].Value));
                continue;
            }

            // 连续普通行属于同一段：用空格连接（与 Markdown 的软换行一致）。
            if (paragraph.Length > 0)
            {
                paragraph.Append(' ');
            }

            paragraph.Append(line.Trim());
        }

        // 未闭合的代码块也照原样输出，不要因为少一个围栏就吞掉内容。
        if (inCode && code.Length > 0)
        {
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.CodeBlock, code.ToString().TrimEnd(), Language: language));
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>
    /// 解析一段行内 Markdown。
    /// </summary>
    /// <param name="text">行内文本（可为空）。</param>
    /// <returns>行内片段列表；无标记时返回单个纯文本片段。</returns>
    public static IReadOnlyList<MarkdownInline> ParseInline(string? text)
    {
        List<MarkdownInline> parts = [];
        if (string.IsNullOrEmpty(text))
        {
            return parts;
        }

        // 纯文本先攒着：不认识的标记（例如非 http 链接）要能和前后文本合并成同一段，
        // 否则界面上会凭空多出几个碎片段。
        StringBuilder pending = new();
        void FlushPending()
        {
            if (pending.Length == 0)
            {
                return;
            }

            parts.Add(new MarkdownInline(pending.ToString()));
            pending.Clear();
        }

        int position = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > position)
            {
                pending.Append(text.AsSpan(position, match.Index - position));
            }

            if (match.Groups["bold"].Success)
            {
                FlushPending();
                parts.Add(new MarkdownInline(match.Groups["bold"].Value, MarkdownInlineKind.Bold));
            }
            else if (match.Groups["italic"].Success)
            {
                FlushPending();
                parts.Add(new MarkdownInline(match.Groups["italic"].Value, MarkdownInlineKind.Italic));
            }
            else if (match.Groups["code"].Success)
            {
                FlushPending();
                parts.Add(new MarkdownInline(match.Groups["code"].Value, MarkdownInlineKind.Code));
            }
            else if (match.Groups["linkText"].Success)
            {
                string url = match.Groups["linkUrl"].Value;
                // 只把 http/https 当链接：说明里出现的其它协议不该被点开。
                bool isWebLink = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                    && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
                if (isWebLink)
                {
                    FlushPending();
                    parts.Add(new MarkdownInline(match.Groups["linkText"].Value, MarkdownInlineKind.Link, url));
                }
                else
                {
                    pending.Append(match.Value);
                }
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            pending.Append(text.AsSpan(position));
        }

        FlushPending();
        return parts;
    }
}
