using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MaestroTool.Core;

/// <summary>
/// Utility MCP tools. Part of the MaestroMcpTools class.
/// See MaestroMcpTools.cs for class declaration and helpers.
/// </summary>
public partial class MaestroMcpTools
{
    [McpServerTool(Name = "maestro_clear_cache", Title = "Clear Cache", Destructive = true, Idempotent = true)]
    [Description("Clear all cached Maestro data (shared across all mstro instances). Use after performing actions or when you need guaranteed fresh data from all tools.")]
    public string ClearCache()
    {
        _cache.Clear();
        return "✅ Cache cleared. All subsequent tool calls will fetch fresh data from the Maestro API.";
    }
}
