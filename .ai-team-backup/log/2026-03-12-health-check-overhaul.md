# Session Log: Health Check Overhaul

**Date:** 2026-03-12  
**Requested by:** Larry Ewing

## Summary

Naomi overhauled Maestro subscription health diagnostics to replace a broken canary warning mechanism with oscillation detection and tracked PR diagnosis. Amos updated the test suite to validate the new approach. Both deliverables are complete and all tests pass.

## Work Completed

### Naomi's Health Check Overhaul

**Problem:** The existing `CheckCanaryWarningAsync` checked if `SubscriptionUpdate.Success` was `true`, but this field is never set in the PCS codebase. The canary fired on every stale subscription with 10+ history entries — pure noise that obscured real stuck subscriptions.

**Solution:**
1. **Oscillation Detection** — Replaced canary with detection of alternating state patterns (typically `ApplyingUpdates` ↔ `MergingPullRequest`). Requires 3+ consecutive oscillations before flagging — this eliminates false positives while catching the arcade-services#6090 bug pattern.
2. **VMR Source Manifest Tracing** — For subscriptions targeting `dotnet/dotnet`, reads `src/source-manifest.json` from GitHub to show the actual commit SHA consumed by the VMR. Provides ground truth without depending on Maestro bookkeeping.
3. **CLI `--validate` Exposure** — The flag already existed on the MCP tool but was missing from the CLI command.

**Key design decisions:**
- Oscillation detection is gated behind staleness to prevent false positives on healthy subscriptions.
- The 3-oscillation threshold avoids false positives from transient retries.
- Source-manifest.json approach gives users actionable data (what commit is actually in the VMR).

**Files changed:**
- `IGitHubApiClient.cs` — Added `GetFileContentsAsync`
- `GitHubApiClient.cs` — Implemented raw file content fetching
- `MaestroService.cs` — Replaced `CheckCanaryWarningAsync` with `DetectStateOscillationAsync` + `GetVmrConsumedCommitAsync`; updated records
- `MaestroMcpTools.cs` — Updated health output formatting
- `Program.cs` — Added `--validate` CLI parameter, added oscillation/VMR output

### Naomi's Tracked PR Diagnosis Enrichment

**Problem:** Oscillation detection alone can't distinguish root causes — all stuck subscriptions produce the same alternating pattern regardless of whether it's a Redis bug, CI failure, or missing PR.

**Solution:** For stale subscriptions, cross-reference the Maestro tracked PR with GitHub's actual PR state to classify WHY a subscription is stuck:
- **MergedButNotCleared** — arcade-services#6090: PR merged but Maestro keeps cycling
- **ClosedButNotCleared** — PR closed but subscription state not cleared
- **BlockedByCI** — PR open but CI checks failing
- **Active** — PR open and healthy (may be in progress)
- **Missing** — No tracked PR exists
- **Unknown** — PR exists but GitHub check failed

**Implementation:**
- `DiagnoseTrackedPrAsync` in MaestroService.cs — Fetches tracked PR, checks GitHub state
- `GetPullRequestStateAsync` in GitHubApiClient.cs — New API method for PR + CI status
- `TrackedPrState` enum + `TrackedPrDiagnosis` record — New types in data model
- `SubscriptionHealthResult.TrackedPr` — New optional field
- Output in both MCP tools and CLI with emoji state indicators

**Key design decision:** The staleness gate prevents false positives on healthy subs. For stale+oscillating subs, the tracked PR is the cheapest signal to classify the root cause.

### Amos's Test Suite Updates

**Problem:** The original canary test suite relied on the broken `SubscriptionUpdate.Success` field. Two tests passed but tested the wrong thing.

**Solution:** Replaced 2 broken canary tests with 4 new oscillation/manifest tests:
1. `DetectStateOscillation_WithThreeOscillations_ReturnsTrue` — Validates oscillation detection with exactly 3 cycles
2. `DetectStateOscillation_WithTwoOscillations_ReturnsFalse` — Validates threshold at 3 (not 2)
3. `GetVmrConsumedCommit_WithValidManifest_ParsesCorrectly` — Validates source-manifest.json parsing
4. `DiagnoseTrackedPr_WithMergedPr_ReturnsMergedButNotCleared` — Validates tracked PR diagnosis classification

**Results:** All 150 tests in the suite pass. New tests cover the core decision logic (oscillation threshold, manifest parsing, PR diagnosis classification).

## Decisions Made

1. **Oscillation over canary** — Oscillation detection is a high-confidence signal; canary was pure noise.
2. **3-oscillation threshold** — Avoids false positives from transient retries while catching real arcade-services#6090 pattern.
3. **Source-manifest.json as ground truth** — Provides actionable data to users about what commit is in the VMR without depending on Maestro bookkeeping.
4. **Staleness gate on oscillation** — Prevents noisy alerts on healthy subscriptions by only diagnosing when staleness is confirmed.
5. **Tracked PR diagnosis adds WHY** — Not just "oscillation detected," but "why" (merged PR, CI blocked, missing PR, etc.) to guide human action.

## Impact

- Maestro health diagnostics now provide actionable root-cause analysis instead of noise.
- Developers stuck on broken deployments can understand why (oscillating PR merged but not cleared, CI blocked, etc.) without digging through logs.
- Test suite validates both the oscillation detection and tracked PR diagnosis logic.
- All 150 tests pass.
