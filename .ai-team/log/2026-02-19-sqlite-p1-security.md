# 2026-02-19 — SQLite Cache P1 Security Fixes

**Session Date:** 2026-02-19  
**Requested by:** Larry Ewing

## Summary

Team executed STRIDE threat model findings for SQLite cache migration. Holden identified 13 findings across 12 threat categories. Naomi implemented P1 security fixes for two MEDIUM-severity vulnerabilities. Amos wrote comprehensive security test coverage. Coordinator resolved Windows connection pool issue. All 73 tests passing.

## Session Details

### Threat Model Analysis
- **Lead:** Holden (Lead / Architect)
- **Scope:** New threats introduced by SQLite migration (cache data now persisted to disk)
- **Findings:** 13 total (1 HIGH, 7 MEDIUM, 5 LOW)
- **Priority Classification:**
  - **P1 (Ship now):** File permissions (I2), Corruption recovery (D2)
  - **P2 (Next sprint):** HMAC integrity (S1/T1), Action dedup integrity (T2), Write attribution (R1)
  - **P3 (Backlog):** Write-lock DoS (D1), Auth boundary (E1/E2), WAL remnants (I3/T3), Cleanup accumulation (D3)

### Implementations Completed

#### Fix 1: Directory Permission Hardening (I2)
- **Implemented by:** Naomi
- **File:** `src/MaestroTool.Core/CacheService.cs`
- **What:** After creating `~/.mstro/` directory, explicitly set owner-only permissions
  - Linux/macOS: `File.SetUnixFileMode(dir, 0o700)` 
  - Windows: No action (profile directories already restricted)
- **Why:** Default umask on shared Linux systems could make cache world-readable
- **Impact:** Prevents accidental info disclosure on shared dev machines

#### Fix 2: Database Corruption Auto-Recovery (D2)
- **Implemented by:** Naomi
- **File:** `src/MaestroTool.Core/CacheService.cs`
- **What:** Run `PRAGMA integrity_check` at startup; if corrupted:
  1. Log to stderr
  2. Close and delete corrupted DB file
  3. Delete WAL/SHM sidecar files
  4. Recreate fresh database
- **Why:** Corrupted SQLite files caused persistent startup failures with no recovery
- **Impact:** Cache now self-heals on corruption; users never need manual intervention

#### Windows Connection Pool Issue
- **Fixed by:** Coordinator
- **Issue:** SQLite connection initialization on Windows was failing under concurrent load
- **Resolution:** Pool connection reuse properly; verified cross-process WAL mode behavior

### Test Coverage

#### Security Tests Written
- **Lead:** Amos (Tester)
- **Scope:** Validation of P1 fixes + gap analysis
- **Tests Added:** 6 security-focused tests
  1. Permission hardening (Unix modes) — 1 test
  2. Corruption detection and recovery — 2 tests
  3. File cleanup after corruption — 1 test
  4. Concurrent access during recovery — 1 test
  5. Regression: Normal operation after recovery — 1 test

#### Test Results
- **Total:** 73 tests
  - 67 existing tests (all passing)
  - 6 new security tests (all passing)
- **Baseline:** All 73 tests passing before commit
- **Status:** ✅ Clean build, no warnings

### Decisions Recorded

- **Threat Model Deep Dive** (Holden): 13 findings prioritized; P1 items are I2 and D2
- **P1 Security Fixes** (Naomi): File permissions + corruption recovery implemented
- **Security Test Coverage** (Amos): 6 tests for P1 fixes
- **SQLite Migration** (Naomi): Cross-process cache sharing with 10k-entry capacity cap
- **Threat Model Fixes** (Naomi): 5 prior fixes already merged (SSRF validation, auth gating, dedup separation, audit logging, cache size cap)

### Commits

- **Commit Hash:** `eb1d5e0`
- **Message:** "P1 security fixes: file permissions (I2) and corruption auto-recovery (D2)"
- **Co-authored-by:** Copilot <223556219+Copilot@users.noreply.github.com>
- **Files Changed:**
  - `src/MaestroTool.Core/CacheService.cs` (surgical edits for permissions + recovery)

### Affected Agents

- **Holden:** Threat model validation → recommendations implemented
- **Naomi:** Security fixes implementation → all tests passing
- **Amos:** Test coverage gaps → 6 new security tests written
- **Coordinator:** Cross-platform issue resolution

## Next Steps

**P2 Items (Next Sprint):**
- HMAC integrity verification (S1/T1) — prevents cache poisoning via same-user process tampering
- Auth-level persistence in cache schema (E2) — future-proofing for auth-scoped cache reads
- Write attribution logging (R1) — forensic data for incident response

**P3 Items (Backlog):**
- Write-lock DoS detection and escalation (D1)
- Cross-process auth boundary enforcement (E1)
- Rate limiting on `noCache` parameter (from prior STRIDE)

## Verification

✅ All 73 tests passing  
✅ Commit pushed: `eb1d5e0`  
✅ No build warnings  
✅ Cross-platform tested (Windows + Linux)  
