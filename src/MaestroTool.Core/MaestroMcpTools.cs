using System.ComponentModel;
using System.Text;
using Microsoft.DotNet.ProductConstructionService.Client;
using Microsoft.DotNet.ProductConstructionService.Client.Models;
using ModelContextProtocol.Server;

namespace MaestroTool.Core;

[McpServerToolType]
public class MaestroMcpTools
{
    private readonly MaestroService _service;
    private readonly MaestroToolOptions _options;
    private readonly CacheService _cache;
    private static readonly TimeSpan ActionCooldown = TimeSpan.FromMinutes(2);

    public MaestroMcpTools(MaestroService service, MaestroToolOptions options, CacheService cache)
    {
        _service = service;
        _options = options;
        _cache = cache;
    }

    private static string Timestamp(bool noCache) =>
        $"_Retrieved: {DateTimeOffset.UtcNow:u}{(noCache ? " (fresh)" : " (cached)")}_\n\n";

    [McpServerTool(Name = "maestro_subscriptions")]
    [Description("List Maestro subscriptions filtered by source/target repository and/or channel name. Returns subscription ID, source/target repo, channel, target branch, and enabled status.")]
    public async Task<string> GetSubscriptions(
        [Description("Filter by source repository URL (e.g. https://github.com/dotnet/runtime)")] string? sourceRepository = null,
        [Description("Filter by target repository URL (e.g. https://github.com/dotnet/dotnet)")] string? targetRepository = null,
        [Description("Filter by channel name (e.g. '.NET 10.0.1xx SDK')")] string? channelName = null,
        [Description("Filter by target branch (e.g. 'main')")] string? targetBranch = null,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        int? channelId = null;
        if (!string.IsNullOrEmpty(channelName))
        {
            var channel = await _service.GetChannelByNameAsync(channelName, noCache, cancellationToken);
            channelId = channel?.Id;
            if (channelId == null)
                return $"Channel '{channelName}' not found.";
        }

        var subs = await _service.GetSubscriptionsAsync(sourceRepository, targetRepository, channelId, targetBranch, noCache, cancellationToken);

        if (subs.Count == 0)
            return "No subscriptions found matching the criteria.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {subs.Count} subscription(s):\n");

        foreach (var sub in subs)
        {
            sb.AppendLine($"**{sub.SourceRepository}** → **{sub.TargetRepository}** ({sub.TargetBranch})");
            sb.AppendLine($"  Channel: {sub.Channel?.Name ?? "N/A"} | ID: {sub.Id}");
            sb.AppendLine($"  Enabled: {sub.Enabled} | Last Build: {sub.LastAppliedBuild?.Id.ToString() ?? "none"}");
            sb.AppendLine();
        }

        return Timestamp(noCache) + sb.ToString();
    }

    [McpServerTool(Name = "maestro_subscription")]
    [Description("Get a specific Maestro subscription by its GUID ID, including health diagnostic comparing last applied build to latest available.")]
    public async Task<string> GetSubscription(
        [Description("The subscription GUID")] string subscriptionId,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(subscriptionId, out var id))
            return "Invalid subscription ID format. Expected a GUID.";

        var sub = await _service.GetSubscriptionAsync(id, noCache, cancellationToken);
        var sb = new StringBuilder();

        sb.AppendLine($"**Subscription {sub.Id}**");
        sb.AppendLine($"Source: {sub.SourceRepository}");
        sb.AppendLine($"Target: {sub.TargetRepository} ({sub.TargetBranch})");
        sb.AppendLine($"Channel: {sub.Channel?.Name ?? "N/A"}");
        sb.AppendLine($"Enabled: {sub.Enabled}");

        if (sub.LastAppliedBuild != null)
        {
            sb.AppendLine($"Last Applied Build: #{sub.LastAppliedBuild.Id} ({sub.LastAppliedBuild.DateProduced:u})");
            sb.AppendLine($"  Commit: {sub.LastAppliedBuild.Commit}");
        }
        else
        {
            sb.AppendLine("Last Applied Build: none");
        }

        // Health check: compare to latest
        if (sub.Channel?.Id != null)
        {
            var latest = await _service.GetLatestBuildAsync(sub.SourceRepository, sub.Channel.Id, noCache, cancellationToken);
            if (latest != null && sub.LastAppliedBuild != null)
            {
                if (latest.Id != sub.LastAppliedBuild.Id)
                {
                    sb.AppendLine($"\n⚠️ STALE: Latest build is #{latest.Id} ({latest.DateProduced:u}), but last applied is #{sub.LastAppliedBuild.Id}");
                    sb.AppendLine($"  {latest.Id - sub.LastAppliedBuild.Id} build(s) behind");
                }
                else
                {
                    sb.AppendLine("\n✅ Up to date with latest channel build");
                }
            }
        }

        return Timestamp(noCache) + sb.ToString();
    }

    [McpServerTool(Name = "maestro_latest_build")]
    [Description("Get the latest build for a repository, optionally filtered by channel name. Returns build ID, commit, date, and channel info.")]
    public async Task<string> GetLatestBuild(
        [Description("Repository URL (e.g. https://github.com/dotnet/runtime)")] string repository,
        [Description("Optional channel name filter")] string? channelName = null,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        int? channelId = null;
        if (!string.IsNullOrEmpty(channelName))
        {
            var channel = await _service.GetChannelByNameAsync(channelName, noCache, cancellationToken);
            channelId = channel?.Id;
            if (channelId == null)
                return $"Channel '{channelName}' not found.";
        }

        var build = await _service.GetLatestBuildAsync(repository, channelId, noCache, cancellationToken);
        if (build == null)
            return $"No builds found for {repository}" + (channelName != null ? $" on channel '{channelName}'" : "") + ".";

        return Timestamp(noCache) + FormatBuild(build);
    }

    [McpServerTool(Name = "maestro_build")]
    [Description("Get a specific build by its BAR build ID. Returns build details including commit, date, channels, and repository.")]
    public async Task<string> GetBuild(
        [Description("The BAR build ID (integer)")] int buildId,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var build = await _service.GetBuildAsync(buildId, noCache, cancellationToken);
        return Timestamp(noCache) + FormatBuild(build);
    }

    [McpServerTool(Name = "maestro_channels")]
    [Description("List all Maestro channels. Returns channel names and IDs.")]
    public async Task<string> GetChannels(
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var channels = await _service.GetChannelsAsync(noCache, cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine($"Found {channels.Count} channel(s):\n");

        foreach (var ch in channels.OrderBy(c => c.Name))
        {
            sb.AppendLine($"- **{ch.Name}** (ID: {ch.Id})");
        }

        return Timestamp(noCache) + sb.ToString();
    }

    [McpServerTool(Name = "maestro_default_channels")]
    [Description("List default channel mappings (repo/branch → channel auto-assignment). Optionally filter by repository URL or branch.")]
    public async Task<string> GetDefaultChannels(
        [Description("Filter by repository URL")] string? repository = null,
        [Description("Filter by branch name")] string? branch = null,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var defaults = await _service.GetDefaultChannelsAsync(repository, branch, noCache, cancellationToken);

        if (defaults.Count == 0)
            return "No default channel mappings found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {defaults.Count} default channel mapping(s):\n");

        foreach (var dc in defaults)
        {
            sb.AppendLine($"- **{dc.Repository}** ({dc.Branch}) → {dc.Channel?.Name ?? "N/A"}");
        }

        return Timestamp(noCache) + sb.ToString();
    }

    [McpServerTool(Name = "maestro_subscription_health")]
    [Description("Check subscription health for a target repository. For each active subscription, compares the last applied build against the latest available build on the channel to detect stale subscriptions.")]
    public async Task<string> GetSubscriptionHealth(
        [Description("Target repository URL (e.g. https://github.com/dotnet/dotnet)")] string targetRepository,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var results = await _service.GetSubscriptionHealthAsync(targetRepository, noCache, cancellationToken);

        if (results.Count == 0)
            return $"No active subscriptions found targeting {targetRepository}.";

        var sb = new StringBuilder();
        var staleCount = results.Count(r => r.IsStale);
        sb.AppendLine($"Subscription health for **{targetRepository}**: {results.Count} subscription(s), {staleCount} stale\n");

        foreach (var r in results)
        {
            var status = r.IsStale ? $"⚠️ STALE ({r.BuildsBehind} behind)" : "✅ Current";
            sb.AppendLine($"**{r.SourceRepository}** → {r.TargetBranch}");
            sb.AppendLine($"  Channel: {r.ChannelName} | Status: {status}");
            if (r.Error != null)
                sb.AppendLine($"  ⚠️ Error: {r.Error}");
            if (r.LastAppliedBuildId != null)
                sb.AppendLine($"  Last Applied: #{r.LastAppliedBuildId} ({r.LastAppliedDate:u})");
            if (r.LatestBuildId != null)
                sb.AppendLine($"  Latest Available: #{r.LatestBuildId} ({r.LatestBuildDate:u})");
            sb.AppendLine();
        }

        return Timestamp(noCache) + sb.ToString();
    }

    [McpServerTool(Name = "maestro_build_freshness")]
    [Description("Check build freshness for a channel by resolving aka.ms redirect URLs and checking the Last-Modified header of the published build artifacts.")]
    public async Task<string> GetBuildFreshness(
        [Description("Channel short name for aka.ms URL (e.g. '10.0.1xx', '9.0.1xx')")] string channel,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetBuildFreshnessAsync(channel, noCache, cancellationToken);

        if (!result.IsAvailable)
            return $"Build freshness check failed for channel '{channel}': {result.Error ?? "URL not available"}";

        var sb = new StringBuilder();
        sb.AppendLine($"**Build Freshness for '{channel}'**");
        sb.AppendLine($"URL: {result.AkaMsUrl}");
        sb.AppendLine($"Resolved to: {result.ResolvedUrl}");

        if (result.LastModified.HasValue)
        {
            var age = DateTimeOffset.UtcNow - result.LastModified.Value;
            sb.AppendLine($"Last Modified: {result.LastModified.Value:u} ({age.TotalHours:F1} hours ago)");
        }
        else
        {
            sb.AppendLine("Last Modified: unknown");
        }

        return Timestamp(noCache) + sb.ToString();
    }

    [McpServerTool(Name = "maestro_trigger_subscription")]
    [Description("Trigger a specific Maestro subscription to process a build. This is a non-destructive action that initiates dependency flow processing.")]
    public async Task<string> TriggerSubscription(
        [Description("The subscription GUID to trigger")] string subscriptionId,
        [Description("The BAR build ID to process")] int buildId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(subscriptionId, out var id))
            return "Invalid subscription ID format. Expected a GUID.";

        var dedupKey = $"action:trigger-sub:{subscriptionId}:{buildId}";
        var recent = _cache.GetRecentAction(dedupKey);
        if (recent.HasValue)
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] Trigger: TriggerSubscription dedup-skipped args=(subscriptionId={subscriptionId}, buildId={buildId}) lastTriggered={recent.Value:O}");
            return $"⏳ This subscription was already triggered for build #{buildId} at {recent.Value:u}. Skipping duplicate.";
        }

        try
        {
            var result = await _service.TriggerSubscriptionAsync(id, buildId, cancellationToken);
            _cache.RecordAction(dedupKey, ActionCooldown);

            var sb = new StringBuilder();
            sb.AppendLine($"✅ Successfully triggered subscription {subscriptionId} for build #{buildId}");
            sb.AppendLine($"\nSubscription: **{result.SourceRepository}** → **{result.TargetRepository}** ({result.TargetBranch})");
            sb.AppendLine($"Channel: {result.Channel?.Name ?? "N/A"}");
            sb.AppendLine($"\nThe subscription will now process build #{buildId} and create/update a dependency update PR if needed.");

            return sb.ToString();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Authentication required"))
        {
            return $"🔒 {ex.Message}";
        }
    }

    [McpServerTool(Name = "maestro_trigger_daily_update")]
    [Description("Trigger all daily-update subscriptions to run. This is a non-destructive action that initiates processing of all subscriptions configured for daily updates.")]
    public async Task<string> TriggerDailyUpdate(CancellationToken cancellationToken = default)
    {
        var dedupKey = "action:trigger-daily-update";
        var recent = _cache.GetRecentAction(dedupKey);
        if (recent.HasValue)
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] Trigger: TriggerDailyUpdate dedup-skipped lastTriggered={recent.Value:O}");
            return $"⏳ Daily update was already triggered at {recent.Value:u}. Skipping duplicate.";
        }

        try
        {
            await _service.TriggerDailyUpdateAsync(cancellationToken);
            _cache.RecordAction(dedupKey, ActionCooldown);

            return "✅ Successfully triggered all daily-update subscriptions. Subscriptions will now process their latest builds and create/update dependency update PRs as needed.";
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Authentication required"))
        {
            return $"🔒 {ex.Message}";
        }
    }

    [McpServerTool(Name = "maestro_clear_cache")]
    [Description("Clear all cached Maestro data (shared across all mstro instances). Use after performing actions or when you need guaranteed fresh data from all tools.")]
    public string ClearCache()
    {
        _cache.Clear();
        return "✅ Cache cleared. All subsequent tool calls will fetch fresh data from the Maestro API.";
    }

    [McpServerTool(Name = "maestro_codeflow_prs")]
    [Description("List active codeflow (tracked) pull requests managed by Maestro. Optionally filter by channel name. Shows PR URL, channel, target branch, last update, and subscription updates.")]
    public async Task<string> GetCodeflowPrs(
        [Description("Filter by channel name (e.g. '.NET 10.0.1xx SDK')")] string? channelName = null,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        int? channelId = null;
        if (!string.IsNullOrEmpty(channelName))
        {
            var channel = await _service.GetChannelByNameAsync(channelName, noCache, cancellationToken);
            channelId = channel?.Id;
            if (channelId == null)
                return $"Channel '{channelName}' not found.";
        }

        var prs = await _service.GetTrackedPullRequestsAsync(channelId, noCache, cancellationToken);

        if (prs.Count == 0)
            return "No active tracked pull requests found" + (channelName != null ? $" for channel '{channelName}'" : "") + ".";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {prs.Count} tracked pull request(s):\n");

        foreach (var pr in prs)
        {
            sb.AppendLine($"**{pr.Url}**");
            sb.AppendLine($"  Channel: {pr.Channel?.Name ?? "N/A"} | Target Branch: {pr.TargetBranch ?? "N/A"}");
            sb.AppendLine($"  Head Branch: {pr.HeadBranch ?? "N/A"} | Source Enabled: {pr.SourceEnabled}");
            sb.AppendLine($"  Last Update: {pr.LastUpdate:u} | Last Check: {pr.LastCheck:u}");
            if (pr.NextCheck.HasValue)
                sb.AppendLine($"  Next Check: {pr.NextCheck.Value:u}");
            if (pr.Updates?.Count > 0)
            {
                sb.AppendLine($"  Updates ({pr.Updates.Count}):");
                foreach (var update in pr.Updates)
                {
                    sb.AppendLine($"    - {update.SourceRepository} (sub: {update.SubscriptionId}, build: #{update.BuildId})");
                }
            }
            sb.AppendLine();
        }

        return Timestamp(noCache) + sb.ToString();
    }

    [McpServerTool(Name = "maestro_tracked_pr")]
    [Description("Get the tracked pull request for a specific Maestro subscription. Returns PR URL, channel, target branch, and update details. Returns a message if no PR is currently tracked.")]
    public async Task<string> GetTrackedPr(
        [Description("The subscription GUID")] string subscriptionId,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(subscriptionId, out _))
            return "Invalid subscription ID format. Expected a GUID.";

        try
        {
            var pr = await _service.GetTrackedPullRequestBySubscriptionIdAsync(subscriptionId, noCache, cancellationToken);

            var sb = new StringBuilder();
            sb.AppendLine($"**Tracked PR for subscription {subscriptionId}**\n");
            sb.AppendLine($"URL: {pr.Url}");
            sb.AppendLine($"Channel: {pr.Channel?.Name ?? "N/A"}");
            sb.AppendLine($"Target Branch: {pr.TargetBranch ?? "N/A"} | Head Branch: {pr.HeadBranch ?? "N/A"}");
            sb.AppendLine($"Source Enabled: {pr.SourceEnabled}");
            sb.AppendLine($"Last Update: {pr.LastUpdate:u} | Last Check: {pr.LastCheck:u}");
            if (pr.NextCheck.HasValue)
                sb.AppendLine($"Next Check: {pr.NextCheck.Value:u}");
            if (pr.Updates?.Count > 0)
            {
                sb.AppendLine($"\nUpdates ({pr.Updates.Count}):");
                foreach (var update in pr.Updates)
                {
                    sb.AppendLine($"  - {update.SourceRepository} (sub: {update.SubscriptionId}, build: #{update.BuildId})");
                }
            }

            return Timestamp(noCache) + sb.ToString();
        }
        catch (RestApiException ex) when (ex.Response.Status == 404)
        {
            return $"No active PR tracked for subscription {subscriptionId}.";
        }
    }

    [McpServerTool(Name = "maestro_backflow_status")]
    [Description("Get backflow status for a specific VMR build. Shows per-branch backflow status including commit distance and subscription details.")]
    public async Task<string> GetBackflowStatus(
        [Description("The VMR (dotnet/dotnet) BAR build ID to check backflow status for")] int vmrBuildId,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var status = await _service.GetBackflowStatusAsync(vmrBuildId, noCache, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"**Backflow Status for VMR Build #{vmrBuildId}**\n");
        sb.AppendLine($"VMR Commit: {status.VmrCommitSha ?? "N/A"}");
        sb.AppendLine($"Computed: {status.ComputationTimestamp:u}");
        sb.AppendLine($"Valid: {status.IsValid}\n");

        if (status.BranchStatuses?.Count > 0)
        {
            sb.AppendLine($"Branch statuses ({status.BranchStatuses.Count}):\n");
            foreach (var (branch, branchStatus) in status.BranchStatuses)
            {
                var branchValid = branchStatus.IsValid ? "✅" : "⚠️";
                sb.AppendLine($"**{branch}** {branchValid} (channel ID: {branchStatus.DefaultChannelId})");
                if (branchStatus.SubscriptionStatuses?.Count > 0)
                {
                    foreach (var subStatus in branchStatus.SubscriptionStatuses)
                    {
                        var distance = subStatus.CommitDistance > 0 ? $"⚠️ {subStatus.CommitDistance} commits behind" : "✅ up to date";
                        sb.AppendLine($"  - {subStatus.TargetRepository} ({subStatus.TargetBranch}): {distance}");
                        sb.AppendLine($"    Sub: {subStatus.SubscriptionId} | Last SHA: {subStatus.LastBackflowedSha ?? "none"}");
                    }
                }
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("No branch statuses available.");
        }

        return Timestamp(noCache) + sb.ToString();
    }

    [McpServerTool(Name = "maestro_subscription_history")]
    [Description("Get the update history for a specific Maestro subscription. Shows timestamped actions, success/failure status, and error messages for failed updates.")]
    public async Task<string> GetSubscriptionHistory(
        [Description("The subscription GUID")] string subscriptionId,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(subscriptionId, out var id))
            return "Invalid subscription ID format. Expected a GUID.";

        var history = await _service.GetSubscriptionHistoryAsync(id, noCache, cancellationToken);

        if (history.Count == 0)
            return $"No history found for subscription {subscriptionId}.";

        var sb = new StringBuilder();
        sb.AppendLine($"**Subscription History for {subscriptionId}** ({history.Count} entries):\n");

        foreach (var item in history.OrderByDescending(h => h.Timestamp))
        {
            var status = item.Success ? "✅" : "❌";
            sb.AppendLine($"{status} **{item.Timestamp:u}** — {item.Action}");
            if (!item.Success && !string.IsNullOrEmpty(item.ErrorMessage))
                sb.AppendLine($"    Error: {item.ErrorMessage}");
            if (!string.IsNullOrEmpty(item.RetryUrl))
                sb.AppendLine($"    Retry: {item.RetryUrl}");
            sb.AppendLine();
        }

        return Timestamp(noCache) + sb.ToString();
    }

    [McpServerTool(Name = "maestro_build_graph")]
    [Description("Get the full dependency graph for a build. Returns all builds in the dependency tree with their relationships.")]
    public async Task<string> GetBuildGraph(
        [Description("The BAR build ID to get the dependency graph for")] int buildId,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var graph = await _service.GetBuildGraphAsync(buildId, noCache, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"**Build Graph for Build #{buildId}**");
        sb.AppendLine($"Valid: {graph.IsValid}");
        sb.AppendLine($"Total Builds: {graph.Builds?.Count ?? 0}\n");

        if (graph.Builds == null || graph.Builds.Count == 0)
        {
            sb.AppendLine("No builds found in the dependency graph.");
            return Timestamp(noCache) + sb.ToString();
        }

        // Identify the root build
        sb.AppendLine($"**Root Build: #{buildId}**\n");

        // Show all builds in the graph
        sb.AppendLine("**All Builds in Dependency Tree:**\n");
        foreach (var (key, build) in graph.Builds.OrderBy(kvp => kvp.Value.Id))
        {
            sb.AppendLine($"**Build #{build.Id}**");
            sb.AppendLine($"  Repository: {build.GitHubRepository ?? build.AzureDevOpsRepository ?? "N/A"}");
            sb.AppendLine($"  Commit: {build.Commit}");
            sb.AppendLine($"  Date: {build.DateProduced:u}");
            sb.AppendLine($"  AzDO Build: {build.AzureDevOpsBuildNumber ?? "N/A"}");
            if (build.Dependencies?.Count > 0)
            {
                sb.AppendLine($"  Dependencies: {build.Dependencies.Count}");
            }
            sb.AppendLine();
        }

        return Timestamp(noCache) + sb.ToString();
    }

    [McpServerTool(Name = "maestro_flow_graph")]
    [Description("Get the dependency flow graph for a channel showing how builds flow through subscriptions between repositories.")]
    public async Task<string> GetFlowGraph(
        [Description("The channel ID to get the flow graph for")] int channelId,
        [Description("Number of days to include in the flow graph analysis")] int days = 7,
        [Description("Include Arcade/tooling dependencies in the graph")] bool includeArcade = true,
        [Description("Include build time metrics in the graph")] bool includeBuildTimes = true,
        [Description("Include disabled subscriptions in the graph")] bool includeDisabledSubscriptions = false,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var graph = await _service.GetFlowGraphAsync(days, channelId, includeArcade, includeBuildTimes, includeDisabledSubscriptions, null, noCache, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"**Flow Graph for Channel #{channelId}** (last {days} days)");
        sb.AppendLine($"Valid: {graph.IsValid}");
        sb.AppendLine($"Flow Nodes: {graph.FlowRefs?.Count ?? 0}");
        sb.AppendLine($"Flow Edges: {graph.FlowEdges?.Count ?? 0}\n");

        if (graph.FlowRefs == null || graph.FlowRefs.Count == 0)
        {
            sb.AppendLine("No flow nodes found in the dependency graph.");
            return Timestamp(noCache) + sb.ToString();
        }

        // Section 1: Flow Nodes (Repositories)
        sb.AppendLine("**Flow Nodes (Repositories):**\n");
        foreach (var flowRef in graph.FlowRefs.OrderBy(f => f.Repository).ThenBy(f => f.Branch))
        {
            var onLongestPath = flowRef.OnLongestBuildPath ? " ⚡" : "";
            sb.AppendLine($"**{flowRef.Repository}** ({flowRef.Branch}){onLongestPath}");
            sb.AppendLine($"  ID: {flowRef.Id}");
            if (flowRef.OfficialBuildTime > 0)
                sb.AppendLine($"  Official Build Time: {flowRef.OfficialBuildTime} min");
            if (flowRef.PrBuildTime > 0)
                sb.AppendLine($"  PR Build Time: {flowRef.PrBuildTime} min");
            if (flowRef.BestCasePathTime > 0)
                sb.AppendLine($"  Best Case Path Time: {flowRef.BestCasePathTime} min");
            if (flowRef.WorstCasePathTime > 0)
                sb.AppendLine($"  Worst Case Path Time: {flowRef.WorstCasePathTime} min");
            if (flowRef.GoalTimeInMinutes > 0)
                sb.AppendLine($"  Goal Time: {flowRef.GoalTimeInMinutes} min");
            if (flowRef.InputChannels?.Count > 0)
                sb.AppendLine($"  Input Channels: {string.Join(", ", flowRef.InputChannels)}");
            if (flowRef.OutputChannels?.Count > 0)
                sb.AppendLine($"  Output Channels: {string.Join(", ", flowRef.OutputChannels)}");
            sb.AppendLine();
        }

        // Section 2: Flow Edges (Subscription Connections)
        if (graph.FlowEdges != null && graph.FlowEdges.Count > 0)
        {
            sb.AppendLine("\n**Flow Edges (Subscription Connections):**\n");
            foreach (var edge in graph.FlowEdges)
            {
                var fromRepo = graph.FlowRefs.FirstOrDefault(f => f.Id == edge.FromId);
                var toRepo = graph.FlowRefs.FirstOrDefault(f => f.Id == edge.ToId);
                
                var indicators = new List<string>();
                if (edge.OnLongestBuildPath) indicators.Add("⚡ Longest Path");
                if (edge.IsToolingOnly) indicators.Add("🔧 Tooling");
                if (edge.PartOfCycle == true) indicators.Add("🔄 Cycle");
                if (edge.BackEdge) indicators.Add("⬅️ Back Edge");
                
                var indicatorStr = indicators.Count > 0 ? $" ({string.Join(", ", indicators)})" : "";
                
                sb.AppendLine($"**{fromRepo?.Repository ?? edge.FromId}** → **{toRepo?.Repository ?? edge.ToId}**{indicatorStr}");
                sb.AppendLine($"  Channel: {edge.ChannelName ?? "N/A"}");
                sb.AppendLine($"  Subscription: {edge.SubscriptionId}");
                sb.AppendLine();
            }
        }

        // Highlight longest build path summary
        var longestPathNodes = graph.FlowRefs.Where(f => f.OnLongestBuildPath).ToList();
        if (longestPathNodes.Count > 0)
        {
            sb.AppendLine("\n⚡ **Longest Build Path:** " + string.Join(" → ", longestPathNodes.Select(n => n.Repository)));
        }

        return Timestamp(noCache) + sb.ToString();
    }

    private static string FormatBuild(Build build)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Build #{build.Id}**");
        sb.AppendLine($"Repository: {build.GitHubRepository ?? build.AzureDevOpsRepository ?? "N/A"}");
        sb.AppendLine($"Commit: {build.Commit}");
        sb.AppendLine($"Date: {build.DateProduced:u}");
        sb.AppendLine($"AzDO Build: {build.AzureDevOpsBuildNumber ?? "N/A"}");
        sb.AppendLine($"Stable: {build.Stable} | Released: {build.Released}");

        if (build.Channels?.Count > 0)
        {
            sb.AppendLine($"Channels: {string.Join(", ", build.Channels.Select(c => c.Name))}");
        }

        return sb.ToString();
    }
}
