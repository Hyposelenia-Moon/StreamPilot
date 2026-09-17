namespace StreamPilot.Core.Caching;

using StreamPilot.Core.Models;

/// <summary>
/// 解析结果缓存：避免用户连续点击导致重复请求平台。
/// </summary>
/// <remarks>
/// 容量上限与过期时间集中定义（禁止魔法数字）；超出容量时淘汰最旧的条目
/// （CLAUDE.md 性能规则：内存中缓存的流地址数量必须有上限）。
/// 线程安全：所有访问都在同一把锁内完成。
/// </remarks>
public sealed class StreamCache
{
    /// <summary>缓存容量上限。</summary>
    public const int Capacity = 64;

    /// <summary>默认过期时间（秒）。</summary>
    public const int DefaultTtlSeconds = 60;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    /// <summary>初始化缓存。</summary>
    /// <param name="ttlSeconds">过期秒数；为 <see langword="null"/> 时使用默认值。</param>
    public StreamCache(int? ttlSeconds = null)
    {
        _ttl = TimeSpan.FromSeconds(Math.Max(1, ttlSeconds ?? DefaultTtlSeconds));
    }

    /// <summary>当前条目数。</summary>
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

    /// <summary>尝试读取缓存。</summary>
    /// <param name="platform">平台标识。</param>
    /// <param name="roomId">房间号。</param>
    /// <param name="now">当前时间。</param>
    /// <param name="room">命中的房间信息。</param>
    /// <returns>命中且未过期返回 <see langword="true"/>。</returns>
    public bool TryGet(PlatformId platform, string roomId, DateTimeOffset now, out ResolvedRoom? room)
    {
        string key = BuildKey(platform, roomId);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                room = null;
                return false;
            }

            if (now - entry.StoredAt > _ttl)
            {
                _entries.Remove(key);
                room = null;
                return false;
            }

            room = entry.Room;
            return true;
        }
    }

    /// <summary>写入缓存，必要时淘汰最旧条目。</summary>
    /// <param name="room">房间信息。</param>
    /// <param name="now">当前时间。</param>
    public void Set(ResolvedRoom room, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(room);
        string key = BuildKey(room.Platform, room.RoomId);

        lock (_gate)
        {
            _entries[key] = new Entry(room, now);
            while (_entries.Count > Capacity)
            {
                string oldestKey = string.Empty;
                DateTimeOffset oldest = DateTimeOffset.MaxValue;
                foreach (KeyValuePair<string, Entry> pair in _entries)
                {
                    if (pair.Value.StoredAt < oldest)
                    {
                        oldest = pair.Value.StoredAt;
                        oldestKey = pair.Key;
                    }
                }

                if (oldestKey.Length == 0)
                {
                    break;
                }

                _entries.Remove(oldestKey);
            }
        }
    }

    /// <summary>清空缓存。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private static string BuildKey(PlatformId platform, string roomId) => $"{(int)platform}:{roomId}";

    private sealed record Entry(ResolvedRoom Room, DateTimeOffset StoredAt);
}
