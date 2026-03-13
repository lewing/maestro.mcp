# Session: 2026-03-03 Interactive Detection

**Requested by:** Larry Ewing

## Summary

Naomi added interactive terminal detection to Program.cs. When `mstro` is run with no arguments, it now uses `Console.IsInputRedirected` to decide between:
- Starting MCP server (when stdin is redirected by MCP host)
- Showing help text (when run interactively in a terminal)

This prevents the MCP server from silently hanging when users accidentally run `mstro` without arguments in a terminal.
