### 2026-02-18: SQLite Cache Threat Model
**By:** Holden
**What:** STRIDE analysis of the SQLite cache migration
**Why:** New persistence layer introduces new attack surface — data that was previously ephemeral in-memory is now persisted to a world-predictable path on disk and shared across process boundaries

---

## Scope

This analysis covers threats **new to the SQLite migration** only. Threats from the original v0.1 STRIDE assessment (SSRF, HTTP transport, noCache abuse, etc.) are documented in `decisions.md` and are not repeated here unless the migration materially changed their risk profile.

### What changed
- Cache data persisted to disk at `~/.mstro/cache.db` (was in-memory `ConcurrentDictionary`)
- Multiple processes read/write the same database (WAL mode + `Cache=Shared`)
- All cached values JSON-serialized (PCS model objects: `Subscription`, `Build`, `Channel`, etc.)
- Database path is deterministic and predictable
- Connection string: `Mode=ReadWriteCreate;Cache=Shared`
- Expired entry cleanup runs fire-and-forget in `Task.Run`
- `internal` constructor accepts custom db paths (test seam)

---

## Findings Summary

| # | STRIDE | Severity | Threat | Effort |
|---|--------|----------|--------|--------|
| S1 | Spoofing | **HIGH** | Cache poisoning via same-user process | Medium |
| T1 | Tampering | **HIGH** | Direct database modification by external process | Medium |
| T2 | Tampering | **MEDIUM** | Action dedup manipulation (bypass or block cooldowns) | Small |
| T3 | Tampering | **LOW** | WAL/journal file manipulation during recovery | Small |
| R1 | Repudiation | **MEDIUM** | No cross-process write attribution | Small |
| I1 | Information Disclosure | **HIGH** | Sensitive operational data persisted in plaintext | Medium |
| I2 | Information Disclosure | **MEDIUM** | Database file permissions not explicitly set | Small |
| I3 | Information Disclosure | **LOW** | Data remnants in WAL/journal after Clear() | Small |
| D1 | Denial of Service | **MEDIUM** | Database write-lock DoS from external process | Small |
| D2 | Denial of Service | **MEDIUM** | Persistent database corruption across restarts | Medium |
| D3 | Denial of Service | **LOW** | Fire-and-forget cleanup failure accumulation | Small |
| E1 | Elevation of Privilege | **MEDIUM** | Cross-process auth boundary violation via shared cache | Medium |
| E2 | Elevation of Privilege | **LOW** | Auth level not persisted with cache entries | Small |

---

## Detailed Findings

### S1: Cache Poisoning via Same-User Process
**Severity:** HIGH  
**Category:** Spoofing

**Description:** Any process running as the same OS user can open `~/.mstro/cache.db` and insert arbitrary cache entries. A malicious process could inject fake subscription data (wrong source/target repositories), fabricated build metadata, or poisoned channel mappings. When a legitimate mstro instance reads the cache, it trusts the data as if it came from the PCS API.

**Attack vector:** Malware or a compromised tool running as the current user executes `INSERT OR REPLACE INTO cache (key, value, expiry) VALUES ('subs:...', '<malicious JSON>', '<far-future expiry>')`. The next mstro instance to read that key returns the poisoned data to the LLM, which may take action on it (e.g., trigger a subscription with a wrong build ID).

**Current mitigation:** None. The previous in-memory approach had implicit process-boundary isolation — only code within the mstro process could write to the cache.

**Recommended fix:**
1. Add an HMAC integrity tag column to both `cache` and `actions` tables. Compute HMAC-SHA256 over `key || value || expiry` using a per-installation secret stored in a separate file with restricted permissions (`~/.mstro/.cache-key`). Verify on read; discard entries that fail verification.
2. If HMAC is considered too heavy: at minimum, add a `source_pid` column and log warnings when reading entries written by a different process ID. This doesn't prevent the attack but creates a detection signal.

**Effort:** Medium

---

### T1: Direct Database Modification by External Process
**Severity:** HIGH  
**Category:** Tampering

**Description:** With in-memory caching, the data lived entirely within the mstro process's address space — tampering required compromising the process itself. SQLite moves this to a file that any same-user process can open and modify. An attacker can alter cached values (change subscription target repos, modify build commit hashes) or delete entries to force API re-fetches.

**Attack vector:** `sqlite3 ~/.mstro/cache.db "UPDATE cache SET value = '...' WHERE key = 'build:12345'"` — trivially scriptable, no special tools needed.

**Current mitigation:** SQLite's WAL mode and busy_timeout provide concurrency safety but zero integrity protection. Parameterized queries in CacheService.cs prevent SQL injection from within mstro, but that's irrelevant against an external writer.

**Recommended fix:** Same as S1 — HMAC integrity verification. Additionally, consider setting the SQLite database to use `PRAGMA locking_mode=EXCLUSIVE` as an option for single-process deployments where cross-process sharing isn't needed.

**Effort:** Medium

---

### T2: Action Dedup Manipulation
**Severity:** MEDIUM  
**Category:** Tampering

**Description:** The `actions` table stores trigger cooldown records. An external process can:
- **Delete action records** → removes cooldown protection, allowing rapid re-triggering
- **Insert fake action records** → blocks legitimate trigger attempts (dedup returns "already triggered")

This is a direct escalation of the previous "dedup bypass via cache clear" finding (now P0-fixed by separating tables), but the SQLite migration re-opens a variant: instead of going through `maestro_clear_cache`, an attacker directly manipulates the `actions` table.

**Attack vector:** `sqlite3 ~/.mstro/cache.db "DELETE FROM actions"` to remove cooldowns, or `INSERT INTO actions ...` with far-future expiry to block triggers.

**Current mitigation:** Tables are separated (Clear() doesn't touch actions), but external access bypasses this.

**Recommended fix:** HMAC integrity on the actions table (same as S1). Alternatively, if the HMAC approach is adopted, action records should use a different HMAC key than cache records to prevent cross-table replay.

**Effort:** Small

---

### T3: WAL/Journal File Manipulation During Recovery
**Severity:** LOW  
**Category:** Tampering

**Description:** SQLite WAL mode creates `cache.db-wal` and `cache.db-shm` sidecar files. If an attacker modifies the WAL file between a crash and the next process start, SQLite will replay the tampered WAL during recovery, injecting malicious data.

**Attack vector:** Requires precise timing (between process crash and restart) and knowledge of SQLite WAL format. Low probability.

**Current mitigation:** None explicit, but the attack window is narrow.

**Recommended fix:** No dedicated fix needed beyond S1 HMAC verification (which would catch tampered data regardless of injection vector). Document that `~/.mstro/` directory integrity matters.

**Effort:** Small

---

### R1: No Cross-Process Write Attribution
**Severity:** MEDIUM  
**Category:** Repudiation

**Description:** When Process A writes a cache entry and Process B reads it, there's no way to determine which process wrote the data. If a rogue mstro instance (or non-mstro process) writes bad data, investigation requires filesystem auditing tools (auditd, Procmon) that are typically not configured.

**Attack vector:** Not an active attack — this is an auditability gap that makes incident response harder after any of the tampering threats are exploited.

**Current mitigation:** Trigger actions have stderr audit logging (`[{timestamp}] Trigger: ...`), but this is per-process and doesn't cover cache reads/writes.

**Recommended fix:** Add a `written_by` column (PID + process name + timestamp) to cache entries. This provides lightweight forensic data without the cost of full HMAC. Not a security control — just an investigation aid.

**Effort:** Small

---

### I1: Sensitive Operational Data Persisted in Plaintext
**Severity:** HIGH  
**Category:** Information Disclosure

**Description:** The cache now persists the following to disk in plaintext JSON:
- **Subscription topology**: Source→target repo mappings, channel assignments, branch targets — reveals the full .NET dependency graph and which repos flow into which products
- **Build metadata**: Commit hashes, build dates, AzDO build numbers, repo URLs
- **Channel configurations**: All Maestro channel names and IDs
- **Action timestamps**: When triggers were last executed

Previously this data existed only in process memory and was lost on exit. Now it survives indefinitely on disk (expired entries are only cleaned up every 100 operations).

**Attack vector:** Any process with read access to `~/.mstro/cache.db` (typically any process running as the same user, or root) can exfiltrate the full operational topology. On shared systems (CI machines, dev VMs), other users may have access depending on umask.

**Current mitigation:** The previous STRIDE assessment noted "no PII flows through server" — this remains true. But subscription topology is "operationally sensitive" per Naomi's assessment. The data is now at rest rather than in transit/memory only.

**Recommended fix:**
1. **Short term (P1):** Explicitly set file permissions on `cache.db` at creation time. On Unix: `chmod 600`. On Windows: restrict ACL to current user.
2. **Long term (P2):** Consider SQLite encryption extension (SQLCipher or similar) if the threat model evolves to include shared-machine scenarios. For single-user dev workstations, file permissions are sufficient.

**Effort:** Medium (cross-platform file permissions are non-trivial in .NET)

---

### I2: Database File Permissions Not Explicitly Set
**Severity:** MEDIUM  
**Category:** Information Disclosure

**Description:** `Directory.CreateDirectory(cacheDir)` and SQLite's `Mode=ReadWriteCreate` create files with default OS permissions (umask on Unix, inherited ACL on Windows). On many Linux systems the default umask is `022`, meaning the database would be world-readable (`644`). On shared CI machines or jump boxes, this exposes cache data to other users.

**Attack vector:** `cat ~/.mstro/cache.db` or `sqlite3 ~/.mstro/cache.db` as a different user on the same machine.

**Current mitigation:** None. The `~/.mstro/` directory is created but permissions are not set.

**Recommended fix:** After `Directory.CreateDirectory`, explicitly set directory permissions to owner-only:
```csharp
if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
{
    File.SetUnixFileMode(cacheDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
```
This is available in .NET 7+. On Windows, the user profile directory is typically already restricted.

**Effort:** Small

---

### I3: Data Remnants in WAL/Journal After Clear()
**Severity:** LOW  
**Category:** Information Disclosure

**Description:** `maestro_clear_cache` executes `DELETE FROM cache`, but SQLite's WAL mode means the deleted data may persist in `cache.db-wal` until a checkpoint occurs. A forensic analysis of the WAL file could recover "cleared" data.

**Attack vector:** Requires filesystem access and SQLite forensic tools. Low probability for typical threat actors.

**Current mitigation:** None needed for the current threat profile (developer workstations).

**Recommended fix:** If required for compliance, add `PRAGMA wal_checkpoint(TRUNCATE)` after `DELETE FROM cache` in the `Clear()` method. For now, document as accepted risk.

**Effort:** Small

---

### D1: Database Write-Lock DoS from External Process
**Severity:** MEDIUM  
**Category:** Denial of Service

**Description:** WAL mode allows concurrent reads, but writes are serialized. A malicious process can acquire a long-running write transaction on `cache.db`, causing all mstro instances to block on writes until `busy_timeout` (5 seconds) expires. After timeout, cache writes silently fail (caught by try/catch), causing every request to bypass cache and hit the PCS API directly.

**Attack vector:** `sqlite3 ~/.mstro/cache.db "BEGIN IMMEDIATE; SELECT * FROM cache; /* hold lock open */"` — holds write lock indefinitely.

**Current mitigation:** `busy_timeout=5000` prevents infinite blocking, and error handling gracefully degrades (cache misses → API calls). But sustained lock-holding turns every mstro instance into a "noCache=true" client, potentially overwhelming the PCS API.

**Recommended fix:**
1. Log a distinct warning when `busy_timeout` is exceeded repeatedly (currently caught as generic exception).
2. Consider a circuit breaker: if N consecutive cache writes fail within a window, temporarily disable caching and warn the user rather than silently hammering PCS.

**Effort:** Small

---

### D2: Persistent Database Corruption Across Restarts
**Severity:** MEDIUM  
**Category:** Denial of Service

**Description:** With in-memory cache, corruption was impossible — each process started fresh. SQLite files can be corrupted by:
- Process crash during write (partially mitigated by WAL journaling)
- Disk failure or filesystem issues
- External process writing invalid data
- Network filesystem issues if `~/.mstro` is on a network mount (NFS, SMB)

Corruption persists across restarts — every new mstro instance inherits the broken database.

**Attack vector:** Write garbage bytes to `cache.db` or its WAL file. Or mount home directory on NFS (SQLite + NFS is notoriously unreliable).

**Current mitigation:** SQLite's WAL journaling handles crash recovery for well-formed transactions. Error handling catches SQLite exceptions. But a structurally corrupted database file causes persistent failures.

**Recommended fix:** Add corruption detection and auto-recovery:
```csharp
// In InitializeDatabase, after Open:
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "PRAGMA integrity_check;";
    var result = (string?)cmd.ExecuteScalar();
    if (result != "ok")
    {
        Console.Error.WriteLine($"[maestro-mcp] Cache database corrupted, recreating...");
        conn.Close();
        File.Delete(_dbPath);
        // Re-initialize
    }
}
```

**Effort:** Medium

---

### D3: Fire-and-Forget Cleanup Failure Accumulation
**Severity:** LOW  
**Category:** Denial of Service

**Description:** `MaybeCleanupExpired()` runs in `Task.Run` every 100 operations. If cleanup consistently fails (e.g., database locked by external process), expired entries accumulate. The error is logged but there's no retry or escalation mechanism.

**Attack vector:** Hold a write lock during cleanup windows → expired rows grow indefinitely → disk usage increases → eventually `MaxCacheEntries` triggers a full wipe of the data cache.

**Current mitigation:** The `MaxCacheEntries` cap prevents unbounded growth. When hit, entire cache is cleared.

**Recommended fix:** No code change needed — the capacity cap is sufficient. Document that cleanup failures are self-correcting via the capacity cap mechanism.

**Effort:** Small (documentation only)

---

### E1: Cross-Process Auth Boundary Violation via Shared Cache
**Severity:** MEDIUM  
**Category:** Elevation of Privilege

**Description:** Process A running with PAT authentication fetches subscription data and caches it. Process B running as anonymous reads the same cache key and receives data that required authentication to fetch. The cache acts as an implicit auth-level escalation channel.

For read-only data, this is arguably a feature (reduce API calls). But for operationally sensitive data like subscription topology, it means an anonymous mstro instance gets the same view as an authenticated one — the only difference is that anonymous can't call trigger tools (gated in `MaestroService`).

**Attack vector:** Start an anonymous mstro instance on the same machine as an authenticated one. The anonymous instance reads all cached data without any PCS authentication.

**Current mitigation:** The PCS API allows anonymous read access anyway (just rate-limited), so the practical escalation is limited to avoiding rate limits. The auth gate on trigger tools is enforced at the service layer, not the cache layer, so write operations are still protected.

**Recommended fix:** Accept as low risk for v0.2. If needed later:
1. Add an `auth_level` column to cache entries
2. On read, skip entries written at a higher auth level than the current session
This would prevent anonymous processes from benefiting from authenticated cache, but at the cost of eliminating the cross-process sharing benefit for mixed-auth deployments.

**Effort:** Medium

---

### E2: Auth Level Not Persisted with Cache Entries
**Severity:** LOW  
**Category:** Elevation of Privilege

**Description:** Related to E1. Cache entries don't record which auth level was used to fetch them. This makes it impossible to implement auth-scoped cache reads later without a schema migration.

**Attack vector:** Not directly exploitable — this is a design gap that prevents future mitigation of E1.

**Current mitigation:** None needed currently (E1 is accepted risk).

**Recommended fix:** When implementing S1 HMAC, also add `auth_level TEXT` column to the cache table schema. Low-cost future-proofing.

**Effort:** Small

---

## Priority Matrix

| Priority | Findings | Rationale |
|----------|----------|-----------|
| **P1** | I2 (file permissions) | Small effort, high impact. Prevents information disclosure on shared machines. Ship in next patch. |
| **P1** | D2 (corruption recovery) | Medium effort, prevents persistent DoS. A corrupted cache that never self-heals is unacceptable for a tool that runs unattended. |
| **P2** | S1/T1 (HMAC integrity) | Medium effort, addresses highest-severity threats. Deferred because same-user tampering requires prior machine compromise — the machine itself is already owned at that point. |
| **P2** | T2 (action dedup integrity) | Included in S1/T1 HMAC work. |
| **P2** | R1 (write attribution) | Small effort, useful for incident response. Include when touching cache schema. |
| **P3** | D1 (write-lock DoS) | Edge case — requires malicious same-user process. Current graceful degradation is adequate. |
| **P3** | E1/E2 (auth boundary) | Accept for v0.2. Anonymous PCS read access makes this low-practical-impact. |
| **P3** | I3/T3/D3 (WAL remnants, journal tampering, cleanup) | Low severity, low probability. Document as accepted risks. |

## Recommendations for Next Sprint

1. **I2 — File permissions** (`CacheService.cs`): Add explicit `chmod 700` on `~/.mstro/` directory and `chmod 600` on `cache.db` after creation. Use .NET 7+ `File.SetUnixFileMode` on Linux/macOS. ~20 lines of code.

2. **D2 — Corruption auto-recovery** (`CacheService.cs`): Add `PRAGMA integrity_check` in `InitializeDatabase()`. On failure, delete and recreate the database file. ~15 lines of code.

3. **Backlog S1/T1 — HMAC integrity**: Design the keying strategy (per-installation random key in `~/.mstro/.cache-key`). This is the right long-term answer but not urgent for dev-workstation deployments.

## Accepted Risks

- **Same-user process tampering** (S1/T1): Requires prior machine compromise. The machine owner can already read PCS data directly via `darc` or the API. Cache tampering gains little that direct API access doesn't already provide.
- **Cross-process auth boundary** (E1): Anonymous PCS read access is intentional. The cache sharing across auth levels is a performance feature, not a security bypass.
- **WAL data remnants** (I3): Acceptable for developer workstations. No PII or credentials in cache. Subscription topology is operationally sensitive but not classified.
