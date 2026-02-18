### 2026-02-18: Action tools policy
**By:** Larry Ewing (via Copilot)
**What:** Action tools should be added to the MCP server. Destructive actions (delete, disable) must be disabled by default and gated behind a config flag. Non-destructive actions (trigger, retry) can be enabled by default. The team should identify which PCS API methods are destructive vs non-destructive as a backlog item.
**Why:** User directive — safety by default for mutation operations
