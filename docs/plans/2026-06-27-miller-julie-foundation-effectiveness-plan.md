# Miller Julie Foundation Effectiveness Matrix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Build a repeatable, evidence-producing matrix that compares Miller and Julie as product inputs while judging Miller on whether it is a better deterministic agent foundation and a cleaner Eros fact source.

**Architecture:** Keep the existing search/inspect benchmark as the narrow regression gate, then add a broader foundation matrix runner with explicit task classes, adapters, scoring, and report generation. Julie remains report-only evidence for useful UX patterns; Miller owns the hard gates for agent workflow usefulness, fewer-tool ergonomics, and stable Eros-facing JSON/JSONL contracts.

**Tech Stack:** Python 3 benchmark scripts, local MCP JSON-RPC processes, Miller CLI JSON/JSONL commands, existing `scripts/bench-julie-miller-search-inspect.py`, docs findings under `docs/findings/`, .NET 10 fast tests where C# surfaces are touched.

**Architecture Quality:** Affected modules are benchmark scripts, benchmark support code, findings docs, and contract/report docs. Caller-facing interfaces are the new matrix manifest, runner flags, CSV/JSON/Markdown output shape, and hard-gate thresholds; Miller MCP/runtime interfaces are not changed by this plan. Architecture risk is medium because the matrix will drive future product goals, but locality is good if adapters, task definitions, scoring, and reporting are kept separate and Julie remains report-only.

## Global Constraints

- Do not add any new MCP tool.
- Do not reintroduce a metrics MCP surface; metrics stays CLI/export only.
- Do not move semantic/vector retrieval into Miller; semantic/vector workflows stay in Eros.
- Do not make Julie parity a hard gate. Julie is a comparison baseline and source of adaptation candidates.
- Do not clone Julie's tool surface. Adapt useful workflow behavior into Miller's smaller tool set.
- Keep the existing search/inspect benchmark gate working during every task.
- Keep Miller hard gates based on current Miller behavior, Eros-facing contract stability, and measurable agent workflow usefulness.
- Keep Julie unavailable as a non-fatal report state; the matrix must run Miller-only when `/Users/murphy/source/julie/target/release/julie-server` is missing.
- Keep benchmark rows deterministic: every row must name a repo, task class, tool route, query/target, expected anchor, scoring mode, and whether it is hard-gated or report-only.
- Keep generated evidence under `docs/findings/benchmarks/` and do not overwrite old run evidence.
- Use real local repositories under `/Users/murphy/source`; no synthetic-only benchmark rows for final evidence.
- Use TDD or a failing-gate-first workflow for each behavior slice and run the narrowest useful verification before broad gates.

---

## Product Frame

This plan is not "make Miller just like Julie." It treats Julie as prior art that shows which agent workflows were useful, then asks whether Miller can deliver those workflows with fewer tools, simpler .NET code, stable contracts, and better Eros integration.

Julie strengths to learn from:

- `fast_search` is forgiving when the caller intent is fuzzy.
- `deep_dive` packages a common search -> symbols -> refs -> read chain into one compact call.
- Julie's search episode analysis judges search by whether it leads to useful downstream actions.
- Julie's tool guidance names workflows, not only schemas.

Miller direction:

- Improve existing tools and guidance instead of expanding MCP surface area.
- Measure `search`, `inspect`, `context`, `trace`, and `impact` as a workflow, not isolated commands only.
- Treat `capabilities`, refresh/status/health/onboarding, JSON read commands, and JSONL exports as first-class Eros foundation surfaces.
- Report Julie wins as adaptation candidates such as "route recovery," "output compactness," "ambiguity guidance," or "workflow packaging," not as direct parity tasks.

## Source Evidence

Existing benchmark and comparison artifacts:

- `scripts/bench-julie-miller-search-inspect.py`
- `docs/findings/2026-06-27-julie-miller-search-inspect-benchmark.md`
- `docs/findings/benchmarks/2026-06-27-search-inspect/summary.md`
- `docs/findings/2026-06-05-julie-vs-miller-search-quality-matrix.md`
- `docs/contracts/cli-eros-v1.md`

Live orientation anchors:

- Existing benchmark runner constants and repo case manifest: `scripts/bench-julie-miller-search-inspect.py:31-149`.
- Existing benchmark gate thresholds: `scripts/bench-julie-miller-search-inspect.py:456-475`.
- Existing runner loop: `scripts/bench-julie-miller-search-inspect.py:478-615`.
- Miller CLI/Eros contract command list: `docs/contracts/cli-eros-v1.md`.
- Miller capability renderer: `src/Miller.Server/Cli/CliCapabilities.cs:10`.
- Telemetry onboarding reader: `src/Miller.Server/Telemetry/TelemetryOnboardingReader.cs:65`.
- Telemetry export reader: `src/Miller.Server/Telemetry/TelemetryExportReader.cs:9`.
- Julie generic tool list: `/Users/murphy/source/julie/src/cli_tools/generic.rs:13-27`.
- Julie useful downstream actions: `/Users/murphy/source/julie/src/dashboard/search_analysis.rs:8-17`.

## Matrix Taxonomy

The expanded matrix has these task classes:

| Task class | Miller route | Julie report-only route | Scoring focus |
|---|---|---|---|
| `retrieval.symbol` | `search mode=auto` | `fast_search` | expected definition/file first and present |
| `retrieval.file` | `search mode=auto` | `fast_search` | expected file first and present |
| `retrieval.source_auto` | `search mode=auto` compact | `fast_search` | source-body anchor present without caller selecting `mode=source` |
| `retrieval.source_best` | `search mode=source` | none | data/ranking ceiling for Miller source search |
| `retrieval.docs` | `search mode=content` | `fast_search` if useful | docs/prose route correctness |
| `retrieval.region` | `search regions=...` | report-only if comparable | comment/doc-comment/literal route state and fail-closed guidance |
| `inspect.summary` | `inspect depth=summary` | `get_symbols` if added later | shallow definition usefulness |
| `inspect.overview` | `inspect depth=overview` | `deep_dive depth=overview` | edit-orientation context, bounded output |
| `inspect.full` | `inspect depth=full` | `deep_dive depth=full` if useful | complete context and size tradeoff |
| `ambiguity` | `inspect`/`trace` scoped and unscoped | `deep_dive context_file` | clear winner vs candidate guidance |
| `workflow.context` | `context` optionally after `search` | `get_context` | expected anchor set and next inspect hints |
| `workflow.refs` | `trace mode=refs` | `fast_refs` | reference availability, noise, and fallback guidance |
| `workflow.path` | `trace mode=path` or `mode=bridge` | `call_path` | call/path/bridge evidence when anchored |
| `workflow.impact` | `impact target` or `changed_paths` | `blast_radius` | impacted symbols and likely tests |
| `eros.contract` | CLI JSON/JSONL commands | none | parseable stable contracts and advertised capabilities |
| `ops.readiness` | `workspace status/health/onboarding` | `manage_workspace` report-only | freshness, sidecar, telemetry, startup guidance |
| `adoption.telemetry` | telemetry export/onboarding analysis | Julie search episodes report-only | search-to-useful-action convergence |

Hard gates apply only to Miller rows marked `hard_gate=true`.

Julie rows can produce `adaptation_candidate` records when Julie finds an expected anchor, gives clearer guidance, or uses substantially less output for the same task. Those candidates feed the final report; they do not fail the branch.

## File Structure

Create:

- `scripts/benchlib/__init__.py` - package marker for shared benchmark helpers.
- `scripts/benchlib/mcp_client.py` - small JSON-RPC MCP process client extracted from the current benchmark script.
- `scripts/benchlib/scoring.py` - path extraction, output-size metrics, JSON/JSONL parsing checks, and expected-anchor scoring.
- `scripts/benchlib/reporting.py` - CSV, JSON, and Markdown summary generation helpers.
- `scripts/bench-foundation-matrix.py` - expanded foundation matrix runner.
- `scripts/benchmarks/miller-foundation-cases.json` - manifest of real local repo cases and task rows.
- `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md` - evergreen finding explaining the matrix purpose, latest run, and interpretation rules.

Modify:

- `scripts/bench-julie-miller-search-inspect.py` - keep the existing narrow gate, but reuse shared benchmark helpers once they exist.
- `docs/findings/2026-06-27-julie-miller-search-inspect-benchmark.md` - add a short pointer to the broader foundation matrix.
- `docs/README.md` - list the new foundation matrix finding if the findings map needs an active entry.
- `TODO.md` - review the active TODO text and remove or reword an old-matrix follow-up when it still points at the old matrix as the only active follow-up. If no matching text exists, record "no TODO.md change needed" in the final report.

Generated evidence:

- `docs/findings/benchmarks/2026-06-27-foundation-matrix/summary.md`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/results.csv`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/results.json`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/prep.csv`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md`

Test and verification files:

- No C# test file is expected unless implementation touches Miller runtime or CLI capability code.
- Python script verification uses `python3 -m py_compile` plus focused runner gates.
- Existing repo fast gate remains `scripts/test.sh`.

## Task 1: Extract Shared Benchmark Support

**Files:**

- Create: `scripts/benchlib/__init__.py`
- Create: `scripts/benchlib/mcp_client.py`
- Create: `scripts/benchlib/scoring.py`
- Create: `scripts/benchlib/reporting.py`
- Modify: `scripts/bench-julie-miller-search-inspect.py`

**Interfaces:**

- Consumes: current `McpProcess`, `content_text`, `is_empty_text`, `first_path`, `score_text`, `score_miller_search_json`, `summarize_by_tool`, and `summarize_by_task` behavior from `scripts/bench-julie-miller-search-inspect.py`.
- Produces: shared Python helper functions with the same observable scoring behavior for the existing narrow benchmark.

**What to build:** Move the generic benchmark plumbing out of the narrow search/inspect script so the broader matrix can reuse it without copying JSON-RPC and scoring code.

**Approach:** Keep the extraction mechanical. The existing script must produce the same CSV columns, summary tables, and gate behavior after the helpers are imported. Do not add new foundation-matrix concepts in this task.

**Acceptance criteria:**

- [x] `python3 -m py_compile scripts/benchlib/*.py scripts/bench-julie-miller-search-inspect.py` passes.
- [x] `python3 scripts/bench-julie-miller-search-inspect.py --repos miller --skip-julie --skip-miller-refresh --gate --out-dir /tmp/miller-search-inspect-smoke` passes.
- [x] The existing gate thresholds in `scripts/bench-julie-miller-search-inspect.py` are unchanged.
- [x] No generated evidence under `docs/findings/benchmarks/` is overwritten.
- [x] Worker-scope verification passes, committed.

## Task 2: Add Foundation Matrix Manifest And Runner Skeleton

**Files:**

- Create: `scripts/bench-foundation-matrix.py`
- Create: `scripts/benchmarks/miller-foundation-cases.json`
- Modify: `scripts/benchlib/scoring.py`
- Modify: `scripts/benchlib/reporting.py`

**Interfaces:**

- Consumes: shared MCP client and scoring helpers from Task 1.
- Produces: a manifest-driven runner with `--repos`, `--tasks`, `--skip-julie`, `--skip-miller-refresh`, `--out-dir`, and `--gate` flags.

**What to build:** Add the broad runner and manifest shape, initially covering retrieval and inspect rows that overlap the existing benchmark plus explicit task-class metadata.

**Approach:** Store rows in JSON so future goal runs can add cases without editing runner code. Every row must include `id`, `repo`, `task_class`, `intent`, `miller`, `julie`, `expected`, `scoring`, and `gate` sections. The runner should validate the manifest before executing any tool calls and print actionable validation errors.

Example manifest shape:

```json
{
  "id": "flask.inspect.overview.flask-class",
  "repo": "flask",
  "task_class": "inspect.overview",
  "intent": "Resolve the canonical Flask application class.",
  "miller": {"tool": "inspect", "args": {"target": "Flask", "depth": "overview"}},
  "julie": {"tool": "deep_dive", "args": {"symbol": "Flask", "depth": "overview"}, "report_only": true},
  "expected": {"path": "src/flask/app.py", "anchor": "class Flask"},
  "scoring": {"mode": "path_present", "top_path": true},
  "gate": {"hard": true}
}
```

**Acceptance criteria:**

- [x] Runner rejects malformed rows before opening MCP processes.
- [x] Runner can execute a Miller-only smoke run for `miller`, `flask`, and `zod`.
- [x] `results.csv` includes `row_id`, `repo`, `task_class`, `tool`, `route`, `hard_gate`, `expected_present`, `expected_top`, `empty`, `ms`, `output_chars`, `first_path`, and `adaptation_candidate`.
- [x] `results.json` includes the same fields plus structured diagnostics for parse failures and skipped tools.
- [x] Existing narrow benchmark still passes its focused smoke command from Task 1.
- [x] Worker-scope verification passes, committed.

## Task 3: Cover Retrieval, Inspect, And Ambiguity Rows

**Files:**

- Modify: `scripts/benchmarks/miller-foundation-cases.json`
- Modify: `scripts/bench-foundation-matrix.py`
- Modify: `scripts/benchlib/scoring.py`
- Create generated run evidence under `docs/findings/benchmarks/2026-06-27-foundation-matrix/`

**Interfaces:**

- Consumes: manifest runner from Task 2 and current local repos under `/Users/murphy/source`.
- Produces: scored retrieval, inspect, and ambiguity coverage across Miller, Julie, Eros, Express, Flask, Gson, Newtonsoft.Json, Zod, and jq.

**What to build:** Populate the first real matrix with retrieval and inspect rows, then add ambiguity cases that exercise test/prod, source/package version, and same-file duplicate behavior.

**Approach:** Start with the nine repos from the existing benchmark. Add rows for:

- exact symbol search,
- file/path lookup,
- source-body auto recovery,
- explicit source search ceiling,
- docs/prose content lookup,
- source-region/comment/string-literal behavior where the repo has a stable expected literal or comment,
- `inspect summary`, `inspect overview`, and `inspect full`,
- unscoped ambiguity and scoped disambiguation.

For Julie, run `fast_search` and `deep_dive` only when a comparable row exists. Mark all Julie rows report-only.

**Acceptance criteria:**

- [x] At least 9 repos and at least 60 total task rows are represented.
- [x] Each row has an expected anchor that can be reviewed as a real file path, symbol, or JSON/JSONL contract field.
- [x] Miller hard-gated retrieval rows preserve the existing search/inspect gate strengths from `scripts/bench-julie-miller-search-inspect.py`.
- [x] Ambiguity rows distinguish "clear preferred definition" from "correct explicit candidates" instead of forcing a single winner.
- [x] Julie rows can be skipped without failing the run when Julie is unavailable.
- [x] Worker-scope verification passes, committed.

## Task 4: Add Workflow Rows For Context, Trace, And Impact

**Files:**

- Modify: `scripts/benchmarks/miller-foundation-cases.json`
- Modify: `scripts/bench-foundation-matrix.py`
- Modify: `scripts/benchlib/scoring.py`
- Modify: `scripts/benchlib/reporting.py`

**Interfaces:**

- Consumes: Miller `context`, `trace`, and `impact` tool calls plus comparable Julie `get_context`, `fast_refs`, `call_path`, and `blast_radius` calls when useful.
- Produces: workflow scoring that measures whether the tool result gives the agent enough correct next anchors, not only whether one file appears somewhere.

**What to build:** Add task rows that model agent workflows Julie used to package well: search-to-context, symbol-to-refs, path/call-flow discovery, and impact/test selection.

**Approach:** Use real workflows from the existing findings:

- Julie fast search dispatch flow in `/Users/murphy/source/julie`.
- Miller/Eros semantic importer workflow in `/Users/murphy/source/eros`.
- Flask class inspection to child/caller navigation in `/Users/murphy/source/flask`.
- Zod versioned-package ambiguity in `/Users/murphy/source/zod`.
- A C# impact case in `/Users/murphy/source/miller` around `SearchRoutePlanner.Plan`.

Workflow scoring should record:

- expected anchor count,
- expected anchors present,
- first useful anchor,
- follow-up hint present,
- output chars,
- whether the row is edit-ready, inspect-ready, or needs another search.

**Acceptance criteria:**

- [x] `context` rows score expected anchors and the `next inspect` footer.
- [x] `trace refs` rows score definition presence, reference count, and noise/skipped diagnostics.
- [x] `trace path` or `trace bridge` rows are provider-scoped and report `unsupported` or `no_path` as structured outcomes, not generic failures.
- [x] `impact` rows score impacted symbols and likely tests separately.
- [x] Julie comparable rows stay report-only and can emit adaptation candidates.
- [x] Worker-scope verification passes, committed.

## Task 5: Add Eros Foundation Contract Rows

**Files:**

- Modify: `scripts/benchmarks/miller-foundation-cases.json`
- Modify: `scripts/bench-foundation-matrix.py`
- Modify: `scripts/benchlib/scoring.py`
- Modify: `docs/contracts/cli-eros-v1.md` only if the live contract doc is stale relative to current CLI output.

**Interfaces:**

- Consumes: Miller CLI commands documented in `docs/contracts/cli-eros-v1.md`.
- Produces: hard-gated parse and capability checks for Eros-facing JSON/JSONL surfaces.

**What to build:** Add rows that prove Miller is a clean foundation for Eros through stable process contracts, not private .NET internals.

**Approach:** Execute CLI commands with bounded output checks. The examples below use the Miller repo as the concrete smoke target; full matrix rows repeat the same command shapes with each selected manifest row's registered repository root.

- `miller capabilities --json`
- `miller workspace status --json --workspace-id /Users/murphy/source/miller`
- `miller workspace health --json --workspace-id /Users/murphy/source/miller`
- `miller workspace onboarding --json --workspace-id /Users/murphy/source/miller`
- `miller search SearchTool --json --workspace-id /Users/murphy/source/miller`
- `miller inspect SearchTool --json --workspace-id /Users/murphy/source/miller --depth overview`
- `miller context "search inspect effectiveness" --json --workspace-id /Users/murphy/source/miller`
- `miller impact SearchRoutePlanner.Plan --json --workspace-id /Users/murphy/source/miller`
- `miller trace SearchRoutePlanner.Plan --json --workspace-id /Users/murphy/source/miller --mode refs`
- `miller patterns --json --workspace-id /Users/murphy/source/miller`
- `miller content export`
- `miller telemetry export --jsonl`
- `miller symbols export --jsonl --workspace-id /Users/murphy/source/miller`
- `miller references export --jsonl --workspace-id /Users/murphy/source/miller`
- `miller complexity export --jsonl --workspace-id /Users/murphy/source/miller`

Metrics CLI commands may be included as report-only contract rows if the current `capabilities --json` advertises them. They must not be counted as agent MCP tool adoption rows.

**Acceptance criteria:**

- [x] `capabilities --json` advertises every hard-gated command the matrix depends on.
- [x] JSON command rows parse as JSON and include required top-level contract fields.
- [x] JSONL export rows parse at least the first 20 non-empty lines or the full stream when fewer than 20 lines exist.
- [x] Missing optional data is reported with a structured `empty_allowed` or `unsupported` outcome when the contract permits it.
- [x] Contract rows fail hard on malformed JSON, malformed JSONL, missing required fields, or undocumented command drift.
- [x] Worker-scope verification passes, committed.

## Task 6: Add Adoption And Episode Analysis

**Files:**

- Modify: `scripts/bench-foundation-matrix.py`
- Modify: `scripts/benchlib/reporting.py`
- Modify: `scripts/benchlib/scoring.py`
- Create generated `adoption-summary.md` in the benchmark output directory.

**Interfaces:**

- Consumes: Miller telemetry export/onboarding JSON and Julie's concept of search episodes leading to useful downstream actions.
- Produces: report-only adoption evidence and hard-gated validation that Miller telemetry can support future Eros/adoption analysis.

**What to build:** Add a report section that asks whether agents are likely to choose the right Miller tool and whether local telemetry can show friction without storing raw queries.

**Approach:** Do not rank product quality by raw usage volume alone. Record:

- search/inspect/context/trace/impact usage counts from telemetry,
- error and empty-result rates by tool/op when available,
- onboarding starter commands and common misses for the selected workspace,
- matrix workflow rows where a Julie-like one-call action still beats Miller's current sequence.

Raw telemetry export parsing is a hard gate; interpretation of usage/adoption quality is report-only.

**Acceptance criteria:**

- [x] `telemetry export --jsonl` parsing is hard-gated when telemetry exists.
- [x] `workspace onboarding --json` parsing is hard-gated for the Miller repo.
- [x] Adoption summary separates "tool exists and is parseable" from "agents actually use it."
- [x] Report identifies low-use tools without proposing MCP surface expansion by default.
- [x] Worker-scope verification passes, committed.

## Task 7: Generate The Foundation Finding And Adaptation Candidate Report

**Files:**

- Create: `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md`
- Modify: `docs/findings/2026-06-27-julie-miller-search-inspect-benchmark.md`
- Modify: `docs/README.md`
- Review: `TODO.md`
- Create generated run evidence under `docs/findings/benchmarks/2026-06-27-foundation-matrix/`

**Interfaces:**

- Consumes: complete foundation matrix output from Tasks 2-6.
- Produces: a human-readable product finding that ranks what Julie still does better and how Miller should adapt without cloning Julie.

**What to build:** Write the finding that a future goal can use to choose the next Miller implementation slice.

**Approach:** The finding must include:

- a clear statement that Julie is a baseline, not a parity target,
- hard-gate Miller results,
- report-only Julie deltas,
- Eros foundation contract results,
- top adaptation candidates ranked by impact and implementation locality,
- rejected moves such as adding MCP metrics or semantic retrieval to Miller,
- recommended next implementation goals with the first goal called out explicitly.

**Acceptance criteria:**

- [x] Finding links to the raw CSV, JSON, prep CSV, summary, and adaptation candidate files.
- [x] Finding separates hard-gated Miller failures from report-only Julie wins.
- [x] At least three adaptation candidates are classified by category: route recovery, output usefulness, ambiguity guidance, graph workflow, Eros contract, or adoption guidance.
- [x] The top recommended next implementation goal is concrete enough to turn into a separate implementation plan.
- [x] No historical benchmark file is edited to pretend old evidence came from the new matrix.
- [x] Worker-scope verification passes, committed.

## Task 8: Full Matrix Baseline Run And Gate Calibration

**Files:**

- Modify: `scripts/bench-foundation-matrix.py`
- Modify: `scripts/benchmarks/miller-foundation-cases.json`
- Modify: `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md`
- Create generated run evidence under `docs/findings/benchmarks/2026-06-27-foundation-matrix/`

**Interfaces:**

- Consumes: all prior tasks.
- Produces: calibrated hard gates for future goal runs and a baseline evidence set from the rebuilt Miller version.

**What to build:** Run the complete matrix, inspect failures, and calibrate thresholds so future gates protect Miller strengths without freezing known improvement work.

**Approach:** Start with report-only mode, classify every miss, then enable only the hard gates that protect already-shipped behavior or active Eros contracts. Known product gaps should become adaptation candidates, not failing gates, until a follow-up implementation plan closes them.

Initial hard gates:

- Existing `scripts/bench-julie-miller-search-inspect.py --gate` passes.
- Foundation runner validates manifest and produces CSV, JSON, Markdown, and candidate reports.
- Miller exact-symbol retrieval present count remains `9/9` on the original nine-repo set.
- Miller file retrieval present count remains at least `7/9` on the original nine-repo set.
- Miller source-auto present count remains at least `8/9` on the original nine-repo set.
- Miller inspect overview present count remains `9/9` on the original nine-repo set.
- Eros contract JSON/JSONL rows have zero parse failures.

Report-only metrics:

- Julie top/present counts.
- Julie output-size medians.
- Miller latency and output-size medians unless they become extreme enough to block interactive use.
- Workflow call-count-to-anchor.
- Adoption and telemetry interpretation.
- Metrics CLI contract rows.

**Acceptance criteria:**

- [x] Full matrix report-only run completes.
- [x] Full matrix `--gate` run completes after threshold calibration.
- [x] Calibration notes explain every report-only miss and every hard gate.
- [x] Existing narrow search/inspect benchmark gate still passes.
- [x] `scripts/test.sh` passes unless only docs/generated evidence changed after the last passing same-HEAD run.
- [x] Branch-gate verification passes, committed.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` / `AGENTS.md` testing and build sections, plus `docs/contracts/cli-eros-v1.md` for Eros-facing contract rows.

**Worker red/green scope:** Focused script and matrix checks for the touched slice:

Task 1 focused command:

```bash
python3 -m py_compile scripts/benchlib/*.py scripts/bench-julie-miller-search-inspect.py
```

Task 2 and later focused commands:

```bash
python3 -m py_compile scripts/benchlib/*.py scripts/bench-julie-miller-search-inspect.py scripts/bench-foundation-matrix.py
python3 scripts/bench-julie-miller-search-inspect.py --repos miller --skip-julie --skip-miller-refresh --gate --out-dir /tmp/miller-search-inspect-smoke
python3 scripts/bench-foundation-matrix.py --repos miller --skip-julie --skip-miller-refresh --out-dir /tmp/miller-foundation-smoke
```

**Worker ceiling:** Workers may run the full foundation matrix for selected repos and may run `scripts/test.sh` after their slice. Workers do not own final gate calibration or final finding interpretation.

**Worker gate invariant:** Focused gates prove the runner validates manifests, calls the intended Miller/Julie surfaces, parses output, scores expected anchors, and preserves the existing narrow benchmark gate.

**Lead affected-change scope:** After coherent benchmark-runner changes, run:

```bash
python3 -m py_compile scripts/benchlib/*.py scripts/bench-julie-miller-search-inspect.py scripts/bench-foundation-matrix.py
python3 scripts/bench-julie-miller-search-inspect.py --gate
python3 scripts/bench-foundation-matrix.py --skip-julie --out-dir /tmp/miller-foundation-miller-only --gate
scripts/test.sh
```

**Branch gate:** Run:

```bash
dotnet build Miller.slnx -c Release
scripts/test.sh
python3 scripts/bench-julie-miller-search-inspect.py --gate
python3 scripts/bench-foundation-matrix.py --out-dir docs/findings/benchmarks/2026-06-27-foundation-matrix --gate
```

Run `scripts/test.sh scale` only if implementation touches indexing, extraction, sidecar build paths, or workspace refresh/full-scan behavior.

**Replay/metric evidence:** Hard gates are Miller behavior and Eros contract gates only. Julie comparison numbers, latency medians, output-size medians, workflow call counts, adoption interpretation, and metrics CLI rows are report-only unless a later plan explicitly promotes them.

**Escalation triggers:** Stop for user decision if a proposed fix requires a new MCP tool, semantic/vector retrieval inside Miller, weakening Eros contracts, changing existing JSON shapes incompatibly, or turning a known improvement candidate into a failing gate before implementation is approved.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the failure is in a test or gate this plan explicitly says to recalibrate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. For matrix evidence, record hard-gate counts, Julie report-only counts, output directories, and the top adaptation candidates. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Model Routing

**Project source of truth:** No `RAZORBACK.md` was found in this repo during planning. Use current harness defaults unless the user specifies a reviewer/model choice at approval time.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage.

- Harness mapping: inherit.

**Implementation tier:** bounded worker tasks from this plan.

- Harness mapping: inherit.

**Mechanical tier:** docs, fixtures, manifest row additions, py_compile checks, and formatting with no gate interpretation.

- Harness mapping: inherit.

**Gate-interpretation reviewer:** lead agent for threshold calibration, failed benchmark interpretation, and deciding whether a miss is a product gap or a harness bug.

- Harness mapping: inherit.

**Escalation tier:** benchmark architecture changes, Eros contract drift, repeated JSON/JSONL parse failures, pressure to expand MCP surface, or any runtime behavior change proposed while executing this matrix plan.

- Harness mapping: inherit.

**Worker eligibility:** Workers may implement Tasks 1-7 when they keep changes inside named files and verify through the worker red/green commands. Task 8 requires lead ownership for gate calibration and final interpretation.

**Escalation triggers:** Any need to change Miller runtime behavior, add an MCP tool, alter stable JSON shapes, or move semantic/vector behavior into Miller must return to the lead and user as a separate implementation plan.

**Mechanical exclusion:** Mechanical workers cannot own failed-gate interpretation, benchmark threshold calibration, Eros contract interpretation, or adaptation candidate ranking.

**Unsupported harness behavior:** If the harness cannot choose models per worker, use inherit and continue.

## Execution Order

1. Task 1: extract shared benchmark support while preserving the existing narrow gate.
2. Task 2: add the foundation runner and manifest skeleton.
3. Task 3: populate retrieval, inspect, and ambiguity rows.
4. Task 4: add context, trace, and impact workflow rows.
5. Task 5: add Eros contract rows.
6. Task 6: add adoption and episode analysis.
7. Task 7: generate the foundation finding and adaptation candidate report.
8. Task 8: run the baseline, calibrate gates, and record final evidence.

## Final Acceptance

- [ ] Existing `scripts/bench-julie-miller-search-inspect.py --gate` still passes.
- [ ] New foundation matrix runner exists and validates a manifest before tool calls.
- [ ] New matrix covers retrieval, inspect, ambiguity, context, trace, impact, Eros contracts, readiness, and adoption evidence.
- [ ] Julie rows are report-only and can be skipped when Julie is unavailable.
- [ ] Miller hard gates protect existing search/inspect strengths and Eros JSON/JSONL contract parseability.
- [ ] Generated evidence includes CSV, JSON, Markdown summary, prep data, and adaptation candidates.
- [ ] The finding ranks what Julie still does better and how Miller should adapt without cloning Julie.
- [ ] The top follow-up implementation goal is concrete enough to become its own plan.
- [ ] No new MCP tool is added.
- [ ] No semantic/vector retrieval is added to Miller.
- [ ] `scripts/test.sh` and branch-gate benchmark commands pass, or any skipped expensive gate is explicitly justified by unchanged code and reused same-HEAD evidence.
