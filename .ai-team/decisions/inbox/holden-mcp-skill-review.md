# MCP Server Design Skill Review — Decisions & Implications

**Date:** 2026-03-11  
**Reviewer:** Holden (Lead / Architect)  
**Requestor:** Larry Ewing  
**Context:** Review of `/home/lewing/src/blazor-playground/copilot-skills/plugins/skill-trainer/skills/mcp-server-design/` against real-world maestro.mcp implementation

## Decision

The mcp-server-design skill has **solid foundational patterns but critical gaps in operational depth**. It should be enhanced with patterns learned from building maestro.mcp before being promoted as comprehensive guidance.

## Rationale

### What Works
- **Knowledge tool architecture** is genuinely valuable — the two-tier pattern (compact descriptions + on-demand knowledge endpoints) matches our best design decision and is well-articulated with `helix_ci_guide` as a concrete exemplar.
- **Tool descriptions as routing signals** is the right mental model and directly informed our description tightening work.
- **Purpose-first structure** ("lead with a verb," "don't describe return schemas") matches what we actually do across 20 tools.

### What's Missing
Critical operational patterns absent from the skill:

1. **Caching architecture** — 15-min TTLs, SQLite persistence, cache invalidation, action deduplication (2-minute cooldowns to prevent LLM retry storms)
2. **Auth patterns** — PAT → Entra ID → Anonymous cascade, when tools enforce auth vs. rely on API rejection, error message design
3. **Error handling** — Structured messages, parameter validation (`Guid.TryParse`), recovery guidance
4. **Parameter design** — Standard params (`noCache`), format examples in descriptions, cross-parameter relationships
5. **Real anti-patterns** — PCS factory crashes (null baseUri), process execution on Windows, GitHub rate limits, SQLite corruption
6. **Health check patterns** — When to create composite diagnostic tools vs. expose primitives

### Slop Detected
- agent-integration-patterns.md repeats the same point 3 times
- industry-alignment.md doesn't extract actionable implications from research
- validation-methodology.md reveals most patterns haven't been formally validated

## Implications for maestro.mcp

**Positive:** The skill validates our core design choices:
- Two-tier architecture (compact descriptions + knowledge tools)
- Purpose-first tool descriptions
- Tool family naming consistency (`maestro_*`)
- Cross-referencing related tools in descriptions

**Negative:** The skill wouldn't have prepared us for real challenges:
- Caching strategy design
- Auth cascade error handling
- Action deduplication patterns
- Composite vs. atomic tool tradeoffs

**Recommendation:** If we write MCP server guidance in the future, prioritize operational patterns over theoretical design principles. The skill has good bones but lacks the depth needed for production servers.

## Actions for Larry

If this skill is intended for external consumption:

**P0 (Critical Gaps):**
1. Add caching strategy section with TTL guidance, persistence patterns, invalidation
2. Add error handling section with structured messages, validation patterns, recovery
3. Add auth patterns section with cascade design, error message design
4. Add parameter design section with standard params, format examples, validation
5. Add real anti-patterns section derived from maestro.mcp / helix.mcp experience

**P1 (Depth):**
6. Expand tool annotations — clarify security vs. documentation distinction
7. Expand parameter descriptions — dedicated section with examples
8. Add composite tool patterns — when to create diagnostic tools vs. primitives

**P2 (Polish):**
9. Cut repetition in agent-integration-patterns.md
10. Extract actionable takeaways from industry-alignment.md research findings
11. Add before/after examples and case studies (use maestro.mcp as exemplar)

## Follow-up Questions

- Is this skill intended as comprehensive guidance or directional patterns?
- Should we contribute maestro.mcp-specific patterns back to the skill?
- Does the skill need formal validation (A/B testing) before external promotion?

## Status

Decision recorded for team awareness. No immediate action required in maestro.mcp codebase — this is guidance for the skill itself.
