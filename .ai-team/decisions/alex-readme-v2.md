# Decision: README.md v2 — Copilot CLI config + Action Tools documentation

**Author:** Alex (DevOps / Infrastructure)  
**Date:** 2025-07-15  
**Status:** Complete

## Context

Naomi is building action tools (`maestro_trigger_subscription`, `maestro_trigger_daily_update`) that enable the MCP server to trigger subscription processing (non-destructive operations). The README needed updates to:

1. Reflect GitHub Copilot CLI as the primary user client (not an afterthought)
2. Document the new action tools with parameters
3. Explain deduplication logic that prevents duplicate LLM retries
4. Sketch the pattern for future destructive actions (opt-in via env var)

## Decision

Made three surgical changes to `README.md`:

### 1. Config file locations table — GitHub Copilot CLI first

Moved **GitHub Copilot CLI** (`~/.copilot/mcp.json`) to the first row. This signals it's the primary client for this project's users, not secondary to VS Code or Claude.

### 2. Action Tools section — New section after Authentication

Inserted a new "Action Tools" section between "Authentication" and "Available Tools" that:

- Explains action tools are **non-destructive** (trigger processing, don't delete/modify config)
- Documents built-in **deduplication with 2-minute cooldown** (prevents duplicate triggers from LLM retries or concurrent skills)
- Sketches the **opt-in pattern for future destructive actions** using `MAESTRO_ENABLE_DESTRUCTIVE_ACTIONS` env var

This placement provides essential context before the full tools reference table.

### 3. Available Tools table — Added 2 rows, updated count to 10

- `maestro_trigger_subscription` — `subscriptionId` (UUID), `buildId` (BAR build ID)
- `maestro_trigger_daily_update` — No parameters

Updated header from "8 MCP tools" to "10 MCP tools" and rephrased to "querying and triggering Maestro/BAR operations".

## Rationale

- **Client primacy**: Copilot CLI is Larry's primary user audience. Leading with it signals the project's focus.
- **Deduplication transparency**: LLM-driven clients may retry the same action. Users need to understand that duplicate triggers are safe (deduped, not re-executed).
- **Future extensibility**: Documenting the opt-in pattern for destructive actions now makes it easier to add dangerous operations later without surprising users.
- **Minimal disruption**: Only 3 changes, all surgical. No restructuring, no rewriting existing sections.

## Files Changed

- `README.md` — Config table, new Action Tools section, updated Available Tools table
