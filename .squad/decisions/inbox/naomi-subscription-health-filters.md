# Subscription Health Filtering and Compact Output

**Author:** Naomi  
**Date:** 2026-05-22  
**Status:** Proposed

## Decision

Add opt-in filtering and compact rendering directly to the `maestro_subscription_health` MCP tool:

- `staleOnly` omits healthy subscriptions.
- `channelFilter` matches channel names case-insensitively.
- `sourceRepoFilter` matches source repository URLs or short repo names case-insensitively.
- `compact` renders one line per subscription for low-token scanning.

## Rationale

`maestro_subscription_health` is expensive because it fans out across every active subscription and can produce very large markdown output. PR #20 already fixed wall-clock latency with parallel fan-out; this decision preserves that by applying filters only after health results are computed. The main user workflow is finding broken subscriptions, so `staleOnly + compact` should be the default recommendation for broad repo health triage.

## Compatibility

All knobs are optional. Existing no-argument callers keep the detailed markdown output unchanged.

## Measurement

For live `https://github.com/dotnet/dotnet` data, the detailed formatter produced 24,398 bytes for 93 subscriptions / 43 stale. `staleOnly + compact` produced 3,049 bytes, about an 87.5% reduction.
