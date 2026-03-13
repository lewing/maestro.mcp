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
