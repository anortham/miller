# Search Inspect Recovery Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Harden the existing `search` and `inspect` tools so agents recover from weak first-call results and ambiguous targets without adding MCP tools or moving semantic retrieval into Miller.

**Architecture:** Keep `search auto` symbol/file-first and add bounded source/docs rescue only when the primary result is weak, ambiguous, or empty. Reuse `SearchTool`, `SearchRoutePlanner`, `IWorkspaceTextContentSearchProvider`, `SmartTargetResolver`, and `CandidateOutput`; do not create a parallel retrieval path. For `inspect`, improve candidate ordering and render copyable scoped retry examples while keeping tie cases explicit.

**Tech Stack:** .NET 10, Miller MCP/CLI tools, existing symbol/search/content sidecars, xUnit fast tests, `scripts/bench-julie-miller-search-inspect.py`, and `scripts/bench-foundation-matrix.py`.

**Architecture Quality:** Affected modules are `SearchTool`, `SmartTargetResolver`, `InspectTool`, `CandidateOutput`, CLI read-command rendering where needed, agent instructions, and benchmark evidence docs. Caller-facing interfaces stay inside existing `search` and `inspect` outputs; any JSON additions must be additive. Architecture risk is medium because `search` and `inspect` are high-use tools, but locality is good if recovery policy stays in `SearchTool`, candidate ordering stays in `SmartTargetResolver`, and retry text stays in `InspectTool`/`CandidateOutput`.

## Global Constraints

- Do not add a new MCP tool.
- Do not reintroduce the removed metrics MCP surface.
- Do not add semantic/vector retrieval to Miller; semantic workflows stay in Eros.
- Do not clone Julie UX. Adapt useful recovery behavior into Miller's existing smaller tool set.
- Preserve explicit `mode=source`, `mode=content`, `regions`, `markers`, `symbol`, and `file` routing.
- Preserve existing `search auto` exact-symbol and file lookup strengths.
- Keep JSON response shapes backward-compatible; new JSON fields must be additive.
- Keep compact rescue output bounded to avoid turning `search auto` into broad content search.
- Do not read source files directly for rescue; use existing text-content/content sidecars.
- Keep resolver tie handling conservative: if two plausible production definitions tie, return candidates rather than choosing silently.
- Use TDD for behavior changes.
- Run the documented fast suite before handoff.

---

## Source Evidence

Completed evidence baseline:

- `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/calibration.md`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md`
- `docs/plans/2026-06-27-search-inspect-effectiveness-implementation-plan.md`

Current shipped behavior to preserve:

- `search auto` already has first-pass source rescue for empty symbol results and source-body-looking queries.
- `inspect depth=overview` already exists and is covered by current tests.
- `SmartTargetResolver` already has `PreferredDefinitionCandidate`, `DefinitionPreferenceScore`, and display ranking.
- `CandidateOutput` already centralizes basic candidate headers and caps.

Remaining gaps from the matrix:

- `18` Miller rows were present-but-not-top across retrieval, inspect, ambiguity, and region rows.
- `retrieval.docs`, `retrieval.source_auto`, and some source-explicit rows had present answers but weak first-call packaging.
- `inspect` passed hard presence gates, but Zod-like versioned/package targets still need clearer ambiguity handling.
- The top adaptation candidate is route recovery inside existing `search` and `inspect` output, not a new tool.

## File Structure

Modify:

- `src/Miller.Server/Tools/SearchTool.cs` - weak-result classification, bounded source/docs rescue, compact hints, telemetry metadata.
- `src/Miller.Server/Resolution/SmartTargetResolver.cs` - conservative candidate scoring hardening and display ordering.
- `src/Miller.Server/Tools/InspectTool.cs` - candidate rendering with scoped retry examples and additive JSON guidance.
- `src/Miller.Server/Tools/CandidateOutput.cs` - shared bounded candidate text helpers for scoped-example formatting.
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` - prompt-facing usage guidance for recovery and scoped inspect retries.
- `scripts/benchmarks/miller-foundation-cases.json` - focused rows or anchors for the recovery cases that become hard gates.
- `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md` - update only the follow-up status and evidence links after implementation.
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md` - mark implemented candidates after evidence is generated.

Test:

- `tests/Miller.Tests/Server/SearchToolTests.cs`
- `tests/Miller.Tests/Server/SmartTargetResolverTests.cs`
- `tests/Miller.Tests/Server/InspectToolTests.cs`
- `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- `tests/Miller.Tests/Tools/TraceToolTests.cs`
- `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

## Task 1: Search Weak-Result Recovery

**Files:**

- Modify: `src/Miller.Server/Tools/SearchTool.cs`
- Test: `tests/Miller.Tests/Server/SearchToolTests.cs`

**Interfaces:**

- Consumes: `SearchTool.Run`, `ShouldRunAutoSourceRescue`, `TryRunAutoSourceRescue`, `IWorkspaceTextContentSearchProvider`, `TextContentKind.WorkspaceSource`, `TextContentKind.WorkspaceDocs`, and `TextContentKind.WorkspaceConfig`.
- Produces: compact-only rescue output for weak `search auto` results; no route or MCP schema change.

**What to build:** Extend the existing empty-result source rescue into a bounded weak-result recovery policy for `search auto`.

**Approach:**

- Add an internal weak-result classification that does not parse rendered compact text. Introduce a private result envelope inside `SearchTool` carrying the rendered output plus recovery signals such as rendered count, top exact-definition presence, all-low-signal page, and path-like query state.
- Rescue can run only when all of these are true:
  - mode is default `auto`,
  - format is compact,
  - no `regions` route is active,
  - query is not path-like file intent,
  - primary output is empty, all visible rows are low signal, or the query shape is source/docs-like while no exact concrete definition was shown.
- Source rescue remains limited to at most two workspace-source hits.
- Add docs/config rescue for natural-language or docs-looking queries by searching workspace docs/config with at most two hits.
- Compact sections must be explicit:

  ```text
  Source matches also found:
  ...
  Rerun with mode=source for more source snippets.
  ```

  ```text
  Docs/config matches also found:
  ...
  Rerun with mode=content for more docs/config snippets.
  ```

- Keep primary symbol/file rows first when they exist.
- If both source and docs/config rescue find hits, render the rescue section that best matches query shape first and cap the total rescue rows at two.
- If text-content providers are unavailable, keep the original primary result and do not turn provider absence into a tool error.
- Add telemetry metadata for `auto_rescue_attempted`, `auto_rescue_kind`, and `auto_rescue_result_count`; do not store raw query text.

**Acceptance Criteria:**

- [ ] `search("KnownSourceError")` still renders bounded source rescue when symbol results are empty.
- [ ] A non-empty but all-low-signal `search auto` result with a source-body query renders bounded source rescue.
- [ ] A natural-language/docs query with weak symbol results renders bounded docs/config rescue.
- [ ] Strong exact-symbol `search auto` does not resolve the text-content provider.
- [ ] Path-like auto file queries do not resolve the text-content provider.
- [ ] Explicit `mode=source`, `mode=content`, `regions`, `markers`, `symbol`, and `file` behavior is unchanged.
- [ ] JSON `search auto` remains backward-compatible and does not append compact rescue prose.
- [ ] Telemetry metadata records rescue attempt/result without raw query text.

## Task 2: Inspect Candidate Guidance

**Files:**

- Modify: `src/Miller.Server/Tools/InspectTool.cs`
- Modify: `src/Miller.Server/Tools/CandidateOutput.cs`
- Test: `tests/Miller.Tests/Server/InspectToolTests.cs`
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs`

**Interfaces:**

- Consumes: `TargetResolution.Candidates`, current compact candidate rows, and `CandidateOutput.Header`.
- Produces: clearer compact and JSON candidate guidance for `inspect`; tie cases remain candidates.

**What to build:** Make ambiguous `inspect` results tell the agent exactly how to disambiguate.

**Approach:**

- Keep the current first line when it is accurate, but append copyable scoped retry examples when candidates span files.
- For compact `inspect`, render at most three scoped examples after the candidate list:

  ```text
  Try:
    inspect target="parse" scope="src/parser.ts"
    inspect target="parse" scope="packages/core/src/parser.ts"
  ```

- Escape quotes in target/scope examples.
- Do not render scoped examples when all candidates are in the same file; in that case keep the existing "pass a more specific target" message.
- For JSON `inspect`, add an additive `rerun_examples` array with objects like:

  ```json
  {"target":"parse","scope":"src/parser.ts","tool":"inspect"}
  ```

- Keep trace candidate tests green and add focused assertions that trace still points to `scope=<file>` after the shared helper change.

**Acceptance Criteria:**

- [ ] Ambiguous `inspect` compact output spanning files includes scoped `inspect target=... scope=...` examples.
- [ ] Ambiguous `inspect` compact output within one file does not suggest `scope=<file>` as the only recovery path.
- [ ] Ambiguous `inspect` JSON includes additive `rerun_examples`.
- [ ] Candidate list cap and remainder note remain bounded.
- [ ] Existing trace ambiguous-target behavior remains green if shared candidate helpers change.

## Task 3: Resolver Ranking Hardening

**Files:**

- Modify: `src/Miller.Server/Resolution/SmartTargetResolver.cs`
- Test: `tests/Miller.Tests/Server/SmartTargetResolverTests.cs`
- Test: `tests/Miller.Tests/Server/InspectToolTests.cs`

**Interfaces:**

- Consumes: `PreferredDefinitionCandidate`, `DefinitionPreferenceScore`, `RankCandidatesForDisplay`, and `IsNameLookupNoise`.
- Produces: better deterministic ordering and conservative auto-selection for inspect/edit/trace/impact target resolution.

**What to build:** Expand existing definition preference scoring so production source definitions beat test, example, generated, dist, and low-signal rows when there is one clear winner.

**Approach:**

- Keep the existing rule: a unique preferred definition may resolve to `TargetResolution.Symbol`; tied plausible definitions must return `TargetResolution.Candidates`.
- Add non-repo-specific path penalties for generated or packaged output segments: `generated`, `dist`, `build`, `coverage`, `fixtures`, `fixture`, `examples`, `example`, `samples`, `sample`, `bench`, `benchmark`, `node_modules`, `vendor`.
- Add non-repo-specific source preference for `src`, `lib`, `app`, `packages/*/src`, `crates/*/src`.
- Extend name-lookup noise only for kinds proven by tests to be non-edit targets, such as `module` or `import`; do not hide real language definitions.
- Preserve existing tests:
  - production beats test,
  - tied production definitions stay candidates,
  - single definition plus imports resolves to definition,
  - wrong scope surfaces out-of-scope matches.
- Add a versioned package fixture with a source definition, an example/test duplicate, and a generated/dist duplicate.

**Acceptance Criteria:**

- [ ] Production source definition beats example/test/generated duplicate when it is the unique top score.
- [ ] Two production definitions with equal score stay ambiguous.
- [ ] Candidate display order puts likely production definitions first.
- [ ] Wrong-scope and near-miss suggestion behavior is unchanged.
- [ ] No repo-specific package names are encoded in resolver scoring.

## Task 4: Matrix Rows And Prompt Guidance

**Files:**

- Modify: `scripts/benchmarks/miller-foundation-cases.json`
- Modify: `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md`
- Modify: `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md`
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

**Interfaces:**

- Consumes: existing foundation matrix runner, final baseline evidence, and Miller server instructions.
- Produces: focused regression rows and updated prompt-facing guidance.

**What to build:** Turn the implemented recovery behavior into repeatable evidence without turning all report-only ranking gaps into hard blockers.

**Approach:**

- Add or update focused matrix rows for:
  - weak `search auto` source rescue,
  - weak `search auto` docs/content rescue,
  - ambiguous `inspect` scoped retry guidance,
  - resolver preference for production source over test/example/generated duplicates.
- Gate only the implemented recovery invariants, not every top-rank gap from the final baseline.
- Keep Julie rows report-only.
- Update `MILLER_AGENT_INSTRUCTIONS.md` so agents learn:
  - use `search auto` first for identifiers,
  - follow rescue hints to `mode=source` or `mode=content`,
  - use `inspect target=... scope=...` when candidates span files,
  - prefer `inspect overview` before `full` for first reads.
- Keep the final matrix finding honest: note that the top recovery candidates were implemented, and keep remaining trace/Eros/onboarding candidates separate.

**Acceptance Criteria:**

- [ ] Matrix rows cover source rescue, docs/content rescue, inspect scoped retry guidance, and resolver production preference.
- [ ] New hard gates assert only implemented recovery behavior.
- [ ] Julie comparison remains report-only.
- [ ] Agent instructions document the new recovery paths and every MCP tool remains documented.
- [ ] Adaptation candidate report marks the search/inspect recovery slice as implemented and leaves unrelated candidates open.

## Verification Strategy

**Project source of truth:** `AGENTS.md` / `CLAUDE.md` testing sections, `tests/Miller.Tests/Miller.Tests.csproj`, `scripts/test.sh`, and the benchmark runner docs in `docs/plans/2026-06-27-miller-julie-foundation-effectiveness-plan.md`.

**Worker red/green scope:** For each task, run the narrow test filter covering the changed behavior:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SearchToolTests|FullyQualifiedName~SmartTargetResolverTests|FullyQualifiedName~InspectToolTests|FullyQualifiedName~TraceToolTests|FullyQualifiedName~AgentInstructionsTests"
```

Workers may narrow the filter further during red/green, but the combined filter above must pass before a task is handed back.

**Worker ceiling:** Workers may run `scripts/test.sh`, `python3 scripts/bench-julie-miller-search-inspect.py --gate`, and focused `python3 scripts/bench-foundation-matrix.py --tasks ... --skip-julie --gate` commands. Workers do not own final branch acceptance or gate calibration.

**Worker gate invariant:** The assigned worker gate must prove caller-facing `search`/`inspect` output, not private helper behavior alone.

**Lead affected-change scope:** After all tasks, run:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SearchToolTests|FullyQualifiedName~SmartTargetResolverTests|FullyQualifiedName~InspectToolTests|FullyQualifiedName~CliDispatchTests|FullyQualifiedName~TraceToolTests|FullyQualifiedName~AgentInstructionsTests"
python3 scripts/bench-julie-miller-search-inspect.py --gate
python3 scripts/bench-foundation-matrix.py --tasks retrieval.docs,retrieval.source_auto,ambiguity.unscoped,inspect.summary,inspect.overview,inspect.full --skip-julie --out-dir /tmp/miller-search-inspect-recovery-hardening --gate
```

**Branch gate:** Run:

```bash
scripts/test.sh
python3 scripts/bench-julie-miller-search-inspect.py --gate
python3 scripts/bench-foundation-matrix.py --skip-julie --out-dir /tmp/miller-search-inspect-recovery-hardening-branch --gate
```

Run `scripts/test.sh scale` only if implementation touches indexing, extraction, sidecar build paths, workspace refresh/full-scan behavior, or language extraction contracts.

**Replay/metric evidence:** Hard gates are recovery behavior and existing benchmark thresholds only. Latency, output-size medians, Julie deltas, and unresolved trace/onboarding candidates remain report-only unless this plan explicitly promotes a row.

**Escalation triggers:** Stop for user decision if implementation requires a new MCP tool, semantic/vector retrieval inside Miller, extractor changes, incompatible JSON shape changes, weakening existing exact-symbol/file/source-present benchmark gates, or broad restructuring outside the named files.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the failing assertion is a deliberately red TDD test before implementation.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. For benchmark evidence, record hard-gate counts, output directory, and which recovery rows changed from report-only to hard-gated.

## Model Routing

**Project source of truth:** No `RAZORBACK.md` was found in this repo during planning. Use current harness defaults unless the user specifies a reviewer/model choice.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage.

- Harness mapping: inherit.

**Implementation tier:** bounded worker tasks from this plan.

- Harness mapping: inherit.

**Mechanical tier:** docs, fixtures, matrix row additions, formatting, and prompt text updates with no gate interpretation.

- Harness mapping: inherit.

**Gate-interpretation reviewer:** lead agent for failed benchmark interpretation, deciding whether a top-rank miss is a product gap or an over-strict gate, and final acceptance.

- Harness mapping: inherit.

**Escalation tier:** search ranking policy changes with broad blast radius, resolver tie-breaking changes, JSON compatibility changes, repeated benchmark failures, or pressure to expand MCP surface.

- Harness mapping: inherit.

**Worker eligibility:** Workers may implement Tasks 1-4 when they keep changes inside named files and verify through the worker red/green commands. The lead owns final gate calibration and final evidence updates.

**Escalation triggers:** Any need to add an MCP tool, alter stable JSON shapes incompatibly, add semantic/vector behavior, or change extractor contracts must return to the lead and user as a separate decision.

**Mechanical exclusion:** Mechanical workers cannot own failing test interpretation, benchmark threshold calibration, resolver policy decisions, or final acceptance gates.

**Unsupported harness behavior:** If the harness cannot choose models per worker, use `inherit` and continue.

## Execution Order

1. Task 1: harden `search auto` weak-result source/docs recovery.
2. Task 2: improve `inspect` candidate scoped retry guidance.
3. Task 3: harden resolver ranking for production definitions without silent tie-breaking.
4. Task 4: add focused matrix rows and update prompt-facing guidance.

## Final Acceptance

- [ ] No new MCP tool is added.
- [ ] No semantic/vector retrieval is added to Miller.
- [ ] `search auto` preserves strong exact-symbol and file behavior.
- [ ] Weak `search auto` source-body intent gives bounded source recovery.
- [ ] Weak `search auto` docs/config intent gives bounded content recovery.
- [ ] Ambiguous `inspect` output gives copyable scoped retry examples when candidates span files.
- [ ] Resolver preference improves production-definition ordering without silently resolving tied production definitions.
- [ ] JSON additions are additive and existing JSON tests remain green.
- [ ] Prompt-facing server instructions document the recovery workflow.
- [ ] Focused tests, `scripts/test.sh`, and the benchmark gates in this plan pass.
