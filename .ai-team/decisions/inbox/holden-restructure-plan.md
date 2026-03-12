# Maestro.mcp Code Reorganization Plan

**Author:** Holden (Lead/Architect)  
**Date:** 2026-03-13  
**Status:** PROPOSAL — awaiting team review and approval  
**Reference:** helix.mcp commit 731260e, maestro.mcp current state (896-line monolithic MaestroMcpTools.cs)

---

## Executive Summary

**Recommendation:** Use a **partial-class + subfolder strategy** for Core domain organization (no separate Mcp.Tools project).

**Rationale:**
- MaestroMcpTools has 20 tools that are heavily coupled to MaestroService (which wraps 3 API clients)
- Splitting to a separate project adds ceremony without isolation benefit (unlike helix.mcp, where tools are thin API wrappers)
- Partial classes preserve zero DI registration changes while enabling logical file organization
- Subfolders in Core (Maestro/, GitHub/, AzDO/) match the API client architecture
- Tests stay flat for now (167 tests, manageable)

**Non-breaking:** No namespace changes, no project structure changes to integrators.

---

## Context & Analysis

### Current State

```
src/MaestroTool.Core/
  ├─ MaestroMcpTools.cs        (896 lines, 20 tools, 1 class)
  ├─ MaestroService.cs          (1000+ lines, business logic, wraps 3 API clients)
  ├─ MaestroApiClient.cs        (Maestro/PCS API)
  ├─ GitHubApiClient.cs         (GitHub API)
  ├─ AzDoApiClient.cs           (AzDO API)
  ├─ CacheService.cs            (14.5 KB, caching layer)
  └─ MaestroToolOptions.cs      (configuration)

src/MaestroTool.Tests/
  ├─ MaestroMcpToolsTests.cs    (167 tests, flat structure)
  ├─ MaestroServiceTests.cs
  ├─ CacheServiceTests.cs
  └─ ...
```

### Why helix.mcp's Pattern Doesn't Fit Directly

**helix.mcp (reference):**
- 2 API clients (Helix, AzDO) → Created separate `HelixTool.Mcp.Tools` project
- Tools are thin wrappers around API calls
- Clear separation: Core (API + service) vs Tools (MCP exposure layer)
- 8 tools total

**maestro.mcp (our context):**
- 3 API clients (Maestro/PCS, GitHub, AzDO) + **MaestroService business logic**
- 20 tools organized by **Maestro domain concepts**, not backend APIs:
  - Subscriptions (5 tools)
  - Channels (3 tools)
  - Builds (3 tools)
  - Codeflow (4 tools)
  - Cache/utilities (5 tools)
- Tools call MaestroService methods, not raw API clients
- Heavy state coupling (MaestroService, CacheService, MaestroToolOptions all passed to constructor)

**Consequence:** Creating `MaestroTool.Mcp.Tools` would require:
- Pulling MaestroService and its dependencies into the new project
- Managing cross-project test dependencies
- Adding `<ProjectReference>` complexity
- **Net result:** No isolation gain, only extra indirection

---

## Tool Organization Analysis

**Current 20 tools by domain concept:**

| Domain | Tools | Lines | Characteristics |
|--------|-------|-------|-----------------|
| **Subscriptions** | subscriptions, subscription, subscription_health, trigger_subscription, subscription_history | ~250 | Heavy service calls, caching, parameter resolution |
| **Channels** | channels, channel, default_channels | ~100 | Simple service wrapping |
| **Builds** | builds, build, latest_build, build_freshness, build_graph | ~150 | Mixed service + HTTP calls, caching |
| **Codeflow** | codeflow_prs, codeflow_pr, codeflow_statuses, backflow_status, flow_graph, trigger_daily_update | ~280 | Complex state logic, multi-level filtering |
| **Cache** | clear_cache | ~20 | Utility |

**Observation:** Tools cluster by **Maestro domain concepts** (what users care about), not by backend API. This is the correct abstraction level for MCP tools and should not be split by API backend.

---

## Proposed Restructure

### OPTION A: Partial Class + Subfolders (RECOMMENDED)

Keep `MaestroMcpTools` as a single logical class spread across multiple files using partial class declarations. Organize subfolders by domain concept.

#### New Structure

```
src/MaestroTool.Core/
  ├─ MaestroMcpTools/                    (new folder)
  │  ├─ MaestroMcpTools.cs               (class declaration, constructor, helper methods)
  │  ├─ MaestroMcpTools.Channels.cs      (partial: channels, channel, default_channels)
  │  ├─ MaestroMcpTools.Subscriptions.cs (partial: subscriptions, subscription, subscription_history, 
  │  │                                     subscription_health, trigger_subscription)
  │  ├─ MaestroMcpTools.Builds.cs        (partial: builds, build, latest_build, build_freshness, 
  │  │                                     build_graph)
  │  ├─ MaestroMcpTools.Codeflow.cs      (partial: codeflow_prs, codeflow_pr, codeflow_statuses, 
  │  │                                     backflow_status, flow_graph, trigger_daily_update)
  │  └─ MaestroMcpTools.Utilities.cs     (partial: clear_cache)
  │
  ├─ Maestro/                            (new folder - Maestro/PCS domain)
  │  ├─ MaestroApiClient.cs              (move)
  │  ├─ IMaestroApiClient.cs             (move)
  │  └─ MaestroService.cs                (move)
  │
  ├─ GitHub/                             (new folder - GitHub domain)
  │  ├─ GitHubApiClient.cs               (move)
  │  └─ IGitHubApiClient.cs              (move)
  │
  ├─ AzDO/                               (new folder - AzDO domain)
  │  ├─ AzDoApiClient.cs                 (move)
  │  └─ IAzDoApiClient.cs                (move)
  │
  ├─ CacheService.cs                     (stays - shared by multiple layers)
  └─ MaestroToolOptions.cs               (stays - configuration)

src/MaestroTool.Tests/
  ├─ MaestroMcpTools/                    (new folder)
  │  ├─ MaestroMcpToolsTests.cs          (stays flat for now, but logically grouped)
  │
  ├─ Maestro/                            (new folder)
  │  ├─ MaestroApiClientTests.cs         (move)
  │  └─ MaestroServiceTests.cs           (move)
  │
  ├─ GitHub/                             (new folder)
  │  ├─ GitHubApiClientTests.cs          (move if exists)
  │
  ├─ AzDO/                               (new folder)
  │  ├─ AzDoUrlParsingTests.cs           (move)
  │  └─ AzDoApiClientTests.cs            (move if exists)
  │
  ├─ CacheServiceTests.cs                (stays - shared)
  └─ MaestroToolOptionsTests.cs          (stays - shared)
```

### Namespace Strategy

**NO CHANGES to public namespaces.** All files stay in `MaestroTool.Core` namespace:

```csharp
namespace MaestroTool.Core;

public partial class MaestroMcpTools { /* channels */ }
public partial class MaestroMcpTools { /* subscriptions */ }
// etc.

public class MaestroService { }        // stays in MaestroTool.Core
public class MaestroApiClient { }      // stays in MaestroTool.Core
// etc.
```

**Rationale:** Partial classes are a language feature—they're still a single namespace. DI registration doesn't change. External callers see no difference.

### Benefits

✅ **Reduced file size:** 896 → ~150-200 lines each, more readable  
✅ **Clear organization:** Tools grouped by user-facing domain (Channels, Subscriptions, etc.)  
✅ **API client structure mirrors reality:** Folders match the 3 API clients (Maestro, GitHub, AzDO)  
✅ **Zero breaking changes:** Same namespace, same DI registration, same public surface  
✅ **Easier code review:** Review by domain (e.g., "review all subscription tools")  
✅ **Tests organized by layer:** Mirrors source structure  
✅ **Low migration effort:** Move files, add `partial class` keyword, done

### Risks

⚠️ **Partial class complexity:** Developers unfamiliar with partial classes might not realize methods are spread across files. **Mitigation:** Add comment at top of each partial file referencing the class declaration.

⚠️ **Potential for divergence:** Different developers might add to different partial files for related concerns. **Mitigation:** Document which partial file owns which domain in the Main method or PR checklist.

---

## OPTION B: Separate Mcp.Tools Project (NOT RECOMMENDED)

For completeness, here's what it would look like if we followed helix.mcp exactly:

```
src/MaestroTool.Core/
  ├─ Maestro/
  │  ├─ MaestroService.cs
  │  ├─ MaestroApiClient.cs
  │  ├─ IMaestroApiClient.cs
  │
  ├─ GitHub/
  │  ├─ GitHubApiClient.cs
  │  └─ IGitHubApiClient.cs
  │
  ├─ AzDO/
  │  ├─ AzDoApiClient.cs
  │  └─ IAzDoApiClient.cs
  │
  ├─ CacheService.cs
  └─ MaestroToolOptions.cs

src/MaestroTool.Mcp.Tools/          (new project)
  ├─ Channels/
  │  └─ ChannelTools.cs
  ├─ Subscriptions/
  │  └─ SubscriptionTools.cs
  ├─ Builds/
  │  └─ BuildTools.cs
  ├─ Codeflow/
  │  └─ CodeflowTools.cs
  ├─ Utilities/
  │  └─ UtilityTools.cs
  └─ MaestroTool.Mcp.Tools.csproj   (depends on MaestroTool.Core)

src/MaestroTool.Mcp/
  └─ Program.cs                       (WithToolsFromAssembly loads MaestroTool.Mcp.Tools)
```

**Why NOT:** 
- MaestroMcpTools constructor takes (MaestroService, MaestroToolOptions, CacheService)
- Splitting across 5 files each needing the same dependencies
- No isolation: tools still tightly coupled to MaestroService
- Adding project reference, updating `.slnx`, build complexity
- Tests would need cross-project setup
- **All the ceremony of separate project with none of the benefits**

This pattern works for helix.mcp because tools are thin API wrappers. Not applicable here.

---

## Migration Plan

### Phase 1: Create Folder Structure (Non-breaking prep)

1. Create folders:
   - `src/MaestroTool.Core/MaestroMcpTools/`
   - `src/MaestroTool.Core/Maestro/`
   - `src/MaestroTool.Core/GitHub/`
   - `src/MaestroTool.Core/AzDO/`
   - `src/MaestroTool.Tests/MaestroMcpTools/`
   - `src/MaestroTool.Tests/Maestro/`
   - `src/MaestroTool.Tests/GitHub/`
   - `src/MaestroTool.Tests/AzDO/`

2. Move API client files and test files to their respective folders (namespace stays the same)

3. Create partial class files for MaestroMcpTools (skeleton)

4. Run tests after each file move—ensure no regressions

### Phase 2: Migrate MaestroMcpTools

1. Move main MaestroMcpTools.cs to `MaestroMcpTools/MaestroMcpTools.cs`
   - Keep class declaration, constructor, shared helpers (Timestamp method)
   - Mark as `public partial class`

2. Create partial files:
   - `MaestroMcpTools.Channels.cs` → channels, channel, default_channels
   - `MaestroMcpTools.Subscriptions.cs` → subscriptions, subscription, subscription_health, trigger_subscription, subscription_history
   - `MaestroMcpTools.Builds.cs` → builds, build, latest_build, build_freshness, build_graph
   - `MaestroMcpTools.Codeflow.cs` → codeflow_prs, codeflow_pr, codeflow_statuses, backflow_status, flow_graph, trigger_daily_update
   - `MaestroMcpTools.Utilities.cs` → clear_cache

3. Each partial file:
   - Starts with `#nullable enable` and global usings
   - Declares `public partial class MaestroMcpTools`
   - Contains 2-6 tool methods

4. Run full test suite after migration

### Phase 3: Reorganize Tests

1. Move test files to mirror source structure
2. Rename MaestroMcpToolsTests.cs → MaestroMcpToolsTests.cs (stays at folder root, can split later)
3. Verify test discovery and execution

---

## File-by-File Plan

### Core Project Migrations

**Move (no namespace changes):**

```
src/MaestroTool.Core/MaestroApiClient.cs
  → src/MaestroTool.Core/Maestro/MaestroApiClient.cs
  namespace: MaestroTool.Core (unchanged)

src/MaestroTool.Core/IMaestroApiClient.cs
  → src/MaestroTool.Core/Maestro/IMaestroApiClient.cs
  namespace: MaestroTool.Core (unchanged)

src/MaestroTool.Core/MaestroService.cs
  → src/MaestroTool.Core/Maestro/MaestroService.cs
  namespace: MaestroTool.Core (unchanged)

src/MaestroTool.Core/GitHubApiClient.cs
  → src/MaestroTool.Core/GitHub/GitHubApiClient.cs
  namespace: MaestroTool.Core (unchanged)

src/MaestroTool.Core/IGitHubApiClient.cs
  → src/MaestroTool.Core/GitHub/IGitHubApiClient.cs
  namespace: MaestroTool.Core (unchanged)

src/MaestroTool.Core/AzDoApiClient.cs
  → src/MaestroTool.Core/AzDO/AzDoApiClient.cs
  namespace: MaestroTool.Core (unchanged)

src/MaestroTool.Core/IAzDoApiClient.cs
  → src/MaestroTool.Core/AzDO/IAzDoApiClient.cs
  namespace: MaestroTool.Core (unchanged)
```

**Split (partial classes):**

```
src/MaestroTool.Core/MaestroMcpTools.cs
  → src/MaestroTool.Core/MaestroMcpTools/MaestroMcpTools.cs
     (class declaration, constructor, helpers, Timestamp method)
     namespace: MaestroTool.Core
     declaration: public partial class MaestroMcpTools

  → src/MaestroTool.Core/MaestroMcpTools/MaestroMcpTools.Channels.cs
     (GetChannels, GetChannel, GetDefaultChannels)
     namespace: MaestroTool.Core
     declaration: public partial class MaestroMcpTools

  → src/MaestroTool.Core/MaestroMcpTools/MaestroMcpTools.Subscriptions.cs
     (GetSubscriptions, GetSubscription, GetSubscriptionHealth, 
      TriggerSubscription, GetSubscriptionHistory)
     namespace: MaestroTool.Core
     declaration: public partial class MaestroMcpTools

  → src/MaestroTool.Core/MaestroMcpTools/MaestroMcpTools.Builds.cs
     (ListBuilds, GetBuild, GetLatestBuild, GetBuildFreshness, GetBuildGraph)
     namespace: MaestroTool.Core
     declaration: public partial class MaestroMcpTools

  → src/MaestroTool.Core/MaestroMcpTools/MaestroMcpTools.Codeflow.cs
     (GetCodeflowPrs, GetTrackedPr, GetCodeflowStatuses, 
      GetBackflowStatus, GetFlowGraph, TriggerDailyUpdate)
     namespace: MaestroTool.Core
     declaration: public partial class MaestroMcpTools

  → src/MaestroTool.Core/MaestroMcpTools/MaestroMcpTools.Utilities.cs
     (ClearCache)
     namespace: MaestroTool.Core
     declaration: public partial class MaestroMcpTools
```

**Stay in place:**

```
src/MaestroTool.Core/CacheService.cs (no move)
src/MaestroTool.Core/MaestroToolOptions.cs (no move)
```

### Test Project Migrations

```
src/MaestroTool.Tests/MaestroMcpToolsTests.cs
  → src/MaestroTool.Tests/MaestroMcpTools/MaestroMcpToolsTests.cs
  namespace: MaestroTool.Tests (unchanged)

src/MaestroTool.Tests/MaestroServiceTests.cs
  → src/MaestroTool.Tests/Maestro/MaestroServiceTests.cs
  namespace: MaestroTool.Tests (unchanged)

src/MaestroTool.Tests/MaestroApiClientTests.cs
  → src/MaestroTool.Tests/Maestro/MaestroApiClientTests.cs
  namespace: MaestroTool.Tests (unchanged)

src/MaestroTool.Tests/AzDoUrlParsingTests.cs
  → src/MaestroTool.Tests/AzDO/AzDoUrlParsingTests.cs
  namespace: MaestroTool.Tests (unchanged)

src/MaestroTool.Tests/CacheServiceTests.cs (no move - shared layer)
src/MaestroTool.Tests/MaestroToolOptionsTests.cs (no move - shared)
```

---

## Example: MaestroMcpTools.Subscriptions.cs

```csharp
using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace MaestroTool.Core;

/// <summary>
/// Subscription-related MCP tools. Part of the MaestroMcpTools class.
/// See MaestroMcpTools.cs for class declaration and helpers.
/// </summary>
public partial class MaestroMcpTools
{
    [McpServerTool(Name = "maestro_subscriptions", Title = "List Subscriptions", ReadOnly = true, Idempotent = true)]
    [Description("List Maestro subscriptions filtered by source/target repository and/or channel name. For health checks, use maestro_subscription_health. For details on a single subscription by ID, use maestro_subscription.")]
    public async Task<string> GetSubscriptions(
        [Description("Filter by source repository URL (e.g. https://github.com/dotnet/runtime)")] string? sourceRepository = null,
        // ... rest of method
    )
    {
        // ... implementation
    }

    // ... other subscription tools
}
```

---

## DI Registration (No Changes)

In `src/MaestroTool.Mcp/Program.cs` and `src/MaestroTool/Program.cs`:

```csharp
// BEFORE and AFTER — IDENTICAL
services.AddScoped<MaestroMcpTools>();
services.AddScoped<MaestroService>();
services.AddScoped<CacheService>();
services.AddScoped<IMaestroApiClient, MaestroApiClient>();
services.AddScoped<IGitHubApiClient, GitHubApiClient>();
services.AddScoped<IAzDoApiClient, AzDoApiClient>();

// BEFORE and AFTER — IDENTICAL
.WithToolsFromAssembly(typeof(MaestroMcpTools).Assembly)
```

The partial class declaration is transparent to DI. One registration works across all partial files.

---

## Testing & Validation

### Build Verification
```bash
dotnet build MaestroTool.slnx
# Should compile with zero errors
```

### Test Execution
```bash
dotnet test src/MaestroTool.Tests/MaestroTool.Tests.csproj
# All 167 tests should pass (assumes no test changes)
```

### Integration Check
```bash
dotnet run --project src/MaestroTool.Mcp/
# MCP server should start with 20 tools available
# Verify: /tools/call maestro_subscriptions works
```

---

## Future Improvements

Once this reorganization is in place:

1. **Test splitting:** Split MaestroMcpToolsTests into domain-specific test files (SubscriptionToolsTests, etc.)
2. **Helper extraction:** Extract common patterns (parameter resolution, output formatting) to domain-specific helpers
3. **Tool documentation:** Create per-domain markdown files documenting tool parameters and workflows
4. **Consider future Mcp.Tools project:** If tool layer becomes thick enough or has different lifecycle, re-evaluate creating separate project

---

## Sign-Off Checklist

- [ ] Team agrees on recommendation (Option A: Partial class + subfolders)
- [ ] No concerns about partial class discoverability in code reviews
- [ ] Agrees to mirror folder structure in tests
- [ ] Namespace stability confirmed (no breaking changes)
- [ ] DI registration verified (zero changes needed)
- [ ] File move order planned (to minimize merge conflicts)

---

## References

- helix.mcp commit 731260e (reference pattern, not directly applicable)
- maestro.mcp current decisions.md (security review, auth cascade, test gaps)
- Holden's audit (2026-03-12) — tool descriptions, parameter design, MCP patterns
- MaestroMcpTools.cs (896 lines, 20 tools)
- MaestroService.cs (1000+ lines, business logic)

