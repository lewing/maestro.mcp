# Decision: `maestro_channels` filter parameters

**Author:** Naomi  
**Date:** 2026-05-22  
**Status:** Proposed / shipped for review

## Context

`maestro_channels` previously returned all channels as bulleted markdown. Most callers need a narrow name match or a classification-constrained list before using a channel ID.

## Decision

Add optional parameters to `maestro_channels`:

- `filter`: case-insensitive substring match on channel name.
- `classification`: passed through to PCS `IChannels.ListChannelsAsync(classification, cancellationToken)`.
- `compact`: bool flag returning `name → id` lines instead of bulleted markdown.

No-argument calls preserve the existing full bulleted list. `classification` gets a distinct cache entry; `filter` is applied after cache retrieval to avoid creating API/cache churn for ad hoc substring searches.

## Deferred

Do not add pagination. Channels is a small hierarchical dataset and previous review rejected forcing markdown tool output into `LimitedResults<T>`. Similar filter parameters for `default_channels` and `subscriptions` are deferred because those tools already have natural API filters and were outside this focused PR.
