using MaestroTool.Core;
using Xunit;

namespace MaestroTool.Tests;

public class CacheServiceTests
{
    [Fact]
    public async Task GetOrAddAsync_ReturnsCachedValue()
    {
        var cache = new CacheService();
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
        var cache = new CacheService();
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
        var cache = new CacheService();
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
        var cache = new CacheService();
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
        var cache = new CacheService();
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
        var cache = new CacheService();

        var result = cache.GetRecentAction("action:trigger:sub1");

        Assert.Null(result);
    }

    [Fact]
    public void RecordAction_ThenGetRecentAction_ReturnsTimestamp()
    {
        var cache = new CacheService();
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
        var cache = new CacheService();

        cache.RecordAction("action:trigger:sub1", TimeSpan.FromMilliseconds(50));

        // Immediately should be non-null
        Assert.NotNull(cache.GetRecentAction("action:trigger:sub1"));

        await Task.Delay(100); // Wait for cooldown to expire

        Assert.Null(cache.GetRecentAction("action:trigger:sub1"));
    }

    [Fact]
    public async Task Clear_ResetsActionRecords()
    {
        var cache = new CacheService();

        // Populate both regular cache and action records
        await cache.GetOrAddAsync("key1", () => Task.FromResult("v1"), TimeSpan.FromMinutes(5));
        cache.RecordAction("action:trigger:sub1", TimeSpan.FromMinutes(5));

        cache.Clear();

        // Both should be gone
        var count = 0;
        await cache.GetOrAddAsync("key1", () => { count++; return Task.FromResult("new"); }, TimeSpan.FromMinutes(5));
        Assert.Equal(1, count); // Cache entry was cleared
        Assert.Null(cache.GetRecentAction("action:trigger:sub1")); // Action record was cleared
    }
}
