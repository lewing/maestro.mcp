# Holden — `--schema` Architecture for `mstro`

**Issue:** #12  
**Author:** Holden  
**Date:** 2026-03-13

## Executive take

We should treat `--schema` as part of the CLI contract, not a bolt-on doc feature.

My recommendation is:

1. **Add `--schema` to every query command that already has `--json`.**
2. **Return a minified JSON skeleton that matches the real `--json` root shape exactly** — array stays array, object stays object, field names stay PascalCase.
3. **Generate that skeleton from explicit CLI response contracts in `MaestroTool.Core`,** not by reflecting raw PCS client types directly.
4. **For custom records, use the actual record type. For PCS-heavy commands, introduce curated CLI contract types and serialize those for both `--json` and `--schema`.**

That is the cleanest architecture. It follows the project shape we want: host/presentation at the edge, contracts and data shaping in Core, transport models quarantined behind the service layer.

---

## What I saw in the code

### Current CLI shape

`src/MaestroTool/Program.cs` is a single `Commands` class with one method per command. The query commands already share a strong pattern:

- `bool json = false`
- `bool noCache = false`
- `JsonSerializer.Serialize(..., s_jsonOptions)`

The serializer options are currently just `new JsonSerializerOptions { WriteIndented = true }`, which means the JSON contract is using **default System.Text.Json property names**. In practice, that means agents see **PascalCase field names** like `Id`, `ChannelName`, `BuildsBehind`, etc.

That matters. `--schema` must emit the exact same names.

### Current response types

The code splits into two groups:

1. **Good agent-facing shapes already exist**
   - `SubscriptionHealthResult`
   - `BuildFreshnessResult`
   - supporting records like `ValidationResult`, `OscillationResult`, `TrackedPrDiagnosis`

2. **Raw PCS types are being serialized directly**
   - `Build`
   - `Subscription`
   - `Channel`
   - `TrackedPullRequest`
   - `BackflowStatus`
   - graph types and other generated models

The custom records are fine. The PCS models are the problem: they are transport objects, not an intentional CLI contract.

---

## Decision 1 — Schema format

## Recommendation

Use a **minified JSON skeleton with typed placeholders**.

### Example: `mstro subscription-health --schema`

```json
[{"SubscriptionId":"<guid>","SourceRepository":"<string>","TargetRepository":"<string>","TargetBranch":"<string>","ChannelName":"<string>","IsStale":true,"BuildsBehind":0,"LastAppliedBuildId":0,"LastAppliedDate":"<datetime>","LatestBuildId":0,"LatestBuildDate":"<datetime>","Error":null,"CommitsBehind":null,"RecentCommits":[{"Sha":"<string>","Message":"<string>","Author":"<string>","Date":"<datetime>"}],"Validation":{"CommitReachable":true,"MergedPrsSinceLastApplied":0,"MergedPrUrls":["<string>"],"BookkeepingAnomalyDetected":false,"AnomalyReason":null},"Oscillation":{"OscillationCount":0,"State1":"<string>","State2":"<string>","FirstSeen":"<datetime>","LastSeen":"<datetime>"},"TrackedPr":{"State":"<Missing|MergedButNotCleared|ClosedButNotCleared|BlockedByCI|Active|Unknown>","PrUrl":null,"Reason":null},"VmrConsumedCommit":null,"VmrConsumedDate":null}]
```

### Why this format wins

- **Exact jq field discovery.** Agents can see the real property names immediately.
- **Mentally cheap.** It looks like the actual payload, not a spec language.
- **Compact enough.** Simple commands stay tiny. Complex commands grow, but still stay far below full JSON Schema noise.
- **Root shape is obvious.** Arrays, nested objects, nullable fields, and repeated items are visible at a glance.

### Why I reject the other options

- **Compact type notation** (`{ Id: int, Name: string }`) is readable, but it is not real JSON. It forces the agent to translate mentally.
- **Full JSON Schema** is the wrong tool here. It is technically precise and practically terrible for token budget.
- **Placeholder instance serialization** only works if the placeholders are produced intentionally; otherwise it collapses nullable/reference semantics and becomes misleading.

### Important correction to the issue example

`subscription-health --json` currently returns an **array of `SubscriptionHealthResult`**, not an object like `{ StaleSubs: ..., HealthySubs: ... }`.

`--schema` must match the real payload shape, not a friendlier wrapper. Schema is a contract mirror, not a re-interpretation layer.

---

## Decision 2 — Generation approach

## Recommendation

Use a **hybrid contract-driven generator**:

- **Generator:** shared `CliSchemaGenerator` that walks a type graph and emits the minified JSON skeleton.
- **Source of truth:** explicit CLI response contract types.
  - For custom records: use the real record type directly.
  - For PCS-heavy commands: use curated CLI contract records, not the generated PCS transport types.

### This is the architecture I want

**Do not** hardcode schema text per command.

**Do not** blindly reflect `Microsoft.DotNet.ProductConstructionService.Client` models.

**Do** generate schema from the same types the CLI intentionally serializes.

### Why blind reflection on PCS types is the wrong answer

It would dump every property the BAR client exposes, including fields we do not surface meaningfully anywhere else.

That creates three problems:

1. **Token bloat** — the schema becomes the same wall of noise we are trying to avoid.
2. **Poor product contract** — it exposes transport details as if they were a supported CLI API.
3. **Drift in the wrong direction** — any upstream BAR client expansion makes our schema worse automatically.

### Why hardcoded per-command schema text is also wrong

It gives perfect control, but it is maintenance debt from day one. The first rename breaks trust.

### Best implementation detail

Have the generator resolve property metadata through **System.Text.Json contract metadata**, not raw reflection, so it respects the same naming policy and future `[JsonPropertyName]` changes automatically.

This is the right level of sophistication. We do **not** need a source generator for this feature.

### Placeholder rules I recommend

- `string` → `"<string>"`
- `Guid` → `"<guid>"`
- `DateTimeOffset` / `DateTime` → `"<datetime>"`
- `bool` → `true`
- integer types → `0`
- floating point / decimal → `0.0`
- enums → `"<Value1|Value2|Value3>"`
- nullable value/reference types → `null` when absence matters, otherwise typed placeholder when presence is expected
- arrays/lists → one representative element
- dictionaries → `{ "<key>": <value-placeholder> }`

### Required guardrails

- **Cycle detection** for object graphs
- **Depth cap** for pathological types
- **Single representative item** for collections

---

## Decision 3 — Where `SchemaGenerator` lives

## Recommendation

Put schema generation in **`MaestroTool.Core`**, not `MaestroTool`.

### Why

`Program.cs` should stay as the CLI edge: parse flags, call service, print output.

The schema is not presentation trivia. It is a **shared contract concern** tied to the response types themselves. If we ever want to surface the same contract metadata in:

- a future help/guide command,
- a skill resource,
- tests,
- or MCP-side discovery,

we will want the generator and the contract registry in Core.

### Concrete structure

I would add something like:

- `src/MaestroTool.Core/CliSchema/`
  - `CliSchemaGenerator.cs`
  - `CliSchemaRegistry.cs`
  - `CliContracts/` (or `CliJson/Contracts/`)

That keeps the architecture honest:

- **Service layer owns shaped data contracts**
- **CLI layer owns command wiring**

---

## Decision 4 — Integration pattern

## Recommendation

Add `bool schema = false` to every **query** command that already supports `--json`.

I do **not** recommend a separate meta-command like `mstro schema subscription-health`.

### Why the flag is better

- `mstro subscription-health --help` and `mstro subscription-health --schema` stay together.
- Agents do not have to learn a second command namespace.
- The existing CLI already uses per-command flags for output mode (`--json`) and cache behavior (`--no-cache`). `--schema` fits naturally.

### Behavior rules

- `--schema` is valid only on query commands.
- `--schema` should short-circuit before any API calls or cache reads.
- `--schema` should ignore `--no-cache`.
- If both `--schema` and `--json` are passed, **`--schema` wins**.

That is simple, predictable, and easy to explain.

### Scope boundary

Do **not** add `--schema` to:

- `mcp`
- `guide`
- `cache`
- `trigger-subscription`
- `trigger-daily-update`

Those are not JSON query payload commands.

---

## Decision 5 — PCS client type handling

## Recommendation

For PCS-heavy commands, **do not expose the full raw PCS model as the supported schema**.

Instead, define a **curated CLI contract** per shape family and use that as the supported JSON surface.

### My view, bluntly

The BAR client models are not a good public CLI contract. They are too wide, too nested, and too upstream-driven.

If we dump the full `Build`, `Subscription`, or `Channel` graph into `--schema`, we technically solve field-name guessing while still producing a bad agent experience.

That is not good product design.

### What I would support instead

Introduce stable agent-facing shapes such as:

- `CliBuildSummary`
- `CliSubscriptionSummary`
- `CliChannelSummary`
- `CliTrackedPullRequestSummary`
- `CliBackflowStatusSummary`

These should contain the fields agents actually need for filtering and jq pipelines.

Examples of the kind of fields I would keep:

- **Build:** `Id`, `BuildNumber`, `Commit`, `DateProduced`, `SourceRepository`, `Channels`
- **Subscription:** `Id`, `SourceRepository`, `TargetRepository`, `TargetBranch`, `Channel`, `Enabled`, `LastAppliedBuildId`
- **Channel:** `Id`, `Name`, `Classification`

The exact field list should be driven by current CLI formatting, existing skill examples, and common Maestro investigation workflows.

### Yes, this implies a JSON contract cleanup

That is intentional.

If we want `--schema` to be useful and trustworthy, it should describe an intentional contract, not whatever the generated client happened to give us.

### Back-compat opinion

If we are going to tighten the CLI JSON contract, **now is the time**. The feature is still young, the main consumers are agents, and the issue itself exists because the current contract is not ergonomic enough.

If we absolutely refuse to change `--json` for PCS-backed commands, the fallback is:

- keep the raw `--json` payloads,
- emit a curated shallow schema,
- accept that the schema is guidance rather than a full mirror.

I consider that a second-best compromise, not the right architecture.

---

## Recommended implementation plan

### Phase 1 — Ship the contract machinery

1. Add CLI schema generation in `MaestroTool.Core`.
2. Add `--schema` to every query command.
3. Start with commands already backed by custom records:
   - `subscription-health`
   - `build-freshness`
4. Add tests that assert exact schema text for representative commands.

### Phase 2 — Rationalize PCS-backed JSON responses

1. Add curated CLI contract records for noisy PCS-backed commands.
2. Map service outputs into those records before `--json` serialization.
3. Generate `--schema` from the same records.

### Phase 3 — Fill the surface

Roll through the remaining query commands once the build/subscription/channel contract family is settled.

---

## Testing expectations

I would require tests for:

- root object vs root array shape
- nullable fields shown as nullable
- enum placeholder rendering
- nested object rendering
- list rendering with one representative item
- `--schema` short-circuiting without hitting service/cache
- `--schema` taking precedence over `--json`
- PascalCase property names matching live serialization

---

## Final recommendation

My recommendation is decisive:

- **Format:** minified JSON skeleton with typed placeholders
- **Generation:** shared generator over explicit CLI contract types
- **Location:** `MaestroTool.Core`
- **Integration:** `--schema` flag on each query command
- **PCS handling:** curated CLI contracts, not raw PCS graphs

That gives us something agents can actually use, keeps the token budget sane, and turns `--schema` into a real contract feature instead of a documentation stunt.
