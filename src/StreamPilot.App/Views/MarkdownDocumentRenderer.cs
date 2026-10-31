namespace StreamPilot.App.Views;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using StreamPilot.Core.Updates;

/// <summary>
/// 把 <see cref="MarkdownParser"/> 解析出的结构渲染成 WPF <see cref="FlowDocument"/>。
/// </summary>
/// <remarks>
/// 用原生文档控件而不是内嵌 WebView：更新说明只是几段文字，为它启动一个浏览器内核
/// 既拖慢窗口打开，也会让窗口多一层异步初始化失败的可能。
/// </remarks>
internal static class MarkdownDocumentRenderer
{
    /// <summary>正文颜色（资源缺失时的回退值，与浅色主题一致）。</summary>
    private static readonly Color TextFallback = Color.FromRgb(0x1F, 0x23, 0x29);

    /// <summary>次要文字颜色（引用块用）。</summary>
    private static readonly Color MutedFallback = Color.FromRgb(0x5B, 0x64, 0x72);

    /// <summary>代码块底色。</summary>
    private static readonly Color CodeFallback = Color.FromRgb(0xF3, 0xF5, 0xF8);

    /// <summary>分隔线 / 边框颜色。</summary>
    private static readonly Color BorderFallback = Color.FromRgb(0xE2, 0xE5, 0xEA);

    /// <summary>
    /// 渲染 Markdown。
    /// </summary>
    /// <param name="markdown">Markdown 原文。</param>
    /// <returns>可直接赋给 <c>FlowDocumentScrollViewer.Document</c> 的文档。</returns>
    public static FlowDocument Render(string? markdown)
    {
        FlowDocument document = new()
        {
            PagePadding = new Thickness(0),
            FontSize = 13.5,
            LineHeight = 21,
            FontFamily = UiFontFamily(),
            Foreground = Brush("TextBrush", TextFallback),
        };

        int orderedCounter = 0;
        foreach (MarkdownBlock block in MarkdownParser.Parse(markdown))
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    orderedCounter = 0;
                    document.Blocks.Add(BuildHeading(block));
                    break;

                case MarkdownBlockKind.ListItem when block.Ordered:
                    orderedCounter++;
                    document.Blocks.Add(BuildListItem(block, orderedCounter.ToString(System.Globalization.CultureInfo.CurrentCulture) + ". "));
                    break;

                case MarkdownBlockKind.ListItem:
                    document.Blocks.Add(BuildListItem(block, "• "));
                    break;

                case MarkdownBlockKind.CodeBlock:
                    orderedCounter = 0;
                    document.Blocks.Add(BuildCodeBlock(block));
                    break;

                case MarkdownBlockKind.Quote:
                    orderedCounter = 0;
                    document.Blocks.Add(BuildQuote(block));
                    break;

                case MarkdownBlockKind.Rule:
                    orderedCounter = 0;
                    document.Blocks.Add(new BlockUIContainer(new Border
                    {
                        Height = 1,
                        Background = Brush("BorderBrushSoft", BorderFallback),
                        Margin = new Thickness(0, 10, 0, 10),
                    }));
                    break;

                default:
                    orderedCounter = 0;
                    document.Blocks.Add(BuildParagraph(block));
                    break;
            }
        }

        return document;
    }

    private static Paragraph BuildHeading(MarkdownBlock block)
    {
        double size = block.Level switch
        {
            1 => 21,
            2 => 18,
            3 => 16,
            _ => 14.5,
        };

        Paragraph paragraph = new()
        {
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, block.Level <= 2 ? 14 : 10, 0, 6),
            LineHeight = size * 1.5,
        };
        AppendInlines(paragraph, block.Text);
        return paragraph;
    }

    private static Paragraph BuildParagraph(MarkdownBlock block)
    {
        Paragraph paragraph = new() { Margin = new Thickness(0, 0, 0, 8) };
        AppendInlines(paragraph, block.Text);
        return paragraph;
    }

    private static Paragraph BuildListItem(MarkdownBlock block, string marker)
    {
        Paragraph paragraph = new()
        {
            Margin = new Thickness(16, 0, 0, 5),
            TextIndent = -14,
        };
        paragraph.Inlines.Add(new Run(marker));
        AppendInlines(paragraph, block.Text);
        return paragraph;
    }

    private static Paragraph BuildCodeBlock(MarkdownBlock block)
    {
        Paragraph paragraph = new()
        {
            FontFamily = MonoFontFamily(),
            FontSize = 12.5,
            Background = Brush("PanelAltBrush", CodeFallback),
            BorderBrush = Brush("BorderBrushSoft", BorderFallback),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 10),
        };

        string[] codeLines = block.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int index = 0; index < codeLines.Length; index++)
        {
            if (index > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            paragraph.Inlines.Add(new Run(codeLines[index]));
        }

        return paragraph;
    }

    private static Paragraph BuildQuote(MarkdownBlock block)
    {
        Paragraph paragraph = new()
        {
            BorderBrush = Brush("BorderBrushSoft", BorderFallback),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(0, 4, 0, 8),
            Foreground = Brush("MutedTextBrush", MutedFallback),
        };
        AppendInlines(paragraph, block.Text);
        return paragraph;
    }

    private static void AppendInlines(Paragraph paragraph, string text)
    {
        foreach (MarkdownInline part in MarkdownParser.ParseInline(text))
        {
            switch (part.Kind)
            {
                case MarkdownInlineKind.Bold:
                    paragraph.Inlines.Add(new Bold(new Run(part.Text)));
                    break;

                case MarkdownInlineKind.Italic:
                    paragraph.Inlines.Add(new Italic(new Run(part.Text)));
                    break;

                case MarkdownInlineKind.Code:
                    paragraph.Inlines.Add(new Span(new Run(part.Text))
                    {
                        FontFamily = MonoFontFamily(),
                        FontSize = 12.5,
                        Background = Brush("PanelAltBrush", CodeFallback),
                    });
                    break;

                case MarkdownInlineKind.Link:
                    paragraph.Inlines.Add(BuildHyperlink(part));
                    break;

                default:
                    paragraph.Inlines.Add(new Run(part.Text));
                    break;
            }
        }
    }

    private static Hyperlink BuildHyperlink(MarkdownInline part)
    {
        Hyperlink link = new(new Run(part.Text))
        {
            Foreground = Brush("AccentStrongBrush", Color.FromRgb(0x1B, 0x4F, 0xD8)),
        };

        if (Uri.TryCreate(part.Url, UriKind.Absolute, out Uri? uri))
        {
            link.NavigateUri = uri;
            link.RequestNavigate += static (_, args) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // 说明里的链接打不开不该打断更新流程：静默忽略，用户仍可复制地址。
                }

                args.Handled = true;
            };
        }

        return link;
    }

    private static FontFamily UiFontFamily() =>
        Application.Current?.TryFindResource("UiFontFamily") as FontFamily
        ?? new FontFamily("Microsoft YaHei UI");

    private static FontFamily MonoFontFamily() => new("Consolas, Cascadia Mono, Courier New");

    private static Brush Brush(string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey) is Brush brush)
        {
            return brush;
        }

        SolidColorBrush created = new(fallback);
        created.Freeze();
        return created;
    }
}
