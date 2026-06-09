# Workspace Health Report Design

- **Date:** 2026-06-09
- **Status:** Implemented first slice
- **Parent plan:** `docs/plans/2026-06-09-miller-data-opportunities-plan.md`
- **Candidate:** Workspace Intelligence And Health Report
- **Scope:** First CLI/MCP/JSON contract slice implemented. Dashboard panel and Eros aggregation remain deferred.

## Purpose

Add a Miller-owned workspace health report that answers:

> Can an agent trust this workspace index right now, and what should it know before using it?

The report should turn existing Miller facts into a short readiness verdict plus stable JSON. It should not become an Eros dashboard, semantic analysis layer, or tree-sitter structural extractor. Eros can later consume the JSON for portfolio and enterprise reporting.

## Product Decision

Use the existing `workspace` tool and CLI namespace:

```bash
miller workspace health [--json] [--id SELECTOR | --path PATH]
```

MCP surface:

```text
workspace(operation="health", workspace_id=?, path=?, format?)
```

This keeps workspace identity, registry routing, refresh state, and status-like behavior in one public surface. A separate top-level `health` tool would add another entry point without reducing complexity.

Dashboard UI is out of scope for the first slice. After the JSON contract is stable, the dashboard can render the same health summary without owning new policy.

## Health Verdict

The compact output should lead with one of these states:

- `ready`: index and sidecars are usable; no high-priority warnings.
- `usable_with_warnings`: reads can proceed, but there are degraded facts such as parse diagnostics, capability gaps, skipped content, empty telemetry patterns, or stale sidecars that do not block the requested workflow.
- `degraded`: the workspace is readable, but important surfaces are stale, missing, unreadable, or lock-busy enough that agents should refresh or investigate before relying on results.
- `unavailable`: the workspace cannot be read because the index is missing, the root is invalid, or the registry target cannot be resolved.

The verdict is deterministic and based only on local Miller facts. It does not claim code quality, architecture quality, security status, semantic search quality, or enterprise readiness.

## Data Included

The first JSON shape should include:

- workspace identity: `workspace_id`, `display_id`, `root`, `db_path`, `server_version`, `server_pid`.
- verdict: `state`, `summary`, `warnings[]`, `recommended_actions[]`.
- index: revision, freshness, document count, known extension count, queue state, registry state, warning text.
- search sidecar: existing `SearchSidecarFacts`.
- content corpus: existing `ContentCorpusFacts`, including skipped counts.
- extraction quality:
  - parse diagnostics grouped by language and kind.
  - open language capability gaps grouped by language and capability.
  - language capability summary: languages with target/actual symbols, relationships, identifiers, pending relationships, and types.
- telemetry: existing per-workspace `TelemetrySummary`, plus a cheap outcome summary with `ok_count`, `empty_count`, and `error_count`.

The compact output should stay short:

```text
# workspace health  usable_with_warnings
workspace: miller-b275269b2d7c  /Users/murphy/source/miller
index: fresh rev 1683  symbols 9168  ext 24
search_db: current rev 1683
content_db: current rev 1683  sources 521  chunks 971  skipped 0
quality: 22 parse diagnostics  17 open capability gaps
telemetry: 16 calls  errors=0  empty=2
recommended: inspect parse diagnostics before relying on unsupported language facts
```

Exact wording can change during implementation, but it must remain short, explicit, and machine-checkable in tests.

## Architecture Quality

**Affected modules:** `Miller.Server` workspace CLI/MCP surface, `Miller.Indexing` cheap SQLite readers, `Miller.Server` renderers, telemetry summary, tests, and public docs/contracts.

**Caller-facing interface:** `miller workspace health`, MCP `workspace(operation="health")`, compact text, JSON output, and `capabilities --json` if the command list changes.

**Depth/locality check:** Health policy belongs beside current workspace status policy. Existing `WorkspaceTool` and `CliDispatch.WorkspaceStatus` already resolve current and registered workspaces; existing `WorkspaceRender` already owns compact/JSON formatting. New extraction-quality facts should come from a small read-only indexing helper so tool/rendering code does not carry raw SQL.

**Test surface:** Prove behavior through `WorkspaceToolTests`, `WorkspaceRenderTests`, `CliDispatchTests`, and a focused reader test if a new reader is added. Private SQL helper tests are acceptable only to fixture missing/malformed tables cheaply.

**Seams/adapters:** Preserve `WorkspaceTool`, `WorkspaceRender`, `WorkspaceIndexFactsReader`, `SymbolSearchSidecar`, `ContentCorpusSidecar`, and `TelemetryLedger` ownership. Add a `WorkspaceHealthReader` for cheap aggregate reads from `symbols.db`.

**Rejected shortcuts:** No full `MillerRepositoryIndex` hydration in the health path. No Eros-specific fields. No dashboard-first implementation. No source parsing. No broad source/text search added to symbol search. No hiding unreadable sidecars or malformed health tables as success.

**Architecture risk:** medium. The public JSON shape and no-full-index requirement are load-bearing.

## Proposed Components

### `WorkspaceHealthReader`

Create `src/Miller.Indexing/WorkspaceHealthReader.cs`.

Responsibilities:

- Open `symbols.db` read-only.
- Return aggregate health facts only.
- Tolerate missing health-detail tables by reporting explicit unavailable sections, not by throwing for the whole report.
- Fail visibly for unreadable/corrupt databases so the caller can mark the workspace unavailable or degraded.

Initial aggregate queries:

- `parse_diagnostics`: count by `language`, `kind`.
- `language_capability_gaps`: count open rows by `language`, `capability`, `status`.
- `language_capabilities`: target/actual counts by language.
- `files`: count by `language`, `status`.

Do not read symbol bodies, edges, identifiers, or content chunks in this first slice.

### `WorkspaceHealthFacts`

Create `src/Miller.Server/Tools/WorkspaceHealthFacts.cs` for the server-level health model that combines status, telemetry, sidecar, and extraction-quality facts.

Suggested fields:

- `WorkspaceFacts StatusFacts`
- `TelemetrySummary Telemetry`
- `ExtractionHealthFacts Extraction`
- `IReadOnlyList<HealthWarning> Warnings`
- `IReadOnlyList<string> RecommendedActions`
- `HealthState State`

Keep state calculation in one place so compact and JSON rendering cannot disagree.

### `TelemetryHealthFacts`

Extend `TelemetryLedger` with a cheap per-workspace outcome aggregate rather than overloading `TelemetrySummary`.

Suggested fields:

- `OkCount`
- `EmptyCount`
- `ErrorCount`
- `TotalCalls`

This can be computed with one grouped query over `tool_telemetry.outcome`, scoped the same way `SummarizeForWorkspace` scopes by `workspace_id`.

### `WorkspaceRender.Health`

Add pure renderers:

```csharp
public static string Health(WorkspaceHealthFacts facts, bool json)
```

Rendering stays deterministic and I/O-free. JSON uses the same `Utf8JsonWriter` style as existing workspace renderers.

### `WorkspaceTool`

Add `operation="health"` to the existing dispatch.

Reuse target resolution from status:

- no selector: current workspace.
- `workspace_id`: registered workspace selector.
- `path`: registered path selector.
- unknown selector: clear empty result, same style as status/remove.

For current workspace, use existing live status facts plus the health reader against `_workspace.ExtractDbPath`. For registered workspaces, use `WorkspaceIndexFactsReader` and sidecar inspectors as status already does, then add health aggregates from the registered `IndexDbPath`.

### CLI

Add `miller workspace health` to `CliDispatch.Workspace`.

The one-shot CLI should not claim live freshness. It can report registry state, sidecar state, extraction-quality facts, and telemetry only when a scoped ledger read is available without starting the host.

### Capabilities And Docs

Update the supported JSON command list:

- `workspace health --json`

Document the JSON shape under `docs/contracts/workspace-health-v1.md` when implementation pins the exact v1 schema.

## Error Handling

- Unknown workspace selector: return the same style and exit behavior as `workspace status`.
- Missing index DB: `state=unavailable`, warning names the missing DB path.
- Unreadable `symbols.db`: `state=unavailable` with error kind and message.
- Missing health-detail tables: `state=usable_with_warnings`; the section reports `available=false`.
- Stale or unreadable `search.db` or `content.db`: `state=degraded` if the affected sidecar is required for common read workflows, otherwise `usable_with_warnings`.
- Parse diagnostics or open capability gaps: `usable_with_warnings`, not `degraded`, unless counts are so high that implementation defines a clear threshold.
- Telemetry errors: `usable_with_warnings`; telemetry should not make the index unavailable.

## Testing Plan

Focused tests:

- `WorkspaceToolTests.Health_CurrentWorkspace_RendersStatusSidecarsAndExtractionWarnings`
- `WorkspaceToolTests.Health_ByWorkspaceId_ReadsRegisteredFactsWithoutHydratingFullIndex`
- `WorkspaceToolTests.Health_MissingIndex_ReturnsUnavailable`
- `WorkspaceRenderTests.Health_Compact_LeadsWithVerdictAndWarnings`
- `WorkspaceRenderTests.Health_Json_RoundTripsStableSections`
- `CliDispatchTests.WorkspaceHealth_Json_RendersRegisteredWorkspace`
- `CliDispatchTests.WorkspaceHealth_UnknownSelector_ReturnsStatusStyleError`
- `WorkspaceHealthReaderTests.Read_GroupsParseDiagnosticsAndCapabilityGaps`
- `WorkspaceHealthReaderTests.Read_MissingOptionalTables_ReportUnavailableSections`
- `TelemetryLedgerTests.HealthSummary_GroupsOutcomesByWorkspace`

Affected-change verification:

```bash
dotnet test Miller.slnx -c Release --filter "FullyQualifiedName~Health&Category!=Scale"
scripts/test.sh
dotnet build Miller.slnx -c Release
git diff --check
```

Scale tests are not required for a pure health reader unless implementation touches extraction, sidecar writers, live refresh, or real workspace scan behavior.

## Acceptance Criteria

- [x] `workspace health` exists in MCP and CLI.
- [x] Compact output starts with a practical verdict and short warnings.
- [x] JSON includes workspace, verdict, index, sidecars, extraction quality, telemetry, warnings, and recommended actions.
- [x] Health does not hydrate the full repository index for registered workspace targets.
- [x] Missing/corrupt index and sidecar states are explicit, not silent success.
- [x] Parse diagnostics and capability gaps are grouped by language and kind/capability.
- [x] Existing `workspace status` behavior remains unchanged.
- [x] Focused tests, `scripts/test.sh`, `dotnet build Miller.slnx -c Release`, and `git diff --check` pass.

## Deferred

- Dashboard health panel.
- Eros portfolio aggregation.
- Threshold tuning beyond conservative first-slice warning rules.
- Health history or trend analysis.
- Any metric that requires new extractor facts from `julie-extractors`.

## Approval Gate

Implementation should not start until this design is approved. The first implementation slice should use TDD and keep file ownership limited to workspace health surfaces.
