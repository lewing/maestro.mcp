# 2026-03-13: CLI-as-Skill Architecture Session

**Requested by:** Larry Ewing

## Team Contributions

- **Holden**: Analyzed CLI-as-skill architecture for reducing MCP context tax. Key finding: skill approach reduces token cost from ~1,310 (20 MCP tools) to 50-100 (1 skill + resource), with progressive disclosure pattern deferring 95% of documentation cost until needed.

- **Naomi**: Enhanced CLI help text to mirror MCP tool descriptions. Added 2 missing commands (`channel` singular, `builds`) to achieve parity with MCP tools. ConsoleAppFramework limits: no parameter-level descriptions in help output.

- **Amos**: Audited JSON output coverage. Found 85% of CLI commands support `--json` flag (17/20). All 20 MCP tools return Markdown-only (no JSON). Recommended adding JSON to trigger commands for agent-friendly responses.

## Directives

**Pattern applicability:** CLI-as-skill pattern will be ported to lewing/helix.mcp if maestro.mcp implementation succeeds.

## Working Branch

`squad/cli-as-skill`
