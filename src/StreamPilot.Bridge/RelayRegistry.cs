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

    /// <summary>空闲淘汰阈值；默认 <see cref="BridgeConstants.RelayIdleMinutes"/> 分钟。</summary>
    private readonly TimeSpan _idleTimeout;

    /// <summary>子注册的试用期；默认 <see cref="BridgeConstants.ProvisionalRelaySeconds"/> 秒。</summary>
    private readonly TimeSpan _provisionalTimeout;

    /// <summary>初始化注册表。</summary>
    /// <param name="logger">结构化日志。</param>
    /// <param name="idleTimeout">空闲淘汰阈值；为 <see langword="null"/> 时使用默认值。</param>
    /// <param name="provisionalTimeout">子注册试用期；为 <see langword="null"/> 时使用默认值。</param>
    public RelayRegistry(
        IStructuredLogger logger,
        TimeSpan? idleTimeout = null,
        TimeSpan? provisionalTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(BridgeConstants.RelayIdleMinutes);
        _provisionalTimeout = provisionalTimeout ?? TimeSpan.FromSeconds(BridgeConstants.ProvisionalRelaySeconds);
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
    /// <param name="provisional">
    /// 是否为"试用期"注册（播放列表改写时批量登记的子地址）。试用期注册若在
    /// <see cref="BridgeConstants.ProvisionalRelaySeconds"/> 秒内没有被真正请求过就会被淘汰；
    /// 首个真实请求会把它转正。
    /// </param>
    /// <returns>中继令牌（用于拼出本地地址）。</returns>
    public string Register(RelayTarget target, bool provisional = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            EvictExpired();
            while (_entries.Count >= BridgeConstants.MaxRelayRegistrations)
            {
                EvictOldest();
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string token = CreateToken();
            _entries[token] = new Entry(target, now, provisional ? now + _provisionalTimeout : null);
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

            // 首个真实请求即"转正"：探测拉过但没人用的子地址靠试用期自然消失，
            // 真正被播放器请求过的地址从此按普通空闲规则续命。
            entry.LastAccessAt = DateTimeOffset.UtcNow;
            entry.ExpiresAt = null;
            target = entry.Target;
            return true;
        }
    }

    /// <summary>
    /// 续命指定令牌（不改变目标内容）。
    /// </summary>
    /// <param name="token">令牌。</param>
    /// <remarks>
    /// 直播 FLV 是长连接：建连时只请求一次 <c>/relay/{token}</c>，之后可能几小时没有任何新请求。
    /// 如果只在"收到 HTTP 请求"时刷新活跃时间，10 分钟后条目会被淘汰，而画面仍在正常播放，
    /// 直到第一次断流重连才突然 404。中继泵在转发期间周期性调用本方法，让"连接活着"等于"注册有效"。
    /// </remarks>
    public void Touch(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(token, out Entry? entry))
            {
                entry.LastAccessAt = DateTimeOffset.UtcNow;
            }
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

    /// <summary>按令牌取出中继目标且**不刷新**活跃时间。</summary>
    /// <param name="token">中继令牌。</param>
    /// <param name="target">取出的目标。</param>
    /// <returns>找到返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 回收链路（释放父中继时反查它是不是播放列表）用这个方法：回收不是访问，
    /// 用 <see cref="TryResolve"/> 会把一条马上要被释放的注册续命。
    /// </remarks>
    public bool TryPeek(string token, out RelayTarget? target)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(token, out Entry? entry))
            {
                target = null;
                return false;
            }

            target = entry.Target;
            return true;
        }
    }

    /// <summary>从本地回环地址（或裸令牌）里取出中继令牌。</summary>
    /// <param name="localUrl">本地地址（形如 <c>http://127.0.0.1:端口/relay/{token}</c>）或裸令牌。</param>
    /// <returns>令牌；输入为空或取不出令牌时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 未启动监听的离线路径里地址可能是相对的（<c>/relay/{token}</c>）甚至是裸令牌，
    /// 三种形态都按"路径最后一段"处理，调用方不必关心自己拿到的是哪一种。
    /// </remarks>
    public static string? ExtractToken(string localUrl)
    {
        if (string.IsNullOrWhiteSpace(localUrl))
        {
            return null;
        }

        string path = Uri.TryCreate(localUrl, UriKind.Absolute, out Uri? uri)
            ? uri.AbsolutePath
            : localUrl.Split('?')[0];

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        string token = segments[^1];
        return token.Length == 0 ? null : token;
    }

    /// <summary>从本地回环地址反解出令牌并释放。</summary>
    /// <param name="localUrl">本地地址。</param>
    public void ReleaseByLocalUrl(string localUrl)
    {
        if (ExtractToken(localUrl) is { } token)
        {
            Release(token);
        }
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
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset threshold = now - _idleTimeout;
        List<string> expired = [];
        foreach (KeyValuePair<string, Entry> pair in _entries)
        {
            bool provisionalExpired = pair.Value.ExpiresAt is { } expiresAt && expiresAt <= now;
            if (provisionalExpired || pair.Value.LastAccessAt < threshold)
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

    /// <summary>
    /// 淘汰一条注册：优先淘汰仍处于试用期、且从未被请求过的子注册。
    /// </summary>
    /// <remarks>
    /// 达到上限说明注册压力已经很大（探测 + 播放列表刷新），此时最该回收的是"登记了但没人用"的
    /// 子地址，而不是正在被播放的切片——后者会被播放器持续请求，活跃时间最新。
    /// </remarks>
    private void EvictOldest()
    {
        string? oldestToken = null;
        DateTimeOffset oldest = DateTimeOffset.MaxValue;
        bool oldestProvisional = false;
        foreach (KeyValuePair<string, Entry> pair in _entries)
        {
            bool provisional = pair.Value.ExpiresAt is not null;
            bool better = provisional
                ? !oldestProvisional || pair.Value.LastAccessAt < oldest
                : !oldestProvisional && pair.Value.LastAccessAt < oldest;
            if (better)
            {
                oldest = pair.Value.LastAccessAt;
                oldestToken = pair.Key;
                oldestProvisional = provisional;
            }
        }

        if (oldestToken is not null)
        {
            _entries.Remove(oldestToken);
        }
    }

    private sealed class Entry
    {
        public Entry(RelayTarget target, DateTimeOffset now, DateTimeOffset? expiresAt)
        {
            Target = target;
            LastAccessAt = now;
            ExpiresAt = expiresAt;
        }

        public RelayTarget Target { get; }

        public DateTimeOffset LastAccessAt { get; set; }

        /// <summary>试用期截止时间；<see langword="null"/> 表示已转正（按普通空闲规则）。</summary>
        public DateTimeOffset? ExpiresAt { get; set; }
    }
}
