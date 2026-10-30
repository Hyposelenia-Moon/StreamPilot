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
    public async Task<ResolveOutcome> ResolveAsync(RoomQuery query, CancellationToken cancellationToken, bool bypassCache = false)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!_factory.IsSupported(query.Platform))
        {
            return ResolveOutcome.FromFailure(ResolveFailure.Unsupported, ResolveMessages.Unsupported, "get-parser");
        }

        IPlatformParser parser = _factory.GetParser(query.Platform);
        string cacheKey = query.RoomId ?? query.RoomUrl ?? string.Empty;
        if (!bypassCache
            && cacheKey.Length > 0
            && _cache.TryGet(query.Platform, cacheKey, DateTimeOffset.UtcNow, out ResolvedRoom? cached)
            && cached is not null)
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
    public async Task<IReadOnlyList<RoomStatus>> ResolveRoomStatusesAsync(
        IReadOnlyList<RoomQuery> queries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queries);
        RoomStatus[] results = new RoomStatus[queries.Count];

        // 按平台分组，但保留每项在输入里的下标，返回时按下标放回，保证结果顺序与输入一致。
        Dictionary<PlatformId, List<int>> groupedIndexes = [];
        for (int index = 0; index < queries.Count; index += 1)
        {
            PlatformId platform = queries[index].Platform;
            if (!groupedIndexes.TryGetValue(platform, out List<int>? indexes))
            {
                indexes = [];
                groupedIndexes[platform] = indexes;
            }

            indexes.Add(index);
        }

        List<Task> groupTasks = [];
        foreach ((PlatformId platform, List<int> indexes) in groupedIndexes)
        {
            groupTasks.Add(ResolveStatusGroupAsync(platform, indexes, queries, results, cancellationToken));
        }

        await Task.WhenAll(groupTasks).ConfigureAwait(false);
        return results;
    }

    /// <summary>处理单个平台的批量状态查询（B站批量 + 其它逐房间），结果按下标写回。</summary>
    /// <param name="platform">平台标识。</param>
    /// <param name="indexes">该平台在输入列表里的下标。</param>
    /// <param name="queries">完整查询列表。</param>
    /// <param name="results">结果数组（按下标写入）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ResolveStatusGroupAsync(
        PlatformId platform,
        List<int> indexes,
        IReadOnlyList<RoomQuery> queries,
        RoomStatus[] results,
        CancellationToken cancellationToken)
    {
        if (!_factory.IsSupported(platform))
        {
            foreach (int index in indexes)
            {
                results[index] = RoomStatus.FromError(QueryKey(queries[index]), ResolveMessages.Unsupported);
            }

            return;
        }

        IPlatformParser parser = _factory.GetParser(platform);

        if (parser is not IBatchStatusParser batch)
        {
            // 无批量能力：逐房间并发，单个房间失败只记进它自己的结果。
            await Task.WhenAll(indexes.Select(async index =>
            {
                results[index] = await ResolveSingleStatusAsync(queries[index], cancellationToken).ConfigureAwait(false);
            })).ConfigureAwait(false);
            return;
        }

        // 有批量能力：能提取出数字房间号的走批量，其余（链接提取失败）回退逐房间。
        List<(int Index, string NumericId)> batchable = [];
        foreach (int index in indexes)
        {
            string? numericId = TryExtractNumericRoomId(queries[index]);
            if (numericId is not null)
            {
                batchable.Add((index, numericId));
            }
            else
            {
                results[index] = await ResolveSingleStatusAsync(queries[index], cancellationToken).ConfigureAwait(false);
            }
        }

        if (batchable.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> roomIds = batchable.Select(entry => entry.NumericId).ToList();
        string? cookie = queries[batchable[0].Index].Cookie;
        IReadOnlyList<RoomStatus> statuses = await batch
            .QueryRoomStatusesAsync(roomIds, cookie, cancellationToken)
            .ConfigureAwait(false);

        for (int i = 0; i < batchable.Count; i += 1)
        {
            results[batchable[i].Index] = statuses[i];
        }
    }

    /// <summary>把单房间完整解析的结果转为状态快照（逐房间降级通道）。</summary>
    /// <param name="query">房间查询条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>状态快照。</returns>
    private async Task<RoomStatus> ResolveSingleStatusAsync(RoomQuery query, CancellationToken cancellationToken)
    {
        ResolveOutcome outcome = await ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        string key = QueryKey(query);
        if (outcome.Success && outcome.Room is not null)
        {
            return new RoomStatus
            {
                RoomId = key,
                Status = LiveStatusKind.Live,
                Anchor = outcome.Room.Anchor,
                Title = outcome.Room.Title,
                Error = string.Empty,
            };
        }

        if (outcome.Failure is ResolveFailure.NotLive)
        {
            return new RoomStatus { RoomId = key, Status = LiveStatusKind.Offline, Error = string.Empty };
        }

        if (outcome.Failure is ResolveFailure.Replaying)
        {
            return new RoomStatus { RoomId = key, Status = LiveStatusKind.Replaying, Error = string.Empty };
        }

        string reason = outcome.Message.Length > 0 ? outcome.Message : ResolveMessages.DescribeFailure(outcome.Failure);
        return RoomStatus.FromError(key, reason);
    }

    /// <summary>取查询的稳定键（房间号优先，否则链接），用于把结果对齐回调用方。</summary>
    private static string QueryKey(RoomQuery query) =>
        query.RoomId ?? query.RoomUrl ?? string.Empty;

    /// <summary>从查询里提取纯数字房间号；提取不出（字母短号、异常链接）返回 <see langword="null"/>。</summary>
    private static string? TryExtractNumericRoomId(RoomQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.RoomId))
        {
            string id = query.RoomId.Trim();
            return IsAllDigits(id) ? id : null;
        }

        if (string.IsNullOrWhiteSpace(query.RoomUrl)
            || !Uri.TryCreate(query.RoomUrl, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? last = segments.Length > 0 ? segments[^1] : null;
        return last is not null && last.Length > 0 && IsAllDigits(last) ? last : null;
    }

    /// <summary>判断字符串是否全部由 ASCII 数字组成。</summary>
    private static bool IsAllDigits(string value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    /// <inheritdoc />
    public void InvalidateCache() => _cache.Clear();
}
