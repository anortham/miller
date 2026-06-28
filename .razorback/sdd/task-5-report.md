# Task 5 Report: Eros Foundation Contract Rows

## Changed Files

- `scripts/benchmarks/miller-foundation-cases.json`
- `scripts/bench-foundation-matrix.py`
- `scripts/benchlib/scoring.py`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task5-eros-contracts/summary.md`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task5-eros-contracts/results.csv`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task5-eros-contracts/results.json`
- `.razorback/sdd/task-5-report.md`

## Miller Evidence Used

- `workspace status` for `/Users/murphy/source/miller/.worktrees/foundation-effectiveness-matrix`: confirmed the worktree index was fresh, reader mode, revision 18.
- `workspace refresh` for the same worktree: confirmed `status: unchanged`, `scanned: yes`, `swapped: no`, revision 18.
- `context` for the foundation benchmark/contract area: confirmed the relevant runner/reporting seed and `docs/contracts/cli-eros-v1.md` as the public contract source.
- `inspect` on `scripts/bench-foundation-matrix.py`: confirmed existing MCP execution, validation, output writing, and main-loop structure before adding a separate CLI route.
- `inspect` on `scripts/benchlib/scoring.py`: confirmed Task 3 path scoring and Task 4 workflow scoring entry points before appending contract scoring modes.
- `inspect` on `scripts/benchlib/reporting.py`: confirmed summary tables derive from generic result rows and did not need a behavior change.
- `inspect` on `scripts/benchmarks/miller-foundation-cases.json`: confirmed the manifest structure and existing row shape before appending Task 5 rows.
- `inspect` on `docs/contracts/cli-eros-v1.md`: confirmed the documented Eros-facing JSON and JSONL command surface.
- `trace refs score_manifest_path`: confirmed only the foundation runner calls the shared scorer, so adding an optional `capabilities` parameter preserved existing callers.
- `impact` on the final working-tree diff: confirmed the changed benchmark/scoring path and listed likely broader tests; the brief-required benchmark smoke and contract matrix covered the relevant Python runner behavior.

## Contract Row Counts

- Added 15 hard-gated Task 5 contract rows.
- `contract.cli.json`: 10 rows, 10 passed.
- `contract.cli.jsonl`: 5 rows, 5 passed.
- Manifest/output assertion confirmed Task 3 classes still exist, Task 4 workflow classes still exist, both contract task classes exist, all hard-gated contract rows passed, every hard-gated command was advertised by `capabilities --json`, and JSONL rows sampled the required first 20 non-empty lines.

## Contract Doc Update

`docs/contracts/cli-eros-v1.md` did not require changes. Live `miller capabilities --json` advertised the hard-gated JSON commands and export feeds used by the matrix, and live CLI output contained the required fields documented for those rows.

## Generated Evidence

Evidence path:

`docs/findings/benchmarks/2026-06-27-foundation-matrix/task5-eros-contracts/`

Files generated:

- `summary.md`
- `results.csv`
- `results.json`

## Verification

- `PYTHONPATH=scripts python3 - <<'PY' ...`: RED check first failed with unsupported `contract_json`; after implementation, the same assertion passed for contract JSON and JSONL scoring.
- `python3 -m py_compile scripts/benchlib/*.py scripts/bench-julie-miller-search-inspect.py scripts/bench-foundation-matrix.py`: passed.
- `python3 scripts/bench-julie-miller-search-inspect.py --repos miller --skip-julie --skip-miller-refresh --gate --out-dir /tmp/miller-search-inspect-task5-smoke`: passed.
- `python3 scripts/bench-foundation-matrix.py --tasks contract.cli.json,contract.cli.jsonl --skip-julie --out-dir docs/findings/benchmarks/2026-06-27-foundation-matrix/task5-eros-contracts --gate`: passed.
- Manifest/output assertion command: passed with `task3_classes=11`, `task4_classes=5`, `contract_rows=15`, `hard_contract_results=15`, `jsonl_rows=5`.
- Miller `impact` on final changed paths: completed; relevant verification remained the benchmark smoke plus contract matrix gate.
- `git diff --check`: passed.

## Concerns

- None.
