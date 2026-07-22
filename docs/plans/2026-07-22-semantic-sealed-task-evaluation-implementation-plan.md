# Semantic Sealed Task Evaluation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Add a privacy-safe paired task-completion scorer, correct retrieval-eval mode routing, and produce a valid visible baseline before the user spends a sealed acceptance event.

**Architecture:** Task completion is a separate pure-scoring lane beside the existing retrieval scorer, with its own models, validator, statistics, and aggregate-only report. Retrieval queries gain a frozen optional `search_mode` consumed by the live-arm runner so docs-like queries exercise the documented content route instead of JSON auto mode. No sealed prompt, check, trajectory, task id, or per-task row enters the repository or returned aggregate.

**Tech Stack:** .NET 10, C#, `System.Text.Json`, xUnit, Python 3 standard library/unittest, existing Miller CLI evaluation runner.

**Architecture Quality:** Medium/high evidence-contract risk. New task-scoring modules are isolated from recall/nDCG code; the CLI is a thin adapter; routing mode is explicit data rather than inferred after results; sealed ownership is a data boundary, not a fake adapter or committed fixture.

## Global Constraints

- The user owns sealed tasks, prompts, acceptance checks, raw trajectories, per-task results, and blinded arm mapping outside this repository.
- Implementers never read sealed task text or per-task sealed outcomes. The repository receives aggregate results only.
- The task manifest contains only opaque `task_id`, `repo`, `language`, and `query_profile`; no prompt or acceptance text.
- Arm results contain only `task_id`, `completed`, `duration_ms`, `tool_calls`, `search_calls`, and `zero_result_search_calls`.
- The task report contains no task ids, per-task rows, input paths, prompt text, result text, or trajectories.
- The primary gate needs at least 30 complete pairs and a two-sided 95% Wilson lower bound above 0.5 for `candidate_only / (candidate_only + baseline_only)`.
- With at least 30 pairs, zero discordant pairs or a Wilson lower bound at or below 0.5 is `fail`, not a pass.
- Identifier/path safety is underpowered below five complete pairs and fails when `baseline_only > candidate_only`.
- Repo/language/query-profile subgroup output is suppressed below five complete pairs.
- Retrieval recall/nDCG remains diagnostic; task completion is the primary offline value measure.
- `search_mode` is optional and defaults to `auto`; valid values are `auto|symbol|file|content|source`.
- The four visible markdown/docs-like queries are pinned to `content`; sealed owners freeze modes before running either arm.
- Negative-query FPR remains report-only because Miller has no confidence-abstention product contract.
- Do not add a threshold, change relevance labels, tune against sealed output, commit sealed content, or change public search JSON.
- Use TDD; preserve the evaluator's zero-product-dependency boundary and its only runtime dependency on `System.Text.Json`.
- Do not push, publish, release, or claim a sealed gate passed without user-returned aggregate evidence.

---

## Verification Strategy

**Project source of truth:** `eval/retrieval-eval/README.md`, `eval/retrieval-eval/sets/SEALED-SET-PROTOCOL.md`, the accepted semantic maturity design, and `CLAUDE.md` / `AGENTS.md` for repository-wide gates.

**Worker red/green scope:** Task 1 runs `dotnet test eval/retrieval-eval/tests/RetrievalEval.Tests.csproj --filter "FullyQualifiedName~TaskCompletionScorerTests"`. Task 2 runs focused `DevSetTests`/`ScorerTests` plus `python3 -m unittest eval/retrieval-eval/tests/test_run_live_arm.py`. Task 3 runs focused `TaskScoreEndToEndTests` and the full evaluator test project.

**Worker ceiling:** The full `RetrievalEval.Tests.csproj` and the single Python unittest module. Workers do not build Miller or run repository fast/Scale suites.

**Worker gate invariant:** Scoring is deterministic and paired, validation fails closed, no report leaks task-level information, and both arms run the same pre-frozen search mode for every retrieval query.

**Lead affected-change scope:** Full evaluator tests, Python runner tests, `scripts/test.sh`, and a diff review proving `eval/retrieval-eval/RetrievalEval.csproj` gained no product reference or package dependency.

**Branch gate:** `dotnet build Miller.slnx -c Release`, `scripts/test.sh`, `scripts/test.sh scale`, `dotnet test eval/retrieval-eval/tests/RetrievalEval.Tests.csproj`, and `python3 -m unittest eval/retrieval-eval/tests/test_run_live_arm.py`.

**Replay/metric evidence:** Hard gates are scorer validation/privacy, Wilson math, minimum populations, safety-subset verdict, same-mode arm execution, correct-mode non-zero markdown coverage, no missing/unknown retrieval results, and unchanged identifier retrieval. Overall recall/nDCG, cold CLI latency, negative FPR, and tool-call/duration deltas are report-only diagnostics.

**Escalation triggers:** If correct-mode markdown remains zero, if the runner must change public Miller JSON, if task scoring requires prompts/checks, if a sealed artifact is discovered in the repository/transcript, or if a proposed fix changes production ranking/thresholds, stop and report a design/plan mismatch before continuing.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in `docs/findings/2026-07-22-semantic-decision-baseline.md`. For replay or metric evidence, also record hard-gate metrics and report-only metrics. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Paired task-completion scoring core | Batch A | Create `eval/retrieval-eval/TaskModel.cs`, `eval/retrieval-eval/TaskReport.cs`, `eval/retrieval-eval/TaskCompletionScorer.cs`, `eval/retrieval-eval/tests/TaskCompletionScorerTests.cs` | No | None - safe parallel batch. |
| Task 2: Correct-mode retrieval evaluation | Batch A | Modify `eval/retrieval-eval/Model.cs`, `eval/retrieval-eval/Report.cs`, `eval/retrieval-eval/Scorer.cs`, `eval/retrieval-eval/QuerySetValidator.cs`, `eval/retrieval-eval/run-live-arm.py`, `eval/retrieval-eval/sets/dev/queries.jsonl`, `eval/retrieval-eval/tests/DevSetTests.cs`, `eval/retrieval-eval/tests/ScorerTests.cs`; create `eval/retrieval-eval/tests/test_run_live_arm.py` | No | None - safe parallel batch. |
| Task 3: Task-score CLI and sealed-task protocol | None - serial | Modify `eval/retrieval-eval/Program.cs`, `eval/retrieval-eval/README.md`, `eval/retrieval-eval/sets/SEALED-SET-PROTOCOL.md`; create `eval/retrieval-eval/sets/SEALED-TASK-PROTOCOL.md`, `eval/retrieval-eval/tests/TaskScoreEndToEndTests.cs` | Yes | Consumes both Batch A contracts and owns their final public documentation/CLI integration. |
| Task 4: Corrected visible baseline replay | None - serial | Create `docs/findings/2026-07-22-semantic-decision-baseline.md` and `eval/retrieval-eval/out/semantic-decision-baseline-2026-07-22/`; update `eval/retrieval-eval/sets/dev/manifest.json` only if live SHA validation proves its existing pins are stale | Yes | Lead-only evidence run after both implementation plans and the full branch gate pass. |

### Task 1: Paired task-completion scoring core

**Files:**
- Create: `eval/retrieval-eval/TaskModel.cs`
- Create: `eval/retrieval-eval/TaskReport.cs`
- Create: `eval/retrieval-eval/TaskCompletionScorer.cs`
- Test: `eval/retrieval-eval/tests/TaskCompletionScorerTests.cs`

**Interfaces:**
- Consumes: in-memory task manifest rows and baseline/candidate arm result rows.
- Produces: `TaskManifestRow`, `TaskArmResult`, `TaskCompletionReport`, and `TaskCompletionScorer.Score(...)` with deterministic validation, Wilson interval, subgroup suppression, and aggregate-only output.

**Contract inputs:** Unique nonblank task ids; nonblank repo/language; query profile in `identifier|path|short_token|prose|docs_like|mixed`; nonnegative duration/calls; `zero_result_search_calls <= search_calls <= tool_calls`; exact task-id set in both arms.

**File ownership:** Create `eval/retrieval-eval/TaskModel.cs`, `eval/retrieval-eval/TaskReport.cs`, `eval/retrieval-eval/TaskCompletionScorer.cs`, `eval/retrieval-eval/tests/TaskCompletionScorerTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** A pure paired scorer with no file I/O. It validates the three input collections, constructs the four completion cells, computes the two-sided 95% Wilson interval with `z=1.959963984540054`, assigns `pass|fail|underpowered`, computes the identifier/path safety verdict, and emits only sufficiently populated aggregate groups.

**Approach:** Keep validation and report construction in this module; do not reuse `CanaryGateMath` because the evaluator intentionally has no Miller project reference. Sort dictionary/group output ordinally for byte-stable JSON later. Include aggregate duration/tool/search/zero-result summaries but never per-task rows.

**Acceptance criteria:**
- [ ] Hand-computed paired fixtures match both completion cells and Wilson bounds.
- [ ] Fewer than 30 pairs is underpowered; 30+ with no demonstrated lift fails; a powered lower bound above 0.5 passes.
- [ ] Missing, extra, duplicate, invalid-profile, or invalid-count rows throw deterministic validation errors.
- [ ] Identifier/path safety uses its five-pair floor and `baseline_only > candidate_only` failure rule.
- [ ] Groups below five are suppressed and no task id appears anywhere in serialized aggregate output.
- [ ] Worker-scope verification passes; hand the change to the lead with `parallel-lead-commit` (do not commit in the worker lane).

### Task 2: Correct-mode retrieval evaluation

**Files:**
- Modify: `eval/retrieval-eval/Model.cs:13-37`
- Modify: `eval/retrieval-eval/Report.cs:93-145`
- Modify: `eval/retrieval-eval/Scorer.cs:20-260`
- Modify: `eval/retrieval-eval/QuerySetValidator.cs:18-90`
- Modify: `eval/retrieval-eval/run-live-arm.py:23-145`
- Modify: `eval/retrieval-eval/sets/dev/queries.jsonl:69-72`
- Test: `eval/retrieval-eval/tests/DevSetTests.cs`
- Test: `eval/retrieval-eval/tests/ScorerTests.cs`
- Create: `eval/retrieval-eval/tests/test_run_live_arm.py`

**Interfaces:**
- Consumes: optional JSON `search_mode` on `EvalQuery`, default `auto`.
- Produces: validated `EvalQuery.SearchMode`; report-level `search_mode_counts`; live Miller command with the exact frozen `--mode`; four visible docs-like rows pinned to `content`.

**Contract inputs:** Valid modes are exactly `auto|symbol|file|content|source`. Both lexical and semantic/production arms receive the same mode. Existing query rows without the field remain `auto`.

**File ownership:** Modify `eval/retrieval-eval/Model.cs`, `eval/retrieval-eval/Report.cs`, `eval/retrieval-eval/Scorer.cs`, `eval/retrieval-eval/QuerySetValidator.cs`, `eval/retrieval-eval/run-live-arm.py`, `eval/retrieval-eval/sets/dev/queries.jsonl`, `eval/retrieval-eval/tests/DevSetTests.cs`, `eval/retrieval-eval/tests/ScorerTests.cs`; create `eval/retrieval-eval/tests/test_run_live_arm.py`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Make retrieval intent routing part of the frozen query contract and prove the runner honors it. Record mode composition in every report so two arms cannot be compared if one used different search surfaces.

**Approach:** Default in the model rather than in the runner, validate the enum before any arm executes, and pass `--mode <value>` unconditionally. The Python test mocks subprocess execution and asserts identical command routing across arms. Pin only the four known docs-like markdown rows to content; do not relabel relevance or alter other queries.

**Acceptance criteria:**
- [ ] Existing rows deserialize as `auto`; invalid modes fail validation.
- [ ] The four docs-like markdown rows validate as `content` and the report records `content: 4`.
- [ ] Every arm command contains the same query-row mode; randomized canary remains off and production remains unforced.
- [ ] No public Miller JSON schema or production ranking behavior changes.
- [ ] Existing retrieval metrics and identifier non-inferiority calculations remain unchanged for equivalent result files.
- [ ] Worker-scope verification passes; hand the change to the lead with `parallel-lead-commit` (do not commit in the worker lane).

### Task 3: Task-score CLI and sealed-task protocol

**Files:**
- Modify: `eval/retrieval-eval/Program.cs:5-149`
- Modify: `eval/retrieval-eval/README.md`
- Modify: `eval/retrieval-eval/sets/SEALED-SET-PROTOCOL.md`
- Create: `eval/retrieval-eval/sets/SEALED-TASK-PROTOCOL.md`
- Create: `eval/retrieval-eval/tests/TaskScoreEndToEndTests.cs`

**Interfaces:**
- Consumes: `task-score --tasks <manifest.jsonl> --baseline <results.jsonl> --candidate <results.jsonl> --out <aggregate.json>` and Task 1 scorer; Task 2 search-mode contract for the secondary retrieval event.
- Produces: aggregate-only schema 1 JSON, concise stdout, exit 0 for a valid report regardless of pass/fail verdict, exit 1 usage/IO, exit 2 validation failure, and a user-owned blinded-run protocol.

**Contract inputs:** Input files may contain sealed ids/results locally; output must omit ids and paths. Input SHA-256 digests may be recorded. The scorer never accepts task prompt/check fields because it never needs them.

**File ownership:** Modify `eval/retrieval-eval/Program.cs`, `eval/retrieval-eval/README.md`, `eval/retrieval-eval/sets/SEALED-SET-PROTOCOL.md`; create `eval/retrieval-eval/sets/SEALED-TASK-PROTOCOL.md`, `eval/retrieval-eval/tests/TaskScoreEndToEndTests.cs`

**Serialization required:** Yes.

**Dependency reason:** Consumes both Batch A contracts and owns their final public documentation/CLI integration.

**What to build:** Wire file parsing and aggregate serialization around the pure scorer, then document how the user-controlled coordinator freezes tasks, blinds arms, resets snapshots, enforces identical budgets, runs at least 30 pairs across five repo/language-family combinations, and returns aggregates only.

**Approach:** Reuse `Jsonl.ReadAll<T>` and the existing option parser. Hash inputs for reproducibility but omit their paths. The protocol names burn rules, arm-order randomization, safety subset, exact returned fields, and the rule that implementation agents may not diagnose from sealed rows.

**Acceptance criteria:**
- [ ] End-to-end fixtures produce the exact schema 1 aggregate and expected stdout/exit codes.
- [ ] The JSON contains no task id, input path, prompt, check, trajectory, or per-task row.
- [ ] Valid fail/underpowered verdicts still exit 0; malformed/mismatched inputs exit 2.
- [ ] Protocol requires blinded order, clean snapshots, identical agent budgets/tools, at least 30 pairs, five repo/language families, aggregate-only return, and burn handling.
- [ ] Existing `score` and `validate` commands retain their output and exit behavior.
- [ ] Full evaluator and Python worker-scope verification pass and the change is committed with `serial-worker-commit` after lead integration of Batch A.

### Task 4: Corrected visible baseline replay

**Files:**
- Create: `docs/findings/2026-07-22-semantic-decision-baseline.md`
- Create: `eval/retrieval-eval/out/semantic-decision-baseline-2026-07-22/`
- Modify only if validation proves stale: `eval/retrieval-eval/sets/dev/manifest.json`

**Interfaces:**
- Consumes: clean branch-gated Miller build, pinned dev corpus commits, corrected runner, unchanged visible relevance labels, lexical/semantic/production arms.
- Produces: reproducible results/reports/timings, correct-mode markdown verdict, negative diagnostic, identifier comparison, and verification ledger; no sealed evidence.

**Contract inputs:** Exclude `eval/`, `.razorback/`, and `.claude/` from indexed benchmark corpora; validate every reference at pinned SHAs; randomized canary off; same search mode per query/arm; serving depth 10; no result or judgment edits after scoring.

**File ownership:** Create `docs/findings/2026-07-22-semantic-decision-baseline.md` and `eval/retrieval-eval/out/semantic-decision-baseline-2026-07-22/`; update `eval/retrieval-eval/sets/dev/manifest.json` only if live SHA validation proves its existing pins are stale

**Serialization required:** Yes.

**Dependency reason:** Lead-only evidence run after both implementation plans and the full branch gate pass.

**What to build:** Recreate clean pinned Miller/Julie corpora, converge their lexical/content/vector artifacts with the frozen candidate, run all three arms, score them, and record why the corrected result is valid. This is the final visible diagnostic before the user spends a sealed task or retrieval event.

**Approach:** Reuse the production-readiness replay methodology and preserve raw machine-readable artifacts under the new output directory. Treat missing/unknown rows, identifier regression, or zero correct-mode markdown as hard failures. Record negative FPR and cold timing without using either to manufacture a semantic pass/fail.

**Acceptance criteria:**
- [ ] Corpus exclusions, commit SHAs, artifact identities, and zero missing reference checks are recorded.
- [ ] All arms complete every query with no missing/unknown result rows and identical frozen modes.
- [ ] Correct-mode markdown recall/nDCG is non-zero; otherwise broad promotion remains blocked and the escalation trigger is reported.
- [ ] Identifier set/quality does not regress in production versus lexical.
- [ ] Overall, per-language, cluster, semantic-contribution, cold timing, and negative diagnostics are recorded without being promoted to task-completion evidence.
- [ ] No sealed file or aggregate is read, generated, or claimed.
- [ ] Lead records the full verification ledger and commits the evidence with `serial-worker-commit`.
