# Decision: Release v0.12.0

**Date:** 2026-03-01
**Author:** Alex (DevOps/Infrastructure)
**Status:** Executed

## Context

The project had accumulated three significant changes since the last released tag (v0.10.0):
1. MCP SDK upgrade to stable 1.0.0
2. Linux/WSL permissions fix in CacheService
3. Tool annotations for MCP client auto-approval

Version 0.11.0 was set during the SDK upgrade but never tagged/released.

## Decision

Cut release v0.12.0 (skipping a v0.11.0 tag) to bundle all three changes into a single release. This avoids confusion between the internal 0.11.0 version that was never published and ensures a clean release history.

## Consequences

- v0.12.0 tag and commit pushed to `origin/master`
- Version string updated in `.csproj`, both `Program.cs` entry points
- 135 tests verified passing before release
