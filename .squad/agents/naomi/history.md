# Naomi — History

## Core Context

**Role:** Backend developer on maestro.mcp. Implements MCP tool surface improvements, bug fixes, and infrastructure enhancements.

**Architecture knowledge:**
- **3-tier auth cascade**: env var (MAESTRO_BAR_TOKEN) → Entra ID (cached darc credentials) → anonymous. Guard on auth record file existence prevents browser popups.
- **SQLite cache**: Cross-process sharing via WAL mode, JSON serialization, SemaphoreSlim lock for dedup, auto-cleanup every 100 ops, max 10K entries.
- **PcsApiFactory**: Always use overloads with explicit `baseUri` parameter ("https://maestro.dot.net"). Parameterless versions crash.

**Key files owned:**
- `src/MaestroTool.Core/MaestroApiClient.cs` — Auth cascade, API client factory
- `src/MaestroTool.Core/CacheService.cs` — SQLite cache with TTLs
- `src/MaestroTool.Core/MaestroService.cs` — Business logic (subscriptions, builds, channels, etc.)
- `src/MaestroTool.Core/MaestroMcpTools/` — Tool surface definitions and descriptions (partial classes)
- `src/MaestroTool/Program.cs` — CLI commands
- `src/MaestroTool.Tests/` — Unit tests (xUnit, NSubstitute)

## Learnings

### 2026-06-11: MCP tool-description economy

- Tool descriptions are always-loaded routing context: lead with a verb, keep them to 1–2 sentences, and let parameter `[Description]` attributes carry formats, defaults, valid values, and filter behavior.
- Adding filters tempts re-explaining them in the tool description; resist that growth vector by moving filter semantics to parameter descriptions and keeping only brief cross-routing hints on the tool.

### 2026-05-22: PR #24 review fixes — filter-miss UX and CLI validation

- Empty-after-filter results should distinguish backend emptiness from filter misses; when unfiltered data exists but `staleOnly`/`channelFilter`/`sourceRepoFilter` remove everything, echo the applied filters in the response.
- CLI command handlers should validate user-supplied ranges before allocating timeout/cancellation resources or calling services that throw domain validation exceptions; print a clear error and exit non-zero.
- Verification: `dotnet test --verbosity minimal` passed 208/208, and invalid `flow-graph --days 0` / `--timeout-seconds -1` now exit 1 with friendly errors.

### 2026-05-22: Issue #19 flow graph scope-reduction defaults

- Changed `maestro_flow_graph` default scope from 7 days with eager build-time metrics to 3 days with `includeBuildTimes=false`, while keeping `days` and `includeBuildTimes` as opt-in expansion knobs.
- Lowered the MCP flow graph timeout to 30 seconds so pathological default calls fail fast instead of consuming the old 2-minute budget.
- PCS client gotcha: `IChannels.GetFlowGraphAsync` already accepts `includeBuildTimes`; passing `false` is the lazy-fetch behavior because PCS skips detailed build timestamp resolution for the whole graph.
- Verification: `dotnet build --no-restore --verbosity minimal` succeeded and `dotnet test --no-restore --verbosity minimal` passed 193/193.
- Perf spot-check: local CLI default flow graph call for `.NET 10.0.1xx SDK` channel returned in ~28s via the 30s guard; PCS did not complete the graph within the budget on that live channel.

### 2026-05-22: PR #23 reviewer fixes — error-state filtering and AzDO short names

- `staleOnly`/"show broken" filters must include unknown or errored health states, not just explicit stale booleans; silently omitting error rows hides the highest-risk cases.
- Compact health output should avoid mixed signals: errored rows render as `error`, not `current`, even when `IsStale` is false.
- AzDO repository display/filter short names should reuse `MaestroService.ParseAzDoUrl`; `dev.azure.com/{org}/{project}/_git/{repo}` and legacy Visual Studio URLs parse to `(org, project, repo)`, which can be rendered consistently as `org/project/repo`.

### 2026-05-22: `maestro_subscription_health` stale filters and compact mode

- Added opt-in MCP parameters `staleOnly`, `channelFilter`, `sourceRepoFilter`, and `compact`; no-arg output remains the same detailed per-subscription block.
- Names are domain-specific rather than a generic `filter` because subscription health has two natural axes: channel and source repo; `staleOnly` matches the common "what is broken" workflow.
- Filters apply after `GetSubscriptionHealthAsync` completes its parallel fan-out, preserving PR #20's concurrency and avoiding new cache/API-key dimensions for ad hoc substring searches.
- Extracted formatter helpers so tests can target filtering and markdown rendering directly without PCS network calls.
- Compact lines intentionally omit channel names and shorten PR URLs to `#N` to keep scanning dense: `⚠️ dotnet/runtime → main: 42 commits behind (PR: #123)`.
- Live dotnet/dotnet measurement from local tool harness: detailed formatter 24,398 bytes for 93 subscriptions / 43 stale; `staleOnly + compact` 3,049 bytes (~87.5% smaller).

### 2026-05-22: `maestro_channels` low-cost filtering

- Chose optional `filter`, `classification`, and `compact` parameters on `maestro_channels`; no-arg calls keep the previous full bulleted markdown output.
- `classification` flows to PCS `IChannels.ListChannelsAsync(classification, cancellationToken)` and gets its own cache key; `filter` is applied after the cached API result so name searches do not multiply API calls.
- Kept `compact` as a bool instead of a format enum because the immediate need is a single low-token `name → id` text mode, not a broader output contract.
- Deferred pagination: channels is small and hierarchical, and the prior audit found markdown tools do not fit `LimitedResults<T>` cleanly.
- Live measurement on 168 channels: current full MCP-style markdown is 6,392 bytes; compact is 5,048 bytes (1,344 bytes / 21% saved). Filtering `.NET 10` reduces the list to 30 channels: 1,289 bytes full, 1,049 bytes compact.

## 2026-05-21: PR #20 — Parallelize subscription_health GitHub fan-out

**PR:** https://github.com/lewing/maestro.mcp/pull/20  
**Branch:** `squad/parallelize-subscription-health`  
**Date:** 2026-05-21

**Shipped:**
- Converted serial `foreach` in `GetSubscriptionHealthAsync` to `Task.WhenAll` with `SemaphoreSlim(5)` for concurrent execution
- Extracted per-subscription logic into `CheckSubscriptionHealthAsync` helper method
- Added `SemaphoreSlim(5, 5)` field to `MaestroService` to limit concurrent GitHub API calls

**Performance impact:**
- Before: N subscriptions × ~1s each = ~N seconds wall time (serial)
- After: max(subscriptions) × ~1s with 5 concurrent = ~(N/5) seconds (parallel)
- Typical fan-out (5 subscriptions): 5s → ~1s

**Behavior preservation:**
- Error handling: Exceptions captured per-subscription, included in results
- Output order: Results maintain input subscription order (Task.WhenAll preserves order)
- Skip behavior: Subscriptions with no channel assigned filtered before parallel execution

**Test result:** 179/179 passed ✅  
**Build:** 0 errors, 0 warnings ✅

**Context:** Holden's verdict in `.squad/log/2026-05-21T18-04-13-holden-pagination-verdict.md` identified the serial fan-out as the real performance bottleneck, recommending parallelization over progress notifications.

**Recent deliverables (2026-03-13, Session 2):**
- Created `src/MaestroTool/copilot-skill.md` — Lightweight skill file shipped in NuGet package (~6KB)
- Created `.ai-team/skills/maestro-cli/SKILL.md` — Squad skill documentation for CLI-as-skill pattern
- Added `mstro guide` command to `Program.cs` — Workflow-organized markdown guide (~5KB)
- All 3 deliverables focus on teaching agents to use `mstro` CLI via bash instead of MCP tools
- Build verified successful, guide command tested and working

**Recent deliverables (2026-03-13, Session 1):**
- Executed Holden's restructuring plan (Option A: partial classes + subfolders)
- Moved API clients to domain folders (Maestro/, GitHub/, AzDO/) using git mv to preserve history
- Split 902-line MaestroMcpTools.cs into 6 partial class files organized by domain:
  - MaestroMcpTools.cs (34 lines): class declaration, constructor, Timestamp helper
  - MaestroMcpTools.Channels.cs (94 lines): 3 channel tools
  - MaestroMcpTools.Subscriptions.cs (318 lines): 5 subscription tools
  - MaestroMcpTools.Builds.cs (153 lines): 5 build tools
  - MaestroMcpTools.Codeflow.cs (339 lines): 6 codeflow tools including FormatBuild/FormatFlowStatus helpers
  - MaestroMcpTools.Utilities.cs (19 lines): 1 cache utility tool
- Moved test files to mirror source structure (MaestroMcpTools/, Maestro/, AzDO/ subdirectories)
- NO namespace changes - all files remain in MaestroTool.Core namespace
- NO DI registration changes required - partial class is transparent to dependency injection
- All 167 tests pass after restructure

**Recent deliverables (2026-03-12):**
- Implemented P0 (description cleanup), P1-M1 (smart trigger), P1-M3 (channel resolution), P1-M4 (cross-refs)
- Trimmed token waste from tool descriptions (removed "Returns X, Y, Z")
- Made trigger_subscription composite (optional buildId, auto-resolve via sourceRepository + channelName)
- Changed maestro_channel to accept string channelNameOrId (int ID resolution internal)
- Added cross-references between overlapping subscription/build/channel tools
- All 167 tests pass (commit 792b4ee)

**Recent deliverables (2026-03-13):**
- Executed Holden's restructuring plan (Option A: partial classes + subfolders)
- Moved API clients to domain folders (Maestro/, GitHub/, AzDO/) using git mv to preserve history
- Split 902-line MaestroMcpTools.cs into 6 partial class files organized by domain:
  - MaestroMcpTools.cs (34 lines): class declaration, constructor, Timestamp helper
  - MaestroMcpTools.Channels.cs (94 lines): 3 channel tools
  - MaestroMcpTools.Subscriptions.cs (318 lines): 5 subscription tools
  - MaestroMcpTools.Builds.cs (153 lines): 5 build tools
  - MaestroMcpTools.Codeflow.cs (339 lines): 6 codeflow tools including FormatBuild/FormatFlowStatus helpers
  - MaestroMcpTools.Utilities.cs (19 lines): 1 cache utility tool
- Moved test files to mirror source structure (MaestroMcpTools/, Maestro/, AzDO/ subdirectories)
- NO namespace changes - all files remain in MaestroTool.Core namespace
- NO DI registration changes required - partial class is transparent to dependency injection
- All 167 tests pass after restructure

## Key Patterns & Learnings (Summarized)

**MCP Dynamic Completions Research (2026-05-08):** The C# ModelContextProtocol SDK v1.3.0 **DOES** support dynamic parameter completion via `WithCompleteHandler()`, but **ONLY for Prompts and Resources** — NOT for Tools. MCP spec defines `completion/complete` JSON-RPC method accepting `PromptReference` or `ResourceTemplateReference` only. Static completion via `[AllowedValues]` attribute also supported. Tool parameters cannot have dynamic completion in current spec (no `ToolReference` type exists). Workaround: expose a discovery tool (e.g., `maestro_list_channels`) that agents call before main tool, or return structured error with valid options when validation fails.

**CLI-as-Skill (2026-03-13):** Lightweight skill file (`copilot-skill.md`) in NuGet, Squad skill file documenting CLI vs MCP preferences, `guide` command with workflow-organized markdown. Portable pattern for other dual-mode tools (helix.mcp, etc.).

**Reflection-Based Schema Generation (2026-03-13):** `SchemaGenerator.cs` reflects return types into JSON skeletons with placeholders (`<string>`, `0`, `<datetime>`, etc.). `--schema` flag on all query commands, cycles guarded at depth 5, uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`.

**Partial Class Organization (2026-03-13):** Split 902-line file by domain (Channels, Subscriptions, Builds, Codeflow, Utilities). Each file self-contained with complete using statements. Benefits: clear review targets, folder structure mirrors API clients, zero breaking changes.

**Known constraints:** SQLite tests fail on reference equality (JSON deserialization); value equality works in production. PcsApiFactory requires explicit `baseUri`. Tool descriptions token-counted in agent routing—conciseness matters.

### Archive: Detailed Learning Sessions

*Full session notes on CLI help text, schema architecture, partial class extraction gotchas, ConsoleAppFramework limits, and portability patterns archived 2026-05-08 for size management. See git history (commits: restructure series, schema-implementation) for original detailed context.*

---

## Archive: Earlier Sessions

*Earlier detailed entries (PcsApiFactory fix, SQLite migration, auth architecture, smoke tests) archived 2026-03-12. Original content preserved in git history and .ai-team/log/.*


📌 Team update (2026-03-13): Restructure review approved — Holden reviewed and approved the restructure implementation. All 20 MCP tools present exactly once across Channels, Subscriptions, Builds, Codeflow, and Utilities partials. API surface and namespace stability preserved. Full test suite passing (167/167). — decided by Holden

### CLI Help Text Enhancement (2026-03-13)

**Decision merged to decisions.md:**
- Enhanced all CLI command `[Description]` attributes to mirror MCP tool descriptions
- Added 2 missing commands: `channel` (singular) and `builds` for parity with MCP tools (20 tools total)
- Implemented CLI-as-skill pattern: agents can use `mstro --help` instead of MCP tool descriptions
- Progressive disclosure: command-level help → command-specific help for parameters

**Key architectural insights:**
- ConsoleAppFramework 5.x limitation: no parameter-level descriptions in help output (command-level only)
- Pattern is portable to other CLI tools (e.g., helix.mcp) using only framework-provided attributes
- Cross-references use kebab-case command names for consistency
- Descriptions include: purpose, cross-refs, defaults, auth requirements

**Next phases (pending Larry's approval of Holden's skill architecture):**
1. Implement `maestro://guide` MCP resource (static markdown guide)
2. Publish Copilot skill that routes to CLI + resource
3. Document CLI-as-skill pattern for agents

**Recent deliverables (2026-03-13, Session 3 - Schema Implementation):**
- Implemented reflection-based schema generation engine in `src/MaestroTool.Core/CliSchema/SchemaGenerator.cs`
- Added `--schema` flag to all 17 query commands in `Program.cs`
- Schema uses `TryPrintSchema<T>(bool schema)` helper at top of command body; short-circuits before API lookups
- Placeholder mappings: strings → `"<string>"`, numerics → `0`, enums → `"<Value1|Value2|...>"`, with cycle protection (max depth 5)
- Output uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` to keep placeholders human-readable
- Decision files merged: holden-schema-architecture.md, naomi-schema-implementation.md

**Related decision:** CLI Schema as Intentional Contracts (holden-schema-architecture.md) — schema generation from shared CLI contract types in Core, not raw BAR client models

### MCP SDK Version Review (2026-05-08)

**Current state:**
- We're on ModelContextProtocol v1.0.0 (both base package and AspNetCore)
- Latest stable is v1.3.0 (released ~20 hours ago)
- We're 3 minor versions behind

**Key insights from upgrade review:**
- Clean upgrade path — no breaking changes affect our code
- v1.1.0: Auto-completion via `AllowedValuesAttribute`, in-flight message handler cleanup fixes
- v1.2.0: Legacy SSE disabled by default (doesn't affect us — we use Streamable HTTP), DI scope lifetime fix for task-augmented tools, `RequestContext` 2-arg constructor obsoleted
- v1.3.0: Public `ClientTransportClosedException` with structured diagnostics, process crash fix for stderr callbacks, stateless HTTP fix for `listChanged` capability

**Our usage patterns:**
- Stdio transport: `.WithStdioServerTransport()` in Program.cs (CLI `mcp` command)
- HTTP transport: `.WithHttpTransport()` in MaestroTool.Mcp/Program.cs (ASP.NET Core host)
- Tool surface: 20 tools via `[McpServerTool]` attributes, organized in 5 partial class files
- No use of: `CallToolResult`, `WithMeta`, `WithProgress`, `AllowedValuesAttribute`, prompts, resources, custom output schemas, manual `RequestContext` construction

**Decision:** Recommend upgrade to v1.3.0 with high confidence — gains reliability fixes, no code changes needed, all 167 tests should pass without modification.

**Future opportunities:**
- `AllowedValuesAttribute` for channel names could improve agent UX (but channel list changes over time)
- `CallToolResult` with structured output schemas if we need richer tool responses
- Role/identity propagation patterns from v1.3.0 docs if we add multi-tenant scenarios

### MCP SDK Upgrade Execution (2026-05-08)

**Applied upgrade:**
- ModelContextProtocol v1.0.0 → v1.3.0 (all 3 projects)
- ModelContextProtocol.AspNetCore v1.0.0 → v1.3.0 (MaestroTool.Mcp)

**Results:**
- Build: Clean — 0 warnings, 0 errors (6.19s Release build)
- Tests: 179/179 passed (21.25s) — test count increased from 167 to 179 since last verification
- No new obsolete-API warnings from v1.2.0 changes (we don't use `RequestContext` 2-arg constructor or EnableLegacySse)
- No code changes required — upgrade was transparent

**Verification notes:**
- No breaking changes affecting our stdio/HTTP transport usage
- No impact on `[McpServerTool]` attribute-based tool definitions
- Legacy SSE deprecation (v1.2.0) doesn't affect us — we use Streamable HTTP transport
- Test suite expanded naturally (new cache tests added in prior work)

## 2026-05-21: Assigned — Microsoft.Data.Sqlite 9.0.0 → 10.0.8

**Holden review approved** (2026-05-21). Naomi owns the Sqlite bump; pair it with safe patch upgrades to reduce PR churn.

**Details:**
- **Package:** Microsoft.Data.Sqlite 9.0.0 → 10.0.8 (MAJOR bump)
- **Risk level:** Low — our CacheService.cs uses only stable ADO.NET APIs (SqliteConnection, SqliteCommand, etc.)
- **Verification:** Clean build (0 warnings), 179+ tests pass, manual cache smoke test (cache.db creation/operation)
- **Bundled with:** Extensions.DependencyInjection 10.0.3→10.0.8, Extensions.Hosting 10.0.0→10.0.8, PCS Client beta refresh

**Rationale:** Sqlite 10.x aligns with net10.0 TFM and avoids unnecessary version skew with the broader .NET ecosystem.

## 2026-05-21: PR #17 — Sqlite 9→10 + Extensions 10.0.8 + PCS Client beta refresh

**PR:** https://github.com/lewing/maestro.mcp/pull/17  
**Branch:** `squad/deps-sqlite-extensions-bump`  
**Date:** 2026-05-21

**Shipped:**
- Microsoft.Data.Sqlite: 9.0.0 → 10.0.8 (major, Holden-approved)
- Microsoft.Extensions.DependencyInjection: 10.0.3 → 10.0.8
- Microsoft.Extensions.Hosting: 10.0.0 → 10.0.8
- Microsoft.DotNet.ProductConstructionService.Client: 1.1.0-beta.26161.4 → 1.1.0-beta.26271.2

**Test result:** 179/179 passed ✅  
**Build:** 0 errors, 0 warnings ✅  
**Note:** Shared-environment branch contention observed (other agents switching branches between bash calls). Mitigated by running checkout + edits + build + commit atomically.

---

## 2026-05-21: Dependency Bump — Sqlite + Extensions

**Task:** Upgrade Microsoft.Data.Sqlite from 9.0.0 to 10.0.8 along with safe patch bumps for Microsoft.Extensions packages and PCS Client beta.

**Deliverable:** PR #17 (`squad/infra-sqlite-extensions-bump`)

**Key changes:**
- Microsoft.Data.Sqlite 9.0.0 → 10.0.8 (approved by Holden; aligns net10.0 TFM with Sqlite major version)
- Microsoft.Extensions.DependencyInjection 10.0.3 → 10.0.8 (patch)
- Microsoft.Extensions.Hosting 10.0.0 → 10.0.8 (patch)
- Microsoft.DotNet.ProductConstructionService.Client 1.1.0-beta.26161.4 → 1.1.0-beta.26271.2 (beta patch)

**Verification:** 179/179 tests pass; cache service smoke test validates (CacheService.cs uses only stable ADO.NET APIs unchanged since v2)

**Incident:** Shared repository environment caused git branch contention during concurrent execution with alex-1 and amos. Mitigation: ran entire workflow (checkout → edits → build → test → commit) atomically within single bash session.

**Team recommendation:** Future parallel fan-outs use SQUAD_WORKTREES=1 to isolate agent worktrees.

## 2026-06-24: PR feat/subscription-outcomes — SubscriptionTriggerOutcomes API integration

**Branch:** `feat/subscription-outcomes`  
**Commits:** `3095e72`, `be26c7a`, `b3fe77b`  
**Date:** 2026-06-24

**Shipped:**
- **Step A:** Bumped PCS client `1.1.0-beta.26271.2` → `1.1.0-beta.26324.1` (adds `ISubscriptionTriggerOutcomes` API)
  - Also bumped `Microsoft.Extensions.DependencyInjection` `10.0.8` → `10.0.9` (transitive dependency from PCS)
  - Build: 0 warnings, 0 errors; Tests: 208/208 passed ✅
- **Step B:** Added `maestro_subscription_outcomes` MCP tool
  - Exposed PCS `ISubscriptionTriggerOutcomes.ListSubscriptionOutcomesAsync` via `IMaestroApiClient`
  - Added `MaestroService.GetSubscriptionOutcomesAsync` with ShortTtl caching
  - MCP tool filters: `subscriptionId`, `buildId`, `outcomeType`, `after`/`before` dates, `count` (default 20, max 100)
  - Markdown output with emoji indicators: ✅ Updated, ❌ Failure, 🔀 HasConflict, ⚠️ UserError, etc.
  - Added unit test; Tests: 209/209 passed ✅
- **Step C:** Integrated latest outcome into `maestro_subscription_health`
  - Added `LatestOutcomeType` and `LatestOutcomeMessage` fields to `SubscriptionHealthResult`
  - For stale subscriptions, fetch latest outcome (limit: 1) from PCS outcomes API
  - Surface in formatted output with emoji + type + message
  - Gracefully handle 404 for subs with zero outcomes (non-error stderr log)
  - Added TODO comment near oscillation detection for future replacement consideration
  - Existing heuristics (oscillation, trackedPr, validation) preserved

**PCS API gotchas discovered:**
- `limit` parameter is **required and positional** (first parameter), not optional
- `subscriptionId` is **`string`**, not `Guid` — must call `.ToString()` on Guid
- `subscriptionOutcomeType` is **`string`**, not `OutcomeType` enum — pass enum name as string
- Parameter order is alphabetical-ish after `limit`; use named arguments for safety
- `LatestOutcome` property is **NOT on `Subscription`** — it's on `CodeflowSubscriptionStatus` / `CodeflowStatus` (not used in this PR)
- Property accessor: `_api.SubscriptionTriggerOutcomes.ListSubscriptionOutcomesAsync(...)` (confirmed via `strings | grep get_SubscriptionTriggerOutcomes`)

**Patterns:**
- **Enum-to-emoji surfacing in MCP markdown:** Used pattern matching on enum `.ToString()` to map outcome types to emoji indicators, making categorized statuses scannable in agent output. Reusable for other status/outcome enumerations.
- **Graceful API 404 handling:** Wrapped outcome fetch in try/catch with stderr log; 404 is expected for subscriptions with no trigger history. Avoids polluting health results with non-critical errors.

**Verification:**
- Build: 0 warnings, 0 errors (all 3 commits)
- Tests: 209/209 passed (1 new test for GetSubscriptionOutcomesAsync)
- Branch pushed to origin: `feat/subscription-outcomes`


## PR #31 Review Fixes (2026-06-15)

**Context**: Copilot pull-request-reviewer flagged 4 valid issues after initial implementation.

**Fixed Issues**:

1. **Count validation missing**: Tool description advertised 1–100 range but accepted any int. Added explicit validation in `GetSubscriptionOutcomes` MCP tool:
   ```csharp
   if (count < 1 || count > 100)
       return $"Invalid count '{count}'. Expected a value between 1 and 100.";
   ```

2. **Service bounds + 404 handling**: `GetSubscriptionOutcomesAsync` needed defensive clamping and graceful 404 handling for subs with zero outcomes:
   - Clamp `count` to 20 if null/<=0 (don't enforce tool bounds in service layer, just default sanely)
   - Wrap PCS call in `try/catch` for `RestApiException` with 404 status → return empty list instead of throwing

3. **Duplicated API wiring**: `CheckSubscriptionHealthAsync` was calling `_api.SubscriptionTriggerOutcomes.ListSubscriptionOutcomesAsync(...)` directly. Replaced with `GetSubscriptionOutcomesAsync(subscriptionId: sub.Id, count: 1, noCache, cancellationToken)` to benefit from centralized caching and 404 handling.

4. **🔴 Real bug — outcome data never rendered**: `LatestOutcomeType`/`LatestOutcomeMessage` were gathered in `SubscriptionHealthResult` but the formatters in `MaestroMcpTools.Subscriptions.cs` never referenced them.
   - Extracted `GetOutcomeEmoji(string outcomeType)` static helper to share emoji mapping between `maestro_subscription_outcomes` tool and health formatters
   - Updated **detailed formatter**: renders `Latest outcome: {emoji} {type} — {message}` inline with staleness message for stale subs with outcome data
   - Updated **compact formatter**: includes outcome emoji+type in the status line after last applied date

**Test Impact**: Added global mock for `ListSubscriptionOutcomesAsync` in test constructor (returns empty list) to handle new service layer call path. All 209 tests passed after fixes.

**Pattern Learned**: Always validate MCP tool formatters actually render new data fields by checking formatted output, not just that the service layer gathers them. This was a silent no-op until the reviewer caught it.

## Second PR Review Fixes (2026-06-24)

**Context**: Second review pass identified 5 refinement issues with the initial PR review fixes.

**Fixed Issues**:

1. **Service count upper bound + culture-stable cache keys**:
   - Added `if (limit > 100) limit = 100;` to cap count at 100 (tool validates 1-100, but service should be defensively robust)
   - Replaced `after` and `before` in cache key from `.ToString()` (culture-dependent) to `.ToString("O", CultureInfo.InvariantCulture)` for round-trip ISO 8601 format
   - Cache keys now stable across cultures and unambiguous for date parsing

2. **🔴 Brittle 404 detection** (real bug):
   - Replaced `catch (Exception ex) when (ex.GetType().Name == "RestApiException" && ex.Message.Contains("404"))` with typed catch:
     ```csharp
     catch (Microsoft.DotNet.ProductConstructionService.Client.RestApiException ex) when (ex.Response.Status == 404)
     ```
   - Direct property access is immune to typos, localization, and message format changes

3. **Named arguments in tool service call**:
   - Replaced `GetSubscriptionOutcomesAsync(parsedSubId, buildId, parsedAfter, parsedBefore, outcomeType, maxCount, noCache, cancellationToken)` with named arguments for clarity and maintainability

4. **Test comment misleading**:
   - Rephrased from "Mock handles 404 gracefully" (implied the mock was doing error handling) to "Default outcomes mock returns empty so tests not focused on outcomes don't need per-test setup" (accurate: it's just a default to reduce boilerplate)

5. **Duplicate mock setup in test helper**:
   - Removed redundant `ListSubscriptionOutcomesAsync` mock from `SetupStaleGitHubSubscription` helper — the constructor default applies globally, no need to repeat

**Verification**:
- Build: 0 warnings, 0 errors
- Tests: 209/209 passed ✅
- `git diff origin/master...HEAD -- global.json`: empty (confirmed no SDK workaround leaked)

**Pattern Learned**: Always check `git diff origin/master...HEAD -- <workaround-files>` before pushing to catch accidental commits of transient changes.

### 2026-06-24: MCP UX hardening patterns from helix.mcp

Adopted three UX hardening patterns from helix.mcp into maestro.mcp as a single coherent PR on branch `feat/mcp-ux-hardening`.

**User-Agent identifier (helix.mcp PR #73)**
- Created `MaestroToolUserAgent.cs` with version-aware UA string (`maestro.mcp/{version}`) and custom `X-Maestro-Mcp-Tool` header
- Applied to AzDoApiClient and GitHubApiClient static HttpClients via `MaestroToolUserAgent.Apply(client)`
- Initialized version from assembly metadata in both `MaestroTool.Mcp/Program.cs` and `MaestroTool/Program.cs` entry points
- PCS client: skipped (no easy policy hook like helix's HelixApiOptions.AddPolicy)
- Tests: HttpClientConfigurationTests covering UA application and deduplication

**Strict unknown-parameter rejection + did-you-mean (helix.mcp PRs #83 + #84)**
- Stage A: `JsonUnmappedMemberHandling.Disallow` passed to `WithToolsFromAssembly` via JsonSerializerOptions
  - Rejects unknown params at binding time with ArgumentException(paramName:"arguments")
- Stage B: `McpServerOptionsExtensions.AddUnknownParameterFilter`
  - Pre-SDK dispatch: inspects incoming arguments against tool schema (built once at startup via McpServerTool.Create + InputSchema introspection)
  - Suggests closest match (Levenshtein threshold: 6) with "Did you mean: X?" message
  - Full allowed-params list always shown for discoverability
- Filter pipeline: AddBindingErrorFilter → AddUnknownParameterFilter → SDK dispatch
- Wired in both Program.cs files, tests cover Levenshtein distance, schema extraction, and end-to-end filter behavior

**Progress notifications on slow tools (helix.mcp PR #48)**
- Created `ProgressUpdate.cs`: transport-agnostic progress record (Current, Total, Message)
- Created `ProgressReporter.cs`: ItemStep helper for coarse-grained updates (~10 per operation)
- Created `McpProgressAdapter.cs`: adapts IProgress<ProgressUpdate> → IProgress<ProgressNotificationValue>
- Instrumented `maestro_subscription_health` with per-subscription progress during parallel fan-out ("Checked N of M: source → target")
- Instrumented `maestro_flow_graph` with start + completion progress ("Computing flow graph..." → "Resolving X nodes/edges...")
- MCP SDK auto-injects IProgress<ProgressNotificationValue> when client supplies progress token; adapter translates at tool boundary
- Service layer remains MCP-agnostic: `GetSubscriptionHealthAsync` accepts `IProgress<ProgressUpdate>?` parameter

**Key learnings:**
- UA setup: Apply after HttpClient creation but before auth cascade; ensure idempotency for multiple Apply() calls
- MCP CallToolFilter pattern: Build filter chain via `options.Filters.Request.CallToolFilters.Add(next => async (request, ct) => ...)`
- IProgress<T> auto-injection: MCP SDK automatically injects IProgress<ProgressNotificationValue> when method signature includes it
  - Hidden from JSON schema (not a user-facing parameter)
  - Adapter at tool boundary keeps service layer transport-agnostic
- UnmappedMemberHandling.Disallow: Must be passed to WithToolsFromAssembly, not set on McpServerOptions.JsonSerializerOptions (property doesn't exist)
- TypeInfoResolver requirement: Must set `new DefaultJsonTypeInfoResolver()` when using custom JsonSerializerOptions to avoid InvalidOperationException from SDK's MakeReadOnly() call

**Commits:**
- eb2a6fe: Add MCP User-Agent identifier for maestro.mcp
- 481cb2e: Add strict unknown-parameter rejection with did-you-mean hints
- b539076: Add progress notifications for slow MCP tools

**Tests:** 231/231 passed (up from 215 baseline)

**Verification:**
- Build: 0 warnings, 0 errors
- global.json: unchanged before all commits and before final push

### 2026-06-24: PR #34 review fixes

**Fixed 6 issues from PR #34 review:**

1. **🔴 Concurrent progress reporting bug** in `MaestroService.GetSubscriptionHealthAsync`:
   - BEFORE: Used LINQ enumeration index (`Select(async (sub, idx) => ...)`) — tasks complete out of order, progress jumps backward
   - AFTER: Use `int completed = 0` + `Interlocked.Increment(ref completed)` for thread-safe monotonic counter
   - Emit at step intervals: `if (done == total || done % step == 0) progress?.Report(...)`
   - Reduced chattiness: ~10 updates per operation via `ProgressReporter.ItemStep(total)`
   - Simplified message: `$"Checked {done} of {total} subscriptions"` (removed per-repo names)

2. **🔴 FormatRepoName can throw** on malformed/relative URIs:
   - BEFORE: `new Uri(...)` throws `UriFormatException` on `"dotnet/runtime"` or malformed strings
   - AFTER: Wrap in try/catch with `Uri.TryCreate`, return raw input on failure
   - Progress is best-effort cosmetics — must **never** fail the operation

3. **🔴 flow_graph validation order** — early return without completion update:
   - BEFORE: Emits first progress update, then validates `days` parameter → client UI stuck on validation failure
   - AFTER: Validate FIRST (`if (days is < 1 or > 30) return ...`), THEN emit progress

4. **🟡 Use AssemblyInformationalVersion** instead of AssemblyVersion:
   - BEFORE: Read `Assembly.GetName().Version` → 4-part like `"0.17.0.0"`
   - AFTER: Read `AssemblyInformationalVersionAttribute.InformationalVersion` → 3-part semver like `"0.17.0"` or `"0.17.0+abc123"`
   - Strip `+gitsha` suffix: `version = version[..version.IndexOf('+')]`
   - Fallback to AssemblyVersion if InformationalVersion not present
   - Added Initialize(Assembly) overload to simplify entry point calls

5. **🟢 Remove redundant using** in McpProgressAdapter.cs:
   - File declares `namespace MaestroTool.Core;` but also had `using MaestroTool.Core;`

6. **Add tests** for concurrent progress + FormatRepoName robustness:
   - `GetSubscriptionHealthAsync_WithProgress_ReportsMonotonicallyIncreasingProgress`: Creates 10 subscriptions, verifies progress never decreases
   - `FormatRepoName_HandlesVariousInputs`: Theory test with 6 cases (null, empty, relative path, malformed URI, single segment, full URL)
   - Updated UA test to verify 3-part version format (no 4th zero)

**Commit:** 0a3d295  
**Tests:** 240/240 passed (up from 231 baseline)  
**Verification:** `git diff origin/master...HEAD -- global.json` empty ✅

**Key learning:**
- **NEVER use LINQ index for progress in parallel Task.WhenAll** — tasks complete out of order
- **Always validate BEFORE emitting first progress** — prevents stuck UI on validation failures
- **Read InformationalVersion, strip +gitsha** — semver > 4-part version for UA strings
- **Progress formatting must be exception-safe** — wrap URI parsing in try/catch
