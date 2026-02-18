using System.Collections.Concurrent;

namespace MaestroTool.Core;

/// <summary>
/// Simple in-memory cache with per-key TTL expiration.
/// </summary>
public class CacheService
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory, TimeSpan ttl)
    {
        if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired)
        {
            return (T)entry.Value!;
        }

        var value = await factory();
        _cache[key] = new CacheEntry(value, DateTimeOffset.UtcNow.Add(ttl));
        return value;
    }

    public void Invalidate(string key) => _cache.TryRemove(key, out _);

    public void InvalidatePrefix(string prefix)
    {
        foreach (var key in _cache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _cache.TryRemove(key, out _);
        }
    }

    public void Clear() => _cache.Clear();

    /// <summary>
    /// Check if an action with this key was recently executed (within cooldown period).
    /// Returns the timestamp if recently executed, null otherwise.
    /// </summary>
    public DateTimeOffset? GetRecentAction(string key)
    {
        if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired)
            return (DateTimeOffset)entry.Value!;
        return null;
    }

    /// <summary>
    /// Record that an action was executed. Future checks within the TTL will see this.
    /// </summary>
    public void RecordAction(string key, TimeSpan cooldown)
    {
        var now = DateTimeOffset.UtcNow;
        _cache[key] = new CacheEntry(now, now.Add(cooldown));
    }

    private record CacheEntry(object? Value, DateTimeOffset Expiry)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= Expiry;
    }
}
