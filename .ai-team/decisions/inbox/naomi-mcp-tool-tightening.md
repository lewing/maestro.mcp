# Decision: MCP Tool Description Tightening and Usability Improvements

**Author:** Naomi (Backend Dev)
**Date:** 2025-07-19
**Status:** Implemented

## Context

Holden's MCP tool audit identified several improvements to reduce token waste and improve agent routing accuracy. This implements P0 and P1 items.

## Decisions

### 1. Remove "Returns..." from tool descriptions (P0)

Removed "Returns X, Y, Z" sentences from 8 tool descriptions. Agents see the actual response — listing return fields wastes tokens and clutters routing. Affected tools: `maestro_subscriptions`, `maestro_latest_build`, `maestro_build`, `maestro_builds`, `maestro_channel`, `maestro_channels`, `maestro_codeflow_prs`, `maestro_codeflow_pr`.

### 2. Cross-reference overlapping tools (P1-M4)

Added cross-references to help agents pick the right tool:
- `maestro_subscriptions` → points to `maestro_subscription_health` and `maestro_subscription`
- `maestro_subscription` → points to `maestro_subscription_health`
- `maestro_subscription_health` → points to `maestro_subscriptions`
- `maestro_build` → points to `maestro_builds`
- `maestro_channel` → points to `maestro_channels`

### 3. Channel ID vs name asymmetry fix (P1-M3)

Changed `maestro_channel` parameter from `int channelId` to `string channelNameOrId`. If it parses as int, routes to `GetChannelAsync(int)`; otherwise uses `GetChannelByNameAsync(string)`. This eliminates a common failure mode where agents pass a channel name to a tool that only accepted IDs.

### 4. Smart trigger_subscription auto-resolve (P1-M1)

Made `buildId` optional on `maestro_trigger_subscription`. When null, agents can provide `sourceRepository` + `channelName` to auto-resolve the latest build. This eliminates a 3-step agent dance (latest_build → parse → trigger) that was error-prone.

## Files Changed

- `src/MaestroTool.Core/MaestroMcpTools.cs` — All description and parameter changes
- `src/MaestroTool.Tests/MaestroMcpToolsTests.cs` — Constructor fix for test compatibility

## Rationale

Token efficiency in MCP tool descriptions directly impacts agent performance. Every unnecessary word in a description is repeated across every `tools/list` call. Cross-references reduce trial-and-error routing. Parameter flexibility (string channel, optional buildId) reduces multi-tool orchestration failures.
