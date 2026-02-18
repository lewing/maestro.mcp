# Threat Model Fixes — Top 5

**Author:** Naomi (Backend Dev)  
**Date:** 2025-07-15  
**Status:** Implemented

## Summary

Implemented 5 security fixes from the STRIDE threat model session (P0 and P1 items). All changes are surgical — no refactoring, no behavior changes for non-security paths. Build clean, 49 tests passing.

## Fixes Applied

### 1. SSRF — Channel parameter sanitization (CRITICAL → Fixed)

- **File:** `MaestroService.cs` — `GetBuildFreshnessAsync`
- **What:** Regex validation `^[a-zA-Z0-9.\-]+$` on `channel` parameter before URL interpolation. Redirect URL host validated against `*.blob.core.windows.net` and `dotnetcli`. Invalid channels return a `BuildFreshnessResult` with `IsAvailable: false` and descriptive error.
- **Why:** User-supplied `channel` was interpolated into `https://aka.ms/dotnet/{channel}/daily/...` — path traversal (`../../`) could target arbitrary aka.ms short links, and redirect URLs were followed without domain validation.

### 2. Auth-level gate on trigger tools (HIGH → Fixed)

- **Files:** `IMaestroApiClient.cs`, `MaestroApiClient.cs`, `MaestroService.cs`, `MaestroMcpTools.cs`
- **What:** Added `AuthLevel` enum (`Pat`, `EntraId`, `Anonymous`) to the API client interface. `CreateApi()` returns the resolved auth level alongside the API instance. Service-layer trigger methods throw `InvalidOperationException` when anonymous. MCP tool methods catch this and return `🔒 Authentication required...` message.
- **Why:** Anonymous sessions could call trigger tools and get opaque 401 errors from PCS. Now they get a clear, actionable error message before any API call is attempted.

### 3. Separate action dedup from data cache (MEDIUM → Fixed)

- **File:** `CacheService.cs`
- **What:** Action dedup records now live in a separate `_actions` ConcurrentDictionary. `Clear()` only clears the data `_cache`. Added `ClearActions()` (not exposed via MCP).
- **Why:** `maestro_clear_cache` → `_cache.Clear()` was wiping trigger cooldown records, enabling abuse (clear cache → re-trigger immediately). Now `Clear()` preserves action dedup integrity.

### 4. Trigger audit logging (MEDIUM → Fixed)

- **Files:** `MaestroService.cs`, `MaestroMcpTools.cs`
- **What:** `Console.Error.WriteLine` with ISO 8601 timestamp, method name, and args for all trigger invocations. Both "triggered" and "dedup-skipped" cases are logged.
- **Why:** No audit trail existed for trigger actions beyond in-memory dedup. Stderr logging is consistent with existing diagnostic output convention (`[maestro-mcp]` prefix pattern).

### 5. Cache size cap (MEDIUM → Fixed)

- **File:** `CacheService.cs`
- **What:** `MaxCacheEntries = 10000` constant. Before adding a new entry, if `_cache.Count >= MaxCacheEntries`, clear entire data cache and log to stderr. Simple eviction appropriate for single-user MCP server.
- **Why:** `ConcurrentDictionary` grew unbounded — varied query parameters could cause memory exhaustion. Full LRU is overkill; clearing on capacity hit is safe because the cache is just a performance optimization.

## Test Impact

- Replaced `Clear_ResetsActionRecords` with `Clear_DoesNotResetActionRecords` (verifies action records survive data cache clear)
- Added `ClearActions_ResetsActionRecords` (verifies explicit action clearing works)
- Total: 49 tests, all passing

## Remaining Threat Items (not addressed in this batch)

- HTTP transport auth middleware (P1, deferred to v0.3)
- Entra auth record file permissions warning (P1, small effort)
- noCache rate limiting (P2)
- TriggerDailyUpdate blast radius gating (P2)
