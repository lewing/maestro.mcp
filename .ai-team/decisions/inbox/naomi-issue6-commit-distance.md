# Decision: Extend commit distance to all GitHub-hosted repos

**Author:** Naomi (Backend Dev)
**Date:** 2025-02-20
**Status:** Implemented
**Issue:** #6

## Context

The `subscription_health` tool computed accurate commit distance (via GitHub Compare API) only for VMR subscriptions (`github.com/dotnet/dotnet`). All other GitHub-hosted source repos fell back to BAR build ID arithmetic, which uses globally sequential IDs across all repos and wildly overstates staleness.

## Decision

- Changed the gate in `GetSubscriptionHealthAsync` from `IsVmrRepository()` to a new `IsGitHubRepository()` helper
- `IsGitHubRepository` delegates to the existing `ParseGitHubUrl` which already handles any `github.com` URL
- Kept `IsVmrRepository` — it may be useful for VMR-specific logic in the future
- No changes needed to display logic — both MCP tools and CLI already handle `CommitsBehind` generically via `.HasValue`

## Impact

All GitHub-hosted source repos now get accurate "N commits behind" instead of inflated "~N builds behind". Non-GitHub repos (e.g., Azure DevOps) continue using BAR ID arithmetic as before.

## Files changed

- `src/MaestroTool.Core/MaestroService.cs` — gate change, new helper, comment update
- `src/MaestroTool/MaestroTool.csproj` — version 0.7.0 → 0.7.1
- `src/MaestroTool/Program.cs` — version string 0.7.0 → 0.7.1
