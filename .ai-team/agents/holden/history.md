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
