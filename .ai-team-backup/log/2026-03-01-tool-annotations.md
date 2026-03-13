# 2026-03-01: MCP Tool Annotations Applied

**Requested by:** Larry Ewing

## Summary

Naomi applied MCP SDK 1.0 tool annotations to all 19 tools in `MaestroMcpTools.cs`.

### Changes
- **16 read-only tools** marked with `ReadOnly=true`
  - Includes: subscriptions, channels, builds, health metrics, codeflow PRs, backflow status, and graph queries
- **1 destructive tool** marked with `Destructive=true`
  - `maestro_clear_cache`: Permanently wipes local SQLite cache
- **2 trigger tools** left at default values
  - `maestro_trigger_subscription`: Triggers Maestro subscription update
  - `maestro_trigger_daily_update`: Triggers daily update workflow
  - These retain default values to indicate side effects without destruction

### Verification
- ✅ Build passed (12.5s)
- ✅ All 135 tests passed
- ✅ Committed as `834b9d5`

### Rationale
The classification enables MCP clients to:
1. Auto-approve safe, read-only queries (16/19 tools)
2. Prompt for confirmation on trigger actions (2 tools with side effects)
3. Require explicit confirmation for destructive operations (1 tool)

This follows the MCP SDK 1.0 best practices for tool metadata signaling.
