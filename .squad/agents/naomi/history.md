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

## Key Patterns & Learnings (Summarized)

**CLI-as-Skill (2026-03-13):** Lightweight skill file (`copilot-skill.md`) in NuGet, Squad skill file documenting CLI vs MCP preferences, `guide` command with workflow-organized markdown. Portable pattern for other dual-mode tools (helix.mcp, etc.).

**Reflection-Based Schema Generation (2026-03-13):** `SchemaGenerator.cs` reflects return types into JSON skeletons with placeholders (`<string>`, `0`, `<datetime>`, etc.). `--schema` flag on all query commands, cycles guarded at depth 5, uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`.

**Partial Class Organization (2026-03-13):** Split 902-line file by domain (Channels, Subscriptions, Builds, Codeflow, Utilities). Each file self-contained with complete using statements. Benefits: clear review targets, folder structure mirrors API clients, zero breaking changes.

**Known constraints:** SQLite tests fail on reference equality (JSON deserialization); value equality works in production. PcsApiFactory requires explicit `baseUri`. Tool descriptions token-counted in agent routing—conciseness matters.

### Archive: Detailed Learning Sessions

*Full session notes on CLI help text, schema architecture, partial class extraction gotchas, ConsoleAppFramework limits, and portability patterns archived 2026-05-08 for size management. See git history (commits: restructure series, schema-implementation) for original detailed context.*

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

### MCP SDK Upgrade Execution (2026-05-08)

**Applied upgrade:**
- ModelContextProtocol v1.0.0 → v1.3.0 (all 3 projects)
- ModelContextProtocol.AspNetCore v1.0.0 → v1.3.0 (MaestroTool.Mcp)

**Results:**
- Build: Clean — 0 warnings, 0 errors (6.19s Release build)
- Tests: 179/179 passed (21.25s) — test count increased from 167 to 179 since last verification
- No new obsolete-API warnings from v1.2.0 changes (we don't use `RequestContext` 2-arg constructor or EnableLegacySse)
- No code changes required — upgrade was transparent

**Verification notes:**
- No breaking changes affecting our stdio/HTTP transport usage
- No impact on `[McpServerTool]` attribute-based tool definitions
- Legacy SSE deprecation (v1.2.0) doesn't affect us — we use Streamable HTTP transport
- Test suite expanded naturally (new cache tests added in prior work)
