# Naomi — History

## Learnings

### Auth cascade architecture (2025-07-14)
- `PcsApiFactory.GetAuthenticated(null, null, disableInteractiveAuth: false)` triggers the `AppCredentialResolver` path 4: `AppCredential.CreateUserCredential`, which uses `InteractiveBrowserCredential` with MSAL cache "maestro" and auth record from `~/.darc/`.
- **Critical safety guard**: Must check for auth record file existence before attempting Entra auth. Without the auth record, `AppCredential.GetInteractiveCredential` calls `credential.Authenticate()` which opens a browser — fatal for an MCP server subprocess.
- The PCS client NuGet (`Microsoft.DotNet.ProductConstructionService.Client`) transitively includes `Azure.Identity`, `Maestro.Common` (with `AppCredential`/`AppCredentialResolver`). No need to add explicit Azure.Identity package reference.
- Auth record path: `~/.darc/.auth-record-54c17f3d-7325-4eca-9db7-f090bfc765a8` (Maestro production app ID)
- MSAL cache name: `"maestro"` (shared with darc CLI)

### Key file paths
- `src/MaestroTool.Core/MaestroApiClient.cs` — API client with auth cascade
- `src/MaestroTool.Core/IMaestroApiClient.cs` — Interface definition
- `src/MaestroTool.Core/MaestroService.cs` — Cached business logic layer
- `src/MaestroTool.Core/MaestroMcpTools.cs` — MCP tool definitions
- `src/MaestroTool.Mcp/Program.cs` — Server entry point, DI setup

### End-to-end smoke test results (2025-07-14)
- **Bug found & fixed**: `MaestroMcpTools` was missing the `[McpServerToolType]` class attribute. Without it, `WithToolsFromAssembly()` can't discover instance-method tools — the server started but reported 0 tools and `tools/call` returned `-32601 Method not available`. Added the attribute; all 8 tools now register correctly.
- **Auth cascade works**: Server logs `[maestro-mcp] Auth: using Entra ID (cached darc credentials)` on first tool invocation. Auth is lazy — the `MaestroApiClient` singleton is constructed by DI at first use, not at startup.
- **All 8 tools verified**: `maestro_channels` (159 channels), `maestro_subscriptions` (filtered by dotnet/runtime, 8 results), `maestro_latest_build` (build #302353 for dotnet/runtime). All return real data from maestro.dot.net.
- **MCP HTTP+SSE transport**: Server listens on `http://localhost:5000`. Client connects to `/sse` (GET, long-lived SSE stream), receives session endpoint URL, then POSTs JSON-RPC messages to `/message?sessionId=<id>`. Responses arrive on the SSE stream. The `tools/list` response now includes `listChanged: true` capability.
- **Performance**: First tool call (channels) took ~1.6s including auth + API call. Subsequent calls (subscriptions, latest build) completed in 150-400ms thanks to the cache layer.
- **Caching confirmed**: The subscriptions call returned in 154ms, confirming the `CacheService` TTL cache is working for second-hit scenarios within the same session.

### Conventions
- Diagnostic output goes to `Console.Error.WriteLine` with `[maestro-mcp]` prefix to avoid interfering with MCP stdio transport
- Auth method is logged at startup for troubleshooting
- **Critical**: Tool classes must have `[McpServerToolType]` attribute for `WithToolsFromAssembly()` to discover instance-method tools. This is the pattern from the Helix reference implementation.

### Decision: [McpServerToolType] attribute required (2025-07-14)
- Smoke test revealed all 8 tools were registering as 0 tools due to missing `[McpServerToolType]` attribute on `MaestroMcpTools` class
- Fix applied; verified all tools now appear in tool list and respond to `tools/call`
- This decision affects **Backend Dev workflow**: Any MCP tool class added to the project must include this attribute

📌 Team update (2026-02-18): README.md created for maestro.mcp covering authentication, tools, architecture, and cache strategy — decided by Alex

### Action tools implementation (2026-02-18)
- PCS client's `TriggerSubscriptionAsync` has signature `(int barBuildId, bool isCoherencyUpdate, Guid subscriptionId, CancellationToken)` — the bool parameter is required and appears to control coherency mode. Passed `true` for standard trigger behavior.
- Action deduplication pattern: `CacheService.GetRecentAction(key)` checks for recent execution timestamp within cooldown window; `RecordAction(key, cooldown)` stores timestamp for duplicate prevention. Actions invalidate related read caches after success.
- Service layer acts as pass-through for actions but invalidates relevant cached reads after mutation to prevent stale data.
- `MaestroToolOptions` wired into DI container with `MAESTRO_ENABLE_DESTRUCTIVE_ACTIONS` env var support, ready for future destructive tools (delete subscription, etc.). For v0.2.0 only non-destructive trigger tools are exposed.
- Action tools enforce 2-minute cooldown to prevent accidental duplicate triggers.

