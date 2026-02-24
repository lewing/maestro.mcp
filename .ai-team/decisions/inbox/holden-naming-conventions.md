# Naming Convention Review for Issue #9

**Date**: 2026-02-20  
**Reviewer**: Holden (Lead/Architect)  
**Issue**: #9 "Inconsistent tool naming conventions"

## Executive Summary

Issue #9 proposes standardizing MCP tool naming conventions. After reviewing all 17 current tools, **the inconsistencies are real but the proposed convention is only partially beneficial**. The current naming follows an implicit pattern that's reasonably predictable once understood. The highest-ROI improvement is **adding missing symmetrical tools** (`maestro_builds`, `maestro_channel`), not renaming existing ones.

**Recommendation**: Accept the proposal's diagnostic value, but implement via **additive changes only** (no breaking renames). Add 2-3 missing tools for symmetry, document the naming pattern, and establish a convention for future tools.

---

## Current State Analysis

### Tool Inventory (17 tools)

**Query tools (bare nouns):**
- `maestro_subscriptions` (list) / `maestro_subscription` (get) ✅ symmetric
- `maestro_channels` (list) / ❌ no `maestro_channel` (get)
- `maestro_latest_build` (query) / `maestro_build` (get) / ❌ no `maestro_builds` (list)
- `maestro_default_channels` (list only, no get) ✅ OK
- `maestro_subscription_health` (detail)
- `maestro_subscription_history` (detail)
- `maestro_build_freshness` (detail)
- `maestro_build_graph` (detail)
- `maestro_flow_graph` (detail)
- `maestro_backflow_status` (detail)
- `maestro_codeflow_prs` (list) / `maestro_tracked_pr` (get) ⚠️ asymmetric noun

**Action tools (verb prefixes):**
- `maestro_trigger_subscription`
- `maestro_trigger_daily_update`
- `maestro_clear_cache`

**CLI commands** (for comparison, use hyphens instead of underscores):
- `subscriptions`, `subscription`, `latest-build`, `build`, `channels`, `default-channels`, `subscription-health`, `build-freshness`, `trigger-subscription`, `trigger-daily-update`, `codeflow-prs`, `tracked-pr`, `backflow-status`, `subscription-history`, `build-graph`, `flow-graph`, `cache`

---

## Issue Analysis

### 1. Plural/Singular Asymmetry ⚠️ Real Issue

**Finding**: 2 of 4 resource pairs are asymmetric:
- Builds: `maestro_latest_build` + `maestro_build` exist, but no `maestro_builds` (list)
- Channels: `maestro_channels` exists, but no `maestro_channel` (get by ID)

**Impact**: Medium. Agents expect list/get pairs. The missing tools force workarounds (e.g., filtering `maestro_channels` client-side to find a specific channel).

**ROI**: HIGH. Adding `maestro_builds` and `maestro_channel` is non-breaking and immediately useful.

### 2. Codeflow Terminology Inconsistency ⚠️ Real Issue

**Finding**: `maestro_codeflow_prs` (list) uses "codeflow", but `maestro_tracked_pr` (get) uses "tracked". Both operate on Maestro-managed PRs.

**Impact**: Low-Medium. Confusing terminology, but both are technically accurate:
- "codeflow PR" = GitHub PR created by dependency flow
- "tracked PR" = Maestro's subscription tracking record

**ROI**: LOW. Renaming would break existing skills. The semantic difference may be intentional (tracking ≠ PR itself).

### 3. Verb Prefix Pattern ✅ Not An Issue

**Finding**: Actions use `trigger_`/`clear_` prefixes, queries use bare nouns.

**Assessment**: This is a GOOD implicit convention, not a bug. It disambiguates read-only queries from state-changing actions. The proposed `maestro_get_build` would be redundant — agents already understand `maestro_build` = get, `maestro_trigger_subscription` = action.

**ROI**: NEGATIVE. Adding `get_` prefixes would make names longer without improving clarity.

### 4. Compound Word Length ✅ Not An Issue

**Finding**: Most tools are 2 words, `trigger_daily_update` is 3 words.

**Assessment**: Acceptable. "daily update" is a domain term (the PCS nightly job). Shortening to `trigger_daily` would lose meaning.

---

## Proposed Convention Evaluation

```
maestro_{verb}_{resource}        # actions: maestro_trigger_subscription
maestro_{resource}               # get one: maestro_subscription
maestro_{resources}              # list:    maestro_subscriptions
maestro_{resource}_{aspect}      # detail:  maestro_subscription_health
```

**Strengths**:
- Codifies the current implicit pattern
- Clear action/query distinction
- Predictable for agent reasoning

**Weaknesses**:
- `maestro_latest_build` doesn't fit (should be `maestro_build_latest`?)
- Doesn't address the codeflow/tracked terminology split
- Over-formalizes what's already working

**Verdict**: The convention is mostly **descriptive** (what we already do) rather than **prescriptive** (new rules). Value is in documentation, not enforcement.

---

## Recommendations

### P1: Non-Breaking Additions (Immediate)

1. **Add `maestro_builds`** (list builds with filters — repo, channel, date range)
   - Fills the symmetry gap with `maestro_build` (get by ID)
   - Useful for "find recent builds" queries
   - Effort: ~4-6 hours (API call + formatting)

2. **Add `maestro_channel`** (get channel by ID)
   - Fills the symmetry gap with `maestro_channels` (list)
   - Useful for "what's channel ID 42?" queries
   - Effort: ~2-3 hours (API call exists in service layer)

### P2: Documentation (Next Sprint)

3. **Document the naming pattern** in README or `MaestroMcpTools.cs` header:
   ```
   Naming convention:
   - Actions: maestro_{verb}_{resource} (e.g., maestro_trigger_subscription)
   - Queries (get): maestro_{resource} (e.g., maestro_subscription)
   - Queries (list): maestro_{resources} (e.g., maestro_subscriptions)
   - Queries (detail): maestro_{resource}_{aspect} (e.g., maestro_subscription_health)
   ```

### P3: Consider for Future (Backlog)

4. **Alias `maestro_tracked_pr` → `maestro_codeflow_pr`** (deprecation period)
   - Makes terminology consistent with `maestro_codeflow_prs`
   - Requires MCP SDK support for tool aliases (TBD if SDK supports this)
   - Low priority — existing name is defensible

### ❌ Not Recommended

- **Renaming existing tools**: Breaking change for all consuming skills. The current names are learnable and not fundamentally broken.
- **Adding `maestro_get_*` prefixes**: Redundant. The implicit "bare noun = get" pattern is already clear.
- **Renaming `maestro_latest_build`**: "Latest" is a common query pattern (cf. REST APIs with `/latest` endpoints). Not worth the churn.

---

## Migration Path (If We Did Break Things)

If we WERE to make breaking changes (not recommended):

1. **Phase 1 (v0.8)**: Add aliases for new names, keep old names working
2. **Phase 2 (v0.9)**: Deprecation warnings in tool descriptions
3. **Phase 3 (v1.0)**: Remove old names

**Estimated disruption**: 6-12 months for ecosystem to migrate. Not worth it for marginal clarity gains.

---

## Conclusion

Issue #9 provides valuable clarity on our naming patterns. The best action is **additive**: fill the 2 symmetry gaps (`maestro_builds`, `maestro_channel`), document the pattern, and move on. Breaking changes aren't justified by the marginal improvement.

**Decision**: Accept the analysis, implement P1 items, defer P3 to backlog, reject breaking renames.
