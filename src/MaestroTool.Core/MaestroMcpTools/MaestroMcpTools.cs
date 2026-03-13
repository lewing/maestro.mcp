using System.ComponentModel;
using System.Text;
using Microsoft.DotNet.ProductConstructionService.Client;
using Microsoft.DotNet.ProductConstructionService.Client.Models;
using ModelContextProtocol.Server;

namespace MaestroTool.Core;

/// <summary>
/// Main MCP tool class for Maestro operations. This class is split across multiple partial files:
/// - MaestroMcpTools.Channels.cs (channel operations)
/// - MaestroMcpTools.Subscriptions.cs (subscription operations)
/// - MaestroMcpTools.Builds.cs (build operations)
/// - MaestroMcpTools.Codeflow.cs (codeflow operations)
/// - MaestroMcpTools.Utilities.cs (utility operations)
/// </summary>
[McpServerToolType]
public partial class MaestroMcpTools
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
}
