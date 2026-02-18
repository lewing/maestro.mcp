using System.ComponentModel;
using System.Text;
using Microsoft.DotNet.ProductConstructionService.Client.Models;
using ModelContextProtocol.Server;

namespace MaestroTool.Core;

[McpServerToolType]
public class MaestroMcpTools
{
    private readonly MaestroService _service;

    public MaestroMcpTools(MaestroService service)
    {
        _service = service;
    }

    [McpServerTool(Name = "maestro_subscriptions")]
    [Description("List Maestro subscriptions filtered by source/target repository and/or channel name. Returns subscription ID, source/target repo, channel, target branch, and enabled status.")]
    public async Task<string> GetSubscriptions(
        [Description("Filter by source repository URL (e.g. https://github.com/dotnet/runtime)")] string? sourceRepository = null,
        [Description("Filter by target repository URL (e.g. https://github.com/dotnet/dotnet)")] string? targetRepository = null,
        [Description("Filter by channel name (e.g. '.NET 10.0.1xx SDK')")] string? channelName = null,
        CancellationToken cancellationToken = default)
    {
        int? channelId = null;
        if (!string.IsNullOrEmpty(channelName))
        {
            var channel = await _service.GetChannelByNameAsync(channelName, cancellationToken);
            channelId = channel?.Id;
            if (channelId == null)
                return $"Channel '{channelName}' not found.";
        }

        var subs = await _service.GetSubscriptionsAsync(sourceRepository, targetRepository, channelId, cancellationToken);

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

        return sb.ToString();
    }

    [McpServerTool(Name = "maestro_subscription")]
    [Description("Get a specific Maestro subscription by its GUID ID, including health diagnostic comparing last applied build to latest available.")]
    public async Task<string> GetSubscription(
        [Description("The subscription GUID")] string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(subscriptionId, out var id))
            return "Invalid subscription ID format. Expected a GUID.";

        var sub = await _service.GetSubscriptionAsync(id, cancellationToken);
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
            var latest = await _service.GetLatestBuildAsync(sub.SourceRepository, sub.Channel.Id, cancellationToken);
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

        return sb.ToString();
    }

    [McpServerTool(Name = "maestro_latest_build")]
    [Description("Get the latest build for a repository, optionally filtered by channel name. Returns build ID, commit, date, and channel info.")]
    public async Task<string> GetLatestBuild(
        [Description("Repository URL (e.g. https://github.com/dotnet/runtime)")] string repository,
        [Description("Optional channel name filter")] string? channelName = null,
        CancellationToken cancellationToken = default)
    {
        int? channelId = null;
        if (!string.IsNullOrEmpty(channelName))
        {
            var channel = await _service.GetChannelByNameAsync(channelName, cancellationToken);
            channelId = channel?.Id;
            if (channelId == null)
                return $"Channel '{channelName}' not found.";
        }

        var build = await _service.GetLatestBuildAsync(repository, channelId, cancellationToken);
        if (build == null)
            return $"No builds found for {repository}" + (channelName != null ? $" on channel '{channelName}'" : "") + ".";

        return FormatBuild(build);
    }

    [McpServerTool(Name = "maestro_build")]
    [Description("Get a specific build by its BAR build ID. Returns build details including commit, date, channels, and repository.")]
    public async Task<string> GetBuild(
        [Description("The BAR build ID (integer)")] int buildId,
        CancellationToken cancellationToken = default)
    {
        var build = await _service.GetBuildAsync(buildId, cancellationToken);
        return FormatBuild(build);
    }

    [McpServerTool(Name = "maestro_channels")]
    [Description("List all Maestro channels. Returns channel names and IDs.")]
    public async Task<string> GetChannels(CancellationToken cancellationToken = default)
    {
        var channels = await _service.GetChannelsAsync(cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine($"Found {channels.Count} channel(s):\n");

        foreach (var ch in channels.OrderBy(c => c.Name))
        {
            sb.AppendLine($"- **{ch.Name}** (ID: {ch.Id})");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "maestro_default_channels")]
    [Description("List default channel mappings (repo/branch → channel auto-assignment). Optionally filter by repository URL or branch.")]
    public async Task<string> GetDefaultChannels(
        [Description("Filter by repository URL")] string? repository = null,
        [Description("Filter by branch name")] string? branch = null,
        CancellationToken cancellationToken = default)
    {
        var defaults = await _service.GetDefaultChannelsAsync(repository, branch, cancellationToken);

        if (defaults.Count == 0)
            return "No default channel mappings found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {defaults.Count} default channel mapping(s):\n");

        foreach (var dc in defaults)
        {
            sb.AppendLine($"- **{dc.Repository}** ({dc.Branch}) → {dc.Channel?.Name ?? "N/A"}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "maestro_subscription_health")]
    [Description("Check subscription health for a target repository. For each active subscription, compares the last applied build against the latest available build on the channel to detect stale subscriptions.")]
    public async Task<string> GetSubscriptionHealth(
        [Description("Target repository URL (e.g. https://github.com/dotnet/dotnet)")] string targetRepository,
        CancellationToken cancellationToken = default)
    {
        var results = await _service.GetSubscriptionHealthAsync(targetRepository, cancellationToken);

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
            if (r.LastAppliedBuildId != null)
                sb.AppendLine($"  Last Applied: #{r.LastAppliedBuildId} ({r.LastAppliedDate:u})");
            if (r.LatestBuildId != null)
                sb.AppendLine($"  Latest Available: #{r.LatestBuildId} ({r.LatestBuildDate:u})");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "maestro_build_freshness")]
    [Description("Check build freshness for a channel by resolving aka.ms redirect URLs and checking the Last-Modified header of the published build artifacts.")]
    public async Task<string> GetBuildFreshness(
        [Description("Channel short name for aka.ms URL (e.g. '10.0.1xx', '9.0.1xx')")] string channel,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetBuildFreshnessAsync(channel, cancellationToken);

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

        return sb.ToString();
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
