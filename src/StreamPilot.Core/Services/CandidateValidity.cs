namespace StreamPilot.Core.Services;

using StreamPilot.Core.Errors;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Models;

/// <summary>
/// 候选流有效期与地址合法性校验的默认实现。
/// </summary>
public sealed class CandidateValidity : ICandidateValidity
{
    /// <summary>允许的 URL 协议。</summary>
    private static readonly string[] AllowedSchemes = ["http", "https", "rtmp", "rtmps"];

    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Core.CandidateValidity";

    /// <summary>初始化校验器。</summary>
    /// <param name="logger">结构化日志。</param>
    public CandidateValidity(IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsExpired(StreamCandidate candidate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.ExpiresAt is { } expiresAt && expiresAt <= now;
    }

    /// <inheritdoc />
    public void EnsureUsable(StreamCandidate candidate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out Uri? uri))
        {
            throw new ResolveException(
                ResolveFailure.ParseError,
                PlatformId.Unknown,
                "validate-candidate",
                "候选流地址不是合法 URL。");
        }

        bool schemeAllowed = false;
        foreach (string scheme in AllowedSchemes)
        {
            if (string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
            {
                schemeAllowed = true;
                break;
            }
        }

        if (!schemeAllowed)
        {
            throw new ResolveException(
                ResolveFailure.ParseError,
                PlatformId.Unknown,
                "validate-candidate",
                $"候选流地址协议不受支持：{uri.Scheme}。");
        }

        if (IsExpired(candidate, now))
        {
            _logger.Warn(_moduleName, "候选流地址已过期。", new Dictionary<string, object?>
            {
                ["sourceIndex"] = candidate.SourceIndex,
                ["host"] = candidate.CdnHost,
                ["fingerprint"] = candidate.UrlFingerprint,
                ["expiresAt"] = candidate.ExpiresAt?.ToString("O"),
            });

            throw new ResolveException(
                ResolveFailure.NetworkError,
                PlatformId.Unknown,
                "validate-candidate",
                "候选流地址已过期，请重新解析。");
        }
    }
}
