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

## 2026-05-21: Assigned — Microsoft.NET.Test.Sdk 17.x → 18.5.1

**Holden review approved with conditions** (2026-05-21). Amos owns the Test.Sdk bump; separate PR from Naomi's Sqlite bump for isolated test infra validation.

**Details:**
- **Package:** Microsoft.NET.Test.Sdk 17.x → 18.5.1 (MAJOR bump)
- **Risk level:** Low — backward-compatible; our test patterns (xunit 2.x + NSubstitute 5.x + xunit.runner.visualstudio 3.x) are compatible
- **Conditions:** Pin Test.Sdk explicitly to 18.5.1 (no wildcards). Also pin xunit and NSubstitute to explicit versions (e.g., 2.9.3, 5.3.0) to avoid latent risks from wildcard pins.
- **Verification:** All 179+ tests pass, `dotnet test --logger trx` CI integration confirmed, no new analyzer warnings
- **Future:** If Central Package Management (Directory.Packages.props) is approved, these pins migrate there.

**Rationale:** Test.Sdk 18.x ships Microsoft.Testing.Platform but maintains VSTest compatibility. Wildcard pins are a risk and should be eliminated.

## 2026-05-21: Shipped — PR #18 (Test.Sdk bump + wildcard pin elimination)

**PR:** https://github.com/lewing/maestro.mcp/pull/18  
**Branch:** `squad/test-infra-pin-and-sdk-bump`  
**Test result:** 179/179 passed ✅

**Versions pinned in `MaestroTool.Tests.csproj`:**
- `Microsoft.NET.Test.Sdk` 17.* → **18.5.1** (bumped)
- `xunit` 2.* → **2.9.3** (was already resolving to this)
- `xunit.runner.visualstudio` 3.* → **3.1.5** (was already resolving to this)
- `NSubstitute` 5.* → **5.3.0** (was already resolving to this)

Holden's pin-exact-versions condition honored. No pre-release packages introduced. `dotnet test --logger trx` CI integration confirmed.

---

## 2026-05-21: Test Infrastructure — Test.Sdk Bump + Wildcard Pin Elimination

**Task:** Upgrade Microsoft.NET.Test.Sdk 17.x → 18.5.1 and pin all wildcard version specifications in test projects to explicit semantic versions.

**Deliverable:** PR #18 (`squad/test-infra-pin-and-sdk-bump`)

**Key changes:**
- Microsoft.NET.Test.Sdk 17.* → 18.5.1 (explicit pin; backward-compatible with VSTest)
- xunit 2.* → 2.9.3 (explicit pin; removes risk of silent MAJOR bump)
- xunit.runner.visualstudio 3.* → 3.1.5 (explicit pin)
- NSubstitute 5.* → 5.3.0 (explicit pin; remains stable, pre-release 6.0.0-rc.1 held)

**Rationale (Holden approval, Decision 2):**
- Test.Sdk 18.x ships Microsoft.Testing.Platform but maintains VSTest backward compatibility
- Wildcard pins are latent risk (easy to accidentally cross MAJOR boundary)
- Central Package Management (Directory.Packages.props) proposal pending — these pins will migrate there when adopted

**Verification:** All 179 tests pass; VSTest output produces valid TRX format; no new analyzer warnings from Test.Sdk 18.x bump.

**Test framework compatibility:** xunit 2.9.3 + NSubstitute 5.3.0 + VSTest 18.5.1 combination validated; no breaking changes in mainstream test patterns.

## 2026-05-21: PR #18 Merge Conflicts Resolved

**Task:** Resolve merge conflicts on PR #18 after PRs #15, #16, and #17 merged ahead.

**Conflicting files:**
- `src/MaestroTool.Core/MaestroTool.Core.csproj` (dependency version conflicts)
- `src/MaestroTool/MaestroTool.csproj` (dependency version conflicts)

**Conflict details:**
My branch (PR #18, Test.Sdk bump) had older dependency versions from the base. PRs #15 (Alex - Extensions patches), #16 (Alex - RollForward), and #17 (Naomi - Sqlite + Extensions) merged ahead with newer versions.

**Resolution strategy:**
Per Holden's policy: pin exact versions, take HIGHER stable versions. Resolved conflicts by accepting incoming versions from PRs #15 and #17:
- Microsoft.Data.Sqlite: 9.0.0 → **10.0.8** (from PR #17, Naomi's Sqlite bump)
- Microsoft.DotNet.ProductConstructionService.Client: 1.1.0-beta.26161.4 → **1.1.0-beta.26271.2** (from PR #15, Alex's PCS refresh)
- Microsoft.Extensions.DependencyInjection: 10.0.3 → **10.0.8** (from PR #15, Alex's patch bump)
- Microsoft.Extensions.Hosting: 10.0.0 → **10.0.8** (from PR #15, Alex's patch bump)

**Verification:**
- `dotnet restore` — clean
- `dotnet test` — **179/179 tests passed** ✅
- `gh pr view 18` — mergeStateStatus: **CLEAN**, mergeable: **MERGEABLE** ✅

**Outcome:** PR #18 is now conflict-free and ready to merge. All test infrastructure changes (Test.Sdk 18.5.1 + pinned xunit/NSubstitute versions) validated with latest dependency versions from team PRs.

---

## 2026-05-21: Live MCP Smoke Test Plan — Post-Reload Validation

**Context:** Larry reloaded maestro.mcp server after recent merges (PRs #15-18) and while PR #20 (subscription_health parallelization) is open. Task was to validate recent changes live and capture baseline metrics before issue #19 (flow_graph 7d→3d perf fix) lands.

**Local sanity check (2026-05-21):**
- `git pull` → Already up to date
- `dotnet test --nologo` → **179/179 tests passed** ✅
- Build status: Clean, no warnings
- Trunk is green after all recent merges

**Recent PRs merged:**
- #18 (Test.Sdk 18.5.1 + wildcard pin elimination) — MERGED 2026-05-21
- #17 (Sqlite 9→10, Extensions 10.0.8, PCS Client beta) — MERGED 2026-05-21
- #16 (RollForward Major on MaestroTool.csproj) — MERGED 2026-05-21
- #15 (global.json SDK pinning, GitHub Actions standardization) — MERGED 2026-05-21

**Open PRs:**
- #20 (subscription_health Task.WhenAll parallelization) — OPEN, awaiting merge

**Open issues:**
- #19 (flow_graph 7d→3d default window) — Fix not yet implemented; baseline timing capture priority

**Smoke test plan drafted:** 5-step live validation plan for Larry, prioritizing subscription_health perf validation, Sqlite 10 + PCS Client stability, flow_graph baseline timing (pre-#19), and coverage of tools not recently exercised.
