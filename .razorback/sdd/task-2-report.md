# Task 2 — `workspace list` recency ordering, default cap, filter

**Status:** DONE (green). Supersedes stale prior `backend-http` content from an earlier SDD run.

## What I implemented

Render-layer changes only (No Architecture Impact). `workspace list` now orders by relevance and
caps compact output so a 100+-row registry no longer dumps ~3.5k tokens.

- **`WorkspaceListEntry` gains `DateTimeOffset LastSeenAt`** (`WorkspaceRender.cs:56`). Added as the
  last positional param with `= default` so every existing construction site (incl. the un-owned
  `WorkspaceRenderTests.cs`) still compiles unchanged.
- **Ordering** (`WorkspaceRender.OrderAndFilter`): current workspace first
  (`OrderByDescending(Current)`), then `LastSeenAt` descending. LINQ `OrderBy` is stable, so ties keep
  the registry's own order.
- **Filter**: case-insensitive substring on `DisplayId` OR `Root`, applied before the cap. No-match
  renders a helpful line with the total registered count instead of an empty string.
- **Compact cap** (`ListCompact`): default 20 (`WorkspaceRender.DefaultListLimit`), `<=0` unlimited.
  Renders at most `limit` entries then a tail `… N more — raise limit or pass filter=<substring>`.
- **Omitted error visibility**: when any omitted (past-cap) entry is `state=error`, appends
  `errors: N workspace(s) in error state — filter or raise limit to see them`.
- **JSON** (`ListJson`): unlimited by default (existing consumers), narrows only on a positive explicit
  `limit`; ordered current-first; every row gains additive `last_seen_at` (ISO-8601 round-trip).
- **Assembler** (`WorkspaceFactsAssembler.ToListEntries`): maps `row.LastSeenAt` onto the entry.
- **MCP `workspace` tool**: new optional `filter` (string, default null) and `limit` (int?, default null
  → 20 compact / unlimited JSON) params with `[Description]` attributes, threaded through `Dispatch` →
  `RenderRegistryList` → `WorkspaceRender.List`. Not a new tool (adding params is approved by the plan).
- **CLI `workspace list`**: `--filter <s>` / `--limit <n>` mapped to the same core (`o.Value("filter")`,
  `o.Has("limit") ? o.Int("limit", DefaultListLimit) : null`). Help text updated.
- **Agent instructions doc**: minimal note `list shows the registry (filter/limit)` — kept terse because
  the embedded doc had only 37 chars of headroom under the 12k budget; final length 11982.

## Miller calls used + what each confirmed

- `inspect WorkspaceListEntry depth=overview` — confirmed the record-struct shape (8 positional params)
  and listed callers/refs to update.
- `trace WorkspaceListEntry mode=refs` — enumerated all 15 construction/type-usage sites across
  `CliDispatch.cs`, `WorkspaceFactsAssembler.cs`, `WorkspaceRender.cs`, `WorkspaceTool.cs`, and 3 test
  files, proving a defaulted trailing param was needed to keep un-owned `WorkspaceRenderTests.cs` green.
- Targeted `Read` on `RenderRegistryList`, `Dispatch`, `WorkspaceList`, `ToListEntries`,
  `WorkspaceRegistry.{UpsertSeen,List}`, `CliOptions`.

## API-shape evidence

- `WorkspaceRegistryRow.LastSeenAt` is a non-null `DateTimeOffset` (`WorkspaceRegistryRow.cs:21`) — safe
  to map without a null guard.
- `WorkspaceRegistry.UpsertSeen(..., DateTimeOffset? seenAtUtc = null)` lets tests seed distinct
  last-seen stamps for deterministic recency ordering.
- `Utf8JsonWriter.WriteString(name, DateTimeOffset)` emits ISO-8601 (additive `last_seen_at`).
- `CliOptions.Int(name, fallback)` + `.Has(name)` give explicit-vs-default detection for the CLI limit.

## Verification

- **Invariant proven**: `workspace list` renders the current workspace first, then most-recently-seen,
  caps compact output at `limit` (default 20) with an accurate omitted-count tail, surfaces omitted
  error-state rows, narrows by case-insensitive `filter` (no-match → helpful line), and keeps JSON an
  unlimited, additive-`last_seen_at` shape unless a positive limit is given — across both the MCP tool
  and the CLI dispatch path.
- **Scope**: `dotnet test tests/Miller.Tests --filter
  "FullyQualifiedName~WorkspaceToolTests|FullyQualifiedName~WorkspaceFactsAssembler|FullyQualifiedName~AgentInstructions|FullyQualifiedName~CliDispatchTests"`
- **Result**: Passed! Failed: 0, Passed: 226, Skipped: 0.
- **Build**: `dotnet build src/Miller.Server -c Release` → 0 warnings / 0 errors.
- Timestamp: 2026-07-02. SHA: see commit trailer.

New tests: `WorkspaceToolTests` (cap+tail+current-first, filter narrow, filter no-match, omitted-error
summary, JSON unlimited+last_seen_at, JSON explicit limit); `WorkspaceFactsAssemblerTests` (LastSeenAt
mapping); `CliDispatchTests` (`--filter`, `--limit` + tail).

## Files changed

- `src/Miller.Server/Tools/WorkspaceRender.cs`
- `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`
- `src/Miller.Server/Tools/WorkspaceTool.cs`
- `src/Miller.Server/Cli/CliDispatch.cs` (workspace-list region only)
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- `tests/Miller.Tests/Server/WorkspaceToolTests.cs`
- `tests/Miller.Tests/Server/WorkspaceFactsAssemblerTests.cs`
- `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

## Judgment calls

- `LastSeenAt` added as a **defaulted** trailing param (not required) so un-owned test/production
  construction sites compile unchanged — the only site passing a real value is `ToListEntries`.
- Header made explicit about capping/filtering (`# workspaces (20 of 26)` / `... matched, ...
  registered; filter="..."`) but kept exactly `# workspaces (N)` when nothing is omitted and no filter,
  preserving the un-owned `WorkspaceRenderTests` header assertion.
- JSON ordered current-first too (additive; order isn't a documented contract) for consistency with
  compact; existing JSON shape tests still pass.
- Agent-instructions note kept terse due to the 12k-char embedded-doc budget. The full contract lives in
  the MCP param `[Description]`s and CLI help text.

## Self-review findings

- Confirmed `limit <= 0` = unlimited in both compact and JSON.
- Confirmed no-match filter returns a non-empty, count-bearing line (not `""`).
- Confirmed omitted-error summary fires only for rows actually omitted past the cap.
- Confirmed the CLI distinguishes explicit `--limit` from the default (via `o.Has`), so JSON stays
  unlimited when `--limit` is absent.

## Concerns

- **Shared build coupling**: the test assembly briefly failed to COMPILE mid-run because a sibling's
  `InspectTool.cs` (Task 5) was mid-edit (CS0103 on `AppendGroupedReferences`/`DistinctCallees`). It
  resolved on its own; my scope then passed 226/226. No action needed.
- The embedded agent-instructions doc is near its 12k budget (now 11982); further additions by other
  tasks may need trimming.
