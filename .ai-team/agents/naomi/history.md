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

### Conventions
- Diagnostic output goes to `Console.Error.WriteLine` with `[maestro-mcp]` prefix to avoid interfering with MCP stdio transport
- Auth method is logged at startup for troubleshooting
