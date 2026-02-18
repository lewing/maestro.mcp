# maestro.mcp — MCP server for Maestro/BAR dependency flow data

An MCP server that provides cached access to [Maestro/BAR](https://maestro.dot.net) (Build Asset Registry) data for the .NET build infrastructure. Exposes 10 tools for querying subscriptions, builds, channels, and health status, plus triggering subscription updates via the Model Context Protocol.

Built with [Squad](https://github.com/bradygaster/squad) — [meet the squad](.ai-team/SQUAD.md).

## Installation

### Run with dnx (no install needed)

`dnx` (new in .NET 10) auto-downloads and runs NuGet tool packages — no install step required:

```bash
dnx lewing.maestro.mcp
```

This is the recommended approach for MCP server configuration (see below). MCP stdio mode is the default.

### Install as Global Tool

```bash
dotnet tool install -g lewing.maestro.mcp
```

For repo-local installation via a [tool manifest](https://learn.microsoft.com/dotnet/core/tools/local-tools-how-to-use):

```bash
dotnet new tool-manifest   # if .config/dotnet-tools.json doesn't exist
dotnet tool install --local lewing.maestro.mcp
```

### Install from Local Build

```bash
dotnet pack src/MaestroTool
dotnet tool install -g --add-source src/MaestroTool/nupkg lewing.maestro.mcp
```

After installation, `mstro` is available globally.

### Build from Source

```bash
# Prerequisites: .NET 10 SDK
git clone https://github.com/lewing/maestro.mcp.git
cd maestro.mcp
dotnet build
```

> **NuGet feed requirement:** The `Microsoft.DotNet.ProductConstructionService.Client` package is published to the
> [dotnet-eng](https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/index.json)
> Azure Artifacts feed. The included `nuget.config` references this feed.

## MCP Configuration

Add the following to your MCP client config. The `--yes` flag ensures `dnx` doesn't prompt for confirmation:

```json
{
  "servers": {
    "maestro": {
      "type": "stdio",
      "command": "dnx",
      "args": ["lewing.maestro.mcp", "--yes"]
    }
  }
}
```

> If you've installed `lewing.maestro.mcp` as a global tool, you can use `"command": "mstro"` with `"args": []` instead of `dnx`.

### With authentication

```json
{
  "servers": {
    "maestro": {
      "type": "stdio",
      "command": "dnx",
      "args": ["lewing.maestro.mcp", "--yes"],
      "env": {
        "MAESTRO_BAR_TOKEN": "your-token-here"
      }
    }
  }
}
```

### HTTP server (alternative)

```bash
dotnet run --project src/MaestroTool.Mcp
```

The HTTP server listens on **http://localhost:5000** by default.

### Config file locations

| Client | Config file | Top-level key |
|--------|------------|---------------|
| **GitHub Copilot CLI** | `~/.copilot/mcp.json` | `servers` |
| **VS Code / GitHub Copilot** | `.vscode/mcp.json` | `servers` |
| **Claude Desktop** (macOS) | `~/Library/Application Support/Claude/claude_desktop_config.json` | `mcpServers` |
| **Claude Desktop** (Windows) | `%APPDATA%\Claude\claude_desktop_config.json` | `mcpServers` |
| **Claude Code / Cursor** | `.cursor/mcp.json` | `mcpServers` |

## Authentication

The server implements a **3-tier authentication cascade**:

1. **Explicit PAT** — Set `MAESTRO_BAR_TOKEN` environment variable
2. **Cached Entra ID** — Reuses credentials from `darc authenticate` (`~/.darc/.auth-record-*`)
3. **Anonymous** — Read-only fallback (may be rate-limited)

**Recommended**: Run `darc authenticate` once (from [arcade-services](https://github.com/dotnet/arcade-services)) to cache credentials, then rely on automatic Entra ID authentication.

## Action Tools

The server includes **action tools** for triggering subscription updates. These are non-destructive operations — they trigger processing but don't delete or modify subscription configuration.

### Deduplication

Action tools include built-in deduplication with a 2-minute cooldown. If the same action is triggered again within the cooldown window, the server returns a notice instead of re-executing. This prevents duplicate triggers from LLM retries or multiple concurrent skills.

### Destructive Actions (future)

Future versions may include destructive actions (delete subscription, remove default channel, etc.). These will be **disabled by default** and require an explicit opt-in:

```json
{
  "servers": {
    "maestro": {
      "type": "stdio",
      "command": "dnx",
      "args": ["lewing.maestro.mcp", "--yes"],
      "env": {
        "MAESTRO_ENABLE_DESTRUCTIVE_ACTIONS": "true"
      }
    }
  }
}
```

## Available Tools

The server registers **10 MCP tools** for querying and triggering Maestro/BAR operations:

| Tool Name | Description | Key Parameters |
|-----------|-------------|-----------------|
| `maestro_subscriptions` | List all subscriptions | `sourceRepository` (optional): filter by source repo |
| `maestro_subscription` | Get a single subscription by ID | `subscriptionId`: UUID of the subscription |
| `maestro_latest_build` | Get the latest build from a channel | `channelId`: channel ID; `sourceRepository`: source repo URL |
| `maestro_build` | Get a build by ID | `buildId`: build ID |
| `maestro_channels` | List all channels | None |
| `maestro_default_channels` | Get default channels for a repository | `repository`: source repository URL |
| `maestro_subscription_health` | Get health status of a subscription (awaiting build, failed, etc.) | `subscriptionId`: UUID; includes freshness, branch, and error details |
| `maestro_build_freshness` | Check how long since a source repository branch was built | `sourceRepository`: source repo URL; `branch`: branch name (e.g., `refs/heads/main`) |
| `maestro_trigger_subscription` | Trigger a subscription to process a specific build | `subscriptionId`: UUID; `buildId`: BAR build ID |
| `maestro_trigger_daily_update` | Trigger all daily-update subscriptions | None |

## Architecture

The project is split into three layers:

```
src/
├── MaestroTool/              # CLI tool + stdio MCP server (dotnet tool)
│   └── Program.cs
├── MaestroTool.Core/         # Shared library — Maestro API logic + MCP tool definitions
│   ├── MaestroMcpTools.cs    # MCP tool definitions ([McpServerToolType])
│   ├── MaestroService.cs     # Cached business logic
│   ├── CacheService.cs       # In-memory TTL cache
│   ├── IMaestroApiClient.cs  # API abstraction
│   └── MaestroApiClient.cs   # PCS client wrapper with auth cascade
├── MaestroTool.Mcp/          # MCP HTTP server (ASP.NET Core)
│   └── Program.cs
└── MaestroTool.Tests/        # Unit tests (35 tests)
```

- **MaestroTool** — stdio MCP server packaged as a [dotnet tool](https://learn.microsoft.com/dotnet/core/tools/global-tools). Default entry point for `dnx` / `dotnet tool` usage.
- **MaestroTool.Mcp** — HTTP MCP server for remote/shared deployments.
- **MaestroTool.Core** — All business logic, caching, API client, and MCP tool definitions. Shared by both hosts.

## Cache TTLs

The `CacheService` respects the following cache durations to balance freshness and performance:

| Data Type | TTL | Reason |
|-----------|-----|--------|
| Subscriptions (list) | 5 minutes | Subscriptions change infrequently |
| Latest builds (by channel/repo) | 5 minutes | Builds are published frequently |
| Channels (list) | 15 minutes | Channels are rarely added/removed |
| Build by ID | 30 minutes | Builds are immutable once published |
| Build freshness (derived) | 10 minutes | Computed; cached to avoid repeated API calls |

TTLs are configurable in `CacheService` if stricter freshness is needed.

## Testing

Run the test suite:

```bash
dotnet test
```

The test suite includes:
- **35 unit tests** covering `CacheService` and `MaestroService` behavior.
- **Framework**: xUnit + NSubstitute for mocking.
- **Coverage**: cache hit/miss, TTL expiration, null handling, error scenarios.

Tests are located in `src/MaestroTool.Tests/`.

## License

MIT

---

## Contributing

For questions or issues, please open an issue in the repository. When modifying the authentication logic or adding new tools, ensure all tests pass and update this README accordingly.
