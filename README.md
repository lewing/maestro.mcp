# maestro.mcp

An MCP server providing cached access to Maestro/BAR (Build Asset Registry) data for the .NET build infrastructure. It exposes 8 tools for querying subscriptions, builds, channels, and health status via the Model Context Protocol.

## Prerequisites

- **.NET 10 SDK** or later (targets `net10.0`)
- **For authenticated access**: Run `darc authenticate` first (from [arcade-services](https://github.com/dotnet/arcade-services)). The server reuses cached Entra ID credentials from `~/.darc/.auth-record-*`.
- **Alternative**: Set the `MAESTRO_BAR_TOKEN` environment variable to use a Personal Access Token directly.

## Building

```bash
dotnet build
dotnet test
```

## Running

Start the MCP server:

```bash
dotnet run --project src/MaestroTool.Mcp
```

The server listens on **http://localhost:5000** by default.

## Configuration for Copilot/MCP Clients

Add this to your MCP configuration file (e.g., `.copilot/mcp-config.json` or your Copilot client's settings):

```json
{
  "mcpServers": {
    "maestro": {
      "command": "dotnet",
      "args": ["run", "--project", "D:\\path\\to\\src\\MaestroTool.Mcp"],
      "env": {}
    }
  }
}
```

Replace `D:\path\to\` with the actual path to the maestro.mcp repository root.

## Authentication

The server implements a **3-tier authentication cascade**:

1. **Explicit PAT via environment variable** (`MAESTRO_BAR_TOKEN`)
   - If set, uses this Personal Access Token for authentication.
   - Highest priority; no other auth methods are attempted.

2. **Entra ID via cached darc credentials**
   - If `MAESTRO_BAR_TOKEN` is not set and `~/.darc/.auth-record-*` exists, the server attempts to authenticate using cached MSAL credentials from a prior `darc authenticate` call.
   - Provides silent, automatic authentication without user interaction.
   - Falls back to anonymous if credential creation fails.

3. **Anonymous fallback**
   - If neither of the above is available, the server operates anonymously (read-only).
   - Access may be rate-limited; useful for testing or public queries only.

**Recommended**: Run `darc authenticate` once to cache credentials, then rely on automatic Entra ID authentication.

## Available Tools

The server registers **8 MCP tools** for querying Maestro/BAR data:

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

## Architecture

The server is organized into three layers:

### Data Layer: **MaestroApiClient**
- Wraps the PCS NuGet client (`Microsoft.Dot.Arcade.Services.Core`) for Maestro API access.
- Implements the 3-tier authentication cascade (PAT → cached Entra ID → anonymous).
- All requests flow through this client, ensuring consistent auth behavior.

### Caching Layer: **CacheService**
- In-memory TTL cache using `ConcurrentDictionary<string, CacheEntry<T>>`.
- Prevents repeated API calls; respects configurable TTLs per data type (see [Cache TTLs](#cache-ttls)).
- Thread-safe; no locks required.

### Business Logic Layer: **MaestroService**
- Orchestrates cached data queries and derived calculations.
- `GetSubscriptionHealthAsync()`: fetches subscription + latest build, computes health status and time-since-build.
- `GetBuildFreshnessAsync()`: fetches latest build for a source repo/branch, computes age in minutes.

### MCP Layer: **MaestroMcpTools**
- Defines the 8 MCP tool methods via `[McpServerTool]` attributes.
- Each tool parses arguments, calls `MaestroService`, and returns structured JSON.

### Hosting: **Program.cs**
- ASP.NET Core host with OpenAI MCP HTTP transport.
- Dependency injection: registers `MaestroApiClient`, `CacheService`, `MaestroService`, and MCP tools.
- JSON configuration for MCP server metadata (name, version, description).

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
