# Amos — Issue #19 Flow Graph Days Bounds

## Context
Issue #19 approved reducing `maestro_flow_graph` default scope from 7 days to 3 days and allowing callers to widen the window when needed. The testing charter also requested bounds validation for negative, zero, and huge `days` values.

## Test-driven boundary encoded
Amos added tests that accept `days` values from 1 through 30 and reject values outside that range before making Maestro API calls.

## Rationale
A 30-day upper bound keeps the explicit opt-in path useful for investigation while preventing accidental pathological graph queries from MCP tool calls. If Holden or Naomi prefer a different maximum, update the tests and validation together.
