# Amos — History

## Core Context

**Role:** QA/Test engineer on maestro.mcp. Writes comprehensive tests for MCP tools and infrastructure improvements.

**Recent deliverables (2026-03-12):**
- Wrote 16 new tests for channel resolution and smart trigger features
- Tests validate channel name/ID parsing, auto-resolution of build IDs from sourceRepository + channelName
- All 167 tests passing (commit 792b4ee)
- Test coverage includes smart trigger auto-resolve workflow (3-step reduction), channel asymmetry fix

**Testing patterns & standards:**
- xUnit framework, NSubstitute for mocking
- Test organization: Unit tests in src/MaestroTool.Tests/MaestroMcpToolsTests.cs
- Focus on boundary conditions: int vs string channels, null buildId resolution
- Validation: parameter parsing, API call paths, error handling

**Known test issues:**
- Some earlier tests failed due to JSON deserialization breaking reference equality (Assert.Same) — refactored to value equality, all now pass
- SQLite cache tests need care around object identity vs equality

**Recent deliverables (2026-03-13):**
- Verified restructure plan execution: all 167 tests pass after core refactoring
- Tests remain in mirrored folder structure (MaestroMcpTools/, Maestro/, AzDO/)
- No test regressions from file moves and partial class reorganization

**Files modified:**
- src/MaestroTool.Tests/MaestroMcpToolsTests.cs — 16 new test cases for P1-M1, P1-M3 features
- src/MaestroTool.Tests/ — mirrored folder structure after restructure

---

## Archive: Earlier Sessions

*Earlier detailed test session entries archived 2026-03-12. Original content preserved in git history.*


📌 Team update (2026-03-13): Restructure review approved — The core MCP tools restructure has been approved by Holden. All tools organized into domain partials with clean separation and test validation (167/167 passing). — decided by Holden

## Learnings

**JSON Output Coverage Audit (2026-03-13):**
- **CLI has 85% JSON support:** 17/20 commands support `--json` flag using `JsonSerializer.Serialize(data, s_jsonOptions)` with `WriteIndented=true`
- **MCP tools are Markdown-only:** All 20 MCP tools return formatted Markdown strings via `StringBuilder` - NO JSON support
- **Gaps for CLI-as-skill pattern:** 2 trigger commands (`trigger-subscription`, `trigger-daily-update`) lack JSON output - agents must parse emoji-decorated text
- **Output patterns identified:**
  - CLI text: Plain `Console.WriteLine()` with arrow symbols and formatted output
  - CLI JSON: Pretty-printed JSON objects serialized directly from `MaestroService` return values
  - MCP: Markdown + emojis (✅ ⚠️ 🔒 ⚡) + timestamp prefix on every response
- **Shared infrastructure:** Both CLI and MCP use same `MaestroService` and SQLite `CacheService` - identical underlying data
- **Error handling divergence:** CLI uses `Console.Error.WriteLine()` + `Environment.Exit(1)`, MCP returns error strings gracefully
- **Recommendation:** Add `--json` support to trigger commands with structured success/error responses to enable agent-friendly parsing
- **Audit deliverable:** Comprehensive markdown report at `.ai-team/decisions/inbox/amos-json-audit.md` with 20-command inventory, gap analysis, and 6-hour implementation plan

### JSON Output Audit Complete (2026-03-13)

**Decision merged to decisions.md:**
- Comprehensive audit of all 20 CLI commands and 20 MCP tools for JSON output support
- **Finding:** 85% of CLI commands support `--json` flag (17/20); 0% of MCP tools support JSON
- **Gaps:** 2 trigger commands (`trigger-subscription`, `trigger-daily-update`) lack JSON output
- **Shared infrastructure:** Both CLI and MCP use identical `MaestroService` methods → same underlying data

**Recommendations prioritized:**
1. **P1:** Add `--json` to trigger commands with structured success/error responses
2. **P2:** Standardize JSON error format across all commands
3. **P3:** Document CLI-as-skill pattern for agents
4. **P4 (optional):** Add JSON mode to MCP tools (lower priority since agents will use CLI)

**Implementation estimate:** 6 hours total (2h P1 + 1h P2 + 3h P3)

**Testing strategy:**
- Unit tests for trigger JSON output (success and error cases)
- Integration tests with MaestroService mock responses
- Validate error format consistency across all commands

### SchemaGenerator TDD Coverage (2026-03-13)

**Test patterns established:**
- New schema tests live in `src/MaestroTool.Tests/SchemaGeneratorTests.cs` and parse generated JSON with `System.Text.Json` before asserting specific property paths.
- The tests discover `SchemaGenerator` via reflection so the test project still compiles before Naomi's implementation lands, while keeping the public API contract (`GenerateSchema<T>()` and `GenerateSchema(Type)`) explicit.
- Real DTO coverage uses `BuildFreshnessResult` and `SubscriptionHealthResult` to verify nested records, collection wrapping, enum placeholders, and PascalCase property names against production model shapes.

**Edge cases identified:**
- Self-referential types must stop recursion and emit `"<circular>"` rather than overflowing the stack.
- Root collection schemas should serialize as a one-element array skeleton, not an object with collection metadata.
- Nullable members should render the underlying placeholder shape (`0`, `"<string>"`, `"<datetime>"`, etc.) instead of `null` sentinel values.

**Recent deliverables (2026-03-13, Session 3 - Schema Testing):**
- Wrote 12 comprehensive tests for schema generation in `src/MaestroTool.Tests/SchemaGeneratorTests.cs`
- Test coverage: placeholder mappings (strings, numerics, booleans, enums, DateTime variants), cycle detection, nullable unwrapping, collections/dictionaries
- Tests validate `SchemaGenerator` walks public instance properties correctly and produces PascalCase JSON skeletons
- All 179 tests passing after merge (167 existing + 12 new schema tests)
- Build verified clean

**Testing notes:**
- Schema cycle protection validation: visited-type set + max recursion depth of 5
- Placeholder consistency: `<string>`, `0`, `false`, `<datetime>`, `<Value1|Value2|...>`, `<circular>`
- JavaScriptEncoder validation ensures placeholders remain human-readable in output

## 2026-05-08: SDK Version Baseline Shifted

Naomi completed upgrade of ModelContextProtocol from v1.0.0 → v1.3.0. Build clean (0 warnings), all 179 tests pass. SDK version baseline is now v1.3.0 across all projects. See decisions.md for upgrade details and benefits.
