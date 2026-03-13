using System.ComponentModel;
using System.Text;
using Microsoft.DotNet.ProductConstructionService.Client.Models;
using ModelContextProtocol.Server;

namespace MaestroTool.Core;

/// <summary>
/// Channel-related MCP tools. Part of the MaestroMcpTools class.
/// See MaestroMcpTools.cs for class declaration and helpers.
/// </summary>
public partial class MaestroMcpTools
{
    [McpServerTool(Name = "maestro_channel", Title = "Get Channel", ReadOnly = true, Idempotent = true)]
    [Description("Get a specific channel by ID or name. For listing all channels, use maestro_channels.")]
    public async Task<string> GetChannel(
        [Description("Channel ID (integer) or channel name (e.g. '.NET 10.0.1xx SDK')")] string channelId,
        [Description("Bypass cache and fetch fresh data")] bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            return "Channel ID or name is required.";

        Channel channel;
        if (int.TryParse(channelId, out var parsedId))
        {
            if (parsedId < 0)
                return $"Invalid channel ID '{channelId}'. Channel IDs must be non-negative integers.";

            try
            {
                channel = await _service.GetChannelAsync(parsedId, noCache, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"Channel with ID {parsedId} not found.";
            }
        }
        else
        {
            var found = await _service.GetChannelByNameAsync(channelId, noCache, cancellationToken);
            if (found == null)
                return $"Channel '{channelId}' not found. Use maestro_channels to list available channels.";
            channel = found;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"**{channel.Name}** (ID: {channel.Id})");
        sb.AppendLine($"Classification: {channel.Classification}");
        return Timestamp(noCache) + sb.ToString();
    }

    [McpServerTool(Name = "maestro_channels", Title = "List Channels", ReadOnly = true, Idempotent = true)]
    [Description("List all Maestro channels.")]
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

    [McpServerTool(Name = "maestro_default_channels", Title = "Default Channels", ReadOnly = true, Idempotent = true)]
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
}
