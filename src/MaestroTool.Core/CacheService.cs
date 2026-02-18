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

    private record CacheEntry(object? Value, DateTimeOffset Expiry)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= Expiry;
    }
}
