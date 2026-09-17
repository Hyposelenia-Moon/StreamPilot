namespace StreamPilot.Tests.Cases;

using StreamPilot.Core.Caching;
using StreamPilot.Core.Models;
using StreamPilot.Core.Services;
using StreamPilot.Tests.Framework;

/// <summary>
/// <see cref="StreamCache"/> 与 <see cref="CandidateValidity"/> 的测试。
/// </summary>
[TestClass]
public sealed class CachingAndValidityTests
{
    /// <summary>写入后命中，过期后失效。</summary>
    [TestMethod("解析缓存：命中与过期")]
    public void HitsAndExpires()
    {
        StreamCache cache = new(ttlSeconds: 60);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ResolvedRoom room = Room("123");

        cache.Set(room, now);
        Assert.True(cache.TryGet(PlatformId.Bilibili, "123", now.AddSeconds(30), out ResolvedRoom? hit));
        Assert.NotNull(hit);
        Assert.Equal("123", hit!.RoomId);

        Assert.False(cache.TryGet(PlatformId.Bilibili, "123", now.AddSeconds(61), out _));
        Assert.Equal(0, cache.Count);
    }

    /// <summary>不同平台 / 不同房间号不串号。</summary>
    [TestMethod("解析缓存：键空间隔离")]
    public void IsolatesKeys()
    {
        StreamCache cache = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        cache.Set(Room("123"), now);

        Assert.False(cache.TryGet(PlatformId.Douyu, "123", now, out _));
        Assert.False(cache.TryGet(PlatformId.Bilibili, "456", now, out _));
        Assert.True(cache.TryGet(PlatformId.Bilibili, "123", now, out _));
    }

    /// <summary>容量上限：超过上限后淘汰最旧条目。</summary>
    [TestMethod("解析缓存：容量上限淘汰最旧")]
    public void EnforcesCapacity()
    {
        StreamCache cache = new();
        DateTimeOffset baseTime = DateTimeOffset.UtcNow;

        for (int index = 0; index <= StreamCache.Capacity; index++)
        {
            cache.Set(Room(index.ToString(System.Globalization.CultureInfo.InvariantCulture)), baseTime.AddSeconds(index));
        }

        Assert.Equal(StreamCache.Capacity, cache.Count);
        Assert.False(cache.TryGet(PlatformId.Bilibili, "0", baseTime.AddMinutes(1), out _), "最旧条目应被淘汰");
    }

    /// <summary>清空缓存。</summary>
    [TestMethod("解析缓存：清空")]
    public void Clears()
    {
        StreamCache cache = new();
        cache.Set(Room("1"), DateTimeOffset.UtcNow);
        Assert.Equal(1, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    /// <summary>有效期校验：未声明有效期视为可用。</summary>
    [TestMethod("候选有效期：未声明有效期可用")]
    public void AllowsUnknownExpiry()
    {
        CandidateValidity validity = new(Core.Logging.NullStructuredLogger.Instance);
        StreamCandidate candidate = Candidate(null);
        validity.EnsureUsable(candidate, DateTimeOffset.UtcNow);
        Assert.False(validity.IsExpired(candidate, DateTimeOffset.UtcNow));
    }

    /// <summary>有效期校验：已过期抛出网络类失败。</summary>
    [TestMethod("候选有效期：过期抛异常")]
    public void RejectsExpiredCandidate()
    {
        CandidateValidity validity = new(Core.Logging.NullStructuredLogger.Instance);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StreamCandidate candidate = Candidate(now.AddSeconds(-1));

        Assert.True(validity.IsExpired(candidate, now));
        Core.Errors.ResolveException exception = Assert.Throws<Core.Errors.ResolveException>(() =>
            validity.EnsureUsable(candidate, now));
        Assert.Equal(ResolveFailure.NetworkError, exception.Failure);

        // 边界：恰好等于当前时间也算过期。
        Assert.True(validity.IsExpired(Candidate(now), now));
    }

    /// <summary>地址协议非法时抛出解析类失败。</summary>
    [TestMethod("候选有效期：非法协议被拒绝")]
    public void RejectsInvalidScheme()
    {
        CandidateValidity validity = new(Core.Logging.NullStructuredLogger.Instance);
        StreamCandidate candidate = new()
        {
            SourceIndex = 0,
            Url = "ftp://cdn.example/x.flv",
            Format = StreamFormat.FlvHttp,
            CdnHost = "cdn.example",
            Codec = VideoCodec.Avc,
            Quality = StreamQuality.Unknown,
            UrlFingerprint = "len=1;fp=-",
        };

        Core.Errors.ResolveException exception = Assert.Throws<Core.Errors.ResolveException>(() =>
            validity.EnsureUsable(candidate, DateTimeOffset.UtcNow));
        Assert.Equal(ResolveFailure.ParseError, exception.Failure);
    }

    private static ResolvedRoom Room(string roomId) => new()
    {
        Platform = PlatformId.Bilibili,
        RoomId = roomId,
        Anchor = "anchor",
        Title = "title",
        Candidates =
        [
            new StreamCandidate
            {
                SourceIndex = 0,
                Url = "https://cdn.example/live.flv",
                Format = StreamFormat.FlvHttp,
                CdnHost = "cdn.example",
                Codec = VideoCodec.Avc,
                Quality = StreamQuality.Hd1080,
                UrlFingerprint = "len=1;fp=-",
            },
        ],
        ResolvedAt = DateTimeOffset.UtcNow,
    };

    private static StreamCandidate Candidate(DateTimeOffset? expiresAt) => new()
    {
        SourceIndex = 0,
        Url = "https://cdn.example/live.flv?sign=1",
        Format = StreamFormat.FlvHttp,
        CdnHost = "cdn.example",
        Codec = VideoCodec.Avc,
        Quality = StreamQuality.Hd1080,
        UrlFingerprint = "len=1;fp=-",
        ExpiresAt = expiresAt,
    };
}
