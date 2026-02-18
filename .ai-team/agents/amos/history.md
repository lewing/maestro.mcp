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
