using System.ComponentModel;
using MaestroTool.Core;
using Microsoft.DotNet.ProductConstructionService.Client.Models;
using NSubstitute;
using Xunit;

namespace MaestroTool.Tests;

/// <summary>
/// Tests for MaestroMcpTools - the MCP tool layer that handles parameter validation,
/// resolution logic, and formatting before delegating to MaestroService.
/// </summary>
public class MaestroMcpToolsTests : IDisposable
{
    private readonly IMaestroApiClient _client;
    private readonly CacheService _cache;
    private readonly MaestroService _service;
    private readonly MaestroMcpTools _tools;
    private readonly string _dbPath;

    public MaestroMcpToolsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mstro-test-{Guid.NewGuid()}.db");
        _client = Substitute.For<IMaestroApiClient>();
        _cache = new CacheService(_dbPath);
        _service = new MaestroService(_client, _cache);
        _tools = new MaestroMcpTools(_service, new MaestroToolOptions(), _cache);
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

    private static Build CreateBuild(
        int id = 100,
        string? gitHubRepo = null,
        DateTimeOffset? date = null,
        string? commit = null) =>
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

    // ================================================================
    // Channel name-or-ID resolution tests
    // ================================================================

    [Fact]
    public async Task GetChannel_WithIntegerString_ResolvesToChannelId()
    {
        // Arrange
        var expectedChannel = CreateChannel(id: 42, name: "Test Channel");
        _client.GetChannelAsync(42, Arg.Any<CancellationToken>())
            .Returns(expectedChannel);

        // Act
        var result = await _tools.GetChannel("42");

        // Assert
        Assert.Contains("Test Channel", result);
        Assert.Contains("ID: 42", result);
        await _client.Received(1).GetChannelAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetChannel_WithChannelName_ResolvesToChannelByName()
    {
        // Arrange
        var channel1 = CreateChannel(id: 1, name: "Other Channel");
        var channel2 = CreateChannel(id: 2, name: ".NET 10.0.1xx SDK");
        var channels = new List<Channel> { channel1, channel2 };

        _client.ListChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(channels);

        // Act
        var result = await _tools.GetChannel(".NET 10.0.1xx SDK");

        // Assert
        Assert.Contains(".NET 10.0.1xx SDK", result);
        Assert.Contains("ID: 2", result);
        await _client.Received(1).ListChannelsAsync(Arg.Any<CancellationToken>());
        await _client.DidNotReceive().GetChannelAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetChannel_WithInvalidChannelName_ReturnsNotFound()
    {
        // Arrange
        var channels = new List<Channel>
        {
            CreateChannel(id: 1, name: "Channel One"),
            CreateChannel(id: 2, name: "Channel Two")
        };

        _client.ListChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(channels);

        // Act
        var result = await _tools.GetChannel("Nonexistent Channel");

        // Assert
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task GetChannel_WithEmptyOrNullInput_ReturnsValidationError(string? input)
    {
        // Act
        var result = await _tools.GetChannel(input!);

        // Assert
        Assert.Contains("required", result, StringComparison.OrdinalIgnoreCase);
        await _client.DidNotReceive().GetChannelAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _client.DidNotReceive().ListChannelsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetChannel_WithChannelNameCaseInsensitive_FindsMatch()
    {
        // Arrange
        var channel = CreateChannel(id: 5, name: ".NET 10.0.1xx SDK");
        var channels = new List<Channel> { channel };

        _client.ListChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(channels);

        // Act - use different casing
        var result = await _tools.GetChannel(".net 10.0.1xx sdk");

        // Assert
        Assert.Contains(".NET 10.0.1xx SDK", result);
        Assert.Contains("ID: 5", result);
    }

    [Fact]
    public async Task GetChannel_WithZeroId_TreatsAsIntegerId()
    {
        // Arrange
        var expectedChannel = CreateChannel(id: 0, name: "Zero Channel");
        _client.GetChannelAsync(0, Arg.Any<CancellationToken>())
            .Returns(expectedChannel);

        // Act
        var result = await _tools.GetChannel("0");

        // Assert
        Assert.Contains("Zero Channel", result);
        Assert.Contains("ID: 0", result);
        await _client.Received(1).GetChannelAsync(0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetChannel_WithNegativeNumber_ReturnsError()
    {
        // Act
        var result = await _tools.GetChannel("-1");

        // Assert
        Assert.Contains("invalid", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetChannels_WithFilter_ReturnsOnlyMatchingChannels()
    {
        var channels = new List<Channel>
        {
            CreateChannel(id: 1, name: ".NET 10.0.1xx SDK"),
            CreateChannel(id: 2, name: ".NET 9.0.1xx SDK"),
            CreateChannel(id: 3, name: "VS 17.14")
        };
        _client.ListChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(channels);

        var result = await _tools.GetChannels(filter: "net 10");

        Assert.Contains(".NET 10.0.1xx SDK", result);
        Assert.DoesNotContain(".NET 9.0.1xx SDK", result);
        Assert.DoesNotContain("VS 17.14", result);
    }

    [Fact]
    public async Task GetChannels_WithClassification_PassesThroughToService()
    {
        var channels = new List<Channel> { CreateChannel(id: 1, name: ".NET 10.0.1xx SDK") };
        _client.ListChannelsAsync(Arg.Any<CancellationToken>(), "product")
            .Returns(channels);

        var result = await _tools.GetChannels(classification: "product");

        Assert.Contains(".NET 10.0.1xx SDK", result);
        await _client.Received(1).ListChannelsAsync(Arg.Any<CancellationToken>(), "product");
    }

    [Fact]
    public async Task GetChannels_WithCompact_ReturnsNameToIdLines()
    {
        var channels = new List<Channel>
        {
            CreateChannel(id: 10, name: ".NET 10.0.1xx SDK"),
            CreateChannel(id: 20, name: "VS 17.14")
        };
        _client.ListChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(channels);

        var result = await _tools.GetChannels(compact: true);

        Assert.Contains(".NET 10.0.1xx SDK → 10", result);
        Assert.Contains("VS 17.14 → 20", result);
        Assert.DoesNotContain("- **", result);
    }

    // ================================================================
    // Smart trigger_subscription - auto-resolve latest build
    // ================================================================

    [Fact]
    public async Task TriggerSubscription_WithExplicitBuildId_UsesItDirectly()
    {
        // Arrange
        var subId = Guid.NewGuid();
        var subscription = CreateSubscription(id: subId);
        
        _client.TriggerSubscriptionAsync(subId, 100, false, Arg.Any<CancellationToken>())
            .Returns(subscription);

        // Act
        var result = await _tools.TriggerSubscription(subId.ToString(), 100);

        // Assert
        Assert.Contains("Successfully triggered", result);
        Assert.Contains($"build #{100}", result);
        await _client.Received(1).TriggerSubscriptionAsync(subId, 100, false, Arg.Any<CancellationToken>());
        await _client.DidNotReceive().GetLatestBuildAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerSubscription_WithNullBuildId_AndSourceRepoAndChannel_ResolvesLatestBuild()
    {
        // Arrange
        var subId = Guid.NewGuid();
        var channel = CreateChannel(id: 5, name: ".NET 10.0.1xx SDK");
        var latestBuild = CreateBuild(id: 200, gitHubRepo: "https://github.com/dotnet/runtime");
        var subscription = CreateSubscription(id: subId, channel: channel);

        var channels = new List<Channel> { channel };
        _client.ListChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(channels);

        _client.GetLatestBuildAsync(
            "https://github.com/dotnet/runtime",
            5,
            Arg.Any<CancellationToken>())
            .Returns(latestBuild);

        _client.TriggerSubscriptionAsync(subId, 200, false, Arg.Any<CancellationToken>())
            .Returns(subscription);

        // Act
        var result = await _tools.TriggerSubscription(
            subId.ToString(),
            buildId: null,
            sourceRepository: "https://github.com/dotnet/runtime",
            channelName: ".NET 10.0.1xx SDK");

        // Assert
        Assert.Contains("Successfully triggered", result);
        Assert.Contains($"build #{200}", result);
        await _client.Received(1).GetLatestBuildAsync(
            "https://github.com/dotnet/runtime",
            5,
            Arg.Any<CancellationToken>());
        await _client.Received(1).TriggerSubscriptionAsync(subId, 200, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerSubscription_WithNullBuildId_MissingSourceRepository_ReturnsValidationError()
    {
        // Arrange
        var subId = Guid.NewGuid();

        // Act
        var result = await _tools.TriggerSubscription(
            subId.ToString(),
            buildId: null,
            channelName: ".NET 10.0.1xx SDK");

        // Assert
        Assert.Contains("sourceRepository", result);
        Assert.Contains("required", result, StringComparison.OrdinalIgnoreCase);
        await _client.DidNotReceive().TriggerSubscriptionAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerSubscription_WithNullBuildId_MissingChannelName_ReturnsValidationError()
    {
        // Arrange
        var subId = Guid.NewGuid();

        // Act
        var result = await _tools.TriggerSubscription(
            subId.ToString(),
            buildId: null,
            sourceRepository: "https://github.com/dotnet/runtime");

        // Assert
        Assert.Contains("channelName", result);
        Assert.Contains("required", result, StringComparison.OrdinalIgnoreCase);
        await _client.DidNotReceive().TriggerSubscriptionAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerSubscription_WithNullBuildId_NoLatestBuild_ReturnsAppropriateError()
    {
        // Arrange
        var subId = Guid.NewGuid();
        var channel = CreateChannel(id: 5, name: ".NET 10.0.1xx SDK");
        var channels = new List<Channel> { channel };

        _client.ListChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(channels);

        _client.GetLatestBuildAsync(
            "https://github.com/dotnet/nonexistent",
            5,
            Arg.Any<CancellationToken>())
            .Returns((Build?)null);

        // Act
        var result = await _tools.TriggerSubscription(
            subId.ToString(),
            buildId: null,
            sourceRepository: "https://github.com/dotnet/nonexistent",
            channelName: ".NET 10.0.1xx SDK");

        // Assert
        Assert.Contains("No build found", result, StringComparison.OrdinalIgnoreCase);
        await _client.DidNotReceive().TriggerSubscriptionAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerSubscription_WithNullBuildId_InvalidChannelName_ReturnsChannelNotFound()
    {
        // Arrange
        var subId = Guid.NewGuid();
        var channels = new List<Channel> { CreateChannel(id: 1, name: "Valid Channel") };

        _client.ListChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(channels);

        // Act
        var result = await _tools.TriggerSubscription(
            subId.ToString(),
            buildId: null,
            sourceRepository: "https://github.com/dotnet/runtime",
            channelName: "Invalid Channel Name");

        // Assert
        Assert.Contains("Channel", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
        await _client.DidNotReceive().GetLatestBuildAsync(
            Arg.Any<string>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerSubscription_WithForceFlag_AndAutoResolve_PassesForceFlagCorrectly()
    {
        // Arrange
        var subId = Guid.NewGuid();
        var channel = CreateChannel(id: 5, name: ".NET 10.0.1xx SDK");
        var latestBuild = CreateBuild(id: 300);
        var subscription = CreateSubscription(id: subId);

        var channels = new List<Channel> { channel };
        _client.ListChannelsAsync(Arg.Any<CancellationToken>())
            .Returns(channels);

        _client.GetLatestBuildAsync(
            "https://github.com/dotnet/runtime",
            5,
            Arg.Any<CancellationToken>())
            .Returns(latestBuild);

        _client.TriggerSubscriptionAsync(subId, 300, true, Arg.Any<CancellationToken>())
            .Returns(subscription);

        // Act
        var result = await _tools.TriggerSubscription(
            subId.ToString(),
            buildId: null,
            sourceRepository: "https://github.com/dotnet/runtime",
            channelName: ".NET 10.0.1xx SDK",
            force: true);

        // Assert
        Assert.Contains("force", result, StringComparison.OrdinalIgnoreCase);
        await _client.Received(1).TriggerSubscriptionAsync(subId, 300, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerSubscription_InvalidSubscriptionId_ReturnsValidationError()
    {
        // Act
        var result = await _tools.TriggerSubscription("not-a-guid", 100);

        // Assert
        Assert.Contains("Invalid subscription ID", result);
        await _client.DidNotReceive().TriggerSubscriptionAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerSubscription_BackwardCompatibility_ExplicitBuildIdStillWorks()
    {
        // This test ensures that the original behavior (passing buildId explicitly)
        // continues to work even after adding the auto-resolve feature
        
        // Arrange
        var subId = Guid.NewGuid();
        var subscription = CreateSubscription(id: subId);
        
        _client.TriggerSubscriptionAsync(subId, 999, false, Arg.Any<CancellationToken>())
            .Returns(subscription);

        // Act - call with explicit buildId, ignoring optional params
        var result = await _tools.TriggerSubscription(
            subId.ToString(),
            buildId: 999,
            sourceRepository: null,
            channelName: null);

        // Assert
        Assert.Contains("Successfully triggered", result);
        Assert.Contains("build #999", result);
        await _client.Received(1).TriggerSubscriptionAsync(subId, 999, false, Arg.Any<CancellationToken>());
        await _client.DidNotReceive().GetLatestBuildAsync(
            Arg.Any<string>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }
}
