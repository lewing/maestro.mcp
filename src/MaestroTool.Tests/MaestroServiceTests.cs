using System.Collections.Immutable;
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

    private static Build CreateBuild(int id = 100, string? gitHubRepo = null, DateTimeOffset? date = null, string? commit = null) =>
        new(id, date ?? DateTimeOffset.UtcNow, staleness: 0, released: false, stable: true,
            commit: commit ?? "abc123", channels: new List<Channel>(), assets: new List<Asset>(),
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

    private static BuildGraph CreateBuildGraph(params Build[] builds)
    {
        var dict = new Dictionary<string, Build>();
        foreach (var b in builds)
            dict[b.Id.ToString()] = b;
        return new BuildGraph(dict);
    }

    private static FlowGraph CreateFlowGraph(List<FlowRef>? refs = null, List<FlowEdge>? edges = null)
    {
        return new FlowGraph(
            refs ?? new List<FlowRef>(),
            edges ?? new List<FlowEdge>()
        );
    }

    private static FlowRef CreateFlowRef(string id = "ref1", string repo = "https://github.com/dotnet/runtime", string branch = "main")
    {
        return new FlowRef(
            officialBuildTime: 30.0,
            prBuildTime: 15.0,
            onLongestBuildPath: false,
            bestCasePathTime: 60.0,
            worstCasePathTime: 120.0,
            goalTimeInMinutes: 90
        )
        {
            Id = id,
            Repository = repo,
            Branch = branch
        };
    }

    private static FlowEdge CreateFlowEdge(string fromId = "ref1", string toId = "ref2", string channel = ".NET 10")
    {
        return new FlowEdge(
            subscriptionId: Guid.NewGuid(),
            onLongestBuildPath: false,
            isToolingOnly: false,
            backEdge: false,
            toId: toId,
            fromId: fromId
        )
        {
            ChannelName = channel,
            PartOfCycle = null
        };
    }

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

    [Fact]
    public async Task GetSubscriptions_WithTargetBranch_FiltersResults()
    {
        var sub1 = CreateSubscription(branch: "main");
        var sub2 = CreateSubscription(branch: "release/9.0");
        var sub3 = CreateSubscription(branch: "main");
        var allSubs = new List<Subscription> { sub1, sub2, sub3 };

        _client.ListSubscriptionsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(), true, Arg.Any<CancellationToken>())
            .Returns(allSubs);

        var result = await _service.GetSubscriptionsAsync(targetBranch: "main");

        Assert.Equal(2, result.Count);
        Assert.All(result, s => Assert.Equal("main", s.TargetBranch));
    }

    [Fact]
    public async Task GetSubscriptions_WithTargetBranch_CaseInsensitive()
    {
        var sub = CreateSubscription(branch: "Main");
        var allSubs = new List<Subscription> { sub };

        _client.ListSubscriptionsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(), true, Arg.Any<CancellationToken>())
            .Returns(allSubs);

        var result = await _service.GetSubscriptionsAsync(targetBranch: "main");

        Assert.Single(result);
    }

    [Fact]
    public async Task GetSubscriptions_WithTargetBranch_NoMatch_ReturnsEmpty()
    {
        var sub1 = CreateSubscription(branch: "main");
        var sub2 = CreateSubscription(branch: "release/9.0");
        var allSubs = new List<Subscription> { sub1, sub2 };

        _client.ListSubscriptionsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(), true, Arg.Any<CancellationToken>())
            .Returns(allSubs);

        var result = await _service.GetSubscriptionsAsync(targetBranch: "release/10.0");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSubscriptions_WithTargetBranch_NullMeansNoFilter()
    {
        var sub1 = CreateSubscription(branch: "main");
        var sub2 = CreateSubscription(branch: "release/9.0");
        var sub3 = CreateSubscription(branch: "develop");
        var allSubs = new List<Subscription> { sub1, sub2, sub3 };

        _client.ListSubscriptionsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(), true, Arg.Any<CancellationToken>())
            .Returns(allSubs);

        var result = await _service.GetSubscriptionsAsync(targetBranch: null);

        Assert.Equal(3, result.Count);
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
    // Subscription Health - GitHub Commit Distance (Issue #4, #6)
    // ================================================================

    [Fact]
    public async Task GetSubscriptionHealth_VmrSubscription_WithGitHubClient_ReturnsCommitsBehind()
    {
        // Arrange: VMR subscription with GitHub client that returns commit distance
        var channel = CreateChannel(1, ".NET 10");
        var lastAppliedBuild = CreateBuild(id: 100, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "abc123");
        var latestBuild = CreateBuild(id: 105, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "def456");
        
        var sub = CreateSubscription(
            source: "https://github.com/dotnet/dotnet",
            target: "https://github.com/dotnet/aspnetcore",
            channel: channel,
            lastApplied: lastAppliedBuild);

        var mockGitHub = Substitute.For<IGitHubApiClient>();
        mockGitHub.CompareCommitsAsync("dotnet", "dotnet", "abc123", "def456", Arg.Any<CancellationToken>())
            .Returns(new GitHubCompareResult(AheadBy: 33, BehindBy: 0, Status: "ahead", TotalCommits: 33));

        var serviceWithGitHub = new MaestroService(_client, _cache, mockGitHub);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/aspnetcore", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns(latestBuild);

        // Act
        var results = await serviceWithGitHub.GetSubscriptionHealthAsync("https://github.com/dotnet/aspnetcore");

        // Assert
        Assert.Single(results);
        Assert.True(results[0].IsStale);
        Assert.Equal(5, results[0].BuildsBehind); // Approximate (105 - 100)
        Assert.Equal(33, results[0].CommitsBehind); // Accurate from GitHub API
    }

    [Fact]
    public async Task GetSubscriptionHealth_VmrSubscription_GitHubClientReturnsNull_FallsBackToBuildsBehind()
    {
        // Arrange: VMR subscription where GitHub API returns null (failure case)
        var channel = CreateChannel(1, ".NET 10");
        var lastAppliedBuild = CreateBuild(id: 100, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "abc123");
        var latestBuild = CreateBuild(id: 108, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "def456");
        
        var sub = CreateSubscription(
            source: "https://github.com/dotnet/dotnet",
            target: "https://github.com/dotnet/aspnetcore",
            channel: channel,
            lastApplied: lastAppliedBuild);

        var mockGitHub = Substitute.For<IGitHubApiClient>();
        mockGitHub.CompareCommitsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((GitHubCompareResult?)null); // GitHub API failure

        var serviceWithGitHub = new MaestroService(_client, _cache, mockGitHub);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/aspnetcore", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns(latestBuild);

        // Act
        var results = await serviceWithGitHub.GetSubscriptionHealthAsync("https://github.com/dotnet/aspnetcore");

        // Assert
        Assert.Single(results);
        Assert.True(results[0].IsStale);
        Assert.Equal(8, results[0].BuildsBehind); // Fallback works
        Assert.Null(results[0].CommitsBehind); // GitHub API failed, so null
    }

    [Fact]
    public async Task GetSubscriptionHealth_GitHubHostedSubscription_ReturnsCommitsBehind()
    {
        // Arrange: Non-VMR GitHub-hosted subscription (dotnet/runtime) — Issue #6 widens commit distance to all GitHub repos
        var channel = CreateChannel(1, ".NET 10");
        var lastAppliedBuild = CreateBuild(id: 100, gitHubRepo: "https://github.com/dotnet/runtime", commit: "aaa111");
        var latestBuild = CreateBuild(id: 105, gitHubRepo: "https://github.com/dotnet/runtime", commit: "bbb222");
        
        var sub = CreateSubscription(
            source: "https://github.com/dotnet/runtime", // Non-VMR GitHub repo
            target: "https://github.com/dotnet/aspnetcore",
            channel: channel,
            lastApplied: lastAppliedBuild);

        var mockGitHub = Substitute.For<IGitHubApiClient>();
        mockGitHub.CompareCommitsAsync("dotnet", "runtime", "aaa111", "bbb222", Arg.Any<CancellationToken>())
            .Returns(new GitHubCompareResult(AheadBy: 12, BehindBy: 0, Status: "ahead", TotalCommits: 12));

        var serviceWithGitHub = new MaestroService(_client, _cache, mockGitHub);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/aspnetcore", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns(latestBuild);

        // Act
        var results = await serviceWithGitHub.GetSubscriptionHealthAsync("https://github.com/dotnet/aspnetcore");

        // Assert
        Assert.Single(results);
        Assert.True(results[0].IsStale);
        Assert.Equal(5, results[0].BuildsBehind);
        Assert.Equal(12, results[0].CommitsBehind); // GitHub-hosted repos now get commit distance

        // Verify GitHub client WAS called with correct owner/repo parsed from the URL
        await mockGitHub.Received(1).CompareCommitsAsync("dotnet", "runtime", "aaa111", "bbb222", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSubscriptionHealth_AzDoHostedSubscription_CommitsBehindIsNull()
    {
        // Arrange: AzDO-hosted subscription — cannot use GitHub Compare API
        var channel = CreateChannel(1, ".NET 10");
        var lastAppliedBuild = CreateBuild(id: 200, gitHubRepo: "https://dev.azure.com/dnceng/internal/_git/dotnet-runtime", commit: "ccc333");
        var latestBuild = CreateBuild(id: 210, gitHubRepo: "https://dev.azure.com/dnceng/internal/_git/dotnet-runtime", commit: "ddd444");
        
        var sub = CreateSubscription(
            source: "https://dev.azure.com/dnceng/internal/_git/dotnet-runtime",
            target: "https://github.com/dotnet/dotnet",
            channel: channel,
            lastApplied: lastAppliedBuild);

        var mockGitHub = Substitute.For<IGitHubApiClient>();
        var serviceWithGitHub = new MaestroService(_client, _cache, mockGitHub);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/dotnet", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns(latestBuild);

        // Act
        var results = await serviceWithGitHub.GetSubscriptionHealthAsync("https://github.com/dotnet/dotnet");

        // Assert
        Assert.Single(results);
        Assert.True(results[0].IsStale);
        Assert.Equal(10, results[0].BuildsBehind);
        Assert.Null(results[0].CommitsBehind); // AzDO repos can't use GitHub compare

        // Verify GitHub client was never called for non-GitHub source repos
        await mockGitHub.DidNotReceive().CompareCommitsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSubscriptionHealth_NonVmrGitHubRepo_CallsCompareWithCorrectOwnerRepo()
    {
        // Arrange: Non-VMR GitHub repo (dotnet/roslyn) — verify owner/repo parsing and CompareCommitsAsync invocation
        var channel = CreateChannel(1, ".NET 10");
        var lastAppliedBuild = CreateBuild(id: 300, gitHubRepo: "https://github.com/dotnet/roslyn", commit: "eee555");
        var latestBuild = CreateBuild(id: 307, gitHubRepo: "https://github.com/dotnet/roslyn", commit: "fff666");
        
        var sub = CreateSubscription(
            source: "https://github.com/dotnet/roslyn",
            target: "https://github.com/dotnet/dotnet",
            channel: channel,
            lastApplied: lastAppliedBuild);

        var mockGitHub = Substitute.For<IGitHubApiClient>();
        mockGitHub.CompareCommitsAsync("dotnet", "roslyn", "eee555", "fff666", Arg.Any<CancellationToken>())
            .Returns(new GitHubCompareResult(AheadBy: 47, BehindBy: 0, Status: "ahead", TotalCommits: 47));

        var serviceWithGitHub = new MaestroService(_client, _cache, mockGitHub);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/dotnet", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns(latestBuild);

        // Act
        var results = await serviceWithGitHub.GetSubscriptionHealthAsync("https://github.com/dotnet/dotnet");

        // Assert
        Assert.Single(results);
        Assert.True(results[0].IsStale);
        Assert.Equal(7, results[0].BuildsBehind);
        Assert.Equal(47, results[0].CommitsBehind); // Commit distance computed for non-VMR GitHub repos

        // Verify CompareCommitsAsync called with "dotnet"/"roslyn" — NOT "dotnet"/"dotnet" (VMR)
        await mockGitHub.Received(1).CompareCommitsAsync("dotnet", "roslyn", "eee555", "fff666", Arg.Any<CancellationToken>());
        // Ensure it wasn't called with VMR params
        await mockGitHub.DidNotReceive().CompareCommitsAsync("dotnet", "dotnet", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSubscriptionHealth_NullGitHubClient_CommitsBehindIsNull()
    {
        // Arrange: VMR subscription but NO GitHub client provided
        var channel = CreateChannel(1, ".NET 10");
        var lastAppliedBuild = CreateBuild(id: 100, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "abc123");
        var latestBuild = CreateBuild(id: 105, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "def456");
        
        var sub = CreateSubscription(
            source: "https://github.com/dotnet/dotnet",
            target: "https://github.com/dotnet/aspnetcore",
            channel: channel,
            lastApplied: lastAppliedBuild);

        // Use default service without GitHub client (null)
        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/aspnetcore", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns(latestBuild);

        // Act
        var results = await _service.GetSubscriptionHealthAsync("https://github.com/dotnet/aspnetcore");

        // Assert
        Assert.Single(results);
        Assert.True(results[0].IsStale);
        Assert.Equal(5, results[0].BuildsBehind); // BuildsBehind still works
        Assert.Null(results[0].CommitsBehind); // No GitHub client, so null
    }

    [Fact]
    public async Task GetSubscriptionHealth_VmrSubscription_UpToDate_CommitsBehindIsNull()
    {
        // Arrange: VMR subscription that is NOT stale (current)
        var channel = CreateChannel(1, ".NET 10");
        var currentBuild = CreateBuild(id: 100, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "abc123");
        
        var sub = CreateSubscription(
            source: "https://github.com/dotnet/dotnet",
            target: "https://github.com/dotnet/aspnetcore",
            channel: channel,
            lastApplied: currentBuild);

        var mockGitHub = Substitute.For<IGitHubApiClient>();
        var serviceWithGitHub = new MaestroService(_client, _cache, mockGitHub);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/aspnetcore", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns(currentBuild); // Same build = not stale

        // Act
        var results = await serviceWithGitHub.GetSubscriptionHealthAsync("https://github.com/dotnet/aspnetcore");

        // Assert
        Assert.Single(results);
        Assert.False(results[0].IsStale); // Not stale
        Assert.Equal(0, results[0].BuildsBehind);
        Assert.Null(results[0].CommitsBehind); // Not computed for up-to-date subscriptions
        
        // Verify GitHub client was never called (only called when stale)
        await mockGitHub.DidNotReceive().CompareCommitsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubscriptionHealth_FetchesFullBuildWhenLastAppliedCommitIsNull()
    {
        // Arrange - LastAppliedBuild has null Commit SHA
        var channel = CreateChannel(1, ".NET 10");
        var lastAppliedBuild = CreateBuild(id: 90, gitHubRepo: "https://github.com/dotnet/dotnet", commit: ""); // Empty commit
        var latestBuild = CreateBuild(id: 95, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "def456");
        var sub = CreateSubscription(
            source: "https://github.com/dotnet/dotnet",
            target: "https://github.com/dotnet/aspnetcore",
            channel: channel,
            lastApplied: lastAppliedBuild);

        // Mock full build with commit SHA
        var fullLastAppliedBuild = CreateBuild(id: 90, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "abc123");
        
        var mockGitHub = Substitute.For<IGitHubApiClient>();
        mockGitHub.CompareCommitsAsync("dotnet", "dotnet", "abc123", "def456", Arg.Any<CancellationToken>())
            .Returns(new GitHubCompareResult(AheadBy: 33, BehindBy: 0, Status: "ahead", TotalCommits: 33));

        var serviceWithGitHub = new MaestroService(_client, _cache, mockGitHub);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/aspnetcore", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns(latestBuild);
        _client.GetBuildAsync(90, Arg.Any<CancellationToken>())
            .Returns(fullLastAppliedBuild); // Return full build with commit

        // Act
        var results = await serviceWithGitHub.GetSubscriptionHealthAsync("https://github.com/dotnet/aspnetcore");

        // Assert
        Assert.Single(results);
        Assert.True(results[0].IsStale);
        Assert.Equal(5, results[0].BuildsBehind);
        Assert.Equal(33, results[0].CommitsBehind); // Should have commit distance

        // Verify GetBuildAsync was called to fetch full build
        await _client.Received(1).GetBuildAsync(90, Arg.Any<CancellationToken>());
        await mockGitHub.Received(1).CompareCommitsAsync("dotnet", "dotnet", "abc123", "def456", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubscriptionHealth_FetchesFullBuildWhenLatestBuildCommitIsNull()
    {
        // Arrange - LatestBuild has null Commit SHA
        var channel = CreateChannel(1, ".NET 10");
        var lastAppliedBuild = CreateBuild(id: 90, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "abc123");
        var latestBuild = CreateBuild(id: 95, gitHubRepo: "https://github.com/dotnet/dotnet", commit: ""); // Empty commit
        var sub = CreateSubscription(
            source: "https://github.com/dotnet/dotnet",
            target: "https://github.com/dotnet/aspnetcore",
            channel: channel,
            lastApplied: lastAppliedBuild);

        // Mock full build with commit SHA
        var fullLatestBuild = CreateBuild(id: 95, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "def456");
        
        var mockGitHub = Substitute.For<IGitHubApiClient>();
        mockGitHub.CompareCommitsAsync("dotnet", "dotnet", "abc123", "def456", Arg.Any<CancellationToken>())
            .Returns(new GitHubCompareResult(AheadBy: 33, BehindBy: 0, Status: "ahead", TotalCommits: 33));

        var serviceWithGitHub = new MaestroService(_client, _cache, mockGitHub);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/aspnetcore", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns(latestBuild);
        _client.GetBuildAsync(95, Arg.Any<CancellationToken>())
            .Returns(fullLatestBuild); // Return full build with commit

        // Act
        var results = await serviceWithGitHub.GetSubscriptionHealthAsync("https://github.com/dotnet/aspnetcore");

        // Assert
        Assert.Single(results);
        Assert.True(results[0].IsStale);
        Assert.Equal(5, results[0].BuildsBehind);
        Assert.Equal(33, results[0].CommitsBehind); // Should have commit distance

        // Verify GetBuildAsync was called to fetch full build
        await _client.Received(1).GetBuildAsync(95, Arg.Any<CancellationToken>());
        await mockGitHub.Received(1).CompareCommitsAsync("dotnet", "dotnet", "abc123", "def456", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubscriptionHealth_FallsBackToBuildsBehindWhenBothCommitsAreNull()
    {
        // Arrange - Both builds have null commits, even after fetching full builds
        var channel = CreateChannel(1, ".NET 10");
        var lastAppliedBuild = CreateBuild(id: 90, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "");
        var latestBuild = CreateBuild(id: 95, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "");
        var sub = CreateSubscription(
            source: "https://github.com/dotnet/dotnet",
            target: "https://github.com/dotnet/aspnetcore",
            channel: channel,
            lastApplied: lastAppliedBuild);

        // Mock full builds that also have empty commits (edge case)
        var fullLastAppliedBuild = CreateBuild(id: 90, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "");
        var fullLatestBuild = CreateBuild(id: 95, gitHubRepo: "https://github.com/dotnet/dotnet", commit: "");
        
        var mockGitHub = Substitute.For<IGitHubApiClient>();
        var serviceWithGitHub = new MaestroService(_client, _cache, mockGitHub);

        _client.ListSubscriptionsAsync(null, "https://github.com/dotnet/aspnetcore", null, true, Arg.Any<CancellationToken>())
            .Returns(new List<Subscription> { sub });
        _client.GetLatestBuildAsync(sub.SourceRepository, channel.Id, Arg.Any<CancellationToken>())
            .Returns(latestBuild);
        _client.GetBuildAsync(90, Arg.Any<CancellationToken>())
            .Returns(fullLastAppliedBuild);
        _client.GetBuildAsync(95, Arg.Any<CancellationToken>())
            .Returns(fullLatestBuild);

        // Act
        var results = await serviceWithGitHub.GetSubscriptionHealthAsync("https://github.com/dotnet/aspnetcore");

        // Assert
        Assert.Single(results);
        Assert.True(results[0].IsStale);
        Assert.Equal(5, results[0].BuildsBehind); // Falls back to build IDs
        Assert.Null(results[0].CommitsBehind); // No commit distance available

        // Verify both GetBuildAsync calls were made
        await _client.Received(1).GetBuildAsync(90, Arg.Any<CancellationToken>());
        await _client.Received(1).GetBuildAsync(95, Arg.Any<CancellationToken>());
        
        // Verify GitHub compare was never called (no commits available)
        await mockGitHub.DidNotReceive().CompareCommitsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GitHubCompareResult_RecordEquality()
    {
        // Test that the record works correctly
        var result1 = new GitHubCompareResult(AheadBy: 10, BehindBy: 5, Status: "ahead", TotalCommits: 10);
        var result2 = new GitHubCompareResult(AheadBy: 10, BehindBy: 5, Status: "ahead", TotalCommits: 10);
        var result3 = new GitHubCompareResult(AheadBy: 15, BehindBy: 5, Status: "ahead", TotalCommits: 15);

        Assert.Equal(result1, result2);
        Assert.NotEqual(result1, result3);
        Assert.Equal(10, result1.AheadBy);
        Assert.Equal(5, result1.BehindBy);
    }

    [Fact]
    public void SubscriptionHealthResult_CommitsBehind_DefaultsToNull()
    {
        // Test that existing record instantiation without CommitsBehind still works
        var result = new SubscriptionHealthResult(
            SubscriptionId: Guid.NewGuid(),
            SourceRepository: "https://github.com/dotnet/runtime",
            TargetRepository: "https://github.com/dotnet/dotnet",
            TargetBranch: "main",
            ChannelName: ".NET 10",
            IsStale: true,
            BuildsBehind: 5,
            LastAppliedBuildId: 100,
            LastAppliedDate: DateTimeOffset.UtcNow,
            LatestBuildId: 105,
            LatestBuildDate: DateTimeOffset.UtcNow
        );

        Assert.Null(result.CommitsBehind); // Defaults to null when not specified
        Assert.Null(result.Error); // Error also defaults to null
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
        _client.TriggerSubscriptionAsync(subId, 42, false, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _service.TriggerSubscriptionAsync(subId, 42);

        Assert.Equal(expected.Id, result.Id);
        await _client.Received(1).TriggerSubscriptionAsync(subId, 42, false, Arg.Any<CancellationToken>());
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
        _client.TriggerSubscriptionAsync(subId, 1, false, Arg.Any<CancellationToken>())
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
    // Trigger method: force parameter tests
    // ================================================================

    [Fact]
    public async Task TriggerSubscription_WithForce_PassesForceThroughToClient()
    {
        var subId = Guid.NewGuid();
        var expected = CreateSubscription(id: subId);
        _client.TriggerSubscriptionAsync(subId, 42, true, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _service.TriggerSubscriptionAsync(subId, 42, force: true);

        Assert.Equal(expected.Id, result.Id);
        await _client.Received(1).TriggerSubscriptionAsync(subId, 42, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerSubscription_WithForce_InvalidatesCaches()
    {
        var subId = Guid.NewGuid();
        var sub = CreateSubscription(id: subId);
        var subs = new List<Subscription> { sub };

        _client.ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>())
            .Returns(subs);
        _client.GetSubscriptionAsync(subId, Arg.Any<CancellationToken>())
            .Returns(sub);
        _client.TriggerSubscriptionAsync(subId, 1, true, Arg.Any<CancellationToken>())
            .Returns(sub);

        // Populate caches
        await _service.GetSubscriptionsAsync();
        await _service.GetSubscriptionAsync(subId);

        // Trigger with force should invalidate
        await _service.TriggerSubscriptionAsync(subId, 1, force: true);

        // Next reads should hit API again
        await _service.GetSubscriptionsAsync();
        await _service.GetSubscriptionAsync(subId);

        await _client.Received(2).ListSubscriptionsAsync(null, null, null, true, Arg.Any<CancellationToken>());
        await _client.Received(2).GetSubscriptionAsync(subId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerSubscription_DefaultForceIsFalse()
    {
        var subId = Guid.NewGuid();
        var expected = CreateSubscription(id: subId);
        _client.TriggerSubscriptionAsync(subId, 42, false, Arg.Any<CancellationToken>())
            .Returns(expected);

        // Call without explicit force parameter
        var result = await _service.TriggerSubscriptionAsync(subId, 42);

        Assert.Equal(expected.Id, result.Id);
        await _client.Received(1).TriggerSubscriptionAsync(subId, 42, false, Arg.Any<CancellationToken>());
        await _client.Received(0).TriggerSubscriptionAsync(subId, 42, true, Arg.Any<CancellationToken>());
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
        _client.TriggerSubscriptionAsync(subId, 42, false, Arg.Any<CancellationToken>())
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

    // ================================================================
    // Codeflow PR Tracking (v0.4.0)
    // ================================================================

    private static TrackedPullRequest CreateTrackedPullRequest(string url = "https://github.com/dotnet/dotnet/pull/1234")
    {
        var tpr = new TrackedPullRequest(sourceEnabled: true, lastUpdate: DateTimeOffset.UtcNow, lastCheck: DateTimeOffset.UtcNow)
        {
            Url = url,
            TargetBranch = "main",
            HeadBranch = "darc-main-abc123"
        };
        return tpr;
    }

    [Fact]
    public async Task GetTrackedPullRequests_ReturnsList()
    {
        var pr1 = CreateTrackedPullRequest("https://github.com/dotnet/dotnet/pull/1001");
        var pr2 = CreateTrackedPullRequest("https://github.com/dotnet/dotnet/pull/1002");
        var expected = new List<TrackedPullRequest> { pr1, pr2 };

        _client.GetTrackedPullRequestsAsync(Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _service.GetTrackedPullRequestsAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("https://github.com/dotnet/dotnet/pull/1001", result[0].Url);
        Assert.Equal("https://github.com/dotnet/dotnet/pull/1002", result[1].Url);
    }

    [Fact]
    public async Task GetTrackedPullRequests_EmptyList()
    {
        _client.GetTrackedPullRequestsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TrackedPullRequest>());

        var result = await _service.GetTrackedPullRequestsAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTrackedPullRequests_CachesResult()
    {
        var expected = new List<TrackedPullRequest> { CreateTrackedPullRequest() };
        _client.GetTrackedPullRequestsAsync(Arg.Any<CancellationToken>())
            .Returns(expected);

        var first = await _service.GetTrackedPullRequestsAsync();
        var second = await _service.GetTrackedPullRequestsAsync();

        Assert.Equal(first[0].Url, second[0].Url);
        await _client.Received(1).GetTrackedPullRequestsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTrackedPullRequestBySubscriptionId_ReturnsTrackedPR()
    {
        var subId = Guid.NewGuid().ToString();
        var expected = CreateTrackedPullRequest("https://github.com/dotnet/dotnet/pull/5555");

        _client.GetTrackedPullRequestBySubscriptionIdAsync(subId, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _service.GetTrackedPullRequestBySubscriptionIdAsync(subId);

        Assert.Equal("https://github.com/dotnet/dotnet/pull/5555", result.Url);
    }

    [Fact]
    public async Task GetTrackedPullRequestBySubscriptionId_CachesResult()
    {
        var subId = Guid.NewGuid().ToString();
        var expected = CreateTrackedPullRequest();

        _client.GetTrackedPullRequestBySubscriptionIdAsync(subId, Arg.Any<CancellationToken>())
            .Returns(expected);

        var first = await _service.GetTrackedPullRequestBySubscriptionIdAsync(subId);
        var second = await _service.GetTrackedPullRequestBySubscriptionIdAsync(subId);

        Assert.Equal(first.Url, second.Url);
        await _client.Received(1).GetTrackedPullRequestBySubscriptionIdAsync(subId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTrackedPullRequestBySubscriptionId_NoCacheBypassesCache()
    {
        var subId = Guid.NewGuid().ToString();
        var first = CreateTrackedPullRequest("https://github.com/dotnet/dotnet/pull/1001");
        var second = CreateTrackedPullRequest("https://github.com/dotnet/dotnet/pull/1002");

        _client.GetTrackedPullRequestBySubscriptionIdAsync(subId, Arg.Any<CancellationToken>())
            .Returns(first, second);

        var result1 = await _service.GetTrackedPullRequestBySubscriptionIdAsync(subId);
        var result2 = await _service.GetTrackedPullRequestBySubscriptionIdAsync(subId, noCache: true);

        Assert.Equal("https://github.com/dotnet/dotnet/pull/1001", result1.Url);
        Assert.Equal("https://github.com/dotnet/dotnet/pull/1002", result2.Url);
        await _client.Received(2).GetTrackedPullRequestBySubscriptionIdAsync(subId, Arg.Any<CancellationToken>());
    }

    // ================================================================
    // Backflow Status (v0.4.0)
    // ================================================================

    [Fact]
    public async Task GetBackflowStatus_ReturnsStatus()
    {
        var expected = new BackflowStatus(
            vmrCommitSha: "abc123",
            computationTimestamp: DateTimeOffset.UtcNow,
            branchStatuses: System.Collections.Immutable.ImmutableDictionary<string, BranchBackflowStatus>.Empty);

        _client.GetBackflowStatusAsync(42, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _service.GetBackflowStatusAsync(42);

        Assert.Equal("abc123", result.VmrCommitSha);
    }

    // ================================================================
    // Subscription History (v0.4.0)
    // ================================================================

    [Fact]
    public async Task GetSubscriptionHistory_ReturnsList()
    {
        var subId = Guid.NewGuid();
        var items = new List<SubscriptionHistoryItem>
        {
            new(DateTimeOffset.UtcNow, success: true, subscriptionId: subId, errorMessage: "", action: "UpdateAssets", retryUrl: ""),
            new(DateTimeOffset.UtcNow.AddMinutes(-5), success: false, subscriptionId: subId, errorMessage: "timeout", action: "UpdateAssets", retryUrl: "https://retry")
        };

        _client.GetSubscriptionHistoryAsync(subId, null, null, Arg.Any<CancellationToken>())
            .Returns(items);

        var result = await _service.GetSubscriptionHistoryAsync(subId);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].Success);
        Assert.False(result[1].Success);
        Assert.Equal("timeout", result[1].ErrorMessage);
    }

    // ================================================================
    // GetBuildGraphAsync
    // ================================================================

    [Fact]
    public async Task GetBuildGraph_ReturnsFromApi()
    {
        var build1 = CreateBuild(101, "https://github.com/dotnet/runtime");
        var build2 = CreateBuild(102, "https://github.com/dotnet/aspnetcore");
        var expected = CreateBuildGraph(build1, build2);

        _client.GetBuildGraphAsync(101, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _service.GetBuildGraphAsync(101);

        Assert.NotNull(result);
        Assert.Equal(2, result.Builds.Count);
        Assert.True(result.Builds.ContainsKey("101"));
        Assert.True(result.Builds.ContainsKey("102"));
    }

    [Fact]
    public async Task GetBuildGraph_SecondCallReturnsCached()
    {
        var build = CreateBuild(101);
        var graph = CreateBuildGraph(build);

        _client.GetBuildGraphAsync(101, Arg.Any<CancellationToken>())
            .Returns(graph);

        var first = await _service.GetBuildGraphAsync(101);
        var second = await _service.GetBuildGraphAsync(101);

        Assert.Equal(first.Builds.Count, second.Builds.Count);
        await _client.Received(1).GetBuildGraphAsync(101, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBuildGraph_NoCacheBypassesCache()
    {
        var build = CreateBuild(101);
        var graph = CreateBuildGraph(build);

        _client.GetBuildGraphAsync(101, Arg.Any<CancellationToken>())
            .Returns(graph);

        var first = await _service.GetBuildGraphAsync(101);
        var second = await _service.GetBuildGraphAsync(101, noCache: true);

        Assert.NotNull(first);
        Assert.NotNull(second);
        await _client.Received(2).GetBuildGraphAsync(101, Arg.Any<CancellationToken>());
    }

    // ================================================================
    // GetFlowGraphAsync
    // ================================================================

    [Fact]
    public async Task GetFlowGraph_ReturnsFromApi()
    {
        var ref1 = CreateFlowRef("ref1", "https://github.com/dotnet/runtime", "main");
        var ref2 = CreateFlowRef("ref2", "https://github.com/dotnet/aspnetcore", "main");
        var edge1 = CreateFlowEdge("ref1", "ref2", ".NET 10");
        var expected = CreateFlowGraph(
            refs: new List<FlowRef> { ref1, ref2 },
            edges: new List<FlowEdge> { edge1 });

        _client.GetFlowGraphAsync(7, 1, true, true, false, null, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _service.GetFlowGraphAsync(7, 1);

        Assert.NotNull(result);
        Assert.Equal(2, result.FlowRefs.Count);
        Assert.Single(result.FlowEdges);
        Assert.Equal("ref1", result.FlowRefs[0].Id);
        Assert.Equal("ref2", result.FlowRefs[1].Id);
    }

    [Fact]
    public async Task GetFlowGraph_SecondCallReturnsCached()
    {
        var ref1 = CreateFlowRef();
        var edge1 = CreateFlowEdge();
        var graph = CreateFlowGraph(
            refs: new List<FlowRef> { ref1 },
            edges: new List<FlowEdge> { edge1 });

        _client.GetFlowGraphAsync(7, 1, true, true, false, null, Arg.Any<CancellationToken>())
            .Returns(graph);

        var first = await _service.GetFlowGraphAsync(7, 1);
        var second = await _service.GetFlowGraphAsync(7, 1);

        Assert.Equal(first.FlowRefs.Count, second.FlowRefs.Count);
        await _client.Received(1).GetFlowGraphAsync(7, 1, true, true, false, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFlowGraph_DifferentChannelIdUsesSeperateCache()
    {
        var ref1 = CreateFlowRef("ref1");
        var ref2 = CreateFlowRef("ref2");
        var graph1 = CreateFlowGraph(refs: new List<FlowRef> { ref1 });
        var graph2 = CreateFlowGraph(refs: new List<FlowRef> { ref2 });

        _client.GetFlowGraphAsync(7, 1, true, true, false, null, Arg.Any<CancellationToken>())
            .Returns(graph1);
        _client.GetFlowGraphAsync(7, 2, true, true, false, null, Arg.Any<CancellationToken>())
            .Returns(graph2);

        var result1 = await _service.GetFlowGraphAsync(7, 1);
        var result2 = await _service.GetFlowGraphAsync(7, 2);

        Assert.Equal("ref1", result1.FlowRefs[0].Id);
        Assert.Equal("ref2", result2.FlowRefs[0].Id);
        await _client.Received(1).GetFlowGraphAsync(7, 1, true, true, false, null, Arg.Any<CancellationToken>());
        await _client.Received(1).GetFlowGraphAsync(7, 2, true, true, false, null, Arg.Any<CancellationToken>());
    }
}
