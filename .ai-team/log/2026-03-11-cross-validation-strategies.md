# Session Log: Cross-Validation Strategies for Subscription Health

**Date:** 2026-03-11  
**Requested by:** Larry Ewing  
**Analysis by:** Holden

## Summary

Holden analyzed cross-validation strategies for detecting when Maestro's subscription bookkeeping diverges from ground truth. The analysis followed up on findings from session 55857d51, where the emsdk subscription was discovered stuck with `LastAppliedBuildId` frozen at an old build and the `Success` field never set to true.

## Key Findings

1. **Identified Root Cause**: Exceptions in `GetLastCodeflownBuild()` can bypass `ClearAllStateAsync()`, leaving subscription records in a frozen state.
2. **Scope of Problem**: Both `maestro_subscription_health` and `maestro_codeflow_statuses` tools are vulnerable to reporting stale data.
3. **Proposed Solutions**: 6 cross-validation strategies across 3 implementation phases.

## 6 Proposed Strategies

### Phase 1 (P1) — Low-Cost, High-Value
- **Strategy 1: PR Ground Truth Cross-Check** — GitHub search for merged PRs vs. Maestro subscription history
- **Strategy 2: Commit Reachability Validation** — Verify commit SHAs exist in target repo using GitHub compare API

**Estimated effort:** 3–4 days  
**Target release:** v0.7.0 (2 weeks)

### Phase 2 (P2) — Deep Diagnostics
- **Strategy 3: Subscription History vs. PR Lifecycle Alignment** — Timeline correlation for root cause analysis
- **Strategy 6: Subscription History "Success" Field Audit** — Detect "Success never true" bug

**Estimated effort:** 4–5 days  
**Target release:** v0.8.0 (backlog)

### Phase 3 (P3) — Proactive Monitoring
- **Strategy 4: Build Freshness vs. Actual Source Activity** — Detect unreported builds in source repo
- **Strategy 5: Codeflow Status Cross-Validation with Tracked PRs** — Validate tracked PR state against codeflow endpoint

## Key Architectural Decisions

1. **Opt-In Validation**: Add `validate: bool = false` parameter to `maestro_subscription_health` to control API call overhead.
2. **Structured Anomaly Reporting**: Return new `ValidationResult` type with clear signal fields (`CommitReachable`, `MergedPrsSinceLastApplied`, `BookkeepingAnomalyDetected`).
3. **Rate Limit Awareness**: Batch validation of top N stale subscriptions first; return partial results if GitHub rate limit hit.
4. **Caching Strategy**: Cache PR search results for 15 min, commit reachability for 30 min (longer than subscription data's 5 min TTL).

## Expected Outcomes (Phase 1)

- Correctly flags the emsdk subscription as stuck (from session 55857d51)
- <5% false positive rate on subscription anomalies
- Adds <10 seconds overhead per 10 subscriptions validated
- Structured data that consuming skills (flow-analysis, etc.) can act on

## Open Questions for Team

1. Is Phase 1 the right focus?
2. Should validation be an optional tool parameter or a separate tool?
3. Should we file upstream issues with Maestro team for self-healing in `PullRequestUpdater.cs`?
4. Will consuming skills need JSON or is markdown sufficient?
5. What's the acceptable GitHub API budget for validation queries?

## References

- Full analysis: `.ai-team/decisions.md` → "Cross-validation strategies for subscription health"
- Session 55857d51: emsdk subscription investigation
- Affected tools: `maestro_subscription_health`, `maestro_codeflow_statuses`
- Affected agents: Naomi (will implement), Amos (will test)
