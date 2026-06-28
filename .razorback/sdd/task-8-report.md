# Task 8 Report: Full Matrix Baseline Run And Gate Calibration

## Changed Files

- `scripts/bench-foundation-matrix.py`
- `scripts/benchmarks/miller-foundation-cases.json`
- `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/summary.md`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/results.csv`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/results.json`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/calibration.md`
- `.razorback/sdd/task-8-report.md`

## Miller Evidence Used

- `workspace status` on `/Users/murphy/source/miller/.worktrees/foundation-effectiveness-matrix`: confirmed the worktree index was fresh before and after edits.
- `context` for the foundation matrix calibration area: confirmed the active surfaces were the Task 8 plan section, runner, manifest, and finding.
- `inspect` on `scripts/bench-foundation-matrix.py`: confirmed the runner entry points, gate function, output writer, and main execution flow before editing.
- `inspect` on `scripts/benchmarks/miller-foundation-cases.json`: confirmed the manifest hard-gate structure before calibration.
- `inspect` on the main finding and implementation plan: confirmed current finding shape and Task 8 acceptance criteria.
- `impact git=true` after edits: confirmed the final diff impacts the benchmark runner/main and related benchmark/report surfaces.

## Final Baseline Command Outcomes

- Report-only/full evidence run completed at `/tmp/miller-foundation-task8-precalibration`.
- Final calibrated gate run completed at `docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline`.
- Julie was available (`julie-server 7.15.2`), so no `--skip-julie` fallback was needed.
- Final baseline files generated: `summary.md`, `results.csv`, `results.json`, and `calibration.md`.

## Calibrated Hard Gates

- Miller exact-symbol retrieval present on original nine repos: `9/9`, hard threshold `9/9`.
- Miller file retrieval present on original nine repos: `9/9`, hard threshold at least `7/9`.
- Miller source-auto retrieval present on original nine repos: `9/9`, hard threshold at least `8/9`.
- Miller inspect overview present on original nine repos: `9/9`, hard threshold `9/9`.
- Eros-facing CLI contract JSON/JSONL parse failures: `0/15`, hard threshold `0`.

## Report-Only Misses

- Julie rows: `69/97` present, `29/97` top-ranked, `54/97` selected-mode pass.
- Miller present-but-not-top path rows: `18`, kept report-only for ranking/recovery work.
- Workflow call-count-to-anchor: `39/56` required anchors present across `16` workflow rows.
- Miller latency and output-size medians remain report-only.
- No metrics CLI contract rows were present in this manifest.
- Adoption and telemetry interpretation remains report-only; Task 6 keeps the parseability evidence.

## Verification Commands

- `python3 -m py_compile scripts/benchlib/*.py scripts/bench-julie-miller-search-inspect.py scripts/bench-foundation-matrix.py` passed.
- `python3 scripts/bench-julie-miller-search-inspect.py --gate` passed.
- `python3 scripts/bench-foundation-matrix.py --out-dir docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline --gate` passed.
- `scripts/test.sh` passed: `2410` passed, `0` failed, `0` skipped.
- Final output assertion passed: required files exist, final gate is PASS, calibrated thresholds match, contract parse failures are zero, calibration notes explain hard gates/report-only misses, and the main finding links final baseline evidence.
- `git diff --check` passed after removing regenerated narrow-benchmark evidence outside Task 8 write scope.

## Concerns Or Blockers

- None.
