# Decision: Enrich oscillation detection with tracked PR diagnosis

**Author:** Naomi (Backend Dev)
**Date:** 2026-07-24

## Context

Oscillation detection (`DetectStateOscillationAsync`) flags stale subscriptions with alternating ApplyingUpdates ↔ MergingPullRequest patterns. However, all stuck subscriptions produce the same pattern regardless of root cause — the oscillation alone can't distinguish Redis bugs, CI failures, or missing PRs.

## Decision

For stale subscriptions, cross-reference the Maestro tracked PR with GitHub's actual PR state to classify WHY a subscription is stuck:

- **MergedButNotCleared**: arcade-services#6090 — PR merged but Maestro keeps cycling
- **ClosedButNotCleared**: PR was closed but subscription state not cleared
- **BlockedByCI**: PR is open but CI checks are failing
- **Active**: PR is open and healthy (may be in progress)
- **Missing**: No tracked PR exists at all
- **Unknown**: PR exists but GitHub check failed

## Implementation

- `DiagnoseTrackedPrAsync` in MaestroService.cs — fetches tracked PR, checks GitHub state
- `GetPullRequestStateAsync` in GitHubApiClient.cs — new API method for PR + CI status
- `TrackedPrState` enum + `TrackedPrDiagnosis` record — new types in data model
- `SubscriptionHealthResult.TrackedPr` — new optional field
- Output in both MCP tools and CLI with emoji state indicators

## Rationale

The staleness gate (`if (isStale)`) prevents false positives on healthy subs. For stale+oscillating subs, the tracked PR is the cheapest signal to classify the root cause and guide human action.
