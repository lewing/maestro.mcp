# Holden — History

## Learnings

### MCP SDK 1.0 Feature Evaluation (2026-02-20)

- **Project is already on SDK 1.0.0** — the .csproj references `ModelContextProtocol 1.0.0`, so we've already completed the upgrade from the 0.8.0-preview.1 baseline. The upgrade brings stability guarantees (SemVer), bug fixes (base64 deserialization, JSON handling), and improved transport reliability (5 reconnection retries instead of 2).

- **Structured tool output (StructuredContent) is not urgent** — all 20 tools return `Task<string>` with markdown-formatted output. This works well for LLMs and human readers. Switching to typed objects would be a breaking change for consuming skills, require defining 20+ DTOs, and solve no current pain point. Backlogged as P3 for future experimentation if consumers request JSON output for automation.

- **Tool annotations (ReadOnlyHint, DestructiveHint, OpenWorldHint) add no value** — tool names and descriptions already disambiguate read vs. write operations. No tools perform truly destructive actions (triggers are non-destructive). All tools interact with the Maestro API (open world), so setting `OpenWorldHint: true` on everything is redundant metadata. Annotations are advisory-only per SDK docs, not security enforcement.

- **Resource links (ResourceLinkBlock) don't apply** — the server exposes 0 MCP resources (data comes from tools, not `resources/list`). GitHub URLs in tool output are external links, not MCP resource URIs. Adopting resource links would require architectural churn (adding resource endpoints, redesigning caching) with no clear benefit over markdown URLs.

- **New protocol features are automatic** — SDK 1.0 brings 2025-11-25 protocol compliance, OAuth backward compatibility, and improved JSON handling without code changes. Features like elicitation (dynamic prompting) and SSE resumability don't apply to our tool set or stdio transport.

- **Security posture unchanged** — tool annotations don't provide security (advisory-only). Auth enforcement remains at the PCS API layer (correct design per STRIDE analysis). Structured output vs. strings doesn't change trust boundaries — the data source (PCS API) and caching layer (SQLite) are unchanged.

- **Markdown-first design is a strength** — the current string-based approach is universal, portable, works across all MCP clients, and is human-readable. LLMs parse our semi-structured markdown (headers, lists, tables) without issues. No bugs or limitations have surfaced from this design choice.

### Naming Convention Review for Issue #9 (2026-02-20)

- **Current naming follows an implicit pattern**: Actions use verb prefixes (`trigger_`, `clear_`), queries use bare nouns. This distinction is actually a GOOD convention — it disambiguates read-only operations from state-changing actions. The proposed `maestro_get_*` prefixes would be redundant.

- **Real asymmetry: missing list/get pairs**: 2 of 4 resource types lack symmetrical tools. `maestro_channels` exists but no `maestro_channel` (get by ID). `maestro_build` exists but no `maestro_builds` (list with filters). This is a genuine gap — agents expect list/get pairs for core resources.

- **"Codeflow" vs "tracked" terminology split**: `maestro_codeflow_prs` (list) and `maestro_tracked_pr` (get) use different nouns for the same concept. Technically both are accurate ("codeflow PR" = the GitHub PR, "tracked PR" = Maestro's subscription record), but the inconsistency adds cognitive load. Low-priority fix via aliasing, not renaming.

- **Breaking changes aren't worth it**: The current 17 tool names are learnable and predictable once the pattern is understood. Renaming for marginal clarity gains would disrupt consuming skills for 6-12 months. Better to fill gaps (`maestro_builds`, `maestro_channel`) and document the pattern.

- **Recommendation**: Add 2 missing symmetrical tools (P1), document the naming convention in code/README (P2), consider aliasing `maestro_codeflow_pr` in future (P3 backlog). Reject breaking renames. Decision recorded in `.ai-team/decisions/inbox/holden-naming-conventions.md`.

### dotnet-replay Architecture & Feature Scoping (2026-02-20)

- **Single-file .NET 10 app design**: dotnet-replay is intentionally monolithic (~3300 lines in replay.cs) for easy distribution via `dnx`. Code organization relies on function nesting, helper utilities reuse, and pluggable format detection. This design choice constrains how new features are added — they must be thin wrappers around core parsing/rendering functions, not separate modules.

- **Pluggable format detection and parsing**: The tool detects and parses three transcript formats independently: (1) Copilot CLI JSONL events, (2) Claude Code JSONL, (3) Waza evaluation JSON. Each format has a dedicated parser (`ParseJsonlData`, `ParseWazaData`, `ParseClaudeData`) that normalizes to common data structures (`JsonlData`, `WazaData` records). This architecture is **ideal for adding new features** — diff, grep, and stats all leverage the same parsers and can reuse turn extraction utilities.

- **Rich turn-level metadata**: All transcript formats expose full turn data with timestamps, roles, content, and tool calls encoded in `JsonElement` structures. The existing code already navigates JSON structure introspection well (witness the summary mode extracting validation data from Waza transcripts, tool call counts from Copilot events). This makes feature implementation straightforward — no need to extend the data model.

- **Existing mode infrastructure is extensible**: The codebase already has multiple "modes" (interactive pager, stream, JSON, summary). New features (diff, grep, stats) should follow the same pattern: a command dispatcher at the top level checks for the mode flag (e.g., `if (cliArgs[0] == "diff")`), then routes to a feature-specific function. Each feature can inherit the existing `--json` and color/formatting utilities.

- **Test structure is mature**: Three test files (SummaryOutputTests, JsonOutputTests, EdgeCaseTests) cover existing modes comprehensively with xUnit. The pattern is to load sample transcripts (Copilot, Waza, malformed edge cases), invoke the parser, and validate output. Adding diff/grep/stats tests should follow the same structure: use existing sample data, add new test cases for alignment accuracy or search correctness.

- **Turn alignment for diff is feasible but algorithmic**: Issue #11's turn alignment algorithm is the core complexity. Strategy: for evaluation transcripts where both models solve the same task, exact timestamp matching works most of the time. Fuzzy matching (Levenshtein distance > 80%) handles cases where prompts differ slightly. Turn count will be similar for models on same task, so O(n²) or O(n log n) alignment is acceptable for transcript sizes <1000 turns.

- **Glob expansion and cross-platform file discovery**: Issues #12 and #13 require handling shell globs (e.g., `results/*.json`). Best approach: use `Directory.GetFiles` with pattern matching rather than relying on shell expansion — ensures consistent behavior across platforms. This utility should be shared between grep and stats.

- **Output format consistency**: All features should support `--json` for pipeline consumption and human-readable table/tree format for interactive use. The existing Spectre.Console markup system is excellent for this — it already powers colored output, tables (witness SummaryOutputTests validating table layout), and aligned text.

- **Recommended implementation order**: #13 (stats, 2–3 days) → #12 (grep, 3–4 days) → #11 (diff, 5–7 days). This order maximizes value delivery (stats immediately useful for Arena), reuses glob/extraction utilities across features, and delays the most complex algorithm (turn alignment) until last.

### Issue #1 Triage: Codeflow Feature Requests (2026-02-19)

- Triaged 9 feature requests from Issue #1 covering codeflow analysis workflows. **All 9 are architecturally feasible** with current PCS client surface. No fundamental blockers.
- Decomposed scope: P1 (high-impact) = 3 features (codeflow PRs, force-trigger, branch filtering); P2 (medium) = 3 features (history, flow graph, health); P3 (nice-to-have) = 3 features (VMR manifest, channel shorthand, build assets).
- **Proposed roadmap**: v0.2.1 (2 features + enhancements: force-trigger + branch filter + channel shorthand, ~10 hrs); v0.3 (3 composite features requiring GitHub API: codeflow-prs, flow-graph, health endpoint, ~2 weeks); v0.4+ (backlog niche features).
- **PCS API investigation needed** for features #4 (subscription history) and #9 (build assets) — Naomi to check if these endpoints exist in current PCS client NuGet. If not, file backlog items with Maestro team.
- **Key decision**: Force-trigger (feature #2) — current code already passes `isCoherencyUpdate: true` to PCS. Clarify with Larry whether this is correct behavior, then either document or expose as separate tool.
- **GitHub API strategy needed** — features #1, #5, #6 require GitHub integration (search PRs, get PR status). Team should decide: Octokit (REST), GraphQL client, or `gh` CLI wrapper. Recommending Octokit for simplicity.
- **Documented questions for Larry** on force-trigger semantics, GitHub client preference, and VMR scope.

📌 Triage document written to `.ai-team/decisions/inbox/holden-issue1-triage.md`. Covers feasibility, complexity, PCS dependencies, effort estimates, and actionable roadmap for team.

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

### GitHub Auth Cascade Threat Model (2025-07-16)

- Conducted STRIDE-informed analysis of the v0.6.0 GitHub auth cascade (`GitHubApiClient.cs`): GITHUB_TOKEN env var → `gh auth token` subprocess → anonymous fallback. 9 findings total — 0 Critical, 0 High, 2 Medium, 4 Low, 3 Info.
- **Most significant finding**: `process.WaitForExit()` with no timeout on the `gh auth token` subprocess call (GH-T1, Medium). This runs in a static initializer, so a hung `gh` process blocks the entire MCP server indefinitely. Fix: add 5-second timeout and kill on hang.
- **Static HttpClient token lifetime** (GH-T4, Medium): Token is set once at type-load time and never refreshed. Acceptable for short-lived MCP subprocess sessions, but needs documentation that token changes require restart.
- **Token handling is clean**: Confirmed the token value is never logged, never included in error messages, never persisted. Only auth method names go to stderr. `AuthenticationHeaderValue` validates the token format, preventing header injection.
- **URL construction is low-risk**: The `owner`/`repo`/`baseSha`/`headSha` parameters come from internal BAR API data, not MCP tool parameters. `IsVmrRepository` restricts to dotnet/dotnet. `ParseGitHubUrl` validates github.com host. SHA format validation (`^[0-9a-f]{7,40}$`) recommended as defense-in-depth but not urgent.
- **PATH-based executable resolution** for `gh` is standard practice and accepted — requires prior machine compromise to exploit.
- **Pattern observed**: The separation between "what goes to stderr" (auth method) vs "what stays in scope" (token value) is a good security pattern worth maintaining across the codebase.

📌 Threat model written to `.ai-team/decisions/inbox/holden-threat-model-github-auth.md`. 1 P1 fix (subprocess timeout), 2 P2 backlog items, 6 accepted.

📌 Team update (2026-02-22): Always pass DefaultBaseUri to PcsApiFactory — decided by Naomi

