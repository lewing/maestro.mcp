# Alex — History

## Completed Work

### README.md Documentation

**Date:** 2025-07-15

Created a comprehensive README.md covering:
- Project title and description
- Prerequisites (.NET 10 SDK, darc authentication)
- Build and run instructions
- MCP client configuration snippet
- 3-tier authentication cascade (PAT → cached Entra ID → anonymous)
- Table of all 8 tools with parameters
- Architecture overview (4-layer design: data, caching, business logic, MCP)
- Cache TTL table with justification
- Testing instructions
- MIT license

The README is production-ready and suitable for both internal developers and external MCP client integrators.

📌 Team update (2026-02-18): README.md created for maestro.mcp covering authentication, tools, architecture, and cache strategy — decided by Alex

## Learnings

### Documentation Standards for MCP Servers
- **Structure**: Prerequisites → Build → Run → Configuration → Authentication → Tools Reference → Architecture → Testing.
- **Authentication**: Always explain the cascade and provide clear examples for each tier. Devs should understand which auth method is active without running the code.
- **Tools Table**: Include name, description, and key parameters. This is the primary reference for MCP client integrators.
- **Architecture Section**: High-level overview of layers + classes is sufficient; don't document every method. Link layers to the problem they solve (caching → performance, service → business logic).
- **Cache TTLs**: Always justify TTL choices. This helps reviewers understand design trade-offs (freshness vs. API load).

### Convention: MCP Server README Layout
- Lead with problem statement: what does this server do?
- Prerequisites and environment setup are critical upfront.
- Configuration examples must be copy-pasteable with minimal edits.
- Always explain authentication flow in human terms before referencing files.
- Tools and architecture sections are reference material; use tables and bullet lists for scannability.

### README v2 Updates — Action Tools Documentation
- **Config file locations**: GitHub Copilot CLI is now the first row since it's the primary client for this project's users.
- **Action Tools section**: Documented non-destructive action tools with deduplication logic (2-minute cooldown prevents duplicate LLM retries).
- **Future destructive actions**: Sketched the opt-in pattern for future delete/remove operations, showing how `MAESTRO_ENABLE_DESTRUCTIVE_ACTIONS` env var gates dangerous operations.
- **Tool count updated**: Changed from 8 to 10 tools in the table header to reflect added action tools.
- **Placement**: Action Tools section sits between Authentication and Available Tools, since it provides context for how triggering works before listing the full tool reference.
