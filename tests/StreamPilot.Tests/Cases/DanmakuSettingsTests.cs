namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Configuration;
using StreamPilot.Tests.Framework;

/// <summary>
/// 弹幕设置项的解析与限制测试。
/// </summary>
/// <remarks>
/// 屏蔽词是"用户随手输入"的典型场景：中文逗号、顿号、分号、换行混用，还常带空行与重复词。
/// 这些用例锁住切分规则，避免将来把空格也当分隔符（那样「你 好」这类词会被拆坏）。
/// </remarks>
[TestClass]
public sealed class DanmakuSettingsTests
{
    /// <summary>屏蔽词支持换行、中英文逗号、分号、顿号与竖线，并忽略空项。</summary>
    [TestMethod("弹幕设置：屏蔽词支持多种分隔符")]
    public void ParsesKeywordsWithMixedSeparators()
    {
        IReadOnlyList<string> keywords = DanmakuKeywordList.Parse("广告\n加群,代练，刷屏;返利；外挂、抽奖|引流");

        Assert.Equal(8, keywords.Count, "八种分隔方式各切出一条");
        Assert.Equal("广告", keywords[0]);
        Assert.Equal("加群", keywords[1]);
        Assert.Equal("代练", keywords[2]);
        Assert.Equal("刷屏", keywords[3]);
        Assert.Equal("返利", keywords[4]);
        Assert.Equal("外挂", keywords[5]);
        Assert.Equal("抽奖", keywords[6]);
        Assert.Equal("引流", keywords[7]);
    }

    /// <summary>空格是词的一部分：不能把「你 好」拆成两条。</summary>
    [TestMethod("弹幕设置：空格不作分隔符")]
    public void KeepsSpacesInsideKeyword()
    {
        IReadOnlyList<string> keywords = DanmakuKeywordList.Parse("你 好\n 多余空白 ");

        Assert.Equal(2, keywords.Count);
        Assert.Equal("你 好", keywords[0], "词内空格必须保留");
        Assert.Equal("多余空白", keywords[1], "首尾空白要去掉");
    }

    /// <summary>重复与空行要去重（忽略大小写），否则同一词会在配置里越攒越多。</summary>
    [TestMethod("弹幕设置：屏蔽词去重与去空")]
    public void DeduplicatesKeywords()
    {
        IReadOnlyList<string> keywords = DanmakuKeywordList.Parse("ABC\nabc\n\n  \nABC\n");

        Assert.Equal(1, keywords.Count, "忽略大小写去重后只剩一条");
        Assert.Equal("ABC", keywords[0]);
    }

    /// <summary>空输入返回空列表，而不是 null。</summary>
    [TestMethod("弹幕设置：空输入返回空列表")]
    public void HandlesEmptyInput()
    {
        Assert.Equal(0, DanmakuKeywordList.Parse(null).Count);
        Assert.Equal(0, DanmakuKeywordList.Parse(string.Empty).Count);
        Assert.Equal(0, DanmakuKeywordList.Parse(" , ， ; \n ").Count, "只有分隔符时不能产出空关键词");
    }

    /// <summary>格式化回文本框：每行一条，空项不占行。</summary>
    [TestMethod("弹幕设置：屏蔽词回填为每行一条")]
    public void FormatsKeywordsLineByLine()
    {
        string text = DanmakuKeywordList.Format(["广告", "  ", "加群"]);

        Assert.Equal("广告" + Environment.NewLine + "加群", text);
        Assert.Equal(string.Empty, DanmakuKeywordList.Format(null));
        Assert.Equal(string.Empty, DanmakuKeywordList.Format([]));
    }

    /// <summary>条数上限：超限要能判定出来，供设置窗口给出可操作提示。</summary>
    [TestMethod("弹幕设置：屏蔽词条数上限")]
    public void RejectsTooManyKeywords()
    {
        List<string> withinLimit = [];
        for (int index = 0; index < DanmakuKeywordList.MaxKeywordCount; index++)
        {
            withinLimit.Add("kw" + index);
        }

        Assert.True(DanmakuKeywordList.IsWithinLimit(withinLimit), "刚好到上限应通过");

        withinLimit.Add("kw-overflow");
        Assert.False(DanmakuKeywordList.IsWithinLimit(withinLimit), "超出上限必须判定为超限");
    }

    /// <summary>默认配置关闭弹幕：未经用户同意不遮挡画面（v0.2.2 评审结论）。</summary>
    [TestMethod("弹幕设置：默认关闭")]
    public void DanmakuDisabledByDefault()
    {
        DanmakuOptions options = new();

        Assert.False(options.Enabled, "默认必须关闭");
        Assert.Equal(DanmakuDisplayMode.Compact, options.DisplayMode, "默认精简版");
        Assert.Equal(DanmakuFilterMode.ChatOnly, options.Filter, "默认只看弹幕");
        Assert.True(options.MergeDuplicates, "默认合并重复弹幕");
        Assert.Equal(0, options.BlockedKeywords.Count);
    }

    /// <summary>
    /// 配置收敛：越界数值夹到边界、屏蔽词去空去重。
    /// </summary>
    /// <remarks>
    /// 配置文件可能被手工改过（比如把字号写成 0、横穿耗时写成 0）：渲染侧不该为此写防御代码，
    /// 读写两侧统一在 <see cref="DanmakuOptions.Normalize"/> 里收敛一次。
    /// </remarks>
    [TestMethod("弹幕设置：越界数值收敛到边界")]
    public void NormalizesOutOfRangeValues()
    {
        DanmakuOptions raw = new()
        {
            Opacity = 3.5,
            FontSize = 0,
            SpeedSeconds = 0,
            MaxOnScreen = 5,
            MergeWindowSeconds = 99,
        };

        DanmakuOptions normalized = raw.Normalize();

        Assert.EqualDouble(1.0, normalized.Opacity, 1e-9, "不透明度不得超过 1");
        Assert.Equal(DanmakuOptions.MinFontSize, normalized.FontSize, "字号下限");
        Assert.Equal(DanmakuOptions.MinSpeedSeconds, normalized.SpeedSeconds, "横穿耗时下限");
        Assert.Equal(DanmakuOptions.MinMaxOnScreen, normalized.MaxOnScreen, "同屏上限下限");
        Assert.Equal(DanmakuOptions.MaxMergeWindowSeconds, normalized.MergeWindowSeconds, "合并窗口上限");

        Assert.EqualDouble(
            0.9,
            (new DanmakuOptions { Opacity = double.NaN }).Normalize().Opacity,
            1e-9,
            "NaN 要回落到默认不透明度（不是夹到边界），否则前端会算出无效动画时长");
        Assert.Equal(
            DanmakuOptions.MaxFontSize,
            (new DanmakuOptions { FontSize = 999 }).Normalize().FontSize,
            "字号上限");
    }

    /// <summary>收敛时顺带把屏蔽词规范化：重复项与空项不会留在配置里。</summary>
    [TestMethod("弹幕设置：收敛时清理屏蔽词")]
    public void NormalizesBlockedKeywords()
    {
        DanmakuOptions normalized = (new DanmakuOptions
        {
            BlockedKeywords = ["广告", " 广告 ", string.Empty, "加群"],
        }).Normalize();

        Assert.Equal(2, normalized.BlockedKeywords.Count, "去重去空后只剩两条");
        Assert.Equal("广告", normalized.BlockedKeywords[0]);
        Assert.Equal("加群", normalized.BlockedKeywords[1]);
    }
}
