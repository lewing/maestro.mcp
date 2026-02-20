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

