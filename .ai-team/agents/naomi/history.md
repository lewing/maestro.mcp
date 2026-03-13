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
- `src/MaestroTool.Core/MaestroMcpTools.cs` — Tool surface definitions and descriptions
- `src/MaestroTool.Tests/` — Unit tests (xUnit, NSubstitute)

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
