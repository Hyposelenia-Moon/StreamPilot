namespace StreamPilot.App.Services;

using StreamPilot.Core.Caching;
using StreamPilot.Core.Errors;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Services;

/// <summary>
/// 房间解析服务：把平台解析器与缓存组合起来，并把异常转换为结果对象。
/// </summary>
/// <remarks>
/// UI 层只依赖 <see cref="IRoomResolver"/>（Core 中的契约），不依赖任何解析器实现类型
/// （满足 CLAUDE.md 的"UI 层不得直接调用解析层"）。
/// </remarks>
public sealed class RoomResolver : IRoomResolver
{
    private readonly IPlatformParserFactory _factory;
    private readonly StreamCache _cache;
    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "App.RoomResolver";

    /// <summary>初始化解析服务。</summary>
    /// <param name="factory">平台解析器工厂。</param>
    /// <param name="cache">解析结果缓存。</param>
    /// <param name="logger">结构化日志。</param>
    public RoomResolver(IPlatformParserFactory factory, StreamCache cache, IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ResolveOutcome> ResolveAsync(RoomQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!_factory.IsSupported(query.Platform))
        {
            return ResolveOutcome.FromFailure(ResolveFailure.Unsupported, ResolveMessages.Unsupported, "get-parser");
        }

        IPlatformParser parser = _factory.GetParser(query.Platform);
        string cacheKey = query.RoomId ?? query.RoomUrl ?? string.Empty;
        if (cacheKey.Length > 0 && _cache.TryGet(query.Platform, cacheKey, DateTimeOffset.UtcNow, out ResolvedRoom? cached) && cached is not null)
        {
            _logger.Debug(_moduleName, "命中解析缓存。", new Dictionary<string, object?>
            {
                ["platform"] = query.Platform.ToString(),
                ["roomId"] = cached.RoomId,
            });
            return ResolveOutcome.FromSuccess(cached);
        }

        try
        {
            ResolvedRoom room = await parser.ParseAsync(query, cancellationToken).ConfigureAwait(false);
            _cache.Set(room, DateTimeOffset.UtcNow);
            _logger.Info(_moduleName, "解析成功。", new Dictionary<string, object?>
            {
                ["platform"] = room.Platform.ToString(),
                ["roomId"] = room.RoomId,
                ["candidates"] = room.Candidates.Count,
                ["title"] = room.Title,
            });
            return ResolveOutcome.FromSuccess(room);
        }
        catch (ResolveException exception)
        {
            _logger.Warn(_moduleName, "解析失败。", exception.ToLogFields());
            return ResolveOutcome.FromFailure(exception.Failure, exception.Message, exception.Operation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ResolveOutcome.FromFailure(ResolveFailure.NetworkError, "解析已取消。", "resolve");
        }
    }

    /// <inheritdoc />
    public void InvalidateCache() => _cache.Clear();
}
