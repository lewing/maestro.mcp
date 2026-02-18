# Decisions

Team decisions are recorded here. Append-only — never edit existing entries.

## Auth cascade for MaestroApiClient

**Author:** Naomi (Backend Dev)  
**Date:** 2025-07-14  
**Status:** Implemented

### Context

Users who have already run `darc authenticate` have a cached MSAL token and auth record on disk (`~/.darc/.auth-record-<appId>`). The MCP server should reuse these credentials silently without requiring users to set environment variables.

### Decision

Implement a 3-tier auth cascade in `MaestroApiClient.CreateApi()`:

1. **MAESTRO_BAR_TOKEN** env var → `PcsApiFactory.GetAuthenticated(token, null, disableInteractiveAuth: true)`
2. **Entra ID cached credentials** → Only if `~/.darc/.auth-record-54c17f3d-7325-4eca-9db7-f090bfc765a8` exists, call `PcsApiFactory.GetAuthenticated(null, null, disableInteractiveAuth: false)`. This uses `InteractiveBrowserCredential` with the MSAL token cache named "maestro" and the auth record, providing silent token acquisition.
3. **Anonymous fallback** → `PcsApiFactory.GetAnonymous()` for read-only access.

### Key design choices

- **Guard on auth record file existence**: Before attempting Entra auth, we check if `~/.darc/.auth-record-<appId>` exists. Without this guard, `AppCredential.CreateUserCredential` would call `credential.Authenticate()` which opens a browser — unacceptable for an MCP server running as a subprocess.
- **`disableInteractiveAuth: false`**: Required so `AppCredentialResolver` takes the `InteractiveBrowserCredential` path (step 4 in the resolver) rather than `AzureCliCredential` (step 3). The browser popup is prevented by the auth record + MSAL cache being present.
- **No direct Azure.Identity dependency needed**: The PCS client NuGet transitively provides Azure.Identity. Our code only uses `PcsApiFactory` and `Path`/`File` for the auth record check.
- **Stderr logging**: Auth method is logged to `Console.Error` so it doesn't interfere with MCP stdio transport.
- **Try/catch on Entra path**: If credential creation fails for any reason (corrupt auth record, etc.), we fall back to anonymous gracefully.

### Files changed

- `src/MaestroTool.Core/MaestroApiClient.cs` — Auth cascade implementation

## Bug Fix: [McpServerToolType] attribute required on MaestroMcpTools

**Author:** Naomi (Backend Dev)
**Date:** 2025-07-14
**Status:** Fixed

### Problem

The MCP server started successfully but reported 0 tools. `tools/call` requests returned error `-32601: Method 'tools/call' is not available`. The server was effectively useless.

### Root Cause

`MaestroMcpTools` was missing the `[McpServerToolType]` class-level attribute. The `WithToolsFromAssembly()` registration in `Program.cs` uses this attribute to discover classes containing instance-method tools (methods decorated with `[McpServerTool]`). Without it, the assembly scan finds nothing.

### Fix

Added `[McpServerToolType]` to the `MaestroMcpTools` class declaration, matching the pattern in the Helix reference implementation (`HelixMcpTools.cs`).

### Impact

All 8 MCP tools now register and work end-to-end against real maestro.dot.net data.

### Files Changed

- `src/MaestroTool.Core/MaestroMcpTools.cs` — Added `[McpServerToolType]` attribute

## Documentation: README.md created for maestro.mcp

**Author:** Alex (DevOps / Infrastructure)  
**Date:** 2025-07-15  
**Status:** Complete

### Context

The maestro.mcp project required comprehensive documentation for both internal developers and external MCP client integrators. The README needed to cover authentication, tool references, architecture, and operational guidance.

### Decision

Created a production-ready README.md following this structure:

1. **Problem statement** — Clear opening describing what the server does and its role in .NET build infrastructure.
2. **Prerequisites** — .NET 10 SDK, authentication options (darc or PAT).
3. **Getting started** — Build, test, and run instructions.
4. **Configuration** — Copy-pasteable mcp-config.json snippet for Copilot clients.
5. **Authentication** — Full 3-tier cascade explanation with example of each tier.
6. **Tools reference** — Table of 8 tools with parameters for quick lookup.
7. **Architecture** — 4-layer model (data, cache, service, MCP) with class/responsibility mapping.
8. **Cache strategy** — TTL table with justifications (trade-offs between freshness and load).
9. **Testing** — How to run tests and scope (35 unit tests, xUnit, NSubstitute).
10. **Contributing** — Guidance for future maintainers.

### Key Design Choices

- **Authentication emphasis**: The 3-tier cascade is explained in plain English before any file references. This is critical because auth is non-obvious (cached darc tokens, MSAL integration).
- **Tools as a table**: Scannable reference format, not prose. MCP client integrators need to find parameter names quickly.
- **Architecture as story**: Each layer (data → cache → service → MCP) is explained by the problem it solves, not by listing every method.
- **Cache TTLs justified**: We explain why each TTL is set, not just the numbers. This helps reviewers understand trade-offs.
- **Copy-pasteable config**: The mcp-config.json example uses a placeholder path with clear instructions to replace it.

### Files Created

- `README.md` — 5980 bytes, production-ready documentation.

### Rationale

Clear documentation is force-multiplier for MCP servers. External integrators (Copilot CLI users, other teams) should understand configuration, auth, and available tools without reading code. Internal developers should see the architecture and cache strategy without digging through source files.

## Decision: GetBuildFreshnessAsync is untestable without refactoring

**Author:** Amos (Tester)  
**Date:** 2025-07-14  
**Status:** Observation / Recommendation

### Context

`MaestroService.GetBuildFreshnessAsync` creates `HttpClient` and `HttpClientHandler` inline with `new`. This makes it impossible to mock the HTTP layer for unit testing without introducing `IHttpClientFactory` or similar injection.

### Recommendation

If we want test coverage on build freshness logic:
1. Inject `IHttpClientFactory` into `MaestroService`, or
2. Extract the HTTP-fetching part into a separate abstraction (e.g., `IAkaMsResolver`), or
3. Accept it as an integration-only test target.

Not blocking — the method is cached and simple. But it's the one gap in `MaestroService` coverage.
