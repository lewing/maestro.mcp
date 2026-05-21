# Alex — History

## Completed Work

### README.md Documentation

**Date:** 2025-07-15

Created a comprehensive README.md covering:
- Project title and description
- Prerequisites (.NET 10 SDK, darc authentication)
- Build and run instructions
- MCP client configuration snippet
- 3-tier authentication cascade (PAT → cached Entra ID → anonymous)
- Table of all 8 tools with parameters
- Architecture overview (4-layer design: data, caching, business logic, MCP)
- Cache TTL table with justification
- Testing instructions
- MIT license

The README is production-ready and suitable for both internal developers and external MCP client integrators.

📌 Team update (2026-02-18): README.md created for maestro.mcp covering authentication, tools, architecture, and cache strategy — decided by Alex

## Learnings

### Documentation Standards for MCP Servers
- **Structure**: Prerequisites → Build → Run → Configuration → Authentication → Tools Reference → Architecture → Testing.
- **Authentication**: Always explain the cascade and provide clear examples for each tier. Devs should understand which auth method is active without running the code.
- **Tools Table**: Include name, description, and key parameters. This is the primary reference for MCP client integrators.
- **Architecture Section**: High-level overview of layers + classes is sufficient; don't document every method. Link layers to the problem they solve (caching → performance, service → business logic).
- **Cache TTLs**: Always justify TTL choices. This helps reviewers understand design trade-offs (freshness vs. API load).

### Convention: MCP Server README Layout
- Lead with problem statement: what does this server do?
- Prerequisites and environment setup are critical upfront.
- Configuration examples must be copy-pasteable with minimal edits.
- Always explain authentication flow in human terms before referencing files.
- Tools and architecture sections are reference material; use tables and bullet lists for scannability.

### README v2 Updates — Action Tools Documentation
- **Config file locations**: GitHub Copilot CLI is now the first row since it's the primary client for this project's users.
- **Action Tools section**: Documented non-destructive action tools with deduplication logic (2-minute cooldown prevents duplicate LLM retries).
- **Future destructive actions**: Sketched the opt-in pattern for future delete/remove operations, showing how `MAESTRO_ENABLE_DESTRUCTIVE_ACTIONS` env var gates dangerous operations.
- **Tool count updated**: Changed from 8 to 10 tools in the table header to reflect added action tools.
- **Placement**: Action Tools section sits between Authentication and Available Tools, since it provides context for how triggering works before listing the full tool reference.

### README Test Count & Version Update (2026-02-19)
- **README test count updated**: Changed from 67 to 76 tests in two locations:
  1. Architecture section tree diagram (line 192)
  2. Testing section description (lines 269-272) with note about 73 original + 3 regression tests
- **Version check**: Confirmed v0.2.2 is already set in MaestroTool.csproj (line 11) — no version update needed in README as no version was mentioned

### Release v0.12.0 (2026-03-01)
- **Tag**: `v0.12.0` pushed to `origin/master`
- **Version bumped** from 0.11.0 → 0.12.0 in 3 files: `MaestroTool.csproj`, `MaestroTool/Program.cs`, `MaestroTool.Mcp/Program.cs`
- **Included changes** (since last released tag v0.10.0):
  1. MCP SDK 1.0.0 upgrade (from 0.8.0-preview.1)
  2. CacheService `SetUnixFileMode` crash fix on Linux/WSL (`/tmp` chmod)
  3. ReadOnly/Destructive tool annotations on all 19 MCP tools
- **All 135 tests passed** before release

📌 Team update (2026-03-13): Restructure review approved — The core MCP tools restructure has been approved by Holden. All tools now organized into domain partials (Channels, Subscriptions, Builds, Codeflow, Utilities) with clean API preservation. — decided by Holden

📌 Team update (2026-05-08): MCP SDK upgrade decision — Naomi reviewed v1.0.0 → v1.3.0 changes. Recommendation: upgrade now (clean path, no code changes, 3 .csproj files). Will require dependency updates and test suite validation.


## 2026-05-08: SDK Version Baseline Shifted

Naomi completed upgrade of ModelContextProtocol from v1.0.0 → v1.3.0. Build clean (0 warnings), all 179 tests pass. SDK version baseline is now v1.3.0 across all projects. See decisions.md for upgrade details and benefits.

## 2026-05-21: Full Dependency Audit

Requested by Larry Ewing. Audited NuGet packages, .NET SDK, GitHub Actions, and NuGet feeds.

### Key Findings

**NuGet Packages — Safe Patch Bumps Available:**
- `Microsoft.Extensions.DependencyInjection` 10.0.3 → 10.0.8 (patch)
- `Microsoft.Extensions.Hosting` 10.0.0 → 10.0.8 (patch)
- `Microsoft.DotNet.ProductConstructionService.Client` 1.1.0-beta.26161.4 → 1.1.0-beta.26271.2 (pre-release patch, internal feed)

**NuGet Packages — Attention Required:**
- `Microsoft.Data.Sqlite` 9.0.0 → 10.0.8 (MAJOR 9→10; aligns with net10.0 target; Holden review recommended)
- `Microsoft.NET.Test.Sdk` 17.x → 18.5.1 (MAJOR; test infra change; validate test pipeline before bumping)
- `ModelContextProtocol` 1.3.0 — no update available (already current)
- `NSubstitute` 5.3.0 → 6.0.0-rc.1 (MAJOR pre-release, wait for stable)
- `xunit.runner.visualstudio` 3.1.5 → 4.0.0-pre.4 (MAJOR pre-release, wait for stable)

**SDK — No global.json present:**
- Installed SDK: 10.0.202. Latest on .NET 10 channel: 10.0.300.
- No global.json to pin the version — risk of environment drift between developer machines and CI.
- Recommend adding global.json pinned to 10.0.300.

**GitHub Actions:**
- `actions/checkout@v4` used in multiple workflows → latest is v6 (2 major versions behind)
- `actions/github-script@v7` → latest v9 (2 major versions behind); v8 reference also present
- `actions/setup-dotnet@v5` → at v5.2.0 (patch, fine)
- `NuGet/login@v1` → at v1.2.0 (patch within v1, fine)
- Mixed versions of same actions within the workflow set is a maintenance smell.

**NuGet Feeds:**
- `nuget.org` (public)
- `dotnet-eng` at Azure DevOps dnceng/public (internal, required for PCS client)
- No Central Package Management in use (no Directory.Packages.props)

### Conventions Learned
- No global.json exists — version pinning is only via TargetFramework. For reproducibility, a global.json should be added.
- No Central Package Management — each .csproj manages its own package versions with wildcard or explicit pins.
- Wildcard version pins (e.g., `17.*`, `5.*`, `3.*`) are used in test projects; this can silently pick up MAJOR bumps if the wildcard spans majors — currently safe but worth monitoring.
- The `dotnet-eng` internal feed is required at build time for the PCS client; any CI environment must have access to this Azure Artifacts feed.

## 2026-05-21: Dependency Bump Review — Safe-Bump Standing Policy

**Holden review of Alex's dependency audit completed** (2026-05-21). Decisions recorded in `.squad/decisions.md`.

**Key outcome:** Confirmed standing policy that Alex can ship safe patch/Actions/global.json bumps without approval:
- Patch bumps within same major.minor (e.g., 10.0.3 → 10.0.8)
- Pre-release refreshes on already-tracked pre-release packages (e.g., PCS Client beta → beta)
- GitHub Actions major bumps documented as drop-in compatible (e.g., checkout v4 → v6)

**No approval gate required** for safe bumps as long as full test suite (179+ tests) passes. Naomi and Amos assigned to handle major/minor dependency PRs.
