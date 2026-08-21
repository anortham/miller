# Telemetry Diagnosis Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make Miller telemetry explain edit failures and slow read paths, then improve the one search-miss shape supported by the last seven days of data.

**Architecture:** Extend the existing privacy-safe `metadata_json` enrichment path rather than changing the telemetry schema. Keep edit classification in the structured `EditResult`, stamp build/index state in the central call filter, mark known blocking paths in `WorkspaceIndexProvider`, and keep file-search recovery copy inside `SearchTool`.

**Tech Stack:** .NET 10, C#, SQLite telemetry ledger, xUnit.

**Architecture Quality:** Affected modules are the edit tool/service, central telemetry scope/filter, workspace read provider, and file-search empty rendering. The caller-facing interfaces remain the existing edit/search results and telemetry export rows. Tests prove behavior through `EditService`, the real MCP call filter and persisted telemetry row, `WorkspaceIndexProvider`, and `SearchTool.Run`. No new DI seam, MCP tool, database column, or speculative telemetry framework is allowed. Architecture risk: medium because the filter and provider are shared infrastructure.

## Global Constraints

- Preserve privacy: never persist raw edit selectors, query text, paths, snippets, or error output in the new metadata values.
- Store all new facts in `tool_telemetry.metadata_json`; do not migrate `telemetry.db`.
- Do not add an MCP tool or change any existing MCP parameter contract.
- Use stable lowercase snake-case metadata keys and bounded enum-like values.
- `server_version` uses `MillerVersion.Current`.
- `index_state` is exactly `fresh`, `stale`, or `unknown`, derived from the existing `IndexFreshProbe` result.
- `wait_reason` is exactly `none`, `workspace_refresh`, or `index_load`; the first known blocking path wins.
- `edit_failure_reason` must classify structured edit results without parsing rendered output.
- Follow TDD: every production behavior begins with a focused failing test that is observed failing for the intended reason.
- Tests contain no narration comments; production changes add no narration comments.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` testing and build sections.

**Worker red/green scope:** Focused `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~<assigned test class>"` for the owned behavior.

**Worker ceiling:** The assigned focused test class only. Workers do not run the full fast, scale, or build gates.

**Worker gate invariant:** Edit tests prove stable structured failure reasons reach telemetry; index telemetry/provider tests prove runtime/index state and known wait paths persist without schema changes; search tests prove path-shaped file misses return the intended recovery guidance.

**Lead affected-change scope:** `scripts/test.sh` after all three reviewed slices are integrated.

**Branch gate:** `dotnet build Miller.slnx -c Release` and `scripts/test.sh all` from the task worktree.

**Replay/metric evidence:** No replay gate. The hard gates are exact metadata values, unchanged privacy constraints, focused red/green evidence, 0-warning Release build, and fast/scale test results. Historical telemetry counts are report-only motivation.

**Escalation triggers:** Any change to the telemetry SQL schema, MCP surface, workspace refresh semantics, cache synchronization, or extractor/indexing behavior requires stopping that worker lane and reporting a plan mismatch.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Structured edit failure telemetry | Batch A | `src/Miller.Server/Tools/EditService.cs`, `src/Miller.Server/Tools/EditTool.cs`, `tests/Miller.Tests/Server/EditToolTests.cs` | No | None - safe parallel batch. |
| Task 2: Runtime, index-state, and wait metadata | Batch A | `src/Miller.Server/Telemetry/TelemetryScope.cs`, `src/Miller.Server/Telemetry/TelemetryCallToolFilter.cs`, `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`, `tests/Miller.Tests/Server/IndexFreshTelemetryTests.cs`, `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs` | No | None - safe parallel batch. |
| Task 3: File-mode path recovery guidance | Batch A | `src/Miller.Server/Tools/SearchTool.cs`, `tests/Miller.Tests/Server/SearchToolTests.cs` | No | None - safe parallel batch. |

### Task 1: Structured edit failure telemetry

**Files:**
- Modify: `src/Miller.Server/Tools/EditService.cs`
- Modify: `src/Miller.Server/Tools/EditTool.cs`
- Test: `tests/Miller.Tests/Server/EditToolTests.cs`

**Interfaces:**
- Consumes: `EditService.EditResult`, `TelemetryContext.Current`, and `TelemetryScope.SetMetadata(string, string?)`.
- Produces: nullable `EditResult.FailureReason` and persisted `metadata_json.edit_failure_reason` for error outcomes.

**Contract inputs:** Stable privacy-safe buckets must cover at least `no_match`, `ambiguous_match`, `stale_target`, `invalid_request`, `target_not_found`, `apply_failed`, and `unknown`. Successful and no-op edits do not emit a failure reason; expected resolution failures may retain their existing `empty` outcome while carrying a diagnostic bucket.

> **Superseded 2026-08-21.** The `unknown` bucket was retired and replaced by two buckets that name the layer
> that failed to classify: `unclassified_plan_error` (a planner error kind with no mapping) and
> `unclassified_result` (an error result that reached the telemetry seam with no bucket). No source constant
> emits `unknown` any more. Rows already written to `~/.miller/telemetry.db` keep the old value, so a replay or
> analysis script built from the current vocabulary must still accept `unknown` for historical rows.

**File ownership:** `src/Miller.Server/Tools/EditService.cs`, `src/Miller.Server/Tools/EditTool.cs`, `tests/Miller.Tests/Server/EditToolTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Extend the structured edit result with a nullable privacy-safe failure reason, set it at existing error construction points, and copy it to telemetry only when present. Do not derive the bucket from rendered output.

**Approach:** Keep the mapping local to `EditService`; reuse its existing structured planner errors and result helpers. Exercise representative no-match, ambiguity, stale-target, invalid-request, target-not-found, and apply failure paths, plus one success case proving absence.

**Acceptance criteria:**
- [x] Representative edit error paths return the specified stable failure buckets without changing user-facing output.
- [x] `EditTool` writes `edit_failure_reason` only when the structured result supplies one.
- [x] No raw selector, path, old text, new text, or rendered error is persisted in the new metadata value.
- [x] Worker-scope verification passes and the change is handed to the lead per `parallel-lead-commit`.

### Task 2: Runtime, index-state, and wait metadata

**Files:**
- Modify: `src/Miller.Server/Telemetry/TelemetryScope.cs`
- Modify: `src/Miller.Server/Telemetry/TelemetryCallToolFilter.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Test: `tests/Miller.Tests/Server/IndexFreshTelemetryTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`

**Interfaces:**
- Consumes: `MillerVersion.Current`, `IndexFreshProbe.Compute()`, `TelemetryContext.Current`, and existing provider refresh/lazy-load paths.
- Produces: persisted `server_version`, `index_state`, and `wait_reason` metadata plus a first-wins `TelemetryScope` wait-reason marker.

**Contract inputs:** `index_state` maps `true` to `fresh`, `false` to `stale`, and `null` to `unknown`. Every filtered call emits `server_version`, `index_state`, and default `wait_reason=none`. A requested registered-workspace refresh marks `workspace_refresh`; a first lazy projection/index materialization marks `index_load` only when no earlier reason was marked.

**File ownership:** `src/Miller.Server/Telemetry/TelemetryScope.cs`, `src/Miller.Server/Telemetry/TelemetryCallToolFilter.cs`, `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`, `tests/Miller.Tests/Server/IndexFreshTelemetryTests.cs`, `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Stamp runtime and coarse index state in the central call filter, and add a small first-wins wait marker used by known synchronous provider blocking paths. Keep refresh behavior and cache synchronization unchanged.

**Approach:** Initialize safe defaults before invoking the tool so every success/empty/error branch is covered. Mark only the existing refresh and lazy materialization paths; do not add timing thresholds, background work, or new locks.

**Acceptance criteria:**
- [x] Real MCP-filter tests persist `server_version`, all three `index_state` mappings, and `wait_reason=none` by default.
- [x] Provider tests prove registered refresh emits `workspace_refresh`, lazy load emits `index_load`, and refresh wins when both occur.
- [x] Existing `index_fresh` column behavior remains unchanged.
- [x] No refresh, cache, or locking semantics change.
- [x] Worker-scope verification passes and the change is handed to the lead per `parallel-lead-commit`.

### Task 3: File-mode path recovery guidance

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs`
- Test: `tests/Miller.Tests/Server/SearchToolTests.cs`

**Interfaces:**
- Consumes: `SearchTool.QueryShapeFor(string)` and the existing `FileEmptyHint(string)` compact recovery path.
- Produces: path-shaped file-miss guidance that tells callers to shorten to a basename/path fragment before broader symbol fallback.

**Contract inputs:** Keep the existing bounded query echo. JSON output remains unchanged. Identifier-like file misses retain their existing guidance.

**File ownership:** `src/Miller.Server/Tools/SearchTool.cs`, `tests/Miller.Tests/Server/SearchToolTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Specialize the compact empty hint when the file-mode query is path-shaped so callers get a concrete basename/path-fragment recovery action before `mode=auto` or symbol search.

**Approach:** Branch only inside `FileEmptyHint` using the existing query-shape classifier. Preserve filtered-miss handling and all successful file rendering.

**Acceptance criteria:**
- [x] Path-shaped file misses recommend a basename or shorter path fragment.
- [x] Identifier-like file misses retain current recovery wording.
- [x] Filtered misses still use outside-scope guidance instead of the bare file-miss hint.
- [x] JSON output remains unchanged.
- [x] Worker-scope verification passes and the change is handed to the lead per `parallel-lead-commit`.
