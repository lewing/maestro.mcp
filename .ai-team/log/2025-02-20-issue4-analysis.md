# Session Log: Issue #4 Analysis and Commit Distance Proposal

**Date:** 2025-02-20
**Requested by:** Larry Ewing
**Analyzer:** Naomi (Backend Developer)

## Key Findings

- **Problem:** `maestro_subscription_health` reports inflated commit distance for VMR subscriptions using BAR build ID arithmetic (566 builds behind vs actual 33 commits)
- **Root cause:** BAR IDs are globally sequential across repos, not per-repo; arithmetic doesn't translate to real commit count
- **API unreliability:** `maestro_backflow_status` BackflowStatus API errors on all tested VMR builds (302627, 302612, 302391), cannot be relied upon

## Proposed Solution

Implement GitHub Compare API integration for VMR subscriptions (dotnet/dotnet → X):
- Proven reliable approach (same as `Get-CodeflowStatus.ps1`)
- No PCS dependency
- Graceful fallback to BAR ID arithmetic if GitHub API unavailable
- Estimated 6-8 hour implementation

## Decision Documents

- `naomi-force-trigger.md`: Decision to add `force` as optional parameter to `maestro_trigger_subscription`
- `naomi-issue4-commit-distance-approach.md`: Technical proposal for GitHub Compare API integration with detailed implementation plan

