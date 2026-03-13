using System.ComponentModel;
using System.Text;
using Microsoft.DotNet.ProductConstructionService.Client.Models;
using ModelContextProtocol.Server;

namespace MaestroTool.Core;

/// <summary>
/// Build-related MCP tools. Part of the MaestroMcpTools class.
/// See MaestroMcpTools.cs for class declaration and helpers.
/// </summary>
public partial class MaestroMcpTools
{
    [McpServerTool(Name = "maestro_latest_build", Title = "Latest Build", ReadOnly = true, Idempotent = true)]
    [Description("Get the latest build for a repository, optionally filtered by channel name.")]
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
    [McpServerTool(Name = "maestro_build", Title = "Get Build", ReadOnly = true, Idempotent = true)]
    [Description("Get a specific build by its BAR build ID. For listing/filtering builds, use maestro_builds.")]
    public async Task<string> GetBuild(
        [Description("The BAR build ID (integer)")] int buildId,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var build = await _service.GetBuildAsync(buildId, noCache, cancellationToken);
        return Timestamp(noCache) + FormatBuild(build);
    }
    [McpServerTool(Name = "maestro_builds", Title = "List Builds", ReadOnly = true, Idempotent = true)]
    [Description("List builds, optionally filtered by repository, channel, commit, or build number.")]
    public async Task<string> ListBuilds(
        [Description("Filter by repository URL (e.g. https://github.com/dotnet/runtime)")] string? repository = null,
        [Description("Filter by channel name (e.g. '.NET 11.0.1xx SDK')")] string? channelName = null,
        [Description("Filter by commit SHA")] string? commit = null,
        [Description("Filter by build number")] string? buildNumber = null,
        [Description("Maximum number of builds to return (default: 20)")] int? count = null,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        int? channelId = null;
        if (!string.IsNullOrEmpty(channelName))
        {
            var ch = await _service.GetChannelByNameAsync(channelName, noCache, cancellationToken);
            if (ch == null)
                return $"Channel '{channelName}' not found. Use maestro_channels to list available channels.";
            channelId = ch.Id;
        }

        var builds = await _service.ListBuildsAsync(repository, channelId, commit, buildNumber, count, noCache, cancellationToken);

        if (builds.Count == 0)
            return "No builds found matching the specified filters.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {builds.Count} build(s):\n");

        foreach (var build in builds)
        {
            sb.AppendLine(FormatBuild(build));
            sb.AppendLine("---");
        }

        return Timestamp(noCache) + sb.ToString();
    }
    [McpServerTool(Name = "maestro_build_freshness", Title = "Build Freshness", ReadOnly = true, Idempotent = true)]
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
    [McpServerTool(Name = "maestro_build_graph", Title = "Build Graph", ReadOnly = true, Idempotent = true)]
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
}
