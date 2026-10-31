namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Updates;
using StreamPilot.Tests.Framework;

/// <summary>
/// 极简 Markdown 解析：Release 说明里会出现的语法必须解析成结构化块，而不是把符号直接丢给界面。
/// </summary>
/// <remarks>
/// 渲染在 WPF 层（FlowDocument），无法在 net10.0 的测试工程里断言；但"解析成什么块"是纯逻辑，
/// 这里把边界情况覆盖住：标题级别、列表类型、行内标记、代码块、软换行合并、非 http 链接。
/// </remarks>
[TestClass]
public sealed class MarkdownParserTests
{
    /// <summary>标题按井号数量分级，且标题文字不含标记。</summary>
    [TestMethod("Markdown：标题分级与文字提取")]
    public void ParsesHeadings()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse(
            """
            # 一级
            ## 二级
            ### 三级
            """);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal("一级", blocks[0].Text);
        Assert.Equal(2, blocks[1].Level);
        Assert.Equal(3, blocks[2].Level);
    }

    /// <summary>无序与有序列表分别识别，连续普通行合并为一段。</summary>
    [TestMethod("Markdown：列表识别与段落软换行合并")]
    public void ParsesListsAndParagraphs()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse(
            """
            - 第一项
            - 第二项
            1. 有序一
            2. 有序二

            这是第一行
            这是第二行
            """);

        Assert.Equal(5, blocks.Count, "四个列表项 + 一个合并后的段落");
        Assert.Equal(MarkdownBlockKind.ListItem, blocks[0].Kind);
        Assert.False(blocks[0].Ordered, "短横线是无序列表");
        Assert.Equal("第一项", blocks[0].Text);
        Assert.True(blocks[2].Ordered, "数字开头是有序列表");
        Assert.Equal("有序一", blocks[2].Text);
        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[4].Kind);
        Assert.Equal("这是第一行 这是第二行", blocks[4].Text, "软换行应合并成空格，而不是保留硬换行");
    }

    /// <summary>代码块原样保留内容与语言标识，内部的 Markdown 符号不参与解析。</summary>
    [TestMethod("Markdown：代码块保留原文与语言")]
    public void ParsesCodeBlocks()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse(
            """
            说明文字

            ```powershell
            dotnet build -c Release
            # 这是代码里的注释
            ```
            """);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(MarkdownBlockKind.CodeBlock, blocks[1].Kind);
        Assert.Equal("powershell", blocks[1].Language);
        Assert.True(blocks[1].Text.Contains("dotnet build -c Release", StringComparison.Ordinal), "代码内容原样保留");
        Assert.True(blocks[1].Text.Contains("# 这是代码里的注释", StringComparison.Ordinal), "代码块内的 # 不当标题");
    }

    /// <summary>引用与分割线。</summary>
    [TestMethod("Markdown：引用与分割线")]
    public void ParsesQuoteAndRule()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse(
            """
            > 这是一段引用
            ---
            """);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(MarkdownBlockKind.Quote, blocks[0].Kind);
        Assert.Equal("这是一段引用", blocks[0].Text);
        Assert.Equal(MarkdownBlockKind.Rule, blocks[1].Kind);
    }

    /// <summary>行内标记：粗体、斜体、行内代码、链接各自成型。</summary>
    [TestMethod("Markdown：行内标记解析")]
    public void ParsesInlineMarks()
    {
        IReadOnlyList<MarkdownInline> parts = MarkdownParser.ParseInline(
            "普通**粗体**与*斜体*以及`code`和[链接](https://example.com)");

        Assert.Equal(8, parts.Count, "四段普通文本 + 四个标记片段");
        Assert.Equal(MarkdownInlineKind.Text, parts[0].Kind);
        Assert.Equal("普通", parts[0].Text);
        Assert.Equal(MarkdownInlineKind.Bold, parts[1].Kind);
        Assert.Equal("粗体", parts[1].Text);
        Assert.Equal(MarkdownInlineKind.Italic, parts[3].Kind);
        Assert.Equal("斜体", parts[3].Text);
        Assert.Equal(MarkdownInlineKind.Code, parts[5].Kind);
        Assert.Equal("code", parts[5].Text);
        Assert.Equal(MarkdownInlineKind.Link, parts[7].Kind);
        Assert.Equal("链接", parts[7].Text);
        Assert.Equal("https://example.com", parts[7].Url);
    }

    /// <summary>非 http/https 的链接保持纯文本，避免说明里出现可执行协议。</summary>
    [TestMethod("Markdown：非 http 链接不当作链接")]
    public void RejectsNonWebLinks()
    {
        IReadOnlyList<MarkdownInline> parts = MarkdownParser.ParseInline("[点我](javascript:alert(1))");

        Assert.Equal(1, parts.Count);
        Assert.Equal(MarkdownInlineKind.Text, parts[0].Kind);
        Assert.Equal("[点我](javascript:alert(1))", parts[0].Text, "原样保留，不生成可点击链接");
    }

    /// <summary>空输入与未闭合代码块都不能丢内容或抛异常。</summary>
    [TestMethod("Markdown：空输入与未闭合代码块")]
    public void HandlesEmptyAndUnclosedCode()
    {
        Assert.Equal(0, MarkdownParser.Parse("").Count, "空串返回空列表");
        Assert.Equal(0, MarkdownParser.Parse(null).Count, "null 返回空列表");

        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse("```\n没有围栏结尾\n");
        Assert.Equal(1, blocks.Count);
        Assert.Equal(MarkdownBlockKind.CodeBlock, blocks[0].Kind);
        Assert.Equal("没有围栏结尾", blocks[0].Text);
    }
}
