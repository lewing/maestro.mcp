# Session Log: 2026-02-18 Smoke Test

**Requested by:** Larry Ewing

**Work:** Naomi ran end-to-end smoke test of MCP server.

**Issue found & fixed:** MaestroMcpTools was missing `[McpServerToolType]` attribute. Server started but reported 0 tools, `tools/call` returned error `-32601`. Added attribute; all 8 tools now register.

**Results:**
- All 8 tools register and work end-to-end
- Auth: Entra ID via cached darc credentials
- maestro_channels: 159 channels returned
- maestro_subscriptions: 8 for dotnet/runtime
- maestro_latest_build: #302353
- Performance: ~1.6s first call, 150-400ms cached
