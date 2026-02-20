# Design Review: AzDO API Client Auth & Interface Design

**Date:** 2026-02-20  
**Facilitator:** Holden (Lead/Architect)  
**Participants:** Naomi (Backend Dev), Amos (Tester)  
**Trigger:** Issue #5 — `subscription_health` shows misleading build count for AzDO repos  

## Problem Statement

`subscription_health` correctly reports commit distance for GitHub-hosted repos (via `IGitHubApiClient.CompareCommitsAsync`), but AzDO-hosted repos (e.g., `dnceng/internal/_git/dotnet-optimization`) fall back to BAR build ID deltas, producing misleading numbers like "~2722 builds behind."

## Decisions

### D1: Separate `IAzDoApiClient` interface (not unified)

**Decision:** Create `IAzDoApiClient` as a standalone interface parallel to `IGitHubApiClient`. Do NOT create a unified `ISourceControlClient`.

**Rationale:** GitHub's compare API returns rich data (ahead/behind/status). AzDO's commits API just returns a list you count. Different auth mechanisms, response shapes, and error modes. A unified interface would be lowest-common-denominator abstraction with no benefit for exactly two implementations.

**Interface:**
```csharp
public interface IAzDoApiClient
{
    Task<int?> GetCommitCountAsync(string org, string project, string repo, 
        string baseSha, string headSha, CancellationToken ct = default);
}
```

Returns `int?` — null on any failure. Matches the existing `CommitsBehind` field on `SubscriptionHealthResult`.

### D2: Auth cascade — `AZDO_TOKEN` → `az account get-access-token` → anonymous

**Decision:** Mirror the GitHub auth cascade pattern:
1. `AZDO_TOKEN` environment variable (PAT or token)
2. `az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798` (Azure CLI)
3. Anonymous fallback (will fail for internal repos like `dnceng/internal`)

**Rationale:** `AZDO_TOKEN` is the standard CI convention. Azure CLI is common for devs working with internal AzDO repos. Anonymous provides graceful degradation for public repos. This matches the existing `GITHUB_TOKEN → gh auth token → anonymous` pattern, reducing cognitive load.

**Amos's refinement (accepted):** Extract token acquisition into a small `IAzDoTokenProvider` with three implementations (EnvVar, AzCli, Anonymous). This isolates the subprocess call behind an interface, making auth cascade testable without running `az` in CI.

### D3: URL parsing — `ParseAzDoUrl` returns `(org, project, repo)?`

**Decision:** Static helper `ParseAzDoUrl` on `MaestroService`, returning `(string org, string project, string repo)?`. Handle both URL forms:
- Modern: `https://dev.azure.com/{org}/{project}/_git/{repo}`
- Legacy: `https://{org}.visualstudio.com/{project}/_git/{repo}`

Return null for non-matching URLs. Add corresponding `IsAzDoRepository` helper.

**Edge cases to handle:** trailing slashes, query parameters (e.g., `?version=GBmain`), repo names with dots.

### D4: Commit count cap — `$top=1000`, no pagination

**Decision:** Use AzDO API's `searchCriteria.$top=1000` parameter. Return the array length directly, capped at 1000. No pagination needed.

**Rationale:** If a subscription is 1000+ commits behind, the exact number is irrelevant — the subscription is very stale. Cap avoids unbounded API responses and pagination complexity. In practice, stale subscriptions are tens to low hundreds behind.

**Display note:** The MCP tool layer can show "≥1000" when the value equals 1000, without polluting the interface.

### D5: MaestroService integration — second optional constructor param

**Decision:** Add `IAzDoApiClient?` as a fourth optional parameter to `MaestroService`:
```csharp
public MaestroService(IMaestroApiClient client, CacheService cache,
    IGitHubApiClient? gitHubClient = null, IAzDoApiClient? azDoClient = null)
```

In `GetSubscriptionHealthAsync`, add an `else if` branch alongside the existing GitHub path:
```csharp
else if (_azDoClient != null && IsAzDoRepository(sub.SourceRepository))
{
    var parsed = ParseAzDoUrl(sub.SourceRepository);
    // ... same commit-fetch pattern, then:
    commitsBehind = await _azDoClient.GetCommitCountAsync(...);
}
```

Wire in both `Program.cs` registration sites (CLI + MCP containers).

**Rationale:** Optional param preserves backward compatibility — existing 3-arg constructor calls compile unchanged, and existing tests that don't inject an AzDO client continue to pass without modification.

### D6: Graceful degradation — return null, log to stderr

**Decision:** On any failure (401/403, timeout, parse error), return `null` from `GetCommitCountAsync`. Log actionable message to stderr:
```
[maestro-mcp] AzDO commits API auth failed for {org}/{project}/{repo} — set AZDO_TOKEN for internal repos
```

**Rationale:** `MaestroService` already treats `commitsBehind == null` as "fall back to build count approximation." No special error type needed. The stderr breadcrumb tells users how to fix auth without breaking the data flow.

## Test Plan (from Amos)

| Category | Test | What it verifies |
|---|---|---|
| Happy path | `AzDoSource_WithAzDoClient_ReturnsCommitsBehind` | End-to-end commit distance for AzDO repos |
| Cap | `AzDoSource_CommitsBehindCapped` | 1000-cap boundary |
| No client | `AzDoSource_NoAzDoClient_CommitsBehindIsNull` | Backward compat (renamed from existing) |
| API failure | `AzDoSource_AzDoClientReturnsNull_CommitsBehindIsNull` | Graceful degradation |
| URL parsing | 6 tests covering standard, legacy, GitHub, malformed, trailing slash, query params | `ParseAzDoUrl` correctness |
| Mixed | `MixedSubscriptions_UsesCorrectClient` | GitHub client for GitHub, AzDO for AzDO |
| Up-to-date | `AzDoSource_UpToDate_SkipsApiCall` | No API call when not stale |

**Regression:** Existing test at line 898 renamed to `AzDoSource_NoAzDoClient_CommitsBehindIsNull` — assertion unchanged, semantics clarified.

## Risks

1. **`dnceng/internal` auth**: Most real-world AzDO repos in .NET are internal. Without `AZDO_TOKEN` or Azure CLI login, the feature silently degrades. Acceptable — matches GitHub pattern.
2. **`az` CLI subprocess**: Same timeout/hang risk as `gh auth token`. Mitigated by: 5-second timeout + `IAzDoTokenProvider` extraction for testability.
3. **Static HttpClient token lifetime**: Token set once at class load. Acceptable for MCP subprocess sessions (short-lived). Same design as GitHub client.

## Action Items

| Who | What | Priority |
|---|---|---|
| Naomi | Implement `IAzDoApiClient` + `AzDoApiClient` with auth cascade | P1 |
| Naomi | Add `ParseAzDoUrl`/`IsAzDoRepository` helpers to `MaestroService` | P1 |
| Naomi | Wire into `GetSubscriptionHealthAsync` + `Program.cs` DI | P1 |
| Amos | Write test suite per matrix above | P1 |
| Amos | Rename existing AzDO test (line 898) for clarity | P1 |
| Holden | Review PR for interface consistency | P1 |
