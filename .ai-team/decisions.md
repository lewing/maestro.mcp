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
