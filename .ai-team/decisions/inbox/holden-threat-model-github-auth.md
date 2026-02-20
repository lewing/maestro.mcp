# Threat Model: GitHub Auth Cascade (v0.6.0)

**Author:** Holden (Lead / Architect)  
**Date:** 2025-07-16  
**Scope:** `GitHubApiClient.cs` — 3-tier GitHub auth cascade and Compare API integration  
**Framework:** STRIDE-informed analysis  
**Context:** Read-only MCP tool server, calls GitHub Compare API for public repos (dotnet/dotnet). Runs as MCP subprocess hosted by Copilot CLI.

---

## Summary

The GitHub auth cascade is **reasonably secure for its scope** — a single-purpose, read-only tool calling one public API endpoint. The most significant finding is the subprocess `WaitForExit()` with no timeout (Medium severity, fix now). The rest are low-severity items appropriate for a dev-local tool, with two "fix later" items worth addressing when time allows.

**Findings:** 9 total — 0 Critical, 0 High, 2 Medium, 4 Low, 3 Info

---

## Findings

### GH-T1: Subprocess Hang — `WaitForExit()` with No Timeout

- **Category:** Denial of Service  
- **Severity:** Medium  
- **Description:** `process.WaitForExit()` on line 48 has no timeout. If `gh auth token` hangs (broken pipe, stuck credential helper, network timeout in gh's own auth flow), the entire MCP server startup blocks indefinitely. The static initializer makes this worse — the `HttpClient` is created during type loading, so a hang here freezes the first request and potentially all subsequent ones.
- **Mitigation:** Add `process.WaitForExit(5000)` (5-second timeout). If it doesn't exit in time, kill the process and fall through to anonymous. Also consider `process.StartInfo.RedirectStandardError = true` to capture any error output for diagnostics.
- **Priority:** **Fix now** — easy fix, prevents a real startup hang scenario.

### GH-T2: PATH-Based Executable Resolution

- **Category:** Tampering / Elevation of Privilege  
- **Severity:** Low  
- **Description:** `FileName = "gh"` resolves via the system PATH. A malicious `gh` binary earlier in PATH could intercept the call and harvest the intent (though the subprocess output — a token — flows back to *our* process, not the other way). In the reverse direction, a trojan `gh` could return a malicious token, but since we only use it as a Bearer token against `api.github.com`, the worst outcome is auth failure.
- **Mitigation:** Accepted. This is the standard pattern for CLI tool integration. The attack requires prior machine compromise (modifying PATH or dropping a binary), at which point the attacker already has access to `gh auth token` directly. No action needed.
- **Priority:** **Accept**

### GH-T3: Token Not Logged — Confirmed Safe

- **Category:** Information Disclosure  
- **Severity:** Info  
- **Description:** The code correctly logs only the *method* of authentication ("using GITHUB_TOKEN env var", "using gh CLI token") to stderr, never the token value itself. The token variable stays in local scope and is only assigned to `DefaultRequestHeaders.Authorization`. No string interpolation includes the token.
- **Mitigation:** None needed — this is correct behavior.
- **Priority:** **Accept** (already handled correctly)

### GH-T4: Static HttpClient — Token Lifetime and Rotation

- **Category:** Spoofing / Information Disclosure  
- **Severity:** Medium  
- **Description:** The `HttpClient` is created once in a static initializer and lives for the process lifetime. If the underlying token is rotated (GITHUB_TOKEN env var changes, `gh auth` re-authenticates), the MCP server continues using the stale token until restarted. This isn't a *leak* risk, but it means: (1) Token revocation doesn't take effect until restart. (2) If the initial auth fails and falls back to anonymous, the server stays anonymous forever — no retry.
- **Mitigation:** For this tool's scope (short-lived MCP subprocess, restarted per session), this is acceptable. Document that token changes require server restart. For longer-lived deployments, consider a `DelegatingHandler` that refreshes the token lazily.
- **Priority:** **Fix later** — document the restart requirement. Consider lazy refresh if the server becomes long-lived.

### GH-T5: URL Construction — Limited SSRF Surface

- **Category:** Spoofing / Server-Side Request Forgery  
- **Severity:** Low  
- **Description:** The Compare API URL is constructed via string interpolation: `$"https://api.github.com/repos/{owner}/{repo}/compare/{baseSha}...{headSha}"`. The parameters `owner`, `repo`, `baseSha`, `headSha` come from *internal* data — specifically `MaestroService.ParseGitHubUrl()` which parses stored repository URLs, and `Build.Commit` values from the Maestro/BAR API. These are **not user-supplied MCP tool parameters**. The `IsVmrRepository` guard further limits this to URLs containing `github.com/dotnet/dotnet`. A path-traversal attempt in a SHA (e.g., `../../other-endpoint`) would produce a 404 from GitHub's API routing, not an SSRF.
- **Mitigation:** The existing guardrails (ParseGitHubUrl validates `github.com` host, IsVmrRepository restricts to dotnet/dotnet, parameters come from trusted BAR API data) are sufficient. For defense-in-depth, could add SHA format validation (`^[0-9a-f]{7,40}$`), but this is a minor hardening.
- **Priority:** **Fix later** — add SHA regex validation as defense-in-depth.

### GH-T6: Error Message Information Disclosure

- **Category:** Information Disclosure  
- **Severity:** Low  
- **Description:** Error messages include `response.StatusCode` and `owner/repo` (line 76), and `ex.Message` for exceptions (line 93). The status code and owner/repo are not sensitive — they're public repo identifiers. The `ex.Message` could theoretically include internal details (e.g., DNS resolution failures revealing internal network topology), but since the only target is `api.github.com`, this is negligible.
- **Mitigation:** Accepted. The error messages go to stderr (not to the MCP tool response — the method returns `null` on failure). The MaestroService caller handles `null` gracefully by omitting the `CommitsBehind` field.
- **Priority:** **Accept**

### GH-T7: Token Scope — Broader Than Needed

- **Category:** Elevation of Privilege  
- **Severity:** Low  
- **Description:** `GITHUB_TOKEN` and `gh auth token` typically return tokens with broader scopes than read-only public repo access (e.g., `repo`, `write:packages`). This tool only needs `public_repo` read access (or no token at all for public repos). If the token leaked, it could be used for more than compare API calls.
- **Mitigation:** Accepted for now. We can't control the user's token scope — this is inherent to reusing ambient credentials. The token is handled safely (not logged, not persisted, not forwarded). Document that users can create a fine-grained PAT with only `public_repo:read` if they want minimal scope.
- **Priority:** **Accept** — document recommendation for fine-grained PATs in README.

### GH-T8: Rate Limiting / DoS via MCP

- **Category:** Denial of Service  
- **Severity:** Info  
- **Description:** The Compare API is called inside `GetSubscriptionHealthAsync`, which iterates subscriptions. A target repository with many subscriptions could trigger many GitHub API calls. However: (1) The `IsVmrRepository` guard limits calls to dotnet/dotnet subscriptions only. (2) Results are cached via `CacheService` (5-minute TTL on subscription health). (3) GitHub's own rate limits (5000 req/hr authenticated, 60 req/hr anonymous) provide natural throttling. (4) The MCP server is single-user (subprocess per Copilot session).
- **Mitigation:** None needed. The existing caching and GitHub rate limits are sufficient. The LLM caller has no incentive to DoS its own tool.
- **Priority:** **Accept**

### GH-T9: MCP Trust Boundary — Subprocess Output Not Sanitized

- **Category:** Tampering  
- **Severity:** Info  
- **Description:** The `gh auth token` subprocess output is `.Trim()`-ed and used as a Bearer token. If a compromised `gh` binary returned output with embedded newlines or HTTP header injection characters, the `AuthenticationHeaderValue` constructor would reject malformed values (it validates the token parameter). The `ReadToEnd().Trim()` pattern is safe for single-line token output.
- **Mitigation:** None needed. `AuthenticationHeaderValue` provides validation. The `.Trim()` handles trailing newlines from stdout.
- **Priority:** **Accept**

---

## Architecture Assessment

### What's Done Right

1. **Token never logged** — Only auth method names go to stderr.
2. **Graceful degradation** — Each auth tier falls through to the next on failure. Catch-all around subprocess prevents crashes.
3. **Scoped API surface** — Only one endpoint (`/repos/{o}/{r}/compare/{b}...{h}`), read-only, public repos only.
4. **Input source is trusted** — owner/repo/SHA come from BAR API responses, not from MCP tool parameters.
5. **stderr for diagnostics** — Auth logging goes to stderr, which is the correct channel for MCP servers (doesn't pollute tool responses).

### What Should Be Improved

| Priority | Finding | Action |
|----------|---------|--------|
| **Fix now** | GH-T1: `WaitForExit()` no timeout | Add 5-second timeout, kill on hang |
| **Fix later** | GH-T4: Static token, no rotation | Document restart requirement |
| **Fix later** | GH-T5: SHA format validation | Add `^[0-9a-f]{7,40}$` regex |
| **Accept** | GH-T2, T3, T6, T7, T8, T9 | Current implementation is appropriate |

---

## Decision

The GitHub auth cascade is **approved for v0.6.0** with one P1 fix required:

- **GH-T1 (subprocess timeout)** should be fixed before the next release. Assign to Naomi.
- **GH-T4 and GH-T5** go on the backlog as P2 hardening items.
- All other findings are accepted — the risk profile is appropriate for a single-user, read-only, dev-local MCP tool.

**Decided by:** Holden  
**Participants:** Holden (analysis), Larry (requested)
