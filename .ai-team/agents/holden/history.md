# Holden — History

## Learnings

### STRIDE Threat Model (2025-07-15)

- Conducted full STRIDE analysis of maestro.mcp. The auth cascade (PAT → Entra ID → Anonymous) is the highest-risk surface — the anonymous fallback is by design for read-only, but the server currently doesn't enforce read-only at the MCP tool layer for anonymous sessions. The `TriggerSubscription` and `TriggerDailyUpdate` tools rely entirely on the PCS API rejecting unauthorized callers, which is correct but means auth failures surface as opaque API errors rather than clean "you're not authenticated" messages.
- The in-memory cache (`ConcurrentDictionary`) has no size bounds. An attacker or misbehaving LLM generating unique cache keys via `noCache=false` with varied parameters could grow memory unbounded. Unlikely in practice (cache keys are derived from a small parameter space), but worth noting for HTTP deployment.
- The `maestro_clear_cache` tool is unauthenticated and available to any MCP client. In multi-user HTTP mode, one client can clear another's cache. In stdio mode this is a non-issue (single user per process).
- `GetBuildFreshnessAsync` creates `HttpClient` inline — noted by Amos as untestable, but also relevant for threat model: no certificate validation customization, follows redirects from aka.ms without validating the target domain.
- Action deduplication (2-minute cooldown via `CacheService`) is a useful defense against LLM retry storms but is trivially bypassed by clearing the cache first (`maestro_clear_cache` → `maestro_trigger_subscription`).
- The HTTP transport (`MaestroTool.Mcp`) has no authentication middleware — it's wide open on localhost:5000. This is fine for local dev but would be critical in any shared deployment.

📌 Team update (2025-07-15): STRIDE threat model completed — identified 14 threats, 8 with mitigations documented. P0 items (SSRF validation, dedup separation, tool-level auth gating) ready for next sprint. Decided by Holden, Naomi, Amos.

### SQLite Cache Migration Threat Model (2026-02-18)

- Conducted focused STRIDE analysis on the SQLite cache migration (ConcurrentDictionary → `~/.mstro/cache.db`). Identified 13 new threats across all 6 STRIDE categories. The migration fundamentally changes the trust boundary: data that was implicitly protected by process isolation is now accessible to any same-user process via a predictable filesystem path.
- **Highest severity findings**: Cache poisoning (S1, HIGH) and direct database tampering (T1, HIGH) by same-user processes, and plaintext persistence of operationally sensitive data (I1, HIGH). These are all inherent to moving from in-memory to on-disk storage.
- **Pragmatic assessment**: Same-user tampering threats (S1/T1/T2) require prior machine compromise. If an attacker has same-user code execution, they can already call the PCS API directly — cache tampering gains little. Prioritized file permissions (I2, P1) and corruption auto-recovery (D2, P1) as the actionable items.
- **Cross-process auth boundary** (E1) is interesting: an anonymous mstro instance can read data cached by an authenticated instance. Accepted as low risk since PCS allows anonymous reads anyway — the "escalation" is avoiding rate limits, not accessing protected data.
- **Key design recommendation**: HMAC integrity verification on cache entries (P2 backlog). Per-installation secret in `~/.mstro/.cache-key`, HMAC-SHA256 over key+value+expiry. This is the right long-term fix for S1/T1/T2 but not urgent for single-user dev workstations.
- **Immediate P1 actions**: (1) Explicit file permissions on `~/.mstro/` directory and `cache.db` — use `File.SetUnixFileMode` on Linux/macOS. (2) `PRAGMA integrity_check` in `InitializeDatabase()` with auto-delete-and-recreate on corruption.

📌 Threat model written to `.ai-team/decisions/inbox/holden-sqlite-threat-model.md`. 13 findings, 2 P1 items for next sprint, HMAC integrity on P2 backlog.

📌 Team update (2026-02-19): P1 security fixes completed — file permissions (I2) and corruption auto-recovery (D2) implemented in CacheService. 6 security tests written. All 73 tests passing. Decided by Naomi, Amos.
