# Maestro CLI Skill

**Confidence:** medium (established pattern)  
**Source:** earned

## Pattern

Use the `mstro` CLI tool via bash instead of loading the full MCP tool set when:
- Working in a CLI/bash-heavy workflow where shelling out is natural
- The agent needs structured JSON output for downstream processing (all query commands support `--json`)
- You want to leverage the shared cache (`~/.mstro/cache.db`) without running an MCP server
- The task involves chaining multiple maestro operations in a script

The CLI and MCP server are the same binary with different entry points. Both share:
- SQLite cache with WAL mode for cross-process sharing
- 3-tier authentication cascade (env var → cached Entra ID → anonymous)
- Same business logic and data models

## When to Use

**Prefer CLI (`mstro` via bash) when:**
- You need JSON output for parsing/filtering with jq, grep, or other CLI tools
- You're already using bash for other parts of the workflow (git, curl, etc.)
- You want to warm the cache for future MCP server usage
- The task is one-shot or scripted (not part of a conversational flow)

**Prefer MCP tools when:**
- The agent is already in a conversational context with the MCP server loaded
- You need Markdown-formatted output with emojis and visual indicators
- The task is part of a multi-step investigation requiring multiple tool calls
- You want to minimize process spawn overhead (MCP server is long-running)

## Examples

### Example 1: Check Subscription Health in a Script
```bash
# Get stale subscriptions for the VMR
STALE_COUNT=$(mstro subscription-health --target-repository https://github.com/dotnet/dotnet --json | jq '.StaleSubs | length')

if [ "$STALE_COUNT" -gt 0 ]; then
  echo "Found $STALE_COUNT stale subscriptions"
  mstro subscription-health --target-repository https://github.com/dotnet/dotnet --json | jq -r '.StaleSubs[] | "\(.SourceRepository) -> \(.TargetRepository) (\(.BuildsBehind) builds behind)"'
fi
```

### Example 2: Trace Build Flow with JSON Pipeline
```bash
# Find latest runtime build on .NET 10.0.1xx SDK channel
BUILD_ID=$(mstro latest-build https://github.com/dotnet/runtime --channel-name ".NET 10.0.1xx SDK" --json | jq -r '.Id')

# Get build graph and extract all dependencies
mstro build-graph $BUILD_ID --json | jq '.Dependencies[] | {repo: .SourceRepository, buildId: .Id, commit: .Commit}'
```

### Example 3: Warm Cache for MCP Server
```bash
# Pre-fetch commonly used data before starting an MCP session
mstro channels --json > /dev/null
mstro subscription-health --target-repository https://github.com/dotnet/dotnet --json > /dev/null
mstro codeflow-statuses --json > /dev/null

# Now launch MCP server (or let it be launched by the client)
# The cache is already warm, so initial queries will be fast
```

## Implementation Notes

- All CLI commands support `--json` flag except `mcp`, `cache`, `trigger-subscription`, and `trigger-daily-update`
- The `--no-cache` flag bypasses the cache for any command (useful after triggering actions)
- Error handling: CLI exits with code 1 and writes to stderr on errors
- The cache is shared at `~/.mstro/cache.db` (SQLite WAL mode) — CLI and MCP server instances share the same cache
- Authentication is the same as MCP server mode (3-tier cascade)

## Portability

This pattern is portable to other MCP servers that also provide CLI interfaces:
- `lewing/helix.mcp` (future) — similar CLI-as-skill pattern for Helix/AzDO data
- Any MCP server that provides both stdio server mode and CLI commands

The key is that the CLI and MCP server share:
1. Same codebase/binary
2. Same authentication mechanism
3. Same cache layer
4. Same business logic

This ensures parity between CLI and MCP tool outputs while allowing agents to choose the most appropriate interface for the task.

## Related Skills

- **flow-analysis** — Uses maestro MCP tools for codeflow health checks (prefers MCP)
- **flow-tracing** — Uses maestro MCP tools for dependency tracing (prefers MCP)
- **vmr-codeflow-status** — Uses PowerShell scripts + maestro MCP tools (hybrid approach)

When these skills need maestro data, they can use either CLI or MCP tools depending on context. The CLI approach is preferred when:
- The skill is already shelling out for other tools (git, gh, darc, etc.)
- The skill needs to parse structured JSON for complex filtering/aggregation
- The skill wants to guarantee fresh data (`--no-cache` flag)
