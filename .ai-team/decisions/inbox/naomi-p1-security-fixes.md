# P1 Security Fixes: SQLite Cache Hardening

**Author:** Naomi (Backend Dev)  
**Date:** 2026-02-19  
**Status:** Implemented

## Context

Holden's STRIDE threat model identified two P1 (MEDIUM severity) vulnerabilities in the SQLite cache implementation that could lead to information disclosure and persistent DoS:

1. **I2 (Info Disclosure):** `~/.mstro/cache.db` directory created with default permissions could be world-readable on shared Linux/macOS systems, exposing cached PCS data (subscription topology, build metadata).

2. **D2 (Denial of Service):** Corrupted SQLite database files (from crashes, disk errors, or tampering) would cause persistent startup failures. No auto-recovery mechanism existed — manual intervention required.

## Decision

Implemented two surgical fixes in `CacheService.cs`:

### Fix 1: Directory Permission Hardening (I2)

After creating `~/.mstro/` or custom cache directories, explicitly set owner-only permissions:

- **Linux/macOS:** Call `File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)` to set `700` permissions
- **Windows:** No action needed — user profile directories are already restricted. Documented this in code comment.

Applied in **two places**:
1. `GetDefaultDbPath()` — production path (`~/.mstro/`)
2. `internal CacheService(string dbPath)` constructor — test paths and custom locations

### Fix 2: Corruption Auto-Recovery (D2)

At the start of `InitializeDatabase()`, after opening the connection:

1. Run `PRAGMA integrity_check`
2. If result is NOT `"ok"` (or if `SqliteException` is thrown during Open):
   - Log `[maestro-mcp] Cache database corrupted, recreating...` to stderr
   - Close and delete the DB file
   - Delete WAL/SHM sidecar files if they exist
   - Re-open a clean database
   - Continue with normal initialization (create tables, set PRAGMAs)

**Edge case handled:** Corrupted database headers that prevent even opening the file are caught via `SqliteException` during `conn.Open()` and trigger the same delete-and-recreate flow.

## Implementation Details

- **Conditional logic:** `if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())` guards Unix-specific file permission APIs
- **Resource management:** Wrapped `InitializeDatabase()` in try/finally to ensure proper connection disposal
- **Test compatibility:** Both fixes work seamlessly with temp test paths — no test changes required

## Impact

- **Security posture:** Mitigated MEDIUM-severity info disclosure on shared machines and persistent DoS from corruption
- **User experience:** Cache now self-heals on startup if corrupted — no manual intervention needed
- **Performance:** Negligible overhead (one `PRAGMA` query at startup)
- **Testing:** All 67 existing tests pass (48 original + 19 v0.2.0)

## Files Changed

- `src/MaestroTool.Core/CacheService.cs` — Added permission hardening and corruption recovery (surgical edits, ~40 lines modified)

## Rationale

Both fixes are **defense-in-depth** measures:

1. **Permission hardening:** Prevents accidental exposure on shared dev machines where `umask` might be too permissive. Low-cost insurance.

2. **Auto-recovery:** SQLite corruption is rare but catastrophic without recovery. The cache is non-critical (can be rebuilt from PCS API) — deleting and recreating is the safest response.

These fixes align with the team's principle: **"Fail gracefully, don't brick the tool."** Users should never need to manually delete `~/.mstro/cache.db` to recover from corruption.
