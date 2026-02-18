# Decision: v0.2.0 Test Coverage Patterns

**Author:** Amos (Tester)
**Date:** 2025-07-15
**Status:** Complete

## Context

Added 13 unit tests for v0.2.0 features (action dedup, noCache, triggers, options). Total test count is now 48, all passing.

## Key patterns established

1. **Action dedup tests** use the same short-TTL + `Task.Delay` approach as existing cache expiry tests. No need for time abstraction — 50ms cooldown with 100ms delay is reliable.

2. **noCache bypass tests** use NSubstitute's `.Returns(firstValue, secondValue)` to verify the API is called again after invalidation. Two methods tested (subscriptions + channels) to prove the pattern works across the service.

3. **Trigger cache invalidation** is verified indirectly: populate cache → trigger → read again → assert `Received(2)` on the API mock. This proves the trigger methods properly invalidate related cache keys.

4. **New test file** `MaestroToolOptionsTests.cs` for options defaults. Kept separate because it doesn't need the MaestroService test fixture.

## Files changed

- `src/MaestroTool.Tests/CacheServiceTests.cs` — 4 new action dedup tests
- `src/MaestroTool.Tests/MaestroServiceTests.cs` — 8 new tests (4 noCache + 4 trigger)
- `src/MaestroTool.Tests/MaestroToolOptionsTests.cs` — 1 new test (new file)
