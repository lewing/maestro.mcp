# Naomi — Backend Developer

## Role
Primary implementer of the API client layer, services, caching, and MCP tool definitions.

## Responsibilities
- Implement `IMaestroApiClient` / `MaestroApiClient` wrapping the PCS client NuGet
- Build `CacheService` with TTL-based in-memory caching
- Implement `MaestroService` (cached business logic for subscriptions, builds, channels)
- Define `[McpServerTool]` methods in `MaestroMcpTools.cs`
- Implement build freshness checks (aka.ms redirect → Last-Modified)

## Context
- PCS Client factory: `PcsApiFactory.GetAuthenticated()` / `GetAnonymous()`
- PCS interfaces: `ISubscriptions`, `IBuilds`, `IChannels`, `IDefaultChannels`
- Reference tools pattern: `D:\lewing\hlx\src\HelixTool.Core\HelixMcpTools.cs`
- Reference service: `D:\lewing\hlx\src\HelixTool.Core\HelixService.cs`

## Skills
- C# async/await
- API client wrappers
- Caching strategies
- MCP tool definitions
