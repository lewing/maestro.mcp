# Decision: Phase 1 Cross-Validation Implementation Choices

**Author:** Naomi (Backend Dev)
**Date:** 2026-03-11
**Status:** Implemented

## Context

Maestro's `LastAppliedBuildId` bookkeeping can get stuck when exceptions bypass state clearing. The `Success` field is never set to true. Our `maestro_subscription_health` tool trusted this data at face value.

## Decisions Made

### 1. Branch pattern matching uses source repo short name
Instead of trying to reconstruct exact `darc-` branch naming conventions (which vary by codeflow version), we search GitHub for merged PRs with `head:{sourceRepoName}` (e.g., `head:emsdk` for `dotnet/emsdk`). This is simpler, covers both darc and VMR codeflow patterns, and is sufficient for anomaly detection.

### 2. Commit reachability checks the SOURCE repo, not target
We verify `LastAppliedBuild.Commit` is reachable in the source repository (where the build was produced), not the target. A 404 from the compare API means the commit doesn't exist, indicating corrupted bookkeeping.

### 3. Canary warning runs unconditionally for stale subs
The canary check (10+ history entries with zero successes) is cheap — it reuses the existing cached `GetSubscriptionHistoryAsync`. No need for `validate=true`. This provides early warning without any extra API calls.

### 4. Validation results cached at MediumTtl (15 min)
Ground truth (merged PRs, commit reachability) changes slowly. Caching at 15 minutes prevents rate limit exhaustion during repeated health checks.

### 5. GitHub search API returns max 10 results
We cap at `per_page=10` since we only need to detect whether PRs exist, not enumerate all of them. This minimizes API quota usage.

## Files Changed
- `src/MaestroTool.Core/IGitHubApiClient.cs` — Added `SearchMergedPullRequestsAsync`, `GitHubPullRequest` record
- `src/MaestroTool.Core/GitHubApiClient.cs` — Implemented search method
- `src/MaestroTool.Core/MaestroService.cs` — `ValidationResult` record, `SubscriptionHealthResult` extension, validation + canary logic
- `src/MaestroTool.Core/MaestroMcpTools.cs` — `validate` param, formatted output
