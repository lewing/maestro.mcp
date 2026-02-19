# Decision: Force trigger as optional parameter

**Author:** Naomi (Backend Developer)
**Date:** 2025-07-16
**Scope:** `maestro_trigger_subscription` MCP tool

## Decision
Added `force` as an optional boolean parameter (`default: false`) to the existing `maestro_trigger_subscription` tool rather than creating a separate `maestro_force_trigger_subscription` tool.

## Rationale
- Keeps the tool surface area small — one tool, one concept (trigger), with a modifier flag.
- The PCS client already has the `isCoherencyUpdate` boolean on `TriggerSubscriptionAsync`. When `force=true`, we pass `true` to `isCoherencyUpdate`, which overwrites the existing PR branch with fresh VMR content.
- Dedup keys include the force flag, so `trigger(sub, build, force=false)` and `trigger(sub, build, force=true)` are tracked independently.

## Impact
- **All 4 layers modified:** `IMaestroApiClient`, `MaestroApiClient`, `MaestroService`, `MaestroMcpTools`
- **Backward compatible:** `force` defaults to `false`, so existing callers are unaffected.
- **Tests:** Build passes with 0 warnings, 0 errors. Existing tests that call `TriggerSubscriptionAsync` without `force` param will continue to work due to default value.
