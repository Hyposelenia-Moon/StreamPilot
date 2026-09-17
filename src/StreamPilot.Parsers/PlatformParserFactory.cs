namespace StreamPilot.Parsers;

using StreamPilot.Core.Errors;
using StreamPilot.Core.Models;
using StreamPilot.Core.Services;

/// <summary>
/// 平台解析器工厂：把平台标识映射为解析器实例。
/// </summary>
/// <remarks>
/// 解析器在构造时一次性注册；新增平台只需在此登记，UI 与录制层无需改动
/// （见 docs/adr/0002-架构分层.md）。
/// </remarks>
public sealed class PlatformParserFactory : IPlatformParserFactory
{
    private readonly Dictionary<PlatformId, IPlatformParser> _parsers;
    private readonly IReadOnlyList<PlatformId> _supportedPlatforms;

    /// <summary>初始化工厂。</summary>
    /// <param name="parsers">已构造的解析器集合。</param>
    /// <exception cref="ArgumentNullException">参数为空时抛出。</exception>
    /// <exception cref="ArgumentException">同一平台注册了多个解析器时抛出。</exception>
    public PlatformParserFactory(IEnumerable<IPlatformParser> parsers)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        _parsers = [];
        foreach (IPlatformParser parser in parsers)
        {
            ArgumentNullException.ThrowIfNull(parser);
            if (!_parsers.TryAdd(parser.Platform, parser))
            {
                throw new ArgumentException($"平台 {parser.Platform} 注册了多个解析器。", nameof(parsers));
            }
        }

        List<PlatformId> platforms = [.. _parsers.Keys];
        platforms.Sort(static (left, right) => left.CompareTo(right));
        _supportedPlatforms = platforms;
    }

    /// <inheritdoc />
    public IReadOnlyList<PlatformId> SupportedPlatforms => _supportedPlatforms;

    /// <inheritdoc />
    public bool IsSupported(PlatformId platform) => _parsers.ContainsKey(platform);

    /// <inheritdoc />
    public IPlatformParser GetParser(PlatformId platform)
    {
        if (_parsers.TryGetValue(platform, out IPlatformParser? parser))
        {
            return parser;
        }

        throw new ResolveException(
            ResolveFailure.Unsupported,
            platform,
            "get-parser",
            ResolveMessages.Unsupported);
    }
}
