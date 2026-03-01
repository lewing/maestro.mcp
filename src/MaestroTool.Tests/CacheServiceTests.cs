using MaestroTool.Core;
using Xunit;

namespace MaestroTool.Tests;

public class CacheServiceTests : IDisposable
{
    private readonly string _dbPath;

    public CacheServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mstro-test-{Guid.NewGuid()}.db");
    }

    public void Dispose()
    {
        // Clean up temp database files
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*"))
        {
            try { File.Delete(f); } catch { }
        }
    }

    private CacheService CreateCache() => new(_dbPath);

    [Fact]
    public async Task GetOrAddAsync_ReturnsCachedValue()
    {
        var cache = CreateCache();
        var callCount = 0;

        var result1 = await cache.GetOrAddAsync("key1", async () =>
        {
            callCount++;
            return "value1";
        }, TimeSpan.FromMinutes(5));

        var result2 = await cache.GetOrAddAsync("key1", async () =>
        {
            callCount++;
            return "value2";
        }, TimeSpan.FromMinutes(5));

        Assert.Equal("value1", result1);
        Assert.Equal("value1", result2); // Cached, not "value2"
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetOrAddAsync_RefreshesAfterExpiry()
    {
        var cache = CreateCache();
        var callCount = 0;

        var result1 = await cache.GetOrAddAsync("key1", async () =>
        {
            callCount++;
            return $"value{callCount}";
        }, TimeSpan.FromMilliseconds(50));

        await Task.Delay(100); // Wait for TTL to expire

        var result2 = await cache.GetOrAddAsync("key1", async () =>
        {
            callCount++;
            return $"value{callCount}";
        }, TimeSpan.FromMinutes(5));

        Assert.Equal("value1", result1);
        Assert.Equal("value2", result2);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task Invalidate_RemovesKey()
    {
        var cache = CreateCache();
        await cache.GetOrAddAsync("key1", () => Task.FromResult("value1"), TimeSpan.FromMinutes(5));

        cache.Invalidate("key1");

        var callCount = 0;
        var result = await cache.GetOrAddAsync("key1", () =>
        {
            callCount++;
            return Task.FromResult("value2");
        }, TimeSpan.FromMinutes(5));

        Assert.Equal("value2", result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task InvalidatePrefix_RemovesMatchingKeys()
    {
        var cache = CreateCache();
        await cache.GetOrAddAsync("subs:repo1", () => Task.FromResult("v1"), TimeSpan.FromMinutes(5));
        await cache.GetOrAddAsync("subs:repo2", () => Task.FromResult("v2"), TimeSpan.FromMinutes(5));
        await cache.GetOrAddAsync("build:123", () => Task.FromResult("v3"), TimeSpan.FromMinutes(5));

        cache.InvalidatePrefix("subs:");

        // subs keys should be gone, build key should remain
        var count = 0;
        await cache.GetOrAddAsync("subs:repo1", () => { count++; return Task.FromResult("new"); }, TimeSpan.FromMinutes(5));
        await cache.GetOrAddAsync("build:123", () => { count++; return Task.FromResult("new"); }, TimeSpan.FromMinutes(5));

        Assert.Equal(1, count); // Only subs:repo1 was re-fetched, build:123 was still cached
    }

    [Fact]
    public async Task Clear_RemovesAllKeys()
    {
        var cache = CreateCache();
        await cache.GetOrAddAsync("key1", () => Task.FromResult("v1"), TimeSpan.FromMinutes(5));
        await cache.GetOrAddAsync("key2", () => Task.FromResult("v2"), TimeSpan.FromMinutes(5));

        cache.Clear();

        var count = 0;
        await cache.GetOrAddAsync("key1", () => { count++; return Task.FromResult("new1"); }, TimeSpan.FromMinutes(5));
        await cache.GetOrAddAsync("key2", () => { count++; return Task.FromResult("new2"); }, TimeSpan.FromMinutes(5));

        Assert.Equal(2, count);
    }

    // ================================================================
    // Action dedup (GetRecentAction / RecordAction)
    // ================================================================

    [Fact]
    public void GetRecentAction_ReturnsNull_WhenNoActionRecorded()
    {
        var cache = CreateCache();

        var result = cache.GetRecentAction("action:trigger:sub1");

        Assert.Null(result);
    }

    [Fact]
    public void RecordAction_ThenGetRecentAction_ReturnsTimestamp()
    {
        var cache = CreateCache();
        var before = DateTimeOffset.UtcNow;

        cache.RecordAction("action:trigger:sub1", TimeSpan.FromMinutes(5));
        var result = cache.GetRecentAction("action:trigger:sub1");

        Assert.NotNull(result);
        Assert.True(result!.Value >= before);
        Assert.True(result.Value <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task GetRecentAction_ReturnsNull_AfterCooldownExpires()
    {
        var cache = CreateCache();

        cache.RecordAction("action:trigger:sub1", TimeSpan.FromMilliseconds(50));

        // Immediately should be non-null
        Assert.NotNull(cache.GetRecentAction("action:trigger:sub1"));

        await Task.Delay(100); // Wait for cooldown to expire

        Assert.Null(cache.GetRecentAction("action:trigger:sub1"));
    }

    [Fact]
    public async Task Clear_DoesNotResetActionRecords()
    {
        var cache = CreateCache();

        // Populate both regular cache and action records
        await cache.GetOrAddAsync("key1", () => Task.FromResult("v1"), TimeSpan.FromMinutes(5));
        cache.RecordAction("action:trigger:sub1", TimeSpan.FromMinutes(5));

        cache.Clear();

        // Data cache should be cleared, action records should survive
        var count = 0;
        await cache.GetOrAddAsync("key1", () => { count++; return Task.FromResult("new"); }, TimeSpan.FromMinutes(5));
        Assert.Equal(1, count); // Cache entry was cleared
        Assert.NotNull(cache.GetRecentAction("action:trigger:sub1")); // Action record survives Clear()
    }

    [Fact]
    public void ClearActions_ResetsActionRecords()
    {
        var cache = CreateCache();

        cache.RecordAction("action:trigger:sub1", TimeSpan.FromMinutes(5));
        Assert.NotNull(cache.GetRecentAction("action:trigger:sub1"));

        cache.ClearActions();

        Assert.Null(cache.GetRecentAction("action:trigger:sub1"));
    }

    // ================================================================
    // Security: cache size bounds (Fix 5)
    // ================================================================

    [Fact]
    public async Task Cache_RespectsMaxEntries()
    {
        var cache = CreateCache();

        // Fill cache beyond MaxCacheEntries (10,000)
        for (int i = 0; i < 10_001; i++)
        {
            await cache.GetOrAddAsync($"key:{i}", () => Task.FromResult($"value:{i}"), TimeSpan.FromMinutes(30));
        }

        // Early entries should have been evicted when capacity was reached
        var factoryCalled = false;
        await cache.GetOrAddAsync("key:0", () =>
        {
            factoryCalled = true;
            return Task.FromResult("new-value");
        }, TimeSpan.FromMinutes(30));

        Assert.True(factoryCalled, "Expected early entries to be evicted when cache exceeds max size");
    }

    // ================================================================
    // Security: concurrent access safety
    // ================================================================

    [Fact]
    public async Task ConcurrentCacheAccess_DoesNotCorrupt()
    {
        var cache = CreateCache();
        var callCount = 0;

        // Hammer the same key from 100 concurrent tasks
        var tasks = Enumerable.Range(0, 100).Select(_ =>
            cache.GetOrAddAsync("concurrent-key", async () =>
            {
                Interlocked.Increment(ref callCount);
                await Task.Yield();
                return "shared-value";
            }, TimeSpan.FromMinutes(5))
        ).ToArray();

        var results = await Task.WhenAll(tasks);

        // All results should be the same value — no corruption
        Assert.All(results, r => Assert.Equal("shared-value", r));
    }

    // ================================================================
    // Security: SQLite P1 — file permissions and corruption recovery
    // ================================================================

    [Fact]
    public void Test_DirectoryPermissions_SetOnUnix()
    {
        // Use a nested directory so CacheService actually creates it and sets permissions
        // (pre-existing dirs like /tmp are skipped)
        var nestedDir = Path.Combine(Path.GetTempPath(), $"mstro-perm-{Guid.NewGuid()}");
        var permTestPath = Path.Combine(nestedDir, "cache.db");

        try
        {
            var cache = new CacheService(permTestPath);

            // Verify the cache was created successfully
            Assert.True(Directory.Exists(nestedDir), "Cache directory should be created");

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var mode = File.GetUnixFileMode(nestedDir);
                // Should have owner read, write, execute only (no group or other access)
                Assert.True(mode.HasFlag(UnixFileMode.UserRead), "Owner should have read permission");
                Assert.True(mode.HasFlag(UnixFileMode.UserWrite), "Owner should have write permission");
                Assert.True(mode.HasFlag(UnixFileMode.UserExecute), "Owner should have execute permission");
                Assert.False(mode.HasFlag(UnixFileMode.GroupRead), "Group should not have read permission");
                Assert.False(mode.HasFlag(UnixFileMode.OtherRead), "Others should not have read permission");
            }
        }
        finally
        {
            try { Directory.Delete(nestedDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Test_DirectoryCreation_ForCustomPath()
    {
        // Create a custom path in a subdirectory that doesn't exist yet
        var customPath = Path.Combine(Path.GetTempPath(), $"mstro-nested-{Guid.NewGuid()}", "subdir", "cache.db");
        
        try
        {
            var cache = new CacheService(customPath);
            
            // Verify the directory structure was created
            var dir = Path.GetDirectoryName(customPath);
            Assert.True(Directory.Exists(dir), "Parent directory should be created");
            
            // Verify the cache works
            await cache.GetOrAddAsync("test-key", () => Task.FromResult("test-value"), TimeSpan.FromMinutes(5));
            var result = await cache.GetOrAddAsync("test-key", () => Task.FromResult("other-value"), TimeSpan.FromMinutes(5));
            Assert.Equal("test-value", result);
        }
        finally
        {
            // Clean up nested directory structure
            var rootDir = Path.Combine(Path.GetTempPath(), Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(customPath))!));
            if (Directory.Exists(rootDir))
            {
                try { Directory.Delete(rootDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Test_CorruptedDatabase_RecreatesAutomatically()
    {
        // Write garbage bytes to simulate a corrupted database
        await File.WriteAllBytesAsync(_dbPath, new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA });
        
        // Creating a CacheService should detect corruption and recreate the DB
        var cache = CreateCache();
        
        // Verify normal operations work after recovery
        await cache.GetOrAddAsync("key1", () => Task.FromResult("value1"), TimeSpan.FromMinutes(5));
        var result = await cache.GetOrAddAsync("key1", () => Task.FromResult("value2"), TimeSpan.FromMinutes(5));
        
        Assert.Equal("value1", result);
    }

    [Fact]
    public async Task Test_CorruptedDatabase_PreservesNormalOperation()
    {
        // Simulate corruption
        await File.WriteAllBytesAsync(_dbPath, new byte[] { 0x00, 0x01, 0x02, 0x03 });
        
        var cache = CreateCache();
        
        // Test all normal operations after recovery
        await cache.GetOrAddAsync("key1", () => Task.FromResult("value1"), TimeSpan.FromMinutes(5));
        await cache.GetOrAddAsync("key2", () => Task.FromResult("value2"), TimeSpan.FromMinutes(5));
        
        cache.Invalidate("key1");
        var count = 0;
        await cache.GetOrAddAsync("key1", () => { count++; return Task.FromResult("new1"); }, TimeSpan.FromMinutes(5));
        Assert.Equal(1, count); // key1 was invalidated, should re-fetch
        
        cache.InvalidatePrefix("key");
        count = 0;
        await cache.GetOrAddAsync("key2", () => { count++; return Task.FromResult("new2"); }, TimeSpan.FromMinutes(5));
        Assert.Equal(1, count); // key2 was invalidated by prefix, should re-fetch
        
        cache.Clear();
        count = 0;
        await cache.GetOrAddAsync("key3", () => { count++; return Task.FromResult("value3"); }, TimeSpan.FromMinutes(5));
        Assert.Equal(1, count); // First time fetching key3
        
        // Test action dedup still works
        cache.RecordAction("action:test", TimeSpan.FromMinutes(5));
        Assert.NotNull(cache.GetRecentAction("action:test"));
    }

    [Fact]
    public async Task Test_EmptyDatabase_InitializesNormally()
    {
        // _dbPath doesn't exist yet (fresh test case)
        Assert.False(File.Exists(_dbPath), "Database file should not exist initially");
        
        var cache = CreateCache();
        
        // Verify normal operations work on fresh database
        await cache.GetOrAddAsync("key1", () => Task.FromResult("value1"), TimeSpan.FromMinutes(5));
        var result = await cache.GetOrAddAsync("key1", () => Task.FromResult("value2"), TimeSpan.FromMinutes(5));
        
        Assert.Equal("value1", result);
        Assert.True(File.Exists(_dbPath), "Database file should now exist");
    }

    [Fact]
    public async Task Test_ValidDatabase_NotRecreated()
    {
        // Create a cache and populate it with data
        var cache1 = CreateCache();
        await cache1.GetOrAddAsync("key1", () => Task.FromResult("value1"), TimeSpan.FromMinutes(30));
        await cache1.GetOrAddAsync("key2", () => Task.FromResult("value2"), TimeSpan.FromMinutes(30));
        
        // Create a NEW CacheService instance pointing at the same DB path
        var cache2 = new CacheService(_dbPath);
        
        // Verify the data survives (proves we didn't unnecessarily recreate)
        var callCount = 0;
        var result1 = await cache2.GetOrAddAsync("key1", () =>
        {
            callCount++;
            return Task.FromResult("new-value1");
        }, TimeSpan.FromMinutes(30));
        
        var result2 = await cache2.GetOrAddAsync("key2", () =>
        {
            callCount++;
            return Task.FromResult("new-value2");
        }, TimeSpan.FromMinutes(30));
        
        Assert.Equal("value1", result1);
        Assert.Equal("value2", result2);
        Assert.Equal(0, callCount); // Factory was never called — data came from existing DB
    }
}
