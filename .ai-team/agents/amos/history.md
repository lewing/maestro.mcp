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
