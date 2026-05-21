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
