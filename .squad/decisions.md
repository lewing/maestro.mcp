# Decisions

Team decisions are recorded here. Append-only — never edit existing entries.

## Dependency Policy Recommendations — 2026-05-21

**Author:** Alex (DevOps / Infrastructure)  
**Date:** 2026-05-21  
**Status:** Proposal — awaiting team review

### Context

Full dependency audit performed on 2026-05-21 across NuGet packages, .NET SDK, GitHub Actions, and NuGet feeds.

---

### Recommendation 1: Add global.json (SDK pinning)

**Risk:** Without global.json, the SDK version used varies per developer machine and CI image. Currently installed: 10.0.202; latest on channel: 10.0.300.

**Proposal:** Add a `global.json` at the repo root pinning `sdk.version` to `10.0.300` with `rollForward: latestPatch`. This ensures all contributors and CI use the same SDK, preventing "works on my machine" build drift.

**Effort:** Trivial (one file). No code changes required.

---

### Recommendation 2: Adopt Central Package Management (Directory.Packages.props)

**Risk:** Four `.csproj` files each manage package versions independently. Wildcard pins (`17.*`, `5.*`, `3.*`) in test projects could silently pick up a MAJOR version bump if the resolver resolves across a major boundary (e.g., if 18.x is available and `17.*` matches, it won't — but this is easy to misconfigure).

**Proposal:** Introduce `Directory.Packages.props` with `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`. All version numbers move to a single file; `.csproj` files keep `<PackageReference>` without version. Makes audits like this one trivial going forward.

**Effort:** Small (refactor only, no dependency changes). Holden approval recommended before adopting as it affects all contributors.

---

### Recommendation 3: Upgrade safe patch NuGet bumps

The following are safe patch bumps that require no API changes and should be done soon:

| Package | Current | Latest | Type |
|---|---|---|---|
| `Microsoft.Extensions.DependencyInjection` | 10.0.3 | 10.0.8 | Patch |
| `Microsoft.Extensions.Hosting` | 10.0.0 | 10.0.8 | Patch |
| `Microsoft.DotNet.ProductConstructionService.Client` | 1.1.0-beta.26161.4 | 1.1.0-beta.26271.2 | Pre-release patch |

**Proposal:** Apply these in one PR without review ceremony. Run full test suite (179 tests) to validate.

---

### Recommendation 4: Pin and standardize GitHub Actions versions

Multiple workflows use different versions of the same action (`actions/checkout@v4` vs `@v6`; `actions/github-script@v7` vs `@v8`). The latest releases are:

| Action | Versions in use | Latest |
|---|---|---|
| `actions/checkout` | v4, v6 | v6 |
| `actions/github-script` | v7, v8 | v9 |
| `actions/setup-dotnet` | v5 | v5 |
| `NuGet/login` | v1 | v1 |

**Proposal:** Standardize all workflows to the latest major version. `actions/checkout@v4` → `v6` and `actions/github-script@v7` and `v8` → `v9`. These are major bumps but GitHub publishes them as drop-in compatible within Node.js runner infrastructure.

**Effort:** Low. Grep-and-replace across `.github/workflows/`. No code logic changes required.

---

### Recommendation 5: Hold on these — wait for stable releases

| Package | Current | Latest | Reason to wait |
|---|---|---|---|
| `Microsoft.Data.Sqlite` | 9.0.0 | 10.0.8 | MAJOR bump — Holden review; may carry EF Core 10 changes |
| `Microsoft.NET.Test.Sdk` | 17.x | 18.5.1 | MAJOR bump — validate full test pipeline before bumping |
| `NSubstitute` | 5.3.0 | 6.0.0-rc.1 | MAJOR pre-release — wait for stable |
| `xunit.runner.visualstudio` | 3.1.5 | 4.0.0-pre.4 | MAJOR pre-release — wait for stable |

`Microsoft.Data.Sqlite` 10.x aligns with the `net10.0` target framework and is the natural upgrade, but carries the most risk of subtle SQLite API changes. Recommend Holden reviews before merging.

---

## MCP Dynamic Parameter Completions Research

**Author:** Naomi  
**Date:** 2026-05-08  
**Status:** Research Complete  
**Target:** Holden (decision authority)

---

### Executive Summary

The C# ModelContextProtocol SDK v1.3.0 **does support dynamic parameter completion**, but **only for Prompts and Resources** — NOT for Tools.

✅ **Supported:** Dynamic completion for prompt arguments and resource template parameters  
❌ **Not Supported:** Dynamic completion for tool parameters  
✅ **Available:** Static completion via `[AllowedValues]` attribute (works for all three)

---

### Key Findings

1. **SDK exposes `completion/complete` handler:** YES via `WithCompleteHandler()` extension
2. **Parameter-level completion attributes:** PARTIAL — only static `[AllowedValues]` supported
3. **Dynamic source for `[AllowedValues]`:** YES, via handler precedence (dynamic first, then static)
4. **Tool parameter completions:** NOT supported by MCP spec; only Prompts and Resources
5. **Active issues/PRs:** None found tracking tool parameter completions

### Recommendation for maestro.mcp

**DO NOT implement completion handler for tools** — it's not supported by the spec and would require prompts/resources we don't need.

**INSTEAD:**
1. **Keep existing pattern:** `maestro_list_channels` discovery tool (already exists)
2. **Enhance validation errors:** Update tool error messages to include "Did you mean: X, Y, Z?" suggestions when validation fails
3. **Agent training:** Document in skill file that agents should call discovery tools first or handle validation errors

**Future monitoring:** If MCP spec adds `ToolReference` completion support in a future version, revisit this decision.

---

### References

- **MCP Spec (Completions):** https://modelcontextprotocol.io/specification/2025-11-25/server/utilities/completion
- **MCP Spec (Tools):** https://modelcontextprotocol.io/specification/2025-11-25/server/tools
- **C# SDK Docs (Completions):** https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/completions/completions.md
- **C# SDK Source (WithCompleteHandler):** https://github.com/modelcontextprotocol/csharp-sdk/blob/main/src/ModelContextProtocol/McpServerBuilderExtensions.cs
- **Schema (2025-11-25):** https://github.com/modelcontextprotocol/specification/blob/main/schema/2025-11-25/schema.ts

**Next Action:** Holden to decide whether to implement Option 2 (enhanced validation errors) for better agent UX.

---

## Dependency Bump Review — 2026-05-21

**Author:** Holden (Lead/Architect)
**Date:** 2026-05-21
**Status:** Decided
**Triggered by:** Alex dependency audit (2026-05-21)

---

### Decision 1: Microsoft.Data.Sqlite 9.0.0 → 10.0.8

**Verdict:** Approve

**Reasoning:** Our CacheService.cs uses only core ADO.NET-pattern APIs: `SqliteConnection`, `SqliteCommand`, `SqliteParameter` (via `AddWithValue`), `SqliteException`, `SqliteConnection.ClearAllPools()`, and `ExecuteReaderAsync`/`ExecuteScalarAsync`/`ExecuteNonQueryAsync`. These APIs have been stable across every major Sqlite version since v2. Running Sqlite 9 on a net10.0 TFM is a version skew that works today but creates unnecessary divergence — 10.x is the natural companion to net10.0 and aligns with the broader .NET 10 ecosystem.

**Risk level:** Low — no API surface we use has changed between 9 and 10; this is a straightforward major bump.

**Assignment:** Naomi — she owns the cache layer and restructured the Core project. Single PR is fine; pair it with the safe patch bumps (Recommendation 3) to reduce PR churn.

**Verification required:**
- `dotnet build` clean (0 warnings)
- Full test suite pass (179+ tests)
- Manual smoke test: run `mstro` with a cold cache, confirm cache.db creates and operates normally

---

### Decision 2: Microsoft.NET.Test.Sdk 17.x → 18.5.1

**Verdict:** Approve with conditions

**Reasoning:** Test.Sdk 18.x ships the Microsoft.Testing.Platform but maintains full backward compatibility with VSTest-based runners. Our test project uses xunit 2.x with xunit.runner.visualstudio 3.x and NSubstitute 5.x — all of which are compatible with Test.Sdk 18. The wildcard version pin (`17.*`) is already risky and should be locked to an explicit version as part of this bump. No xunit or NSubstitute upgrades are required — those stay on their current stable versions.

**Conditions:**
1. Pin version explicitly: `<PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.5.1" />` — no wildcards.
2. While at it, pin xunit and NSubstitute to explicit versions too (e.g., `2.9.3`, `5.3.0`) — the wildcard pins are a latent risk Alex flagged.
3. If Alex's Central Package Management (Directory.Packages.props) proposal is approved later, these pins migrate there.

**Risk level:** Low — backward-compatible major bump; our test patterns (xunit + NSubstitute) are mainstream.

**Assignment:** Amos — he owns test infrastructure. Separate PR from the Sqlite/patch bump PR so test infra changes are isolated and easy to revert if CI behaves differently.

**Verification required:**
- `dotnet test` — all 179+ tests pass
- Verify `dotnet test --logger trx` still produces valid output (CI integration)
- Confirm no new analyzer warnings from the SDK bump

---

### Decision 3: 🟢 Safe bumps — Alex ships directly

**Verdict:** No lead approval needed for safe patch bumps.

The following do not need my review:
- Microsoft.Extensions.DependencyInjection 10.0.3 → 10.0.8 (patch)
- Microsoft.Extensions.Hosting 10.0.0 → 10.0.8 (patch)
- PCS Client beta refresh (1.1.0-beta.26161.4 → 1.1.0-beta.26271.2)
- global.json pin to 10.0.300
- GitHub Actions standardization (checkout→v6, github-script→v9)

**Standing policy:** Patch bumps within the same major.minor, pre-release refreshes on packages we already track as pre-release, and GitHub Actions major bumps that are documented as drop-in compatible — Alex can ship these directly with a passing test suite. No lead gate required.

---

### Decision 4: 🔴 Pre-release holds — confirmed

NSubstitute 6.0.0-rc.1 and xunit.runner.visualstudio 4.0.0-pre.4 stay on hold until stable releases. No action needed.

---

## Naomi — Sqlite + Extensions PR Shipped

**Date:** 2026-05-21  
**Author:** Naomi  
**PR:** https://github.com/lewing/maestro.mcp/pull/17

### Notable: Shared-Environment Branch Contention

During execution, git HEAD was being switched between calls by other concurrent agent sessions (Alex on `squad/infra-globaljson-and-actions`, Amos on `squad/test-infra-pin-and-sdk-bump`). This caused the initial checkout + edit sequence to land on the wrong branch at commit time. Mitigation: ran checkout + file edits + dotnet restore/build/test + commit all within a single bash process to prevent interleaved branch switching.

**Recommendation for team:** When multiple agents work on the same repo simultaneously, each should do checkout-through-push in a single atomic shell session. Consider coordination via `.squad/orchestration-log.md` before branch ops.

### Outcome

All 4 packages bumped successfully. 179/179 tests pass. PR open for merge.

---

## Alex — RollForward Major Added to MaestroTool Executable

**Author:** Alex (DevOps / Infrastructure)  
**Date:** 2026-05-21  
**Status:** Complete  
**PR:** #16  
**Precedent:** helix.mcp PR #52

---

Add `<RollForward>Major</RollForward>` to `src/MaestroTool/MaestroTool.csproj` PropertyGroup to enable the global tool to start on machines with only newer .NET runtimes installed.
### Decision 1: Microsoft.Data.Sqlite 9.0.0 → 10.0.8

**Verdict:** Approve

#### Problem

The maestro.mcp global tool targets `net10.0`. On machines with only .NET 11 (or newer) installed, the tool fails to start because the .NET runtime config refuses to run if the exact target framework runtime is unavailable.

#### Solution: Why RollForward Major

- **Major (chosen):** Tool stays on net10 when available; rolls forward only when net10 is unavailable. Conservative, predictable behavior.
- **LatestMajor (rejected):** Would unconditionally upgrade to any newer major version. Aggressive, unpredictable side effects.

The decision to use `Major` preserves user expectations: the tool should behave the same across machines when possible, and only adapt when necessary.

### Implementation

Changed line 11 in `src/MaestroTool/MaestroTool.csproj`:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <PackAsTool>true</PackAsTool>
  <PackageType>McpServer</PackageType>
  <ToolCommandName>mstro</ToolCommandName>
  <Version>0.15.1</Version>
  <RollForward>Major</RollForward>  <!-- Added -->
</PropertyGroup>
```

### Verification

- ✅ Build succeeds (0 warnings, 0 errors)
- ✅ Pack succeeds (`lewing.maestro.mcp.0.15.1.nupkg` created)
- ✅ All 179 tests pass
- ✅ Change mirrors helix.mcp PR #52 exactly

### Reference

[.NET roll-forward behavior documentation](https://learn.microsoft.com/en-us/dotnet/core/versions/selection#control-roll-forward-behavior)

### Precedent

This fix replicates the approach taken in helix.mcp PR #52, which applied the same RollForward Major setting to HelixTool.csproj for the same reason: enabling compatibility with newer .NET runtimes while maintaining predictable behavior on machines with the target framework available.
**Reasoning:** Our CacheService.cs uses only core ADO.NET-pattern APIs: `SqliteConnection`, `SqliteCommand`, `SqliteParameter` (via `AddWithValue`), `SqliteException`, `SqliteConnection.ClearAllPools()`, and `ExecuteReaderAsync`/`ExecuteScalarAsync`/`ExecuteNonQueryAsync`. These APIs have been stable across every major Sqlite version since v2. Running Sqlite 9 on a net10.0 TFM is a version skew that works today but creates unnecessary divergence — 10.x is the natural companion to net10.0 and aligns with the broader .NET 10 ecosystem.

**Risk level:** Low — no API surface we use has changed between 9 and 10; this is a straightforward major bump.

**Assignment:** Naomi — she owns the cache layer and restructured the Core project. Single PR is fine; pair it with the safe patch bumps (Recommendation 3) to reduce PR churn.

**Verification required:**
- `dotnet build` clean (0 warnings)
- Full test suite pass (179+ tests)
- Manual smoke test: run `mstro` with a cold cache, confirm cache.db creates and operates normally

---

### Decision 2: Microsoft.NET.Test.Sdk 17.x → 18.5.1

**Verdict:** Approve with conditions

**Reasoning:** Test.Sdk 18.x ships the Microsoft.Testing.Platform but maintains full backward compatibility with VSTest-based runners. Our test project uses xunit 2.x with xunit.runner.visualstudio 3.x and NSubstitute 5.x — all of which are compatible with Test.Sdk 18. The wildcard version pin (`17.*`) is already risky and should be locked to an explicit version as part of this bump. No xunit or NSubstitute upgrades are required — those stay on their current stable versions.

**Conditions:**
1. Pin version explicitly: `<PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.5.1" />` — no wildcards.
2. While at it, pin xunit and NSubstitute to explicit versions too (e.g., `2.9.3`, `5.3.0`) — the wildcard pins are a latent risk Alex flagged.
3. If Alex's Central Package Management (Directory.Packages.props) proposal is approved later, these pins migrate there.

**Risk level:** Low — backward-compatible major bump; our test patterns (xunit + NSubstitute) are mainstream.

**Assignment:** Amos — he owns test infrastructure. Separate PR from the Sqlite/patch bump PR so test infra changes are isolated and easy to revert if CI behaves differently.

**Verification required:**
- `dotnet test` — all 179+ tests pass
- Verify `dotnet test --logger trx` still produces valid output (CI integration)
- Confirm no new analyzer warnings from the SDK bump

---

### Decision 3: 🟢 Safe bumps — Alex ships directly

**Verdict:** No lead approval needed for safe patch bumps.

The following do not need my review:
- Microsoft.Extensions.DependencyInjection 10.0.3 → 10.0.8 (patch)
- Microsoft.Extensions.Hosting 10.0.0 → 10.0.8 (patch)
- PCS Client beta refresh (1.1.0-beta.26161.4 → 1.1.0-beta.26271.2)
- global.json pin to 10.0.300
- GitHub Actions standardization (checkout→v6, github-script→v9)

**Standing policy:** Patch bumps within the same major.minor, pre-release refreshes on packages we already track as pre-release, and GitHub Actions major bumps that are documented as drop-in compatible — Alex can ship these directly with a passing test suite. No lead gate required.

---

### Decision 4: 🔴 Pre-release holds — confirmed

NSubstitute 6.0.0-rc.1 and xunit.runner.visualstudio 4.0.0-pre.4 stay on hold until stable releases. No action needed.

---

## Amos — Issue #19 Flow Graph Days Bounds — 2026-06-24

**Author:** Amos  
**Status:** Shipped (PR #33 docs update included scope notes)

### Decision

`maestro_flow_graph` default scope reduced from 7 days to 3 days. Callers may widen the window via `days` parameter (1-30, bounds-enforced).

### Rationale

A 30-day upper bound prevents accidental pathological graph queries while preserving opt-in paths for deeper investigation. Bounds validated in test suite before API calls; negative, zero, and >30 values are rejected before MCP tool invocation.

---

## Naomi — `maestro_channels` Filter Parameters — 2026-05-22

**Author:** Naomi  
**Status:** Shipped (PR #23)

### Decision

Add optional parameters to `maestro_channels`:

- `filter`: case-insensitive substring match on channel name.
- `classification`: passed through to PCS.
- `compact`: bool flag returning `name → id` lines instead of markdown.

No-argument calls preserve full bulleted list. `classification` receives distinct cache entry; `filter` applied post-cache for ad hoc searches.

### Deferred

Pagination (channels is small dataset), and similar filters for `default_channels` and `subscriptions` (have natural API filters).

---

## Naomi — MCP Description Trim Result — 2026-06-11

**Author:** Naomi  
**Status:** Complete (PR #16)

### Achieved Metrics

Trimmed tool descriptions from ~430 words / ~559 tokens to ~280 words / ~364 tokens. Measured result:

| File | Before words | After words | Savings |
|---|---:|---:|---:|
| Total | **413** | **251** | **162 words, 211 tokens saved** |

### Rationale

Descriptions are always-loaded routing context. Holden's audit rules applied: lead with a verb, keep to 1-2 sentences, move filter semantics to parameter `[Description]` attributes.

---

## Naomi — Subscription Health Filtering and Compact Output — 2026-05-22

**Author:** Naomi  
**Status:** Shipped (PR #20)

### Decision

Add opt-in filtering to `maestro_subscription_health`:

- `staleOnly`: omits healthy subscriptions.
- `channelFilter`: case-insensitive channel name match.
- `sourceRepoFilter`: case-insensitive repo URL or short name match.
- `compact`: one line per subscription.

All optional; no-argument output unchanged. Filters applied post-computation (preserves parallel fan-out perf).

### Measurement

For dotnet/dotnet VMR data: detailed output 24,398 bytes (93 subscriptions / 43 stale); `staleOnly + compact` output 3,049 bytes (~87.5% reduction).

