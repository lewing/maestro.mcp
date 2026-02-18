### 2026-02-18: Action tools implementation for v0.2.0

**By:** Naomi (Backend Dev)

**What:** Implemented non-destructive action tools (`maestro_trigger_subscription`, `maestro_trigger_daily_update`) with deduplication, cache invalidation, and future-proofed config for destructive actions.

**Why:** Users need the ability to trigger subscriptions and daily updates programmatically via MCP tools. Action deduplication prevents accidental duplicate triggers (2-minute cooldown). Cache invalidation ensures subsequent read queries don't return stale data after mutations. The `MaestroToolOptions` config class prepares the codebase for future destructive tools (delete, update) that will require explicit opt-in via env var.

**Key Technical Details:**

- **PCS Client Method Signature Discovery**: `ISubscriptions.TriggerSubscriptionAsync` has signature `(int barBuildId, bool isCoherencyUpdate, Guid subscriptionId, CancellationToken)`. The bool parameter controls coherency mode; passing `true` enables standard trigger behavior.

- **Action Deduplication Pattern**: `CacheService.GetRecentAction(key)` returns timestamp if action was executed within cooldown period; `RecordAction(key, cooldown)` stores execution timestamp. Dedup keys follow pattern `action:trigger-sub:{subscriptionId}:{buildId}` for subscription triggers and `action:trigger-daily-update` for daily updates.

- **Cache Invalidation Strategy**: Action methods in `MaestroService` call API client, then invalidate related read caches. `TriggerSubscriptionAsync` invalidates `sub:{subscriptionId}` and prefix `subs:*`. `TriggerDailyUpdateAsync` invalidates all subscription caches (`subs:*`). This prevents stale data from being served after mutations.

- **Config for Future Destructive Actions**: `MaestroToolOptions.EnableDestructiveActions` (default: false) is registered in DI and read from `MAESTRO_ENABLE_DESTRUCTIVE_ACTIONS` env var. v0.2.0 does not expose destructive tools yet — this is prep work for future delete/update operations.

- **Tool Design**: Both action tools return user-friendly confirmation messages with relevant context (subscription details, build ID). The 2-minute cooldown prevents accidental re-triggers while still allowing intentional retries after a reasonable delay.

**Files Changed:**
- Created `src/MaestroTool.Core/MaestroToolOptions.cs` (config class)
- Updated `src/MaestroTool/Program.cs` (register options, version bump to 0.2.0)
- Updated `src/MaestroTool.Mcp/Program.cs` (register options, version bump to 0.2.0)
- Updated `src/MaestroTool.Core/IMaestroApiClient.cs` (add action methods)
- Updated `src/MaestroTool.Core/MaestroApiClient.cs` (implement action methods)
- Updated `src/MaestroTool.Core/CacheService.cs` (add `GetRecentAction`, `RecordAction`)
- Updated `src/MaestroTool.Core/MaestroService.cs` (add service layer action methods with cache invalidation)
- Updated `src/MaestroTool.Core/MaestroMcpTools.cs` (add `maestro_trigger_subscription`, `maestro_trigger_daily_update` tools, inject options and cache service)

**Impact:** Maestro MCP server now supports programmatic triggering of subscription processing and daily updates. Version bumped to 0.2.0. All changes compile successfully with dotnet build.
