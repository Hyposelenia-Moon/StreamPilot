namespace StreamPilot.Bridge;

using System.Security.Cryptography;
using StreamPilot.Core.Configuration;
using StreamPilot.Core.Logging;
using StreamPilot.Core.Services;

/// <summary>
/// 流中继注册表：把"需要 Referer / 有签名"的上游地址映射为本地回环地址。
/// </summary>
/// <remarks>
/// 签名 URL 只保存在内存中（<see cref="BridgeConstants.RelayIdleMinutes"/> 分钟未被访问即淘汰，
/// 上限 <see cref="BridgeConstants.MaxRelayRegistrations"/> 条），绝不落盘、绝不写日志。
/// </remarks>
public sealed class RelayRegistry
{
    private readonly IStructuredLogger _logger;
    private readonly string _moduleName = "Bridge.RelayRegistry";
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>初始化注册表。</summary>
    /// <param name="logger">结构化日志。</param>
    public RelayRegistry(IStructuredLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>当前注册数量。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>注册一个中继目标。</summary>
    /// <param name="target">中继目标。</param>
    /// <returns>中继令牌（用于拼出本地地址）。</returns>
    public string Register(RelayTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            EvictExpired();
            while (_entries.Count >= BridgeConstants.MaxRelayRegistrations)
            {
                EvictOldest();
            }

            string token = CreateToken();
            _entries[token] = new Entry(target, DateTimeOffset.UtcNow);
            _logger.Debug(_moduleName, "已注册中继。", new Dictionary<string, object?>
            {
                ["count"] = _entries.Count,
                ["fingerprint"] = SensitiveData.Fingerprint(target.UpstreamUrl),
            });
            return token;
        }
    }

    /// <summary>按令牌取出中继目标并刷新其活跃时间。</summary>
    /// <param name="token">中继令牌。</param>
    /// <param name="target">取出的目标。</param>
    /// <returns>找到返回 <see langword="true"/>。</returns>
    public bool TryResolve(string token, out RelayTarget? target)
    {
        lock (_gate)
        {
            EvictExpired();
            if (!_entries.TryGetValue(token, out Entry? entry))
            {
                target = null;
                return false;
            }

            entry.LastAccessAt = DateTimeOffset.UtcNow;
            target = entry.Target;
            return true;
        }
    }

    /// <summary>释放指定令牌。</summary>
    /// <param name="token">中继令牌。</param>
    /// <returns>释放成功返回 <see langword="true"/>。</returns>
    public bool Release(string token)
    {
        lock (_gate)
        {
            return _entries.Remove(token);
        }
    }

    /// <summary>从本地回环地址反解出令牌并释放。</summary>
    /// <param name="localUrl">本地地址。</param>
    public void ReleaseByLocalUrl(string localUrl)
    {
        if (string.IsNullOrWhiteSpace(localUrl))
        {
            return;
        }

        if (!Uri.TryCreate(localUrl, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return;
        }

        Release(segments[^1]);
    }

    /// <summary>清空所有注册（用于停止服务）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private static string CreateToken()
    {
        byte[] buffer = new byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private void EvictExpired()
    {
        DateTimeOffset threshold = DateTimeOffset.UtcNow.AddMinutes(-BridgeConstants.RelayIdleMinutes);
        List<string> expired = [];
        foreach (KeyValuePair<string, Entry> pair in _entries)
        {
            if (pair.Value.LastAccessAt < threshold)
            {
                expired.Add(pair.Key);
            }
        }

        foreach (string token in expired)
        {
            _entries.Remove(token);
        }

        if (expired.Count > 0)
        {
            _logger.Debug(_moduleName, "已淘汰过期中继注册。", new Dictionary<string, object?>
            {
                ["expired"] = expired.Count,
                ["remaining"] = _entries.Count,
            });
        }
    }

    private void EvictOldest()
    {
        string? oldestToken = null;
        DateTimeOffset oldest = DateTimeOffset.MaxValue;
        foreach (KeyValuePair<string, Entry> pair in _entries)
        {
            if (pair.Value.LastAccessAt < oldest)
            {
                oldest = pair.Value.LastAccessAt;
                oldestToken = pair.Key;
            }
        }

        if (oldestToken is not null)
        {
            _entries.Remove(oldestToken);
        }
    }

    private sealed class Entry
    {
        public Entry(RelayTarget target, DateTimeOffset now)
        {
            Target = target;
            LastAccessAt = now;
        }

        public RelayTarget Target { get; }

        public DateTimeOffset LastAccessAt { get; set; }
    }
}
