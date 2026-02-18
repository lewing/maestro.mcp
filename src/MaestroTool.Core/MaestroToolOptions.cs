namespace MaestroTool.Core;

/// <summary>
/// Configuration options for Maestro MCP tool behavior.
/// </summary>
public class MaestroToolOptions
{
    /// <summary>
    /// When true, destructive actions (delete subscription, remove default channel, etc.) are enabled.
    /// Default: false. Set via MAESTRO_ENABLE_DESTRUCTIVE_ACTIONS=true env var.
    /// </summary>
    public bool EnableDestructiveActions { get; set; } = false;
}
