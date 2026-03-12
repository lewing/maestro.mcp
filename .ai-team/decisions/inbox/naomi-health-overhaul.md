# Decision: Replace canary warning with oscillation detection

**Author:** Naomi (Backend Dev)
**Date:** 2026-07-25
**Status:** Implemented

## Context

Live investigation of arcade-services#6090 revealed that `SubscriptionUpdate.Success` is never set to `true` in the PCS codebase. The existing `CheckCanaryWarningAsync` fired on every stale subscription with 10+ history entries — pure noise that obscured real stuck subscriptions.

## Decision

1. **Replace canary with oscillation detection**: Check for alternating state patterns (typically `ApplyingUpdates` ↔ `MergingPullRequest`) in subscription history. Require 3+ consecutive oscillations before flagging — this eliminates false positives while catching the specific arcade-services#6090 bug pattern.

2. **Add VMR manifest tracing**: For subscriptions targeting `dotnet/dotnet`, read `src/source-manifest.json` from GitHub to show the actual commit SHA consumed by the VMR. This provides ground truth without depending on Maestro bookkeeping.

3. **Expose `--validate` in CLI**: The flag already existed on the MCP tool but was missing from the CLI command.

## Rationale

- The oscillation pattern is a high-confidence signal — healthy subscriptions don't alternate between exactly two states indefinitely.
- The 3-oscillation threshold avoids false positives from transient retries.
- The source-manifest.json approach gives users actionable data (what commit is actually in the VMR) rather than just "something might be wrong."

## Files Changed

- `IGitHubApiClient.cs` — Added `GetFileContentsAsync`
- `GitHubApiClient.cs` — Implemented raw file content fetching
- `MaestroService.cs` — Replaced `CheckCanaryWarningAsync` with `DetectStateOscillationAsync` + `GetVmrConsumedCommitAsync`; updated records
- `MaestroMcpTools.cs` — Updated health output formatting
- `Program.cs` — Added `--validate` CLI parameter, added oscillation/VMR output
