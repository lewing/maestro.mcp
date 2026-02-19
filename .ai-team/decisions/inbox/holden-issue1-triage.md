# Issue #1 Triage: Codeflow Feature Requests

**Date:** 2026-02-19  
**By:** Holden (Lead / Architect)  
**Issue:** https://github.com/lewing/maestro.mcp/issues/1  
**Scope:** 9 feature requests for codeflow analysis workflows

## Executive Summary

Issue #1 contains 9 well-scoped feature requests for enhancing maestro.mcp's usability in codeflow analysis workflows (vmr-codeflow-status, PR dependency tracking, flow visualization). All features are **feasible** with the current PCS client NuGet surface, though 3 require deeper investigation or GitHub API integration.

**Recommended roadmap:**
1. **v0.2.1 (sprint 1):** #1 + #2 + #3 — High-impact read/write fundamentals
2. **v0.3 (sprint 2):** #4 + #5 + #6 — Health & visualization composites  
3. **v0.4+ (backlog):** #7 + #8 + #9 — Specialized, lower-frequency queries

---

## Feature-by-Feature Triage

### Priority 1: High Impact

#### **1. `maestro_codeflow_prs` — List Codeflow PRs for a Repo**

**What**: Given a target repo (e.g., `dotnet/sdk`), list all open codeflow PRs created by Maestro.

**Current complexity**: 5–10 API calls (subscriptions + GitHub PR search + health checks)

**Assessment:**
- **Feasibility:** ✅ **Implementable** (medium effort)
- **Complexity:** Medium
- **PCS Support:** Full
- **Dependencies:** Requires GitHub API client for PR enrichment
- **Agent:** Naomi (backend) + (external) GitHub API integration

**Technical details:**
1. Query `ISubscriptions.ListSubscriptionsAsync(targetRepository: targetRepo, enabled: true)` → list of subscriptions
2. For each subscription, query GitHub's `search_pull_requests` API (not provided by maestro.mcp) with:
   - `author: "dotnet-maestro"` or similar
   - `base: subscription.TargetBranch`
   - `repo: targetRepository`
   - `is: open` or open/closed filter
3. Enrich each PR with subscription metadata (channel, lastAppliedBuild, staleness)

**Design notes:**
- Could cache GitHub API results (lower TTL, ~5 min, since PR state changes)
- Staleness check requires comparing `lastAppliedBuild` to latest on channel → reuse `GetSubscriptionHealthAsync`
- VMR snapshot commit delta (#1's bonus return field) requires checkout or GitHub API for HEAD — defer to v0.3

**Recommendation:** **Implement in v0.2.1**. Core logic is PCS-side (subscriptions + health); GitHub API integration is standard. Estimated: 2-3 dev days (Naomi).

---

#### **2. `maestro_force_trigger_subscription` — Force-Trigger a Subscription**

**What**: Force-trigger a subscription (overwrite existing PR with fresh VMR content).

**Current state**: `maestro_trigger_subscription` exists but is a *normal* trigger, not force.

**Assessment:**
- **Feasibility:** ✅ **Implementable** (small effort)
- **Complexity:** Small
- **PCS Support:** ✅ Already supported (just need to discover the right parameter)
- **Dependencies:** None
- **Agent:** Naomi (backend)

**Technical details:**

The PCS client's `ISubscriptions.TriggerSubscriptionAsync` signature is:
```csharp
Task<Subscription> TriggerSubscriptionAsync(int barBuildId, bool isCoherencyUpdate, Guid subscriptionId, CancellationToken)
```

The `isCoherencyUpdate` boolean is the force-trigger flag:
- `false` = normal trigger (standard PR update/creation)
- `true` = coherency update (force-rewrite all assets, overwrite existing PR branch)

**Current code in MaestroApiClient:**
```csharp
return await _api.Subscriptions.TriggerSubscriptionAsync(buildId, true, subscriptionId, cancellationToken);
```

Wait—the current implementation already passes `isCoherencyUpdate: true`, which means **the current `maestro_trigger_subscription` IS a force trigger**. Verify this with user/logs.

**Options:**
1. **Add a separate `maestro_force_trigger_subscription` tool** with `isCoherencyUpdate: true` explicitly exposed
2. **Add a boolean parameter to existing tool** `maestro_trigger_subscription(..., force: bool = false)`
3. **Clarify with Larry** whether current behavior is correct, then document

**Recommendation:** **Implement as new tool v0.2.1** (Option 1). Safer to have explicit tool names. Estimated: 4 hours (Naomi) + test coverage (Amos).

---

#### **3. Target Branch Filtering on `maestro_subscriptions`**

**What**: Add a `targetBranch` filter parameter to `maestro_subscriptions`.

**Current state**: Subscription objects have `TargetBranch` field, but tool doesn't expose filter.

**Assessment:**
- **Feasibility:** ✅ **Implementable** (trivial)
- **Complexity:** Small
- **PCS Support:** ✅ Fully supported
- **Dependencies:** None
- **Agent:** Naomi (backend) + Amos (test)

**Technical details:**

1. Add parameter to `MaestroMcpTools.GetSubscriptions()`:
   ```csharp
   [Description("Filter by target branch name (e.g., 'release/net10.0')")] string? targetBranch = null
   ```

2. Pass through to service layer (new optional param in `MaestroService.GetSubscriptionsAsync`):
   ```csharp
   // Filter client-side (PCS API doesn't have targetBranch filter, only sourceRepo/targetRepo/channelId)
   if (!string.IsNullOrEmpty(targetBranch))
   {
       subscriptions = subscriptions.Where(s => s.TargetBranch == targetBranch).ToList();
   }
   ```

3. Update cache key to include targetBranch: `subs:{sourceRepository}:{targetRepository}:{channelId}:{targetBranch}`

**Design notes:**
- PCS API's `ListSubscriptionsAsync` doesn't filter by target branch — must filter client-side post-fetch
- Acceptable: most branch filters will narrow to 1-2 subscriptions anyway
- Cache hit rate is good if users repeatedly query the same branch

**Recommendation:** **Implement in v0.2.1 (alongside #2)**. Trivial code change. Estimated: 2 hours (Naomi + Amos).

---

### Priority 2: Medium Impact

#### **4. `maestro_subscription_history` — Build Application History**

**What**: Return the timeline of builds applied by a subscription (when processed, success/failure, PR ID).

**Assessment:**
- **Feasibility:** 🟡 **Partially implementable** (PCS API gap)
- **Complexity:** Medium
- **PCS Support:** ⚠️ **Requires investigation**
- **Dependencies:** PCS client history APIs (if available)
- **Agent:** Naomi (backend research) → potentially blocked

**Technical details:**

Current PCS objects don't expose subscription history:
- `Subscription` model has `LastAppliedBuild` (scalar) — only the most recent
- No `AppliedBuildHistory` or similar collection on `Subscription`

**Possible approaches:**
1. **Check PCS client NuGet** for `ISubscriptions.GetHistoryAsync()` or related endpoint (Naomi to investigate)
2. **If not in PCS**: Suggest Maestro team add history endpoint to PCS API, or defer to v0.3

**Recommendation:** **Defer to v0.2.2/v0.3 pending PCS API investigation.** Assign Naomi to check PCS client API surface (1-2 hours). If available, implement. If not, file feature request with Maestro team.

---

#### **5. `maestro_flow_graph` — Dependency Flow Visualization**

**What**: Show complete dependency graph (inbound/outbound flows) for a repo.

**Assessment:**
- **Feasibility:** ✅ **Implementable** (medium effort)
- **Complexity:** Medium
- **PCS Support:** Full (via `ListSubscriptionsAsync` + `ListDefaultChannelsAsync`)
- **Dependencies:** Graph visualization library (optional; can return structured data)
- **Agent:** Naomi (backend) + (optional) Amos (tests)

**Technical details:**

1. **Inbound flows:** Subscriptions where `TargetRepository == repo`
   ```csharp
   var inbound = await _client.ListSubscriptionsAsync(targetRepository: repo, enabled: true);
   ```

2. **Outbound flows:** Subscriptions where `SourceRepository == repo`
   ```csharp
   var outbound = await _client.ListSubscriptionsAsync(sourceRepository: repo, enabled: true);
   ```

3. **Default channels:** Which channels this repo auto-assigns to:
   ```csharp
   var defaults = await _client.ListDefaultChannelsAsync(repository: repo);
   ```

4. **Compose graph:** Map subscriptions to directed edges (sourceRepo → targetRepo, labeled with channel).

**Output format:**
- Option A: Markdown graph (ASCII diagram)
- Option B: JSON structure with nodes/edges
- Option C: Mermaid/graphviz syntax

**Design notes:**
- If `direction = inbound`: show subscriptions targeting this repo + all their source repos
- If `direction = outbound`: show subscriptions originating from this repo + all their target repos
- If `direction = both`: union of inbound + outbound
- Optionally include channel names + health status per edge

**Recommendation:** **Implement in v0.3.** Core logic is straightforward; biggest decision is output format. Estimated: 2-3 dev days (Naomi) + 1 day format bikeshedding.

---

#### **6. `maestro_repo_flow_status` — Combined Health Endpoint**

**What**: Single-call health summary combining subscription health + build freshness + PR status.

**Assessment:**
- **Feasibility:** ✅ **Implementable** (low effort, composition of existing)
- **Complexity:** Small
- **PCS Support:** Full (reuses existing methods)
- **Dependencies:** GitHub PR status integration (external)
- **Agent:** Naomi (backend)

**Technical details:**

This is a **composite endpoint** reusing existing methods:
1. `GetSubscriptionHealthAsync(targetRepository)` → stale/up-to-date per subscription
2. `GetBuildFreshnessAsync(channel)` → last-modified timestamp per channel
3. **(External)** GitHub PR search → fetch open PRs for this repo authored by Maestro

**Return structure:**
```
{
  "repository": "dotnet/sdk",
  "health": {
    "subscriptions": [{id, channel, isStale, buildsBehind, ...}],
    "staleSummary": "2/5 subscriptions stale",
    "flows": {
      "inbound": [{sourceRepo, channel, freshness, prStatus, ...}],
      "outbound": [{targetRepo, channel, freshness, prStatus, ...}]
    }
  },
  "timestamp": "2026-02-19T12:34:56Z"
}
```

**Design notes:**
- Cache at service level: TTL ~5 min (short due to multi-source aggregation)
- Calls existing methods internally — minimal new code
- GitHub PR enrichment (is: open, author: maestro, repo: targetRepo) requires external API

**Recommendation:** **Implement in v0.3.** Easy to build once #5 (flow graph) and GitHub integration are done. Estimated: 1-2 dev days (Naomi).

---

### Priority 3: Nice to Have

#### **7. `maestro_vmr_source_manifest` — VMR Source Manifest Reader**

**What**: Read `src/source-manifest.json` from VMR at a given ref, parse it, return commit SHA mapping per product.

**Assessment:**
- **Feasibility:** 🟡 **Implementable but niche** (medium effort)
- **Complexity:** Medium
- **PCS Support:** None (external data source)
- **Dependencies:** GitHub REST API (read file), JSON parsing
- **Agent:** Naomi (backend) + (optional) Amos (tests)

**Technical details:**

1. **Input:** repo ref (e.g., `refs/heads/main`, `v10.0.0`)
2. **Fetch** `https://raw.githubusercontent.com/dotnet/vmr/{ref}/src/source-manifest.json`
3. **Parse** JSON schema (structure is Maestro-defined; Naomi to verify)
4. **Extract** commit mapping: `{ "runtime": "abc123...", "sdk": "def456...", ... }`
5. **Cache** with medium TTL (tags/stable refs: 24h; branches: 5 min)

**Design notes:**
- Requires HTTP client (can reuse existing pattern from `GetBuildFreshnessAsync`)
- Small niche use case — primarily for "trace which commit of X was used to build Y"
- Could be useful for conflict resolution or version tracking

**Recommendation:** **Backlog (v0.4+).** Low frequency; can be user-requested. Estimated: 1-2 dev days (Naomi) if needed.

---

#### **8. Channel Name Shorthand Resolution**

**What**: Accept short names (`net11`, `10.0.2xx`) and resolve to full Maestro channel names (`.NET 11.0.1xx SDK`).

**Assessment:**
- **Feasibility:** ✅ **Implementable** (trivial, one-liner)
- **Complexity:** Small
- **PCS Support:** Full (via `ListChannelsAsync`)
- **Dependencies:** None (composition of existing `GetChannelByNameAsync`)
- **Agent:** Naomi (backend) + Amos (tests)

**Technical details:**

Create a helper in `MaestroService`:
```csharp
public async Task<Channel?> ResolveChannelAsync(string shorthandOrFull, CancellationToken ct = default)
{
    // Try direct match first (full name or ID)
    if (Guid.TryParse(shorthandOrFull, out var id)) return await GetChannelAsync(id, ct);
    var exact = await GetChannelByNameAsync(shorthandOrFull, ct);
    if (exact != null) return exact;
    
    // Try shorthand mappings:
    // "net11" → ".NET 11.0.1xx SDK"
    // "10.0.2xx" → ".NET 10.0.2xx SDK"
    // etc.
    var mapping = new Dictionary<string, string>
    {
        { "net10", ".NET 10.0.1xx SDK" },
        { "net11", ".NET 11.0.1xx SDK" },
        // ... user-extensible
    };
    
    if (mapping.TryGetValue(shorthandOrFull.ToLowerInvariant(), out var fullName))
        return await GetChannelByNameAsync(fullName, ct);
    
    return null;
}
```

**Design notes:**
- Hardcoded mappings are fine; rarely change
- Could be environment-configurable via `MAESTRO_CHANNEL_SHORTCUTS` if needed
- Users would pass shorthand in any parameter that accepts channel names

**Recommendation:** **Implement in v0.2.1 alongside #3.** Trivial and improves UX. Estimated: 1 hour (Naomi) + 30 min tests (Amos).

---

#### **9. `maestro_build_assets` — List Build Assets**

**What**: List the assets (NuGet packages, blobs) produced by a build ID.

**Assessment:**
- **Feasibility:** 🟡 **Partially implementable** (PCS API gap)
- **Complexity:** Medium
- **PCS Support:** ⚠️ **Requires investigation**
- **Dependencies:** PCS client asset APIs (if available)
- **Agent:** Naomi (backend research)

**Technical details:**

Current `Build` model in PCS may or may not expose an `Assets` collection:
```csharp
// Check if Build.Assets exists
var build = await _client.GetBuildAsync(buildId);
var assets = build.Assets; // Does this exist?
```

**Possible implementations:**
1. **If `Build.Assets` exists:** Trivial — just expose it
2. **If separate endpoint:** `IAssets.ListAsync(buildId: id)` or similar (Naomi to discover)
3. **If not in PCS:** File feature request with Maestro team

**Recommendation:** **Defer pending investigation.** Assign Naomi to check PCS client API surface (1-2 hours). If available, implement in v0.2.1. If not, backlog.

---

## Implementation Roadmap

### v0.2.1 (Sprint 1) — *Estimated 1–2 weeks*

| Feature | Lead | Effort | Priority |
|---------|------|--------|----------|
| #3 Target branch filtering | Naomi | 2h | P1 |
| #8 Channel shorthand | Naomi | 1h | P3 (bundled) |
| #2 `maestro_force_trigger_subscription` | Naomi | 4h | P1 |
| Tests (#3, #8, #2) | Amos | 1-2h | P1 |
| **Subtotal** | — | ~10h | — |

**Blockers:** None. All use existing PCS APIs.

---

### v0.2.2 (Sprint 1.5) — *Pending investigation*

| Feature | Lead | Status |
|---------|------|--------|
| #4 `maestro_subscription_history` | Naomi | 🔍 **Investigate PCS API** |
| #9 `maestro_build_assets` | Naomi | 🔍 **Investigate PCS API** |

**Blockers:** PCS client surface discovery (Naomi, 1–2 hours).

---

### v0.3 (Sprint 2) — *Estimated 2–3 weeks*

| Feature | Lead | Effort | Notes |
|---------|------|--------|-------|
| #1 `maestro_codeflow_prs` | Naomi | 2-3d | Requires GitHub API integration |
| #5 `maestro_flow_graph` | Naomi | 2-3d | Composite of existing methods |
| #6 `maestro_repo_flow_status` | Naomi | 1-2d | Reuses #1 + #5 + subscriptions |
| Tests | Amos | 1-2d | Integration tests with GitHub mocks |
| **Subtotal** | — | ~2 weeks | — |

**Blockers:** GitHub API client integration (decision: use `Octokit`? GraphQL client? Naomi/Larry to decide).

---

### v0.4+ (Backlog) — *As-needed*

| Feature | Lead | Effort | Notes |
|---------|------|--------|-------|
| #7 `maestro_vmr_source_manifest` | Naomi | 1-2d | Niche use case |
| #4/#9 if PCS API unavailable | TBD | ? | Requires Maestro team changes |

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|-----------|
| PCS API missing `history`/`assets` | Medium | Blocks #4 + #9 | Naomi investigates immediately; backlog if missing |
| GitHub API rate limiting | Low | Blocks #1 + #5 | Cache PR results (5 min TTL) |
| Test coverage gaps (composite methods) | Medium | Release bugs | Amos writes integration tests; mock GitHub |
| User expectations on "force trigger" semantics | Medium | Support burden | Document `isCoherencyUpdate` behavior clearly |

---

## Design Decisions for Team

1. **GitHub API Client:** Which library for #1/#5/#6?
   - Option A: `Octokit` (REST client)
   - Option B: GraphQL client (lower bandwidth)
   - Option C: Shell out to `gh` CLI (simplest, but adds dependency)
   - **Recommendation:** Octokit (widely used, easy to integrate).

2. **`maestro_force_trigger_subscription` vs boolean parameter:**
   - Option A: Separate tool `maestro_force_trigger_subscription`
   - Option B: Add boolean param to existing `maestro_trigger_subscription`
   - **Recommendation:** Option A (clearer intent, less confusion).

3. **Channel shorthand strategy:**
   - Hardcoded mappings vs environment-configurable vs auto-detect from .NET version
   - **Recommendation:** Hardcoded in v0.2.1; accept environment override in v0.3 if requested.

4. **Output format for `maestro_flow_graph`:**
   - ASCII art / Markdown table / Mermaid graph / JSON
   - **Recommendation:** JSON (structured); Mermaid syntax is string bonus field.

---

## Questions for Larry

1. **Force-trigger semantics:** Does the current `maestro_trigger_subscription` already force-trigger (i.e., `isCoherencyUpdate: true`)? Or should normal triggers be non-force and we add a separate tool?
2. **GitHub API preference:** Any team preference on GitHub client library (Octokit, GraphQL, gh CLI)?
3. **VMR scope:** Is #7 (source-manifest parsing) likely to be heavily used, or should we defer?

---

## Summary

**All 9 features are architecturally sound and buildable.** No fundamental blockers. The roadmap prioritizes high-impact, low-effort wins (v0.2.1) before tackling composite/visualization features (v0.3). Two features (#4, #9) depend on PCS API availability and need immediate investigation.

**Next steps:**
1. Naomi investigates PCS API surface for history/assets (1–2 hours)
2. Team aligns on GitHub client strategy
3. Kickoff v0.2.1 implementation (target: 1 week)

---

## Appendix: Feature Summary Table

| # | Feature | Priority | Feasibility | Complexity | PCS Blocker? | Est. Days | v-Plan |
|---|---------|----------|-------------|-----------|-------------|----------|--------|
| 1 | `maestro_codeflow_prs` | P1 | ✅ | Medium | ❌ | 2-3 | v0.3 |
| 2 | `maestro_force_trigger_subscription` | P1 | ✅ | Small | ❌ | 0.5 | v0.2.1 |
| 3 | Target branch filtering | P1 | ✅ | Small | ❌ | 0.25 | v0.2.1 |
| 4 | `maestro_subscription_history` | P2 | 🟡 | Medium | ⚠️ | ? | v0.2.2 |
| 5 | `maestro_flow_graph` | P2 | ✅ | Medium | ❌ | 2-3 | v0.3 |
| 6 | `maestro_repo_flow_status` | P2 | ✅ | Small | ❌ | 1-2 | v0.3 |
| 7 | `maestro_vmr_source_manifest` | P3 | ✅ | Medium | ❌ | 1-2 | v0.4+ |
| 8 | Channel shorthand | P3 | ✅ | Small | ❌ | 0.1 | v0.2.1 |
| 9 | `maestro_build_assets` | P3 | 🟡 | Medium | ⚠️ | ? | v0.2.2 |

**Key:** ✅ Implementable, 🟡 Partially/needs investigation, ⚠️ Requires PCS API discovery

