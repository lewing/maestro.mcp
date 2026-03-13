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

// Default to MCP if launched by an MCP host (stdin redirected), otherwise show help
app.Run(args.Length == 0 ? (Console.IsInputRedirected ? ["mcp"] : ["--help"]) : args);

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
                options.ServerInfo = new() { Name = "maestro", Version = "0.13.0" };
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(MaestroMcpTools).Assembly);

        await builder.Build().RunAsync();
    }

    [Command("subscriptions")]
    [Description("List Maestro subscriptions filtered by source/target repository and/or channel name. For health checks, use subscription-health. For details on a single subscription by ID, use subscription.")]
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
    [Description("Get a specific Maestro subscription by its GUID ID, including health diagnostic comparing last applied build to latest available. For batch health checks across a repository, use subscription-health instead.")]
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
    [Description("Get the latest build for a repository, optionally filtered by channel name.")]
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
    [Description("Get a specific build by its BAR build ID. For listing/filtering builds, use builds command.")]
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

    [Command("builds")]
    [Description("List builds, optionally filtered by repository, channel, commit, or build number.")]
    public async Task Builds(
        string? repository = null,
        string? channelName = null,
        string? commit = null,
        string? buildNumber = null,
        int? count = null,
        bool json = false,
        bool noCache = false)
    {
        int? channelId = null;
        if (!string.IsNullOrEmpty(channelName))
        {
            var ch = await _service.GetChannelByNameAsync(channelName, noCache);
            if (ch == null)
            {
                Console.Error.WriteLine($"Channel '{channelName}' not found.");
                Environment.Exit(1);
                return;
            }
            channelId = ch.Id;
        }

        var builds = await _service.ListBuildsAsync(repository, channelId, commit, buildNumber, count, noCache);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(builds, s_jsonOptions));
        }
        else
        {
            if (builds.Count == 0)
            {
                Console.WriteLine("No builds found matching the specified filters.");
                return;
            }

            Console.WriteLine($"Found {builds.Count} build(s):\n");
            foreach (var build in builds)
            {
                PrintBuild(build);
                Console.WriteLine("---");
            }
        }
    }

    [Command("channels")]
    [Description("List all Maestro channels.")]
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

    [Command("channel")]
    [Description("Get a specific channel by ID or name. For listing all channels, use channels command.")]
    public async Task Channel(
        [Argument] string channelId,
        bool json = false,
        bool noCache = false)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            Console.Error.WriteLine("Channel ID or name is required.");
            Environment.Exit(1);
            return;
        }

        Channel channel;
        if (int.TryParse(channelId, out var parsedId))
        {
            if (parsedId < 0)
            {
                Console.Error.WriteLine($"Invalid channel ID '{channelId}'. Channel IDs must be non-negative integers.");
                Environment.Exit(1);
                return;
            }

            try
            {
                channel = await _service.GetChannelAsync(parsedId, noCache);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Channel with ID {parsedId} not found.");
                Environment.Exit(1);
                return;
            }
        }
        else
        {
            var found = await _service.GetChannelByNameAsync(channelId, noCache);
            if (found == null)
            {
                Console.Error.WriteLine($"Channel '{channelId}' not found.");
                Environment.Exit(1);
                return;
            }
            channel = found;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(channel, s_jsonOptions));
        }
        else
        {
            Console.WriteLine($"{channel.Name} (ID: {channel.Id})");
            Console.WriteLine($"Classification: {channel.Classification}");
        }
    }

    [Command("default-channels")]
    [Description("List default channel mappings (repo/branch → channel auto-assignment). Optionally filter by repository URL or branch.")]
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
    [Description("Check subscription health for a target repository. For each active subscription, compares the last applied build against the latest available build on the channel to detect stale subscriptions. Start here for most investigations.")]
    public async Task SubscriptionHealth(
        [Argument] string targetRepository,
        bool json = false,
        bool noCache = false,
        bool includeCommitDetails = false,
        bool validate = false)
    {
        var results = await _service.GetSubscriptionHealthAsync(targetRepository, noCache, includeCommitDetails, validate);

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

                if (r.Oscillation != null)
                {
                    var timespan = r.Oscillation.LastSeen.HasValue && r.Oscillation.FirstSeen.HasValue
                        ? $" over {(r.Oscillation.LastSeen.Value - r.Oscillation.FirstSeen.Value).TotalHours:F1}h"
                        : "";
                    Console.WriteLine($"  🔄 State oscillation detected: {r.Oscillation.OscillationCount} cycles of {r.Oscillation.State1} ↔ {r.Oscillation.State2}{timespan}");
                    Console.WriteLine($"     → Subscription likely stuck (arcade-services#6090)");
                }

                if (r.TrackedPr != null)
                {
                    var stateEmoji = r.TrackedPr.State switch
                    {
                        TrackedPrState.MergedButNotCleared => "🔴",
                        TrackedPrState.ClosedButNotCleared => "🟠",
                        TrackedPrState.BlockedByCI => "🟡",
                        TrackedPrState.Active => "🟢",
                        TrackedPrState.Missing => "⚪",
                        _ => "❓"
                    };
                    var stateLabel = r.TrackedPr.State switch
                    {
                        TrackedPrState.MergedButNotCleared => "Stuck: PR merged but state not cleared (arcade-services#6090)",
                        TrackedPrState.ClosedButNotCleared => "Stuck: PR closed but state not cleared",
                        TrackedPrState.BlockedByCI => "Blocked: PR has failing CI checks",
                        TrackedPrState.Active => "PR open and active",
                        TrackedPrState.Missing => "No tracked PR",
                        _ => "Unknown PR state"
                    };
                    Console.WriteLine($"  {stateEmoji} Tracked PR: {stateLabel}");
                    if (r.TrackedPr.PrUrl != null)
                        Console.WriteLine($"     {r.TrackedPr.PrUrl}");
                }

                if (r.VmrConsumedCommit != null)
                {
                    var shortSha = r.VmrConsumedCommit.Length > 7 ? r.VmrConsumedCommit[..7] : r.VmrConsumedCommit;
                    var dateStr = r.VmrConsumedDate.HasValue ? $" ({r.VmrConsumedDate.Value:u})" : "";
                    Console.WriteLine($"  📌 VMR consumed: {shortSha}{dateStr} — actual code in dotnet/dotnet");
                }

                Console.WriteLine();
            }
        }
    }

    [Command("build-freshness")]
    [Description("Check build freshness for a channel by resolving aka.ms redirect URLs and checking the Last-Modified header of the published build artifacts.")]
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
    [Description("Trigger a Maestro subscription. Provide buildId directly. Use force=true to force-trigger (overwrites existing PR branch) for stale backflow PR remediation. Requires authentication.")]
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
    [Description("Trigger all daily-update subscriptions to run. This is a non-destructive action that initiates processing of all subscriptions configured for daily updates. Requires authentication.")]
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
    [Description("List active codeflow (tracked) pull requests managed by Maestro. Optionally filter by channel name.")]
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
    [Description("Get the tracked pull request for a specific Maestro subscription.")]
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
    [Description("Get backflow status for a specific VMR build. Shows per-branch backflow status including commit distance and subscription details.")]
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
    [Description("Get the update history for a specific Maestro subscription. Shows timestamped actions, success/failure status, and error messages for failed updates.")]
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
    [Description("Get the full dependency graph for a build. Returns all builds in the dependency tree with their relationships.")]
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
    [Description("Get the dependency flow graph for a channel showing how builds flow through subscriptions between repositories.")]
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

    [Command("codeflow-statuses")]
    [Description("Get codeflow status for a repository and branch. Shows forward flow and backflow subscription statuses, active PRs, and build staleness for each mapping. Defaults to the VMR (dotnet/dotnet, main).")]
    public async Task CodeflowStatuses(
        [Argument] string repositoryUrl = "https://github.com/dotnet/dotnet",
        string branch = "main",
        bool json = false,
        bool noCache = false)
    {
        var statuses = await _service.GetCodeflowStatusesAsync(repositoryUrl, branch, noCache);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(statuses, s_jsonOptions));
        }
        else
        {
            if (statuses.Count == 0)
            {
                Console.WriteLine($"No codeflow statuses found for {repositoryUrl} ({branch}).");
                return;
            }

            Console.WriteLine($"Codeflow statuses for {repositoryUrl} (branch: {branch})\n");
            Console.WriteLine($"Found {statuses.Count} mapping(s):\n");

            foreach (var status in statuses)
            {
                Console.WriteLine($"=== {status.MappingName ?? "Unknown Mapping"} ===");
                Console.WriteLine($"Repository: {status.RepositoryUrl} ({status.RepositoryBranch})");

                PrintFlowStatus("Forward Flow", status.ForwardFlow);
                PrintFlowStatus("Backflow", status.Backflow);
                Console.WriteLine();
            }
        }
    }

    [Command("guide")]
    [Description("Output a structured guide to all mstro capabilities, organized by workflow. Designed for agent consumption.")]
    public void Guide()
    {
        const string guide = @"# mstro — Maestro/BAR CLI Guide

## Quick Reference
| Command | Description |
|---------|-------------|
| subscriptions | List subscriptions filtered by source/target repository and/or channel |
| subscription | Get a subscription by GUID ID with health diagnostic |
| latest-build | Get the latest build for a repository, optionally filtered by channel |
| build | Get a specific build by BAR build ID |
| builds | List builds, filtered by repository, channel, commit, or build number |
| channels | List all Maestro channels |
| channel | Get a specific channel by ID or name |
| default-channels | List default channel mappings (repo/branch → channel auto-assignment) |
| subscription-health | Check subscription health for a target repository (detects stale subscriptions) |
| build-freshness | Check build freshness for a channel via aka.ms redirect |
| trigger-subscription | Trigger a subscription update (requires authentication) |
| trigger-daily-update | Trigger all daily-update subscriptions (requires authentication) |
| codeflow-prs | List active codeflow (tracked) pull requests managed by Maestro |
| tracked-pr | Get the tracked PR for a specific subscription |
| backflow-status | Get backflow status for a VMR build |
| subscription-history | Get update history for a subscription |
| build-graph | Get the full dependency graph for a build |
| flow-graph | Get the dependency flow graph for a channel |
| codeflow-statuses | Get codeflow status (forward/backflow) for a repository and branch |
| cache | Cache management (clear, status) |

## Workflows

### Investigating Subscription Health
1. `mstro subscription-health --target-repository <repo-url> --json`
   Check all subscriptions targeting a repository, detect stale subscriptions by comparing
   last-applied builds against latest available builds on their channels.

2. `mstro subscription <subscription-id> --json`
   Drill into a specific subscription by GUID to see detailed configuration, last applied
   build, and health status.

3. `mstro subscription-history <subscription-id> --json`
   Check subscription update history to see timestamped actions, success/failure status,
   and error messages for failed updates. Useful for diagnosing stuck subscriptions.

**Example:** Find stale subscriptions for the VMR, then investigate a specific one:
```bash
mstro subscription-health --target-repository https://github.com/dotnet/dotnet --json | jq '.StaleSubs[]'
mstro subscription <guid> --json
mstro subscription-history <guid> --json
```

### Tracing Build Flow
1. `mstro latest-build --repository <repo-url> --channel-name <channel> --json`
   Find the latest build for a repository on a specific channel. Use this to identify
   the most recent build that should have flowed downstream.

2. `mstro build <build-id> --json`
   Get detailed information about a specific build including repository, commit,
   date produced, and channels.

3. `mstro build-graph <build-id> --json`
   Get the full dependency graph showing all builds in the dependency tree with their
   relationships. Useful for tracing where dependencies came from.

**Example:** Trace runtime dependency flow:
```bash
BUILD_ID=$(mstro latest-build https://github.com/dotnet/runtime --channel-name "".NET 10.0.1xx SDK"" --json | jq -r '.Id')
mstro build $BUILD_ID --json
mstro build-graph $BUILD_ID --json
```

### Checking Codeflow Status
1. `mstro codeflow-statuses --json`
   Get overview of forward flow and backflow for the VMR (dotnet/dotnet, main branch).
   Shows per-branch status including commit distance and subscription details.

2. `mstro codeflow-prs --channel-name <channel> --json`
   List all active codeflow PRs managed by Maestro, optionally filtered by channel.
   Shows PR URLs, last update times, and subscription IDs.

3. `mstro tracked-pr <subscription-id> --json`
   Get the specific tracked PR for a subscription. Useful when investigating why
   a subscription is stuck with an active PR.

4. `mstro backflow-status <vmr-build-id> --json`
   Check backflow status for a specific VMR build. Shows per-branch backflow status
   including commit distance and subscription details.

**Example:** Check VMR codeflow health:
```bash
mstro codeflow-statuses --json
mstro codeflow-prs --json | jq '.[] | select(.Url != null)'
mstro backflow-status <vmr-build-id> --json
```

### Channel & Build Discovery
1. `mstro channels --json`
   List all Maestro channels. Use this to discover available channels for filtering
   other commands.

2. `mstro channel <id-or-name> --json`
   Get details for a specific channel by integer ID or string name (case-insensitive).
   Shows channel classification and metadata.

3. `mstro default-channels --repository <repo-url> --json`
   List default channel mappings showing which channels are auto-assigned when builds
   are published from specific repo/branch combinations.

4. `mstro build-freshness <channel-short-name> --json`
   Check build freshness by resolving aka.ms redirect URLs and inspecting Last-Modified
   headers. Channel short name examples: '10.0.1xx', '9.0.1xx'.

**Example:** Explore channel configuration:
```bash
mstro channels --json | jq '.[] | select(.Name | contains(""10.0""))'
mstro channel "".NET 10.0.1xx SDK"" --json
mstro default-channels --repository https://github.com/dotnet/runtime --json
mstro build-freshness 10.0.1xx --json
```

### Triggering Actions
1. `mstro trigger-subscription <subscription-id> --build-id <build-id>`
   Trigger a subscription to process a specific build. Requires authentication
   (MAESTRO_BAR_TOKEN or cached darc credentials).

   Alternative: Auto-resolve latest build by source repository and channel:
   `mstro trigger-subscription <subscription-id> --source-repository <repo-url> --channel-name <channel>`

2. `mstro trigger-subscription <subscription-id> --build-id <build-id> --force`
   Force-trigger a subscription, which overwrites the existing PR branch with fresh
   VMR content. Use this for stale backflow PR remediation.

3. `mstro trigger-daily-update`
   Trigger all daily-update subscriptions to run. This is a non-destructive action
   that initiates processing of all subscriptions configured for daily updates.

**Example:** Trigger a stale subscription:
```bash
# Option 1: Specify build ID directly
mstro trigger-subscription <guid> --build-id 302353

# Option 2: Auto-resolve latest build
mstro trigger-subscription <guid> --source-repository https://github.com/dotnet/runtime --channel-name "".NET 10.0.1xx SDK""

# Option 3: Force-trigger to overwrite stale PR
mstro trigger-subscription <guid> --build-id 302353 --force
```

### Cache Management
- `mstro cache status` — Show cache statistics and location
- `mstro cache clear` — Clear all cached data (shared across all mstro instances)

**Notes:**
- Cache is shared across processes at `~/.mstro/cache.db` (SQLite WAL mode)
- Cache is shared between CLI and MCP server instances
- All commands support `--no-cache` to bypass cache for fresh data
- Clearing the cache does NOT clear action dedup cooldowns (2-minute window for triggers)

## Notes
- All query commands support `--json` for structured output
- All commands support `--no-cache` to bypass the cache
- Cache is shared across processes at `~/.mstro/cache.db` (SQLite WAL mode)
- Install: `dotnet tool install -g lewing.maestro.mcp`
- Authentication: Set MAESTRO_BAR_TOKEN or run `darc authenticate` once
- For command-specific help: `mstro <command> --help`
";

        Console.WriteLine(guide);
    }

    [Command("cache")]
    [Description("Cache management commands. Actions: 'clear' to clear all cached Maestro data (shared across all mstro instances), 'status' to show cache status.")]
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

    private static void PrintFlowStatus(string label, CodeflowSubscriptionStatus? flow)
    {
        if (flow == null)
        {
            Console.WriteLine($"  {label}: not configured");
            return;
        }

        Console.WriteLine($"  {label}:");
        if (flow.Subscription != null)
        {
            var sub = flow.Subscription;
            Console.WriteLine($"    Subscription: {sub.Id}");
            Console.WriteLine($"    {sub.SourceRepository} → {sub.TargetRepository} ({sub.TargetBranch})");
            Console.WriteLine($"    Channel: {sub.Channel?.Name ?? "N/A"} | Enabled: {sub.Enabled}");
            if (sub.LastAppliedBuild != null)
                Console.WriteLine($"    Last Applied Build: #{sub.LastAppliedBuild.Id} ({sub.LastAppliedBuild.DateProduced:u})");
        }

        if (flow.ActivePullRequest != null)
        {
            Console.WriteLine($"    🔄 Active PR: {flow.ActivePullRequest.Url}");
            Console.WriteLine($"      Last Update: {flow.ActivePullRequest.LastUpdate:u}");
        }

        if (flow.NewestBuildId.HasValue)
        {
            var lastAppliedId = flow.Subscription?.LastAppliedBuild?.Id;
            if (lastAppliedId.HasValue && lastAppliedId.Value < flow.NewestBuildId.Value)
                Console.WriteLine($"    ⚠️ Behind — newest build #{flow.NewestBuildId.Value} ({flow.NewestBuildDate:u}), last applied #{lastAppliedId.Value}");
            else if (lastAppliedId.HasValue)
                Console.WriteLine($"    ✅ Up to date (build #{flow.NewestBuildId.Value})");
            else
                Console.WriteLine($"    Newest Build: #{flow.NewestBuildId.Value} ({flow.NewestBuildDate:u})");
        }
    }
}
