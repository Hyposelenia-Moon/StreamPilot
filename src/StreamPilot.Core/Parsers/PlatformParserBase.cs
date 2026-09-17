namespace StreamPilot.Core.Parsers;

using System.Text.RegularExpressions;
using StreamPilot.Core.Errors;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;
using StreamPilot.Core.Services;

/// <summary>
/// 平台解析器基类：统一输入校验与平台错误包装。
/// </summary>
/// <remarks>
/// 子类只需实现 <see cref="OnParseAsync"/>，并调用 <see cref="Fail"/> 抛出带分类的失败。
/// 输入校验与"平台原始错误 → 失败分类"的映射集中在基类，保证六个平台行为一致。
/// </remarks>
public abstract class PlatformParserBase : IPlatformParser
{
    /// <summary>允许的房间号字符（数字或字母数字短号）。</summary>
    private static readonly Regex RoomIdPattern = new(
        "^[A-Za-z0-9_-]{1,64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>初始化基类。</summary>
    /// <param name="logger">结构化日志。</param>
    protected PlatformParserBase(IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Logger = logger;
    }

    /// <inheritdoc />
    public abstract PlatformId Platform { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <inheritdoc />
    public abstract string RoomUrlPrefix { get; }

    /// <summary>结构化日志。</summary>
    protected IStructuredLogger Logger { get; }

    /// <inheritdoc />
    public async Task<ResolvedRoom> ParseAsync(RoomQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);

        try
        {
            ResolvedRoom room = await OnParseAsync(query, cancellationToken).ConfigureAwait(false);
            if (room.Candidates.Count == 0)
            {
                throw Fail(
                    ResolveFailure.NotLive,
                    "resolve",
                    $"{DisplayName} 未返回任何可用直播流，直播间可能未开播。");
            }

            return room;
        }
        catch (ResolveException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Logger.LogError(LogLevel.Error, $"Parsers.{Platform}", "平台解析出现未预期异常。", exception, new Dictionary<string, object?>
            {
                ["platform"] = Platform.ToString(),
            });

            throw Fail(ResolveFailure.ParseError, "resolve", $"{DisplayName} 解析失败：{exception.Message}", exception);
        }
    }

    /// <summary>
    /// 平台具体解析实现。失败时必须抛 <see cref="ResolveException"/>（建议通过 <see cref="Fail"/> 构造）。
    /// </summary>
    /// <param name="query">已校验的房间查询条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>房间信息与候选流。</returns>
    protected abstract Task<ResolvedRoom> OnParseAsync(RoomQuery query, CancellationToken cancellationToken);

    /// <summary>构造带分类的解析失败异常。</summary>
    /// <param name="failure">失败分类。</param>
    /// <param name="operation">操作名。</param>
    /// <param name="detail">失败描述。</param>
    /// <param name="innerException">原始异常，可为 <see langword="null"/>。</param>
    /// <returns>异常实例。</returns>
    protected ResolveException Fail(
        ResolveFailure failure,
        string operation,
        string detail,
        Exception? innerException = null)
    {
        Logger.Warn($"Parsers.{Platform}", "解析失败。", new Dictionary<string, object?>
        {
            ["failure"] = failure.ToString(),
            ["operation"] = operation,
            ["detail"] = detail,
        });

        return new ResolveException(failure, Platform, operation, detail, innerException);
    }

    /// <summary>
    /// 校验房间查询条件：房间号必须是字母数字短号，链接必须是目标平台域名。
    /// </summary>
    /// <param name="query">查询条件。</param>
    /// <exception cref="ResolveException">输入非法时抛出。</exception>
    protected virtual void ValidateQuery(RoomQuery query)
    {
        if (query.Platform != Platform)
        {
            throw new ResolveException(
                ResolveFailure.InvalidInput,
                Platform,
                "validate",
                $"解析器 {DisplayName} 无法处理平台 {query.Platform}。");
        }

        bool hasRoomId = !string.IsNullOrWhiteSpace(query.RoomId);
        bool hasUrl = !string.IsNullOrWhiteSpace(query.RoomUrl);

        if (!hasRoomId && !hasUrl)
        {
            throw Fail(ResolveFailure.InvalidInput, "validate", "必须提供房间号或直播间链接。");
        }

        if (hasRoomId && !RoomIdPattern.IsMatch(query.RoomId!))
        {
            throw Fail(ResolveFailure.InvalidInput, "validate", "房间号格式不正确（只允许字母、数字、下划线与连字符）。");
        }

        if (hasUrl && !IsRoomUrlAllowed(query.RoomUrl!))
        {
            throw Fail(ResolveFailure.InvalidInput, "validate", $"直播间链接的域名不属于 {DisplayName}。");
        }
    }

    /// <summary>
    /// 判断直播间链接是否属于本平台（默认要求主机等于 <see cref="RoomUrlPrefix"/> 的主机）。
    /// </summary>
    /// <param name="roomUrl">直播间链接。</param>
    /// <returns>允许返回 <see langword="true"/>。</returns>
    protected virtual bool IsRoomUrlAllowed(string roomUrl)
    {
        if (!Uri.TryCreate(roomUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(RoomUrlPrefix, UriKind.Absolute, out Uri? baseUri))
        {
            return false;
        }

        return uri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + baseUri.Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从链接中提取房间号（取路径首个非空片段，去掉查询串与锚点）。
    /// </summary>
    /// <param name="roomUrl">直播间链接。</param>
    /// <returns>房间号；无法提取时返回 <see langword="null"/>。</returns>
    protected static string? TryExtractRoomIdFromUrl(string? roomUrl)
    {
        if (string.IsNullOrWhiteSpace(roomUrl) || !Uri.TryCreate(roomUrl, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            if (RoomIdPattern.IsMatch(segment))
            {
                return segment;
            }
        }

        return null;
    }
}
