# Miller Five Gap Implementation Plan

> For agentic workers implementing this plan: keep each slice deterministic, local, and boundary-respecting. Miller owns code navigation, local facts, and stable JSON feeds. Eros owns semantic/vector retrieval, fleet-level ranking, history, suppressions, cleanup workflows, and commercial orchestration. `julie-extractors` owns parser extraction.

**Goal:** Implement the five agreed Miller-owned gaps: git churn/history facts, empty-state candidate recovery, safe stale-leader handoff, dashboard health/onboarding panels, and deterministic clone/complexity discovery.

**Architecture:** Add narrow reader/service layers for new facts, keep renderers pure, route CLI/MCP through shared tool cores, and keep dashboard projections cheap and read-only. Avoid adding extraction ownership or Eros-style recommendation workflows to Miller.

**Tech Stack:** .NET 10, SQLite, local git CLI, Razor dashboard components, xUnit v3, Miller MCP/CLI JSON contracts.

**Architecture Quality:** Medium risk. This touches public tool contracts, CLI dispatch, leadership coordination, and high-traffic search/inspect paths. Preserve existing module boundaries and add tests at the core, tool, CLI, and dashboard layers before expanding behavior.

## Global Constraints

- Miller may compute deterministic local facts from existing `symbols.db`, `search.db`, `content.db`, telemetry, and git history. It must not add embeddings, semantic ranking, cleanup suggestions, suppressions, or fleet history.
- Do not add parser-backed extraction in Miller. If a future slice needs new extractor facts, it belongs in `julie-extractors` across all supported languages first.
- Keep `Miller.Core` pure. New filesystem, SQLite, git, and process logic belongs in server/indexing/dashboard infrastructure, not core ranking logic.
- Do not kill stale leaders. Leadership remediation must use a request file plus graceful abdication/cooldown through the existing queue pattern.
- Public JSON changes must be additive or under a new command/tool contract. Keep compact text human-friendly but treat JSON as the stable integration surface.
- Avoid growing `CliDispatch` further with large inline command bodies. Put new command families in focused helper dispatchers and call them from `CliDispatch`.
- Dashboard panels must not hydrate full indexes just to render overview/detail views. Use bounded projections and existing reader contracts.
- Update `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` for any new MCP tool or parameter. Update `docs/contracts/cli-eros-v1.md` and `docs/README.md` for new CLI/JSON contracts.
- If agent harness guidance changes, edit `CLAUDE.md`, run `scripts/sync-agents.sh`, and confirm `cmp -s CLAUDE.md AGENTS.md`.
- Verification defaults to `scripts/test.sh`. Use focused `dotnet test` filters while iterating only when they materially shorten red/green loops, then run the wrapper.

## Surface Decisions

- Add one new MCP tool, `metrics`, instead of separate churn/clone/complexity tools.
  - `operation=churn` covers local git history/churn facts.
  - `operation=clones` covers duplicate body-hash groups.
  - `operation=complexity` covers deterministic complexity ranking.
- Add one CLI command family, `miller metrics <operation> --json`, backed by the same service as the MCP tool.
- Keep existing `complexity export --jsonl` unchanged as a raw Eros feed. The new `metrics complexity` surface is an interactive/top-N report over the same extracted facts.
- Add `workspace leader` as a new `workspace` operation for diagnostics and explicit handoff, rather than a standalone tool.
- Empty-state recovery enriches existing `search`, `inspect`, `trace`, `impact target`, and `edit` misses. It does not create a new tool.

## Slice 1: Git Churn And History Facts

### Desired Behavior

`metrics operation=churn` and `miller metrics churn --json` report symbols and files touched across a git commit range, with deterministic counts and optional commit detail.

Default behavior:

- Accept `range`, defaulting to a bounded recent range such as `HEAD~20..HEAD` when the workspace is a git repo.
- Read local git history only. No network access.
- Map changed hunks to current symbols using the current `symbols.db`; when the current symbol no longer exists, emit a file-level row with `mapping_basis=file_only`.
- Include `mapping_basis=current_index` for symbol rows so callers know this is current-source mapping, not historical AST reconstruction.
- Bound output with `limit`, default top 50, sorted by commit count, changed lines, last commit time, path, and symbol name.
- Include optional `include_commits=true` for per-row commit ids; default compact JSON omits large commit arrays.

### Implementation Tasks

1. Add `src/Miller.Server/Git/GitHistoryReader.cs`.
   - Shells through the existing process-runner style used by `ProcessGitDiffReader`.
   - Uses `git log` and per-commit `git diff --unified=0` or equivalent plumbing to gather changed hunks.
   - Validates ranges as git arguments, not shell strings.
   - Produces deterministic records: commit id, parent id, author time, path, added/deleted lines, hunks.

2. Add a symbol mapper for churn hunks.
   - Reuse the current diff-to-symbol mapping logic where practical.
   - Return both symbol-level and file-level facts.
   - Never claims historical precision beyond current-index mapping.

3. Add `src/Miller.Server/Tools/MetricsTool.cs` with `operation=churn`.
   - Supports MCP compact and JSON output.
   - Uses workspace resolution, freshness checks, and error formatting consistent with existing read tools.

4. Add CLI dispatch support in a focused helper, for example `src/Miller.Server/Cli/CliMetricsDispatch.cs`.
   - Wire from `CliDispatch` with minimal glue.
   - Add `metrics churn --json --range <range> --limit <n>`.

5. Update contracts/docs.
   - `docs/contracts/cli-eros-v1.md`
   - `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
   - `docs/README.md`

### Acceptance Criteria

- A two-commit fixture maps changed lines to the expected current symbols.
- Deleted/renamed files produce file-level facts rather than false symbol facts.
- Non-git workspaces return a clear, non-crashing error.
- JSON output is deterministic and documented.
- `impact` remains focused on blast radius; churn is exposed through `metrics`.

## Slice 2: Empty-State Candidate Recovery

### Desired Behavior

When a high-traffic read tool misses, Miller suggests likely candidates instead of returning only a generic no-results hint.

Examples:

- `inspect ReadReferencesAsyncc` suggests `ReadReferencesAsync`.
- `search "ReadReferencs"` suggests nearby indexed symbols.
- `trace` and `impact target=` misses include the same bounded suggestion array in JSON.

### Implementation Tasks

1. Add a shared suggestion component, for example `src/Miller.Server/Resolution/SymbolSuggestionEngine.cs`.
   - Inputs: query text, symbol name corpus, optional path/language/kind filters.
   - Scoring: exact case-folded, prefix, tail segment, acronym/camel tokens, substring, and bounded edit-distance similarity.
   - Output: top 5 candidates with name, path, kind, line, and score reason.

2. Replace the narrow `SmartTargetResolver.NearMisses` logic with the shared engine.
   - Preserve existing exact/ambiguous resolution behavior.
   - Only enrich misses.

3. Add search empty-state suggestions.
   - For ordinary symbol search misses, query the same indexed corpus and render a short "Try:" line in compact output.
   - For JSON, add a `suggestions` array.
   - Keep existing region/content mode hints; do not broaden symbol search into source-body search.

4. Propagate suggestions through existing tool outputs.
   - `inspect`
   - `trace`
   - `impact target`
   - `edit`
   - CLI read commands using those cores

### Acceptance Criteria

- Typo tests cover insertion, deletion, transposition, case differences, and suffix misses.
- Suggestions are bounded and stable.
- Existing ambiguity behavior is unchanged.
- Existing empty-state hints remain, now with candidate recovery when candidates exist.
- JSON compatibility tests pass or are intentionally updated with additive fields.

## Slice 3: Safe Leader Diagnostics And Handoff

### Desired Behavior

`workspace leader` reports the current leader state and can request a graceful handoff. This replaces "stop stale Miller processes" with a local, auditable protocol that does not kill processes.

Default diagnostics include:

- Workspace id/root.
- Lock holder/leader identity when available.
- PID and liveness result.
- Extractor version, Miller version/commit when available.
- Whether the current process is eligible to lead.
- Current remediation recommendation.

Explicit handoff:

- `workspace operation=leader handoff=true wait=true` for MCP.
- `miller workspace leader --handoff --wait --json` for CLI.
- Writes a request file to the existing `.miller/requests` queue.
- The leader drains the request and abdicates only if it can verify the requester and the request is safe.
- If the leader is too old to understand handoff, the command reports that the request was queued but not observed before timeout. It must not kill the old process.

### Implementation Tasks

1. Extend the request queue with a separate handoff operation.
   - Add `OperationLeaderHandoff` and request/drain records in `LeaderScanRequestQueue`.
   - Do not overload version-aware `yield`; current `yield` intentionally ignores equal/lower extractor versions.
   - Keep TTL, claim, malformed JSON handling, and Windows-safe delete behavior aligned with existing request types.

2. Extend leadership coordination.
   - Add a leader-side evaluator for explicit handoff requests.
   - Apply requester-alive checks and cooldown before releasing the lease.
   - Keep automatic extractor-version yield behavior unchanged.
   - Log handoff request, acceptance, ignore reason, and cooldown reason.

3. Add `workspace leader` rendering.
   - Implement report building in `WorkspaceTool`.
   - Add pure rendering in `WorkspaceRender`.
   - Include compact text and JSON.

4. Add CLI support.
   - Keep `CliDispatch` glue small.
   - Add JSON capability entries.

5. Update docs and agent instructions.
   - Replace manual "stop stale Miller processes" wording where this is now available.
   - Document that old leaders may not support handoff and must still be handled operationally.

### Acceptance Criteria

- Queue tests prove handoff requests write, drain, expire, skip unclaimed files, and ignore malformed files.
- Coordinator tests prove a valid explicit handoff causes abdication and cooldown.
- Equal-version automatic yield behavior remains unchanged.
- `workspace leader --json` has a stable schema.
- Windows behavior relies on rename/claim and tolerant delete, not process killing.

## Slice 4: Dashboard Health And Onboarding Panels

### Desired Behavior

The dashboard renders the two existing stable JSON contracts:

- `workspace health --json`
- `workspace onboarding`

The panels should make the current workspace detail page useful without requiring a terminal.

Health panel:

- Overall health state.
- Stale/missing sidecar warnings.
- leadership/freshness warnings.
- Capability gaps and parse/index diagnostics.
- Last checked timestamp.

Onboarding panel:

- Suggested starter commands.
- Common misses and recovery hints.
- Hot targets from local telemetry.
- Workspace-specific guidance already present in the onboarding JSON.

### Implementation Tasks

1. Extend dashboard data projection.
   - Add bounded health and onboarding projections to `DashboardData`.
   - Reuse existing readers/contracts where possible.
   - Keep failures local to the panel, not fatal to the whole dashboard.

2. Add Razor components.
   - `src/Miller.Dashboard/Components/WorkspaceHealthPanel.razor`
   - `src/Miller.Dashboard/Components/WorkspaceOnboardingPanel.razor`
   - Wire into `WorkspaceDetailStack.razor`.

3. Style with existing dashboard CSS.
   - Use the established dense operational dashboard style.
   - Avoid nested cards and marketing-style sections.

4. Add dashboard tests.
   - Render healthy, warning, and error states.
   - Verify no panel requests a refresh or full index hydration just to render.

### Acceptance Criteria

- Dashboard detail view displays health and onboarding data when available.
- Missing/corrupt data renders a clear panel-local fallback.
- Existing telemetry/activity/refresh/context panels still render.
- Component tests cover both panels.

## Slice 5: Clone Discovery And Complexity Ranking

### Desired Behavior

`metrics operation=clones` and `metrics operation=complexity` expose deterministic local facts that are already present in the artifact.

Clone discovery:

- Groups symbols by identical non-empty `body_hash`.
- Default `min_count=2`, bounded by `limit`.
- Emits body hash, group size, and symbol refs: name, kind, language, path, line.
- Does not emit source body text.
- Does not suggest cleanup or suppressions.

Complexity ranking:

- Reads extracted complexity metrics.
- Sorts deterministically by severity, decision count, nesting depth, covered lines, path, and symbol name.
- Applies transparent Miller-owned thresholds:
  - `low`: below reporting threshold
  - `moderate`: decision count >= 8 or nesting depth >= 4
  - `high`: decision count >= 15 or nesting depth >= 6
- Supports `limit`, `min_severity`, `include_tests`, and JSON output.
- Keeps `complexity export --jsonl` unchanged.

### Implementation Tasks

1. Add readers in `Miller.Indexing`.
   - `CloneGroupReader`
   - `ComplexityRankingReader`
   - Use bounded SQL queries and deterministic ordering.

2. Extend `MetricsTool`.
   - `operation=clones`
   - `operation=complexity`
   - Compact output for agents, JSON output for contracts.

3. Extend CLI metrics dispatch.
   - `miller metrics clones --json`
   - `miller metrics complexity --json`
   - Keep old `miller complexity export --jsonl` behavior intact.

4. Update docs and capabilities.
   - `CliCapabilities`
   - `docs/contracts/cli-eros-v1.md`
   - `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`

### Acceptance Criteria

- Duplicate body-hash fixture returns exactly the expected clone groups.
- Singletons and empty hashes are excluded.
- Complexity thresholds classify rows correctly.
- Existing complexity export tests still pass.
- No output implies remediation, cleanup, or semantic similarity.

## Cross-Cutting Task: Contract And CLI Hygiene

### Desired Behavior

The new surfaces are discoverable without making `CliDispatch` harder to maintain.

### Implementation Tasks

1. Add or update capability metadata.
   - MCP tool registration for `metrics`.
   - `capabilities --json` lists new JSON contracts.
   - Agent instructions document the new tool and its boundaries.

2. Keep CLI parsing local.
   - Add `CliMetricsDispatch`.
   - Add small workspace-leader helpers if `workspace` parsing would otherwise add large inline blocks.
   - Do not refactor unrelated CLI commands in this slice.

3. Add docs.
   - `docs/contracts/cli-eros-v1.md`
   - `docs/README.md`
   - A short plan completion note under `docs/findings/` after implementation and verification.

### Acceptance Criteria

- `miller help` and `capabilities --json` expose the new commands.
- Agent instructions include `metrics` and `workspace leader`.
- `CliDispatch` does not absorb large new command bodies.

## Verification Strategy

Run focused tests during each slice, then run the repo gate once the combined branch is ready.

Per-slice focused checks:

- Empty-state recovery:
  - `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~SmartTargetResolverTests`
  - `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~SearchToolTests`
- Leader handoff:
  - `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~WorkspaceToolTests`
  - `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~CliDispatchTests`
  - Add/run coordinator and request-queue tests.
- Metrics:
  - `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~CliDispatchTests`
  - Add/run `MetricsToolTests`, `GitHistoryReaderTests`, `CloneGroupReaderTests`, and `ComplexityRankingReaderTests`.
- Dashboard:
  - `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~Dashboard`

Final gate:

```bash
scripts/test.sh
dotnet build Miller.slnx -c Release
```

Run scale only if a slice touches extractor launch/index refresh paths:

```bash
scripts/test.sh scale
```

If `CLAUDE.md` or `AGENTS.md` changes:

```bash
scripts/sync-agents.sh
cmp -s CLAUDE.md AGENTS.md
```

## Model Routing

- No repo-local `RAZORBACK.md` routing file exists.
- Use `inherit` for low, medium, and high complexity execution unless a later implementation owner chooses an external review.
- Suggested worker split:
  - Worker A: empty-state recovery.
  - Worker B: metrics churn.
  - Worker C: leader diagnostics/handoff.
  - Worker D: dashboard panels.
  - Worker E: clone/complexity metrics.
- The lead agent must review each worker output against this plan, run the relevant focused tests, then run the final gate.

## Out Of Scope

- Semantic/vector search.
- Fleet-level hotspot ranking or historical trend storage.
- Clone cleanup workflows, suppressions, or code rewrite suggestions.
- Parser-backed extraction changes.
- Killing stale processes.
- `workspace_id=all` symbol reads.
