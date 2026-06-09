# Miller Data Opportunities Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Capture and sequence the next Miller-owned features that use existing Miller data: workspace intelligence/health, reference-aware context, and stable trace JSON.

**Architecture:** Keep Miller as the local fact and contract owner. Add narrow CLI/MCP surfaces over existing sidecar/index/telemetry data, keep commercial aggregation in Eros, and avoid tree-sitter structural extraction or semantic retrieval inside Miller.

**Tech Stack:** .NET 10, xUnit v3, SQLite, MCP tools, Miller CLI JSON/compact renderers, existing `julie-extract` SQLite contract, content/search/telemetry sidecars.

**Architecture Quality:** Medium risk. The candidates touch caller-facing CLI/MCP contracts, workspace/read-path performance, and Eros-facing public JSON. Each candidate needs a focused design before implementation; this plan is the shared sequence and boundary document, not implementation approval.

---

- **Date:** 2026-06-09
- **Status:** Implemented in `codex/miller-data-opportunities`
- **Scope:** Completed feature bundle covering workspace health, trace JSON, and reference-aware context.

## Boundary

Miller owns the free local code-intelligence core: local extraction consumption, sidecars, search, inspect, context, trace, impact, content corpus, telemetry export, workspace state, and stable CLI/MCP contracts.

Eros should build higher-level commercial workflows on top of Miller public contracts. If Eros needs facts that Miller has but does not expose, Miller should add a narrow stable CLI/export contract. Eros should not read Miller private SQLite files, call Miller private .NET internals, or duplicate Miller's local code-intelligence tools.

## Current Data Miller Can Leverage

Miller already has these useful fact sets:

- `symbols.db`: files, symbols, relationships, pending relationships, identifiers, literals, parse diagnostics, language capabilities, language capability gaps, parser inventory, extraction revisions.
- `search.db`: default-on symbol search sidecar with revision metadata and BM25 ranking in C#.
- `content.db`: source/docs/config/external/web chunks, line and byte spans, test/source flags, source hashes, and containing-symbol metadata.
- `telemetry.db`: tool calls, operation, duration, outcome, result count, bytes examined/returned, estimated tokens, freshness, target hashes, and metadata.
- Workspace registry and status facts for current and cross-workspace routing.

## File Surfaces And Ownership

Likely shared/public surfaces:

- Modify as needed: `src/Miller.Server/Cli/CliDispatch.cs`
- Modify as needed: `src/Miller.Server/Cli/CliCapabilities.cs`
- Modify as needed: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Modify as needed: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`
- Modify as needed: `README.md`, `docs/contracts/*.md`, `CLAUDE.md`, `AGENTS.md`

Candidate 1 likely files:

- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs`
- Modify or split from: `src/Miller.Server/Tools/WorkspaceRender.cs`
- Create: `src/Miller.Indexing/WorkspaceHealthReader.cs`
- Modify if dashboard follows later: `src/Miller.Dashboard/DashboardData.cs`, `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor`
- Test: `tests/Miller.Tests/Server/WorkspaceToolTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`
- Test if a reader is added: `tests/Miller.Tests/Indexing/WorkspaceHealthReaderTests.cs`
- Test CLI JSON: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

Candidate 2 likely files:

- Modify: `src/Miller.Server/Tools/ContextTool.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Modify or extend through a small reader: `src/Miller.Indexing/ExtractReader.cs`
- Use existing content search: `src/Miller.Indexing/FtsTextContentSearchIndex.cs`
- Test: `tests/Miller.Tests/Server/ContextToolTests.cs`
- Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- Test reference reads: `tests/Miller.Tests/Indexing/ExtractReaderTests.cs`
- Live/scale dogfood when needed: `tests/Miller.Tests/Server/LiveContextImpactTests.cs`

Candidate 3 likely files:

- Modify: `src/Miller.Server/Tools/TraceTool.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Modify: `src/Miller.Server/Cli/CliCapabilities.cs`
- Create: `docs/contracts/trace-v1.md`
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs`
- Test CLI JSON: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- Test bridge graph fixtures: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`
- Live/scale bridge proof when touched: `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs`

Workers must not edit shared contract/docs files concurrently with implementation files. The lead owns public JSON shape, capability advertisement, and final docs sync.

## Architecture Quality

**Affected modules:** `Miller.Server` CLI/MCP tools, `Miller.Indexing` cheap readers and sidecar readers, `Miller.Core` graph/search models, `Miller.Dashboard` if health gets a UI panel, tests, public contracts, and Eros-facing docs.

**Caller-facing interface:** CLI commands, MCP tool parameters/results, compact text output, `capabilities --json`, contract docs, and Eros-visible JSON/export shapes.

**Depth/locality check:** Each candidate should land as a separate feature slice. Health should read cheap facts without hydrating full indexes. Reference-aware context should reuse existing context/search/reader seams instead of adding a second context system. Trace JSON should introduce a stable result DTO, not expose private graph internals.

**Test surface:** Tests must prove behavior through the same surfaces callers use: MCP tool tests, CLI dispatch tests, JSON contract assertions, and focused indexing reader tests only where public behavior needs cheap fixtures.

**Seams/adapters:** Preserve `WorkspaceTool`/`WorkspaceRender`, `WorkspaceIndexProvider`, `ExtractReader`, text content search, `TraceTool`, bridge graph providers, and CLI dispatch as ownership boundaries. Add new seams only when they keep storage policy out of tool renderers or prevent full-index hydration.

**Rejected shortcuts:** Do not parse private human trace text for Eros, do not read Miller private SQLite directly from Eros, do not broaden symbol search with content/source text, do not add tree-sitter structural extraction policy to Miller, and do not treat extractor capability gaps as Miller-owned parsing policy.

**Architecture risk:** medium.

## Model Routing

**Project source of truth:** No repo-local `RAZORBACK.md` is present. Use harness defaults unless a future session adds explicit routing.

**Strategy tier:** planning, contract shape, architecture review, Eros/Miller boundary decisions
- Harness mapping: inherit

**Implementation tier:** bounded feature tasks after a focused design is approved
- Harness mapping: inherit

**Mechanical tier:** docs sync, capability list updates, compact text copy changes with no gate interpretation
- Harness mapping: inherit

**Gate-interpretation reviewer:** JSON contract review, trace confidence interpretation, health warning semantics, dogfood metric interpretation
- Harness mapping: inherit

**Escalation tier:** public JSON contract changes, read-path performance regressions, cross-language/extractor support claims, Eros dependency on a new Miller contract
- Harness mapping: inherit

**Worker eligibility:** A worker may implement one candidate only after its focused design is approved and the file scope is bounded.

**Escalation triggers:** Any change to public JSON, `capabilities --json`, dashboard contract surfaces, Scale/live bridge tests, or extractor-derived support interpretation returns to the lead.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, trace/health contract interpretation, performance evidence, or Eros boundary decisions.

**Unsupported harness behavior:** If the harness cannot select models per worker, inherit the active model and note that in the run ledger.

## Candidate 1: Workspace Intelligence And Health Report

**What to build:** Add a compact workspace health/intelligence surface that answers, "Can an agent trust this workspace index right now, and what should it know before using it?"

**Why:** Miller already exposes status and sidecar state, but the richer quality facts are scattered across index metadata, parse diagnostics, language capability gaps, content skipped counts, sidecar freshness, and telemetry. A single report would make Miller more trustworthy for agents and would give Eros a clean input for portfolio health dashboards.

**Likely surfaces:**

- CLI: `miller workspace health --json [--workspace-id SELECTOR]`
- MCP: either `workspace(operation="health")` or a narrow `health` operation if the existing workspace tool shape fits.
- Dashboard: later summary panel after the CLI/MCP contract is stable.

**Data flow:**

1. Resolve the workspace through the existing registry path.
2. Ensure freshness only when the caller requests it; default should follow current read-tool behavior.
3. Read cheap facts without hydrating full symbol graphs.
4. Summarize sidecar freshness, content corpus health, parse diagnostics, capability gaps, skipped content, recent telemetry error/empty rates, and basic indexed size.
5. Return compact text plus stable JSON.

**Acceptance criteria:**

- [x] Does not hydrate the full repository index for the common health path.
- [x] JSON includes workspace id/root, index revision, sidecar states, content corpus counts/skips, parse diagnostic summary, capability gap summary, recent telemetry summary, and warnings.
- [x] Compact text gives an agent a short trust/readiness answer.
- [x] Tests prove lock-busy/stale/corrupt/missing sidecar states produce clear warnings, not silent success.
- [x] Eros can consume the JSON without private Miller internals.

**Risks:**

- Too much data could turn into a noisy report. Keep the first slice focused on trust/readiness and top warnings.
- Some capability gaps originate in `julie-extractors`; Miller should report them, not reinterpret extractor support policy.

## Candidate 2: Reference-Aware Context

**What to build:** Improve `context` so it can include the definition, key references, nearby related content chunks, and an explicit caller/callee thread mode around the user's target or query.

**Why:** Miller has name-based identifiers, containing-symbol metadata in content chunks, and graph edges. Current context assembly can be smarter without embeddings by using possible references and containment to pick evidence that helps an agent understand how a symbol is actually used.

**Likely surfaces:**

- Extend `context` with an option such as `reference_depth`, `include_references`, or a mode name after design approval.
- Keep default output budgeted and compact; the goal is better selection, not larger output.

**Data flow:**

1. Resolve query seeds through existing search/target resolution.
2. Load definition candidates from the symbol lookup projection.
3. Pull bounded name-based references from `identifiers` and label them as possible references.
4. Pull content chunks whose `containing_symbol_id` or `containing_symbol_name` matches selected symbols.
5. Optionally use graph neighbors for caller/callee context when the user requests it.
6. Rank and dedupe by directness, file diversity, symbol kind, and token budget.

**Acceptance criteria:**

- [x] Default `context` behavior remains fast and bounded.
- [x] New reference-aware mode improves at least one documented dogfood query where ordinary symbol/content search is thin.
- [x] Output identifies why each section was included: definition, possible reference, containing chunk, graph neighbor, or callee identifier.
- [x] Tests cover duplicate references, missing content data, ambiguous symbols, test-file exclusion, and token budget enforcement.
- [x] Implementation preserves `Miller.Core` purity and keeps storage reads behind existing indexing/server seams.

**Risks:**

- Reference data quality varies by language. The output must expose confidence and degrade gracefully.
- Ranking can become speculative. Prefer simple deterministic ordering first, then dogfood before adding more scoring.

## Candidate 3: Stable JSON Trace Contract

**What to build:** Add `trace --json` and matching MCP JSON behavior for trace results, especially `mode=bridge`, without changing the human compact trace output.

**Why:** Trace is useful today, but it is text-only. Eros and other consumers need stable structured links if they want to build commercial cross-language dashboards, change-risk flows, or route/entity maps without parsing human text.

**Likely surfaces:**

- CLI: `miller trace <target> --json [--mode auto|path|bridge] [--to TARGET]`
- MCP: `trace(format="json")` or equivalent parameter aligned with existing tool conventions.
- Capabilities: advertise trace JSON support through `capabilities --json`.

**Data flow:**

1. Keep existing trace graph construction and provider-scoped bridge logic.
2. Introduce DTOs for trace nodes, links, provider capability metadata, confidence, reduced-confidence flags, and diagnostics.
3. Render compact human text from the same trace result model where practical.
4. Add JSON contract documentation under `docs/contracts/`.
5. Add Eros contract notes only after the Miller shape is stable.

**Acceptance criteria:**

- [x] JSON supports `auto`, `path`, and `bridge` modes.
- [x] Bridge JSON includes provider, capability status, nodes, links, confidence, flags such as `verb_unknown` or `ambiguous`, and diagnostics.
- [x] No human text parsing is required for Eros or tests.
- [x] `capabilities --json` reports trace JSON support and contract version.
- [x] Tests pin stable shapes for empty/no-provider, ambiguous, path-not-found, and successful bridge traces.

**Risks:**

- JSON contract design is public API. Keep v1 small and explicit rather than mirroring private graph internals.
- Bridge confidence is provider-scoped evidence, not generic semantics. The contract should say that clearly.

## Completed Sequence

1. **Workspace Intelligence And Health Report**
   - Implemented in this branch. It uses existing data, improves agent trust immediately, and gives Eros a
     clean future portfolio-health input. Dashboard rendering and Eros aggregation remain deferred.
2. **Stable JSON Trace Contract**
   - Implemented in this branch before Eros builds route/entity/risk dashboards that need trace data.
3. **Reference-Aware Context**
   - Implemented in this branch as opt-in `reference_mode=usage`, with explicit reason/confidence labels and
     dogfood evidence in `docs/plans/2026-06-09-reference-aware-context-design.md`.

## Extractor-Backed Opportunities After v2.2.0

These ideas started in `julie-extractors`, not Miller, because they are parser-backed facts:

- tree-sitter structural query,
- AST complexity metrics,
- normalized body-hash or near-duplicate detection.

`julie-extractors` v2.2.0 now exposes the required primitives under SQLite/report contract v3:
`structural_facts`, `complexity_metrics`, and clone-ready `symbols.body_hash` semantics. Miller's first consume slice
is intentionally narrow: pin `julie-extract` v2.2.0, accept schema/contract/report version 3, keep fast fixtures in
step, and report structural/complexity fact availability through `workspace health --json`.

Future Miller-owned slices should be designed separately:

- structural-fact search/filtering over stable `pattern_id` values,
- complexity reporting/ranking with Miller-owned thresholds and no extractor-side quality labels,
- duplicate/clone discovery over normalized `body_hash`.

Eros should consume these through Miller CLI/MCP/export contracts, not by reading Miller private SQLite state.

## Verification Strategy For Any Future Implementation

**Project source of truth:** `CLAUDE.md`, generated mirror `AGENTS.md`, `scripts/test.sh`, `scripts/test.ps1`, `scripts/test-plugin.sh`, `docs/contracts/`, and `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`.

**Worker red/green scope:** Focused xUnit tests for the touched tool/reader/CLI surface. Examples: `ContextToolTests`, `TraceToolTests`, `WorkspaceToolTests`, `WorkspaceRenderTests`, `CliDispatchTests`, or a new indexing reader test.

**Worker ceiling:** Workers may run focused tests, `scripts/test.sh`, `dotnet build Miller.slnx -c Release`, and targeted Scale tests when their candidate touches live extraction/indexing or bridge traces.

**Worker gate invariant:** The assigned gate must prove caller-visible behavior: compact output, JSON shape, capability advertisement, warning semantics, reference selection, or trace link structure.

**Lead affected-change scope:** After a coherent feature slice, run `scripts/test.sh`, `dotnet build Miller.slnx -c Release`, `git diff --check`, and any focused Scale/bridge/content corpus gate triggered by touched files.

**Branch gate:** Before merge or handoff, run:

```bash
dotnet build Miller.slnx -c Release
scripts/test.sh
scripts/test.sh scale
git diff --check
```

Run `scripts/test-plugin.sh` if plugin manifests, launchers, package shape, or MCP config files changed.

**Replay/metric evidence:** Health report work needs representative workspace status/health samples. Reference-aware context needs at least one documented dogfood query showing improved evidence selection under a fixed token budget. Trace JSON needs JSON samples for empty, path, auto, and bridge cases.

**Escalation triggers:** Broaden to Scale/live tests when touching extraction/indexing, sidecar readers, bridge graph construction, or real workspace refresh behavior. Broaden to Eros contract review before Eros consumes a new Miller JSON surface.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the failure is the expected red test for their own implementation.

**Verification ledger:** Record command, invariant, scope, result, commit SHA, timestamp, and any report-only dogfood observations in the candidate design or findings doc.
