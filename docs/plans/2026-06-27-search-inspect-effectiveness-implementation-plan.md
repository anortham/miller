# Search And Inspect Effectiveness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Close the measured Miller-vs-Julie effectiveness gaps in the existing `search` and `inspect` surfaces without adding MCP tools.

**Architecture:** Keep `search auto` symbol/file-first, then add a bounded source-text rescue only when the normal route is weak or misses. Improve inspect target selection inside `SmartTargetResolver`, then add an `inspect` overview depth that reuses the current symbol-detail reader while producing smaller output. The benchmark matrix becomes the regression harness for the work.

**Tech Stack:** .NET 10, existing `Miller.Server.Tools` MCP/CLI surfaces, `Miller.Indexing` symbol and content sidecars, xUnit fast tests, `scripts/bench-julie-miller-search-inspect.py`.

**Architecture Quality:** Affected modules are `SearchTool`/`SearchRouteExecutor`, `SmartTargetResolver`, `InspectTool`, CLI dispatch/help, agent instructions, and focused fast tests. Caller-facing interfaces stay inside existing `search` and `inspect`; the only interface extension is `inspect depth=overview`. Architecture risk is medium because these are high-use tools, but locality is good: rescue policy lives in search execution, target-ranking policy lives in resolver, and overview rendering lives in inspect rendering.

## Global Constraints

- Do not add a new MCP tool.
- Do not reintroduce the removed metrics MCP surface.
- Do not add semantic/vector search to Miller; keep semantic workflows in Eros.
- Preserve explicit `mode=source` behavior and result shape.
- Keep `search auto` symbol/file-first; source rescue is bounded and conditional.
- Preserve JSON compatibility for existing search and inspect consumers; any new JSON fields must be additive or gated behind the new `depth=overview`.
- Keep natural-language test hiding behavior unless a task explicitly changes it.
- Update prompt-facing docs when changing tool behavior.
- Use TDD for each behavior slice and run the narrowest useful verification before broad gates.

---

## Source Evidence

Benchmark artifacts:

- `docs/findings/2026-06-27-julie-miller-search-inspect-benchmark.md`
- `docs/findings/benchmarks/2026-06-27-search-inspect/summary.md`
- `scripts/bench-julie-miller-search-inspect.py`

Measured gaps to close:

- `miller.search.auto` source-body intent: `5/9` top, `6/9` present.
- `miller.search.source` source-body intent: `8/9` top, `9/9` present.
- `miller.inspect.full` output median: `4409` chars versus Julie `deep_dive overview` median `1129` chars.
- `inspect` found all expected files, but first visible target was sometimes a test/example/older-version definition.

Measured strengths to protect:

- `miller.search.auto` exact symbol intent: `8/9` top, `9/9` present.
- `miller.search.auto` file intent: `7/9` top, `7/9` present.
- Warm-read latency is acceptable; this plan is about routing, ranking, and output shape.

## Current Orientation Notes

- MCP `search` entry point: `src/Miller.Server/Tools/SearchTool.cs:133`.
- Pure symbol/file search core: `src/Miller.Server/Tools/SearchTool.cs:541`.
- Explicit source/content rendering already exists in `src/Miller.Server/Tools/SearchTool.cs:710`, `:816`, and `:880`.
- Route planning maps `mode=source` to text content and leaves `auto` on symbols in `src/Miller.Server/Tools/SearchRoutePlanner.cs:23`.
- Search execution wrapper is in `src/Miller.Server/Tools/SearchRouteExecutor.cs:19`.
- Target resolution is centralized in `src/Miller.Server/Resolution/SmartTargetResolver.cs:57`, with ambiguous-name policy in `ResolveByName` and `SingleDefinitionCandidate`.
- Inspect symbol rendering is centralized in `src/Miller.Server/Tools/InspectTool.cs:347` and JSON rendering in `:423`.
- CLI `inspect --depth` dispatch lives in `src/Miller.Server/Cli/CliDispatch.cs:628`.

## File Structure

Modify:

- `src/Miller.Server/Tools/SearchTool.cs:133-317` - route-level source rescue, telemetry, and compact rendering integration.
- `src/Miller.Server/Tools/SearchTool.cs:541-648` - pure search result classification helpers if needed by route-level rescue.
- `src/Miller.Server/Tools/SearchRouteExecutor.cs:19-41` - keep pure symbol execution unchanged unless a small result envelope is needed for reuse.
- `src/Miller.Server/Resolution/SmartTargetResolver.cs:112-157` - deterministic definition ranking for inspect/edit/trace/impact target resolution.
- `src/Miller.Server/Tools/InspectTool.cs:39-220` - accept and route `depth=overview`.
- `src/Miller.Server/Tools/InspectTool.cs:347-512` - compact overview rendering and JSON shape.
- `src/Miller.Server/Cli/CliDispatch.cs:628-658` - document and route `--depth overview`.
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` - document source rescue and `inspect depth=overview`.
- `scripts/bench-julie-miller-search-inspect.py` - add hard-gate checks for the measured acceptance targets.

Test:

- `tests/Miller.Tests/Server/SearchToolTests.cs`
- `tests/Miller.Tests/Server/SearchRouteExecutorTests.cs`
- `tests/Miller.Tests/Server/SmartTargetResolverTests.cs`
- `tests/Miller.Tests/Server/InspectToolTests.cs`
- `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

## Task 1: Benchmark Regression Gate

**Files:**

- Modify: `scripts/bench-julie-miller-search-inspect.py`
- Modify: `docs/findings/2026-06-27-julie-miller-search-inspect-benchmark.md`
- Create or update: `docs/findings/benchmarks/<run-id>/summary.md`

**Interfaces:**

- Consumes: current benchmark CSV and summary generation.
- Produces: repeatable report-only numbers plus explicit hard-gate assertions for Miller behavior.

**What to build:** Add a gate mode to the existing benchmark runner so implementation work can prove it moved the measured gaps and did not regress protected strengths.

**Approach:** Add a `--gate` or equivalent flag that exits non-zero only on Miller acceptance failures. Keep Julie comparison numbers report-only because Julie is a baseline, not the product under test. Preserve CSV output for trend analysis.

**Acceptance criteria:**

- [x] Gate fails when `miller.search.auto` source-body present count is below `8/9`.
- [x] Gate fails when `miller.search.auto` exact-symbol present count falls below `9/9`.
- [x] Gate fails when `miller.search.auto` file present count falls below `7/9`.
- [x] Gate records `inspect` median output chars for `full` and `overview` but treats char counts as report-only until `overview` exists.
- [x] Gate output names the failing provider/task group and the expected threshold.
- [x] Worker-scope verification passes.

## Task 2: Search Auto Source Rescue

**Files:**

- Modify: `src/Miller.Server/Tools/SearchTool.cs:133-317`
- Modify: `src/Miller.Server/Tools/SearchTool.cs:541-648`
- Modify: `tests/Miller.Tests/Server/SearchToolTests.cs`
- Modify if needed: `tests/Miller.Tests/Server/SearchRouteExecutorTests.cs`

**Interfaces:**

- Consumes: existing symbol/file `SearchTool.Run`, text-content source search through `IWorkspaceTextContentSearchProvider`, `SearchRoutePlanner` source route behavior.
- Produces: unchanged primary `search auto` output when symbol/file results are strong; compact source-match addendum when rescue fires.

**What to build:** For default `search auto`, keep the current symbol/file route first, then query workspace source text only when the normal result is empty or weak enough that a source-body query is likely. This closes the measured gap where explicit `mode=source` works but agents do not choose it.

**Approach:**

- Add a small internal policy helper near search routing, for example `ShouldRunAutoSourceRescue(query, mode, json, symbolCount, fileMode, topScoreOrShape)`.
- Rescue candidates should include phrase/body-like queries: spaces, punctuation, quotes, operators, error strings, method-call fragments, or queries that `ResolveHideLowSignalKinds` already treats as natural language.
- Rescue should also run on empty `auto` symbol results, unless the query is path-like file mode.
- Do not run rescue for explicit `mode=symbol`, `mode=file`, `mode=source`, `mode=content`, `regions`, or JSON in the first slice unless a compatible JSON shape is deliberately added.
- Use the existing text-content provider and `TextContentKind.WorkspaceSource`; do not read source files directly.
- Bound the rescue to a small limit, for example two source hits, and a small source-search overfetch.
- Compact output should append a short section such as:

  ```text
  Source matches also found:
  src/Api.cs:42
    throw new InvalidOperationException("KnownSourceError");
  ```

- Existing symbol/file output remains first. If there are no symbol/file results, the source matches replace the generic no-results hint and include a hint to rerun `mode=source` for more snippets.
- Telemetry should still classify the tool as `search`; add metadata that rescue was attempted and whether it found rows. Preserve `ResultCount` as the primary rendered rows or explicitly include source rows in the count, then test that choice.

**Acceptance criteria:**

- [x] `search("KnownSourceError")` in auto mode renders a source match from workspace source content when symbol search is empty.
- [x] A strong exact-symbol query still resolves only the symbol provider; update the existing `Search_NonContentMode_DoesNotResolveContentProvider` expectation only if the new policy intentionally allows rescue for weak/empty cases, not for strong symbols.
- [x] Auto source rescue does not fire for path-like file queries.
- [x] Auto source rescue does not change explicit `mode=source` compact or JSON shapes.
- [x] JSON `search auto` remains backward-compatible.
- [x] Benchmark gate improves source-body auto present count to at least `8/9`.
- [x] Existing symbol/file benchmark thresholds do not regress.
- [x] Worker-scope verification passes.

## Task 3: Inspect Target Ranking

**Files:**

- Modify: `src/Miller.Server/Resolution/SmartTargetResolver.cs:112-157`
- Modify: `tests/Miller.Tests/Server/SmartTargetResolverTests.cs`
- Modify if needed: `tests/Miller.Tests/Server/InspectToolTests.cs`

**Interfaces:**

- Consumes: `ISymbolLookupIndex.FindByName`, current `TargetResolution.Symbol/Candidates/NotFound` contract.
- Produces: a deterministic preferred definition only when ranking gives one clear winner; otherwise candidates remain explicit.

**What to build:** Improve ambiguous symbol resolution so inspect does not pick tests, examples, generated helpers, imports, constructors, or older package paths when there is a clear concrete definition elsewhere.

**Approach:**

- Replace `SingleDefinitionCandidate` with a ranking helper that filters name-lookup noise, scores candidates, and returns a symbol only when the top candidate is unique.
- Ranking factors should be deterministic and conservative:
  - prefer non-noise kinds over imports/modules/constructors,
  - prefer non-test paths via `IsTestPath.IsTest`,
  - prefer source roots such as `src/`, `lib/`, `crates/*/src/`, `app/`, `packages/*/src/` over `test/`, `tests/`, `spec/`, `examples/`, `samples/`, `fixtures/`, `bench/`, `generated/`, `dist/`,
  - prefer shorter path distance only as a tie-breaker,
  - do not encode repo-specific package names like Flask or Zod.
- If two real definitions tie after scoring, return `TargetResolution.Candidates` rather than picking one silently.
- Keep scoped resolution behavior unchanged: a supplied `scope` must still disambiguate by file.

**Acceptance criteria:**

- [x] Existing single-definition-plus-imports test still resolves to the concrete definition.
- [x] New test with a production definition and a test/example duplicate resolves to production.
- [x] New test with two equally plausible production definitions returns candidates.
- [x] Wrong-scope and near-miss suggestion behavior is unchanged.
- [x] Benchmark inspect cases stop surfacing test/example/old-version paths first when a clear production definition exists.
- [x] Worker-scope verification passes.

## Task 4: Inspect Overview Depth

**Files:**

- Modify: `src/Miller.Server/Tools/InspectTool.cs:39-220`
- Modify: `src/Miller.Server/Tools/InspectTool.cs:347-512`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs:628-658`
- Modify: `tests/Miller.Tests/Server/InspectToolTests.cs`
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Interfaces:**

- Consumes: existing `depth` string, `ExtractReader.ReadDetail`, `ReadSymbolComplexity`, `FindChildren`, `ReadReferences`, `ReadCallees`, `ReadBody`.
- Produces: `depth=overview` for MCP and CLI with compact edit-orientation output.

**What to build:** Add an overview depth that is more useful than summary but far smaller than full. It should provide enough context to choose the next inspect or edit target without dumping the whole symbol body and full reference lists.

**Approach:**

- Treat depth case-insensitively: `summary`, `overview`, `full`.
- For file targets, keep existing file-summary behavior unless a later task proves file overview is needed.
- For symbol targets, render:
  - header, path, signature, visibility, doc comment,
  - complexity if present,
  - top children capped to a small number,
  - top references/callers/callees capped lower than `full`, for example five each,
  - a bounded body preview instead of the full body, for example first 40 non-empty lines or a character cap with an explicit truncation note.
- JSON overview should be structurally compatible with summary/full by including the `symbol` object and additive overview-only arrays/preview fields. It must not pretend to be full output.
- CLI usage changes from `--depth summary|full` to `--depth summary|overview|full`.

**Acceptance criteria:**

- [x] `inspect target depth=overview` includes definition, docs/signature, complexity when present, bounded children, bounded refs/callers/callees, and body preview.
- [x] `depth=summary` output is unchanged.
- [x] `depth=full` output is unchanged.
- [x] Overview JSON is parseable and includes a clear body-preview/truncated indicator.
- [x] CLI accepts `--depth overview` and routes it through the same behavior.
- [x] Benchmark records overview median chars near the target range of `1000-2000` for the current matrix without using that range as a brittle unit-test assertion.
- [x] Worker-scope verification passes.

## Task 5: Prompt-Facing Docs And Instructions

**Files:**

- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Modify: `CLAUDE.md`
- Regenerate: `AGENTS.md` via `scripts/sync-agents.sh`
- Modify if needed: `README.md` or `docs/README.md` only if public quickstart wording references inspect depth values.
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

**Interfaces:**

- Consumes: new behavior from Tasks 2-4.
- Produces: prompt-facing guidance that teaches agents the effective default path without expanding the MCP surface.

**What to build:** Update docs so agents understand that `search auto` can surface source matches, explicit `mode=source` remains the deeper source-text path, and `inspect depth=overview` is the default next step before `full`.

**Approach:**

- Update MCP agent instructions first.
- Update `CLAUDE.md`, then run `scripts/sync-agents.sh` to regenerate `AGENTS.md`.
- Avoid overstating behavior; say source rescue is bounded and explicit `mode=source` is still preferred for full source-text search.

**Acceptance criteria:**

- [x] Agent instructions document `inspect depth=overview`.
- [x] Agent instructions still direct source-body searches to `mode=source` when that is the user intent.
- [x] `CLAUDE.md` and `AGENTS.md` are byte-for-byte synchronized after running the sync script.
- [x] Agent instruction tests pass.
- [x] Worker-scope verification passes.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` / `AGENTS.md` testing and build sections.

**Worker red/green scope:** Focused xUnit tests for the touched tool:

- Search slice: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~SearchToolTests|FullyQualifiedName~SearchRouteExecutorTests"`
- Resolver slice: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~SmartTargetResolverTests|FullyQualifiedName~InspectToolTests"`
- Inspect overview slice: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~InspectToolTests|FullyQualifiedName~CliDispatchTests"`
- Docs/instructions slice: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~AgentInstructionsTests"`

**Worker ceiling:** Workers may run `scripts/test.sh` after their slice. Scale tests are not required unless implementation touches indexing, extraction, or sidecar build paths.

**Worker gate invariant:** Focused tests prove caller-facing behavior through `search`, `inspect`, CLI dispatch, and resolver outputs rather than private helpers alone.

**Lead affected-change scope:** Run `scripts/test.sh`, then run the benchmark gate:

```bash
scripts/test.sh
python3 scripts/bench-julie-miller-search-inspect.py --gate
```

**Branch gate:** Run `dotnet build Miller.slnx -c Release` and `scripts/test.sh`. Run `scripts/test.sh scale` only if code changes move into indexing/extract/sidecar generation.

**Replay/metric evidence:** Benchmark thresholds are hard gates only for Miller acceptance targets. Julie comparison numbers, latency, and char-count medians are report-only trend evidence unless this plan explicitly marks them as thresholds.

**Escalation triggers:** Broaden to `scripts/test.sh all` if implementation changes `WorkspaceIndexProvider`, sidecar loading, extractor interaction, or shared read-tool routing. Stop for user decision if benchmark improvements require adding a new MCP tool, semantic retrieval, or broad source-text ranking in symbol search.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the failure is in a test this plan explicitly says to update.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the implementation notes or final report. For benchmark evidence, include source-auto present/top, symbol-auto present/top, file-auto present/top, and inspect overview median chars.

## Model Routing

**Project source of truth:** No `RAZORBACK.md` was found in this repo during planning. Use the current harness defaults unless the user specifies a reviewer/model choice at approval time.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage.

- Harness mapping: inherit.

**Implementation tier:** bounded worker tasks from this plan.

- Harness mapping: inherit.

**Mechanical tier:** docs, fixtures, formatting, and benchmark report generation with no gate interpretation.

- Harness mapping: inherit.

**Gate-interpretation reviewer:** lead agent for benchmark and test-gate interpretation.

- Harness mapping: inherit.

**Escalation tier:** subtle ranking correctness, compatibility risk in JSON output, repeated benchmark failures, or pressure to add semantic retrieval/new MCP tools.

- Harness mapping: inherit.

**Worker eligibility:** Workers may implement Tasks 1-5 when they keep changes inside the named files and verify through the caller-facing tests listed above.

**Escalation triggers:** Any need to add a new MCP tool, alter the stable JSON shape of existing search output, or move semantic/vector behavior into Miller must return to the lead and user.

**Mechanical exclusion:** Mechanical workers cannot own benchmark interpretation, failing-test adjudication, or acceptance-gate decisions.

**Unsupported harness behavior:** If the harness cannot choose models per worker, use inherit and continue.

## Execution Order

1. Task 1: add the benchmark gate first so every behavior change has live evidence.
2. Task 2: implement source rescue because it closes the largest measured gap with the least architecture risk.
3. Task 3: improve resolver ranking so later overview output starts from the right symbol.
4. Task 4: add overview depth after target ranking is stable.
5. Task 5: update prompt-facing docs and instructions.

## Final Acceptance

- [x] No new MCP tool is added.
- [x] `search auto` source-body present count is at least `8/9` on the benchmark matrix.
- [x] `search auto` exact-symbol present count remains `9/9`.
- [x] `search auto` file present count remains at least `7/9`.
- [x] `inspect` target selection avoids test/example/old-version first results when a clear production definition exists.
- [x] `inspect depth=overview` exists for MCP and CLI and is substantially smaller than `full`.
- [x] `scripts/test.sh` passes.
- [x] `dotnet build Miller.slnx -c Release` passes.
- [x] Benchmark summary is regenerated and linked from the final report.
