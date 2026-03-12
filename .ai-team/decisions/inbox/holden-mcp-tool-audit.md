# Decision: Maestro MCP Tool Audit Findings

**Author:** Holden (Lead)  
**Date:** 2026-02-20  
**Status:** Audit Complete — Implementation TBD

## Context

Larry requested a thorough audit of `src/MaestroTool.Core/MaestroMcpTools.cs` to ensure the tool surface is agent-optimized, not API-mirrored. The audit cross-referenced all 19 tools against flow-analysis and flow-tracing skill files (449 lines combined) to validate against actual agent workflows.

## Audit Scope

1. **Tool descriptions** — Are they tight? Do they list return schemas agents already see? Are they vague or confusable? Do they cross-reference related tools?
2. **API abstraction** — Are we exposing raw API internals (IDs without context)? Could we provide higher-level abstractions? Do parameters need descriptions?
3. **Agent workflows** — What do agents actually want? Are there gaps where 3+ calls could be 1?

## Key Findings

### 🔴 HIGH — Description Bloat (8 tools)

8 of 19 tools list return fields in descriptions ("Returns subscription ID, source/target repo..."). Agents see the actual response — schema docs are waste. **Fix: Remove "Returns X, Y, Z" from all descriptions.**

Affected tools: `maestro_subscriptions`, `maestro_latest_build`, `maestro_build`, `maestro_builds`, `maestro_channel`, `maestro_channels`, `maestro_codeflow_prs`, `maestro_tracked_pr`.

### 🟡 MEDIUM — Multi-Step Friction (2 gaps)

1. **Triggering requires 3 steps**: Agents call `maestro_latest_build` → parse markdown → extract build ID → call `maestro_trigger_subscription`. **Fix: Add optional `sourceRepository`/`channelName` parameters to trigger tool to resolve latest build internally.**

2. **Subscription discovery is awkward**: Agents call `maestro_subscriptions` to list all, then grep for the one they want. **Fix: Add `maestro_find_subscription(source, target, channel, branch)` tool for direct lookup.**

### 🟡 MEDIUM — Channel ID Asymmetry

`maestro_channel` requires an integer ID, but agents only have names. This forces a "list all → grep → extract ID" workflow. **Fix: Change parameter to `string channelNameOrId` and resolve internally** (pattern already used in other tools).

### 🟡 MEDIUM — Missing Cross-References

Three subscription tools don't guide agents on when to use which: `maestro_subscriptions` (discovery), `maestro_subscription` (single detail), `maestro_subscription_health` (batch check). **Fix: Add "Use X for..." cross-references to descriptions.**

### 🟢 LOW — Parameter Description Gaps

`maestro_flow_graph` has 5 parameters (4 booleans) with unclear impact. **Fix: Explain what "flow graph" means and when to toggle flags** (e.g., "includeArcade=false hides tooling noise").

### ✅ POSITIVE — What's Working

- **Composite tools are excellent**: `maestro_subscription_health` and `maestro_codeflow_statuses` match agent mental models perfectly. Flow-analysis skill shows these are first-step tools for "why is X stuck?" questions. DO NOT break into smaller primitives.
- **Parameter examples are effective**: Almost every parameter includes examples (e.g., "e.g. https://github.com/dotnet/runtime"). Agents use these directly.
- **noCache is consistent**: All 17 read-only tools accept it; agents understand when to use it.
- **Naming conventions work**: Verbs for actions, nouns for queries. Agents learn the pattern without docs.

## Agent Workflow Validation

Validated against flow-analysis (302 lines) and flow-tracing (147 lines) skills:

✅ **Codeflow overview** — covered by `maestro_subscription_health`, `maestro_codeflow_prs`, `maestro_build_freshness`  
✅ **PR analysis** — skill uses PowerShell for GitHub data, Maestro tools for enrichment  
✅ **Flow health** — covered by composite tools + batch checks  
⚠️ **Remediation** — covered but friction (3-step trigger workflow)  
⚠️ **Subscription discovery** — gap (no direct "find subscription for A→B" tool)

## Recommendations

### P0 (Must Do)
1. Remove "Returns X, Y, Z" from 8 tool descriptions

### P1 (Should Do)
2. Add optional `sourceRepository`/`channelName` to `maestro_trigger_subscription`
3. Add `maestro_find_subscription` tool OR document workaround in cross-references
4. Add cross-references to overlapping subscription tools

### P2 (Nice to Have)
5. Improve `maestro_flow_graph` parameter descriptions
6. Add auth requirement note to write operations
7. Clarify cache scope in `maestro_clear_cache`

### Do NOT Change
8. Keep composite tools (`maestro_subscription_health`, `maestro_codeflow_statuses`)
9. Keep `noCache` parameter consistent
10. Keep parameter descriptions with examples

## Decision

**Audit complete.** Findings documented for Larry to prioritize. The tool surface is fundamentally well-designed — most issues are polish (description bloat, missing cross-refs) or optional improvements (composite trigger, subscription finder). The core abstraction level is correct.

**Next steps**: Larry to decide:
- P0 (description cleanup) — 1 hour, high ROI
- P1 (composite trigger + finder) — 4-6 hours, medium ROI
- P2 (parameter docs) — 2 hours, low ROI
