# Telemetry Workspace Onboarding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Build a read-only `workspace onboarding` report that gives agents practical, telemetry-derived startup guidance for any Miller-indexed repo.

**Architecture:** Add a workspace-scoped report beside existing `workspace status` and `workspace health`. Read telemetry from the machine-global ledger through a small read-only aggregator, recover hot targets by matching `target_hash` against current index candidates, and render compact Markdown-like output plus deterministic JSON. Do not edit instruction files or change search/ranking behavior.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, existing Miller workspace registry/selectors, `Utf8JsonWriter`, xUnit v3, current README/GitHub Pages static site.

**Architecture Quality:** Medium risk public surface. The approved shape keeps behavior read-only and local: `WorkspaceTool`/CLI route the operation, small readers own SQLite access, `WorkspaceRender` owns pure formatting, and README/site docs explain the privacy boundary and non-goals.

## Global Constraints

- The feature is generic for any Miller-indexed repo, not specific to `/Users/murphy/source/miller`.
- The first slice is read-only. It must never modify `CLAUDE.md`, `AGENTS.md`, `ONBOARDING.md`, or any repo file.
- Telemetry remains query-safe. Do not persist or expose raw query text, raw target text, snippets, or source text.
- Compact output must not print unresolved `target_hash` values.
- JSON output may include counts and confidence labels, but must not make unresolved hashes look like useful agent targets.
- Selector behavior must match `workspace status` and `workspace health`.
- Add public documentation in both `README.md` and `docs/site/index.html`.
- `AGENTS.md` is generated from `CLAUDE.md`; this feature should not require changing either file.

---

## File Structure

- Create `src/Miller.Server/Telemetry/TelemetryOnboardingReader.cs`
  - Read-only aggregate queries over `tool_telemetry`.
  - Models for tool mix, transitions, repeated target hashes, empty/error groups, cost/friction, and sample window.
- Create `src/Miller.Indexing/WorkspaceTargetHashResolver.cs`
  - Read current index candidates cheaply from `symbols.db`.
  - Hash symbol ids, symbol names, file paths, and `path:name` scoped strings using the telemetry SHA-256 convention.
  - Match repeated telemetry hashes to recover hot symbols/files with confidence labels.
- Create `src/Miller.Server/Tools/WorkspaceOnboardingFacts.cs`
  - Combine workspace identity, telemetry aggregates, recovered targets, generated guidance, and sparse-data fallback state.
- Modify `src/Miller.Server/Tools/WorkspaceTool.cs`
  - Add MCP `workspace(operation="onboarding")` routing.
- Modify `src/Miller.Server/Cli/CliDispatch.cs`
  - Add `miller workspace onboarding [--json] [--markdown]` routing and help text.
- Modify `src/Miller.Server/Tools/WorkspaceRender.cs`
  - Add pure compact/Markdown and JSON renderers.
- Modify `src/Miller.Server/Cli/CliCapabilities.cs`
  - Advertise `workspace onboarding --json` as a supported JSON command if JSON is implemented as a stable public shape.
- Add `docs/contracts/workspace-onboarding-v1.md`
  - Document JSON shape and privacy semantics.
- Modify `docs/contracts/cli-eros-v1.md`
  - List the JSON command/contract when implemented.
- Modify `docs/README.md`
  - Add the contract doc to current docs.
- Modify `README.md`
  - Document the CLI/MCP report, privacy boundary, and non-goals.
- Modify `docs/site/index.html`
  - Add marketing/product copy for telemetry-derived onboarding.
- Tests:
  - Modify `tests/Miller.Tests/Server/WorkspaceToolTests.cs`.
  - Modify `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`.
  - Modify `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`.
  - Modify or add telemetry reader tests under `tests/Miller.Tests/Server/Telemetry*Tests.cs`.
  - Add indexing hash-resolver tests under `tests/Miller.Tests/Indexing/`.

## Task 1: Telemetry Aggregation Reader

**Files:**
- Create: `src/Miller.Server/Telemetry/TelemetryOnboardingReader.cs`
- Test: `tests/Miller.Tests/Server/TelemetryOnboardingReaderTests.cs`

**Interfaces:**
- Produces: `TelemetryOnboardingReader.Read(string telemetryDbPath, string? workspaceId, int windowDays = 30)`.
- Produces: immutable facts for sample window, tool/op/outcome counts, transitions, repeated target hashes, empty/error groups, and cost/friction.
- Consumes: existing `tool_telemetry` table.

**Approach:**
- Open the telemetry DB read-only with `Pooling=false`.
- Missing DB or missing table returns an empty/sparse facts object, not an exception.
- Scope every query by `workspace_id IS $ws`, matching `TelemetryLedger.Summarize`.
- Default sample window is last 30 days relative to the newest row for that workspace, not wall-clock now. This keeps tests deterministic and avoids making an old but real ledger look empty.
- Transition query uses `LEAD(...) OVER (PARTITION BY workspace_id ORDER BY ts, id)` and keeps pairs with delta between 0 and 300 seconds.
- Repeated target hashes are grouped by `tool`, `op`, `target_hash`, and count, but the facts object should not contain raw hashes intended for compact rendering. It may carry them internally for Task 2 matching.
- Empty/error groups use `json_extract(metadata_json, '$.empty_reason')` and `json_extract(metadata_json, '$.error_category')`.

**Acceptance criteria:**
- Reader returns sparse fallback when telemetry DB is absent.
- Reader scopes rows to the requested workspace.
- Reader reports top transitions above a minimum threshold.
- Reader reports repeated target hashes for Task 2 without exposing raw query text.
- Reader summarizes common empty/error categories and high-cost tools.

## Task 2: Target Hash Recovery

**Files:**
- Create: `src/Miller.Indexing/WorkspaceTargetHashResolver.cs`
- Test: `tests/Miller.Tests/Indexing/WorkspaceTargetHashResolverTests.cs`

**Interfaces:**
- Produces: `WorkspaceTargetHashResolver.Resolve(string symbolsDbPath, IReadOnlyList<TelemetryTargetHashGroup> groups, int limit)`.
- Produces recovered target facts with `confidence` values:
  - `symbol_id_hash`
  - `symbol_name_hash`
  - `file_path_hash`
  - `scoped_symbol_hash`
  - `unresolved_hash`
- Consumes: repeated target hash groups from Task 1 and current `symbols.db`.

**Approach:**
- Use `SqliteReadOnlyAccess.Open` and `JulieSchemaGate.Verify` for current artifacts.
- Read only cheap columns from `symbols`: `symbol_id`, `name`, `kind`, `language`, `path`, `start_line`, `is_test`.
- Build candidates for:
  - symbol id
  - symbol name
  - file path
  - `path:name`
- Hash with SHA-256 lowercase hex to match `TelemetryScope.SetTarget`.
- Match repeated telemetry hashes to candidates and cap output.
- If multiple symbols share a name hash, include a collision count and lower confidence text; do not pretend it is exact.
- Do not print raw unresolved hashes in compact rendering.

**Acceptance criteria:**
- Exact symbol-id hash recovers one symbol with `symbol_id_hash`.
- Shared name hash reports collision/ambiguous facts rather than one false exact target.
- File path hash recovers a file target.
- Unmatched repeated hashes remain counted but not printed as raw hashes in compact output.

## Task 3: Workspace Onboarding Facts And Guidance Policy

**Files:**
- Create: `src/Miller.Server/Tools/WorkspaceOnboardingFacts.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceToolTests.cs`

**Interfaces:**
- Produces: `WorkspaceOnboardingFacts.Create(...)` or equivalent factory that combines workspace facts, telemetry facts, and recovered targets.
- Consumes: `WorkspaceFactsAssembler`, `TelemetryOnboardingReader`, `WorkspaceTargetHashResolver`, and existing workspace target resolution.

**Approach:**
- Add `case "onboarding"` to `WorkspaceTool.Dispatch`.
- Reuse `ResolveTarget` and `VerifyRegisteredRoot` paths from status/health.
- For current workspace, use `_workspace.ExtractDbPath`, `_workspace.WorkspaceId`, and `_workspace.TelemetryDbPath`.
- For registered workspace, use registry row `IndexDbPath` and `WorkspaceId`.
- Generate these sections:
  - `StartHere`
  - `SuccessfulFlows`
  - `HotTargets`
  - `CommonMisses`
  - `ToolFriction`
  - `SuggestedInstructionAdditions`
- Sparse fallback:
  - If no telemetry exists, produce generic Miller guidance: `workspace health`, `context`, `search -> inspect`, `impact` before edits.
  - Mark telemetry state as `sparse` or `missing`.
- Bad habits from telemetry must go into `CommonMisses`, not `StartHere`.

**Acceptance criteria:**
- `workspace(operation="onboarding")` works for current workspace.
- Registered workspace selectors behave like `status` and `health`.
- Unknown selectors return the same usage/empty style as existing workspace target operations.
- Sparse telemetry returns useful generic guidance.
- Bad empty/error patterns are represented as cautions.

## Task 4: Rendering And CLI Surface

**Files:**
- Modify: `src/Miller.Server/Tools/WorkspaceRender.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Modify: `src/Miller.Server/Cli/CliCapabilities.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`
- Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Interfaces:**
- Produces: `WorkspaceRender.Onboarding(WorkspaceOnboardingFacts facts, bool json)` or equivalent.
- Produces CLI:
  - `miller workspace onboarding`
  - `miller workspace onboarding --json`
  - `miller workspace onboarding --markdown`
- Consumes: Task 3 facts.

**Approach:**
- Compact/Markdown output starts with `# workspace onboarding`.
- Keep compact output short and action-oriented.
- JSON output includes:
  - `workspace`
  - `telemetry`
  - `start_here`
  - `successful_flows`
  - `hot_targets`
  - `common_misses`
  - `tool_friction`
  - `suggested_instruction_additions`
  - `privacy`
- `--markdown` can be accepted as the default compact renderer in the first slice. It should not imply file writing.
- Update workspace help text to include `onboarding`.
- Update unknown operation text to include `onboarding`.
- Add `workspace onboarding --json` to capabilities and JSON contract list if the JSON shape is documented in Task 5.

**Acceptance criteria:**
- Compact output is readable as Markdown.
- JSON output is deterministic and includes privacy/non-goal flags.
- CLI selectors match existing workspace operations.
- Capabilities advertise the JSON command and contract.

## Task 5: Contracts, README, And GitHub Pages Site

**Files:**
- Add: `docs/contracts/workspace-onboarding-v1.md`
- Modify: `docs/contracts/cli-eros-v1.md`
- Modify: `docs/README.md`
- Modify: `README.md`
- Modify: `docs/site/index.html`
- Test: existing docs/link tests if present; otherwise covered by build and grep checks.

**Interfaces:**
- Produces: public documentation for the new onboarding report.
- Consumes: final JSON shape from Task 4.

**Approach:**
- Contract doc must define:
  - command shape.
  - JSON fields.
  - privacy rules.
  - sparse telemetry behavior.
  - target hash recovery confidence labels.
  - non-goals: no instruction-file edits, no ranking changes, no prediction/prefetching.
- `cli-eros-v1.md` lists `workspace onboarding --json` in stable commands/contracts.
- `docs/README.md` links the new contract under current docs.
- `README.md` adds a concise CLI/onboarding bullet near CLI output expectations and agent onboarding docs.
- `docs/site/index.html` adds product copy that describes telemetry-derived onboarding for any indexed repo. Keep wording accurate: local, advisory, query-safe, read-only.

**Acceptance criteria:**
- README documents the command and non-goals.
- GitHub Pages site mentions the onboarding report without implying cloud telemetry or automatic doc rewriting.
- Contract doc is linked from docs map and Eros CLI contract.

## Task 6: Final Review And Cleanup

**Files:**
- Review all files touched in Tasks 1-5.

**Interfaces:**
- Consumes: completed implementation and docs.
- Produces: verified branch state.

**Approach:**
- Confirm the implementation does not write repo files.
- Confirm compact output does not print unresolved target hashes.
- Confirm all telemetry aggregation paths tolerate missing telemetry DB.
- Confirm no broad source/index hydration was introduced for onboarding.
- Confirm README/site copy matches actual command names and behavior.

**Acceptance criteria:**
- All acceptance criteria from the design doc are satisfied.
- Final verification commands pass.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` / `AGENTS.md` testing section.

**Worker red/green scope:** Focused xUnit tests for the touched surface:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~TelemetryOnboardingReaderTests|FullyQualifiedName~WorkspaceTargetHashResolverTests|FullyQualifiedName~WorkspaceToolTests|FullyQualifiedName~WorkspaceRenderTests|FullyQualifiedName~CliDispatchTests'
```

**Worker ceiling:** The focused test filter above plus `dotnet build Miller.slnx -c Release`.

**Worker gate invariant:** The focused tests must prove the public behavior: missing telemetry fallback, scoped aggregation, target-hash recovery, MCP/CLI routing, renderer JSON/Markdown shape, and capabilities/docs discovery.

**Lead affected-change scope:** Run `dotnet build Miller.slnx -c Release`, `scripts/test.sh`, `git diff --check`, and `cmp -s CLAUDE.md AGENTS.md`.

**Branch gate:** `scripts/test.sh` and `dotnet build Miller.slnx -c Release` must pass with 0 failures and 0 warnings. Scale tests are not required unless implementation touches `julie-extract` invocation, extract schema, full rebuild, or scale-only fixtures.

**Replay/metric evidence:** No replay gate. Local telemetry smoke may be used as report-only evidence:

```bash
dotnet run --project src/Miller.Server -c Release -- workspace onboarding --json
dotnet run --project src/Miller.Server -c Release -- workspace onboarding --markdown
```

**Escalation triggers:** Escalate if target hash recovery needs raw query storage, if report generation requires full repository hydration, if docs suggest automatic instruction-file edits, or if JSON output cannot stay stable without exposing private telemetry details.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless the failure is an expected red test before implementation in the same TDD cycle.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the final report or checkpoint. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning an expensive gate.

## Model Routing

**Project source of truth:** No `RAZORBACK.md` exists in this repo. Use harness defaults unless the lead explicitly selects a model.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage.
- Harness mapping: inherit.

**Implementation tier:** bounded worker tasks from this clear plan.
- Harness mapping: inherit.

**Mechanical tier:** docs, contracts, README/site copy, rote help text updates.
- Harness mapping: inherit.

**Gate-interpretation reviewer:** reviewer tier for reading the plan, failing test, and diff to decide whether the test or implementation is wrong.
- Harness mapping: inherit.

**Escalation tier:** subtle privacy/security issues, raw telemetry exposure, high blast radius JSON contract changes, repeated gate failures.
- Harness mapping: inherit.

**Worker eligibility:** Implementation-tier workers may take one task when file ownership is clear. Tasks 1 and 2 can run in parallel. Tasks 3 and 4 depend on Tasks 1-2. Task 5 depends on final command/JSON shape from Task 4.

**Escalation triggers:** Any need to store raw query/target text, mutate instruction files, change search ranking, or add cross-machine telemetry sync requires lead review.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, privacy gate interpretation, or final acceptance. Split docs-only copy updates from evidence interpretation if delegated.

**Unsupported harness behavior:** If the harness cannot choose models per agent, use `inherit` and continue.
