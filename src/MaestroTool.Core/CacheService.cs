using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MaestroTool.Core;

/// <summary>
/// SQLite-backed cache with per-key TTL expiration and cross-process sharing.
/// Database location: ~/.mstro/cache.db
/// </summary>
public class CacheService
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private const int MaxCacheEntries = 10000;
    private int _operationCount = 0;
    private readonly object _cleanupLock = new();

    public CacheService() : this(GetDefaultDbPath()) { }

    internal CacheService(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";
        InitializeDatabase();
    }

    private static string GetDefaultDbPath()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cacheDir = Path.Combine(homeDir, ".mstro");
        Directory.CreateDirectory(cacheDir);
        return Path.Combine(cacheDir, "cache.db");
    }

    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        
        // Enable WAL mode for concurrent reads across processes
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }
        
        // Set busy timeout for write contention
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
        }
        
        // Create tables
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS cache (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    expiry TEXT NOT NULL
                );
                
                CREATE TABLE IF NOT EXISTS actions (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    expiry TEXT NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }
    }

    public async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory, TimeSpan ttl)
    {
        // Try to get from cache
        var cached = await GetFromCacheAsync<T>(key);
        if (cached != null)
        {
            return cached;
        }

        // Cache miss - call factory
        var value = await factory();

        // Check capacity before insert
        using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync();
            
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM cache";
            var count = (long)(await countCmd.ExecuteScalarAsync() ?? 0L);
            
            if (count >= MaxCacheEntries)
            {
                Console.Error.WriteLine($"[maestro-mcp] Cache capacity ({MaxCacheEntries}) reached, clearing data cache");
                using var clearCmd = conn.CreateCommand();
                clearCmd.CommandText = "DELETE FROM cache";
                await clearCmd.ExecuteNonQueryAsync();
            }
        }

        // Store in cache
        await SetInCacheAsync(key, value, ttl);
        
        // Periodic cleanup
        MaybeCleanupExpired();
        
        return value;
    }

    private async Task<T?> GetFromCacheAsync<T>(string key)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value, expiry FROM cache WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var valueJson = reader.GetString(0);
                var expiryStr = reader.GetString(1);
                var expiry = DateTimeOffset.Parse(expiryStr);
                
                if (DateTimeOffset.UtcNow < expiry)
                {
                    return JsonSerializer.Deserialize<T>(valueJson);
                }
            }
            
            return default;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] Cache read error for key '{key}': {ex.Message}");
            return default;
        }
    }

    private async Task SetInCacheAsync<T>(string key, T value, TimeSpan ttl)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO cache (key, value, expiry) VALUES (@key, @value, @expiry)";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", JsonSerializer.Serialize(value));
            cmd.Parameters.AddWithValue("@expiry", DateTimeOffset.UtcNow.Add(ttl).ToString("O"));
            
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] Cache write error for key '{key}': {ex.Message}");
        }
    }

    public void Invalidate(string key)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM cache WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] Cache invalidate error for key '{key}': {ex.Message}");
        }
    }

    public void InvalidatePrefix(string prefix)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM cache WHERE key LIKE @prefix";
            cmd.Parameters.AddWithValue("@prefix", prefix + "%");
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] Cache invalidate prefix error for prefix '{prefix}': {ex.Message}");
        }
    }

    public void Clear()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM cache";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] Cache clear error: {ex.Message}");
        }
    }

    /// <summary>
    /// Clear action dedup records. Not exposed via MCP tools.
    /// </summary>
    public void ClearActions()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM actions";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] Actions clear error: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if an action with this key was recently executed (within cooldown period).
    /// Returns the timestamp if recently executed, null otherwise.
    /// Uses a separate store from the data cache so Clear() doesn't reset cooldowns.
    /// </summary>
    public DateTimeOffset? GetRecentAction(string key)
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value, expiry FROM actions WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var valueJson = reader.GetString(0);
                var expiryStr = reader.GetString(1);
                var expiry = DateTimeOffset.Parse(expiryStr);
                
                if (DateTimeOffset.UtcNow < expiry)
                {
                    return JsonSerializer.Deserialize<DateTimeOffset>(valueJson);
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] Action read error for key '{key}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Record that an action was executed. Future checks within the TTL will see this.
    /// Uses a separate store from the data cache so Clear() doesn't reset cooldowns.
    /// </summary>
    public void RecordAction(string key, TimeSpan cooldown)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO actions (key, value, expiry) VALUES (@key, @value, @expiry)";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", JsonSerializer.Serialize(now));
            cmd.Parameters.AddWithValue("@expiry", now.Add(cooldown).ToString("O"));
            
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[maestro-mcp] Action record error for key '{key}': {ex.Message}");
        }
    }

    private void MaybeCleanupExpired()
    {
        var shouldCleanup = false;
        lock (_cleanupLock)
        {
            _operationCount++;
            if (_operationCount >= 100)
            {
                _operationCount = 0;
                shouldCleanup = true;
            }
        }

        if (shouldCleanup)
        {
            Task.Run(() =>
            {
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();
                    
                    var now = DateTimeOffset.UtcNow.ToString("O");
                    
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM cache WHERE expiry < @now";
                        cmd.Parameters.AddWithValue("@now", now);
                        cmd.ExecuteNonQuery();
                    }
                    
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM actions WHERE expiry < @now";
                        cmd.Parameters.AddWithValue("@now", now);
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[maestro-mcp] Cleanup error: {ex.Message}");
                }
            });
        }
    }
}
