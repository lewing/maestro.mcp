### 2026-02-20: AzDO API client auth and interface design
**By:** Holden (with input from Naomi, Amos)

**What:**
1. **Separate `IAzDoApiClient` interface** with `Task<int?> GetCommitCountAsync(org, project, repo, baseSha, headSha, ct)` — parallel to `IGitHubApiClient`, not unified.
2. **Auth cascade**: `AZDO_TOKEN` env var → `az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798` → anonymous. Token acquisition extracted into `IAzDoTokenProvider` for testability.
3. **URL parsing**: `ParseAzDoUrl` returns `(org, project, repo)?`, handles both `dev.azure.com/{org}/{project}/_git/{repo}` and `{org}.visualstudio.com/{project}/_git/{repo}` legacy format.
4. **Commit cap**: `$top=1000`, no pagination. Array length = commit count, capped at 1000.
5. **Integration**: `IAzDoApiClient?` as optional 4th param to `MaestroService` constructor. Sibling `else if` branch in `GetSubscriptionHealthAsync`.
6. **Degradation**: Return `null` on any failure, log actionable stderr message. Existing null-handling in `SubscriptionHealthResult` covers fallback.

**Why:**
- Separate interface avoids lowest-common-denominator abstraction between APIs with different capabilities (GitHub compare is richer than AzDO commit listing).
- Auth cascade mirrors the proven `GitHubApiClient` pattern. `IAzDoTokenProvider` extraction is the key addition — it isolates subprocess calls for testability and prevents CI flake from missing `az` CLI.
- Optional constructor param guarantees backward compatibility: all existing tests and 3-arg call sites continue to work unchanged.
- 1000-cap eliminates pagination complexity for an informational metric. If you're 1000+ commits behind, the exact number doesn't matter.
- `int?` return type aligns directly with the existing `CommitsBehind` field on `SubscriptionHealthResult`, requiring no model changes.
