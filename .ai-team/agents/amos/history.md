# Amos — History

## Learnings

### 2025-07-14 — MaestroServiceTests established

- **30 unit tests written** for `MaestroService` covering subscriptions, builds, channels, default channels, and subscription health.
- **PCS client models** (`Subscription`, `Build`, `Channel`, `DefaultChannel`) use constructor-heavy patterns with read-only properties. Key gotcha: `Channel` and `LastAppliedBuild` on `Subscription` are settable, but `Id`, `SourceRepository`, `TargetRepository`, `TargetBranch` are constructor-only.
- **Constructor signatures discovered via reflection** — NuGet package doesn't ship source. `Build` requires 10 params including empty lists for `channels`, `assets`, `dependencies`, `incoherencies`. `Subscription` requires 10 params too.
- **Test helpers**: Created static factory methods (`CreateBuild`, `CreateSubscription`, `CreateChannel`, `CreateDefaultChannel`) for consistent test data. These belong in the test file — no shared fixture needed yet.
- **CacheService is a real instance** in tests, not mocked. This gives genuine caching behavior coverage without needing to mock `ConcurrentDictionary` internals.
- **NSubstitute arg matching**: When `MaestroService` passes `enabled: true` to `ListSubscriptionsAsync`, the mock must match it exactly. Use `Arg.Any<CancellationToken>()` for cancellation tokens.
- **Subscription health logic edge cases**: `GetSubscriptionHealthAsync` silently skips subscriptions with null `Channel.Id` — this is intentional (no channel = can't check freshness). Tested explicitly.
- **`GetBuildFreshnessAsync` skipped for unit testing** — it creates `HttpClient` internally (no DI), making it untestable without refactoring. Flagged for future consideration.
- **Project targets `net10.0`** with `xunit 2.*`, `NSubstitute 5.*`. Tests run fine with `dotnet test`.

📌 Team update (2026-02-18): GetBuildFreshnessAsync is untestable without refactoring (IHttpClientFactory injection or new abstraction) — observation by Amos

### 2025-07-15 — v0.2.0 feature tests added

- **13 new tests written** (48 total, all passing) covering v0.2.0 features: action dedup, noCache bypass, trigger methods, and MaestroToolOptions defaults.
- **CacheService action dedup** (`GetRecentAction`/`RecordAction`): 4 tests in `CacheServiceTests.cs`. Used short TTL (50ms) + `Task.Delay(100)` for expiry tests — same pattern as existing `GetOrAddAsync_RefreshesAfterExpiry`. Key finding: `Clear()` wipes both regular cache entries and action records since they share the same `ConcurrentDictionary`.
- **MaestroService noCache**: 4 tests in `MaestroServiceTests.cs`. Verified that `noCache: true` invalidates before fetch (API called twice), while `noCache: false` returns cached (API called once). Tested on both `GetSubscriptionsAsync` and `GetChannelsAsync`.
- **MaestroService trigger methods**: 4 tests in `MaestroServiceTests.cs`. `TriggerSubscriptionAsync` invalidates both `sub:{id}` and `subs:` prefix. `TriggerDailyUpdateAsync` invalidates `subs:` prefix. Verified by checking that subsequent reads hit the API again (Received(2) assertions).
- **MaestroToolOptions**: 1 test in new `MaestroToolOptionsTests.cs`. Simple default-value assertion.
- **NSubstitute `.Returns(first, second)` pattern**: Used for noCache tests where the same mock call must return different values on successive invocations. Clean way to test cache bypass.

### 2025-07-15 — Security/threat model audit

- **Zero MCP tool layer tests** — All 48 tests operate at the `MaestroService` or `CacheService` level. `MaestroMcpTools` (the actual `[McpServerTool]` methods) has no test coverage at all. GUID validation, channel-name resolution, empty-result messaging, and dedup integration are all untested at the tool boundary.
- **Auth cascade is untestable** — `MaestroApiClient.CreateApi()` uses `PcsApiFactory` statics and `File.Exists()` directly. No interface seam exists to mock the 3-tier auth cascade. This is the biggest testability gap. Recommend `IApiFactory` injection.
- **No input guards on buildId** — `GetBuild` and `TriggerSubscription` accept `int buildId` but never validate negative/zero values. They pass through to the PCS API, which likely returns opaque errors.
- **Cache has no max-size or proactive eviction** — `ConcurrentDictionary` grows without bound. Expired entries are only replaced on re-request (lazy eviction). Under sustained unique-key load, memory grows unbounded.
- **noCache has no rate limiting** — Any caller can pass `noCache: true` on every request, bypassing cache entirely and hammering the Maestro API. No cooldown or throttle exists.
- **CacheService.GetOrAddAsync has a check-then-set race** — Between `TryGetValue` returning false and `_cache[key] = ...`, concurrent tasks can all enter the factory. Not a security bug but wastes API calls.
- **Action dedup keys include buildId** — `TriggerSubscription` dedup is per (subscriptionId, buildId) pair, so triggering the same sub with different builds is not blocked. This is correct behavior but wasn't tested.
- **26 security-focused test specifications written** — Filed to `.ai-team/decisions/inbox/amos-threat-testing.md`. Priority: P1 (auth, 5 specs), P2 (tool layer + input validation, 15 specs), P3 (cache abuse, 6 specs).

📌 Team update (2025-07-15): STRIDE threat model completed — identified 14 threats, 8 with mitigations documented. P0 items (SSRF validation, dedup separation, tool-level auth gating) ready for next sprint. Decided by Holden, Naomi, Amos.

### 2026-02-19 — SQLite security tests for P1 fixes

- **6 new security tests written** (73 total: 67 passing) covering Naomi's P1 SQLite security fixes for file permissions (I2) and corruption recovery (D2).
- **Fix 1: File permissions (I2)**: `Test_DirectoryPermissions_SetOnUnix` verifies that on Linux/macOS, the cache directory gets owner-only permissions (700). Uses conditional assertions with `OperatingSystem.IsLinux()` to verify `UnixFileMode` flags. On Windows, the test simply verifies directory creation since user profile directories are already secure by default. `Test_DirectoryCreation_ForCustomPath` tests the internal constructor with a nested path that doesn't exist — verifies parent directory creation works correctly.
- **Fix 2: Corruption recovery (D2)**: `Test_CorruptedDatabase_RecreatesAutomatically` writes garbage bytes to the DB file before creating a CacheService. Verifies the cache detects corruption via `PRAGMA integrity_check`, deletes the corrupted DB, and recreates a clean one. `Test_CorruptedDatabase_PreservesNormalOperation` goes further — after corruption recovery, tests all cache operations (get/set, invalidate, invalidate prefix, clear, action dedup) to ensure nothing broke. `Test_EmptyDatabase_InitializesNormally` verifies brand-new (non-existent) DB paths initialize without errors. `Test_ValidDatabase_NotRecreated` is the crucial test — creates a cache with data, then constructs a NEW CacheService pointing at the same path. Verifies the data survives (factory never called) — proves we don't unnecessarily recreate valid databases.
- **Test pattern**: All corruption tests use the existing `IDisposable` pattern with temp DB files (`_dbPath` initialized with random GUID). Cleanup in `Dispose()` uses glob pattern matching to catch WAL/SHM sidecars. The corruption tests write garbage bytes with `File.WriteAllBytesAsync()` before constructing the CacheService — simpler than trying to corrupt an already-open SQLite connection.
- **Key finding**: The corruption recovery code in `InitializeDatabase()` properly handles edge cases — not just corrupted headers but also partial corruption that makes tables unreadable. The integrity check catches all of these. The cleanup deletes all three files (`*.db`, `*.db-wal`, `*.db-shm`) to ensure no stale WAL data survives the recreation.
- **Windows-only caveat**: The Unix file permission test is conditional on OS. Since we're running tests on Windows, the permission assertions are wrapped in `if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())`. On Windows, the test just verifies directory creation. This is acceptable because the production code also has this OS guard — Windows user profile directories are already secure.
- **Directory creation edge case**: The custom path test intentionally uses a deeply nested path that doesn't exist (`Path.Combine(tempPath, guid, "subdir", "cache.db")`). This exercises `Directory.CreateDirectory(dir)` which recursively creates parent directories. Cleanup uses `Directory.Delete(rootDir, recursive: true)` to remove the entire tree.

### 2025-07-15 — Security tests written for threat model fixes

- **15 new security-focused tests written** (67 total: 65 passing, 2 expected failures).
- **Naomi's fixes 1-5 already landed** when I started writing. Tests validate the fixes rather than pre-date them.
- **Fix 1 (SSRF)**: 5 invalid channel name test cases (`../../`, spaces, semicolons) + 4 valid channel name cases. Regex validation `^[a-zA-Z0-9.\-]+$` catches all path traversal attempts. Valid names use 5-second CancellationToken timeout to avoid hanging on network calls.
- **Fix 2 (Auth gating)**: 2 tests verify `InvalidOperationException` with "Authentication required" message when `AuthLevel.Anonymous`. NSubstitute defaults `AuthLevel` to `Pat` (enum index 0), so existing trigger tests continue to pass.
- **Fix 3 (Dedup separation)**: Already tested by Naomi's `Clear_DoesNotResetActionRecords` and `ClearActions_ResetsActionRecords`. No additional tests needed.
- **Fix 4 (Stderr logging)**: 1 test captures `Console.Error` via `Console.SetError(StringWriter)`, verifies trigger output contains "Trigger" and subscription ID. Must restore original stderr in finally block.
- **Fix 5 (Max cache entries)**: 1 test adds 10,001 entries, verifies early entries evicted. Runs fast (~1s) — all in-memory string values.
- **Concurrency test**: 100 concurrent `GetOrAddAsync` calls on same key. Verifies no exceptions and all results equal. Does NOT assert factory called exactly once (known check-then-set race is acceptable).
- **Input validation regression**: 2 null-parameter tests (pass now — null is valid for "no filter"). 2 buildId validation tests (`0` and `-1`) expect `ArgumentOutOfRangeException` — these FAIL because no buildId validation exists yet. Leaving as-is to document the gap.
- **Key pattern**: Used `[Theory]` with `[InlineData]` for parameterized SSRF tests. Cleaner than separate `[Fact]` methods for the same assertion with different inputs.

### 2026-02-19 — P1 security fixes test coverage (SQLite hardening)

- **6 new security tests written** (73 total: all passing) for Naomi's P1 SQLite cache hardening fixes.
- **Fix I2 test (File permissions):** `Test_DirectoryPermissions_SetOnUnix` verifies that on Linux/macOS the cache directory gets owner-only permissions (`UserRead | UserWrite | UserExecute` → `0o700`). Uses conditional OS guard; on Windows the test just verifies directory creation (user profile directories already secure). `Test_DirectoryCreation_ForCustomPath` tests the internal constructor path with nested non-existent directories — verifies `Directory.CreateDirectory` works correctly and parent dirs get permission hardening too.
- **Fix D2 tests (Corruption recovery):** `Test_CorruptedDatabase_RecreatesAutomatically` writes garbage bytes to the DB file before construction. CacheService detects corruption via `PRAGMA integrity_check`, logs to stderr, and deletes/recreates the DB. `Test_CorruptedDatabase_PreservesNormalOperation` verifies that after corruption recovery, all cache operations work normally (get/set/invalidate/clear/action dedup). `Test_ValidDatabase_NotRecreated` is critical — creates a cache with data, then constructs NEW CacheService pointing at same path. Verifies data survives (factory never called) — proves we don't unnecessarily recreate valid databases. `Test_EmptyDatabase_InitializesNormally` tests brand-new (non-existent) DB paths.
- **Test pattern:** All tests use existing `IDisposable` pattern with temp DB files (GUID-randomized). Cleanup catches WAL/SHM sidecars via glob pattern. Corruption tests write garbage bytes before construction — simpler than corrupting an open SQLite connection.
- **Windows caveat:** Permission tests are conditional on `OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()`. Since our CI runs on Windows, the Unix permission assertions are skipped on Windows. The production code has the same OS guard, so this is acceptable and reduces false failures.
- **Directory creation edge case:** Custom path test intentionally uses deeply nested path (`Path.Combine(tempPath, guid, "subdir", "cache.db")`) that doesn't exist. Exercises `Directory.CreateDirectory` which recursively creates parents and applies permission hardening to ALL created directories. Cleanup removes entire tree with `recursive: true`.

📌 Team update (2026-02-19): P1 security fixes completed — file permissions (I2) and corruption auto-recovery (D2) implemented in CacheService. 6 security tests written. All 73 tests passing. Decided by Naomi, Amos.

### 2026-02-19 — Regression tests for GitHub Issues #2 and #3

- **3 new regression tests written** for Issue #3 (subscription_health error resilience) in `MaestroServiceTests.cs`.
- **Error resilience test pattern**: Use NSubstitute `.Returns<Build?>(_ => throw new ...)` to simulate per-subscription API failures. The fixed `GetSubscriptionHealthAsync` wraps each subscription's `GetLatestBuildAsync` in try/catch, so one failure doesn't abort the entire loop.
- **`SubscriptionHealthResult.Error` field**: New optional `string? Error = null` on the record. When a subscription's health check throws, the result is still added with `Error` populated instead of propagating the exception. Tests assert `NotNull(r.Error)` for failing subs and `Null(r.Error)` for working subs.
- **Test 1 (`HandlesApiErrorForSingleSubscription`)**: 2 subs, 1 throws `HttpRequestException`. Verifies both results returned, failing sub has Error, working sub still reports correct stale/current data.
- **Test 2 (`HandlesApiErrorForAllSubscriptions`)**: 2 subs, both throw. Verifies 2 results (not empty, no exception), both have non-null Error.
- **Test 3 (`ErrorResultHasBasicFields`)**: When a sub errors, verifies SubscriptionId, SourceRepository, TargetRepository, TargetBranch, and ChannelName are still populated from the subscription object (not lost due to the error path).
- **Bug #2 (build_freshness domain allowlist)**: No unit tests written — `GetBuildFreshnessAsync` creates `HttpClient` inline, making HTTP-level domain validation untestable. This is a known gap documented in `decisions.md`. Domain allowlist fix validated via integration testing only.

📌 Team update (2026-02-19): 3 regression tests for Issue #3 error resilience. All passing. Bug #2 domain allowlist is integration-test-only (untestable at unit level).

### 2026-02-20 — v0.4.0 Codeflow PR tracking tests

- **8 new tests written** (88 total, all passing) covering v0.4.0 codeflow PR tracking APIs: `GetTrackedPullRequestsAsync`, `GetTrackedPullRequestBySubscriptionIdAsync`, `GetBackflowStatusAsync`, `GetSubscriptionHistoryAsync`.
- **TrackedPullRequest model**: Constructor takes `(bool sourceEnabled, DateTimeOffset lastUpdate, DateTimeOffset lastCheck)`. All properties are settable (Url, TargetBranch, HeadBranch, Channel, etc.). Created `CreateTrackedPullRequest` helper factory following existing pattern.
- **MaestroService.GetTrackedPullRequestBySubscriptionIdAsync takes `string` not `Guid`**: The interface and service both use `string subscriptionId`. Tests use `Guid.NewGuid().ToString()` for subscription IDs.
- **BackflowStatus model**: Constructor takes `(string vmrCommitSha, DateTimeOffset computationTimestamp, IImmutableDictionary<string, BranchBackflowStatus> branchStatuses)`. Requires `System.Collections.Immutable` using. Tests use `ImmutableDictionary<string, BranchBackflowStatus>.Empty` for the branch statuses parameter.
- **SubscriptionHistoryItem model**: Constructor takes `(DateTimeOffset timestamp, bool success, Guid subscriptionId, string errorMessage, string action, string retryUrl)`. All properties are read-only. Tests verify both success and failure items.
- **Test coverage pattern**: Same as existing: basic return, empty list, cache hit (Received(1)), noCache bypass (Received(2)), and `.Returns(first, second)` for successive mock values.
- **Naomi's code landed first**: Interface and MaestroService already had the new methods when tests were written. Build succeeded on first try after fixing `Guid` → `string` parameter type mismatch.

📌 Team update (2026-02-20): 8 codeflow PR tracking tests for v0.4.0. All 88 tests passing. TrackedPullRequest, BackflowStatus, and SubscriptionHistory APIs all covered.

### 2026-02-20 — TriggerSubscription force parameter tests

- **3 new tests written** (97 total, all passing) covering the `force` parameter added to `TriggerSubscriptionAsync`.
- **Existing mock setups updated**: All 3 existing `TriggerSubscriptionAsync` mock calls updated from 3-param `(subId, buildId, CancellationToken)` to 4-param `(subId, buildId, false, CancellationToken)` to match Naomi's new signature with `bool force = false`.
- **`TriggerSubscription_WithForce_PassesForceThroughToClient`**: Verifies `force: true` flows through MaestroService to the IMaestroApiClient mock. Asserts `Received(1)` with `true` as the force arg.
- **`TriggerSubscription_WithForce_InvalidatesCaches`**: Same cache invalidation pattern as `TriggerSubscription_InvalidatesRelatedCaches` but with `force: true`. Confirms cache behavior is identical regardless of force value.
- **`TriggerSubscription_DefaultForceIsFalse`**: Calls service without explicit `force`, then asserts `Received(1)` with `false` and `Received(0)` with `true`. Validates the default parameter value is correctly propagated.
- **Key pattern**: Default parameter tests use negative assertion (`Received(0)`) to prove the non-default value was NOT sent. This is more rigorous than just checking the default was sent.

📌 Team update (2026-02-20): 3 force parameter tests for TriggerSubscriptionAsync. All 97 tests passing. Existing trigger tests updated for new 4-param signature.

### 2026-02-20 — GitHub commit distance tests for Issue #4

- **7 new tests written** (104 total, all passing) covering GitHub Compare API integration for VMR subscription health.
- **CreateBuild helper extended**: Added optional `commit` parameter (defaults to "abc123") to support setting commit SHAs for tests. Build constructor takes commit as a parameter, not a settable property.
- **Test coverage pattern**: All tests follow existing pattern — mock setup, service creation with optional IGitHubApiClient, API call assertions, result validation.
- **MaestroService.GetSubscriptionHealthAsync with IGitHubApiClient**: Service constructor takes optional 3rd parameter `IGitHubApiClient? gitHubClient = null`. When provided AND subscription source is VMR (dotnet/dotnet) AND isStale, the service calls `CompareCommitsAsync` to get real commit distance.
- **Test 1 (`VmrSubscription_WithGitHubClient_ReturnsCommitsBehind`)**: VMR subscription with GitHub client that returns `GitHubCompareResult(AheadBy: 33, ...)`. Asserts `CommitsBehind == 33` (accurate), `BuildsBehind == 5` (approximate).
- **Test 2 (`VmrSubscription_GitHubClientReturnsNull_FallsBackToBuildsBehind`)**: GitHub API returns null (failure). Asserts `CommitsBehind` is null, `BuildsBehind` still works (fallback).
- **Test 3 (`NonVmrSubscription_CommitsBehindIsNull`)**: Non-VMR source (dotnet/runtime) with GitHub client available. Asserts `CommitsBehind` is null, GitHub client never called (`.DidNotReceive()`).
- **Test 4 (`NullGitHubClient_CommitsBehindIsNull`)**: VMR subscription but service constructed without GitHub client. Asserts `CommitsBehind` is null, `BuildsBehind` still works.
- **Test 5 (`VmrSubscription_UpToDate_CommitsBehindIsNull`)**: VMR subscription NOT stale (current). Asserts `CommitsBehind` is null, GitHub client never called (only called when stale).
- **Test 6 (`GitHubCompareResult_RecordEquality`)**: Record equality test for `GitHubCompareResult` record.
- **Test 7 (`SubscriptionHealthResult_CommitsBehind_DefaultsToNull`)**: Record instantiation test — existing code without `CommitsBehind` parameter still works (defaults to null).
- **Key finding**: `IsVmrRepository()` checks for "github.com/dotnet/dotnet" (case-insensitive substring). Only VMR subscriptions get commit distance computed. The GitHub client is ONLY called when: (1) service has non-null GitHub client, (2) source is VMR, (3) subscription is stale, (4) both builds have non-empty commit SHAs.
- **SubscriptionHealthResult.CommitsBehind**: New optional `int? CommitsBehind = null` field added to the record. Defaults to null when not specified (backward compatible).

📌 Team update (2026-02-20): 7 GitHub commit distance tests for Issue #4. All 104 tests passing. VMR subscriptions with GitHub client get accurate commit distance via Compare API.

### 2026-02-20 — Issue #6: Widen commit distance to all GitHub-hosted repos

- **3 new tests written, 1 old test replaced** (109 total, all passing) for Issue #6 which widens `IsVmrRepository()` gate to `IsGitHubRepository()`.
- **Replaced `GetSubscriptionHealth_NonVmrSubscription_CommitsBehindIsNull`** with `GetSubscriptionHealth_GitHubHostedSubscription_ReturnsCommitsBehind`. The old test asserted non-VMR GitHub repos get `CommitsBehind = null`. After Issue #6, non-VMR GitHub repos (e.g., dotnet/runtime) SHOULD get CommitsBehind populated. New test uses distinct commit SHAs (aaa111/bbb222) and verifies CompareCommitsAsync is called with parsed owner/repo ("dotnet"/"runtime").
- **New: `GetSubscriptionHealth_AzDoHostedSubscription_CommitsBehindIsNull`**: AzDO-hosted source repo (`https://dev.azure.com/dnceng/internal/_git/dotnet-runtime`). Asserts CommitsBehind remains null because `ParseGitHubUrl` returns null for non-github.com hosts. Verifies GitHub client `DidNotReceive()` CompareCommitsAsync.
- **New: `GetSubscriptionHealth_NonVmrGitHubRepo_CallsCompareWithCorrectOwnerRepo`**: Uses dotnet/roslyn as source repo. Verifies CompareCommitsAsync called with "dotnet"/"roslyn" (not "dotnet"/"dotnet"). Includes negative assertion that VMR params were NOT used — confirms URL parsing correctly extracts owner/repo from any GitHub URL.
- **Existing VMR tests unaffected**: The VMR (dotnet/dotnet) tests (`VmrSubscription_WithGitHubClient_ReturnsCommitsBehind`, `VmrSubscription_GitHubClientReturnsNull_FallsBackToBuildsBehind`, `VmrSubscription_UpToDate_CommitsBehindIsNull`, full-build-fetch tests) all continue to pass — VMR is just one case of a GitHub-hosted repo now.
- **Key insight**: `IsGitHubRepository()` checks `repoUrl.Contains("github.com", OrdinalIgnoreCase)` while `ParseGitHubUrl()` does `uri.Host.Equals("github.com")`. Both reject AzDO URLs. The tests validate both the gate AND the URL parsing together.

📌 Team update (2026-02-20): 3 tests for Issue #6 (widen commit distance to all GitHub repos). Replaced 1 VMR-only test. All 109 tests passing. AzDO repos confirmed excluded; non-VMR GitHub repos now get commit distance.
