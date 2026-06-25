using System.ComponentModel;
using System.Text;
using Microsoft.DotNet.ProductConstructionService.Client;
using Microsoft.DotNet.ProductConstructionService.Client.Models;
using ModelContextProtocol.Server;

namespace MaestroTool.Core;

/// <summary>
/// Codeflow-related MCP tools. Part of the MaestroMcpTools class.
/// See MaestroMcpTools.cs for class declaration and helpers.
/// </summary>
public partial class MaestroMcpTools
{
    [McpServerTool(Name = "maestro_trigger_daily_update", Title = "Trigger Daily Update", Idempotent = true)]
    [Description("Trigger all daily-update subscriptions. Not for single subscriptions; use maestro_trigger_subscription for targeted triggers.")]
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
    [McpServerTool(Name = "maestro_codeflow_prs", Title = "Codeflow PRs", ReadOnly = true, Idempotent = true)]
    [Description("List active codeflow PRs managed by Maestro. For per-mapping health status, use maestro_codeflow_statuses.")]
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
    [McpServerTool(Name = "maestro_codeflow_pr", Title = "Tracked PR", ReadOnly = true, Idempotent = true)]
    [Description("Get the tracked pull request for a specific Maestro subscription.")]
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
    [McpServerTool(Name = "maestro_backflow_status", Title = "Backflow Status", ReadOnly = true, Idempotent = true)]
    [Description("Get backflow status for a VMR build. Requires a VMR build ID; use maestro_builds to find one.")]
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
    [McpServerTool(Name = "maestro_flow_graph", Title = "Flow Graph", ReadOnly = true, Idempotent = true)]
    [Description("Get the dependency flow graph for a channel.")]
    public async Task<string> GetFlowGraph(
        [Description("The channel ID to get the flow graph for")] int channelId,
        [Description("Number of days to include in the flow graph analysis (default: 3, max: 30)")] int days = 3,
        [Description("Include Arcade/tooling dependencies in the graph")] bool includeArcade = true,
        [Description("Include build time metrics; expensive, so enable only when expanding a scoped graph")] bool includeBuildTimes = false,
        [Description("Include disabled subscriptions in the graph")] bool includeDisabledSubscriptions = false,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        IProgress<ModelContextProtocol.ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ModelContextProtocol.ProgressNotificationValue
        {
            Progress = 0,
            Total = 2,
            Message = $"Computing flow graph (days={days}, includeArcade={includeArcade}, includeBuildTimes={includeBuildTimes})..."
        });
        
        if (days is < 1 or > 30)
            return $"Invalid days value '{days}'. Expected a value between 1 and 30.";

        FlowGraph graph;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            graph = await _service.GetFlowGraphAsync(days, channelId, includeArcade, includeBuildTimes, includeDisabledSubscriptions, null, noCache, cts.Token);
            
            progress?.Report(new ModelContextProtocol.ProgressNotificationValue
            {
                Progress = 1,
                Total = 2,
                Message = $"Resolving {graph.FlowRefs?.Count ?? 0} nodes and {graph.FlowEdges?.Count ?? 0} edges..."
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"⏱️ Flow graph request timed out after 30 seconds.\n\n" +
                   $"The flow graph for channel {channelId} with {days} days of data is too large to compute in time.\n\n" +
                   $"**Suggestions to reduce scope:**\n" +
                   $"- Use the default time window: days=3 (currently {days})\n" +
                   $"- Exclude Arcade dependencies: includeArcade=false\n" +
                   $"- Keep build time metrics disabled: includeBuildTimes=false";
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or TimeoutException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return $"⚠️ Flow graph request failed: {ex.Message}\n\n" +
                   $"**Suggestions to reduce scope:**\n" +
                   $"- Use the default time window: days=3 (currently {days})\n" +
                   $"- Exclude Arcade dependencies: includeArcade=false\n" +
                   $"- Keep build time metrics disabled: includeBuildTimes=false";
        }

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
        
        progress?.Report(new ModelContextProtocol.ProgressNotificationValue
        {
            Progress = 2,
            Total = 2,
            Message = "Flow graph complete."
        });

        return Timestamp(noCache) + sb.ToString();
    }
    [McpServerTool(Name = "maestro_codeflow_statuses", Title = "Codeflow Statuses", ReadOnly = true, Idempotent = true)]
    [Description("Get codeflow status for a repository and branch. Defaults to the VMR (dotnet/dotnet, main).")]
    public async Task<string> GetCodeflowStatuses(
        [Description("Repository URL (default: https://github.com/dotnet/dotnet)")] string repositoryUrl = "https://github.com/dotnet/dotnet",
        [Description("Branch name (default: main)")] string branch = "main",
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var statuses = await _service.GetCodeflowStatusesAsync(repositoryUrl, branch, noCache, cancellationToken);

        if (statuses.Count == 0)
            return $"No codeflow statuses found for {repositoryUrl} ({branch}).";

        var sb = new StringBuilder();
        sb.AppendLine($"**Codeflow Statuses for {repositoryUrl}** (branch: `{branch}`)\n");
        sb.AppendLine($"Found {statuses.Count} mapping(s):\n");

        foreach (var status in statuses)
        {
            sb.AppendLine($"### {status.MappingName ?? "Unknown Mapping"}");
            sb.AppendLine($"Repository: {status.RepositoryUrl} (`{status.RepositoryBranch}`)");
            sb.AppendLine();

            FormatFlowStatus(sb, "Forward Flow", status.ForwardFlow);
            FormatFlowStatus(sb, "Backflow", status.Backflow);
            sb.AppendLine("---");
        }

        return Timestamp(noCache) + sb.ToString();
    }

    private static void FormatFlowStatus(StringBuilder sb, string label, CodeflowSubscriptionStatus? flow)
    {
        if (flow == null)
        {
            sb.AppendLine($"**{label}:** _not configured_");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"**{label}:**");

        if (flow.Subscription != null)
        {
            var sub = flow.Subscription;
            sb.AppendLine($"  Subscription: `{sub.Id}`");
            sb.AppendLine($"  {sub.SourceRepository} → {sub.TargetRepository} (`{sub.TargetBranch}`)");
            sb.AppendLine($"  Channel: {sub.Channel?.Name ?? "N/A"} | Enabled: {sub.Enabled}");
            if (sub.LastAppliedBuild != null)
                sb.AppendLine($"  Last Applied Build: #{sub.LastAppliedBuild.Id} ({sub.LastAppliedBuild.DateProduced:u})");
        }
        else
        {
            sb.AppendLine($"  Subscription: _none_");
        }

        if (flow.ActivePullRequest != null)
        {
            var pr = flow.ActivePullRequest;
            sb.AppendLine($"  🔄 Active PR: {pr.Url}");
            sb.AppendLine($"    Last Update: {pr.LastUpdate:u}");
            if (pr.HeadBranch != null)
                sb.AppendLine($"    Head Branch: {pr.HeadBranch}");
        }

        if (flow.NewestBuildId.HasValue)
        {
            var lastAppliedId = flow.Subscription?.LastAppliedBuild?.Id;
            if (lastAppliedId.HasValue && lastAppliedId.Value < flow.NewestBuildId.Value)
                sb.AppendLine($"  ⚠️ Behind — newest build #{flow.NewestBuildId.Value} ({flow.NewestBuildDate:u}), last applied #{lastAppliedId.Value}");
            else if (lastAppliedId.HasValue)
                sb.AppendLine($"  ✅ Up to date (build #{flow.NewestBuildId.Value})");
            else
                sb.AppendLine($"  Newest Build: #{flow.NewestBuildId.Value} ({flow.NewestBuildDate:u})");
        }

        sb.AppendLine();
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
