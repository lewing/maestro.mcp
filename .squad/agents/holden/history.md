# Holden — History

## Core Context

**Role:** Lead/Architect on maestro.mcp. Conducts system design reviews, audits, and architectural guidance for MCP tooling.

**Recent deliverables (2026-03-12):**
- Completed comprehensive audit of 19 MCP tools for description bloat, missing cross-refs, multi-step friction
- Audit findings: 8 tools had "Returns X,Y,Z" bloat (P0), 2 gaps in multi-step workflows (P1-M1, P1-M3), channel ID asymmetry (P1-M3), missing cross-refs (P1-M4)
- Reviewed MCP Server Design skill against maestro.mcp implementation; identified critical gaps (caching patterns, auth cascade, error handling, real anti-patterns)
- Validated tool surface against flow-analysis (302 lines) and flow-tracing (147 lines) agent skills
- Confirmed core design is solid: composite tools excellent, parameter examples effective, naming conventions work

**Key architectural insights:**
- **Two-tier tool design**: Compact descriptions (routing signals) + on-demand knowledge tools (helix_ci_guide pattern)
- **Composite tools preferred**: maestro_subscription_health and maestro_codeflow_statuses match agent mental models; don't break into primitives
- **Parameter design matters**: Standard params (noCache), format examples in descriptions, cross-parameter relationships
- **MCP skill gaps**: Needs operational patterns (caching TTLs, auth cascade, error handling, real anti-patterns from production experience)

**Recent deliverables (2026-03-13):**
- Restructuring plan (holden-restructure-plan.md) executed successfully by Naomi
- Option A (partial classes + subfolders) selected and implemented — zero breaking changes
- Core structure now: 6 partial class files organized by domain (Channels, Subscriptions, Builds, Codeflow, Utilities)
- API clients moved to domain folders (Maestro/, GitHub/, AzDO/) with mirrored test structure
- All 167 tests passing after restructure (commit pending)

**Known issues identified but deferred:**
- P0 (description cleanup) — implemented by Naomi
- P1-M1 (smart trigger) — implemented by Naomi
- P1-M3 (channel resolution) — implemented by Naomi
- P1-M4 (cross-refs) — implemented by Naomi
- P2 items (flow_graph docs, auth notes) — not yet implemented

**Files audited:**
- src/MaestroTool.Core/MaestroMcpTools.cs — 19 tools, descriptions, parameter design

---

## Archive: Earlier Sessions

*Earlier detailed audit findings and skill review entries archived 2026-03-12. Original content preserved in git history and decisions.md.*

## Learnings

- 2026-03-13 restructure review: approved Naomi's Option A implementation on `squad/restructure-core-partials`.
- Verified the tool surface stayed intact across the split: all 20 MCP tools moved exactly once into domain partials with the expected 3/5/5/6/1 breakdown for Channels, Subscriptions, Builds, Codeflow, and Utilities.
- Main `MaestroMcpTools.cs` correctly retained the `[McpServerToolType]` declaration, constructor, shared fields, and `Timestamp` helper while the partial files kept `MaestroTool.Core` namespace and independent using sets.
- File moves were clean `git mv` renames for API clients and matching tests: `AzDO/`, `GitHub/`, `Maestro/`, and `MaestroMcpTools/` now mirror the intended structure without namespace churn.
- Future restructure pattern: validate with a tool-name set diff against the pre-split file, then confirm `dotnet test MaestroTool.slnx --no-restore` to catch missing usings, dropped methods, or accidental duplication fast.

### 2026-03-18: Context Tax Analysis & Progressive Disclosure Patterns

**Context tax quantification:**
- Current MCP tool surface: 20 tools, 74 description attributes, 5,241 chars → **~1,310 tokens**
- Every agent pays this upfront, even for simple queries using 1-3 tools
- Multi-client deployments (VS Code + CLI + Claude) replicate this cost 3×

**Progressive disclosure patterns:**
- **Pattern 1 (Help command):** Skill → `mstro --help` → command-specific help. Token cost: 50 initial + 500 help = 650 total (50% reduction)
- **Pattern 2 (MCP resource):** Skill → `maestro://guide` resource with markdown guide. Token cost: 50 initial + 200 guide = 250 total (81% reduction)
- **Key insight:** Defer 95% of documentation cost until actually needed. Most agents only use 1-3 tools, not all 20.

**CLI vs MCP tradeoff analysis:**
- ✅ CLI advantages: No persistent connection, lower latency, easier debugging, shared cache, `--json` for structured output
- ❌ CLI disadvantages: Process overhead (~200ms), no streaming (but not needed for <10KB responses)
- **Verdict:** CLI disadvantages are negligible for maestro.mcp use case

**Recommended architecture (Option C - Hybrid):**
1. Add `maestro://guide` MCP resource (2-3KB markdown) for progressive disclosure
2. Publish Copilot skill that routes to CLI + resource (50 token entry point)
3. Keep MCP tools unchanged (backward compatible)
4. Let clients choose: MCP tools (1,310 tokens) vs skill+resource (250 tokens)

**Comparison to helix.mcp approaches:**
- Helix knowledge tool (`helix_ci_guide`): Good discovery, but still counts against context tax
- Helix resource experiment: Semantic search over `helix://knowledgebase`. Complex implementation, debugging harder.
- Maestro hybrid: Simpler (static markdown), cheaper (resource on-demand), more flexible (CLI is standalone)

**Implementation cost:** ~4 hours (resource handler + skill + testing). Zero breaking changes.

**Decision documented:** Merged to `.ai-team/decisions.md` (2026-03-13) — Skill-Based Architecture decision approved for implementation. Hybrid approach (Option C) recommended: resource handler + CLI skill + backward-compatible MCP tools.

### 2026-03-13: `--schema` CLI contract architecture

- `mstro` query commands in `src/MaestroTool/Program.cs` already share a strong output pattern: per-command `--json`/`--no-cache` flags and a single `JsonSerializerOptions` instance with default STJ naming, so schema output must preserve the current PascalCase field names exactly.
- `src/MaestroTool.Core/Maestro/MaestroService.cs` already contains the best agent-facing JSON contracts in the codebase (`SubscriptionHealthResult`, `BuildFreshnessResult`, plus nested validation/oscillation/PR records); those can be used directly for schema generation.
- Architecture decision: implement `--schema` as a per-command flag on every query command, not as a meta-command. It belongs beside `--json` in the CLI discovery flow (`--help` → `--schema` → `--json`).
- Architecture decision: schema format should be a minified JSON skeleton with typed placeholders and the same root shape as the live payload. Schema is a contract mirror, not prose and not full JSON Schema.
- Architecture decision: put schema generation in `MaestroTool.Core`, driven by explicit CLI response contracts. For raw PCS client types like `Build`, `Subscription`, and `Channel`, prefer curated CLI contract types over exposing the full generated BAR object graph.
- New design doc: `.ai-team/agents/holden/schema-architecture.md` captures the recommendations, rationale, rollout phases, and testing expectations for issue #12.

**Recent deliverables (2026-03-13, Session 3 - Schema Architecture):**
- Designed `mstro --schema` as intentional contract feature, not docs dump
- Architecture decision: schema generation uses CLI contract types in Core, not raw PCS client models
- Consolidates schema logic in single `SchemaGenerator.cs` file
- Supports all 17 query commands with consistent field naming (PascalCase)
- Key insight: curated contract types for noisy PCS commands allow agent-friendly field filtering without bloating the schema
- Naomi implemented per architecture; all 179 tests passing

**Related decision:** Reflection-based CLI Schema Output (naomi-schema-implementation.md) — implementation details and file changes

📌 Team update (2026-05-08): MCP SDK v1.3.0 upgrade approved — Naomi comprehensive review confirms no breaking changes, clean upgrade path. 3 .csproj files need version bump (ModelContextProtocol v1.0.0 → v1.3.0). Reliability wins include DI scope fixes, memory leak prevention, improved diagnostics.


## 2026-05-08: SDK Version Baseline Shifted

Naomi completed upgrade of ModelContextProtocol from v1.0.0 → v1.3.0. Build clean (0 warnings), all 179 tests pass. SDK version baseline is now v1.3.0 across all projects. See decisions.md for upgrade details and benefits.
