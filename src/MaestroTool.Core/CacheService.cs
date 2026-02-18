using System.Collections.Concurrent;

namespace MaestroTool.Core;

/// <summary>
/// Simple in-memory cache with per-key TTL expiration.
/// </summary>
public class CacheService
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _actions = new();
    private const int MaxCacheEntries = 10000;

    public async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory, TimeSpan ttl)
    {
        if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired)
        {
            return (T)entry.Value!;
        }

        var value = await factory();

        if (_cache.Count >= MaxCacheEntries)
        {
            Console.Error.WriteLine($"[maestro-mcp] Cache capacity ({MaxCacheEntries}) reached, clearing data cache");
            _cache.Clear();
        }

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
    /// Clear action dedup records. Not exposed via MCP tools.
    /// </summary>
    public void ClearActions() => _actions.Clear();

    /// <summary>
    /// Check if an action with this key was recently executed (within cooldown period).
    /// Returns the timestamp if recently executed, null otherwise.
    /// Uses a separate store from the data cache so Clear() doesn't reset cooldowns.
    /// </summary>
    public DateTimeOffset? GetRecentAction(string key)
    {
        if (_actions.TryGetValue(key, out var entry) && !entry.IsExpired)
            return (DateTimeOffset)entry.Value!;
        return null;
    }

    /// <summary>
    /// Record that an action was executed. Future checks within the TTL will see this.
    /// Uses a separate store from the data cache so Clear() doesn't reset cooldowns.
    /// </summary>
    public void RecordAction(string key, TimeSpan cooldown)
    {
        var now = DateTimeOffset.UtcNow;
        _actions[key] = new CacheEntry(now, now.Add(cooldown));
    }

    private record CacheEntry(object? Value, DateTimeOffset Expiry)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= Expiry;
    }
}
