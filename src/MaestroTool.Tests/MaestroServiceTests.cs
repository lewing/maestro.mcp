using MaestroTool.Core;
using Microsoft.DotNet.ProductConstructionService.Client.Models;
using NSubstitute;
using Xunit;

namespace MaestroTool.Tests;

public class MaestroServiceTests : IDisposable
{
    private readonly IMaestroApiClient _client;
    private readonly CacheService _cache;
    private readonly MaestroService _service;
    private readonly string _dbPath;

    public MaestroServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mstro-test-{Guid.NewGuid()}.db");
        _client = Substitute.For<IMaestroApiClient>();
        _cache = new CacheService(_dbPath);
        _service = new MaestroService(_client, _cache);
    }

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*"))
        {
            try { File.Delete(f); } catch { }
        }
    }

    // --- Helper factories ---

    private static Channel CreateChannel(int id = 1, string name = ".NET 10") =>
        new(id, name, "product");

    private static Build CreateBuild(int id = 100, string? gitHubRepo = null, DateTimeOffset? date = null) =>
        new(id, date ?? DateTimeOffset.UtcNow, staleness: 0, released: false, stable: true,
            commit: "abc123", channels: new List<Channel>(), assets: new List<Asset>(),
            dependencies: new List<BuildRef>(), incoherencies: new List<BuildIncoherence>())
        {
            GitHubRepository = gitHubRepo ?? "https://github.com/dotnet/runtime"
        };

    private static Subscription CreateSubscription(
        Guid? id = null,
        string source = "https://github.com/dotnet/runtime",
        string target = "https://github.com/dotnet/dotnet",
        string branch = "main",
        Channel? channel = null,
        Build? lastApplied = null)
    {
        var sub = new Subscription(
            id ?? Guid.NewGuid(),
            enabled: true, sourceEnabled: true,
            sourceRepository: source, targetRepository: target,
            targetBranch: branch,
            sourceDirectory: "", targetDirectory: "",
            pullRequestFailureNotificationTags: "",
            excludedAssets: new List<string>())
        {
            Channel = channel ?? CreateChannel(),
            LastAppliedBuild = lastApplied
        };
        return sub;
    }

    private static DefaultChannel CreateDefaultChannel(
        int id = 1, string repo = "https://github.com/dotnet/runtime",
        string branch = "main", Channel? channel = null) =>
        new(id, repo, enabled: true)
        {
            Branch = branch,
            Channel = channel ?? CreateChannel()
        };

    // ================================================================
    // GetSubscriptionsAsync
    // ================================================================

    [Fact]
    public async Task GetSubscriptions_ReturnsFromApi()
    {
        var expected = new List<Subscription> { CreateSubscription() };
        _client.ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _service.GetSubscriptionsAsync();

        Assert.Single(result);
        Assert.Equal(expected[0].Id, result[0].Id);
    }

    [Fact]
    public async Task GetSubscriptions_SecondCallReturnsCached()
    {
        var subs = new List<Subscription> { CreateSubscription() };
        _client.ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>())
            .Returns(subs);

        var first = await _service.GetSubscriptionsAsync();
        var second = await _service.GetSubscriptionsAsync();

        Assert.Equal(first[0].Id, second[0].Id);
        await _client.Received(1).ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSubscriptions_DifferentFiltersUseDifferentCacheKeys()
    {
        var subsBySource = new List<Subscription> { CreateSubscription(source: "https://github.com/dotnet/runtime") };
        var subsByTarget = new List<Subscription> { CreateSubscription(target: "https://github.com/dotnet/aspnetcore") };

        _client.ListSubscriptionsAsync("https://github.com/dotnet/runtime", null, null, true, Arg.Any<CancellationToken>())
            .Returns(subsBySource);
        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/aspnetcore", null, true, Arg.Any<CancellationToken>())
            .Returns(subsByTarget);

        var result1 = await _service.GetSubscriptionsAsync(sourceRepository: "https://github.com/dotnet/runtime");
        var result2 = await _service.GetSubscriptionsAsync(targetRepository: "https://github.com/dotnet/aspnetcore");

        Assert.Single(result1);
        Assert.Single(result2);
        Assert.NotSame(result1, result2);
    }

    [Fact]
    public async Task GetSubscriptions_ReturnsEmptyList()
    {
        _client.ListSubscriptionsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(), true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription>());

        var result = await _service.GetSubscriptionsAsync(sourceRepository: "https://github.com/dotnet/nonexistent");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSubscriptions_WithChannelId()
    {
        var expected = new List<Subscription> { CreateSubscription() };
        _client.ListSubscriptionsAsync(null, null, 42, true, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _service.GetSubscriptionsAsync(channelId: 42);

        Assert.Equal(expected[0].Id, result[0].Id);
    }

    // ================================================================
    // GetSubscriptionAsync
    // ================================================================

    [Fact]
    public async Task GetSubscription_ReturnsSubscription()
    {
        var id = Guid.NewGuid();
        var sub = CreateSubscription(id: id);
        _client.GetSubscriptionAsync(id, Arg.Any<CancellationToken>()).Returns(sub);

        var result = await _service.GetSubscriptionAsync(id);

        Assert.Equal(id, result.Id);
        Assert.Equal(sub.SourceRepository, result.SourceRepository);
    }

    [Fact]
    public async Task GetSubscription_CachesByGuid()
    {
        var id = Guid.NewGuid();
        var sub = CreateSubscription(id: id);
        _client.GetSubscriptionAsync(id, Arg.Any<CancellationToken>()).Returns(sub);

        var first = await _service.GetSubscriptionAsync(id);
        var second = await _service.GetSubscriptionAsync(id);

        Assert.Equal(first.Id, second.Id);
        await _client.Received(1).GetSubscriptionAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSubscription_DifferentIdsMakeSeparateCalls()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var sub1 = CreateSubscription(id: id1, source: "repo1");
        var sub2 = CreateSubscription(id: id2, source: "repo2");

        _client.GetSubscriptionAsync(id1, Arg.Any<CancellationToken>()).Returns(sub1);
        _client.GetSubscriptionAsync(id2, Arg.Any<CancellationToken>()).Returns(sub2);

        var result1 = await _service.GetSubscriptionAsync(id1);
        var result2 = await _service.GetSubscriptionAsync(id2);

        Assert.Equal("repo1", result1.SourceRepository);
        Assert.Equal("repo2", result2.SourceRepository);
    }

    // ================================================================
    // GetLatestBuildAsync
    // ================================================================

    [Fact]
    public async Task GetLatestBuild_ReturnsBuild()
    {
        var build = CreateBuild(id: 42);
        _client.GetLatestBuildAsync("https://github.com/dotnet/runtime", null, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _service.GetLatestBuildAsync("https://github.com/dotnet/runtime");

        Assert.NotNull(result);
        Assert.Equal(42, result!.Id);
    }

    [Fact]
    public async Task GetLatestBuild_ReturnsNull_WhenNoBuild()
    {
        _client.GetLatestBuildAsync("https://github.com/dotnet/empty", null, Arg.Any<CancellationToken>())
            .Returns((Build?)null);

        var result = await _service.GetLatestBuildAsync("https://github.com/dotnet/empty");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestBuild_CachesResult()
    {
        var build = CreateBuild(id: 99);
        _client.GetLatestBuildAsync("https://github.com/dotnet/runtime", null, Arg.Any<CancellationToken>())
            .Returns(build);

        var first = await _service.GetLatestBuildAsync("https://github.com/dotnet/runtime");
        var second = await _service.GetLatestBuildAsync("https://github.com/dotnet/runtime");

        Assert.Equal(first!.Id, second!.Id);
        await _client.Received(1).GetLatestBuildAsync("https://github.com/dotnet/runtime", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLatestBuild_DifferentChannelIds_DifferentCacheEntries()
    {
        var build1 = CreateBuild(id: 10);
        var build2 = CreateBuild(id: 20);

        _client.GetLatestBuildAsync("repo", 1, Arg.Any<CancellationToken>()).Returns(build1);
        _client.GetLatestBuildAsync("repo", 2, Arg.Any<CancellationToken>()).Returns(build2);

        var r1 = await _service.GetLatestBuildAsync("repo", channelId: 1);
        var r2 = await _service.GetLatestBuildAsync("repo", channelId: 2);

        Assert.Equal(10, r1!.Id);
        Assert.Equal(20, r2!.Id);
    }

    // ================================================================
    // GetBuildAsync
    // ================================================================

    [Fact]
    public async Task GetBuild_ReturnsBuild()
    {
        var build = CreateBuild(id: 55);
        _client.GetBuildAsync(55, Arg.Any<CancellationToken>()).Returns(build);

        var result = await _service.GetBuildAsync(55);

        Assert.Equal(55, result.Id);
    }

    [Fact]
    public async Task GetBuild_CachesWithLongTtl()
    {
        var build = CreateBuild(id: 77);
        _client.GetBuildAsync(77, Arg.Any<CancellationToken>()).Returns(build);

        var first = await _service.GetBuildAsync(77);
        var second = await _service.GetBuildAsync(77);

        Assert.Equal(first.Id, second.Id);
        await _client.Received(1).GetBuildAsync(77, Arg.Any<CancellationToken>());
    }

    // ================================================================
    // GetChannelsAsync / GetChannelByNameAsync
    // ================================================================

    [Fact]
    public async Task GetChannels_ReturnsChannelList()
    {
        var channels = new List<Channel> { CreateChannel(1, ".NET 10"), CreateChannel(2, ".NET 9") };
        _client.ListChannelsAsync(Arg.Any<CancellationToken>()).Returns(channels);

        var result = await _service.GetChannelsAsync();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetChannels_CachesResult()
    {
        _client.ListChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Channel> { CreateChannel() });

        await _service.GetChannelsAsync();
        await _service.GetChannelsAsync();

        await _client.Received(1).ListChannelsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetChannelByName_FindsMatchingChannel()
    {
        var channels = new List<Channel>
        {
            CreateChannel(1, ".NET 10.0.1xx SDK"),
            CreateChannel(2, ".NET 9.0.1xx SDK")
        };
        _client.ListChannelsAsync(Arg.Any<CancellationToken>()).Returns(channels);

        var result = await _service.GetChannelByNameAsync(".NET 10.0.1xx SDK");

        Assert.NotNull(result);
        Assert.Equal(1, result!.Id);
    }

    [Fact]
    public async Task GetChannelByName_CaseInsensitive()
    {
        var channels = new List<Channel> { CreateChannel(1, ".NET 10") };
        _client.ListChannelsAsync(Arg.Any<CancellationToken>()).Returns(channels);

        var result = await _service.GetChannelByNameAsync(".net 10");

        Assert.NotNull(result);
        Assert.Equal(1, result!.Id);
    }

    [Fact]
    public async Task GetChannelByName_ReturnsNull_WhenNotFound()
    {
        _client.ListChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Channel> { CreateChannel(1, ".NET 10") });

        var result = await _service.GetChannelByNameAsync("nonexistent");

        Assert.Null(result);
    }

    // ================================================================
    // GetDefaultChannelsAsync
    // ================================================================

    [Fact]
    public async Task GetDefaultChannels_ReturnsResults()
    {
        var defaults = new List<DefaultChannel> { CreateDefaultChannel() };
        _client.ListDefaultChannelsAsync(null, null, null, Arg.Any<CancellationToken>())
            .Returns(defaults);

        var result = await _service.GetDefaultChannelsAsync();

        Assert.Single(result);
    }

    [Fact]
    public async Task GetDefaultChannels_FiltersByRepo()
    {
        var defaults = new List<DefaultChannel>
        {
            CreateDefaultChannel(repo: "https://github.com/dotnet/runtime")
        };
        _client.ListDefaultChannelsAsync("https://github.com/dotnet/runtime", null, null, Arg.Any<CancellationToken>())
            .Returns(defaults);

        var result = await _service.GetDefaultChannelsAsync(repository: "https://github.com/dotnet/runtime");

        Assert.Single(result);
    }

    [Fact]
    public async Task GetDefaultChannels_CachesResult()
    {
        _client.ListDefaultChannelsAsync(null, null, null, Arg.Any<CancellationToken>())
            .Returns(new List<DefaultChannel> { CreateDefaultChannel() });

        await _service.GetDefaultChannelsAsync();
        await _service.GetDefaultChannelsAsync();

        await _client.Received(1).ListDefaultChannelsAsync(null, null, null, Arg.Any<CancellationToken>());
    }

    // ================================================================
    // GetSubscriptionHealthAsync
    // ================================================================

    [Fact]
    public async Task SubscriptionHealth_DetectsStaleSubscription()
    {
        var channel = CreateChannel(1, ".NET 10");
        var lastApplied = CreateBuild(id: 90);
        var latestBuild = CreateBuild(id: 95);
        var sub = CreateSubscription(
            target: "https://github.com/dotnet/dotnet",
            channel: channel,
            lastApplied: lastApplied);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/dotnet", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns(latestBuild);

        var results = await _service.GetSubscriptionHealthAsync("https://github.com/dotnet/dotnet");

        Assert.Single(results);
        var r = results[0];
        Assert.True(r.IsStale);
        Assert.Equal(5, r.BuildsBehind);
        Assert.Equal(90, r.LastAppliedBuildId);
        Assert.Equal(95, r.LatestBuildId);
    }

    [Fact]
    public async Task SubscriptionHealth_DetectsCurrentSubscription()
    {
        var channel = CreateChannel(1, ".NET 10");
        var build = CreateBuild(id: 100);
        var sub = CreateSubscription(
            target: "https://github.com/dotnet/dotnet",
            channel: channel,
            lastApplied: build);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/dotnet", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns(build);

        var results = await _service.GetSubscriptionHealthAsync("https://github.com/dotnet/dotnet");

        Assert.Single(results);
        Assert.False(results[0].IsStale);
        Assert.Equal(0, results[0].BuildsBehind);
    }

    [Fact]
    public async Task SubscriptionHealth_HandlesNoLastAppliedBuild()
    {
        var channel = CreateChannel(1, ".NET 10");
        var latestBuild = CreateBuild(id: 50);
        var sub = CreateSubscription(
            target: "https://github.com/dotnet/dotnet",
            channel: channel,
            lastApplied: null);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/dotnet", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns(latestBuild);

        var results = await _service.GetSubscriptionHealthAsync("https://github.com/dotnet/dotnet");

        Assert.Single(results);
        Assert.False(results[0].IsStale); // Not stale because lastApplied is null
        Assert.Null(results[0].LastAppliedBuildId);
        Assert.Equal(50, results[0].LatestBuildId);
    }

    [Fact]
    public async Task SubscriptionHealth_HandlesNoLatestBuild()
    {
        var channel = CreateChannel(1, ".NET 10");
        var lastApplied = CreateBuild(id: 42);
        var sub = CreateSubscription(
            target: "https://github.com/dotnet/dotnet",
            channel: channel,
            lastApplied: lastApplied);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/dotnet", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns((Build?)null);

        var results = await _service.GetSubscriptionHealthAsync("https://github.com/dotnet/dotnet");

        Assert.Single(results);
        Assert.False(results[0].IsStale);
        Assert.Null(results[0].LatestBuildId);
    }

    [Fact]
    public async Task SubscriptionHealth_SkipsSubsWithNoChannel()
    {
        var sub = CreateSubscription(target: "https://github.com/dotnet/dotnet");
        sub.Channel = null; // No channel

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/dotnet", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });

        var results = await _service.GetSubscriptionHealthAsync("https://github.com/dotnet/dotnet");

        Assert.Empty(results); // Skipped due to null channel
    }

    [Fact]
    public async Task SubscriptionHealth_ReturnsEmptyWhenNoSubscriptions()
    {
        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/empty", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription>());

        var results = await _service.GetSubscriptionHealthAsync("https://github.com/dotnet/empty");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SubscriptionHealth_MultipleSubscriptions()
    {
        var channel = CreateChannel(1, ".NET 10");
        var staleSub = CreateSubscription(
            source: "https://github.com/dotnet/runtime",
            target: "https://github.com/dotnet/dotnet",
            channel: channel,
            lastApplied: CreateBuild(id: 80));
        var currentSub = CreateSubscription(
            source: "https://github.com/dotnet/aspnetcore",
            target: "https://github.com/dotnet/dotnet",
            channel: channel,
            lastApplied: CreateBuild(id: 100));

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/dotnet", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { staleSub, currentSub });
        _client.GetLatestBuildAsync("https://github.com/dotnet/runtime", channel.Id, Arg.Any<CancellationToken>())
            .Returns(CreateBuild(id: 90));
        _client.GetLatestBuildAsync("https://github.com/dotnet/aspnetcore", channel.Id, Arg.Any<CancellationToken>())
            .Returns(CreateBuild(id: 100));

        var results = await _service.GetSubscriptionHealthAsync("https://github.com/dotnet/dotnet");

        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsStale);
        Assert.Equal(10, results[0].BuildsBehind);
        Assert.False(results[1].IsStale);
    }

    [Fact]
    public async Task SubscriptionHealth_PopulatesAllFields()
    {
        var channel = CreateChannel(5, "TestChannel");
        var applied = CreateBuild(id: 30, date: DateTimeOffset.Parse("2025-01-15T10:00:00Z"));
        var latest = CreateBuild(id: 35, date: DateTimeOffset.Parse("2025-01-16T10:00:00Z"));
        var subId = Guid.NewGuid();
        var sub = CreateSubscription(
            id: subId,
            source: "https://github.com/dotnet/runtime",
            target: "https://github.com/dotnet/dotnet",
            branch: "release/10.0",
            channel: channel,
            lastApplied: applied);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/dotnet", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync("https://github.com/dotnet/runtime", 5, Arg.Any<CancellationToken>())
            .Returns(latest);

        var results = await _service.GetSubscriptionHealthAsync("https://github.com/dotnet/dotnet");

        var r = results[0];
        Assert.Equal(subId, r.SubscriptionId);
        Assert.Equal("https://github.com/dotnet/runtime", r.SourceRepository);
        Assert.Equal("https://github.com/dotnet/dotnet", r.TargetRepository);
        Assert.Equal("release/10.0", r.TargetBranch);
        Assert.Equal("TestChannel", r.ChannelName);
        Assert.True(r.IsStale);
        Assert.Equal(5, r.BuildsBehind);
        Assert.Equal(30, r.LastAppliedBuildId);
        Assert.Equal(35, r.LatestBuildId);
        Assert.NotNull(r.LastAppliedDate);
        Assert.NotNull(r.LatestBuildDate);
    }

    [Fact]
    public async Task SubscriptionHealth_HandlesApiErrorForSingleSubscription()
    {
        var channel = CreateChannel(1, ".NET 10");
        var failingSub = CreateSubscription(
            source: "https://github.com/dotnet/runtime",
            target: "https://github.com/dotnet/dotnet",
            channel: channel,
            lastApplied: CreateBuild(id: 80));
        var workingSub = CreateSubscription(
            source: "https://github.com/dotnet/aspnetcore",
            target: "https://github.com/dotnet/dotnet",
            channel: channel,
            lastApplied: CreateBuild(id: 100));

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/dotnet", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { failingSub, workingSub });
        _client.GetLatestBuildAsync("https://github.com/dotnet/runtime", channel.Id, Arg.Any<CancellationToken>())
            .Returns<Build?>(_ => throw new HttpRequestException("API timeout"));
        _client.GetLatestBuildAsync("https://github.com/dotnet/aspnetcore", channel.Id, Arg.Any<CancellationToken>())
            .Returns(CreateBuild(id: 100));

        var results = await _service.GetSubscriptionHealthAsync("https://github.com/dotnet/dotnet");

        Assert.Equal(2, results.Count);

        var failed = results.First(r => r.SourceRepository == "https://github.com/dotnet/runtime");
        Assert.NotNull(failed.Error);

        var working = results.First(r => r.SourceRepository == "https://github.com/dotnet/aspnetcore");
        Assert.Null(working.Error);
        Assert.False(working.IsStale);
    }

    [Fact]
    public async Task SubscriptionHealth_HandlesApiErrorForAllSubscriptions()
    {
        var channel = CreateChannel(1, ".NET 10");
        var sub1 = CreateSubscription(
            source: "https://github.com/dotnet/runtime",
            target: "https://github.com/dotnet/dotnet",
            channel: channel,
            lastApplied: CreateBuild(id: 80));
        var sub2 = CreateSubscription(
            source: "https://github.com/dotnet/aspnetcore",
            target: "https://github.com/dotnet/dotnet",
            channel: channel,
            lastApplied: CreateBuild(id: 90));

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/dotnet", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub1, sub2 });
        _client.GetLatestBuildAsync("https://github.com/dotnet/runtime", channel.Id, Arg.Any<CancellationToken>())
            .Returns<Build?>(_ => throw new HttpRequestException("API timeout"));
        _client.GetLatestBuildAsync("https://github.com/dotnet/aspnetcore", channel.Id, Arg.Any<CancellationToken>())
            .Returns<Build?>(_ => throw new HttpRequestException("Service unavailable"));

        var results = await _service.GetSubscriptionHealthAsync("https://github.com/dotnet/dotnet");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.NotNull(r.Error));
    }

    [Fact]
    public async Task SubscriptionHealth_ErrorResultHasBasicFields()
    {
        var channel = CreateChannel(5, "TestChannel");
        var subId = Guid.NewGuid();
        var sub = CreateSubscription(
            id: subId,
            source: "https://github.com/dotnet/runtime",
            target: "https://github.com/dotnet/dotnet",
            branch: "release/10.0",
            channel: channel,
            lastApplied: CreateBuild(id: 42));

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/dotnet", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync("https://github.com/dotnet/runtime", 5, Arg.Any<CancellationToken>())
            .Returns<Build?>(_ => throw new InvalidOperationException("Upstream failure"));

        var results = await _service.GetSubscriptionHealthAsync("https://github.com/dotnet/dotnet");

        Assert.Single(results);
        var r = results[0];
        Assert.NotNull(r.Error);
        Assert.Equal(subId, r.SubscriptionId);
        Assert.Equal("https://github.com/dotnet/runtime", r.SourceRepository);
        Assert.Equal("https://github.com/dotnet/dotnet", r.TargetRepository);
        Assert.Equal("release/10.0", r.TargetBranch);
        Assert.Equal("TestChannel", r.ChannelName);
    }

    // ================================================================
    // noCache parameter tests
    // ================================================================

    [Fact]
    public async Task GetSubscriptions_NoCache_BypassesCache()
    {
        var firstResult = new List<Subscription> { CreateSubscription(source: "repo1") };
        var secondResult = new List<Subscription> { CreateSubscription(source: "repo2") };

        _client.ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>())
            .Returns(firstResult, secondResult);

        var first = await _service.GetSubscriptionsAsync();
        var second = await _service.GetSubscriptionsAsync(noCache: true);

        Assert.Equal(firstResult[0].SourceRepository, first[0].SourceRepository);
        Assert.Equal(secondResult[0].SourceRepository, second[0].SourceRepository);
        await _client.Received(2).ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSubscriptions_NoCacheFalse_ReturnsCached()
    {
        var expected = new List<Subscription> { CreateSubscription() };
        _client.ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>())
            .Returns(expected);

        var first = await _service.GetSubscriptionsAsync(noCache: false);
        var second = await _service.GetSubscriptionsAsync(noCache: false);

        Assert.Equal(first[0].Id, second[0].Id);
        await _client.Received(1).ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetChannels_NoCache_BypassesCache()
    {
        var firstResult = new List<Channel> { CreateChannel(1, ".NET 10") };
        var secondResult = new List<Channel> { CreateChannel(2, ".NET 9") };

        _client.ListChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(firstResult, secondResult);

        var first = await _service.GetChannelsAsync();
        var second = await _service.GetChannelsAsync(noCache: true);

        Assert.Equal(firstResult[0].Id, first[0].Id);
        Assert.Equal(secondResult[0].Id, second[0].Id);
        await _client.Received(2).ListChannelsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetChannels_NoCacheFalse_ReturnsCached()
    {
        var expected = new List<Channel> { CreateChannel() };
        _client.ListChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(expected);

        var first = await _service.GetChannelsAsync(noCache: false);
        var second = await _service.GetChannelsAsync(noCache: false);

        Assert.Equal(first[0].Id, second[0].Id);
        await _client.Received(1).ListChannelsAsync(Arg.Any<CancellationToken>());
    }

    // ================================================================
    // Trigger method tests
    // ================================================================

    [Fact]
    public async Task TriggerSubscription_CallsThroughToClient()
    {
        var subId = Guid.NewGuid();
        var expected = CreateSubscription(id: subId);
        _client.TriggerSubscriptionAsync(subId, 42, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _service.TriggerSubscriptionAsync(subId, 42);

        Assert.Equal(expected.Id, result.Id);
        await _client.Received(1).TriggerSubscriptionAsync(subId, 42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerDailyUpdate_CallsThroughToClient()
    {
        await _service.TriggerDailyUpdateAsync();

        await _client.Received(1).TriggerDailyUpdateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerSubscription_InvalidatesRelatedCaches()
    {
        var subId = Guid.NewGuid();
        var sub = CreateSubscription(id: subId);
        var subs = new List<Subscription> { sub };

        // Setup: populate caches
        _client.ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>())
            .Returns(subs);
        _client.GetSubscriptionAsync(subId, Arg.Any<CancellationToken>())
            .Returns(sub);
        _client.TriggerSubscriptionAsync(subId, 1, Arg.Any<CancellationToken>())
            .Returns(sub);

        // Populate caches
        await _service.GetSubscriptionsAsync();
        await _service.GetSubscriptionAsync(subId);

        // Trigger should invalidate
        await _service.TriggerSubscriptionAsync(subId, 1);

        // Next reads should hit API again
        await _service.GetSubscriptionsAsync();
        await _service.GetSubscriptionAsync(subId);

        await _client.Received(2).ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>());
        await _client.Received(2).GetSubscriptionAsync(subId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerDailyUpdate_InvalidatesSubscriptionCaches()
    {
        var subs = new List<Subscription> { CreateSubscription() };
        _client.ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>())
            .Returns(subs);

        // Populate cache
        await _service.GetSubscriptionsAsync();

        // Trigger daily update
        await _service.TriggerDailyUpdateAsync();

        // Next read should hit API again
        await _service.GetSubscriptionsAsync();

        await _client.Received(2).ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>());
    }

    // ================================================================
    // Security: SSRF validation (Fix 1)
    // ================================================================

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("../admin")]
    [InlineData("10.0/../../secret")]
    [InlineData("channel name with spaces")]
    [InlineData("channel;drop")]
    public async Task GetBuildFreshnessAsync_RejectsInvalidChannelCharacters(string channel)
    {
        var result = await _service.GetBuildFreshnessAsync(channel);

        Assert.False(result.IsAvailable);
        Assert.NotNull(result.Error);
        Assert.Contains("invalid", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("10.0.1xx")]
    [InlineData("9.0")]
    [InlineData("net10")]
    [InlineData("9.0.1xx-preview1")]
    public async Task GetBuildFreshnessAsync_AcceptsValidChannelNames(string channel)
    {
        // Valid channels should pass validation. They may fail with network errors,
        // but the error should NOT be a validation rejection.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var result = await _service.GetBuildFreshnessAsync(channel, cancellationToken: cts.Token);
            if (!result.IsAvailable && result.Error != null)
            {
                Assert.DoesNotContain("Invalid channel name", result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // Network timeout is acceptable — validation didn't reject the channel
        }
    }

    // ================================================================
    // Security: Auth gating on write operations (Fix 2)
    // ================================================================

    [Fact]
    public async Task TriggerSubscription_RequiresAuthentication()
    {
        _client.AuthLevel.Returns(AuthLevel.Anonymous);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.TriggerSubscriptionAsync(Guid.NewGuid(), 42));

        Assert.Contains("Authentication required", ex.Message);
    }

    [Fact]
    public async Task TriggerDailyUpdate_RequiresAuthentication()
    {
        _client.AuthLevel.Returns(AuthLevel.Anonymous);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.TriggerDailyUpdateAsync());

        Assert.Contains("Authentication required", ex.Message);
    }

    // ================================================================
    // Security: Stderr audit logging (Fix 4)
    // ================================================================

    [Fact]
    public async Task TriggerSubscription_LogsToStderr()
    {
        var subId = Guid.NewGuid();
        _client.TriggerSubscriptionAsync(subId, 42, Arg.Any<CancellationToken>())
            .Returns(CreateSubscription(id: subId));

        var originalErr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            await _service.TriggerSubscriptionAsync(subId, 42);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        var output = captured.ToString();
        Assert.Contains("Trigger", output);
        Assert.Contains(subId.ToString(), output);
    }

    // ================================================================
    // Input validation: null/empty parameters
    // ================================================================

    [Fact]
    public async Task GetSubscriptions_HandlesNullSourceRepo()
    {
        _client.ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription>());

        var result = await _service.GetSubscriptionsAsync(sourceRepository: null);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetSubscriptions_HandlesNullTargetRepo()
    {
        _client.ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription>());

        var result = await _service.GetSubscriptionsAsync(targetRepository: null);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetBuilds_HandlesZeroBuildId()
    {
        // buildId=0 is invalid — should be caught by validation
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _service.GetBuildAsync(0));
    }

    [Fact]
    public async Task GetBuilds_HandlesNegativeBuildId()
    {
        // buildId=-1 is invalid — should be caught by validation
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _service.GetBuildAsync(-1));
    }
}
