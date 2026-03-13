using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace MaestroTool.Core;

/// <summary>
/// Subscription-related MCP tools. Part of the MaestroMcpTools class.
/// See MaestroMcpTools.cs for class declaration and helpers.
/// </summary>
public partial class MaestroMcpTools
{
    [McpServerTool(Name = "maestro_subscriptions", Title = "List Subscriptions", ReadOnly = true, Idempotent = true)]
    [Description("List Maestro subscriptions filtered by source/target repository and/or channel name. For health checks, use maestro_subscription_health. For details on a single subscription by ID, use maestro_subscription.")]
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
    [McpServerTool(Name = "maestro_subscription", Title = "Get Subscription", ReadOnly = true, Idempotent = true)]
    [Description("Get a specific Maestro subscription by its GUID ID, including health diagnostic comparing last applied build to latest available. For batch health checks across a repository, use maestro_subscription_health instead.")]
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
    [McpServerTool(Name = "maestro_subscription_health", Title = "Subscription Health", ReadOnly = true, Idempotent = true)]
    [Description("Check subscription health for a target repository. For each active subscription, compares the last applied build against the latest available build on the channel to detect stale subscriptions. Start here for most investigations. For listing/filtering subscriptions, use maestro_subscriptions.")]
    public async Task<string> GetSubscriptionHealth(
        [Description("Target repository URL (e.g. https://github.com/dotnet/dotnet)")] string targetRepository,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        [Description("Include recent commit details (SHA, message, author, date) for stale subscriptions")] bool includeCommitDetails = false,
        [Description("Cross-validate stale subscriptions against GitHub ground truth (PR activity, commit reachability). Slower but detects stuck bookkeeping.")] bool validate = false,
        CancellationToken cancellationToken = default)
    {
        var results = await _service.GetSubscriptionHealthAsync(targetRepository, noCache, includeCommitDetails, validate, cancellationToken);

        if (results.Count == 0)
            return $"No active subscriptions found targeting {targetRepository}.";

        var sb = new StringBuilder();
        var staleCount = results.Count(r => r.IsStale);
        sb.AppendLine($"Subscription health for **{targetRepository}**: {results.Count} subscription(s), {staleCount} stale\n");

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

            sb.AppendLine($"**{r.SourceRepository}** → {r.TargetBranch}");
            sb.AppendLine($"  Channel: {r.ChannelName} | Status: {status}");
            if (r.Error != null)
                sb.AppendLine($"  ⚠️ Error: {r.Error}");
            if (r.LastAppliedBuildId != null)
                sb.AppendLine($"  Last Applied: #{r.LastAppliedBuildId} ({r.LastAppliedDate:u})");
            if (r.LatestBuildId != null)
                sb.AppendLine($"  Latest Available: #{r.LatestBuildId} ({r.LatestBuildDate:u})");

            if (r.RecentCommits is { Count: > 0 })
            {
                var totalCommits = r.CommitsBehind ?? r.RecentCommits.Count;
                var showing = Math.Min(r.RecentCommits.Count, 10);
                sb.AppendLine($"  Recent commits (showing {showing} of {totalCommits}):");
                foreach (var c in r.RecentCommits.Take(10))
                {
                    sb.AppendLine($"    `{c.Sha}` {c.Message} ({c.Author}, {c.Date:yyyy-MM-dd})");
                }
            }

            if (r.Validation != null)
            {
                sb.AppendLine("  🔍 Cross-validation:");
                sb.AppendLine($"    {(r.Validation.CommitReachable ? "✅" : "❌")} Commit reachable: {(r.Validation.CommitReachable ? "Yes" : "No")}");
                if (r.Validation.MergedPrsSinceLastApplied > 0)
                {
                    var prRefs = r.Validation.MergedPrUrls != null
                        ? string.Join(", ", r.Validation.MergedPrUrls.Select(u => $"#{u.Split('/').Last()}"))
                        : "";
                    sb.AppendLine($"    ⚠️ PR activity: {r.Validation.MergedPrsSinceLastApplied} PR(s) merged since last applied ({prRefs})");
                }
                else
                {
                    sb.AppendLine("    ✅ PR activity: No merged PRs found since last applied");
                }
                if (r.Validation.BookkeepingAnomalyDetected)
                {
                    sb.AppendLine($"    → Bookkeeping appears STUCK — {r.Validation.AnomalyReason}");
                }
            }

            if (r.Oscillation != null)
            {
                var timespan = r.Oscillation.LastSeen.HasValue && r.Oscillation.FirstSeen.HasValue
                    ? $" over {(r.Oscillation.LastSeen.Value - r.Oscillation.FirstSeen.Value).TotalHours:F1}h"
                    : "";
                sb.AppendLine($"  🔄 State oscillation detected: {r.Oscillation.OscillationCount} cycles of {r.Oscillation.State1} ↔ {r.Oscillation.State2}{timespan}");
                sb.AppendLine($"     → Subscription likely stuck (arcade-services#6090)");
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
                sb.AppendLine($"  {stateEmoji} Tracked PR: {stateLabel}");
                if (r.TrackedPr.PrUrl != null)
                    sb.AppendLine($"     {r.TrackedPr.PrUrl}");
            }

            if (r.VmrConsumedCommit != null)
            {
                var shortSha = r.VmrConsumedCommit.Length > 7 ? r.VmrConsumedCommit[..7] : r.VmrConsumedCommit;
                var dateStr = r.VmrConsumedDate.HasValue ? $" ({r.VmrConsumedDate.Value:u})" : "";
                sb.AppendLine($"  📌 VMR consumed: {shortSha}{dateStr} — actual code in dotnet/dotnet");
            }

            sb.AppendLine();
        }

        return Timestamp(noCache) + sb.ToString();
    }
    [McpServerTool(Name = "maestro_trigger_subscription", Title = "Trigger Subscription", Idempotent = true)]
    [Description("Trigger a Maestro subscription. Provide buildId directly, or provide sourceRepository and channelName to auto-resolve the latest build. Use force=true to force-trigger (overwrites existing PR branch) for stale backflow PR remediation.")]
    public async Task<string> TriggerSubscription(
        [Description("The subscription GUID to trigger")] string subscriptionId,
        [Description("BAR build ID. If omitted, the latest build is resolved from sourceRepository and channelName.")] int? buildId = null,
        [Description("Source repository URL to resolve latest build (e.g. 'https://github.com/dotnet/runtime'). Required if buildId is omitted.")] string? sourceRepository = null,
        [Description("Channel name to resolve latest build (e.g. '.NET 10.0.1xx SDK'). Required if buildId is omitted.")] string? channelName = null,
        [Description("Force trigger (overwrites existing PR branch). Use for stale backflow PR remediation.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(subscriptionId, out var id))
            return "Invalid subscription ID format. Expected a GUID.";

        int resolvedBuildId;
        string? resolvedInfo = null;

        if (buildId.HasValue)
        {
            resolvedBuildId = buildId.Value;
        }
        else
        {
            if (string.IsNullOrEmpty(sourceRepository) || string.IsNullOrEmpty(channelName))
                return "Both sourceRepository and channelName are required when buildId is not provided.";

            var channel = await _service.GetChannelByNameAsync(channelName, noCache: false, cancellationToken);
            if (channel == null)
                return $"Channel '{channelName}' not found. Use maestro_channels to list available channels.";

            var latestBuild = await _service.GetLatestBuildAsync(sourceRepository, channel.Id, noCache: true, cancellationToken);
            if (latestBuild == null)
                return $"No build found for {sourceRepository} on channel '{channelName}'.";

            resolvedBuildId = latestBuild.Id;
            var commitShort = latestBuild.Commit?.Length >= 7 ? latestBuild.Commit[..7] : latestBuild.Commit ?? "N/A";
            resolvedInfo = $"Auto-resolved build #{resolvedBuildId} ({commitShort}, {latestBuild.DateProduced:u}) from {sourceRepository} on '{channelName}'";
        }

        var dedupKey = $"action:trigger-sub:{subscriptionId}:{resolvedBuildId}:{force}";
        var recent = _cache.GetRecentAction(dedupKey);
        if (recent.HasValue)
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] Trigger: TriggerSubscription dedup-skipped args=(subscriptionId={subscriptionId}, buildId={resolvedBuildId}, force={force}) lastTriggered={recent.Value:O}");
            return $"⏳ This subscription was already triggered for build #{resolvedBuildId}{(force ? " (force)" : "")} at {recent.Value:u}. Skipping duplicate.";
        }

        try
        {
            var result = await _service.TriggerSubscriptionAsync(id, resolvedBuildId, force, cancellationToken);
            _cache.RecordAction(dedupKey, ActionCooldown);

            var sb = new StringBuilder();
            if (resolvedInfo != null)
                sb.AppendLine($"ℹ️ {resolvedInfo}");
            sb.AppendLine($"✅ Successfully {(force ? "force-" : "")}triggered subscription {subscriptionId} for build #{resolvedBuildId}");
            sb.AppendLine($"\nSubscription: **{result.SourceRepository}** → **{result.TargetRepository}** ({result.TargetBranch})");
            sb.AppendLine($"Channel: {result.Channel?.Name ?? "N/A"}");
            if (force)
                sb.AppendLine($"\n⚡ Force mode: existing PR branch will be overwritten with fresh VMR content.");
            sb.AppendLine($"\nThe subscription will now process build #{resolvedBuildId} and create/update a dependency update PR if needed.");

            return sb.ToString();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Authentication required"))
        {
            return $"🔒 {ex.Message}";
        }
    }
    [McpServerTool(Name = "maestro_subscription_history", Title = "Subscription History", ReadOnly = true, Idempotent = true)]
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
}
