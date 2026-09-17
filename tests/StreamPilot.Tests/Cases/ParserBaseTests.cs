namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Errors;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Parsers;
using StreamPilot.Tests.Framework;

/// <summary>
/// <see cref="PlatformParserBase"/> 的输入校验与失败包装测试。
/// </summary>
[TestClass]
public sealed class ParserBaseTests
{
    /// <summary>房间号格式非法时抛出 InvalidInput。</summary>
    [TestMethod("解析器基类：非法房间号被拒绝")]
    public async Task RejectsInvalidRoomId()
    {
        StubParser parser = new();
        string[] invalidInputs = ["房间号", "a b", "a/b", "a?b", new string('x', 65), ""];

        foreach (string input in invalidInputs)
        {
            ResolveException exception = await Assert.ThrowsAsync<ResolveException>(() =>
                parser.ParseAsync(RoomQuery.FromRoomId(PlatformId.Bilibili, input), CancellationToken.None));
            Assert.Equal(ResolveFailure.InvalidInput, exception.Failure, "输入：" + input);
        }
    }

    /// <summary>链接域名不属于平台时被拒绝。</summary>
    [TestMethod("解析器基类：链接域名校验")]
    public async Task RejectsForeignRoomUrl()
    {
        StubParser parser = new();

        ResolveException exception = await Assert.ThrowsAsync<ResolveException>(() =>
            parser.ParseAsync(RoomQuery.FromUrl(PlatformId.Bilibili, "https://evil.example/123"), CancellationToken.None));
        Assert.Equal(ResolveFailure.InvalidInput, exception.Failure);

        // 子域与未知 scheme 同样被拒绝。
        ResolveException schemeException = await Assert.ThrowsAsync<ResolveException>(() =>
            parser.ParseAsync(RoomQuery.FromUrl(PlatformId.Bilibili, "file:///etc/passwd"), CancellationToken.None));
        Assert.Equal(ResolveFailure.InvalidInput, schemeException.Failure);
    }

    /// <summary>子域链接被接受。</summary>
    [TestMethod("解析器基类：子域链接被接受")]
    public async Task AcceptsSubdomainRoomUrl()
    {
        StubParser parser = new();
        ResolvedRoom room = await parser.ParseAsync(
            RoomQuery.FromUrl(PlatformId.Bilibili, "https://live.bilibili.com/123"),
            CancellationToken.None);
        Assert.Equal("123", room.RoomId);
    }

    /// <summary>平台不匹配时立即失败。</summary>
    [TestMethod("解析器基类：平台不匹配被拒绝")]
    public async Task RejectsWrongPlatform()
    {
        StubParser parser = new();
        ResolveException exception = await Assert.ThrowsAsync<ResolveException>(() =>
            parser.ParseAsync(RoomQuery.FromRoomId(PlatformId.Douyu, "123"), CancellationToken.None));
        Assert.Equal(ResolveFailure.InvalidInput, exception.Failure);
    }

    /// <summary>无候选流时基类统一转换为 NotLive。</summary>
    [TestMethod("解析器基类：无候选流转为未开播")]
    public async Task ConvertsEmptyCandidatesToNotLive()
    {
        StubParser parser = new() { ReturnNoCandidates = true };
        ResolveException exception = await Assert.ThrowsAsync<ResolveException>(() =>
            parser.ParseAsync(RoomQuery.FromRoomId(PlatformId.Bilibili, "123"), CancellationToken.None));
        Assert.Equal(ResolveFailure.NotLive, exception.Failure);
    }

    /// <summary>未预期异常被包装为 ParseError 而不是静默吞掉。</summary>
    [TestMethod("解析器基类：未预期异常转为解析错误")]
    public async Task WrapsUnexpectedExceptions()
    {
        StubParser parser = new() { ThrowUnexpected = true };
        ResolveException exception = await Assert.ThrowsAsync<ResolveException>(() =>
            parser.ParseAsync(RoomQuery.FromRoomId(PlatformId.Bilibili, "123"), CancellationToken.None));
        Assert.Equal(ResolveFailure.ParseError, exception.Failure);
        Assert.NotNull(exception.InnerException);
    }

    /// <summary>已取消的令牌被原样传递（不包装成解析错误）。</summary>
    [TestMethod("解析器基类：取消不被包装")]
    public async Task PropagatesCancellation()
    {
        StubParser parser = new() { ThrowCancellation = true };
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            parser.ParseAsync(RoomQuery.FromRoomId(PlatformId.Bilibili, "123"), cancellation.Token));
    }

    /// <summary>从链接中提取房间号的行为。</summary>
    [TestMethod("解析器基类：从链接提取房间号")]
    public async Task ExtractsRoomIdFromUrl()
    {
        StubParser parser = new();
        ResolvedRoom room = await parser.ParseAsync(
            RoomQuery.FromUrl(PlatformId.Bilibili, "https://live.bilibili.com/2233?from=search#x"),
            CancellationToken.None);
        Assert.Equal("2233", room.RoomId);
    }

    private sealed class StubParser : PlatformParserBase
    {
        public StubParser()
            : base(NullStructuredLogger.Instance)
        {
        }

        public override PlatformId Platform => PlatformId.Bilibili;

        public override string DisplayName => "测试平台";

        public override string RoomUrlPrefix => "https://live.bilibili.com/";

        public bool ReturnNoCandidates { get; init; }

        public bool ThrowUnexpected { get; init; }

        public bool ThrowCancellation { get; init; }

        protected override Task<ResolvedRoom> OnParseAsync(RoomQuery query, CancellationToken cancellationToken)
        {
            if (ThrowCancellation)
            {
                throw new OperationCanceledException();
            }

            if (ThrowUnexpected)
            {
                throw new InvalidOperationException("平台响应结构变化");
            }

            StreamCandidateBuilder builder = new(Platform);
            if (!ReturnNoCandidates)
            {
                builder.TryAdd("https://a.example/live.flv", StreamFormat.FlvHttp, VideoCodec.Avc, StreamQuality.Hd1080);
            }

            string roomId = query.RoomId ?? TryExtractRoomIdFromUrl(query.RoomUrl) ?? "0";
            return Task.FromResult(new ResolvedRoom
            {
                Platform = Platform,
                RoomId = roomId,
                Anchor = "测试主播",
                Title = "测试标题",
                Candidates = builder.Build(),
                ResolvedAt = DateTimeOffset.UtcNow,
            });
        }
    }
}
