# Session Log: Issue #8 — PcsApiFactory URI Fix

**Date:** 2026-02-22  
**Requested by:** Larry Ewing  
**Status:** Complete

## Summary

Fixed issue #8 (`PcsApiFactory.GetAnonymous()` UriFormatException) by introducing a `DefaultBaseUri` constant and passing it to all 3 PcsApiFactory call sites.

## Changes Made

### Code Changes (Commit 73ef721)
- Added `DefaultBaseUri` constant to PcsApiFactory
- Updated all 3 call sites to pass DefaultBaseUri
- Version bumped to 0.8.4

### Tests (Commit 5bdf2df)
- Amos wrote 3 regression tests in MaestroApiClientTests.cs
- Tests added separately from code changes

## Verification

- **Total tests passing:** 123
  - 120 original tests
  - 3 new regression tests
- **Issue #8:** Closed

## Commits

- Main fix: 73ef721
- Test addition: 5bdf2df
- Both pushed to origin master

## Contributors

- **Naomi:** Issue analysis and fix implementation
- **Amos:** Regression test development
- **Larry Ewing:** Session request/oversight
