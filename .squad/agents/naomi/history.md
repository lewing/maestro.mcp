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

## Learnings

### Reflection-Based CLI Schema Output (2026-03-13)

**Pattern established:**
- Query commands that already expose `--json` now also expose `--schema`, implemented with `TryPrintSchema<T>(schema)` as the first line in each handler.
- `SchemaGenerator` lives in `src/MaestroTool.Core/CliSchema/SchemaGenerator.cs` and reflects public instance properties into a PascalCase JSON skeleton, so PCS model types do not need curated hand-maintained contracts.
- Placeholder rules are centralized: strings → `<string>`, numerics → `0`, booleans → `false`, date/time values → `<datetime>`, enums → `<Value1|Value2|...>`, nullable types unwrap to their underlying placeholder, collections emit a single sample element, and dictionaries emit a single `<key>` entry.
- Use `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` for schema serialization so placeholders render as `<string>` instead of escaped unicode sequences.
- Recursion is guarded with both a per-path visited-type set and a max depth of 5; depth/cycle cutoffs emit `<circular>`.
- `--schema` wins over `--json` and returns before any command-specific Maestro lookups, so `--no-cache` is effectively irrelevant for schema generation.

### CLI-as-Skill Pattern (2026-03-13)

**Pattern established:**
- Ship lightweight skill file (`copilot-skill.md`) in NuGet package for agent discovery
- Create Squad skill file (`SKILL.md`) documenting when to use CLI vs MCP tools
- Add `guide` command to CLI that outputs workflow-organized markdown (not just command list)
- Guide content is a single string constant (~5KB) organized by scenario, not by command

**copilot-skill.md design:**
- ~100 lines, covers: what/when to use, install, quick discovery, 5-6 common workflows, JSON output note, cache note
- Quick start examples use most common commands: subscription-health, latest-build, codeflow-statuses, build + build-graph
- All examples include `--json` flag to teach structured output pattern
- Mentions shared cache (`~/.mstro/cache.db`) and warm cache benefit for both CLI and MCP

**SKILL.md design:**
- Squad skill format: Pattern, When to Use, Examples, Implementation Notes, Portability
- Documents preference rules: CLI when need JSON/bash pipeline, MCP when conversational/long-running
- 3 concrete examples: bash script with jq, JSON pipeline, cache warming
- Notes portability to lewing/helix.mcp and other dual-mode (CLI + MCP) tools

**guide command design:**
- Outputs single markdown document to stdout (not interactive)
- Organized by **workflows** (Investigating Subscription Health, Tracing Build Flow, etc.) not commands
- Each workflow section: numbered steps with command + explanation, followed by bash example
- Quick Reference table at top lists all commands with one-line descriptions
- Notes section at bottom covers JSON output, cache, auth, install

**Why workflow organization matters:**
- Teaches agents HOW to accomplish tasks, not just what commands exist
- Agent can search guide for "subscription health" and find complete workflow
- Examples show command chaining patterns (pipe to jq, capture output to variable)
- More useful than `--help` which only lists commands

**Portability to helix.mcp:**
- Same pattern: ship `copilot-skill.md`, create `SKILL.md`, add `guide` command
- Same structure: workflow-organized guide, not command-organized
- Same format: all examples use `--json` for structured output
- Key difference: helix workflows will be different (test failures, CI analysis, work items)

### CLI Help Text and ConsoleAppFramework (2026-03-13)

**CLI-as-Skill Pattern:**
- Enhanced all CLI command descriptions to mirror MCP tool descriptions for agent discoverability
- Pattern enables agents to use `mstro` CLI instead of MCP tools with equivalent information density
- Cross-references between commands help agents navigate (e.g., "use subscription-health for batch checks")

**ConsoleAppFramework 5.x limitations:**
- No command grouping/category support (commands are flat list in `--help`)
- Parameter descriptions not shown in help output (only types and defaults)
- Auto-generates kebab-case option names from C# parameter names (`sourceRepository` → `--source-repository`)
- Command-level `[Description]` attribute is the primary discoverability mechanism

**Mapping MCP to CLI:**
- All 20 MCP tools now have corresponding CLI commands
- Added missing commands: `channel` (singular) and `builds` to achieve parity
- CLI cross-references use kebab-case command names, not MCP tool names
- Command descriptions include: purpose, cross-refs, defaults, and auth requirements

**Portability insights:**
- Pattern uses only framework-provided attributes (no custom code)
- Portable to other CLI tools (e.g., helix.mcp) without maestro-specific dependencies
- Could be enhanced with code generation (auto-generate CLI from `[McpServerTool]` attributes)

### Partial Class File Organization (2026-03-13)

**Structure decisions:**
- Used `partial class` to split large monolithic file while maintaining single logical class
- Organized by **user-facing domain concepts** (Channels, Subscriptions, Builds, Codeflow, Utilities) rather than backend APIs
- Helper methods stay in the partial file where they're used (FormatBuild, FormatFlowStatus in Codeflow)
- Main file keeps only class declaration, constructor, fields, and shared helpers (Timestamp)

**File extraction gotchas:**
- Each partial file needs its own complete `using` statements - cannot rely on main file
- Must include PCS client imports: `Microsoft.DotNet.ProductConstructionService.Client` and `.Models`
- Domain-specific helpers (like FormatBuild) can live in their respective partial files
- Easy to miss closing brace when extracting - each partial file needs proper class closure

**Benefits realized:**
- 902-line file → largest partial is 339 lines, most under 200
- Clear separation for code review (e.g., "review subscription tools" targets one file)
- Folder structure mirrors API client architecture (Maestro, GitHub, AzDO)
- Git history preserved through `git mv`
- Zero breaking changes - same public surface, same DI registration

**Known issues & constraints:**
- SQLite tests fail on object identity checks (Assert.Same) due to JSON deserialization — value equality works, production unaffected
- PcsApiFactory overloads are confusing; all three auth paths need explicit baseUri
- Cache migration forced JSON round-trip, breaking reference equality in some test assertions
- Tool descriptions now subject to token-counting in agent routing — conciseness matters

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
