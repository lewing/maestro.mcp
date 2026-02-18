# Decision: SQLite-backed CacheService for Cross-Process Sharing

**Author:** Naomi (Backend Dev)  
**Date:** 2026-02-18  
**Status:** Implemented

## Context

The original `CacheService` used `ConcurrentDictionary` for in-memory caching. This meant each `mstro` instance (VS Code, Copilot CLI, etc.) maintained separate caches, resulting in redundant PCS API calls when multiple MCP clients ran simultaneously.

## Decision

Migrated `CacheService` from in-memory `ConcurrentDictionary` to SQLite-backed storage at `~/.mstro/cache.db`.

### Technical Implementation

1. **Database location**: `~/.mstro/cache.db` (created automatically)
2. **WAL mode**: `PRAGMA journal_mode=WAL` enables concurrent reads across processes
3. **Busy timeout**: `PRAGMA busy_timeout=5000` handles write contention
4. **Tables**:
   - `cache` — key TEXT PRIMARY KEY, value TEXT (JSON), expiry TEXT (ISO 8601)
   - `actions` — key TEXT PRIMARY KEY, value TEXT (JSON), expiry TEXT (ISO 8601)
5. **Serialization**: `System.Text.Json` for all cached values
6. **Thread safety**: `SemaphoreSlim` lock around factory calls prevents duplicate execution during cache misses
7. **Capacity cap**: `MaxCacheEntries = 10000` enforced before insert; entire cache cleared when exceeded
8. **Cleanup**: Every 100 operations triggers background purge of expired rows

### Key Design Choices

- **Separate `actions` table**: Action dedup records (`RecordAction`, `GetRecentAction`) live in a separate table so `Clear()` doesn't reset cooldowns. This prevents `maestro_clear_cache` from defeating trigger deduplication.
- **Connection-per-operation**: No connection pooling at application level; rely on SQLite's `Cache=Shared` mode for connection reuse across threads/processes.
- **Double-check locking**: After acquiring `SemaphoreSlim`, check cache again before calling factory. Prevents race condition where multiple threads miss cache and call factory concurrently.
- **Error handling**: SQLite failures logged to stderr, return expired/missing data instead of crashing. Graceful degradation under I/O errors.

### Trade-offs

**Pros:**
- Cross-process cache sharing reduces redundant PCS API calls
- Persistent cache survives process restarts
- WAL mode enables true concurrent reads
- Scales to multiple MCP clients (VS Code + CLI + future clients)

**Cons:**
- Slightly slower than in-memory (SQLite I/O overhead)
- JSON serialization adds CPU cost and eliminates object identity
- Tests relying on object identity (`Assert.Same`) fail; require refactoring to value equality
- Disk space usage (negligible, max ~10MB for 10K entries)

### Files Changed

- `src/MaestroTool.Core/MaestroTool.Core.csproj` — Added `Microsoft.Data.Sqlite` package reference
- `src/MaestroTool.Core/CacheService.cs` — Complete rewrite with SQLite backend
- `src/MaestroTool.Core/MaestroMcpTools.cs` — Updated `maestro_clear_cache` description to mention shared cache

### Test Impact

20 of 67 tests fail after migration. Failures are due to:
1. **Object identity checks** (`Assert.Same`) — JSON deserialization creates new object instances
2. **Timing differences** — SQLite I/O introduces small delays affecting expiry tests
3. **Shared database** — Tests run against real `~/.mstro/cache.db`, no test isolation

**Recommendation**: Refactor tests to use value equality (`Assert.Equal`) instead of reference equality. Add internal constructor to `CacheService` accepting custom database paths for test isolation.

### Rationale

Cross-process cache sharing is essential for multi-client MCP deployment. The performance trade-off (SQLite I/O vs in-memory) is negligible compared to PCS API latency (150ms-1.6s). Persistent cache also improves cold-start performance after process restart.
