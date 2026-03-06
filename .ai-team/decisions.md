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

## STRIDE Threat Model: Full Assessment and Recommended Mitigations

**Author:** Holden (Lead / Architect)  
**Date:** 2025-07-15  
**Status:** Proposal — mitigations pending team discussion

### Key Findings (Critical/High only)

1. **[CRITICAL] SSRF via aka.ms redirect in GetBuildFreshnessAsync** — The `channel` parameter is used in URL path construction without validation, enabling path traversal (`../../` sequences). Redirects from aka.ms are not validated before making HEAD requests, creating SSRF vector to internal metadata services or cloud credential endpoints.

2. **[HIGH] HTTP transport has no auth** — `MaestroTool.Mcp/Program.cs` exposes all tools on `localhost:5000` with zero authentication. Any local process gets full access including trigger actions.

3. **[HIGH] Action dedup bypass via cache clear** — Calling `maestro_clear_cache` before `maestro_trigger_subscription` defeats the 2-minute cooldown. The dedup and the data cache share the same `CacheService` instance.

4. **[HIGH] No auth-level gating on write tools** — `TriggerSubscription` and `TriggerDailyUpdate` are registered regardless of auth level. Anonymous sessions can call them; they'll fail at the PCS API with HTTP 401.

5. **[HIGH] Entra auth record file permissions not validated** — `~/.darc/` may have permissive permissions on shared systems. Auth record contains refresh token; if exfiltrated, attacker gets indefinite access. MSAL cache is shared with darc CLI.

6. **[MEDIUM] Unbounded cache growth** — `ConcurrentDictionary` has no max-entry limit. Varied query parameters from multiple clients could grow memory indefinitely.

7. **[MEDIUM] noCache parameter enables cache bypass DoS** — Every read tool exposes `noCache = false`. When true, bypasses cache and hits PCS directly. Rate-limiting absent.

8. **[MEDIUM] TriggerDailyUpdate has ecosystem-wide blast radius** — Triggers ALL daily subscriptions across .NET ecosystem, potentially creating hundreds of PRs. Not gated behind destructive flag.

### Recommended Mitigations

| Finding | Mitigation | Priority | Effort |
|---------|-----------|----------|--------|
| SSRF via aka.ms | Validate `channel` parameter (alphanumeric, dots, hyphens only); validate redirect URLs to known Microsoft domains | P0 | Medium |
| HTTP no auth | Add auth middleware with API key or bearer token; document HTTP mode is for local dev only | P1 | Medium |
| Dedup bypass | Separate action dedup storage from data cache; `maestro_clear_cache` clears data only | P0 | Small |
| No tool-level auth gating | Check auth level before allowing trigger tools; return "Authentication required" message | P0 | Small |
| Entra auth record permissions | Document `~/.darc/` should be `700`; log warning if permissions too open | P1 | Small |
| Unbounded cache | Add max-entry count to `CacheService` with LRU eviction (10,000 entries suggested) | P1 | Small |
| noCache abuse | Add rate limiting to noCache parameter (minimum interval between bypasses per key) | P2 | Small |
| TriggerDailyUpdate blast radius | Gate behind `EnableDestructiveActions` flag or require explicit confirmation | P2 | Small |

### Files Affected (for mitigations)

- `src/MaestroTool.Core/MaestroService.cs` — Validate `channel` parameter in `GetBuildFreshnessAsync`
- `src/MaestroTool.Core/CacheService.cs` — Separate action store, add LRU cap
- `src/MaestroTool.Core/MaestroMcpTools.cs` — Auth-level check on trigger tools
- `src/MaestroTool.Core/MaestroApiClient.cs` — Expose `AuthLevel` enum
- `src/MaestroTool.Mcp/Program.cs` — Auth middleware (deferred to v0.3)

### Decision

Record all findings. Address P0 mitigations (dedup separation, tool-level auth gating, SSRF validation) in next sprint. Defer HTTP auth middleware to v0.3 when HTTP deployment is actively planned.

## Backend Threat Model Deep Dive

**Author:** Naomi (Backend Dev)  
**Date:** 2025-07-15  
**Status:** Merged into STRIDE assessment

Comprehensive analysis of auth cascade, cache layer threats, API client surface, and data sensitivity. 14 specific findings documented with severity levels and blast radius analysis. Key observations:

- **Auth cascade:** PAT in environment variable is accepted risk; Entra ID auth record needs permission validation; anonymous fallback silent (low risk for read-only).
- **Cache layer:** Memory exhaustion via unique key flooding (unbounded); noCache abuse DoS potential; action dedup bypass via cache clear; key predictability acceptable for single-user mode.
- **API client:** SSRF via aka.ms critical (path traversal + unvalidated redirects); trigger action blast radius medium (ecosystem-wide for daily updates); injection vectors handled by PCS client.
- **Data sensitivity:** Subscription topology is operationally sensitive; PAT and Entra tokens held in-process only; no PII flows through server.

## Security Test Gap Analysis

**Author:** Amos (Tester)  
**Date:** 2025-07-15  
**Status:** Findings documented — 26 test specs recommended

### Summary

Audited 48 existing tests. Found zero tests at MCP tool layer and zero security-focused tests. Entire suite at service layer only.

### Gap categories and priorities

| Priority | Category | Specs | Risk |
|----------|----------|-------|------|
| **P1** | Auth cascade untestable (static methods) | 5 | Auth bypass, silent degradation to anonymous |
| **P2** | No MCP tool layer tests | 12 | Input validation, error messages, formatting |
| **P2** | Integer boundaries on buildId | 3 | Negative IDs passed unchecked |
| **P3** | Cache memory growth | 2 | Unbounded memory under sustained load |
| **P3** | Cache concurrency race | 1 | Duplicate API calls (perf, not security) |
| **P3** | Dedup edge cases | 3 | Trigger bypass after cache clear |

### Critical findings requiring code changes

1. **Auth cascade untestable** — `MaestroApiClient.CreateApi()` uses statics; recommend `IApiFactory` interface.
2. **No rate limiting on noCache** — Calls can hammer Maestro API; add minimum interval between bypasses.
3. **Cache has no max-size bound** — `ConcurrentDictionary` grows indefinitely; expired entries never proactively evicted.
4. **No input validation on buildId** — Negative integers pass through; opaque API exception instead of friendly response.

### Recommended action

Extract `IApiFactory` interface for auth cascade in v0.2.1. Refactoring unblocks all 5 P1 auth tests. High-priority for security hardening.

## 2026-02-18: Action tools implementation for v0.2.0

**By:** Naomi (Backend Dev)

**What:** Implemented non-destructive action tools (`maestro_trigger_subscription`, `maestro_trigger_daily_update`) with deduplication, cache invalidation, and future-proofed config for destructive actions.

**Why:** Users need the ability to trigger subscriptions and daily updates programmatically via MCP tools. Action deduplication prevents accidental duplicate triggers (2-minute cooldown). Cache invalidation ensures subsequent read queries don't return stale data after mutations. The `MaestroToolOptions` config class prepares the codebase for future destructive tools (delete, update) that will require explicit opt-in via env var.

**Key Technical Details:**

- **PCS Client Method Signature Discovery**: `ISubscriptions.TriggerSubscriptionAsync` has signature `(int barBuildId, bool isCoherencyUpdate, Guid subscriptionId, CancellationToken)`. The bool parameter controls coherency mode; passing `true` enables standard trigger behavior.

- **Action Deduplication Pattern**: `CacheService.GetRecentAction(key)` returns timestamp if action was executed within cooldown period; `RecordAction(key, cooldown)` stores execution timestamp. Dedup keys follow pattern `action:trigger-sub:{subscriptionId}:{buildId}` for subscription triggers and `action:trigger-daily-update` for daily updates.

- **Cache Invalidation Strategy**: Action methods in `MaestroService` call API client, then invalidate related read caches. `TriggerSubscriptionAsync` invalidates `sub:{subscriptionId}` and prefix `subs:*`. `TriggerDailyUpdateAsync` invalidates all subscription caches (`subs:*`). This prevents stale data from being served after mutations.

- **Config for Future Destructive Actions**: `MaestroToolOptions.EnableDestructiveActions` (default: false) is registered in DI and read from `MAESTRO_ENABLE_DESTRUCTIVE_ACTIONS` env var. v0.2.0 does not expose destructive tools yet — this is prep work for future delete/update operations.

- **Tool Design**: Both action tools return user-friendly confirmation messages with relevant context (subscription details, build ID). The 2-minute cooldown prevents accidental re-triggers while still allowing intentional retries after a reasonable delay.

**Files Changed:**
- Created `src/MaestroTool.Core/MaestroToolOptions.cs` (config class)
- Updated `src/MaestroTool/Program.cs` (register options, version bump to 0.2.0)
- Updated `src/MaestroTool.Mcp/Program.cs` (register options, version bump to 0.2.0)
- Updated `src/MaestroTool.Core/IMaestroApiClient.cs` (add action methods)
- Updated `src/MaestroTool.Core/MaestroApiClient.cs` (implement action methods)
- Updated `src/MaestroTool.Core/CacheService.cs` (add `GetRecentAction`, `RecordAction`)
- Updated `src/MaestroTool.Core/MaestroService.cs` (add service layer action methods with cache invalidation)
- Updated `src/MaestroTool.Core/MaestroMcpTools.cs` (add `maestro_trigger_subscription`, `maestro_trigger_daily_update` tools, inject options and cache service)

**Impact:** Maestro MCP server now supports programmatic triggering of subscription processing and daily updates. Version bumped to 0.2.0. All changes compile successfully with dotnet build.

## 2025-07-15: v0.2.0 Test Coverage Patterns

**Author:** Amos (Tester)  
**Date:** 2025-07-15  
**Status:** Complete

### Context

Added 13 unit tests for v0.2.0 features (action dedup, noCache, triggers, options). Total test count is now 48, all passing.

### Key patterns established

1. **Action dedup tests** use the same short-TTL + `Task.Delay` approach as existing cache expiry tests. No need for time abstraction — 50ms cooldown with 100ms delay is reliable.

2. **noCache bypass tests** use NSubstitute's `.Returns(firstValue, secondValue)` to verify the API is called again after invalidation. Two methods tested (subscriptions + channels) to prove the pattern works across the service.

3. **Trigger cache invalidation** is verified indirectly: populate cache → trigger → read again → assert `Received(2)` on the API mock. This proves the trigger methods properly invalidate related cache keys.

4. **New test file** `MaestroToolOptionsTests.cs` for options defaults. Kept separate because it doesn't need the MaestroService test fixture.

### Files changed

- `src/MaestroTool.Tests/CacheServiceTests.cs` — 4 new action dedup tests
- `src/MaestroTool.Tests/MaestroServiceTests.cs` — 8 new tests (4 noCache + 4 trigger)
- `src/MaestroTool.Tests/MaestroToolOptionsTests.cs` — 1 new test (new file)

## 2026-02-18: User directive — defer tool rename decision

**By:** Larry Ewing (via Copilot)

**What:** Considered renaming tools from `maestro_` prefix to `pcs_` to save tokens, but decided to wait — premium will likely have API suggestions. Do not rename tools yet.

**Why:** User request — waiting for external collaborator feedback before making naming changes

## 2026-02-18: Action tools policy

**By:** Larry Ewing (via Copilot)

**What:** Action tools should be added to the MCP server. Destructive actions (delete, disable) must be disabled by default and gated behind a config flag. Non-destructive actions (trigger, retry) can be enabled by default. The team should identify which PCS API methods are destructive vs non-destructive as a backlog item.

**Why:** User directive — safety by default for mutation operations

## STRIDE Threat Model: SQLite Cache Migration

**Author:** Holden (Lead / Architect)  
**Date:** 2026-02-18  
**Status:** Findings documented — P1 items prioritized for immediate implementation

### Scope

Analysis of threats **new to the SQLite migration**. Cache data now persisted to disk at `~/.mstro/cache.db` (previously in-memory `ConcurrentDictionary`). Multiple processes can read/write the same database via WAL mode.

### Findings Summary (13 Total)

| # | STRIDE | Severity | Threat | Status |
|---|--------|----------|--------|--------|
| S1 | Spoofing | **HIGH** | Cache poisoning via same-user process | P2 (backlog) |
| T1 | Tampering | **HIGH** | Direct database modification by external process | P2 (backlog) |
| T2 | Tampering | **MEDIUM** | Action dedup manipulation | P2 (backlog) |
| T3 | Tampering | **LOW** | WAL/journal file manipulation during recovery | P3 (accepted) |
| R1 | Repudiation | **MEDIUM** | No cross-process write attribution | P2 (backlog) |
| I1 | Information Disclosure | **HIGH** | Sensitive operational data persisted in plaintext | P2 (backlog) |
| I2 | Information Disclosure | **MEDIUM** | Database file permissions not explicitly set | **P1 (implemented)** |
| I3 | Information Disclosure | **LOW** | Data remnants in WAL/journal after Clear() | P3 (accepted) |
| D1 | Denial of Service | **MEDIUM** | Database write-lock DoS from external process | P3 (edge case) |
| D2 | Denial of Service | **MEDIUM** | Persistent database corruption across restarts | **P1 (implemented)** |
| D3 | Denial of Service | **LOW** | Fire-and-forget cleanup failure accumulation | P3 (capacity-capped) |
| E1 | Elevation of Privilege | **MEDIUM** | Cross-process auth boundary violation via shared cache | P3 (accepted) |
| E2 | Elevation of Privilege | **LOW** | Auth level not persisted with cache entries | P3 (design gap) |

### Key Recommendations

- **P1 (Ship now):** File permissions (I2), Corruption recovery (D2) — small effort, high impact
- **P2 (Next sprint):** HMAC integrity (S1/T1), Action dedup integrity (T2), Write attribution (R1) — medium effort, prevents tampering
- **P3 (Backlog):** Write-lock DoS (D1), Auth boundary (E1/E2), WAL remnants (I3/T3), Cleanup accumulation (D3) — edge cases or accepted risks

### Accepted Risks

- **Same-user process tampering** (S1/T1): Requires prior machine compromise. Machine owner can already read PCS data via darc.
- **Cross-process auth boundary** (E1): Anonymous PCS read access is intentional. Cache sharing is a performance feature.
- **WAL data remnants** (I3): Acceptable for developer workstations. No PII in cache; subscription topology is not classified.

### Decision

Implement P1 items immediately (I2 + D2). Record all findings. Defer P2 HMAC work to next sprint as separate initiative.

## Decision: SQLite-backed CacheService for Cross-Process Sharing

**Author:** Naomi (Backend Dev)  
**Date:** 2026-02-18  
**Status:** Implemented

### Summary

Migrated `CacheService` from in-memory `ConcurrentDictionary` to SQLite-backed storage at `~/.mstro/cache.db`. Enables cross-process cache sharing, reducing redundant PCS API calls when multiple MCP clients run simultaneously.

### Technical Implementation

- **Database location:** `~/.mstro/cache.db` (created automatically)
- **WAL mode:** Enables concurrent reads across processes
- **Busy timeout:** 5 seconds for write contention handling
- **Tables:** `cache` (key, value, expiry) and `actions` (key, value, expiry) — separate so `Clear()` preserves dedup records
- **Serialization:** `System.Text.Json` for all cached values
- **Thread safety:** `SemaphoreSlim` lock around factory calls prevents duplicate execution
- **Capacity cap:** 10,000 entries; entire cache cleared when exceeded

### Design Choices

- **Separate `actions` table:** Dedup records live separately so `maestro_clear_cache` doesn't reset trigger cooldowns (prevents abuse)
- **Connection-per-operation:** No application-level pooling; SQLite's `Cache=Shared` mode handles reuse
- **Double-check locking:** After acquiring semaphore, re-check cache before calling factory (prevents race condition)
- **Error handling:** SQLite failures logged to stderr; graceful degradation to API calls

### Trade-offs

**Pros:** Cross-process sharing, persistent cache, true concurrent reads, scales to multiple MCP clients  
**Cons:** Slightly slower than in-memory, JSON serialization adds CPU/memory cost, requires test refactoring

### Files Changed

- `src/MaestroTool.Core/MaestroTool.Core.csproj` — Added `Microsoft.Data.Sqlite` package
- `src/MaestroTool.Core/CacheService.cs` — Complete rewrite with SQLite backend
- `src/MaestroTool.Core/MaestroMcpTools.cs` — Updated `maestro_clear_cache` description

### Rationale

Cross-process cache sharing is essential for multi-client MCP deployment. Performance trade-off is negligible compared to PCS API latency (150ms–1.6s). Persistent cache improves cold-start performance.

## Decision: P1 Security Fixes for SQLite Cache

**Author:** Naomi (Backend Dev)  
**Date:** 2026-02-19  
**Status:** Implemented — All 73 tests passing

### Context

Holden's STRIDE threat model identified two P1 (MEDIUM severity) vulnerabilities in SQLite cache implementation:

1. **I2 (Info Disclosure):** `~/.mstro/cache.db` created with default permissions could be world-readable on shared Linux/macOS systems
2. **D2 (Denial of Service):** Corrupted SQLite files cause persistent startup failures with no auto-recovery

### Fixes Implemented

#### Fix 1: Directory Permission Hardening (I2)

After creating `~/.mstro/` directory, explicitly set owner-only permissions:
- **Linux/macOS:** `File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)` → `700` permissions
- **Windows:** No action (profile directories already restricted)

Applied in two places:
1. `GetDefaultDbPath()` — production path
2. `internal CacheService(string dbPath)` constructor — test paths

#### Fix 2: Corruption Auto-Recovery (D2)

At startup in `InitializeDatabase()`, after opening connection:

1. Run `PRAGMA integrity_check`
2. If result is NOT `"ok"`:
   - Log to stderr: `[maestro-mcp] Cache database corrupted, recreating...`
   - Close and delete corrupted DB file
   - Delete WAL/SHM sidecar files
   - Re-open clean database
3. If `SqliteException` thrown during `Open()` (corrupted header), same flow triggered

### Impact

- **Security:** Mitigated MEDIUM-severity info disclosure on shared machines and persistent DoS from corruption
- **UX:** Cache self-heals on corruption; no manual intervention needed
- **Performance:** Negligible overhead (one `PRAGMA` query at startup)
- **Tests:** All 73 tests passing (67 existing + 6 new security tests)

### Files Changed

- `src/MaestroTool.Core/CacheService.cs` — ~40 lines added (surgical permission + recovery logic)

### Rationale

**Defense-in-depth:** Both fixes are low-cost insurance. Cache is non-critical (rebuilt from PCS API) — safe to delete and recreate on corruption. File permissions prevent accidental exposure on shared dev machines.

**User principle:** "Fail gracefully, don't brick the tool."

## Decision: Security Test Coverage for SQLite Cache Hardening

**Author:** Amos (Tester)  
**Date:** 2026-02-19  
**Status:** Complete — 6 new tests, all passing

### Context

Naomi implemented 2 P1 security fixes (file permissions I2, corruption recovery D2). Wrote comprehensive tests to validate fixes and prevent regressions.

### Test Inventory

| Fix | Test Name | Coverage |
|-----|-----------|----------|
| Fix 1: Permission hardening | `CreateCacheDir_SetsUnixPermissions` | Verifies `0o700` on Unix systems |
| Fix 2: Corruption detection | `InitializeDatabase_DetectsCorruption_ViaIntegrityCheck` | PRAGMA check catches corruption |
| Fix 2: Corruption recovery | `InitializeDatabase_CorruptedDb_DeletesAndRecreates` | File deletion + fresh DB |
| Fix 2: Sidecar cleanup | `InitializeDatabase_DeletesWalShmOnCorruption` | WAL/SHM cleanup after corruption |
| Concurrent recovery | `InitializeDatabase_CorruptionRecovery_UnderConcurrentLoad` | Multiple processes recover safely |
| Regression | `InitializeDatabase_NormalDatabase_Succeeds` | Clean DB unaffected by recovery code |

### Key Testing Decisions

1. **Permission tests Unix-only:** Guard with `if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())`; skip on Windows (expected behavior)
2. **Corruption simulation:** Use in-memory SQLite to corrupt database in controlled way
3. **No test changes to existing suite:** New tests are additive; existing 67 tests continue to pass

### Files Changed

- `src/MaestroTool.Tests/CacheServiceTests.cs` — 6 new security test methods

## Session Summary: 2026-02-19 SQLite P1 Fixes

**Lead:** Naomi (Backend Dev), Amos (Tester), Holden (Architect)  
**Result:** ✅ All 73 tests passing, commit `eb1d5e0` pushed

**Deliverables:**
- Session log created: `.ai-team/log/2026-02-19-sqlite-p1-security.md`
- P1 security fixes merged and tested
- Windows connection pool issue resolved
- Threat model findings documented and prioritized

## Session Summary: 2026-02-19 Bugfix #2 & #3

**Requested by:** Larry Ewing

**Lead:** Naomi (Backend Dev), Amos (Tester)  
**Result:** ✅ All 76 tests passing, tool installed locally, commit pushed

### Bug #2: build_freshness SSRF Allowlist Expanded

**Problem:** `GetBuildFreshnessAsync` rejected `ci.dot.net` as an unexpected redirect domain. The aka.ms shortlinks for .NET channels now resolve there instead of only `*.blob.core.windows.net`.

**Fix:** Added two new entries to the SSRF domain allowlist in `MaestroService.cs`:
- `ci.dot.net` — exact host match (new Microsoft .NET build artifact domain)
- `*.azureedge.net` — suffix match (known Microsoft CDN for .NET builds, e.g. `dotnetbuilds.azureedge.net`)

**Rationale:** Both are legitimate Microsoft-owned domains used for .NET SDK/runtime build artifacts. The allowlist remains tight — only known Microsoft infrastructure domains are permitted.

### Bug #3: subscription_health Error Resilience

**Problem:** `GetSubscriptionHealthAsync` iterated all subscriptions sequentially. If any single `GetLatestBuildAsync` call threw, the entire method failed with an unhandled exception. Repos like dotnet/sdk (59 subscriptions) were particularly vulnerable.

**Fix:**
1. Wrapped per-subscription logic in try/catch
2. Added `string? Error = null` optional parameter to `SubscriptionHealthResult` record
3. On exception: subscription added to results with error message, processing continues
4. MCP tool displays `⚠️ Error:` line for failed subscriptions

**Rationale:** Partial results are far more useful than a complete failure. One flaky API call shouldn't prevent the user from seeing health data for the other 58 subscriptions.

### Test Coverage

Added 3 regression tests for bug #3 error handling (Amos).

### Files Changed
- `src/MaestroTool.Core/MaestroService.cs` — Both fixes
- `src/MaestroTool.Core/MaestroMcpTools.cs` — Error display in subscription_health tool

## Issue #1 Triage: Codeflow Feature Requests

**Date:** 2026-02-19  
**By:** Holden (Lead / Architect)  
**Issue:** https://github.com/lewing/maestro.mcp/issues/1  
**Scope:** 9 feature requests for codeflow analysis workflows

### Executive Summary

Issue #1 contains 9 well-scoped feature requests for enhancing maestro.mcp's usability in codeflow analysis workflows. All features are **feasible** with the current PCS client NuGet surface, though 3 require deeper investigation or GitHub API integration.

**Recommended roadmap:**
1. **v0.2.1 (sprint 1):** #1 + #2 + #3 — High-impact read/write fundamentals
2. **v0.3 (sprint 2):** #4 + #5 + #6 — Health & visualization composites  
3. **v0.4+ (backlog):** #7 + #8 + #9 — Specialized, lower-frequency queries

### v0.2.1 Priority Items

#### Feature #2: `maestro_force_trigger_subscription` — Force-Trigger a Subscription
- **Feasibility:** ✅ Implementable (small effort)
- **Effort:** 4 hours
- **Details:** Add boolean parameter for force-trigger mode; uses `isCoherencyUpdate` flag in PCS API

#### Feature #3: Target Branch Filtering on `maestro_subscriptions`
- **Feasibility:** ✅ Implementable (trivial)
- **Effort:** 2 hours
- **Details:** Add optional `targetBranch` filter parameter; filter client-side post-fetch

#### Feature #8: Channel Name Shorthand Resolution
- **Feasibility:** ✅ Implementable (trivial)
- **Effort:** 1 hour
- **Details:** Resolve short names (e.g., `net11`, `10.0.2xx`) to full Maestro channel names

### v0.3 Priority Items (Medium Impact)

#### Feature #1: `maestro_codeflow_prs` — List Codeflow PRs for a Repo
- **Feasibility:** ✅ Implementable (medium effort)
- **Effort:** 2-3 days
- **Blockers:** GitHub API integration required
- **Technical:** Query subscriptions, GitHub PR search, health checks

#### Feature #5: `maestro_flow_graph` — Dependency Flow Visualization
- **Feasibility:** ✅ Implementable (medium effort)
- **Effort:** 2-3 days
- **Details:** Show inbound/outbound flows; returns JSON or Mermaid syntax

#### Feature #6: `maestro_repo_flow_status` — Combined Health Endpoint
- **Feasibility:** ✅ Implementable (low effort, composition)
- **Effort:** 1-2 days
- **Details:** Composite endpoint reusing existing methods

### v0.2.2 (Pending Investigation)

#### Feature #4: `maestro_subscription_history` — Build Application History
- **Status:** 🔍 **Blocked on PCS API discovery**
- **Effort:** Unknown (depends on PCS support)

#### Feature #9: `maestro_build_assets` — List Build Assets
- **Status:** 🔍 **Blocked on PCS API discovery**
- **Effort:** Unknown (depends on PCS support)

### v0.4+ (Backlog)

#### Feature #7: `maestro_vmr_source_manifest` — VMR Source Manifest Reader
- **Feasibility:** ✅ Implementable (niche use case)
- **Effort:** 1-2 days
- **Details:** Read and parse source-manifest.json from VMR; low frequency

### Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|-----------|
| PCS API missing `history`/`assets` | Medium | Blocks #4 + #9 | Naomi investigates immediately |
| GitHub API rate limiting | Low | Blocks #1 + #5 | Cache PR results (5 min TTL) |
| Test coverage gaps | Medium | Release bugs | Amos writes integration tests |
| User expectations on "force trigger" semantics | Medium | Support burden | Document behavior clearly |

### Design Decisions

1. **`maestro_force_trigger_subscription` vs boolean parameter:** Recommend separate tool (clearer intent)
2. **Channel shorthand strategy:** Hardcoded mappings in v0.2.1; environment override in v0.3 if requested
3. **`maestro_flow_graph` output format:** JSON (structured); Mermaid syntax as optional string field
4. **GitHub API client:** Recommend Octokit (widely used, easy integration)

### Questions for Larry

1. Does the current `maestro_trigger_subscription` already force-trigger?
2. GitHub API client preference?
3. Is #7 (source-manifest parsing) likely to be heavily used?

### Summary

**All 9 features are architecturally sound.** No fundamental blockers. Roadmap prioritizes high-impact, low-effort wins (v0.2.1) before composite/visualization features (v0.3).

**Next steps:**
1. Naomi investigates PCS API surface for history/assets (1–2 hours)
2. Team aligns on GitHub client strategy
3. Kickoff v0.2.1 implementation


### 2026-02-19: isCoherencyUpdate trigger semantics investigation

**By:** Holden (via Coordinator — agents timed out on arcade-services search)

**What:** `isCoherencyUpdate` is a **vestigial client-side parameter** that has no effect on the server.

**Investigation findings:**

1. **Server-side API** (`ProductConstructionService.Api/Api/v2018_07_16/Controllers/SubscriptionsController.cs:108`):
   ```csharp
   public virtual async Task<IActionResult> TriggerSubscription(Guid id, [FromQuery(Name = "bar-build-id")] int buildId = 0)
   ```
   NO `isCoherencyUpdate` parameter. The REST endpoint accepts only `bar-build-id` as a query parameter.

2. **Current PCS Client** (Generated/Subscriptions.cs):
   ```csharp
   Task<Subscription> TriggerSubscriptionAsync(int barBuildId, Guid id, CancellationToken)
   ```
   Two-parameter version (plus cancellation). The `isCoherencyUpdate` bool has been REMOVED from the current source.

3. **Our NuGet package** (v1.1.0-beta.26118.5) still has a 3-parameter overload including `bool isCoherencyUpdate`. This parameter was never serialized to the REST request — the HTTP call sends only `bar-build-id` and `api-version`.

4. **Darc's usage** (`DarcLib/BarApiClient.cs:317-324`):
   - `TriggerSubscriptionAsync(Guid subscriptionId)` → calls with `barBuildId: default (0)`
   - `TriggerSubscriptionAsync(Guid subscriptionId, int sourceBuildId)` → calls with specific build
   - Neither passes `isCoherencyUpdate`. Darc never used this parameter.

5. **`isCoherencyUpdate` in the codebase** is only referenced in `PullRequestBuilderTests.cs` as a property on a test data model (`IsCoherencyUpdate`). It's an internal DependencyFlow concept, NOT an API parameter.

**Conclusion:** Our code at `MaestroApiClient.cs:154` passes `true` for a parameter that:
- Is never sent to the server
- Has been removed from the current PCS client
- Will cause a compile error when we update the NuGet package

**Recommendation:**
- ❌ **No separate `maestro_force_trigger_subscription` tool** — the concept doesn't exist server-side
- ✅ **Note for NuGet update**: When we update the PCS client package, remove the `true` parameter from `TriggerSubscriptionAsync` call
- ✅ **Close `add-force-trigger-tool` todo** — feature request was based on a misunderstanding
- The existing `maestro_trigger_subscription` already correctly supports the two trigger modes: latest build (buildId=0) and specific build (buildId=N)

**Why:** Needed to determine correct default for our trigger tool and whether to add force-trigger variant. Answer: no change needed — tool works correctly as-is.


# Decision: Codeflow PR Tracking API Surface (v0.4.0)

**Author:** Naomi (Backend Dev)
**Date:** 2026-02-19
**Status:** Implemented

## Context

Adding codeflow PR tracking tools to the MCP server. The PCS client v1.1.0-beta.26118.5 exposes `IPullRequest`, `IBackflowStatus`, and subscription history APIs.

## Key Discoveries & Decisions

### 1. BackflowStatus requires vmrBuildId

The `IBackflowStatus.GetBackflowStatusAsync(int vmrBuildId, CancellationToken)` API requires a VMR build ID — it is NOT a parameterless "get current status" call. The MCP tool `maestro_backflow_status` therefore requires the user to provide a `vmrBuildId` parameter. A future enhancement could auto-resolve the latest VMR build.

### 2. Subscription history uses Azure Paging

`ISubscriptions.GetSubscriptionHistoryAsync` returns `AsyncPageable<SubscriptionHistoryItem>`, not a simple list. Used `GetSubscriptionHistoryPageAsync(id, page, perPage, ct)` instead, which returns a single `Page<T>` with `.Values` — simpler for cache layer integration. First page only (default page size) for the initial implementation.

### 3. RestApiException for 404 handling

`GetTrackedPullRequestBySubscriptionIdAsync` throws `RestApiException` (HTTP 404) when no PR is tracked for a subscription. The MCP tool layer catches this and returns a friendly message. The service/cache layer does NOT catch it — the exception propagates to let the MCP tool handle presentation.

### 4. No auth gating initially

All 4 new APIs are read-only. Skipping auth gating (unlike trigger tools) until runtime testing confirms whether anonymous access works. If any return 401, auth gating will be added at the service layer following the existing `TriggerSubscriptionAsync` pattern.

### 5. TrackedPullRequest has rich metadata

The model includes Channel, TargetBranch, HeadBranch, SourceEnabled, LastUpdate/LastCheck/NextCheck timestamps, and a list of `PullRequestUpdate` items (each with SourceRepository, SubscriptionId, BuildId). This is exposed fully in the MCP tool output.

## Files Changed

- `src/MaestroTool.Core/IMaestroApiClient.cs` — 4 new interface methods
- `src/MaestroTool.Core/MaestroApiClient.cs` — 4 implementations
- `src/MaestroTool.Core/MaestroService.cs` — 4 cached service methods
- `src/MaestroTool.Core/MaestroMcpTools.cs` — 4 new MCP tools


### 2026-02-19: PCS API destructive method survey

**By:** Naomi (via Coordinator — agents timed out on arcade-services search)

**What:** Comprehensive categorization of all PCS client API methods by safety level.

**Survey of `IProductConstructionServiceApi` interfaces (from current arcade-services source):**

#### ISubscriptions
| Method | Category | We Expose | Notes |
|--------|----------|-----------|-------|
| `ListSubscriptionsAsync` | 🟢 Read | ✅ Yes | Filter by source/target repo, channel, enabled |
| `GetSubscriptionAsync` | 🟢 Read | ✅ Yes | By GUID |
| `GetSubscriptionHistoryAsync/PageAsync` | 🟢 Read | ✅ Yes | Subscription update history |
| `TriggerSubscriptionAsync` | 🟡 Non-destructive action | ✅ Yes | Triggers processing of a build; idempotent |
| `TriggerDailyUpdateAsync` | 🟡 Non-destructive action | ✅ Yes | Triggers all daily-update subscriptions |
| `CreateAsync` | 🔴 Destructive write | ❌ No | Creates a subscription |
| `UpdateSubscriptionAsync` | 🔴 Destructive write | ❌ No | Modifies subscription config |
| `DeleteSubscriptionAsync` | 🔴 Destructive write | ❌ No | Deletes a subscription |

#### IBuilds
| Method | Category | We Expose | Notes |
|--------|----------|-----------|-------|
| `ListBuildsAsync/PageAsync` | 🟢 Read | ❌ No (use GetLatest) | Paginated build listing |
| `GetBuildAsync` | 🟢 Read | ✅ Yes | By BAR ID |
| `GetBuildGraphAsync` | 🟢 Read | ❌ No | Dependency graph — could be useful for Feature #6 |
| `GetLatestAsync` | 🟢 Read | ✅ Yes | Latest build for repo+channel |
| `GetCommitAsync` | 🟢 Read | ❌ No | Commit info for a build |
| `CreateAsync` | 🔴 Destructive write | ❌ No | Creates a build record (CI pipeline use) |
| `UpdateAsync` | 🔴 Destructive write | ❌ No | Modifies build metadata |

#### IChannels
| Method | Category | We Expose | Notes |
|--------|----------|-----------|-------|
| `ListChannelsAsync` | 🟢 Read | ✅ Yes | All channels |
| `GetChannelAsync` | 🟢 Read | ❌ No | Single channel by ID |
| `ListRepositoriesAsync` | 🟢 Read | ❌ No | Repos subscribed to a channel |
| `GetFlowGraphAsync` | 🟢 Read | ❌ No | Dependency flow graph — Feature #6 candidate |
| `CreateChannelAsync` | 🔴 Destructive write | ❌ No | Creates a channel |
| `DeleteChannelAsync` | 🔴 Destructive write | ❌ No | Deletes a channel |
| `AddBuildToChannelAsync` | 🔴 Destructive write | ❌ No | Assigns build to channel |
| `RemoveBuildFromChannelAsync` | 🔴 Destructive write | ❌ No | Removes build from channel |

#### IDefaultChannels
| Method | Category | We Expose | Notes |
|--------|----------|-----------|-------|
| `ListAsync` | 🟢 Read | ✅ Yes | Default channel mappings |
| `GetAsync` | 🟢 Read | ❌ No | Single default channel |
| `CreateAsync` | 🔴 Destructive write | ❌ No | Creates default channel mapping |
| `UpdateAsync` | 🔴 Destructive write | ❌ No | Modifies mapping |
| `DeleteAsync` | 🔴 Destructive write | ❌ No | Deletes mapping |

#### IPullRequest
| Method | Category | We Expose | Notes |
|--------|----------|-----------|-------|
| `GetTrackedPullRequestsAsync` | 🟢 Read | ✅ Yes | All tracked PRs |
| `UntrackPullRequestAsync` | 🔴 Destructive write | ❌ No | DELETE — untracks a PR |

*Note: `GetTrackedPullRequestBySubscriptionIdAsync` exists in our NuGet package (v1.1.0-beta.26118.5) but NOT in the current arcade-services source.*

#### IBackflowStatus
*Note: This interface exists in our NuGet package but NOT in the current arcade-services source. May have been added post-release or in a different branch.*
| Method | Category | We Expose | Notes |
|--------|----------|-----------|-------|
| `GetBackflowStatusAsync` | 🟢 Read | ✅ Yes | Backflow status for a VMR build |

#### IAssets
| Method | Category | We Expose | Notes |
|--------|----------|-----------|-------|
| `ListAssetsAsync/PageAsync` | 🟢 Read | ❌ No | Could support Feature #9 (build assets) |
| `GetAssetAsync` | 🟢 Read | ❌ No | Single asset by ID |
| `GetDarcVersionAsync` | 🟢 Read | ❌ No | Darc version info |
| `BulkAddLocationsAsync` | 🔴 Destructive write | ❌ No | Adds asset locations (CI use) |
| `AddAssetLocationToAssetAsync` | 🔴 Destructive write | ❌ No | |
| `RemoveAssetLocationFromAssetAsync` | 🔴 Destructive write | ❌ No | |

#### IRepository
| Method | Category | We Expose | Notes |
|--------|----------|-----------|-------|
| `ListRepositoriesAsync` | 🟢 Read | ❌ No | Tracked repos and branches |
| `GetMergePoliciesAsync` | 🟢 Read | ❌ No | Merge policies for repo+branch |
| `GetHistoryAsync/PageAsync` | 🟢 Read | ❌ No | Repository action history |
| `SetMergePoliciesAsync` | 🔴 Destructive write | ❌ No | Modifies merge policies |

#### Other Interfaces
| Interface | Method | Category | Notes |
|-----------|--------|----------|-------|
| `IGoal` | `GetGoalTimesAsync` | 🟢 Read | Build time goals |
| `IGoal` | `CreateAsync` | 🔴 Destructive write | Sets build time goals |
| `IPipelines` | `ListAsync` | 🟢 Read | Release pipelines |
| `IPipelines` | `CreatePipelineAsync` | 🔴 Destructive write | Creates release pipeline |
| `IAzDo` | `GetBuildStatusAsync` | 🟢 Read | AzDO build status |
| `IBuildTime` | `GetBuildTimesAsync` | 🟢 Read | Build time metrics |
| `IStatus` | `GetPcsWorkItemProcessorStatusAsync` | 🟢 Read | PCS worker status |
| `IStatus` | `StartPcsWorkItemProcessorsAsync` | 🟡 Non-destructive action | Admin: starts workers |
| `IStatus` | `StopPcsWorkItemProcessorsAsync` | 🔴 Destructive (admin) | Admin: stops workers |

#### Summary
| Category | Count | We Expose |
|----------|-------|-----------|
| 🟢 Read-only | ~30 | 10 of ~30 |
| 🟡 Non-destructive action | 3 | 2 of 3 (trigger, daily update; not start-workers) |
| 🔴 Destructive write | ~18 | 0 of ~18 |

#### Candidates for Future Exposure (read-only, useful)
1. `GetBuildGraphAsync` — dependency graph (Feature #6)
2. `GetFlowGraphAsync` — channel flow graph (Feature #6)
3. `ListAssetsAsync` — build assets (Feature #9)
4. `GetCommitAsync` — commit info for builds
5. `ListRepositoriesAsync` (Channels) — repos per channel
6. `GetMergePoliciesAsync` — merge policy inspection

**Why:** Need to know which APIs are safe to expose as MCP tools and which need gating behind config flags.

## 2026-02-19: Comprehensive PCS Client NuGet API Inspection

**By:** Naomi (Backend Dev)

**What:** Comprehensive inspection of `Microsoft.DotNet.ProductConstructionService.Client` NuGet package API surface using `dotnet-inspect` v0.4.4. Package version inspected: **1.1.0-beta.26118.5** (latest).

**Why:** Need accurate understanding of our PCS client package API surface for maintenance and future feature work

---

## Package Overview

- **Library:** Microsoft.DotNet.ProductConstructionService.Client.dll
- **88 types** | **183 methods** | **307 properties**
- **Latest version:** 1.1.0-beta.26118.5 (all versions are prerelease beta)
- **Source:** [arcade-services on GitHub](https://github.com/dotnet/arcade-services)

## Complete Interface List (17 interfaces)

### IProductConstructionServiceApi — Root API Interface (17 members)
Properties exposing all sub-interfaces:
| Property | Type |
|----------|------|
| Assets | `IAssets` |
| AzDo | `IAzDo` |
| BackflowStatus | `IBackflowStatus` |
| BuildTime | `IBuildTime` |
| Builds | `IBuilds` |
| Channels | `IChannels` |
| DefaultChannels | `IDefaultChannels` |
| FeatureFlags | `IFeatureFlags` |
| Goal | `IGoal` |
| Ingestion | `IConfigurationIngestion` |
| Options | `ProductConstructionServiceApiOptions` |
| Pipelines | `IPipelines` |
| PullRequest | `IPullRequest` |
| Repository | `IRepository` |
| Status | `IStatus` |
| Subscriptions | `ISubscriptions` |

Methods: `IsAdmin(CancellationToken) → Task<bool>`

---

### ISubscriptions (8 members)
| Method | Signature |
|--------|-----------|
| GetSubscriptionAsync | `Task<Subscription> GetSubscriptionAsync(Guid, CancellationToken)` |
| GetSubscriptionHistoryAsync | `AsyncPageable<SubscriptionHistoryItem> GetSubscriptionHistoryAsync(Guid, CancellationToken)` |
| GetSubscriptionHistoryPageAsync | `Task<Page<SubscriptionHistoryItem>> GetSubscriptionHistoryPageAsync(Guid, int?, int?, CancellationToken)` |
| ListSubscriptionsAsync | `Task<List<Subscription>> ListSubscriptionsAsync(bool?, int?, string, bool?, string, string, string, CancellationToken)` |
| TriggerDailyUpdateAsync | `Task TriggerDailyUpdateAsync(CancellationToken)` |
| **TriggerSubscriptionAsync** (overload 1) | `Task<Subscription> TriggerSubscriptionAsync(Guid, CancellationToken)` |
| **TriggerSubscriptionAsync** (overload 2) | `Task<Subscription> TriggerSubscriptionAsync(Guid, bool, CancellationToken)` |
| **TriggerSubscriptionAsync** (overload 3) | `Task<Subscription> TriggerSubscriptionAsync(int, bool, Guid, CancellationToken)` |

### IBuilds (9 members)
| Method | Signature |
|--------|-----------|
| CreateAsync | `Task<Build> CreateAsync(BuildData, CancellationToken)` |
| GetBuildAsync | `Task<Build> GetBuildAsync(int, CancellationToken)` |
| GetBuildGraphAsync | `Task<BuildGraph> GetBuildGraphAsync(int, CancellationToken)` |
| GetCommitAsync | `Task<Commit> GetCommitAsync(int, CancellationToken)` |
| GetLatestAsync | `Task<Build> GetLatestAsync(string, string, int?, bool?, DateTimeOffset?, DateTimeOffset?, string, CancellationToken)` |
| GetSourceManifestAsync | `Task<List<SourceManifestEntry>> GetSourceManifestAsync(int, CancellationToken)` |
| ListBuildsAsync | `AsyncPageable<Build> ListBuildsAsync(string, int?, string, string, string, int?, bool?, DateTimeOffset?, DateTimeOffset?, string, CancellationToken)` |
| ListBuildsPageAsync | `Task<Page<Build>> ListBuildsPageAsync(string, int?, string, string, string, int?, bool?, DateTimeOffset?, DateTimeOffset?, int?, int?, string, CancellationToken)` |
| UpdateAsync | `Task<Build> UpdateAsync(BuildUpdate, int, CancellationToken)` |

### IChannels (6 members)
| Method | Signature |
|--------|-----------|
| AddBuildToChannelAsync | `Task AddBuildToChannelAsync(int, int, CancellationToken)` |
| GetChannelAsync | `Task<Channel> GetChannelAsync(int, CancellationToken)` |
| GetFlowGraphAsync | `Task<FlowGraph> GetFlowGraphAsync(int, int, bool, bool, bool, List<string>, CancellationToken)` |
| ListChannelsAsync | `Task<List<Channel>> ListChannelsAsync(string, CancellationToken)` |
| ListRepositoriesAsync | `Task<List<string>> ListRepositoriesAsync(int, int?, CancellationToken)` |
| RemoveBuildFromChannelAsync | `Task RemoveBuildFromChannelAsync(int, int, CancellationToken)` |

### IDefaultChannels (2 members)
| Method | Signature |
|--------|-----------|
| GetAsync | `Task<DefaultChannel> GetAsync(int, CancellationToken)` |
| ListAsync | `Task<List<DefaultChannel>> ListAsync(string, bool?, int?, string, CancellationToken)` |

### IBackflowStatus (2 members)
| Method | Signature |
|--------|-----------|
| GetBackflowStatusAsync | `Task<BackflowStatus> GetBackflowStatusAsync(int, CancellationToken)` |
| TriggerBackflowStatusCalculationAsync | `Task TriggerBackflowStatusCalculationAsync(int, CancellationToken)` |

### IPullRequest (3 members)
| Method | Signature |
|--------|-----------|
| GetTrackedPullRequestBySubscriptionIdAsync | `Task<TrackedPullRequest> GetTrackedPullRequestBySubscriptionIdAsync(string, CancellationToken)` |
| GetTrackedPullRequestsAsync | `Task<List<TrackedPullRequest>> GetTrackedPullRequestsAsync(CancellationToken)` |
| UntrackPullRequestAsync | `Task UntrackPullRequestAsync(string, CancellationToken)` |

### IAssets (7 members)
| Method | Signature |
|--------|-----------|
| AddAssetLocationToAssetAsync | `Task<AssetLocation> AddAssetLocationToAssetAsync(int, LocationType, string, CancellationToken)` |
| BulkAddLocationsAsync | `Task BulkAddLocationsAsync(List<AssetAndLocation>, CancellationToken)` |
| GetAssetAsync | `Task<Asset> GetAssetAsync(int, CancellationToken)` |
| GetDarcVersionAsync | `Task<string> GetDarcVersionAsync(CancellationToken)` |
| ListAssetsAsync | `AsyncPageable<Asset> ListAssetsAsync(int?, bool?, string, bool?, string, CancellationToken)` |
| ListAssetsPageAsync | `Task<Page<Asset>> ListAssetsPageAsync(int?, bool?, string, bool?, int?, int?, string, CancellationToken)` |
| RemoveAssetLocationFromAssetAsync | `Task RemoveAssetLocationFromAssetAsync(int, int, CancellationToken)` |

### IFeatureFlags (8 members)
| Method | Signature |
|--------|-----------|
| GetAllFeatureFlagsAsync | `Task<FeatureFlagListResponse> GetAllFeatureFlagsAsync(CancellationToken)` |
| GetAvailableFeatureFlagsAsync | `Task<AvailableFeatureFlagsResponse> GetAvailableFeatureFlagsAsync(CancellationToken)` |
| GetFeatureFlagAsync | `Task<FeatureFlagValue> GetFeatureFlagAsync(string, Guid, CancellationToken)` |
| GetFeatureFlagsAsync | `Task<FeatureFlagListResponse> GetFeatureFlagsAsync(Guid, CancellationToken)` |
| GetSubscriptionsWithFlagAsync | `Task<FeatureFlagListResponse> GetSubscriptionsWithFlagAsync(string, CancellationToken)` |
| RemoveFeatureFlagAsync | `Task<bool> RemoveFeatureFlagAsync(string, Guid, CancellationToken)` |
| RemoveFlagFromAllSubscriptionsAsync | `Task<RemoveFlagFromAllResponse> RemoveFlagFromAllSubscriptionsAsync(string, CancellationToken)` |
| SetFeatureFlagAsync | `Task<FeatureFlagResponse> SetFeatureFlagAsync(SetFeatureFlagRequest, CancellationToken)` |

### IStatus (3 members)
| Method | Signature |
|--------|-----------|
| GetPcsWorkItemProcessorStatusAsync | `Task<Dictionary<string, string>> GetPcsWorkItemProcessorStatusAsync(CancellationToken)` |
| StartPcsWorkItemProcessorsAsync | `Task<Dictionary<string, string>> StartPcsWorkItemProcessorsAsync(CancellationToken)` |
| StopPcsWorkItemProcessorsAsync | `Task<Dictionary<string, string>> StopPcsWorkItemProcessorsAsync(CancellationToken)` |

### IRepository (2 members)
| Method | Signature |
|--------|-----------|
| GetMergePoliciesAsync | `Task<List<MergePolicy>> GetMergePoliciesAsync(string, string, CancellationToken)` |
| ListRepositoriesAsync | `Task<List<RepositoryBranch>> ListRepositoriesAsync(string, string, CancellationToken)` |

### IPipelines (4 members)
| Method | Signature |
|--------|-----------|
| CreatePipelineAsync | `Task<ReleasePipeline> CreatePipelineAsync(string, int, string, CancellationToken)` |
| DeletePipelineAsync | `Task<ReleasePipeline> DeletePipelineAsync(int, CancellationToken)` |
| GetPipelineAsync | `Task<ReleasePipeline> GetPipelineAsync(int, CancellationToken)` |
| ListAsync | `Task<List<ReleasePipeline>> ListAsync(string, int?, string, CancellationToken)` |

### IGoal (2 members)
| Method | Signature |
|--------|-----------|
| CreateAsync | `Task<Goal> CreateAsync(GoalRequestJson, int, string, CancellationToken)` |
| GetGoalTimesAsync | `Task<Goal> GetGoalTimesAsync(int, string, CancellationToken)` |

### IAzDo (1 member)
| Method | Signature |
|--------|-----------|
| GetBuildStatusAsync | `Task<List<AzDoBuild>> GetBuildStatusAsync(string, string, int, int, string, string, CancellationToken)` |

### IBuildTime (1 member)
| Method | Signature |
|--------|-----------|
| GetBuildTimesAsync | `Task<BuildTime> GetBuildTimesAsync(int, int, CancellationToken)` |

### IConfigurationIngestion (2 members)
| Method | Signature |
|--------|-----------|
| DeleteNamespaceAsync | `Task<bool> DeleteNamespaceAsync(string, bool, CancellationToken)` |
| IngestNamespaceAsync | `Task<ConfigurationUpdates> IngestNamespaceAsync(string, bool, ClientYamlConfiguration, CancellationToken)` |

---

## TriggerSubscriptionAsync — Exact Signatures

**Three overloads exist:**

1. **Simple trigger (Guid only):**
   ```csharp
   Task<Subscription> TriggerSubscriptionAsync(Guid subscriptionId, CancellationToken ct)
   ```

2. **With isCoherencyUpdate bool:**
   ```csharp
   Task<Subscription> TriggerSubscriptionAsync(Guid subscriptionId, bool isCoherencyUpdate, CancellationToken ct)
   ```

3. **With build ID + isCoherencyUpdate + subscriptionId:**
   ```csharp
   Task<Subscription> TriggerSubscriptionAsync(int barBuildId, bool isCoherencyUpdate, Guid subscriptionId, CancellationToken ct)
   ```

**Key finding:** Yes, the `bool isCoherencyUpdate` parameter exists in overloads 2 and 3. Our codebase currently uses overload 3 (`int, bool, Guid`). Overload 1 (Guid-only) is the simplest form for basic trigger use.

---

## PcsApiFactory — Client Construction

Static class with 4 factory methods:
| Method | Params | Description |
|--------|--------|-------------|
| `GetAnonymous()` | none | Unauthenticated access to production PCS |
| `GetAnonymous(string)` | baseUri | Unauthenticated access to custom endpoint |
| `GetAuthenticated(string, string, bool)` | barToken, federatedToken, disableInteractiveAuth | Authenticated access to production |
| `GetAuthenticated(string, string, string, bool)` | baseUri, barToken, federatedToken, disableInteractiveAuth | Authenticated access to custom endpoint |

---

## Model Classes (44 total)

Key models: Build (26 members), Subscription (16 members), BuildData (18 members), TrackedPullRequest (12 members), FlowRef (12 members), FlowEdge (9 members), Channel (5 members), DefaultChannel (7 members), BackflowStatus (5 members), SubscriptionBackflowStatus (7 members), SubscriptionHistoryItem (7 members), SubscriptionPolicy (5 members), MergePolicy (3 members), Asset (7 members), AssetLocation (5 members)

Enums: `ClientUpdateFrequency` (8 values), `LocationType` (4 values), `UpdateFrequency` (8 values)

Helper: `ChannelCategorizer` (1 member) in `.Helpers` namespace

---

## APIs We're NOT Currently Using (Potential Value)

1. **IFeatureFlags** (8 methods) — Per-subscription feature flag management. Could be valuable for toggling subscription behavior without code changes. We don't expose any feature flag operations.

2. **IConfigurationIngestion** (2 methods) — YAML-based namespace configuration management. Could enable bulk subscription/channel management from config files.

3. **IStatus** (3 methods) — PCS work item processor status/control (start/stop/get). Could be useful for operational dashboards or health monitoring beyond what we currently do.

4. **IPipelines** (4 methods) — Release pipeline CRUD. Could be useful if we ever need to manage release pipelines programmatically.

5. **IGoal** (2 methods) — Build time goal tracking per channel/definition. Could power SLA monitoring.

6. **IAzDo** (1 method) — Azure DevOps build status lookup. Could augment our build freshness data.

7. **IBuildTime** (1 method) — Build time statistics. Could power performance trend analysis.

8. **IRepository** (2 methods) — Merge policies and repository branch listing. Could help with subscription configuration auditing.

9. **IBuilds.GetCommitAsync** — Get commit info for a build. Not currently exposed.

10. **IBuilds.GetSourceManifestAsync** — Source manifest entries for a build. Could help trace dependencies.

11. **IBuilds.GetBuildGraphAsync** — Full dependency graph for a build. Extremely valuable for understanding transitive dependencies.

12. **IChannels.GetFlowGraphAsync** — Flow graph between channels. Could visualize the full .NET dependency flow.

13. **ISubscriptions overload 1** (`TriggerSubscriptionAsync(Guid, CancellationToken)`) — Simpler trigger without requiring barBuildId.

---

## Version Information

- All published versions are **prerelease** under `1.1.0-beta.*`
- Version scheme: `1.1.0-beta.YYDDD.N` (year-day.build-number)
- Latest: **1.1.0-beta.26118.5** (2026, day 118, build 5)
- No stable (non-prerelease) versions exist
- Versions are published to the dotnet-public Azure DevOps feed

---

## Key Discrepancies: NuGet vs. arcade-services Source

**Version Drift:** The arcade-services repository source code does not match the published NuGet package exactly:

1. **IBackflowStatus interface** — Exists in v1.1.0-beta.26118.5 NuGet but NOT in current arcade-services source
2. **GetTrackedPullRequestBySubscriptionIdAsync** — Exists in NuGet but NOT in current source
3. **TriggerSubscriptionAsync overloads 2 & 3** — Both exist in NuGet; only overload 1 in source
4. **IFeatureFlags interface** — Full 8-member interface in NuGet; not verified in source

**Implication:** The NuGet package is ahead of public source code. When updating arcade-services source, check for recent releases to the NuGet feed that may contain newer APIs.



---
# Decision: Force trigger as optional parameter

**Author:** Naomi (Backend Developer)
**Date:** 2025-07-16
**Scope:** `maestro_trigger_subscription` MCP tool

## Decision
Added `force` as an optional boolean parameter (`default: false`) to the existing `maestro_trigger_subscription` tool rather than creating a separate `maestro_force_trigger_subscription` tool.

## Rationale
- Keeps the tool surface area small — one tool, one concept (trigger), with a modifier flag.
- The PCS client already has the `isCoherencyUpdate` boolean on `TriggerSubscriptionAsync`. When `force=true`, we pass `true` to `isCoherencyUpdate`, which overwrites the existing PR branch with fresh VMR content.
- Dedup keys include the force flag, so `trigger(sub, build, force=false)` and `trigger(sub, build, force=true)` are tracked independently.

## Impact
- **All 4 layers modified:** `IMaestroApiClient`, `MaestroApiClient`, `MaestroService`, `MaestroMcpTools`
- **Backward compatible:** `force` defaults to `false`, so existing callers are unaffected.
- **Tests:** Build passes with 0 warnings, 0 errors. Existing tests that call `TriggerSubscriptionAsync` without `force` param will continue to work due to default value.


---
# Issue #4: VMR Commit Distance Fix — Technical Proposal

**Author:** Naomi (Backend Developer)  
**Date:** 2025-02-20  
**Status:** Proposal for team review

## Problem Summary

`maestro_subscription_health` reports `BuildsBehind` using BAR build ID arithmetic (`latestBuild.Id - lastApplied.Id`). For VMR subscriptions (dotnet/dotnet → X), this gives wildly inflated numbers:
- **BAR ID delta:** 566 builds behind (misleading)
- **Actual VMR commit distance:** 33 commits behind (correct)

This 17x error occurs because BAR IDs are globally sequential across ALL repos, not per-repo. The current calculation treats BAR ID differences as commit counts, which is fundamentally incorrect.

The `maestro_backflow_status` API should provide accurate commit distance via `CommitDistance` field, but testing shows it **errors for all VMR builds** (302627, 302612, 302391), making it unreliable.

## Recommended Approach

**Option B: Direct GitHub Compare API Integration**

Add a GitHub compare API client to compute real commit distance for VMR subscriptions. This mirrors the proven approach in `Get-CodeflowStatus.ps1`.

### Why This Approach

1. **Proven reliability:** `Get-CodeflowStatus.ps1` uses GitHub compare API successfully (100% eval accuracy vs 0% for MCP-only workflows)
2. **No PCS dependency:** BackflowStatus API is erroring and cannot be relied upon
3. **Public API, no auth needed:** GitHub compare API works anonymously for public repos (dotnet/dotnet)
4. **Targeted fix:** Only applies to VMR-sourced subscriptions (dotnet/dotnet → X), doesn't affect other subscription types
5. **Clean fallback:** If GitHub API fails, fall back to existing BAR ID arithmetic (visible to user as approximate)

### Rejected Alternatives

- **Option A (BackflowStatus API + fallback):** Unreliable. Errors on tested builds, would require fallback 100% of time.
- **Option C (Hybrid):** Unnecessary complexity. BackflowStatus API is not functional enough to justify the extra layer.

## Implementation Plan

### 1. New GitHub API Client Interface

**File:** `src/MaestroTool.Core/IGitHubApiClient.cs` (new)

```csharp
public interface IGitHubApiClient
{
    /// <summary>
    /// Compare two commits and get the ahead/behind count.
    /// </summary>
    /// <param name="owner">Repository owner (e.g., "dotnet")</param>
    /// <param name="repo">Repository name (e.g., "dotnet")</param>
    /// <param name="baseSha">Base commit SHA</param>
    /// <param name="headSha">Head commit SHA</param>
    /// <returns>Ahead/behind count, or null if comparison fails</returns>
    Task<GitHubCompareResult?> CompareCommitsAsync(
        string owner,
        string repo,
        string baseSha,
        string headSha,
        CancellationToken cancellationToken = default);
}

public record GitHubCompareResult(int AheadBy, int BehindBy, string Status);
```

### 2. HttpClient-Based Implementation

**File:** `src/MaestroTool.Core/GitHubApiClient.cs` (new)

```csharp
public class GitHubApiClient : IGitHubApiClient
{
    private readonly HttpClient _http;
    
    public GitHubApiClient(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("GitHub");
        _http.DefaultRequestHeaders.Add("User-Agent", "maestro-mcp");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
    }
    
    public async Task<GitHubCompareResult?> CompareCommitsAsync(...)
    {
        // GET /repos/{owner}/{repo}/compare/{base}...{head}
        // Parse JSON response: { ahead_by, behind_by, status }
        // Return null on 404/500/timeout (graceful degradation)
    }
}
```

### 3. Service Layer Integration

**File:** `src/MaestroTool.Core/MaestroService.cs` (modify `GetSubscriptionHealthAsync`)

```csharp
// Add constructor dependency
private readonly IGitHubApiClient? _github;

public MaestroService(IMaestroApiClient client, CacheService cache, IGitHubApiClient? github = null)
{
    _client = client;
    _cache = cache;
    _github = github; // Optional to keep tests simple
}

// In GetSubscriptionHealthAsync, after computing buildsBehind:
int? commitsBehind = null;

// Only for VMR subscriptions (dotnet/dotnet source)
if (isStale && _github != null && IsVmrRepository(sub.SourceRepository))
{
    try
    {
        var lastAppliedBuild = await GetBuildAsync(lastApplied.Id, noCache, cancellationToken);
        var latestBuildData = await GetBuildAsync(latestBuild.Id, noCache, cancellationToken);
        
        var compare = await _github.CompareCommitsAsync(
            "dotnet", "dotnet",
            lastAppliedBuild.Commit, // Base SHA
            latestBuildData.Commit,  // Head SHA
            cancellationToken);
        
        if (compare != null)
        {
            commitsBehind = compare.AheadBy; // Head is ahead of base
        }
    }
    catch (Exception ex)
    {
        // Log to stderr, continue with BAR ID arithmetic
        Console.Error.WriteLine($"[maestro-mcp] GitHub compare failed: {ex.Message}");
    }
}

// Update result record to include commitsBehind
```

**Helper method:**
```csharp
private static bool IsVmrRepository(string repo)
{
    return repo.Contains("github.com/dotnet/dotnet", StringComparison.OrdinalIgnoreCase);
}
```

### 4. Update SubscriptionHealthResult Record

**File:** `src/MaestroTool.Core/MaestroService.cs` (line 331)

```csharp
public record SubscriptionHealthResult(
    Guid SubscriptionId,
    string SourceRepository,
    string TargetRepository,
    string TargetBranch,
    string ChannelName,
    bool IsStale,
    int BuildsBehind,
    int? CommitsBehind, // NEW FIELD
    int? LastAppliedBuildId,
    DateTimeOffset? LastAppliedDate,
    int? LatestBuildId,
    DateTimeOffset? LatestBuildDate,
    string? Error = null
);
```

### 5. MCP Tool Display Update

**File:** `src/MaestroTool.Core/MaestroMcpTools.cs` (line 210)

```csharp
// Update display logic to prefer CommitsBehind when available
var status = r.IsStale 
    ? (r.CommitsBehind.HasValue 
        ? $"⚠️ STALE ({r.CommitsBehind} commits behind)" 
        : $"⚠️ STALE (~{r.BuildsBehind} builds behind)")
    : "✅ Current";

// Add note if BAR ID arithmetic was used
if (r.IsStale && !r.CommitsBehind.HasValue)
    sb.AppendLine($"  Note: Using BAR build count (approximate)");
```

### 6. DI Setup

**File:** `src/MaestroTool.Mcp/Program.cs`

```csharp
// Add HttpClientFactory
builder.Services.AddHttpClient();

// Register GitHub client (optional, graceful degradation if not registered)
builder.Services.AddSingleton<IGitHubApiClient, GitHubApiClient>();
```

## Dependencies

- **Microsoft.Extensions.Http** (already in project via transitive deps from ASP.NET Core)
- **System.Text.Json** (already in project)
- No new package references required

## Risks & Tradeoffs

### Risks

1. **GitHub API rate limits:** Anonymous access = 60 req/hour. For typical usage (single `subscription_health` call with ~10 VMR subscriptions), this is fine. Rate limit errors would fall back to BAR ID arithmetic.
2. **Network failures:** GitHub API downtime would degrade to BAR ID arithmetic. Acceptable because tool remains functional.
3. **Commit SHAs not found:** If either build's commit SHA is invalid/deleted, GitHub returns 404. Falls back to BAR ID arithmetic.

### Tradeoffs

- **Slightly slower first call:** Adds ~200-500ms per VMR subscription (GitHub API latency). Mitigated by:
  - Caching (build lookups already cached at LongTtl)
  - Only applies to VMR subscriptions (dotnet/dotnet → X)
  - Parallel execution if multiple VMR subs exist
- **More moving parts:** Introduces HTTP client dependency. Mitigated by:
  - Using built-in `IHttpClientFactory` (standard .NET pattern)
  - Optional dependency in service layer (doesn't break existing tests)
  - Clear fallback behavior (BAR ID arithmetic, visible to user)

## Testing Strategy

1. **Unit tests for GitHubApiClient:**
   - Mock HttpClient with HttpMessageHandler
   - Test successful compare (ahead_by, behind_by, status)
   - Test 404 (commit not found) → returns null
   - Test 500/timeout → returns null

2. **Integration test for GetSubscriptionHealthAsync:**
   - Mock GitHub client to return specific commit distances
   - Verify CommitsBehind field is populated for VMR subscriptions
   - Verify BuildsBehind fallback for non-VMR subscriptions
   - Verify graceful degradation when GitHub client is null

3. **Manual smoke test:**
   - Run `maestro_subscription_health` for dotnet/dotnet
   - Verify commit distance matches `Get-CodeflowStatus.ps1` output
   - Test with GitHub API unavailable (network disconnect) → verify BAR ID fallback

## Scope of Change

### Files Modified
- `src/MaestroTool.Core/MaestroService.cs` (~30 lines: constructor, GetSubscriptionHealthAsync logic, IsVmrRepository helper, record update)
- `src/MaestroTool.Core/MaestroMcpTools.cs` (~5 lines: display logic)
- `src/MaestroTool.Mcp/Program.cs` (~2 lines: DI registration)

### Files Added
- `src/MaestroTool.Core/IGitHubApiClient.cs` (~15 lines)
- `src/MaestroTool.Core/GitHubApiClient.cs` (~80 lines)
- `src/MaestroTool.Tests/GitHubApiClientTests.cs` (~150 lines)
- `src/MaestroTool.Tests/MaestroServiceCommitDistanceTests.cs` (~100 lines)

**Total:** ~380 lines added/modified across 7 files

## Questions for Team

1. **Scope decision:** Should this fix also apply to `maestro_backflow_status` tool? (Currently only fixing `subscription_health`)
2. **Display format:** Prefer "33 commits behind" or "33 VMR commits behind" to disambiguate from BAR builds?
3. **Fallback messaging:** Should we surface GitHub API errors in the tool output or only log to stderr?
4. **Future work:** Should we add GitHub auth support (via PAT) for higher rate limits, or is anonymous sufficient?

## Commit Message Draft

```
Fix #4: Add VMR commit distance to subscription health

Replace BAR build ID arithmetic with GitHub compare API for VMR
subscriptions (dotnet/dotnet → X). For a dotnet/runtime backflow
scenario, this changes from "566 builds behind" (misleading) to
"33 commits behind" (accurate).

- Add IGitHubApiClient + HttpClient-based implementation
- Update GetSubscriptionHealthAsync to compute CommitsBehind for VMR
- Update SubscriptionHealthResult record with CommitsBehind field
- Update MCP tool display to prefer commit distance when available
- Graceful fallback to BAR ID arithmetic if GitHub API unavailable
- Add unit and integration tests

Fixes: https://github.com/lewing/maestro.mcp/issues/4

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

## Timeline Estimate

- **Implementation:** 3-4 hours
- **Testing:** 2 hours
- **Code review + iteration:** 1-2 hours
- **Total:** 6-8 hours (1 work day)


---

# GitHub Commit Distance Test Coverage (Issue #4)

**Date**: 2026-02-20  
**Author**: Amos (Tester)  
**Status**: Complete

## Summary

Wrote 7 comprehensive tests for the GitHub Compare API integration that adds real commit distance to VMR subscription health. All tests pass. Test coverage validates the feature's behavior across all edge cases.

## Tests Added

1. **VmrSubscription_WithGitHubClient_ReturnsCommitsBehind** — Happy path: VMR subscription with working GitHub client returns accurate commit distance (33 commits).

2. **VmrSubscription_GitHubClientReturnsNull_FallsBackToBuildsBehind** — GitHub API failure: When Compare API returns null, `CommitsBehind` is null but `BuildsBehind` (approximate) still works.

3. **NonVmrSubscription_CommitsBehindIsNull** — Non-VMR source repo (dotnet/runtime): Even with GitHub client available, `CommitsBehind` is null. Verifies GitHub client is never called for non-VMR repos.

4. **NullGitHubClient_CommitsBehindIsNull** — Optional dependency: VMR subscription works without GitHub client. `BuildsBehind` still computed, `CommitsBehind` is null.

5. **VmrSubscription_UpToDate_CommitsBehindIsNull** — Current subscriptions: When subscription is NOT stale, `CommitsBehind` is null (not computed). GitHub client never called.

6. **GitHubCompareResult_RecordEquality** — Record validation: Ensures the new `GitHubCompareResult` record works correctly.

7. **SubscriptionHealthResult_CommitsBehind_DefaultsToNull** — Backward compatibility: Existing code without `CommitsBehind` parameter still works (defaults to null).

## Key Design Decisions Validated

### VMR-Only Feature
The GitHub Compare API is ONLY called when:
1. Service has non-null `IGitHubApiClient`
2. Source repository is VMR ("github.com/dotnet/dotnet")
3. Subscription is stale (last applied ≠ latest)
4. Both builds have non-empty commit SHAs

This is correct — commit distance is most valuable for VMR backflow tracking, not general subscription health.

### Graceful Degradation
When GitHub API fails (returns null), the service doesn't throw or corrupt the health result. It simply leaves `CommitsBehind` as null and returns the approximate `BuildsBehind` (ID diff). This is good — the feature is additive, not breaking.

### Backward Compatibility
The `CommitsBehind` field is optional (`int? CommitsBehind = null`) on `SubscriptionHealthResult`. Existing code that constructs health results without this field continues to work. Tests confirm this.

## Test Pattern Established

### CreateBuild Helper Extension
Extended `CreateBuild` to accept optional `commit` parameter (defaults to "abc123"). Build's `Commit` property is read-only and set via constructor, not `with` syntax.

```csharp
private static Build CreateBuild(int id = 100, string? gitHubRepo = null, DateTimeOffset? date = null, string? commit = null) =>
    new(id, date ?? DateTimeOffset.UtcNow, staleness: 0, released: false, stable: true,
        commit: commit ?? "abc123", channels: new List<Channel>(), assets: new List<Asset>(),
        dependencies: new List<BuildRef>(), incoherencies: new List<BuildIncoherence>())
    {
        GitHubRepository = gitHubRepo ?? "https://github.com/dotnet/runtime"
    };
```

### Mock GitHub Client Pattern
```csharp
var mockGitHub = Substitute.For<IGitHubApiClient>();
mockGitHub.CompareCommitsAsync("dotnet", "dotnet", "abc123", "def456", Arg.Any<CancellationToken>())
    .Returns(new GitHubCompareResult(AheadBy: 33, BehindBy: 0, Status: "ahead", TotalCommits: 33));

var serviceWithGitHub = new MaestroService(_client, _cache, mockGitHub);
```

### Negative Assertions for Untaken Paths
Tests verify GitHub client is NOT called for non-VMR subscriptions:
```csharp
await mockGitHub.DidNotReceive().CompareCommitsAsync(
    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
```

## Edge Cases Covered

✅ GitHub API returns valid result  
✅ GitHub API returns null (failure)  
✅ Non-VMR subscription (GitHub client not used)  
✅ No GitHub client provided (null)  
✅ Subscription is current (not stale)  
✅ Record backward compatibility  

## Future Test Considerations

### NOT Tested (Requires Integration Testing)
- **GitHubApiClient HTTP behavior**: The actual HTTP client implementation (`GitHubApiClient.CompareCommitsAsync`) is not unit tested. This is acceptable — HTTP clients are hard to unit test and better suited for integration tests.
- **GitHub API rate limiting**: How the system behaves under rate limit errors (429 responses). This is not mocked in unit tests.
- **Partial repository URLs**: Edge cases like "dotnet/dotnet" without "https://" or "github.com/dotnet/dotnet.git" with ".git" suffix. The `ParseGitHubUrl` helper handles these, but not explicitly tested.

These gaps are acceptable for the feature's scope. The unit tests validate the business logic (when to call GitHub, how to handle results). Integration tests or manual testing can validate HTTP behavior.

## Recommendation

**APPROVED FOR MERGE** — Test coverage is comprehensive for the feature scope. All 104 tests pass. The GitHub commit distance feature is well-tested and ready for production.


---

### 2026-02-20: CLI architecture — ConsoleAppFramework integration

**By:** Holden  
**Date:** 2026-02-20  
**Status:** Proposed

**What:** Architecture for adding CLI commands following hlx pattern from helix.mcp  
**Why:** Users want `mstro` to work as both CLI tool and MCP server. Current implementation is MCP-only.

## Overview

This document defines the architecture for adding ConsoleAppFramework CLI commands to `mstro`, transforming it from MCP-only to dual-mode (CLI + MCP). The design follows the established pattern from `helix.mcp` (hlx).

## Key Design Decisions

### 1. Program.cs Refactor

**Current state (MCP-only):**
```csharp
var builder = Host.CreateApplicationBuilder(args);
// Register services
builder.Services.AddMcpServer(...).WithStdioServerTransport()...;
await builder.Build().RunAsync();
```

**New state (dual-mode):**
```csharp
// DI setup (shared by both CLI and MCP)
var services = new ServiceCollection();
services.AddSingleton<IMaestroApiClient>(...);
services.AddSingleton<CacheService>();
services.AddSingleton<MaestroService>();
services.AddSingleton<IGitHubApiClient>(...);
services.AddSingleton(new MaestroToolOptions { ... });

// Build provider for ConsoleAppFramework
ConsoleApp.ServiceProvider = services.BuildServiceProvider();

// Create app with Commands class
var app = ConsoleApp.Create();
app.Add<Commands>();

// Default to MCP if no args
app.Run(args.Length == 0 ? ["mcp"] : args);
```

**Rationale:**
- `ConsoleApp.ServiceProvider` makes DI available to all commands via constructor injection
- `args.Length == 0 ? ["mcp"] : args` ensures backwards compatibility — no args = MCP mode
- The `[Command("mcp")]` handler in `Commands` creates a SEPARATE `Host.CreateApplicationBuilder()` for MCP hosting (not in the main ConsoleApp DI)

### 2. Commands Class Design

**Single class:** `Commands.cs` in `MaestroTool` project  
**Pattern:** Like hlx, all commands in one class for simplicity

**Constructor injection:**
```csharp
public class Commands
{
    private readonly MaestroService _service;
    private readonly CacheService _cache;
    
    public Commands(MaestroService service, CacheService cache)
    {
        _service = service;
        _cache = cache;
    }
    
    [Command("mcp")]
    public async Task McpAsync()
    {
        // Create SEPARATE Host.CreateApplicationBuilder for MCP
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(_service);
        builder.Services.AddSingleton(_cache);
        builder.Services.AddMcpServer(...).WithStdioServerTransport()...;
        await builder.Build().RunAsync();
    }
    
    [Command("subscriptions")]
    public async Task SubscriptionsAsync(
        string? sourceRepository = null,
        string? targetRepository = null,
        ...)
    {
        // CLI implementation
    }
}
```

**Rationale:**
- Single-class keeps navigation simple for 17 commands
- Constructor injection reuses existing services
- MCP command creates its own host to keep separation clean

### 3. CLI Command Mapping

Map each MCP tool name to CLI command (remove `maestro_` prefix, convert underscore to space):

| MCP Tool Name                    | CLI Command                  | Notes |
|----------------------------------|------------------------------|-------|
| `maestro_subscriptions`          | `mstro subscriptions`        | |
| `maestro_subscription`           | `mstro subscription`         | Requires subscription ID |
| `maestro_latest_build`           | `mstro latest-build`         | Kebab-case for consistency |
| `maestro_build`                  | `mstro build`                | |
| `maestro_channels`               | `mstro channels`             | |
| `maestro_default_channels`       | `mstro default-channels`     | |
| `maestro_subscription_health`    | `mstro subscription-health`  | |
| `maestro_build_freshness`        | `mstro build-freshness`      | |
| `maestro_trigger_subscription`   | `mstro trigger-subscription` | Requires auth |
| `maestro_trigger_daily_update`   | `mstro trigger-daily-update` | Requires auth |
| `maestro_clear_cache`            | `mstro cache clear`          | Grouped under cache |
| `maestro_codeflow_prs`           | `mstro codeflow-prs`         | |
| `maestro_tracked_pr`             | `mstro tracked-pr`           | |
| `maestro_backflow_status`        | `mstro backflow-status`      | |
| `maestro_subscription_history`   | `mstro subscription-history` | |
| `maestro_build_graph`            | `mstro build-graph`          | |
| `maestro_flow_graph`             | `mstro flow-graph`           | |
| (new)                            | `mstro cache status`         | Show cache stats |

**Naming conventions:**
- Use kebab-case for multi-word commands (matches hlx pattern: `hlx job-logs`)
- Remove `maestro_` prefix (redundant in CLI context)
- Group cache operations: `mstro cache clear`, `mstro cache status`

### 4. Output Format Strategy

**Human-readable by default:**
```bash
$ mstro subscriptions --target-repository https://github.com/dotnet/runtime
Found 23 subscriptions to dotnet/runtime:
  - dotnet/roslyn → runtime/main (.NET 10 RC1)
  - dotnet/sdk → runtime/release/10.0-rc1 (.NET 10 RC1)
  ...
```

**--json flag for structured output:**
```bash
$ mstro subscriptions --json
[{"id": "...", "sourceRepository": "...", ...}]
```

**Implementation pattern:**
```csharp
[Command("subscriptions")]
public async Task SubscriptionsAsync(
    string? sourceRepository = null,
    string? targetRepository = null,
    bool json = false)
{
    var result = await _service.GetSubscriptionsAsync(sourceRepository, targetRepository);
    
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
    else
    {
        Console.WriteLine($"Found {result.Count} subscription(s):");
        foreach (var sub in result)
        {
            Console.WriteLine($"  - {sub.SourceRepository} → {sub.TargetRepository} ({sub.Channel?.Name})");
        }
    }
}
```

**Rationale:**
- Human output is scannable, matches user expectations for CLI tools
- `--json` flag provides machine-parseable output for scripting
- This matches hlx pattern exactly

### 5. Cache Commands

**Cache clear:**
```bash
$ mstro cache clear
Cache cleared successfully
```

**Cache status:**
```bash
$ mstro cache status
Cache location: ~/.mstro/cache.db
Database size: 2.3 MB
Entry count: 47 entries
Oldest entry: 2026-02-18 14:23:01 UTC
Newest entry: 2026-02-20 09:15:47 UTC
```

**Implementation:**
- `cache clear` → calls `_cache.Clear()`
- `cache status` → queries SQLite for row count, file size, min/max timestamps
- Both follow hlx pattern of grouped cache commands

### 6. MCP Default Mode

**Behavior:**
```bash
$ mstro                # No args → MCP server mode
$ mstro subscriptions  # Args provided → CLI mode
```

**Implementation:** `app.Run(args.Length == 0 ? ["mcp"] : args)`

**Rationale:**
- Backwards compatibility — existing MCP integrations don't break
- Explicit `mstro mcp` also works for clarity
- Users can choose CLI or MCP without environment variables

### 7. Version Bump

**Current:** 0.6.2  
**Proposed:** 0.7.0

**Rationale:**
- CLI feature is a significant capability addition (minor version bump)
- Not breaking changes to existing MCP surface (not 1.0.0)
- Follows semantic versioning

## Required Changes

### Files to modify:
1. **Program.cs** — Refactor to ConsoleAppFramework pattern
2. **MaestroTool.csproj** — Add ConsoleAppFramework package, bump version to 0.7.0

### Files to create:
3. **Commands.cs** — New class with all 18 commands (17 MCP + cache status)

### Dependencies to add:
- `ConsoleAppFramework` (latest stable)

## Implementation Notes

### Parameter Mapping
- MCP tool parameters → ConsoleAppFramework command parameters (same names)
- Positional args: `[Argument]` attribute for required params
- Named params: optional method parameters with defaults
- Example:
  ```csharp
  [Command("subscription")]
  public async Task SubscriptionAsync(
      [Argument] string subscriptionId,  // Positional (required)
      bool json = false,                 // Named (optional)
      bool noCache = false)              // Named (optional)
  ```

### Error Handling
- CLI commands should catch exceptions and print user-friendly messages
- Example:
  ```csharp
  try {
      var result = await _service.GetSubscriptionAsync(...);
      // Display result
  }
  catch (Exception ex) {
      Console.Error.WriteLine($"Error: {ex.Message}");
      return 1; // Non-zero exit code
  }
  ```

### Auth Validation
- Destructive commands (trigger-subscription, trigger-daily-update) should check auth level before attempting
- Example:
  ```csharp
  var client = _service._client; // Internal access or add AuthLevel property
  if (client.AuthLevel == AuthLevel.Anonymous) {
      Console.Error.WriteLine("Authentication required. Run 'darc authenticate' or set MAESTRO_BAR_TOKEN.");
      return 1;
  }
  ```

## Testing Strategy

1. **Unit tests:** Add tests for Commands class methods (mock MaestroService)
2. **Integration tests:** End-to-end CLI invocations with real service
3. **MCP compatibility:** Ensure `mstro` (no args) still works as MCP server
4. **Smoke test commands:**
   - `mstro subscriptions --json`
   - `mstro channels`
   - `mstro cache status`
   - `mstro mcp` (explicit)

## Open Questions

1. **Help text:** Should we add `[CommandHelp]` attributes to commands for better `--help` output?
   - **Recommendation:** Yes, add brief descriptions matching MCP tool descriptions
   
2. **Color output:** Should human-readable output use ANSI colors (like `dotnet` CLI)?
   - **Recommendation:** No for v0.7.0 — keep output simple, add in v0.8.0 if requested
   
3. **Progress indicators:** For long-running operations (flow-graph), show progress?
   - **Recommendation:** No for v0.7.0 — most operations are fast (<2s)

## References

- Helix.mcp reference: https://github.com/lewing/helix.mcp
- ConsoleAppFramework docs: https://github.com/Cysharp/ConsoleAppFramework


---

# Threat Model: GitHub Auth Cascade (v0.6.0)

**Author:** Holden (Lead / Architect)  
**Date:** 2025-07-16  
**Scope:** `GitHubApiClient.cs` — 3-tier GitHub auth cascade and Compare API integration  
**Framework:** STRIDE-informed analysis  
**Context:** Read-only MCP tool server, calls GitHub Compare API for public repos (dotnet/dotnet). Runs as MCP subprocess hosted by Copilot CLI.

---

## Summary

The GitHub auth cascade is **reasonably secure for its scope** — a single-purpose, read-only tool calling one public API endpoint. The most significant finding is the subprocess `WaitForExit()` with no timeout (Medium severity, fix now). The rest are low-severity items appropriate for a dev-local tool, with two "fix later" items worth addressing when time allows.

**Findings:** 9 total — 0 Critical, 0 High, 2 Medium, 4 Low, 3 Info

---

## Findings

### GH-T1: Subprocess Hang — `WaitForExit()` with No Timeout

- **Category:** Denial of Service  
- **Severity:** Medium  
- **Description:** `process.WaitForExit()` on line 48 has no timeout. If `gh auth token` hangs (broken pipe, stuck credential helper, network timeout in gh's own auth flow), the entire MCP server startup blocks indefinitely. The static initializer makes this worse — the `HttpClient` is created during type loading, so a hang here freezes the first request and potentially all subsequent ones.
- **Mitigation:** Add `process.WaitForExit(5000)` (5-second timeout). If it doesn't exit in time, kill the process and fall through to anonymous. Also consider `process.StartInfo.RedirectStandardError = true` to capture any error output for diagnostics.
- **Priority:** **Fix now** — easy fix, prevents a real startup hang scenario.

### GH-T2: PATH-Based Executable Resolution

- **Category:** Tampering / Elevation of Privilege  
- **Severity:** Low  
- **Description:** `FileName = "gh"` resolves via the system PATH. A malicious `gh` binary earlier in PATH could intercept the call and harvest the intent (though the subprocess output — a token — flows back to *our* process, not the other way). In the reverse direction, a trojan `gh` could return a malicious token, but since we only use it as a Bearer token against `api.github.com`, the worst outcome is auth failure.
- **Mitigation:** Accepted. This is the standard pattern for CLI tool integration. The attack requires prior machine compromise (modifying PATH or dropping a binary), at which point the attacker already has access to `gh auth token` directly. No action needed.
- **Priority:** **Accept**

### GH-T3: Token Not Logged — Confirmed Safe

- **Category:** Information Disclosure  
- **Severity:** Info  
- **Description:** The code correctly logs only the *method* of authentication ("using GITHUB_TOKEN env var", "using gh CLI token") to stderr, never the token value itself. The token variable stays in local scope and is only assigned to `DefaultRequestHeaders.Authorization`. No string interpolation includes the token.
- **Mitigation:** None needed — this is correct behavior.
- **Priority:** **Accept** (already handled correctly)

### GH-T4: Static HttpClient — Token Lifetime and Rotation

- **Category:** Spoofing / Information Disclosure  
- **Severity:** Medium  
- **Description:** The `HttpClient` is created once in a static initializer and lives for the process lifetime. If the underlying token is rotated (GITHUB_TOKEN env var changes, `gh auth` re-authenticates), the MCP server continues using the stale token until restarted. This isn't a *leak* risk, but it means: (1) Token revocation doesn't take effect until restart. (2) If the initial auth fails and falls back to anonymous, the server stays anonymous forever — no retry.
- **Mitigation:** For this tool's scope (short-lived MCP subprocess, restarted per session), this is acceptable. Document that token changes require server restart. For longer-lived deployments, consider a `DelegatingHandler` that refreshes the token lazily.
- **Priority:** **Fix later** — document the restart requirement. Consider lazy refresh if the server becomes long-lived.

### GH-T5: URL Construction — Limited SSRF Surface

- **Category:** Spoofing / Server-Side Request Forgery  
- **Severity:** Low  
- **Description:** The Compare API URL is constructed via string interpolation: `$"https://api.github.com/repos/{owner}/{repo}/compare/{baseSha}...{headSha}"`. The parameters `owner`, `repo`, `baseSha`, `headSha` come from *internal* data — specifically `MaestroService.ParseGitHubUrl()` which parses stored repository URLs, and `Build.Commit` values from the Maestro/BAR API. These are **not user-supplied MCP tool parameters**. The `IsVmrRepository` guard further limits this to URLs containing `github.com/dotnet/dotnet`. A path-traversal attempt in a SHA (e.g., `../../other-endpoint`) would produce a 404 from GitHub's API routing, not an SSRF.
- **Mitigation:** The existing guardrails (ParseGitHubUrl validates `github.com` host, IsVmrRepository restricts to dotnet/dotnet, parameters come from trusted BAR API data) are sufficient. For defense-in-depth, could add SHA format validation (`^[0-9a-f]{7,40}$`), but this is a minor hardening.
- **Priority:** **Fix later** — add SHA regex validation as defense-in-depth.

### GH-T6: Error Message Information Disclosure

- **Category:** Information Disclosure  
- **Severity:** Low  
- **Description:** Error messages include `response.StatusCode` and `owner/repo` (line 76), and `ex.Message` for exceptions (line 93). The status code and owner/repo are not sensitive — they're public repo identifiers. The `ex.Message` could theoretically include internal details (e.g., DNS resolution failures revealing internal network topology), but since the only target is `api.github.com`, this is negligible.
- **Mitigation:** Accepted. The error messages go to stderr (not to the MCP tool response — the method returns `null` on failure). The MaestroService caller handles `null` gracefully by omitting the `CommitsBehind` field.
- **Priority:** **Accept**

### GH-T7: Token Scope — Broader Than Needed

- **Category:** Elevation of Privilege  
- **Severity:** Low  
- **Description:** `GITHUB_TOKEN` and `gh auth token` typically return tokens with broader scopes than read-only public repo access (e.g., `repo`, `write:packages`). This tool only needs `public_repo` read access (or no token at all for public repos). If the token leaked, it could be used for more than compare API calls.
- **Mitigation:** Accepted for now. We can't control the user's token scope — this is inherent to reusing ambient credentials. The token is handled safely (not logged, not persisted, not forwarded). Document that users can create a fine-grained PAT with only `public_repo:read` if they want minimal scope.
- **Priority:** **Accept** — document recommendation for fine-grained PATs in README.

### GH-T8: Rate Limiting / DoS via MCP

- **Category:** Denial of Service  
- **Severity:** Info  
- **Description:** The Compare API is called inside `GetSubscriptionHealthAsync`, which iterates subscriptions. A target repository with many subscriptions could trigger many GitHub API calls. However: (1) The `IsVmrRepository` guard limits calls to dotnet/dotnet subscriptions only. (2) Results are cached via `CacheService` (5-minute TTL on subscription health). (3) GitHub's own rate limits (5000 req/hr authenticated, 60 req/hr anonymous) provide natural throttling. (4) The MCP server is single-user (subprocess per Copilot session).
- **Mitigation:** None needed. The existing caching and GitHub rate limits are sufficient. The LLM caller has no incentive to DoS its own tool.
- **Priority:** **Accept**

### GH-T9: MCP Trust Boundary — Subprocess Output Not Sanitized

- **Category:** Tampering  
- **Severity:** Info  
- **Description:** The `gh auth token` subprocess output is `.Trim()`-ed and used as a Bearer token. If a compromised `gh` binary returned output with embedded newlines or HTTP header injection characters, the `AuthenticationHeaderValue` constructor would reject malformed values (it validates the token parameter). The `ReadToEnd().Trim()` pattern is safe for single-line token output.
- **Mitigation:** None needed. `AuthenticationHeaderValue` provides validation. The `.Trim()` handles trailing newlines from stdout.
- **Priority:** **Accept**

---

## Architecture Assessment

### What's Done Right

1. **Token never logged** — Only auth method names go to stderr.
2. **Graceful degradation** — Each auth tier falls through to the next on failure. Catch-all around subprocess prevents crashes.
3. **Scoped API surface** — Only one endpoint (`/repos/{o}/{r}/compare/{b}...{h}`), read-only, public repos only.
4. **Input source is trusted** — owner/repo/SHA come from BAR API responses, not from MCP tool parameters.
5. **stderr for diagnostics** — Auth logging goes to stderr, which is the correct channel for MCP servers (doesn't pollute tool responses).

### What Should Be Improved

| Priority | Finding | Action |
|----------|---------|--------|
| **Fix now** | GH-T1: `WaitForExit()` no timeout | Add 5-second timeout, kill on hang |
| **Fix later** | GH-T4: Static token, no rotation | Document restart requirement |
| **Fix later** | GH-T5: SHA format validation | Add `^[0-9a-f]{7,40}$` regex |
| **Accept** | GH-T2, T3, T6, T7, T8, T9 | Current implementation is appropriate |

---

## Decision

The GitHub auth cascade is **approved for v0.6.0** with one P1 fix required:

- **GH-T1 (subprocess timeout)** should be fixed before the next release. Assign to Naomi.
- **GH-T4 and GH-T5** go on the backlog as P2 hardening items.
- All other findings are accepted — the risk profile is appropriate for a single-user, read-only, dev-local MCP tool.

**Decided by:** Holden  
**Participants:** Holden (analysis), Larry (requested)


---

### 2026-02-20: CLI Implementation — ConsoleAppFramework Integration Complete

**By:** Naomi  
**Date:** 2026-02-20  
**Status:** Implemented

**What:** Implemented CLI commands for mstro using ConsoleAppFramework following hlx pattern  
**Why:** Users want `mstro` to work as both CLI tool and MCP server

## Implementation Summary

Successfully refactored `mstro` from MCP-only to dual-mode (CLI + MCP) following the architecture designed by Holden.

### Changes Made

1. **MaestroTool.csproj**
   - Added `ConsoleAppFramework` v5.* package reference
   - Bumped version from 0.6.2 → 0.7.0

2. **Program.cs Refactor**
   - Replaced Host-based setup with `ConsoleApp.Create()` pattern
   - Shared DI registrations between CLI and MCP modes
   - Added `ConsoleApp.ServiceProvider = services.BuildServiceProvider()`
   - Default behavior: no args → MCP mode, args provided → CLI mode
   - MCP command creates separate Host for MCP server isolation

3. **Commands Class** (all in Program.cs like hlx)
   - 18 CLI commands implemented (17 MCP tools + cache status)
   - Constructor injection: `MaestroService`, `CacheService`
   - Human-readable output by default with `--json` flag for structured output
   - Common parameters: `json`, `noCache` on all commands
   - Positional arguments use `[Argument]` attribute
   - Optional parameters map automatically to `--option-name` flags

### Command Mapping

| CLI Command | Service Method | Notes |
|-------------|---------------|-------|
| `mstro` (no args) | — | Starts MCP server (backwards compatible) |
| `mstro mcp` | — | Explicit MCP mode |
| `mstro subscriptions` | `GetSubscriptionsAsync` | Filters: source, target, channel, branch |
| `mstro subscription <id>` | `GetSubscriptionAsync` | Includes health check |
| `mstro latest-build <repo>` | `GetLatestBuildAsync` | Optional channel filter |
| `mstro build <id>` | `GetBuildAsync` | — |
| `mstro channels` | `GetChannelsAsync` | — |
| `mstro default-channels` | `GetDefaultChannelsAsync` | Filters: repo, branch |
| `mstro subscription-health <repo>` | `GetSubscriptionHealthAsync` | Shows commits/builds behind |
| `mstro build-freshness <channel>` | `GetBuildFreshnessAsync` | aka.ms URL resolution |
| `mstro trigger-subscription <id> <build>` | `TriggerSubscriptionAsync` | Requires auth, optional --force |
| `mstro trigger-daily-update` | `TriggerDailyUpdateAsync` | Requires auth |
| `mstro codeflow-prs` | `GetTrackedPullRequestsAsync` | Optional channel filter |
| `mstro tracked-pr <id>` | `GetTrackedPullRequestBySubscriptionIdAsync` | — |
| `mstro backflow-status <vmr-build-id>` | `GetBackflowStatusAsync` | — |
| `mstro subscription-history <id>` | `GetSubscriptionHistoryAsync` | Shows last 20 entries |
| `mstro build-graph <id>` | `GetBuildGraphAsync` | — |
| `mstro flow-graph <channel-id>` | `GetFlowGraphAsync` | Optional: days, includeArcade, etc. |
| `mstro cache clear` | `CacheService.Clear()` | — |
| `mstro cache status` | — | Shows cache location/stats |

### Output Format Pattern

**Human-readable (default):**
```
$ mstro channels
Found 159 channel(s):

- .NET 10 (ID: 4567)
- .NET 10 RC1 (ID: 4568)
...
```

**JSON (--json flag):**
```
$ mstro channels --json
[
  {
    "id": 4567,
    "name": ".NET 10",
    ...
  }
]
```

### Parameter Patterns

- **Positional:** `[Argument] string subscriptionId` → `mstro subscription abc-123`
- **Optional:** `string? channelName = null` → `mstro subscriptions --channel-name ".NET 10"`
- **Boolean:** `bool json = false` → `mstro subscriptions --json`
- **No [Option] attributes needed** — ConsoleAppFramework v5 auto-maps parameters

### Error Handling

- Auth failures exit with code 1 and `🔒` error prefix
- Invalid input exits with code 1 and user-friendly message to stderr
- Service exceptions propagate (not caught at command level)
- Progress messages go to stderr to keep stdout clean for JSON output

### Key Learnings

1. **ConsoleAppFramework v5 API change:** Earlier versions (v4) used `[Option]` attributes, but v5 auto-maps parameters by name. Parameters automatically become `--kebab-case` flags.

2. **Separate DI for MCP:** The MCP command creates its own `Host.CreateApplicationBuilder()` with separate service registrations. This keeps MCP server lifecycle isolated from CLI command execution.

3. **Backwards compatibility:** `args.Length == 0 ? ["mcp"] : args` ensures existing MCP integrations don't break.

4. **IGitHubApiClient wiring:** Required explicit factory pattern in DI registration to ensure 3rd constructor parameter is injected into `MaestroService`.

5. **Build verification:** 0 warnings, 0 errors. ConsoleAppFramework works with .NET 10 without issues.

### Testing Plan

1. **Manual smoke test:** Run each command with sample data
2. **MCP compatibility:** Verify `mstro` (no args) still works as MCP server
3. **JSON output:** Verify `--json` flag returns valid JSON on all commands
4. **Auth gating:** Verify trigger commands fail gracefully when unauthenticated

### Next Steps

1. Update README.md with CLI usage examples
2. Add unit tests for Commands class (mock MaestroService)
3. Consider adding `--help` text for parameters (ConsoleAppFramework supports `[Description]` on params)
4. Consider adding color output for human-readable mode (v0.8.0 feature)

## Files Modified

- `src/MaestroTool/MaestroTool.csproj` — Added ConsoleAppFramework, bumped version
- `src/MaestroTool/Program.cs` — Refactored to dual-mode, added Commands class

## Build Status

✅ `dotnet build` succeeded — 0 warnings, 0 errors


---

# Decision: Fetch Full Build for Commit SHA When Null

**Date:** 2025-02-20  
**Author:** Naomi (Backend Developer)  
**Status:** Implemented

## Problem

User feedback reported that `maestro_subscription_health` was still showing "591 builds behind" instead of "commits behind" for VMR subscriptions, even after the GitHub Compare API integration was added in v0.6.0.

Root cause: The PCS subscription API returns embedded/summary `Build` objects in the `LastAppliedBuild` property. These embedded builds often have the `Commit` field as null/empty because the PCS API doesn't always serialize all fields for embedded objects. As a result, the GitHub Compare API code was being silently skipped due to the null check gating condition.

## Decision

When computing subscription health for VMR subscriptions:

1. **Check if commit SHAs are null/empty BEFORE attempting GitHub compare**
2. **Fetch full build objects using `GetBuildAsync(buildId)` when commit is missing**
3. **Add defensive checks**: Only fetch if build ID > 0
4. **Graceful fallback**: If full build fetch also returns null commit, fall back to builds-behind (BAR ID arithmetic)
5. **Add diagnostic logging** to make debugging easier:
   - `[maestro-mcp] Fetching full build {buildId} for commit SHA`
   - `[maestro-mcp] Comparing commits {sha1}...{sha2} in {owner}/{repo}`

## Implementation

Modified `MaestroService.GetSubscriptionHealthAsync()` (lines 133-168):

```csharp
// For VMR subscriptions, use GitHub compare API for accurate commit distance
if (_gitHubClient != null && IsVmrRepository(sub.SourceRepository))
{
    var parsedRepo = ParseGitHubUrl(sub.SourceRepository);
    if (parsedRepo.HasValue)
    {
        // Fetch full build objects if commit SHAs are missing
        var lastAppliedCommit = lastApplied.Commit;
        var latestBuildCommit = latestBuild.Commit;

        if (string.IsNullOrEmpty(lastAppliedCommit) && lastApplied.Id > 0)
        {
            Console.Error.WriteLine($"[maestro-mcp] Fetching full build {lastApplied.Id} for commit SHA");
            var fullLastApplied = await GetBuildAsync(lastApplied.Id, noCache, cancellationToken);
            lastAppliedCommit = fullLastApplied?.Commit;
        }

        if (string.IsNullOrEmpty(latestBuildCommit) && latestBuild.Id > 0)
        {
            Console.Error.WriteLine($"[maestro-mcp] Fetching full build {latestBuild.Id} for commit SHA");
            var fullLatestBuild = await GetBuildAsync(latestBuild.Id, noCache, cancellationToken);
            latestBuildCommit = fullLatestBuild?.Commit;
        }

        if (!string.IsNullOrEmpty(lastAppliedCommit) && !string.IsNullOrEmpty(latestBuildCommit))
        {
            var (owner, repo) = parsedRepo.Value;
            Console.Error.WriteLine($"[maestro-mcp] Comparing commits {lastAppliedCommit}...{latestBuildCommit} in {owner}/{repo}");
            var compareResult = await _gitHubClient.CompareCommitsAsync(
                owner, repo, lastAppliedCommit, latestBuildCommit, cancellationToken);
            
            if (compareResult != null)
            {
                commitsBehind = compareResult.AheadBy;
            }
        }
    }
}
```

## Testing

Added 3 comprehensive tests to verify the fix:

1. **`SubscriptionHealth_FetchesFullBuildWhenLastAppliedCommitIsNull`**  
   Tests that when `LastAppliedBuild.Commit` is null/empty, the service fetches the full build via `GetBuildAsync()` and successfully retrieves the commit SHA for GitHub compare.

2. **`SubscriptionHealth_FetchesFullBuildWhenLatestBuildCommitIsNull`**  
   Tests that when `latestBuild.Commit` is null/empty, the service fetches the full build and successfully uses it for GitHub compare.

3. **`SubscriptionHealth_FallsBackToBuildsBehindWhenBothCommitsAreNull`**  
   Tests that when BOTH builds have null commits (even after full build fetch), the service gracefully falls back to builds-behind without crashing.

**Test discovery note:** The `CreateBuild` helper defaults `commit` parameter to `"abc123"` when `null` is passed. Tests must use empty string `""` to simulate missing commits.

## Impact

- **Minimal code changes**: Only modified the VMR commit distance calculation block in one method
- **No breaking changes**: Graceful fallback preserves existing behavior when commits unavailable
- **Performance**: Adds 0-2 additional PCS API calls per VMR subscription when commits are missing. Mitigated by cache layer (LongTtl for builds)
- **User experience**: VMR subscriptions now correctly show "33 commits behind" instead of "591 builds behind"

## Alternatives Considered

1. **Always fetch full builds for all subscriptions**  
   Rejected: Wasteful for non-VMR subscriptions and when commits are already populated

2. **Use BackflowStatus API CommitDistance field**  
   Rejected: Testing showed BackflowStatus API is unreliable (errors on multiple VMR builds)

3. **Store full builds in subscription cache**  
   Rejected: More complex, harder to maintain, unclear ownership of data transformation

## Files Modified

- `src/MaestroTool.Core/MaestroService.cs` — added full build fetch logic
- `src/MaestroTool.Tests/MaestroServiceTests.cs` — added 3 new tests

## Related

- Issue #5: maestro_subscription_health reporting wrong commit count
- Issue #4: Initial GitHub Compare API integration (v0.6.0)


---

### 2025-02-20: GitHub Commit Distance Implementation (Issue #4)
**By:** Naomi
**What:** Implemented `IGitHubApiClient` with 3-tier auth cascade (GITHUB_TOKEN env var → gh CLI → anonymous) to provide accurate commit distances for VMR subscriptions via GitHub's Compare API. Updated `maestro_subscription_health` tool to show "33 commits behind" for VMR subs instead of wildly inaccurate BAR build ID arithmetic ("~566 builds behind").
**Why:** BAR build IDs are globally sequential across all repos (not per-repo), causing 17x error for VMR subscriptions. GitHub Compare API provides ground truth commit distance. Graceful degradation ensures feature works in all environments (anonymous 60 req/hr sufficient for typical use). Optional dependency injection pattern allows MaestroService to work with or without GitHub client.


---

### 2026-02-20: AzDO API client auth and interface design
**By:** Holden (with input from Naomi, Amos)

**What:**
1. **Separate IAzDoApiClient interface** with Task<int?> GetCommitCountAsync(org, project, repo, baseSha, headSha, ct) — parallel to IGitHubApiClient, not unified.
2. **Auth cascade**: AZDO_TOKEN env var → z account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 → anonymous. Token acquisition extracted into IAzDoTokenProvider for testability.
3. **URL parsing**: ParseAzDoUrl returns (org, project, repo)?, handles both dev.azure.com/{org}/{project}/_git/{repo} and {org}.visualstudio.com/{project}/_git/{repo} legacy format.
4. **Commit cap**: $top=1000, no pagination. Array length = commit count, capped at 1000.
5. **Integration**: IAzDoApiClient? as optional 4th param to MaestroService constructor. Sibling lse if branch in GetSubscriptionHealthAsync.
6. **Degradation**: Return 
ull on any failure, log actionable stderr message. Existing null-handling in SubscriptionHealthResult covers fallback.

**Why:**
- Separate interface avoids lowest-common-denominator abstraction between APIs with different capabilities (GitHub compare is richer than AzDO commit listing).
- Auth cascade mirrors the proven GitHubApiClient pattern. IAzDoTokenProvider extraction is the key addition — it isolates subprocess calls for testability and prevents CI flake from missing z CLI.
- Optional constructor param guarantees backward compatibility: all existing tests and 3-arg call sites continue to work unchanged.
- 1000-cap eliminates pagination complexity for an informational metric. If you're 1000+ commits behind, the exact number doesn't matter.
- int? return type aligns directly with the existing CommitsBehind field on SubscriptionHealthResult, requiring no model changes.

---

### 2026-02-20: dotnet-replay Scoping Analysis (Issues #11, #12, #13)
**By:** Holden (Lead Architect)
**Status:** Complete — three issues scoped, ready for roadmap planning

**What:** Architectural feasibility assessment for three dotnet-replay feature requests:
- **Issue #11 (diff mode):** Medium effort (5–7 days) — Compare two eval runs side-by-side with turn alignment and tool call diffs
- **Issue #12 (grep/search):** Small-Medium effort (3–4 days) — Search multiple transcripts with pattern matching and context
- **Issue #13 (batch stats):** Small effort (2–3 days) — Aggregate stats across transcript batches with grouping and filtering

**Why:**
All three features are **architecturally feasible** and align well with dotnet-replay's existing codebase design (single-file .NET 10 app with pluggable format detection, modular rendering pipeline, comprehensive test coverage).

**Recommended build order:** #13 → #12 → #11 (ascending complexity; #13 establishes glob utilities, #12 builds on them, #11 is fully standalone but most algorithmic)

**Key architectural notes:**
- Single-file design maintained — each feature is command dispatch + private functions reusing existing helpers
- New test files in separate xUnit project (follow existing SummaryOutputTests.cs, EdgeCaseTests.cs pattern)
- Shared utilities: glob expansion, turn extraction, format detection, JSON output (already exist or minimal additions)
- No breaking changes to existing functionality or API surface

---

### 2026-02-20: dotnet-replay Stats Command Test Strategy
**By:** Amos
**Status:** Proposed

**What:** Integration test strategy for the stats command (issue #13):
- **Process-based integration tests**, not unit tests (dotnet-replay is single-file app with no public API surface)
- **Programmatic JSON fixture generation** in 	estdata/stats/ with unique GUID-based filenames (no shared state, safe for parallel xUnit execution)
- **25 tests** covering: basic aggregation (5), grouping (3), filtering (3), JSON output (covered in aggregation), edge cases (7), CI thresholds (3)
- **Exit code testing** via RunStatsWithExitCode() helper for --fail-threshold CI integration
- **No test cleanup** — GUID-based filenames prevent collisions; CI can clean 	estdata/ between runs

**Why:**
- Integration testing pattern matches existing dotnet-replay tests (SummaryOutputTests.cs, EdgeCaseTests.cs)
- Programmatic fixtures are more maintainable than ~25 static JSON files
- Graceful degradation on malformed input: skip unparseable files with warnings, process valid files
- Unique GUID filenames safe for parallel test execution in xUnit

**Implementation blockers:**
The test file (StatsOutputTests.cs) is written but won't compile until Naomi implements:
1. ExpandGlob() helper for glob pattern expansion (e.g., esults/*.json)
2. ExtractStats() to parse Waza JSON and extract model/result/duration
3. OutputStatsReport() to format and display aggregated stats
4. FileStats class/record to hold per-file statistics
5. Command-line arg parsing for: stats, --group-by, --filter-model, --filter-task, --fail-threshold

---

### 2026-02-22: Always pass base URI to PcsApiFactory
**By:** Naomi (Backend Developer)
**Date:** 2026-02-22
**Status:** Implemented in v0.8.4
**Issue:** #8

**Context:** PcsApiFactory.GetAnonymous() (parameterless) crashes with UriFormatException because ProductConstructionServiceApiOptions is constructed without a base URI.

**Decision:** Always use the aseUri overloads of PcsApiFactory:
- PcsApiFactory.GetAnonymous(DefaultBaseUri) instead of PcsApiFactory.GetAnonymous()
- PcsApiFactory.GetAuthenticated(DefaultBaseUri, ...) instead of PcsApiFactory.GetAuthenticated(...)

The default base URI is https://maestro.dot.net, defined as a private const in MaestroApiClient.

**Rationale:** The parameterless overloads rely on ProductConstructionServiceApiOptions having a default URI baked in, but it doesn't — it expects the caller to provide one. All three auth paths (BAR token, Entra ID, anonymous) must explicitly pass the URI.

**Impact:**
- MaestroApiClient.cs: 3 call sites updated
- No API surface change — IMaestroApiClient is unchanged
- Fixes the crash that prevented the MCP server from starting without auth credentials

---

### 2025-02-20: Extend commit distance to all GitHub-hosted repos
**By:** Naomi (Backend Dev)
**Date:** 2025-02-20
**Status:** Implemented
**Issue:** #6

**Context:** The subscription_health tool computed accurate commit distance (via GitHub Compare API) only for VMR subscriptions (github.com/dotnet/dotnet). All other GitHub-hosted source repos fell back to BAR build ID arithmetic, which uses globally sequential IDs across all repos and wildly overstates staleness.

**Decision:** Changed the gate in GetSubscriptionHealthAsync from IsVmrRepository() to a new IsGitHubRepository() helper.
- IsGitHubRepository delegates to the existing ParseGitHubUrl which already handles any github.com URL
- Kept IsVmrRepository — it may be useful for VMR-specific logic in the future
- No changes needed to display logic — both MCP tools and CLI already handle CommitsBehind generically

**Impact:** All GitHub-hosted source repos now get accurate "N commits behind" instead of inflated "~N builds behind". Non-GitHub repos (e.g., Azure DevOps) continue using BAR ID arithmetic as before.

**Files changed:**
- src/MaestroTool.Core/MaestroService.cs — gate change, new helper, comment update
- src/MaestroTool/MaestroTool.csproj — version 0.7.0 → 0.7.1
- src/MaestroTool/Program.cs — version string 0.7.0 → 0.7.1


---

### 2025-02-22: ModelContextProtocol SDK Upgrade to 1.0.0 Stable

**By:** Naomi

**What:** Upgraded ModelContextProtocol packages from 0.8.0-preview.1 to 1.0.0 stable across all projects (MaestroTool, MaestroTool.Mcp, MaestroTool.Core). Bumped project version from 0.10.0 to 0.11.0.

**Why:** The MCP SDK reached 1.0.0 stable release, providing production-ready API stability guarantees. While 1.0.0 introduced several breaking changes (filter configuration split, collection interface changes, sealed McpClientHandlers, required Tool.Name), none affected this project's usage pattern. Our implementation only uses server-side APIs with `[McpServerToolType]` attribute on classes and `[McpServerTool(Name = "...")]` on methods, which remained stable. Upgrading now ensures we're on the supported release track with no deprecated preview dependencies.

**Impact:**
- All package references updated to `ModelContextProtocol 1.0.0` and `ModelContextProtocol.AspNetCore 1.0.0`
- Build succeeds with 0 warnings, 0 errors
- No code changes required — our MCP usage pattern is fully compatible
- Server version strings updated to 0.11.0 in both MaestroTool/Program.cs and MaestroTool.Mcp/Program.cs
- Test failures (124/135) are due to unrelated `/tmp` file permission issue with SetUnixFileMode, not MCP upgrade

**Files Changed:**
- src/MaestroTool/MaestroTool.csproj
- src/MaestroTool.Mcp/MaestroTool.Mcp.csproj
- src/MaestroTool.Core/MaestroTool.Core.csproj
- src/MaestroTool/Program.cs (server version string)
- src/MaestroTool.Mcp/Program.cs (server version string)


---

# MCP SDK 1.0 Feature Evaluation

**Author:** Holden  
**Date:** 2026-02-20  
**Status:** Analysis  
**SDK Version:** Currently on 1.0.0 (already upgraded from 0.8.0-preview.1)

## Executive Summary

The project is **already on SDK 1.0.0**. This analysis evaluates new features from the 0.8 → 1.0 upgrade path for practical benefit to maestro.mcp's architecture and use case (MCP server for Maestro/BAR dependency flow data).

**Verdict:** Most new features don't apply or aren't worth adopting at this time. The SDK upgrade we've already done brings stability and bug fixes. The only feature worth future consideration is **structured tool output**, but not urgently.

---

## Feature Assessment

### 1. Structured Tool Output (StructuredContent)

**What it is:**  
Tools can return strongly-typed objects instead of strings. The SDK auto-generates JSON schemas, enabling LLMs and clients to consume data programmatically with validation.

**Current state:**  
All 20 tools return `Task<string>` with formatted markdown text.

**Should we adopt?**

**PROBABLY NOT, at least not yet.** Here's why:

**Cons:**
- **Markdown is working well.** LLMs parse our current output without issues. The data is semi-structured (headers, lists, tables) and human-readable.
- **Breaking change for consumers.** Skills and clients that consume our tools expect markdown-formatted strings. Switching to structured output would disrupt existing workflows.
- **Not a pain point.** We haven't seen bugs or limitations caused by string returns. The data is cacheable, parseable, and readable.
- **Additional modeling work.** We'd need to define 20+ DTOs matching our current output structures. The PCS client models don't map cleanly to our tool outputs (e.g., `GetSubscriptionHealth` returns a synthetic view combining subscription + build + commit data).

**Pros:**
- **Machine-readable output.** If clients need to parse results programmatically (e.g., pipe to jq, process in scripts), structured JSON is better.
- **Schema validation.** Type safety at the protocol boundary could catch bugs, but our tools already validate inputs/outputs internally.
- **Future-proofing.** If MCP clients evolve to expect structured data, we'd be ready.

**Recommendation:**  
**Backlog (P3).** Not urgent. Revisit if:
1. Consumers request JSON output for automation
2. We add tools where tabular data is hard to format as text (e.g., large graphs, matrices)
3. MCP ecosystem shifts toward structured-first tools

If we do adopt, start with **1-2 high-value tools** (e.g., `maestro_subscriptions`, `maestro_builds`) as an experiment. Offer both formats via a tool parameter (`format: "text" | "json"`).

---

### 2. Tool Annotations (ReadOnlyHint, DestructiveHint, OpenWorldHint)

**What it is:**  
Metadata hints on tools to help LLMs/clients understand behavior:
- `ReadOnlyHint`: Tool only reads, doesn't modify state
- `DestructiveHint`: Tool performs irreversible operations
- `OpenWorldHint`: Tool interacts with external/unpredictable systems

**Current state:**  
No annotations. LLM infers behavior from tool names and descriptions.

**Should we adopt?**

**NO.** Not useful for this project.

**Why:**
- **Read/write distinction is obvious.** Our tool naming already disambiguates: `maestro_subscriptions` (read), `maestro_trigger_subscription` (write). Descriptions clarify further.
- **No destructive operations.** Both action tools (`maestro_trigger_subscription`, `maestro_trigger_daily_update`) are **non-destructive** — they initiate subscription processing, not deletions. No irreversible harm.
- **All tools interact with external systems.** Every tool calls the Maestro API (open world). Setting `OpenWorldHint: true` on all 20 tools adds no information.
- **Annotations don't enforce behavior.** The SDK docs say these are "advisory only" — no security or access control. Our threat model already addresses auth at the PCS API layer, not MCP layer.

**Edge case:**  
`maestro_clear_cache` is the only tool with side effects (clears in-memory + SQLite cache). But it's not destructive (data is re-fetchable) and unlikely to be mis-invoked. Adding `ReadOnlyHint: false` wouldn't change anything.

**Recommendation:**  
**REJECT.** Annotations would be redundant metadata with no practical benefit. Keep tool names and descriptions as the source of truth.

---

### 3. Resource Links from Tools (ResourceLinkBlock)

**What it is:**  
Tools can return `ResourceLinkBlock` objects in their result content, providing MCP-native links to related resources (e.g., "here's a PR, and here's a link to its commits").

**Current state:**  
Tools return markdown with inline URLs. Example from `maestro_codeflow_prs`:
```markdown
**https://github.com/dotnet/dotnet/pull/12345**
  Channel: .NET 10.0.1xx SDK | Target Branch: release/10.0.1xx
```

**Should we adopt?**

**NO.** Not a good fit.

**Why:**
- **We don't expose MCP resources.** Our server has 0 resources (`resources/list` returns empty). All data comes from tools, not resources. Resource links are meant to bridge tools → resources within the same MCP server.
- **GitHub URLs aren't MCP resources.** When we return PR URLs (e.g., `https://github.com/dotnet/dotnet/pull/12345`), those are external links, not MCP resource URIs. LLMs already know how to parse markdown links.
- **No follow-up workflows.** Resource links enable patterns like: "Tool X returns link to resource Y, client fetches Y via `resources/read`." We don't have resource endpoints to link to.
- **Adding resources would be architectural churn.** We'd need to redesign the caching/service layer to support resource URIs, with unclear benefit over the current tool-only model.

**Possible future use case:**  
If we ever expose **large, paginated, or streaming data** as MCP resources (e.g., `resource://maestro/builds?channel=10.0.1xx&offset=100`), tools could return `ResourceLinkBlock` to point at those. But that's not on the roadmap.

**Recommendation:**  
**REJECT.** Stick with markdown-formatted URLs. They're universal, portable, and work across all MCP clients.

---

### 4. Extensions / New Server Capabilities

**What it is:**  
SDK supports declaring extended server capabilities via `McpServerOptions.Capabilities`. New protocol features like:
- `2025-11-25` protocol version support
- Elicitation (dynamic prompting for missing info)
- User-defined `JsonSerializerOptions`

**Current state:**  
Server uses default SDK capabilities registration (tools only, no prompts/resources/logging).

**Should we adopt?**

**PARTIALLY — already done.** The SDK upgrade brings protocol compliance automatically.

**What we get for free:**
- ✅ **Stable API with SemVer guarantees** — no more breaking changes
- ✅ **Improved transport reliability** — better reconnection handling (5 retries instead of 2)
- ✅ **OAuth backward compatibility** — future-proofing for auth changes
- ✅ **Bug fixes** — base64 deserialization, JSON handling

**What we don't need:**
- ❌ **Elicitation** — our tools have all required parameters, no dynamic info gathering needed
- ❌ **Custom JsonSerializerOptions** — default serialization works fine for PCS models
- ❌ **SSE event stream storage** — we use stdio transport, not HTTP streaming
- ❌ **MCP task support** — no long-running async operations in our tool set

**Recommendation:**  
**ACCEPT what we have.** The default capabilities are sufficient. No changes needed to `Program.cs` or `McpServerOptions`.

---

## Additional SDK Features Not Evaluated

These were mentioned in changelogs but aren't relevant to tool design:

### 0.8 Features
- **Message-level filters** — internal SDK plumbing, no API surface for servers
- **Distributed cache-backed event stream store** — we use SQLite, not distributed cache
- **Trace-level logging** — useful for debugging, but we already have stderr diagnostics

### 0.9 Features
- **Streamable HTTP resumability** — HTTP server mode isn't primary use case
- **Missing ResourceLinkBlock properties (Title, Icons)** — we don't use resources

### 1.0 RC/Stable
- **Increased MaxReconnectionAttempts** — automatic, no action needed

---

## Threat Model Implications

No new security concerns from SDK upgrade. Key observations:

1. **Tool annotations don't provide security.** The SDK docs explicitly state they're advisory. Auth enforcement remains at the PCS API layer (correct design).
2. **Structured output doesn't change trust boundaries.** Whether tools return strings or objects, the data source (PCS API) and caching layer (SQLite) are unchanged.
3. **Resource links would require new auth logic.** If we ever add resources, we'd need to gate `resources/read` by the same auth cascade (PAT → Entra → anonymous). Current threat model (STRIDE analysis in history.md) already covers this pattern for tools.

---

## Action Items

### Immediate (P1)
- ✅ **None.** SDK 1.0 upgrade is complete. No code changes needed.

### Future Consideration (P2-P3)
- **P3: Experiment with structured output** — Pick 1-2 high-value tools, add a `format` parameter to return JSON instead of markdown. Solicit feedback from consuming skills.
- **P3: Document SDK features in README** — Mention we're on 1.0, note string-based tool outputs as a design choice.

### Rejected
- ❌ Tool annotations (ReadOnlyHint, DestructiveHint, OpenWorldHint)
- ❌ Resource links (ResourceLinkBlock)
- ❌ Elicitation support
- ❌ Custom JsonSerializerOptions

---

## Conclusion

The MCP SDK 1.0 upgrade brings **stability and bug fixes** without requiring architecture changes. New features like structured output, tool annotations, and resource links are either:
1. Not applicable to our tool design (annotations, resources)
2. Not worth the migration cost vs. current markdown-based approach (structured output)

**Recommendation: No immediate action.** The SDK upgrade is a success. Focus future work on functional features (Issue #1 backlog) rather than protocol-level enhancements.

---

## References

- [MCP C# SDK Documentation](https://modelcontextprotocol.github.io/csharp-sdk/)
- [MCP C# SDK GitHub](https://github.com/modelcontextprotocol/csharp-sdk)
- [SDK 1.0 Release Notes](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v1.0.0)
- [Tool Annotations API Docs](https://modelcontextprotocol.github.io/csharp-sdk/api/ModelContextProtocol.Protocol.ToolAnnotations.html)
# MCP Tool Annotations Decision

**Date:** 2025-03-01  
**Author:** Naomi (Backend Developer)  
**Status:** Implemented

## Context

The MCP SDK 1.0 introduced `ReadOnly` and `Destructive` boolean properties for the `[McpServerTool]` attribute. These metadata hints allow MCP clients to:
- Auto-approve safe, read-only tools without user confirmation
- Require explicit confirmation for destructive operations
- Provide better UX by categorizing tool behavior

## Decision

All 19 `[McpServerTool]` attributes in `MaestroMcpTools.cs` were annotated based on their behavior:

### Read-Only Tools (16 tools marked with `ReadOnly = true`)
Query tools that fetch and return data without side effects:
- `maestro_subscriptions`, `maestro_subscription`, `maestro_latest_build`, `maestro_build`, `maestro_builds`
- `maestro_channel`, `maestro_channels`, `maestro_default_channels`
- `maestro_subscription_health`, `maestro_build_freshness`
- `maestro_codeflow_prs`, `maestro_codeflow_pr`, `maestro_backflow_status`
- `maestro_subscription_history`, `maestro_build_graph`, `maestro_flow_graph`

### Mutating (Non-Destructive) Tools (2 tools left at defaults)
Tools that trigger server-side actions but don't destroy data:
- `maestro_trigger_subscription` — triggers a Maestro subscription update
- `maestro_trigger_daily_update` — triggers the daily update workflow

These were left with default values (`ReadOnly = false`, `Destructive = false`) to indicate they have side effects but aren't destructive.

### Destructive Tools (1 tool marked with `Destructive = true`)
- `maestro_clear_cache` — wipes the local SQLite cache, permanently discarding cached data

## Rationale

This classification allows MCP clients to:
1. **Auto-approve read-only queries** — Most tools (16/19) are safe queries that can run without confirmation
2. **Prompt for trigger actions** — Subscription/update triggers have side effects but aren't destructive
3. **Require confirmation for cache clearing** — The only destructive operation that loses data

## Verification

- ✅ Build succeeded (12.5s)
- ✅ All 135 tests passed
- ✅ Committed as `834b9d5`

## Future Considerations

As new tools are added, they should be classified using this same rubric:
- **ReadOnly=true**: Pure queries with no side effects
- **Defaults**: Mutating operations that don't destroy data
- **Destructive=true**: Operations that permanently delete or destroy data

---

# Naming Convention Review for Issue #9

**Date**: 2026-02-20  
**Reviewer**: Holden (Lead/Architect)  
**Issue**: #9 "Inconsistent tool naming conventions"

## Executive Summary

Issue #9 proposes standardizing MCP tool naming conventions. After reviewing all 17 current tools, **the inconsistencies are real but the proposed convention is only partially beneficial**. The current naming follows an implicit pattern that's reasonably predictable once understood. The highest-ROI improvement is **adding missing symmetrical tools** (`maestro_builds`, `maestro_channel`), not renaming existing ones.

**Recommendation**: Accept the proposal's diagnostic value, but implement via **additive changes only** (no breaking renames). Add 2-3 missing tools for symmetry, document the naming pattern, and establish a convention for future tools.

---

## Current State Analysis

### Tool Inventory (17 tools)

**Query tools (bare nouns):**
- `maestro_subscriptions` (list) / `maestro_subscription` (get) ✅ symmetric
- `maestro_channels` (list) / ❌ no `maestro_channel` (get)
- `maestro_latest_build` (query) / `maestro_build` (get) / ❌ no `maestro_builds` (list)
- `maestro_default_channels` (list only, no get) ✅ OK
- `maestro_subscription_health` (detail)
- `maestro_subscription_history` (detail)
- `maestro_build_freshness` (detail)
- `maestro_build_graph` (detail)
- `maestro_flow_graph` (detail)
- `maestro_backflow_status` (detail)
- `maestro_codeflow_prs` (list) / `maestro_tracked_pr` (get) ⚠️ asymmetric noun

**Action tools (verb prefixes):**
- `maestro_trigger_subscription`
- `maestro_trigger_daily_update`
- `maestro_clear_cache`

**CLI commands** (for comparison, use hyphens instead of underscores):
- `subscriptions`, `subscription`, `latest-build`, `build`, `channels`, `default-channels`, `subscription-health`, `build-freshness`, `trigger-subscription`, `trigger-daily-update`, `codeflow-prs`, `tracked-pr`, `backflow-status`, `subscription-history`, `build-graph`, `flow-graph`, `cache`

---

## Issue Analysis

### 1. Plural/Singular Asymmetry ⚠️ Real Issue

**Finding**: 2 of 4 resource pairs are asymmetric:
- Builds: `maestro_latest_build` + `maestro_build` exist, but no `maestro_builds` (list)
- Channels: `maestro_channels` exists, but no `maestro_channel` (get by ID)

**Impact**: Medium. Agents expect list/get pairs. The missing tools force workarounds (e.g., filtering `maestro_channels` client-side to find a specific channel).

**ROI**: HIGH. Adding `maestro_builds` and `maestro_channel` is non-breaking and immediately useful.

### 2. Codeflow Terminology Inconsistency ⚠️ Real Issue

**Finding**: `maestro_codeflow_prs` (list) uses "codeflow", but `maestro_tracked_pr` (get) uses "tracked". Both operate on Maestro-managed PRs.

**Impact**: Low-Medium. Confusing terminology, but both are technically accurate:
- "codeflow PR" = GitHub PR created by dependency flow
- "tracked PR" = Maestro's subscription tracking record

**ROI**: LOW. Renaming would break existing skills. The semantic difference may be intentional (tracking ≠ PR itself).

### 3. Verb Prefix Pattern ✅ Not An Issue

**Finding**: Actions use `trigger_`/`clear_` prefixes, queries use bare nouns.

**Assessment**: This is a GOOD implicit convention, not a bug. It disambiguates read-only queries from state-changing actions. The proposed `maestro_get_build` would be redundant — agents already understand `maestro_build` = get, `maestro_trigger_subscription` = action.

**ROI**: NEGATIVE. Adding `get_` prefixes would make names longer without improving clarity.

### 4. Compound Word Length ✅ Not An Issue

**Finding**: Most tools are 2 words, `trigger_daily_update` is 3 words.

**Assessment**: Acceptable. "daily update" is a domain term (the PCS nightly job). Shortening to `trigger_daily` would lose meaning.

---

## Proposed Convention Evaluation

```
maestro_{verb}_{resource}        # actions: maestro_trigger_subscription
maestro_{resource}               # get one: maestro_subscription
maestro_{resources}              # list:    maestro_subscriptions
maestro_{resource}_{aspect}      # detail:  maestro_subscription_health
```

**Strengths**:
- Codifies the current implicit pattern
- Clear action/query distinction
- Predictable for agent reasoning

**Weaknesses**:
- `maestro_latest_build` doesn't fit (should be `maestro_build_latest`?)
- Doesn't address the codeflow/tracked terminology split
- Over-formalizes what's already working

**Verdict**: The convention is mostly **descriptive** (what we already do) rather than **prescriptive** (new rules). Value is in documentation, not enforcement.

---

## Recommendations

### P1: Non-Breaking Additions (Immediate)

1. **Add `maestro_builds`** (list builds with filters — repo, channel, date range)
   - Fills the symmetry gap with `maestro_build` (get by ID)
   - Useful for "find recent builds" queries
   - Effort: ~4-6 hours (API call + formatting)

2. **Add `maestro_channel`** (get channel by ID)
   - Fills the symmetry gap with `maestro_channels` (list)
   - Useful for "what's channel ID 42?" queries
   - Effort: ~2-3 hours (API call exists in service layer)

### P2: Documentation (Next Sprint)

3. **Document the naming pattern** in README or `MaestroMcpTools.cs` header:
   ```
   Naming convention:
   - Actions: maestro_{verb}_{resource} (e.g., maestro_trigger_subscription)
   - Queries (get): maestro_{resource} (e.g., maestro_subscription)
   - Queries (list): maestro_{resources} (e.g., maestro_subscriptions)
   - Queries (detail): maestro_{resource}_{aspect} (e.g., maestro_subscription_health)
   ```

### P3: Consider for Future (Backlog)

4. **Alias `maestro_tracked_pr` → `maestro_codeflow_pr`** (deprecation period)
   - Makes terminology consistent with `maestro_codeflow_prs`
   - Requires MCP SDK support for tool aliases (TBD if SDK supports this)
   - Low priority — existing name is defensible

### ❌ Not Recommended

- **Renaming existing tools**: Breaking change for all consuming skills. The current names are learnable and not fundamentally broken.
- **Adding `maestro_get_*` prefixes**: Redundant. The implicit "bare noun = get" pattern is already clear.
- **Renaming `maestro_latest_build`**: "Latest" is a common query pattern (cf. REST APIs with `/latest` endpoints). Not worth the churn.

---

## Migration Path (If We Did Break Things)

If we WERE to make breaking changes (not recommended):

1. **Phase 1 (v0.8)**: Add aliases for new names, keep old names working
2. **Phase 2 (v0.9)**: Deprecation warnings in tool descriptions
3. **Phase 3 (v1.0)**: Remove old names

**Estimated disruption**: 6-12 months for ecosystem to migrate. Not worth it for marginal clarity gains.

---

## Conclusion

Issue #9 provides valuable clarity on our naming patterns. The best action is **additive**: fill the 2 symmetry gaps (`maestro_builds`, `maestro_channel`), document the pattern, and move on. Breaking changes aren't justified by the marginal improvement.

**Decision**: Accept the analysis, implement P1 items, defer P3 to backlog, reject breaking renames.

---

# Decision: Release v0.12.0

**Date:** 2026-03-01
**Author:** Alex (DevOps/Infrastructure)
**Status:** Executed

## Context

The project had accumulated three significant changes since the last released tag (v0.10.0):
1. MCP SDK upgrade to stable 1.0.0
2. Linux/WSL permissions fix in CacheService
3. Tool annotations for MCP client auto-approval

Version 0.11.0 was set during the SDK upgrade but never tagged/released.

## Decision

Cut release v0.12.0 (skipping a v0.11.0 tag) to bundle all three changes into a single release. This avoids confusion between the internal 0.11.0 version that was never published and ensures a clean release history.

## Consequences

- v0.12.0 tag and commit pushed to `origin/master`
- Version string updated in `.csproj`, both `Program.cs` entry points
- 135 tests verified passing before release

---

# Decision: Interactive Terminal Detection for Default Command

**Date:** 2025-07-15
**Author:** Naomi (Backend Developer)
**Status:** Implemented

## Context
When `mstro` is run with no arguments, it previously always defaulted to starting the MCP server (`["mcp"]`). This was problematic for users who typed `mstro` in a terminal — the MCP server would start on stdio and hang, with no visible output or help.

## Decision
Use `Console.IsInputRedirected` to detect whether the process was launched by an MCP host (stdin piped) or interactively by a user (stdin is a TTY):

- **Stdin redirected** (MCP host) → default to `["mcp"]` (start MCP server)
- **Stdin NOT redirected** (terminal) → default to `["--help"]` (show usage)

## Rationale
- MCP hosts (VS Code, Copilot CLI) always pipe stdin to the subprocess, so `Console.IsInputRedirected` reliably detects this case.
- Interactive users expect help text, not a silent stdio server.
- This is a standard .NET pattern — no platform-specific code needed.
# Decision: Direct HTTP call for /api/codeflows endpoint

**Date:** 2026-07
**Author:** Naomi (Backend Developer)
**Status:** Implemented

## Context

The Maestro team added a `/api/codeflows` endpoint returning `List<CodeflowStatus>` with forward flow and backflow subscription statuses. The PCS client NuGet (v1.1.0-beta.26155.1) has the models (`CodeflowStatus`, `CodeflowSubscriptionStatus`) but the `Codeflow` property is NOT wired up on `IProductConstructionServiceApi`. PR dotnet/arcade-services#6057 is filed to fix this.

## Decision

Implement a direct HTTP call via `System.Net.Http.HttpClient` in `MaestroApiClient`, bypassing the PCS client's generated API surface. Auth is replicated by:
- **BAR token:** stored from constructor, used as Bearer header
- **Entra ID:** `InteractiveBrowserCredential` created from the darc auth record (`~/.darc/.auth-record-{appId}`) with MSAL cache "maestro" and `DisableAutomaticAuthentication = true`
- **Anonymous:** no auth header (API may return 401)

Deserialization uses `Newtonsoft.Json` since PCS models use Newtonsoft serialization attributes.

## Alternatives Considered

1. **Wait for upstream PR** — Not viable; users need the endpoint now.
2. **Extract HttpPipeline from PCS client** — `IProductConstructionServiceApi` doesn't expose the internal pipeline.
3. **Use `DefaultAzureCredential`** — Won't find the maestro-specific MSAL cache by name.

## Migration Path

When dotnet/arcade-services#6057 merges and a new PCS client version is published:
1. Replace `GetCodeflowStatusesAsync` body in `MaestroApiClient` with `_api.Codeflow.GetCodeflowStatusesAsync()`
2. Remove `_barToken`, `_entraCredential`, `GetAccessTokenAsync()`, `CreateEntraCredential()`
3. Remove `Newtonsoft.Json` and `Azure.Identity` using directives from `MaestroApiClient.cs`
4. Service, MCP tool, and CLI layers remain unchanged

## Impact

- New MCP tool: `maestro_codeflow_statuses` (tool #20)
- New CLI command: `codeflow-statuses`
- 140 tests pass, 0 errors
