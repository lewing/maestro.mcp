# Naomi MCP Description Trim Result

**Author:** Naomi  
**Date:** 2026-06-11  
**Status:** Complete  
**Input:** `.squad/decisions/inbox/holden-mcp-description-audit.md`

## Achieved vs Projected

Holden projected trimming tool descriptions from ~430 words / ~559 tokens to ~280 words / ~364 tokens, saving ~150 words / ~195 tokens.

Measured on the current source tree:

| File | Before words | After words | Word savings | Before tokens | After tokens | Token savings |
|---|---:|---:|---:|---:|---:|---:|
| `MaestroMcpTools.Builds.cs` | 76 | 61 | 15 | 99 | 79 | 20 |
| `MaestroMcpTools.Channels.cs` | 60 | 40 | 20 | 78 | 52 | 26 |
| `MaestroMcpTools.Codeflow.cs` | 116 | 76 | 40 | 151 | 99 | 52 |
| `MaestroMcpTools.Subscriptions.cs` | 136 | 59 | 77 | 177 | 77 | 100 |
| `MaestroMcpTools.Utilities.cs` | 25 | 15 | 10 | 32 | 20 | 12 |
| **Total** | **413** | **251** | **162** | **537** | **327** | **211** |

## Notes

- Achieved savings slightly exceed projection: ~211 tokens saved vs ~195 projected.
- Applied Holden's explicit rewrites for top offenders and used the same rules for remaining trim verdicts.
- Added the flagged cross-routing hints for daily update, codeflow PRs, backflow status, and build freshness while still reducing total context.
- This was a mechanical application of the established audit, not a new design decision.
