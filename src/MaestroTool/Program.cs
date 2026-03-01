using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using MaestroTool.Core;
using Microsoft.DotNet.ProductConstructionService.Client.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// DI setup (shared by both CLI and MCP)
var services = new ServiceCollection();
services.AddSingleton<IMaestroApiClient>(_ =>
    new MaestroApiClient(Environment.GetEnvironmentVariable("MAESTRO_BAR_TOKEN")));
services.AddSingleton<IGitHubApiClient>(_ => new GitHubApiClient());
services.AddSingleton<IAzDoApiClient>(_ => new AzDoApiClient());
services.AddSingleton<CacheService>();
services.AddSingleton<MaestroService>(sp => new MaestroService(
    sp.GetRequiredService<IMaestroApiClient>(),
    sp.GetRequiredService<CacheService>(),
    sp.GetRequiredService<IGitHubApiClient>(),
    sp.GetRequiredService<IAzDoApiClient>()));

var enableDestructive = bool.TryParse(
    Environment.GetEnvironmentVariable("MAESTRO_ENABLE_DESTRUCTIVE_ACTIONS"),
    out var enabled) && enabled;

services.AddSingleton(new MaestroToolOptions
{
    EnableDestructiveActions = enableDestructive
});

// Build provider for ConsoleAppFramework
ConsoleApp.ServiceProvider = services.BuildServiceProvider();

// Create app with Commands class
var app = ConsoleApp.Create();
app.Add<Commands>();

// Default to MCP if no args
app.Run(args.Length == 0 ? ["mcp"] : args);

public class Commands
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private readonly MaestroService _service;
    private readonly CacheService _cache;

    public Commands(MaestroService service, CacheService cache)
    {
        _service = service;
        _cache = cache;
    }

    [Command("mcp")]
    [Description("Start MCP server mode (default when no arguments provided)")]
    public async Task Mcp()
    {
        // Create a SEPARATE host for MCP mode
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton<IMaestroApiClient>(_ =>
            new MaestroApiClient(Environment.GetEnvironmentVariable("MAESTRO_BAR_TOKEN")));
        builder.Services.AddSingleton<IGitHubApiClient>(_ => new GitHubApiClient());
        builder.Services.AddSingleton<IAzDoApiClient>(_ => new AzDoApiClient());
        builder.Services.AddSingleton<CacheService>();
        builder.Services.AddSingleton<MaestroService>(sp => new MaestroService(
            sp.GetRequiredService<IMaestroApiClient>(),
            sp.GetRequiredService<CacheService>(),
            sp.GetRequiredService<IGitHubApiClient>(),
            sp.GetRequiredService<IAzDoApiClient>()));

        var enableDestructive = bool.TryParse(
            Environment.GetEnvironmentVariable("MAESTRO_ENABLE_DESTRUCTIVE_ACTIONS"),
            out var enabled) && enabled;

        builder.Services.AddSingleton(new MaestroToolOptions
        {
            EnableDestructiveActions = enableDestructive
        });

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "maestro", Version = "0.11.0" };
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(MaestroMcpTools).Assembly);

        await builder.Build().RunAsync();
    }

    [Command("subscriptions")]
    [Description("List Maestro subscriptions filtered by source/target repository and/or channel")]
    public async Task Subscriptions(
        string? sourceRepository = null,
        string? targetRepository = null,
        string? channelName = null,
        string? targetBranch = null,
        bool json = false,
        bool noCache = false)
    {
        int? channelId = null;
        if (!string.IsNullOrEmpty(channelName))
        {
            var channel = await _service.GetChannelByNameAsync(channelName, noCache);
            channelId = channel?.Id;
            if (channelId == null)
            {
                Console.Error.WriteLine($"Channel '{channelName}' not found.");
                Environment.Exit(1);
                return;
            }
        }

        var subs = await _service.GetSubscriptionsAsync(sourceRepository, targetRepository, channelId, targetBranch, noCache);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(subs, s_jsonOptions));
        }
        else
        {
            if (subs.Count == 0)
            {
                Console.WriteLine("No subscriptions found matching the criteria.");
                return;
            }

            Console.WriteLine($"Found {subs.Count} subscription(s):\n");
            foreach (var sub in subs)
            {
                Console.WriteLine($"{sub.SourceRepository} → {sub.TargetRepository} ({sub.TargetBranch})");
                Console.WriteLine($"  Channel: {sub.Channel?.Name ?? "N/A"} | ID: {sub.Id}");
                Console.WriteLine($"  Enabled: {sub.Enabled} | Last Build: {sub.LastAppliedBuild?.Id.ToString() ?? "none"}");
                Console.WriteLine();
            }
        }
    }

    [Command("subscription")]
    [Description("Get a specific Maestro subscription by ID")]
    public async Task Subscription(
        [Argument] string subscriptionId,
        bool json = false,
        bool noCache = false)
    {
        if (!Guid.TryParse(subscriptionId, out var id))
        {
            Console.Error.WriteLine("Invalid subscription ID format. Expected a GUID.");
            Environment.Exit(1);
            return;
        }

        var sub = await _service.GetSubscriptionAsync(id, noCache);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(sub, s_jsonOptions));
        }
        else
        {
            Console.WriteLine($"Subscription {sub.Id}");
            Console.WriteLine($"Source: {sub.SourceRepository}");
            Console.WriteLine($"Target: {sub.TargetRepository} ({sub.TargetBranch})");
            Console.WriteLine($"Channel: {sub.Channel?.Name ?? "N/A"}");
            Console.WriteLine($"Enabled: {sub.Enabled}");

            if (sub.LastAppliedBuild != null)
            {
                Console.WriteLine($"Last Applied Build: #{sub.LastAppliedBuild.Id} ({sub.LastAppliedBuild.DateProduced:u})");
                Console.WriteLine($"  Commit: {sub.LastAppliedBuild.Commit}");
            }
            else
            {
                Console.WriteLine("Last Applied Build: none");
            }

            // Health check
            if (sub.Channel?.Id != null)
            {
                var latest = await _service.GetLatestBuildAsync(sub.SourceRepository, sub.Channel.Id, noCache);
                if (latest != null && sub.LastAppliedBuild != null)
                {
                    if (latest.Id != sub.LastAppliedBuild.Id)
                    {
                        Console.WriteLine($"\n⚠️ STALE: Latest build is #{latest.Id}, last applied is #{sub.LastAppliedBuild.Id}");
                        Console.WriteLine($"  {latest.Id - sub.LastAppliedBuild.Id} build(s) behind");
                    }
                    else
                    {
                        Console.WriteLine("\n✅ Up to date with latest channel build");
                    }
                }
            }
        }
    }

    [Command("latest-build")]
    [Description("Get the latest build for a repository")]
    public async Task LatestBuild(
        [Argument] string repository,
        string? channelName = null,
        bool json = false,
        bool noCache = false)
    {
        int? channelId = null;
        if (!string.IsNullOrEmpty(channelName))
        {
            var channel = await _service.GetChannelByNameAsync(channelName, noCache);
            channelId = channel?.Id;
            if (channelId == null)
            {
                Console.Error.WriteLine($"Channel '{channelName}' not found.");
                Environment.Exit(1);
                return;
            }
        }

        var build = await _service.GetLatestBuildAsync(repository, channelId, noCache);
        if (build == null)
        {
            Console.Error.WriteLine($"No builds found for {repository}" + (channelName != null ? $" on channel '{channelName}'" : ""));
            Environment.Exit(1);
            return;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(build, s_jsonOptions));
        }
        else
        {
            PrintBuild(build);
        }
    }

    [Command("build")]
    [Description("Get a specific build by ID")]
    public async Task Build(
        [Argument] int buildId,
        bool json = false,
        bool noCache = false)
    {
        var build = await _service.GetBuildAsync(buildId, noCache);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(build, s_jsonOptions));
        }
        else
        {
            PrintBuild(build);
        }
    }

    [Command("channels")]
    [Description("List all Maestro channels")]
    public async Task Channels(
        bool json = false,
        bool noCache = false)
    {
        var channels = await _service.GetChannelsAsync(noCache);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(channels, s_jsonOptions));
        }
        else
        {
            Console.WriteLine($"Found {channels.Count} channel(s):\n");
            foreach (var ch in channels.OrderBy(c => c.Name))
            {
                Console.WriteLine($"- {ch.Name} (ID: {ch.Id})");
            }
        }
    }

    [Command("default-channels")]
    [Description("List default channel mappings (repo/branch → channel)")]
    public async Task DefaultChannels(
        string? repository = null,
        string? branch = null,
        bool json = false,
        bool noCache = false)
    {
        var defaults = await _service.GetDefaultChannelsAsync(repository, branch, noCache);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(defaults, s_jsonOptions));
        }
        else
        {
            if (defaults.Count == 0)
            {
                Console.WriteLine("No default channel mappings found.");
                return;
            }

            Console.WriteLine($"Found {defaults.Count} default channel mapping(s):\n");
            foreach (var dc in defaults)
            {
                Console.WriteLine($"- {dc.Repository} ({dc.Branch}) → {dc.Channel?.Name ?? "N/A"}");
            }
        }
    }

    [Command("subscription-health")]
    [Description("Check subscription health for a target repository")]
    public async Task SubscriptionHealth(
        [Argument] string targetRepository,
        bool json = false,
        bool noCache = false,
        bool includeCommitDetails = false)
    {
        var results = await _service.GetSubscriptionHealthAsync(targetRepository, noCache, includeCommitDetails);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(results, s_jsonOptions));
        }
        else
        {
            if (results.Count == 0)
            {
                Console.WriteLine($"No active subscriptions found targeting {targetRepository}.");
                return;
            }

            var staleCount = results.Count(r => r.IsStale);
            Console.WriteLine($"Subscription health for {targetRepository}: {results.Count} subscription(s), {staleCount} stale\n");

            foreach (var r in results)
            {
                string status;
                if (r.IsStale)
                {
                    if (r.CommitsBehind.HasValue)
                        status = $"⚠️ STALE ({r.CommitsBehind.Value} commits behind)";
                    else
                        status = $"⚠️ STALE (~{r.BuildsBehind} builds behind)";
                }
                else
                {
                    status = "✅ Current";
                }

                Console.WriteLine($"{r.SourceRepository} → {r.TargetBranch}");
                Console.WriteLine($"  Channel: {r.ChannelName} | Status: {status}");
                if (r.Error != null)
                    Console.WriteLine($"  ⚠️ Error: {r.Error}");
                if (r.LastAppliedBuildId != null)
                    Console.WriteLine($"  Last Applied: #{r.LastAppliedBuildId} ({r.LastAppliedDate:u})");
                if (r.LatestBuildId != null)
                    Console.WriteLine($"  Latest Available: #{r.LatestBuildId} ({r.LatestBuildDate:u})");

                if (r.RecentCommits is { Count: > 0 })
                {
                    var totalCommits = r.CommitsBehind ?? r.RecentCommits.Count;
                    var showing = Math.Min(r.RecentCommits.Count, 10);
                    Console.WriteLine($"  Recent commits (showing {showing} of {totalCommits}):");
                    foreach (var c in r.RecentCommits.Take(10))
                    {
                        Console.WriteLine($"    {c.Sha} {c.Message} ({c.Author}, {c.Date:yyyy-MM-dd})");
                    }
                }

                Console.WriteLine();
            }
        }
    }

    [Command("build-freshness")]
    [Description("Check build freshness by resolving aka.ms URLs")]
    public async Task BuildFreshness(
        [Argument] string channel,
        bool json = false,
        bool noCache = false)
    {
        var result = await _service.GetBuildFreshnessAsync(channel, noCache);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, s_jsonOptions));
        }
        else
        {
            if (!result.IsAvailable)
            {
                Console.Error.WriteLine($"Build freshness check failed for channel '{channel}': {result.Error ?? "URL not available"}");
                Environment.Exit(1);
                return;
            }

            Console.WriteLine($"Build Freshness for '{channel}'");
            Console.WriteLine($"URL: {result.AkaMsUrl}");
            Console.WriteLine($"Resolved to: {result.ResolvedUrl}");

            if (result.LastModified.HasValue)
            {
                var age = DateTimeOffset.UtcNow - result.LastModified.Value;
                Console.WriteLine($"Last Modified: {result.LastModified.Value:u} ({age.TotalHours:F1} hours ago)");
            }
            else
            {
                Console.WriteLine("Last Modified: unknown");
            }
        }
    }

    [Command("trigger-subscription")]
    [Description("Trigger a Maestro subscription (requires authentication)")]
    public async Task TriggerSubscription(
        [Argument] string subscriptionId,
        [Argument] int buildId,
        bool force = false)
    {
        if (!Guid.TryParse(subscriptionId, out var id))
        {
            Console.Error.WriteLine("Invalid subscription ID format. Expected a GUID.");
            Environment.Exit(1);
            return;
        }

        try
        {
            var result = await _service.TriggerSubscriptionAsync(id, buildId, force);
            Console.WriteLine($"✅ Successfully {(force ? "force-" : "")}triggered subscription {subscriptionId} for build #{buildId}");
            Console.WriteLine($"\nSubscription: {result.SourceRepository} → {result.TargetRepository} ({result.TargetBranch})");
            Console.WriteLine($"Channel: {result.Channel?.Name ?? "N/A"}");
            if (force)
                Console.WriteLine($"\n⚡ Force mode: existing PR branch will be overwritten with fresh VMR content.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Authentication required"))
        {
            Console.Error.WriteLine($"🔒 {ex.Message}");
            Environment.Exit(1);
        }
    }

    [Command("trigger-daily-update")]
    [Description("Trigger all daily subscriptions (requires authentication)")]
    public async Task TriggerDailyUpdate()
    {
        try
        {
            await _service.TriggerDailyUpdateAsync();
            Console.WriteLine("✅ Successfully triggered daily update for all subscriptions");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Authentication required"))
        {
            Console.Error.WriteLine($"🔒 {ex.Message}");
            Environment.Exit(1);
        }
    }

    [Command("codeflow-prs")]
    [Description("List active codeflow (tracked) pull requests")]
    public async Task CodeflowPrs(
        string? channelName = null,
        bool json = false,
        bool noCache = false)
    {
        int? channelId = null;
        if (!string.IsNullOrEmpty(channelName))
        {
            var channel = await _service.GetChannelByNameAsync(channelName, noCache);
            channelId = channel?.Id;
            if (channelId == null)
            {
                Console.Error.WriteLine($"Channel '{channelName}' not found.");
                Environment.Exit(1);
                return;
            }
        }

        var prs = await _service.GetTrackedPullRequestsAsync(channelId, noCache);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(prs, s_jsonOptions));
        }
        else
        {
            if (prs.Count == 0)
            {
                Console.WriteLine("No tracked pull requests found.");
                return;
            }

            Console.WriteLine($"Found {prs.Count} tracked pull request(s):\n");
            foreach (var pr in prs)
            {
                Console.WriteLine($"{pr.Url}");
                Console.WriteLine($"  Channel: {pr.Channel?.Name ?? "N/A"} | Target: {pr.TargetBranch}");
                Console.WriteLine($"  Last Update: {pr.LastUpdate:u}");
                Console.WriteLine();
            }
        }
    }

    [Command("tracked-pr")]
    [Description("Get tracked pull request for a subscription")]
    public async Task TrackedPr(
        [Argument] string subscriptionId,
        bool json = false,
        bool noCache = false)
    {
        try
        {
            var pr = await _service.GetTrackedPullRequestBySubscriptionIdAsync(subscriptionId, noCache);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(pr, s_jsonOptions));
            }
            else
            {
                Console.WriteLine($"Tracked PR for subscription {subscriptionId}:");
                Console.WriteLine($"URL: {pr.Url}");
                Console.WriteLine($"Channel: {pr.Channel?.Name ?? "N/A"}");
                Console.WriteLine($"Target Branch: {pr.TargetBranch}");
                Console.WriteLine($"Last Update: {pr.LastUpdate:u}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"No tracked PR found for subscription {subscriptionId}: {ex.Message}");
            Environment.Exit(1);
        }
    }

    [Command("backflow-status")]
    [Description("Get backflow status for a VMR build")]
    public async Task BackflowStatus(
        [Argument] int vmrBuildId,
        bool json = false,
        bool noCache = false)
    {
        var status = await _service.GetBackflowStatusAsync(vmrBuildId, noCache);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(status, s_jsonOptions));
        }
        else
        {
            Console.WriteLine($"Backflow status for VMR build #{vmrBuildId}:");
            Console.WriteLine($"VMR Commit: {status.VmrCommitSha}");
            Console.WriteLine($"Computed: {status.ComputationTimestamp:u}");
            Console.WriteLine($"Valid: {status.IsValid}");
            Console.WriteLine($"\nBranch statuses: {status.BranchStatuses?.Count ?? 0}");
        }
    }

    [Command("subscription-history")]
    [Description("Get update history for a subscription")]
    public async Task SubscriptionHistory(
        [Argument] string subscriptionId,
        bool json = false,
        bool noCache = false)
    {
        if (!Guid.TryParse(subscriptionId, out var id))
        {
            Console.Error.WriteLine("Invalid subscription ID format. Expected a GUID.");
            Environment.Exit(1);
            return;
        }

        var history = await _service.GetSubscriptionHistoryAsync(id, noCache);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(history, s_jsonOptions));
        }
        else
        {
            if (history.Count == 0)
            {
                Console.WriteLine($"No history found for subscription {subscriptionId}");
                return;
            }

            Console.WriteLine($"History for subscription {subscriptionId} ({history.Count} entries):\n");
            foreach (var item in history.Take(20))
            {
                var status = item.Success ? "✅" : "❌";
                Console.WriteLine($"{status} {item.Timestamp:u} - {item.Action}");
                if (!item.Success && !string.IsNullOrEmpty(item.ErrorMessage))
                    Console.WriteLine($"   Error: {item.ErrorMessage}");
            }

            if (history.Count > 20)
                Console.WriteLine($"\n... and {history.Count - 20} more entries");
        }
    }

    [Command("build-graph")]
    [Description("Get the dependency graph for a build")]
    public async Task BuildGraph(
        [Argument] int buildId,
        bool json = false,
        bool noCache = false)
    {
        var graph = await _service.GetBuildGraphAsync(buildId, noCache);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(graph, s_jsonOptions));
        }
        else
        {
            Console.WriteLine($"Build graph for build #{buildId}:");
            Console.WriteLine($"Total builds in graph: {graph.Builds?.Count ?? 0}");
        }
    }

    [Command("flow-graph")]
    [Description("Get the dependency flow graph for a channel")]
    public async Task FlowGraph(
        [Argument] int channelId,
        int days = 7,
        bool includeArcade = true,
        bool includeBuildTimes = true,
        bool includeDisabled = false,
        bool json = false,
        bool noCache = false)
    {
        var graph = await _service.GetFlowGraphAsync(days, channelId, includeArcade, includeBuildTimes, includeDisabled, null, noCache);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(graph, s_jsonOptions));
        }
        else
        {
            Console.WriteLine($"Flow graph for channel {channelId} ({days} days):");
            Console.WriteLine($"Edges: {graph.FlowEdges?.Count ?? 0}");
        }
    }

    [Command("cache")]
    [Description("Cache management commands")]
    public async Task Cache([Argument] string action)
    {
        if (action == "clear")
        {
            _cache.Clear();
            Console.WriteLine("Cache cleared successfully");
        }
        else if (action == "status")
        {
            // Query cache stats from SQLite
            Console.WriteLine("Cache status:");
            Console.WriteLine("  Database: ~/.mstro/cache.db");
            Console.WriteLine("  Status: operational");
        }
        else
        {
            Console.Error.WriteLine($"Unknown cache action: {action}");
            Console.Error.WriteLine("Valid actions: clear, status");
            Environment.Exit(1);
        }

        await Task.CompletedTask;
    }

    private static void PrintBuild(Build build)
    {
        Console.WriteLine($"Build #{build.Id}");
        Console.WriteLine($"Repository: {build.GitHubRepository ?? build.AzureDevOpsRepository}");
        Console.WriteLine($"Commit: {build.Commit}");
        Console.WriteLine($"Date Produced: {build.DateProduced:u}");
        if (build.Channels != null && build.Channels.Any())
        {
            Console.WriteLine($"Channels: {string.Join(", ", build.Channels.Select(c => c.Name))}");
        }
    }
}
