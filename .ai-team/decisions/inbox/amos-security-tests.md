# Decision: Security test coverage for threat model fixes

**Author:** Amos (Tester)
**Date:** 2025-07-15
**Status:** Complete

## Context

Naomi implemented 5 security fixes from the STRIDE threat model. I wrote 15 tests to validate those fixes plus 2 tests for a gap that remains open (buildId validation).

## Test inventory

| Fix | Tests | Status |
|-----|-------|--------|
| Fix 1: SSRF channel validation | 5 invalid + 4 valid channel names | All pass |
| Fix 2: Auth gating on triggers | 2 tests (subscription + daily update) | All pass |
| Fix 3: Dedup separation | Already covered by Naomi's tests | N/A |
| Fix 4: Stderr audit logging | 1 test (Console.Error capture) | Pass |
| Fix 5: Max cache entries | 1 test (10,001 entries) | Pass |
| Concurrency safety | 1 test (100 concurrent tasks) | Pass |
| Null parameter regression | 2 tests (null source/target repo) | Pass |
| BuildId validation (no fix yet) | 2 tests (0 and -1) | **Expected fail** |

## Decisions made

1. **buildId validation tests left failing** — These document the gap. When someone adds `ArgumentOutOfRangeException` for buildId <= 0 in `MaestroService.GetBuildAsync`, the tests will pass. Not blocking since the PCS API handles invalid IDs gracefully.

2. **Valid channel name tests use 5s timeout** — `GetBuildFreshnessAsync` makes real HTTP calls for valid channels. The CancellationToken prevents test hangs in offline environments. `OperationCanceledException` is caught and treated as pass (validation didn't reject the input).

3. **Concurrency test does not assert single factory call** — The check-then-set race in `GetOrAddAsync` means multiple threads may enter the factory. This is a known performance issue, not a security bug. The test only verifies data integrity (no corruption, no exceptions).

## Files changed

- `src/MaestroTool.Tests/CacheServiceTests.cs` — 2 new tests
- `src/MaestroTool.Tests/MaestroServiceTests.cs` — 13 new test methods (including Theory variants)
