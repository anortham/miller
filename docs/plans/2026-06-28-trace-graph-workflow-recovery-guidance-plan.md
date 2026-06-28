# Trace Graph Workflow Recovery Guidance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Improve Miller's graph workflow quality by making `trace` failure and partial-result states tell agents the next useful Miller call, while keeping the MCP tool surface unchanged.

**Architecture:** Keep graph behavior honest and deterministic: `trace` must not invent call paths, bridge links, or reference confidence. Add bounded recovery guidance to existing `TraceTool` compact output and additive JSON fields for machine consumers. Update prompt-facing instructions, tool descriptions, README, GitHub Pages, and foundation-matrix evidence so the guidance is discoverable and regression-gated.

**Tech Stack:** .NET 10, existing Miller MCP/CLI read-command cores, `TraceTool`, `CandidateOutput`, `Utf8JsonWriter`, xUnit fast tests, `scripts/bench-foundation-matrix.py`, README, and the static GitHub Pages site under `docs/site/`.

**Architecture Quality:** Affected modules are `TraceTool`, `CandidateOutput` only if scoped examples are shared, trace JSON contract docs, prompt/server instructions, README/site docs, and benchmark manifest/evidence. Caller-facing interfaces stay inside the existing `trace` MCP/CLI tool; compact text changes are user-facing, and JSON changes must be additive. Architecture risk is medium because `trace` is a public read tool and Eros has a trace JSON contract, but locality is good if next-action policy stays inside `TraceTool` and does not alter graph traversal or extractor data.

## Global Constraints

- Do not add a new MCP tool.
- Do not reintroduce the removed metrics MCP surface.
- Do not add semantic/vector retrieval to Miller; semantic workflows stay in Eros.
- Do not clone Julie UX. Adapt useful recovery behavior into Miller's existing smaller tool set.
- Do not change graph truth semantics: no fabricated paths, inferred callers, synthetic bridge links, or upgraded reference confidence.
- Keep compact guidance bounded: at most three next-action lines per empty/diagnostic trace result.
- Keep trace JSON backward-compatible; new JSON fields must be additive.
- Keep `trace mode=refs` honest that extracted refs are name-based, not fully resolved semantic references.
- Keep `trace mode=bridge` provider-scoped to `dotnet-web`; unsupported stacks should get fallback guidance, not fake bridge coverage.
- Do not store raw query text in telemetry. Guidance may echo the caller's current `target`/`to` in the immediate tool response because those fields are already returned.
- Tool descriptions must stay under the existing test budget: 900 chars per tool description and 250 chars per parameter description.
- Server instructions must stay under the existing 12,000-char budget after CRLF normalization.
- Use TDD for behavior changes.
- Run the documented fast suite before handoff.

---

## Source Evidence

Completed evidence baseline:

- `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task4-workflows/summary.md`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task4-workflows/results.csv`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md`

Current behavior to preserve:

- `trace mode=auto` shows neighbours and already has a same-file/source-search fallback for no-neighbour cases.
- `trace mode=refs` returns name-based identifier references and already hints to `search mode=source` when no refs are extracted.
- `trace mode=path` returns ordered shortest paths when they exist.
- `trace mode=bridge` is provider-scoped to `dotnet-web` and reports capability status for skipped providers.
- `TraceTool` JSON already has stable fields documented in `docs/contracts/trace-json-v1.md`.
- `CandidateOutput` already has bounded scoped rerun example helpers used by `inspect`.

Remaining gaps from the matrix:

- Task 4 captured report-only workflow outcomes `needs-search`, `no-path`, and `unsupported`.
- `trace path` no-path output currently gives only `No path from ... within N hop(s).`
- Bridge unsupported/not-on-bridge output explains bridge scope but does not clearly route the agent to ordinary refs/source/path inspection.
- Trace ambiguity remains less actionable than inspect ambiguity after the search/inspect recovery hardening slice.
- Usage telemetry shows `trace` is low-use, which should be treated as a discovery/guidance problem, not a reason to remove or replace the tool.

## File Structure

Modify:

- `src/Miller.Server/Tools/TraceTool.cs` - compact next-action guidance, additive JSON `next_actions`, trace tool description text, and telemetry metadata if needed.
- `src/Miller.Server/Tools/CandidateOutput.cs` - only if trace scoped rerun examples should reuse the existing inspect helper instead of duplicating formatting.
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` - prompt-facing trace recovery guidance and subagent primer wording.
- `docs/contracts/trace-json-v1.md` - document additive `next_actions` JSON field and mode-specific diagnostic guidance.
- `README.md` - update tool-surface and CLI examples so `trace` guidance is visible to users outside MCP instructions.
- `docs/site/index.html` - update GitHub Pages tool-surface copy for `trace` to mention honest fallback guidance.
- `docs/README.md` - update only if a new evidence artifact or contract link is added.
- `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md` - mark graph workflow recovery as implemented after evidence is generated.
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md` - update candidate 4 status after implementation.
- `scripts/benchmarks/miller-foundation-cases.json` - add focused hard-gate rows for trace recovery guidance.

Test:

- `tests/Miller.Tests/Tools/TraceToolTests.cs`
- `tests/Miller.Tests/Server/AgentInstructionsTests.cs`
- `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` only if CLI argument handling or command help changes.

Generated evidence after implementation:

- `docs/findings/benchmarks/2026-06-27-foundation-matrix/trace-graph-recovery-guidance/summary.md`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/trace-graph-recovery-guidance/results.csv`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/trace-graph-recovery-guidance/results.json`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/trace-graph-recovery-guidance/calibration.md`

## Model Routing

**Project source of truth:** No repo-local `RAZORBACK.md` exists. Use the active harness default unless the approval message specifies a reviewer or model policy.

**Strategy tier:** Planning, architecture, decomposition, lead review, and gate interpretation.
- Harness mapping: inherit.

**Implementation tier:** Bounded worker tasks from this plan.
- Harness mapping: inherit.

**Mechanical tier:** Documentation wording, benchmark manifest rows, generated evidence collation, and static site copy when no behavioral tests or acceptance gates are owned by the worker.
- Harness mapping: inherit.

**Gate-interpretation reviewer:** Lead agent interprets failing trace tests, matrix gates, and JSON contract impact.
- Harness mapping: inherit.

**Escalation tier:** Use the lead agent for JSON contract shape changes, repeated test failures, unexpected benchmark regressions, or any proposal to alter trace semantics rather than guidance.
- Harness mapping: inherit.

**Worker eligibility:** Implementation-tier workers may own tasks with clear caller-facing tests and no product-boundary changes.

**Escalation triggers:** Stop and report if a task requires a new MCP tool, semantic retrieval, extractor changes, non-additive JSON removal/rename, or broad graph model changes.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, benchmark interpretation, release-gate acceptance, or trace JSON contract compatibility decisions.

**Unsupported harness behavior:** If the harness cannot choose models per agent, use `inherit`, note it in the worker report, and continue.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` / `AGENTS.md` testing section and `tests/Miller.Tests/Miller.Tests.csproj` fast-suite filter.

**Worker red/green scope:** Use focused xUnit filters for the changed behavior:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TraceToolTests"
```

For instruction/tool-description changes:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentInstructionsTests"
```

For CLI help/argument changes, if any:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CliDispatchTests"
```

**Worker ceiling:** Focused trace, agent-instruction, and CLI tests. Workers do not own broad matrix interpretation unless assigned Task 5.

**Worker gate invariant:** Focused tests prove compact guidance, additive JSON guidance, ambiguity recovery, and docs budget constraints through the same public surfaces agents call.

**Lead affected-change scope:** After a coherent batch, run:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TraceToolTests|FullyQualifiedName~AgentInstructionsTests|FullyQualifiedName~CliDispatchTests"
python3 -m py_compile scripts/benchlib/*.py scripts/bench-julie-miller-search-inspect.py scripts/bench-foundation-matrix.py
git diff --check
```

**Branch gate:** Before handoff or merge:

```bash
scripts/test.sh
```

**Replay/metric evidence:** Run the focused foundation matrix rows after implementation:

```bash
python3 scripts/bench-foundation-matrix.py --repos miller,zod,flask --tasks trace.refs,trace.path,trace.bridge --skip-julie --out-dir docs/findings/benchmarks/2026-06-27-foundation-matrix/trace-graph-recovery-guidance --gate
```

Hard gates:

- no-path compact output includes a bounded next-action block;
- unsupported/not-on-bridge compact output includes non-bridge fallback guidance;
- ambiguous trace compact output includes copyable scoped rerun examples when candidates span files;
- trace JSON diagnostic rows include additive `next_actions`;
- existing path/refs/bridge success rows remain present.

Report-only:

- latency, output chars, and exact first path ranking;
- Julie skipped/report-only rows, if included for comparison.

**Escalation triggers:** Run `scripts/test.sh scale` only if implementation touches extraction, indexing, sidecar generation, or real `julie-extract` subprocess behavior. This plan should not touch those areas.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless the task explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the worker report or checkpoint. For matrix evidence, record hard-gate metrics and report-only metrics.

## Task 1: Trace Next-Action Model And No-Path Guidance

**Files:**

- Modify: `src/Miller.Server/Tools/TraceTool.cs`
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs`

**Interfaces:**

- Consumes: `RunPath`, `RenderPathJson`, `RenderTraceJson`, `DiagnosticCode`, current `path` result fields.
- Produces: compact no-path next-action block and additive JSON `next_actions` array.

**What to build:** Make `trace mode=path` no-path output useful without changing path-finding semantics. When no graph path exists within `depth`, the compact output should say what to try next and JSON should expose the same suggestions as structured data.

**Approach:**

- Add a small internal next-action representation inside `TraceTool.cs`, for example `TraceNextAction(string Tool, string Reason, IReadOnlyDictionary<string,string?> Args)`, or an equivalent private method pair if a record is overkill.
- Keep next-action generation deterministic and target-local.
- For `no_path`, compact output should keep the current first sentence, then add:

  ```text
  Next:
    trace target="<target>" mode="refs" — check extracted identifier references from the source endpoint.
    trace target="<to>" mode="refs" — check extracted identifier references from the destination endpoint.
    search query="<target> <to>" mode="source" — look for text links not represented in the graph.
  ```

- If target and `to` are identical after trim, do not emit duplicate refs actions.
- Do not suggest raising `depth` as the primary fix unless the current depth is less than `3`; when suggested, phrase it as a bounded option, not proof that a path exists.
- For JSON, add top-level `next_actions` after `diagnostics` or before `diagnostics`; because JSON consumers should ignore unknown fields, placement is not semantically important but tests should pin one stable shape.
- Each JSON action should include:

  ```json
  {
    "tool": "trace",
    "reason": "check extracted identifier references from the source endpoint",
    "args": {"target": "SearchRoutePlanner", "mode": "refs"}
  }
  ```

- Do not alter `hops`, `nodes`, `links`, `resolved_target`, or `resolved_to` behavior.
- Add telemetry metadata only if needed for adoption reporting, using booleans/counts such as `next_actions_count`; do not store raw target text beyond existing target hash behavior.

**Acceptance criteria:**

- [x] `Path_NoConnection_CleanMessage` or a new neighboring test proves compact no-path output keeps `No path from ...` and includes a bounded `Next:` block.
- [x] `Path_NoConnection_JsonCarriesDiagnostic` proves JSON still has diagnostic code `no_path` and now includes `next_actions`.
- [x] Successful path output is unchanged except for any intentionally documented JSON additive default, and focused path success tests remain green.
- [x] Duplicate target/destination names do not produce duplicate next-action lines.
- [x] Worker-scope verification passes, committed.

## Task 2: Bridge Unsupported And Not-On-Bridge Recovery

**Files:**

- Modify: `src/Miller.Server/Tools/TraceTool.cs`
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs`

**Interfaces:**

- Consumes: `RunBridge`, `AppendBridgeCapabilityStatus`, `RenderBridgeJson`, bridge diagnostic codes `not_on_bridge` and `no_bridge_links`.
- Produces: compact and JSON fallback guidance for bridge-empty states.

**What to build:** Keep `trace mode=bridge` provider-scoped and honest, but tell agents what to do when a target is not on a bridge or the workspace is not in the supported provider shape.

**Approach:**

- For `not_on_bridge`, keep the existing explanation and capability status.
- Append compact next actions:

  ```text
  Next:
    trace target="<target>" mode="refs" — inspect ordinary name-based references.
    trace target="<target>" mode="auto" — inspect local graph neighbours.
    search query="<target>" mode="source" — find text occurrences outside bridge evidence.
  ```

- For `no_bridge_links`, include the same actions and keep the existing depth-specific message.
- For bridge target resolution ambiguity, route to Task 3's candidate guidance rather than silently choosing a bridge start.
- JSON `next_actions` should be present for `not_on_bridge` and `no_bridge_links`.
- Keep provider diagnostics exactly as documented; `next_actions` augments them and does not replace `provider`.

**Acceptance criteria:**

- [x] `Bridge_NotOnBridge_CleanMessage` or a new neighboring test proves compact output includes `trace mode=refs`, `trace mode=auto`, and `search mode=source` recovery lines.
- [x] `Bridge_NotOnBridge_IncludesCapabilityStatus_WhenProvidersSkipped` still proves skipped-provider status is present.
- [x] `Bridge_NotOnBridge_JsonIncludesCapabilityDiagnostics` proves existing diagnostics still render and JSON now includes `next_actions`.
- [x] Bridge success output and `full` signal output remain unchanged.
- [x] Worker-scope verification passes, committed.

## Task 3: Trace Ambiguity Guidance

**Files:**

- Modify: `src/Miller.Server/Tools/TraceTool.cs`
- Modify: `src/Miller.Server/Tools/CandidateOutput.cs` only if needed to reuse scoped examples cleanly.
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs`

**Interfaces:**

- Consumes: `ResolveSymbol`, `ResolveBridgeStart`, `RenderCandidatesNote`, `CandidateOutput.RerunExamples`, `CandidateOutput.AppendRerunExamples`.
- Produces: trace-specific copyable scoped rerun examples for ambiguous targets spanning files.

**What to build:** Bring trace ambiguity recovery up to the same practical level as inspect ambiguity recovery. Agents should get copyable trace reruns when multiple files match a target.

**Approach:**

- Keep current candidate rows and caps.
- When candidates span multiple files, append at most three examples:

  ```text
  Try:
    trace target="ZodObject" scope="packages/zod/src/v4/classic/schemas.ts"
    trace target="ZodObject" scope="packages/zod/src/v3/types.ts"
  ```

- Use the existing `CandidateOutput.RerunExamples` / `AppendRerunExamples` helper if it can accept the trace tool name without awkward branching. If it cannot, extend it minimally to accept a `toolName` argument while preserving inspect output.
- Do not render scoped examples when all candidates are in one file; keep the existing "pass a more specific target" guidance.
- For JSON ambiguity diagnostics, add `next_actions` with trace rerun actions when candidates span files. Do not remove existing `diagnostics`, candidate notes, or candidate list behavior.
- Because `ImpactTool` references `TraceTool.RenderCandidatesNote` indirectly through shared code context, run existing impact ambiguity tests if a shared helper changes.

**Acceptance criteria:**

- [x] `Auto_AmbiguousTarget_PointsToScope` or a new test proves compact trace ambiguity includes scoped `trace target=... scope=...` examples.
- [x] `Auto_ScopedAmbiguousTarget_AsksForMoreSpecificTarget` proves same-file ambiguity does not suggest scope as the only recovery path.
- [x] Bridge ambiguous target tests still show bridge-specific ambiguity flags where appropriate.
- [x] JSON ambiguity output includes additive `next_actions` when applicable.
- [x] Existing inspect ambiguity tests remain green if `CandidateOutput` changes.
- [x] Worker-scope verification passes, committed.

## Task 4: Server Instructions And Tool Description Updates

**Files:**

- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Modify: `src/Miller.Server/Tools/TraceTool.cs`
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs`

**Interfaces:**

- Consumes: embedded server instructions via `AgentInstructions.Load()` and `TraceTool.Trace` `[Description]`.
- Produces: prompt-facing guidance that teaches agents how to recover from trace no-path, no-refs, unsupported bridge, and ambiguity states.

**What to build:** Update MCP-facing instructions and trace's tool description so agents see the recovery model without reading README.

**Approach:**

- In `MILLER_AGENT_INSTRUCTIONS.md`, update the `trace` tool bullet to mention:
  - `mode=refs` is name-based and empty refs should fall back to `search mode=source`;
  - `mode=path` no-path means no extracted graph path within depth, not proof the code is unrelated;
  - `mode=bridge` is `dotnet-web` provider-scoped and unsupported targets should fall back to refs/source/path.
- Update the `Trace a flow` workflow bullet to mention following `Next:` suggestions before raw file reads.
- Update the subagent primer trace bullet with one concise sentence about following trace recovery hints.
- Keep the server instructions under 12,000 chars. If over budget, remove redundant prose rather than weakening tool rules.
- Update the `TraceTool.Trace` description only if needed to advertise that empty/diagnostic output includes recovery hints. Keep it under 900 chars.
- Add or update tests in `AgentInstructionsTests` for durable phrases:
  - `No path` or `no-path`;
  - `not proof the code is unrelated`;
  - `mode=bridge` and `fallback`/`mode=refs` guidance.

**Acceptance criteria:**

- [x] `AgentInstructionsTests.Load_StaysUnderClaudeCodeInstructionBudget` remains green.
- [x] `ToolDescriptions_StayWithinClaudeCodeBudgets` remains green.
- [x] Tests pin trace recovery language without overfitting the whole paragraph.
- [x] Server instructions still document every public MCP tool and do not advertise metrics/todos as MCP tools.
- [x] Worker-scope verification passes, committed.

## Task 5: Public Docs, GitHub Pages, And Trace Contract

**Files:**

- Modify: `README.md`
- Modify: `docs/site/index.html`
- Modify: `docs/contracts/trace-json-v1.md`
- Modify: `docs/README.md` if a new generated evidence artifact is linked from the docs map.
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs` for description budgets; otherwise docs are verified by link/content checks and build gates.

**Interfaces:**

- Consumes: final compact and JSON behavior from Tasks 1-3.
- Produces: public docs and site copy that set correct expectations for trace guidance and JSON `next_actions`.

**What to build:** Make the new trace recovery behavior visible to users installing Miller from README or the public GitHub Pages site.

**Approach:**

- In `README.md` tool surface section, update the `trace` description to explain:
  - refs are name-based;
  - path mode reports real extracted graph paths and no-path includes next calls;
  - bridge mode is provider-scoped and unsupported targets include fallback guidance.
- In README CLI examples, add one compact example that demonstrates recovery guidance without becoming a long tutorial, for example:

  ```bash
  dotnet run --project src/Miller.Server -c Release -- trace SearchRoutePlanner --mode path --to SearchToolTests --depth 2
  ```

  Use a command that is stable in this repo after implementation and appears in the matrix rows.
- In `docs/site/index.html`, update the `trace` tool card copy to mention "honest no-path/unsupported results with next calls" or equivalent concise text.
- In `docs/contracts/trace-json-v1.md`, document top-level additive `next_actions`:
  - present as an array;
  - empty when no recovery guidance is needed;
  - objects contain `tool`, `reason`, and `args`;
  - intended for callers that want to surface next actions without parsing compact text.
- Do not claim semantic reachability, all-language bridge coverage, or fully resolved refs.
- If a new evidence directory is generated in Task 6 and should be discoverable from the docs map, add it to `docs/README.md` in the findings/evidence section.

**Acceptance criteria:**

- [x] README explains trace recovery without implying fake graph certainty.
- [x] GitHub Pages trace card mentions recovery guidance and keeps the site concise.
- [x] Trace JSON contract documents `next_actions` as additive and optional for consumers.
- [x] Docs avoid claiming semantic/vector retrieval belongs to Miller.
- [x] Worker-scope verification passes, committed.

## Task 6: Foundation Matrix Rows And Evidence Refresh

**Files:**

- Modify: `scripts/benchmarks/miller-foundation-cases.json`
- Modify: `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md`
- Modify: `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md`
- Create: `docs/findings/benchmarks/2026-06-27-foundation-matrix/trace-graph-recovery-guidance/summary.md`
- Create: `docs/findings/benchmarks/2026-06-27-foundation-matrix/trace-graph-recovery-guidance/results.csv`
- Create: `docs/findings/benchmarks/2026-06-27-foundation-matrix/trace-graph-recovery-guidance/results.json`
- Create: `docs/findings/benchmarks/2026-06-27-foundation-matrix/trace-graph-recovery-guidance/calibration.md`

**Interfaces:**

- Consumes: `scripts/bench-foundation-matrix.py` workflow outcome scoring and the final behavior from Tasks 1-5.
- Produces: hard-gated evidence that graph recovery guidance works and the adaptation candidate status is current.

**What to build:** Add focused benchmark rows for trace recovery states and refresh the human-facing matrix/candidate documents after the implementation passes.

**Approach:**

- Add hard-gated Miller-only rows for:
  - `trace.path` no-path guidance on the Miller repo;
  - `trace.bridge` unsupported/not-on-bridge fallback guidance using the existing Flask `flask.trace.bridge.add-url-rule-unsupported` row;
  - `trace.refs` ambiguous/needs-search guidance using the existing Zod `zod.trace.refs.zodobject-needs-search` row;
  - `trace.refs` empty reference guidance using a new Miller row for a symbol with no extracted identifier refs.
- Keep Julie rows report-only/skipped if included; this is a Miller quality gate, not parity.
- Extend the benchmark parser only if existing `follow_up_hint_present`, `readiness`, and `workflow_outcome` fields cannot detect `Next:` / `next_actions` reliably.
- The new rows should hard-gate on guidance presence, not on path top rank or latency.
- Regenerate evidence into the new `trace-graph-recovery-guidance` directory.
- Update `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md` Recommended Next Implementation Goals:
  - mark graph workflow recovery as implemented;
  - link the new summary/CSV/JSON.
- Update `adaptation-candidates.md` candidate 4 status with the implementation date and evidence links.
- Keep Task 5 Eros contract and onboarding/adoption candidates listed as future work.

**Acceptance criteria:**

- [x] Focused matrix command passes with the new hard-gated trace guidance rows.
- [x] Generated evidence files are committed and linked from the foundation matrix finding.
- [x] Adaptation candidate 4 is marked implemented only after tests and matrix evidence pass.
- [x] Julie rows remain report-only; no Julie parity gate is introduced.
- [x] Worker-scope verification passes, committed.

## Task 7: Final Verification And Live Tool Smoke

**Files:**

- No planned source files beyond prior tasks.
- Check: `git diff --stat`, generated evidence, and final docs.

**Interfaces:**

- Consumes: all previous task outputs.
- Produces: final branch confidence and a concise live-smoke report.

**What to build:** Verify the complete slice and prove the new guidance appears through the actual MCP/CLI surfaces.

**Approach:**

- Run affected-change scope:

  ```bash
  dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TraceToolTests|FullyQualifiedName~AgentInstructionsTests|FullyQualifiedName~CliDispatchTests"
  python3 -m py_compile scripts/benchlib/*.py scripts/bench-julie-miller-search-inspect.py scripts/bench-foundation-matrix.py
  git diff --check
  ```

- Run branch gate:

  ```bash
  scripts/test.sh
  ```

- Run the focused matrix command:

  ```bash
  python3 scripts/bench-foundation-matrix.py --repos miller,zod,flask --tasks trace.refs,trace.path,trace.bridge --skip-julie --out-dir docs/findings/benchmarks/2026-06-27-foundation-matrix/trace-graph-recovery-guidance --gate
  ```

- Do a built-binary CLI smoke with stable rows:

  ```bash
  src/Miller.Server/bin/Release/net10.0/miller trace SearchRoutePlanner --mode path --to SearchToolTests --depth 2
  src/Miller.Server/bin/Release/net10.0/miller trace SearchRoutePlanner --mode path --to SearchToolTests --depth 2 --json
  ```

- After the user rebuilds/restarts Miller in the session, do the matching MCP smoke:
  - `trace(target="SearchRoutePlanner", mode="path", to="SearchToolTests", depth=2)`
  - `trace(target="SearchRoutePlanner", mode="path", to="SearchToolTests", depth=2, format="json")`
  - one bridge unsupported row if the local indexed repo supports a stable non-bridge target.

**Acceptance criteria:**

- [x] Focused tests pass.
- [x] `scripts/test.sh` passes.
- [x] Focused foundation matrix gate passes and evidence files match the committed behavior.
- [x] CLI smoke shows compact `Next:` guidance and JSON `next_actions`.
- [x] Final report lists any live MCP smoke that still requires user rebuild/restart.
- [x] Changes are committed locally; no push, release, or publish is performed without explicit approval.
