# Task 2 Report: Foundation Matrix Manifest And Runner Skeleton

## Files Changed

- Created `scripts/bench-foundation-matrix.py`
- Created `scripts/benchmarks/miller-foundation-cases.json`
- Modified `scripts/benchlib/scoring.py`
- Modified `scripts/benchlib/reporting.py`
- Updated `.razorback/sdd/task-2-report.md`

## Miller Calls Used

- `workspace status` on `/Users/murphy/source/miller/.worktrees/foundation-effectiveness-matrix`
  - Confirmed the requested worktree was indexed, fresh, and on the active Miller workspace.
- `context` for the Task 2 benchmark runner area
  - Confirmed the relevant plan section plus Task 1 helper surfaces: `scripts/benchlib/*` and `scripts/bench-julie-miller-search-inspect.py`.
- `inspect` on `scripts/benchlib/scoring.py`, `scripts/benchlib/reporting.py`, `scripts/benchlib/mcp_client.py`, and `scripts/bench-julie-miller-search-inspect.py`
  - Confirmed existing helper interfaces and the narrow benchmark's current MCP/scoring/reporting flow before editing.
- `impact` on `scripts/benchlib/scoring.py`, `scripts/benchlib/reporting.py`, and the final working-tree diff
  - Confirmed the shared helper changes impact the benchmark scripts and that the requested smoke commands are the relevant worker verification.
- `search mode=file` for `RAZORBACK.md` and `.razorback/sdd/task-2-report.md`
  - Confirmed no repo-local Razorback policy file was indexed and the task report path was not already indexed.

## Validation And Gate Evidence

- Red check before implementation:
  - `python3 -m py_compile scripts/bench-foundation-matrix.py`
  - Result: failed because the new runner did not exist yet.
- Compile verification:
  - `python3 -m py_compile scripts/benchlib/*.py scripts/bench-julie-miller-search-inspect.py scripts/bench-foundation-matrix.py`
  - Result: passed.
- Malformed manifest validation:
  - Created `/tmp/miller-foundation-bad-manifest.json` with a row missing required sections.
  - `python3 scripts/bench-foundation-matrix.py --manifest /tmp/miller-foundation-bad-manifest.json --repos miller --skip-julie --skip-miller-refresh --out-dir /tmp/miller-foundation-bad --gate`
  - Result: exited 2 with row-specific validation errors before Miller binary/process checks.
- Existing narrow benchmark smoke:
  - `python3 scripts/bench-julie-miller-search-inspect.py --repos miller --skip-julie --skip-miller-refresh --gate --out-dir /tmp/miller-search-inspect-smoke`
  - Result: passed.
- New foundation runner smoke:
  - `python3 scripts/bench-foundation-matrix.py --repos miller,flask,zod --skip-julie --skip-miller-refresh --out-dir /tmp/miller-foundation-smoke --gate`
  - Result: passed.
- Output contract check:
  - Verified `/tmp/miller-foundation-smoke/results.csv` includes `row_id`, `repo`, `task_class`, `tool`, `route`, `hard_gate`, `expected_present`, `expected_top`, `empty`, `ms`, `output_chars`, `first_path`, and `adaptation_candidate`.
  - Verified `/tmp/miller-foundation-smoke/results.json` includes those fields on every result row and structured `skipped_tool` diagnostics for skipped Julie rows.
- Diff hygiene:
  - `git diff --check`
  - Result: passed.

## Commit SHA

- Pending until commit creation. The final worker status response reports the commit SHA for this completed task slice.

## Concerns

- None.
