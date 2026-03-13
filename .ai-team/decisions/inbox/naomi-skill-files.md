# CLI-as-Skill Files and Guide Command

**Date:** 2026-03-13  
**Author:** Naomi  
**Status:** Implemented

## Context

We're establishing a CLI-as-skill pattern where AI agents can use the `mstro` CLI tool via bash instead of loading the MCP server. This requires:
1. Lightweight documentation shipped with the NuGet package
2. Squad skill file documenting the pattern
3. Workflow-organized guide command for agent consumption

This pattern needs to be portable to `lewing/helix.mcp` later.

## Decision

### Created Three Deliverables

**1. `src/MaestroTool/copilot-skill.md` (~6KB)**
- Ships in NuGet package as discoverable documentation
- Content: what mstro does, install command, quick discovery, 5-6 common workflows, JSON output, cache notes
- All examples use `--json` flag to teach structured output pattern
- Focuses on most common use cases: subscription-health, latest-build, codeflow-statuses, build tracing

**2. `.ai-team/skills/maestro-cli/SKILL.md` (~4.5KB)**
- Squad skill documentation following standard skill format
- Sections: Pattern, When to Use, Examples, Implementation Notes, Portability
- Documents preference rules: CLI when need JSON/bash pipeline, MCP when conversational/long-running
- 3 concrete examples showing bash scripting patterns with jq, variable capture, cache warming

**3. `mstro guide` command in Program.cs (~5KB inline)**
- New CLI command that outputs workflow-organized markdown guide
- Structure: Quick Reference table → Workflows (by scenario) → Notes
- Each workflow section: numbered steps with command + explanation, followed by bash example
- Organized by **user intent** (Investigating Subscription Health, Tracing Build Flow) not by command

### Key Design Choices

**Why workflow organization in guide?**
- Teaches agents HOW to accomplish tasks, not just what commands exist
- Agent searches guide for "subscription health" and finds complete workflow with examples
- Shows command chaining patterns (pipe to jq, capture output to variable)
- More valuable than `--help` which only lists commands

**Why inline string constant?**
- Guide content is static, doesn't need external file dependencies
- Easy to maintain (single location in Program.cs)
- Always in sync with command availability
- No build-time generation complexity

**Why ship copilot-skill.md in NuGet package?**
- Agents can discover it without needing to query the repo
- Available immediately after `dotnet tool install`
- Lightweight entry point (100 lines) that points to `mstro guide` for details
- Pattern is portable to other NuGet-packaged CLI+MCP tools

## Rationale

1. **Progressive disclosure:** `copilot-skill.md` → `mstro --help` → `mstro guide` → `mstro <cmd> --help`
2. **Portability:** Pattern uses only framework features, portable to helix.mcp with different content
3. **Maintainability:** Guide content is single string constant, easy to update when commands change
4. **Discoverability:** NuGet package ships with skill file, no external docs needed

## Alternative Considered

**Generate guide from command attributes at build time:** Could extract `[Description]` attributes and build guide automatically. **Rejected** because:
- Guide needs workflow organization, not command-alphabetical
- Examples and command chaining patterns can't be auto-generated
- Inline string constant is easier to maintain for workflow-based content
- Code generation adds complexity for marginal benefit

## Implementation Notes

- Guide command is simple: no parameters, outputs string constant to stdout
- Guide content organized by workflows matching common user tasks (not by command)
- All examples include `--json` flag to reinforce structured output pattern
- Quick Reference table lists all 21 commands (20 query/action + 1 cache utility)
- copilot-skill.md focuses on top 5-6 most common workflows only

## Future Considerations

When porting this pattern to helix.mcp:
- Keep same file structure (`copilot-skill.md`, `SKILL.md`, `guide` command)
- Adapt workflow sections to helix/AzDO tasks (test failures, CI analysis, work items)
- Use same progressive disclosure pattern (skill file → --help → guide → command help)
- Consider sharing guide format template between maestro.mcp and helix.mcp

## Testing

- Build verified: `dotnet build src/MaestroTool/MaestroTool.csproj` succeeded
- Guide command tested: `dotnet run --project src/MaestroTool/MaestroTool.csproj -- guide` outputs formatted markdown
- Help listing verified: `mstro --help` shows guide command in list

## Related Decisions

- **naomi-cli-help.md** — Enhanced CLI command descriptions for MCP/CLI parity
- **amos-json-audit.md** — Documented JSON output coverage (17/20 commands support --json)
- **holden-skill-architecture.md** — Squad skill format and organization
