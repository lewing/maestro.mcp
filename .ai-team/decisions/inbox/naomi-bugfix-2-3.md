# Bugfix: Issues #2 and #3

**Author:** Naomi (Backend Dev)
**Date:** 2025-07-16
**Status:** Implemented

## Issue #2: build_freshness SSRF allowlist expanded

**Problem:** `GetBuildFreshnessAsync` rejected `ci.dot.net` as an unexpected redirect domain. The aka.ms shortlinks for .NET channels now resolve there instead of only `*.blob.core.windows.net`.

**Fix:** Added two new entries to the SSRF domain allowlist in `MaestroService.cs`:
- `ci.dot.net` — exact host match (new Microsoft .NET build artifact domain)
- `*.azureedge.net` — suffix match (known Microsoft CDN for .NET builds, e.g. `dotnetbuilds.azureedge.net`)

**Rationale:** Both are legitimate Microsoft-owned domains used for .NET SDK/runtime build artifacts. The allowlist remains tight — only known Microsoft infrastructure domains are permitted.

## Issue #3: subscription_health resilience for high-subscription repos

**Problem:** `GetSubscriptionHealthAsync` iterated all subscriptions sequentially. If any single `GetLatestBuildAsync` call threw, the entire method failed with an unhandled exception. Repos like dotnet/sdk (59 subscriptions) were particularly vulnerable.

**Fix:**
1. Wrapped per-subscription logic in try/catch
2. Added `string? Error = null` optional parameter to `SubscriptionHealthResult` record
3. On exception: subscription added to results with error message, processing continues
4. MCP tool displays `⚠️ Error:` line for failed subscriptions

**Rationale:** Partial results are far more useful than a complete failure. One flaky API call shouldn't prevent the user from seeing health data for the other 58 subscriptions.

## Files Changed

- `src/MaestroTool.Core/MaestroService.cs` — Both fixes
- `src/MaestroTool.Core/MaestroMcpTools.cs` — Error display in subscription_health tool
