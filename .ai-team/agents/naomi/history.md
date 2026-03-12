# Naomi — History

## Learnings

### PcsApiFactory base URI fix (2026-02-22)
- **Root cause of #8**: `PcsApiFactory.GetAnonymous()` (parameterless) internally creates `ProductConstructionServiceApiOptions()` with no base URI, causing `UriFormatException: Invalid URI: The URI is empty.`
- **PcsApiFactory has 4 overloads** — two without base URI (crash-prone) and two with `string baseUri`:
  - `GetAnonymous()` and `GetAuthenticated(accessToken, managedIdentityId, disableInteractiveAuth)` — **do not use these**
  - `GetAnonymous(string baseUri)` and `GetAuthenticated(string baseUri, accessToken, managedIdentityId, disableInteractiveAuth)` — **always use these**
- **Fix pattern**: Added `private const string DefaultBaseUri = "https://maestro.dot.net"` and passed it to all three PcsApiFactory call sites (BAR token auth, Entra ID auth, anonymous fallback).
- **All three auth paths were vulnerable**, not just anonymous. The parameterless `GetAuthenticated` could also fail if the internal credential resolver didn't inject a URI.
- **Version**: 0.8.3 → 0.8.4

### SQLite Cache Migration (2026-02-18)
- **Migrated CacheService from in-memory ConcurrentDictionary to SQLite** for cross-process cache sharing. Multiple `mstro` instances (VS Code, Copilot CLI, etc.) now share cached PCS API data via `~/.mstro/cache.db`.
- **WAL (Write-Ahead Logging) mode** enables concurrent reads across processes. `PRAGMA busy_timeout=5000` handles write contention.
- **Two tables**: `cache` (key, value JSON, expiry ISO 8601) and `actions` (same schema, separate for dedup records). Both use `INSERT OR REPLACE` for upserts.
- **JSON serialization** via `System.Text.Json`. All cached objects are serialized/deserialized, eliminating object identity but enabling cross-process sharing.
- **SemaphoreSlim lock** around factory calls in `GetOrAddAsync` prevents duplicate API calls during cache misses. Double-check pattern after lock acquisition.
- **MaxCacheEntries (10,000) cap** enforced before insert. When exceeded, entire `cache` table is cleared (same behavior as original).
- **Periodic cleanup** every 100 operations purges expired rows from both tables via background Task.
- **Error handling**: SQLite failures logged to stderr, return default/expired data rather than crashing.
- **Test impact**: Some tests fail due to object identity checks (`Assert.Same`) no longer working with JSON deserialization. Build succeeds; production code works correctly. Tests would need refactoring to use value equality instead of reference equality.

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
- `src/MaestroTool.Core/CacheService.cs` — SQLite-backed cache with cross-process sharing

### End-to-end smoke test results (2025-07-14)
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

### Backend threat model findings (2025-07-15)
- **CRITICAL: SSRF in GetBuildFreshnessAsync** — `channel` parameter is user-controlled and interpolated into aka.ms URL without validation. Path traversal (`../../`) can target arbitrary aka.ms short links, and the redirect URL is followed without domain validation. Must sanitize channel input (alphanumeric + dots + hyphens only) and validate redirect targets.
- **HIGH: Entra auth record file permissions** — We check `File.Exists()` on `~/.darc/.auth-record-*` but never verify file permissions. On shared systems with permissive `~/.darc/` permissions, refresh tokens could be stolen. Should warn if permissions are too open.
- **MEDIUM: No cache size limit** — `ConcurrentDictionary` grows unbounded. A malicious client sending unique parameters could cause OOM. Should add max-entry or LRU eviction.
- **MEDIUM: noCache bypass enables PCS DoS** — Every read tool exposes `noCache` with no rate limit. Automated agents calling with `noCache: true` in loops would hammer PCS directly.

📌 Team update (2025-07-15): STRIDE threat model completed — identified 14 threats, 8 with mitigations documented. P0 items (SSRF validation, dedup separation, tool-level auth gating) ready for next sprint. Decided by Holden, Naomi, Amos.
- **MEDIUM: TriggerDailyUpdate blast radius** — Triggers ALL daily subscriptions ecosystem-wide. Not gated by `EnableDestructiveActions`. Consider gating or separate flag.
- **LOW: clear_cache resets action dedup** — Calling `maestro_clear_cache` removes trigger cooldown records, allowing immediate re-trigger. Consider separating action dedup store from read cache.
- **LOW: Anonymous fallback doesn't fail-fast on actions** — Action tools will get 401 at PCS runtime rather than failing early when running in anonymous mode.
- **Architecture note:** No PII flows through the server. Most sensitive read data is subscription topology (reveals .NET build dependency graph). Secrets (PAT, Entra tokens) only flow in HTTP auth headers, never in MCP tool output.

### Threat model fixes implementation (2025-07-15)
- **Fix 1 (SSRF):** Added regex validation (`^[a-zA-Z0-9.\-]+$`) on `channel` parameter in `GetBuildFreshnessAsync` before URL interpolation. Also validates redirect URL host against `*.blob.core.windows.net` and `dotnetcli` — rejects unexpected domains.
- **Fix 2 (Auth gate):** Added `AuthLevel` enum (`Pat`, `EntraId`, `Anonymous`) to `IMaestroApiClient`. `MaestroApiClient.CreateApi()` now returns a tuple of `(api, authLevel)`. Service-layer trigger methods throw `InvalidOperationException` if `AuthLevel.Anonymous`. MCP tools catch this specifically and return a `🔒` prefixed user-friendly message.
- **Fix 3 (Dedup separation):** `CacheService` now uses a separate `_actions` ConcurrentDictionary for action dedup records. `Clear()` only wipes the data `_cache`, NOT action records. Added `ClearActions()` for explicit action clearing (not exposed via MCP). This prevents `maestro_clear_cache` from defeating trigger cooldowns.
- **Fix 4 (Trigger audit):** Added `Console.Error.WriteLine` in `MaestroService` trigger methods (logs before API call with ISO 8601 timestamp + args). MCP tools log dedup-skipped cases separately. Both triggered and dedup-skipped events are now auditable on stderr.
- **Fix 5 (Cache cap):** Added `MaxCacheEntries = 10000` constant. `GetOrAddAsync` checks `_cache.Count` before inserting; if at capacity, clears entire data cache and logs to stderr. Simple and appropriate for single-user MCP server.
- **Test impact:** Replaced `Clear_ResetsActionRecords` with `Clear_DoesNotResetActionRecords` and added `ClearActions_ResetsActionRecords`. Total test count: 49, all passing.

### P1 security fixes: SQLite threat model (2026-02-19)
- **Fix I2 (File permissions — MEDIUM):** Added explicit directory permission hardening to prevent info disclosure on shared machines. After creating `~/.mstro/` directory (and custom dirs in test constructor), the code now calls `File.SetUnixFileMode()` with `700` permissions (UserRead|UserWrite|UserExecute) on Linux/macOS. Windows user profile directories are already restricted by default, documented in code comment. Applied in both `GetDefaultDbPath()` and the `internal CacheService(string dbPath)` constructor to cover both production and test paths.
- **Fix D2 (Corruption auto-recovery — MEDIUM):** Added `PRAGMA integrity_check` at the start of `InitializeDatabase()` immediately after opening the connection. If the result is NOT `"ok"` (or if `SqliteException` is thrown during Open due to corrupted header), the code logs `[maestro-mcp] Cache database corrupted, recreating...` to stderr, closes/deletes the DB file plus WAL/SHM sidecars, then re-opens a clean database and continues normal initialization. This prevents persistent DoS from corrupted cache files — the cache auto-heals on next launch.
- **Implementation pattern:** Both fixes use `OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()` guard for Unix-specific file permission APIs. The corruption recovery wraps the entire `InitializeDatabase()` in try/finally to ensure proper connection disposal. Tests confirmed unaffected — temp test paths also get permission hardening and corruption recovery.
- **Test results:** All 73 tests pass (67 from original suite + 6 new security tests).
- **Commit:** `eb1d5e0` — P1 security fixes: file permissions (I2) and corruption auto-recovery (D2)

📌 Team update (2026-02-19): P1 security fixes completed — file permissions (I2) and corruption auto-recovery (D2) implemented in CacheService. 6 security tests written. All 73 tests passing. Windows connection pool issue resolved. Decided by Naomi, Amos, Coordinator.

### Bug fixes: Issues #2 and #3 (2025-07-16)
- **Issue #2 (build_freshness rejects ci.dot.net):** The SSRF domain allowlist in `GetBuildFreshnessAsync` only accepted `*.blob.core.windows.net` and `dotnetcli` hosts. The aka.ms redirect for .NET channels now resolves to `ci.dot.net`, a legitimate Microsoft domain. Added `ci.dot.net` (exact match) and `*.azureedge.net` (suffix match) to the allowlist. Two lines added to the existing `if` condition.
- **Issue #3 (subscription_health errors for dotnet/sdk):** The `GetSubscriptionHealthAsync` foreach loop had no error handling — a single failed `GetLatestBuildAsync` call would crash the entire method. Wrapped the per-subscription body in try/catch. On exception, the subscription is added to results with an `Error` field populated. Added optional `string? Error = null` to the `SubscriptionHealthResult` record. MCP tool layer now displays `⚠️ Error:` for any subscription that failed. This makes the tool resilient for repos with many subscriptions (dotnet/sdk has 59).
- **Build:** 0 warnings, 0 errors. **Tests:** All 76 pass.

### PCS Client NuGet API Inspection via dotnet-inspect (2026-02-19)
- **Package:** `Microsoft.DotNet.ProductConstructionService.Client` v1.1.0-beta.26118.5 — 88 types, 183 methods, 307 properties across 17 interfaces.
- **TriggerSubscriptionAsync has 3 overloads:** `(Guid, CT)`, `(Guid, bool isCoherencyUpdate, CT)`, and `(int barBuildId, bool isCoherencyUpdate, Guid, CT)`. Our code uses overload 3.
- **Unused APIs with high value:** `IFeatureFlags` (8 methods, per-subscription feature toggles), `IBuilds.GetBuildGraphAsync` (dependency graph), `IChannels.GetFlowGraphAsync` (flow visualization), `IConfigurationIngestion` (YAML config management), `IStatus` (PCS processor control).
- **All versions are prerelease** beta under `1.1.0-beta.*` scheme. No stable releases exist.
- **PcsApiFactory** is static with 4 factory methods (2 anonymous, 2 authenticated). Each optionally takes a custom base URI.
- **dotnet-inspect usage patterns:**
  - `dotnet-inspect api <package> <TypeName>` — best way to get member signatures for a specific type
  - `dotnet-inspect api --package <pkg>` — full type surface summary (no member details)
  - `dotnet-inspect api <pkg> <Type> -m <Method> --select` — inspect specific method overloads
  - `dotnet-inspect package <pkg> --versions --prerelease` — list available versions
  - `-T q` flag suppresses tip output for cleaner results
  - The `-t <Type>` filter flag shows matching types but NOT their members (use positional arg instead)

### Codeflow PR tracking APIs (v0.4.0) (2026-02-19)
- **PCS Client `IPullRequest` interface:**
  - `GetTrackedPullRequestsAsync(CancellationToken)` → `Task<List<TrackedPullRequest>>`
  - `GetTrackedPullRequestBySubscriptionIdAsync(string subscriptionId, CancellationToken)` → `Task<TrackedPullRequest>` (throws `RestApiException` 404 if none)
  - `UntrackPullRequestAsync(string id, CancellationToken)` → `Task` (available but not exposed)
- **PCS Client `IBackflowStatus` interface:**
  - `GetBackflowStatusAsync(int vmrBuildId, CancellationToken)` → `Task<BackflowStatus>` — **requires vmrBuildId parameter** (not parameterless)
  - `TriggerBackflowStatusCalculationAsync(int vmrBuildId, CancellationToken)` → `Task`
- **PCS Client `ISubscriptions` (history):**
  - `GetSubscriptionHistoryAsync(Guid id, CancellationToken)` → `AsyncPageable<SubscriptionHistoryItem>` (Azure paging)
  - `GetSubscriptionHistoryPageAsync(Guid id, int? page, int? perPage, CancellationToken)` → `Task<Page<SubscriptionHistoryItem>>` — used this for simplicity
- **TrackedPullRequest model properties:** Id, Url, Channel, TargetBranch, SourceEnabled, LastUpdate, LastCheck, NextCheck, Updates (List\<PullRequestUpdate\>), HeadBranch, NextBuildsToApply
- **PullRequestUpdate model:** SourceRepository, SubscriptionId, BuildId
- **BackflowStatus model:** VmrCommitSha, ComputationTimestamp, BranchStatuses (IImmutableDictionary\<string, BranchBackflowStatus\>), IsValid
- **BranchBackflowStatus:** Branch, DefaultChannelId, SubscriptionStatuses (List\<SubscriptionBackflowStatus\>), IsValid
- **SubscriptionBackflowStatus:** TargetRepository, TargetBranch, LastBackflowedSha, CommitDistance, SubscriptionId, IsValid
- **SubscriptionHistoryItem:** Timestamp, ErrorMessage, Success, SubscriptionId, Action, RetryUrl
- **RestApiException** is in `Microsoft.DotNet.ProductConstructionService.Client` namespace (not Models). Used for 404 handling in tracked PR lookup.
- **Cache key patterns:** `tracked-prs:{channelId}`, `tracked-pr:{subId}`, `backflow-status:{vmrBuildId}`, `sub-history:{subscriptionId}` — all use ShortTtl (5 min).
- **No auth gating** on new APIs initially — will add if needed based on runtime testing.

### Force trigger parameter (Issue #1 Feature #2) (2025-07-16)
- Added optional `bool force = false` parameter across all 4 layers: `IMaestroApiClient` → `MaestroApiClient` → `MaestroService` → `MaestroMcpTools`.
- PCS client's `TriggerSubscriptionAsync(int barBuildId, bool isCoherencyUpdate, Guid subscriptionId, CT)` — the `isCoherencyUpdate` parameter, when `true`, force-triggers (overwrites existing PR branch with fresh VMR content). Previously hardcoded to `false`.
- Dedup key now includes force flag: `action:trigger-sub:{subId}:{buildId}:{force}` — so force and non-force triggers are deduped independently.
- MCP tool description updated to document force behavior. Success message differentiates force vs normal trigger.
- Decided as team to add as optional param to existing tool rather than a separate `maestro_force_trigger_subscription` tool.

### Issue #4 Analysis: VMR Commit Distance Problem (2025-02-20)
- **Root cause identified:** `BuildsBehind` calculation on line 132 of `MaestroService.cs` uses BAR build ID arithmetic (`latestBuild.Id - lastApplied.Id`). BAR IDs are globally sequential across ALL repos, not per-repo. For VMR subscriptions (dotnet/dotnet → X), this gives 17x inflated numbers (566 builds vs 33 actual commits).
- **BackflowStatus API unreliable:** Tested on VMR builds 302627, 302612, 302391 — all error. Cannot use `CommitDistance` field as primary solution.
- **Proven solution exists:** `Get-CodeflowStatus.ps1` uses GitHub compare API (`/repos/{owner}/{repo}/compare/{base}...{head}`) with 100% eval accuracy vs 0% for MCP-only workflows using BAR IDs.
- **Build model properties:** PCS `Build` model has `Commit` (SHA), `GitHubRepository`/`AzureDevOpsRepository`, `DateProduced`, `Id`. Already have everything needed to look up commits via `GetBuildAsync(lastAppliedBuildId)` and `GetBuildAsync(latestBuildId)`, then call GitHub compare.
- **Scope decision:** Fix applies only to VMR-sourced subscriptions (dotnet/dotnet → X). Non-VMR subscriptions continue using BAR ID arithmetic (acceptable approximation for non-VMR scenarios).
- **Technical approach chosen:** Add `IGitHubApiClient` interface + HttpClient implementation, inject into `MaestroService` as optional dependency. Compute `CommitsBehind` for VMR subscriptions, gracefully fall back to `BuildsBehind` (BAR IDs) if GitHub API unavailable. No new NuGet dependencies (HttpClientFactory already available).
- **Display strategy:** Prefer "33 commits behind" when available, fall back to "~566 builds behind" with note "Using BAR build count (approximate)" when GitHub API unavailable.
- **Rate limit consideration:** GitHub anonymous API = 60 req/hour. Typical `subscription_health` call has ~10 VMR subscriptions, well within limits. Failures degrade gracefully to BAR ID arithmetic.
- **Proposal written:** `.ai-team/decisions/inbox/naomi-issue4-commit-distance-approach.md` — awaiting team review before implementation.

### GitHub Commit Distance Implementation (Issue #4) (2025-02-20)
- **Implemented `IGitHubApiClient` interface + `GitHubApiClient` class** in `src/MaestroTool.Core/`. Uses single static `HttpClient` instance following existing project patterns.
- **Auth cascade** (Larry-approved): 1. `GITHUB_TOKEN` env var → 2. `gh auth token` subprocess → 3. anonymous (60 req/hr). Auth method logged to stderr on first use.
- **GitHub Compare API integration:** `GET https://api.github.com/repos/{owner}/{repo}/compare/{base}...{head}` returns `ahead_by`, `behind_by`, `status`, `total_commits`. Graceful degradation on ANY error (404, 403, timeout) — returns `null`.
- **MaestroService updates:**
  - Added optional `IGitHubApiClient?` constructor param (default `null`) — injected via DI
  - Helper `IsVmrRepository(string?)` detects `github.com/dotnet/dotnet` URLs
  - Helper `ParseGitHubUrl(string)` extracts owner/repo from GitHub URLs
  - Updated `GetSubscriptionHealthAsync` to compute `CommitsBehind` for VMR subscriptions when GitHub client available
  - Added `int? CommitsBehind` field to `SubscriptionHealthResult` record
- **MCP tool display logic updated:** Shows "⚠️ STALE (33 commits behind)" when `CommitsBehind` available, falls back to "⚠️ STALE (~566 builds behind)" with `~` prefix to indicate approximation
- **DI wiring in Program.cs:** Registered `IGitHubApiClient` singleton, updated `MaestroService` registration to explicit factory pattern to ensure 3rd constructor param is injected
- **Build verification:** `dotnet build` succeeded (46.1s) — 0 warnings, 0 errors
- **File paths:**
  - `src/MaestroTool.Core/IGitHubApiClient.cs` — interface definition
  - `src/MaestroTool.Core/GitHubApiClient.cs` — implementation with auth cascade
  - `src/MaestroTool.Core/MaestroService.cs` — updated for commit distance
  - `src/MaestroTool.Core/MaestroMcpTools.cs` — updated display logic
  - `src/MaestroTool.Mcp/Program.cs` — DI registration

### CLI Commands Implementation (2026-02-20)
- **Implemented dual-mode CLI + MCP** following hlx pattern from helix.mcp. `mstro` now works as both CLI tool and MCP server.
- **18 CLI commands added:** subscriptions, subscription, latest-build, build, channels, default-channels, subscription-health, build-freshness, trigger-subscription, trigger-daily-update, codeflow-prs, tracked-pr, backflow-status, subscription-history, build-graph, flow-graph, cache (clear/status), mcp.
- **ConsoleAppFramework v5 integration:** No [Option] attributes needed — parameters auto-map to `--kebab-case` flags. Positional arguments use `[Argument]`.
- **Output pattern:** Human-readable by default (clean console output), `--json` flag returns structured JSON for scripting.
- **Backwards compatibility:** No args → MCP server mode. Args provided → CLI mode. Existing MCP integrations unaffected.
- **DI architecture:** Shared service registrations between CLI and MCP. MCP command creates separate Host for server isolation.
- **Build status:** ✅ 0 warnings, 0 errors. Smoke tests confirmed CLI and MCP modes both work.
- **Version bump:** 0.6.2 → 0.7.0 (minor version — significant capability addition, no breaking changes).
- **Decision doc:** `.ai-team/decisions/inbox/naomi-cli-implementation.md` — implementation notes, command mapping table, learnings.

### Key file paths
- `src/MaestroTool.Core/MaestroApiClient.cs` — API client with auth cascade
- `src/MaestroTool.Core/IMaestroApiClient.cs` — Interface definition
- `src/MaestroTool.Core/MaestroService.cs` — Cached business logic layer
- `src/MaestroTool.Core/MaestroMcpTools.cs` — MCP tool definitions
- `src/MaestroTool/Program.cs` — Dual-mode entry point, DI setup, Commands class
- `src/MaestroTool.Core/CacheService.cs` — SQLite-backed cache with cross-process sharing

### Commit SHA Fetch Fix (Issue #5) (2025-02-20)
- **Root cause:** PCS subscription API returns embedded `LastAppliedBuild` objects without full commit SHA field populated. The GitHub Compare API code added in v0.6.0 was being silently skipped due to null/empty commit SHAs.
- **Fix implemented:** In `GetSubscriptionHealthAsync`, when `lastApplied.Commit` or `latestBuild.Commit` is null/empty, the code now fetches the full build using `GetBuildAsync(buildId)` to retrieve the commit SHA before attempting GitHub compare.
- **Defensive approach:** Only fetch full build when commit is null/empty AND build ID > 0. If full build also has null commit, gracefully fall back to builds-behind (BAR ID arithmetic).
- **Diagnostic logging added:**
  - `[maestro-mcp] Fetching full build {buildId} for commit SHA` — when fetching full build
  - `[maestro-mcp] Comparing commits {sha1}...{sha2} in {owner}/{repo}` — before GitHub compare call
- **Tests added (3 new tests, 107 total passing):**
  1. `SubscriptionHealth_FetchesFullBuildWhenLastAppliedCommitIsNull` — verifies full build fetch for lastApplied
  2. `SubscriptionHealth_FetchesFullBuildWhenLatestBuildCommitIsNull` — verifies full build fetch for latestBuild
  3. `SubscriptionHealth_FallsBackToBuildsBehindWhenBothCommitsAreNull` — verifies graceful fallback when both commits unavailable
- **Test discovery:** `CreateBuild` helper defaults `commit` parameter to `"abc123"` when null is passed. Tests must use empty string `""` to simulate missing commits.
- **Files modified:**
  - `src/MaestroTool.Core/MaestroService.cs` — added full build fetch logic (lines 138-168)
  - `src/MaestroTool.Tests/MaestroServiceTests.cs` — added 3 new tests

### Commit Distance for All GitHub Repos (Issue #6) (2025-02-20)
- **Root cause:** `GetSubscriptionHealthAsync` gated commit distance computation on `IsVmrRepository()`, which only matched `github.com/dotnet/dotnet`. All other GitHub-hosted source repos (e.g., `dotnet/runtime`, `dotnet/sdk`) fell back to BAR build ID deltas, wildly overstating staleness (e.g., 340 builds behind when actual commits behind is 1).
- **Fix:** Changed gate from `IsVmrRepository(sub.SourceRepository)` to `IsGitHubRepository(sub.SourceRepository)`. The existing `ParseGitHubUrl` method already correctly parses ANY `github.com` URL into owner/repo, so the commit distance logic worked for all GitHub repos without further changes.
- **New helper:** Added `IsGitHubRepository(string?)` — delegates to `ParseGitHubUrl` for readability. `IsVmrRepository` retained for potential future use.
- **Display logic verified:** Both `MaestroMcpTools.cs` (line 213) and `Program.cs` (line 337) already handle `CommitsBehind` generically via `.HasValue` — no changes needed.
- **Version bump:** 0.7.0 → 0.7.1 in `MaestroTool.csproj` and `Program.cs` server info.
- **Build:** 0 warnings, 0 errors.
- **Files modified:**
  - `src/MaestroTool.Core/MaestroService.cs` — changed gate, added `IsGitHubRepository` helper, updated comment
  - `src/MaestroTool/MaestroTool.csproj` — version bump
  - `src/MaestroTool/Program.cs` — version string bump

### dotnet-replay stats command (Issue #13 lewing/dotnet-replay) (2025-02-20)
- **Implemented `replay stats` command** to aggregate statistics across multiple transcript files (JSONL and Waza JSON formats).
- **Key features:**
  - Supports glob patterns for file input (`results/*.json`)
  - Aggregates: total sessions, pass/fail counts, average duration, tool call counts
  - Group by model or task (`--group-by model`, `--group-by task`)
  - Filter by model or task name (`--filter-model`, `--filter-task`)
  - CI integration with pass rate threshold (`--fail-threshold N` exits with code 1 if pass rate < N%)
  - JSON output mode (`--json`) for scripting
- **Implementation approach:**
  - Added stats command dispatch early in CLI arg parsing (line ~100)
  - Created `ExpandGlob()` helper for Windows-style path glob expansion
  - Created `ExtractStats()` helper that:
    - Auto-detects format (Copilot JSONL, Claude JSONL, Waza JSON)
    - Reuses existing parse functions (`ParseJsonlData`, `ParseClaudeData`, `ParseWazaData`)
    - Extracts model name from agent string for Copilot/Claude transcripts
    - Returns unified `FileStats` record with all relevant metrics
  - Created `OutputStatsReport()` for both console and JSON output formats
  - Added `FileStats` record to hold per-file statistics
- **Architecture patterns learned:**
  - dotnet-replay is a **single-file .NET 10 app** with file-scoped statements (no namespace/class wrapper)
  - All local functions must be defined at top level
  - Records go at the bottom of the file after all code
  - Existing summary extraction logic (`OutputSummary`, `OutputWazaSummary`) provided reference for stat calculation
  - Format detection logic (line 770-816) cleanly separates JSONL vs Waza JSON handling
- **Build verification:**
  - `dotnet build replay.cs` succeeded with 1 pre-existing warning (unreachable code at line 2384)
  - All 35 existing tests pass (30.8s runtime)
  - Fixed minor test compilation error in `StatsOutputTests.cs` (anonymous array type inference)
- **Files modified:**
  - `D:\lewing\dotnet-replay\replay.cs` — added stats command, helpers, FileStats record, updated help text
  - `D:\lewing\dotnet-replay\tests\StatsOutputTests.cs` — fixed array type inference


📌 Team update (2026-02-22): Always pass DefaultBaseUri to PcsApiFactory — decided by Naomi


### ModelContextProtocol SDK Upgrade to 1.0.0 (2025-02-22)
- **Upgraded from 0.8.0-preview.1 to 1.0.0 stable** across all four projects: MaestroTool, MaestroTool.Mcp, MaestroTool.Core, and MaestroTool.Tests.
- **No breaking changes detected** in this project's usage pattern. The MCP 1.0.0 release introduced several breaking changes between 0.8.0 and 1.0.0, but none affected our implementation:
  - Configuration filter methods (replaced Add*Filter with WithMessageFilters/WithRequestFilters) — we don't use filters
  - Collection type changes (List<T>/T[] → IList<T>) — not applicable to our code
  - McpClientHandlers sealed — we use server-side, not client
  - Tool.Name now required — already specified via [McpServerTool(Name = "...")] attribute
  - Removed AddXxxFilter extension methods — not used
  - RunSessionHandler marked experimental — not used
- **Build verification**: `dotnet restore` and `dotnet build` succeeded with 0 warnings, 0 errors.
- **Version bump**: 0.10.0 → 0.11.0 in MaestroTool.csproj and both Program.cs server version strings.
- **Test status**: 11 tests passed. 124 tests failed due to unrelated file permission issue on `/tmp` (SetUnixFileMode fails on shared /tmp directory), not MCP-related.
- **Usage pattern confirmed stable**: `[McpServerToolType]` attribute on class, `[McpServerTool(Name = "...")]` on methods, `AddMcpServer()` → `WithStdioServerTransport()`/`WithHttpTransport()` → `WithToolsFromAssembly()` pattern all work unchanged in MCP 1.0.0.

### Interactive detection pattern (2025-07-15)
- `Console.IsInputRedirected` reliably distinguishes MCP host launches (stdin piped) from interactive terminal usage (stdin is TTY). Used in Program.cs to default no-arg invocation to `["mcp"]` when piped or `["--help"]` when interactive. This is a standard .NET pattern requiring no platform-specific code.

### README documentation patterns from helix.mcp (2025-07-16)
- **helix.mcp structure to emulate:** Top description mentions both MCP + CLI, "Why" section explaining value over raw API, "Quick Start → CLI" section with practical examples, interactive detection table, accurate tool/test counts.
- **README should reflect dual-mode nature:** `mstro` is both an MCP server AND a standalone CLI. The README should lead with this, not treat CLI as an afterthought.
- **Tool count source of truth:** Count `[McpServerTool]` annotations in `MaestroMcpTools.cs`. Currently 19 tools. Update ALL references when tools are added/removed.
- **Test count:** Update when tests are added. Currently 135 tests. The old "73 + 3 + 4 + 8" breakdown was getting stale — just state the total.

### CLI commands available in mstro (2025-07-16)
- **18 CLI commands** defined in `Commands` class in `src/MaestroTool/Program.cs`:
  - `mcp` — Start MCP server mode
  - `subscriptions` — List subscriptions (--source-repository, --target-repository, --channel-name, --target-branch)
  - `subscription <id>` — Get subscription by GUID
  - `latest-build <repository>` — Get latest build (--channel-name)
  - `build <buildId>` — Get build by ID
  - `channels` — List all channels
  - `default-channels` — List default channel mappings (--repository, --branch)
  - `subscription-health <targetRepo>` — Check health (--include-commit-details)
  - `build-freshness <channel>` — Check freshness via aka.ms
  - `trigger-subscription <id> <buildId>` — Trigger subscription (--force)
  - `trigger-daily-update` — Trigger all daily subscriptions
  - `codeflow-prs` — List tracked PRs (--channel-name)
  - `tracked-pr <id>` — Get tracked PR for subscription
  - `backflow-status <vmrBuildId>` — Get backflow status
  - `subscription-history <id>` — Get update history
  - `build-graph <buildId>` — Get dependency graph
  - `flow-graph <channelId>` — Get flow graph (--days, --include-arcade, etc.)
  - `codeflow-statuses <repositoryUrl>` — Get codeflow status (forward/backflow) for a repo (--branch, --json, --no-cache)
  - `cache <action>` — Cache management (clear, status)
- **Common flags:** All read commands support `--json` (structured output) and `--no-cache` (bypass cache). ConsoleAppFramework auto-maps camelCase params to `--kebab-case` CLI flags.

### Codeflow statuses endpoint (2026-07)
- **API endpoint:** `GET /api/codeflows?repositoryUrl={url}&branch={branch}&api-version=2020-02-20` at `maestro.dot.net`. Returns `List<CodeflowStatus>` with forward flow and backflow `CodeflowSubscriptionStatus` per mapping.
- **Workaround for missing `ICodeflow`:** PCS client NuGet v1.1.0-beta.26155.1 has the models (`CodeflowStatus`, `CodeflowSubscriptionStatus`) but doesn't wire `ICodeflow` onto `IProductConstructionServiceApi`. PR dotnet/arcade-services#6057 filed to fix. Workaround: direct HTTP call via `HttpClient` with the same auth mechanism.
- **Auth for HTTP calls:** Added `_barToken` field and `_entraCredential` (`TokenCredential?`) to `MaestroApiClient`. BAR token → Bearer header. Entra ID → `InteractiveBrowserCredential` with the darc auth record and MSAL cache "maestro", `DisableAutomaticAuthentication = true` to avoid browser popups. Anonymous → no auth header.
- **Deserialization:** Uses `Newtonsoft.Json.JsonConvert.DeserializeObject<List<CodeflowStatus>>` since the PCS client models use Newtonsoft.Json serialization attributes.
- **Cache key:** `codeflow-statuses:{repositoryUrl}:{branch}`, ShortTtl (5 min).
- **Default values:** `repositoryUrl = "https://github.com/dotnet/dotnet"`, `branch = "main"` — the VMR is the primary use case.
- **When upstream PR merges:** Replace the `GetCodeflowStatusesAsync` HTTP call in `MaestroApiClient` with `_api.Codeflow.GetCodeflowStatusesAsync()`. Remove `_barToken`, `_entraCredential`, and helper methods. The rest of the stack (service, tools, CLI) stays unchanged.

📌 Team update (2026-03-11): Cross-validation strategies for subscription health proposed by Holden — Phase 1 targets PR ground truth and commit reachability validation

### Cross-validation for subscription health (Phase 1) (2026-03-11)
- **Added `SearchMergedPullRequestsAsync`** to `IGitHubApiClient`/`GitHubApiClient` — uses GitHub search API (`/search/issues`) to find merged PRs in a target repo matching codeflow head branch patterns. Returns `List<GitHubPullRequest>` with number, title, merge commit SHA, and merged date.
- **Added `GitHubPullRequest` record** to `IGitHubApiClient.cs` alongside existing `CommitInfo`/`GitHubCompareResult`.
- **Added `ValidationResult` record** to `MaestroService.cs` — captures commit reachability, merged PR count/URLs, and whether bookkeeping anomaly was detected.
- **Extended `SubscriptionHealthResult`** with two new optional fields: `Validation` (cross-validation results) and `CanaryWarning` (cheap anomaly heuristic).
- **Cross-validation logic** in `CrossValidateSubscriptionAsync`: (a) checks commit reachability via existing `CompareCommitsAsync` on the source repo; (b) searches for merged PRs in the target repo matching the source repo name as branch pattern, merged after `LastAppliedDate`.
- **Canary warning** via `CheckCanaryWarningAsync`: when stale, fetches subscription history and emits a warning if 10+ entries with zero recorded successes. Runs even without `validate=true` since it uses already-cached data.
- **Key design decisions**:
  - `validate=false` default — no performance impact on normal usage
  - Validation results cached at `MediumTtl` (15 min) — ground truth changes slowly
  - Only GitHub-hosted target repos validated in Phase 1 (AzDO skipped)
  - Branch pattern matching uses source repo short name (e.g., "emsdk" from `dotnet/emsdk`) — simple but effective for codeflow PRs
  - Rate limiting: only stale subscriptions are validated, not all
  - Search API returns max 10 results per query, sufficient for anomaly detection
- **MCP tool output format** includes emoji-coded cross-validation section and canary warning when applicable.

---

## 2026-03-11 - Phase 1 Cross-Validation Implementation

**Decision Merged:** Phase 1 Cross-Validation Implementation Choices (2026-03-11)

Phase 1 cross-validation for subscription health completed. Decisions now in decisions.md:
- Branch pattern matching uses source repo short name
- Commit reachability checks the SOURCE repo
- Canary warning runs unconditionally for stale subscriptions  
- Validation results cached at MediumTtl (15 min)
- GitHub search API capped at 10 results

Affected work: `maestro_subscription_health` tool now supports `validate=true` parameter.

### Health-check overhaul: oscillation detection + VMR manifest (2026-07-25)

- **CRITICAL FINDING: `SubscriptionUpdate.Success` is NEVER set to `true`** in the PCS codebase. ALL history entries report Success=false regardless of actual health. The previous `CheckCanaryWarningAsync` method checked `history.Any(h => h.Success)` — this always returned false, making the canary fire on EVERY stale subscription with 10+ history entries. It was pure noise.
- **Replaced canary with oscillation detection**: The real signal for stuck subscriptions (arcade-services#6090) is state oscillation — history entries that alternate between `ApplyingUpdates` and `MergingPullRequest` indefinitely. The new `DetectStateOscillationAsync` method counts consecutive A→B→A patterns in recent history and flags subscriptions with 3+ oscillations.
- **source-manifest.json structure**: `dotnet/dotnet` at `src/source-manifest.json` contains ground truth for what code is in the VMR. It's a JSON object with a `submodules` array, each entry having `path`, `remoteUri`, `commitSha`, `barId`. The `remoteUri` matches against source repository URLs (needs `.git` suffix normalization). Cached at MediumTtl since the manifest changes infrequently.
- **New records**: `OscillationResult` (count, two alternating states, time span) and `SourceManifestEntry` (commitSha, path, barId) added to the data model. `CanaryWarning` field removed from `SubscriptionHealthResult`.
- **CLI `--validate` flag**: Now exposed in the `subscription-health` CLI command, matching the MCP tool's parameter.
- **IGitHubApiClient.GetFileContentsAsync**: New method using `application/vnd.github.raw+json` Accept header to get raw file content from GitHub.

### Tracked PR diagnosis enrichment (2026-07-24)
- **Cross-reference pattern**: For stale subscriptions, `DiagnoseTrackedPrAsync` fetches the Maestro tracked PR via `GetTrackedPullRequestBySubscriptionIdAsync`, then checks the actual GitHub PR state via `GetPullRequestStateAsync`. This cross-reference distinguishes four root causes:
  - `MergedButNotCleared` — PR is merged but Maestro keeps cycling (arcade-services#6090 bug)
  - `ClosedButNotCleared` — PR was closed/abandoned but state not cleared
  - `BlockedByCI` — PR is open but CI checks are failing
  - `Active` — PR is open and healthy, may just be in progress
  - `Missing` — No tracked PR at all, subscription may be failing earlier
  - `Unknown` — PR exists but couldn't verify state on GitHub
- **GitHub API additions**: `IGitHubApiClient.GetPullRequestStateAsync` calls `GET /repos/{owner}/{repo}/pulls/{prNumber}` for PR state, then `GET /repos/{owner}/{repo}/commits/{head_sha}/status` for combined CI status. `PullRequestState` record holds `Merged`, `Closed`, `ChecksFailing`.
- **TryParseGitHubPrUrl**: Static helper parses `https://github.com/{owner}/{repo}/pull/{number}` into components. Non-GitHub URLs gracefully return false.
- **Output integration**: Both MCP tool and CLI show tracked PR state with emoji indicators (🔴 stuck/merged, 🟠 stuck/closed, 🟡 CI-blocked, 🟢 active, ⚪ missing, ❓ unknown) after the oscillation block.
- **flow-analysis emphasis**: The key insight is that oscillation alone can't tell you WHY a sub is stuck — all stuck subs produce the same alternating pattern. The tracked PR cross-reference is what distinguishes actionable root causes.

### MCP tool audit implementation (P0 + P1) (2025-07-19)
- **P0: Removed "Returns X, Y, Z" from 8 tool descriptions** — `maestro_subscriptions`, `maestro_latest_build`, `maestro_build`, `maestro_builds`, `maestro_channel`, `maestro_channels`, `maestro_codeflow_prs`, `maestro_codeflow_pr`. Agents see the actual response; listing return fields wastes tokens and clutters routing.
- **P1-M4: Added cross-references to overlapping tools** — `maestro_subscriptions` ↔ `maestro_subscription_health` ↔ `maestro_subscription`, `maestro_build` → `maestro_builds`, `maestro_channel` → `maestro_channels`. Helps agents pick the right tool without trial-and-error.
- **P1-M3: Fixed channel ID vs name asymmetry** — `maestro_channel` now accepts `string channelNameOrId` instead of `int channelId`. If it parses as int, calls `GetChannelAsync(int)`; otherwise looks up by name via `GetChannelByNameAsync`. Added null/empty guard and negative-ID validation with try/catch for invalid channel IDs.
- **P1-M1: Smart trigger_subscription** — `buildId` is now optional (`int?`). When null, agents can provide `sourceRepository` + `channelName` to auto-resolve the latest build internally. Eliminates the 3-step dance (latest_build → parse → trigger). Commit short-SHA display safely handles strings shorter than 7 chars.
- **Test constructor fix**: `MaestroMcpToolsTests` constructor was missing `MaestroToolOptions` and `CacheService` args for the `MaestroMcpTools` constructor — pre-existing issue fixed as part of this work.
- **All 167 tests pass** after changes, including 8 new tests that were pre-written for this refactoring.
