# Task 4 Report: Workflow Rows For Context, Trace, And Impact

## Changed Files

- `scripts/benchmarks/miller-foundation-cases.json`
- `scripts/bench-foundation-matrix.py`
- `scripts/benchlib/scoring.py`
- `scripts/benchlib/reporting.py`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task4-workflows/summary.md`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task4-workflows/results.csv`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task4-workflows/results.json`
- `.razorback/sdd/task-4-report.md`

## Miller Evidence Used

- `workspace open/status/health` on `/Users/murphy/source/miller/.worktrees/foundation-effectiveness-matrix`: confirmed the requested worktree, branch, fresh index, current search/content sidecars, and no freshness blocker.
- `context` on the benchmark area in the Task 4 worktree: confirmed the active runner/scoring/reporting entry points and the plan's Task 4 section.
- `inspect` on `scripts/bench-foundation-matrix.py`, `scripts/benchlib/scoring.py`, and `scripts/benchlib/reporting.py`: confirmed existing validation, scoring, output, and report shapes before edits.
- Cross-workspace `context` calls for Julie, Eros, Flask, Zod, and Miller: selected stable context anchors and verified non-usage compact context output includes the `## next inspect` footer.
- Cross-workspace `trace` calls: selected hard-gated refs rows for `FastSearchTool`, `add_url_rule`, `SearchRoutePlanner.Plan`, and `WorkspaceStore.ListSemanticInputs`; confirmed Zod `ZodObject` is a structured `needs-search` ambiguity; confirmed Flask bridge is provider-scoped/unsupported.
- Cross-workspace `impact` calls: selected hard-gated impact anchors for `SearchRoutePlanner.Plan`, `WorkspaceStore.ListSemanticInputs`, and `PutSemanticInput`.
- Final worktree `impact(git=true)`: confirmed the final diff maps primarily to benchmark runner/scoring/reporting changes and suggested benchmark/trace telemetry test surfaces; no extra Task 4 gate beyond the brief's explicit commands was required.

## Row Counts

- `context.workflow`: 5
- `trace.refs`: 5
- `trace.path`: 1
- `trace.bridge`: 1
- `impact.workflow`: 3
- Task 3 path-scored rows preserved: 82

## Generated Evidence

- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task4-workflows/summary.md`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task4-workflows/results.csv`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task4-workflows/results.json`

## Verification

- RED check: manifest/output assertion failed before edits with `AssertionError: missing context coverage`.
- `python3 -m py_compile scripts/benchlib/*.py scripts/bench-julie-miller-search-inspect.py scripts/bench-foundation-matrix.py`: PASS.
- `python3 scripts/bench-julie-miller-search-inspect.py --repos miller --skip-julie --skip-miller-refresh --gate --out-dir /tmp/miller-search-inspect-task4-smoke`: PASS.
- `python3 scripts/bench-foundation-matrix.py --tasks context.workflow,trace.refs,trace.path,trace.bridge,impact.workflow --skip-julie --out-dir docs/findings/benchmarks/2026-06-27-foundation-matrix/task4-workflows --gate`: PASS.
- Manifest/output assertion: PASS; `task3_rows=82`, `task4_rows=15`, `hard_task4_rows=12`, structured unsupported/no-path rows present.
- `git diff --check`: PASS.

## Concerns Or Blockers

- No blockers.
- Julie workflow comparison rows are report-only and were skipped by the required `--skip-julie` verification command.
