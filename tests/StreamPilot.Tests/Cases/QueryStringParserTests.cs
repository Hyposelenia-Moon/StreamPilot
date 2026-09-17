namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Utilities;
using StreamPilot.Tests.Framework;

/// <summary>
/// <see cref="QueryStringParser"/> 的单元测试（正常 / 异常 / 边界）。
/// </summary>
[TestClass]
public sealed class QueryStringParserTests
{
    /// <summary>重复键保留全部值，且按 <c>+</c> 视为空格解析。</summary>
    [TestMethod("查询串解析：重复键与加号语义")]
    public void ParsesRepeatedKeys()
    {
        Dictionary<string, List<string>> query = QueryStringParser.Parse("a=1&a=2&b=hello+world&c=");

        Assert.Equal(2, query["a"].Count);
        Assert.Equal("1", query["a"][0]);
        Assert.Equal("2", query["a"][1]);
        Assert.Equal("hello world", QueryStringParser.GetFirst(query, "b"));
        Assert.Equal(string.Empty, QueryStringParser.GetFirst(query, "c"));
        Assert.Null(QueryStringParser.GetFirst(query, "missing"));
    }

    /// <summary>空输入返回空字典。</summary>
    [TestMethod("查询串解析：空输入与问号前缀")]
    public void HandlesEmptyInput()
    {
        Assert.Equal(0, QueryStringParser.Parse(null).Count);
        Assert.Equal(0, QueryStringParser.Parse(string.Empty).Count);
        Assert.Equal("1", QueryStringParser.GetFirst(QueryStringParser.Parse("?a=1"), "a"));
    }

    /// <summary>百分号解码与非法转义的行为。</summary>
    [TestMethod("查询串解析：百分号解码与非法转义")]
    public void DecodesPercentEncoding()
    {
        Dictionary<string, List<string>> query = QueryStringParser.Parse("t=%E4%B8%AD%E6%96%87&bad=%E4%B8");
        Assert.Equal("中文", QueryStringParser.GetFirst(query, "t"));
        Assert.NotNull(QueryStringParser.GetFirst(query, "bad"));
    }

    /// <summary>严格解码保持 <c>+</c> 不变（JS decodeURIComponent 语义）。</summary>
    [TestMethod("严格解码：加号不被转换为空格")]
    public void StrictDecodeKeepsPlus()
    {
        Assert.Equal("a+b", QueryStringParser.DecodeComponentStrict("a+b"));
        Assert.Equal("a b", QueryStringParser.DecodeComponent("a+b"));
    }

    /// <summary>时间戳按位数区分秒与毫秒。</summary>
    [TestMethod("时间戳解析：秒与毫秒、非法输入")]
    public void ParsesUnixTimestamps()
    {
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            QueryStringParser.ParseUnixTimestamp("1700000000"));
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
            QueryStringParser.ParseUnixTimestamp("1700000000000"));
        Assert.Null(QueryStringParser.ParseUnixTimestamp(null));
        Assert.Null(QueryStringParser.ParseUnixTimestamp("abc"));
    }

    /// <summary>构建查询串会做百分号转义。</summary>
    [TestMethod("查询串构建：转义特殊字符")]
    public void BuildsEncodedQuery()
    {
        string query = QueryStringParser.Build(
        [
            new KeyValuePair<string, string>("a", "1"),
            new KeyValuePair<string, string>("b", "x y"),
        ]);

        Assert.Equal("a=1&b=x%20y", query);
    }
}
