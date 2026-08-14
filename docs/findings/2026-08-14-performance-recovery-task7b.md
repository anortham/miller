# Performance recovery Task 7B: post-rotation bounded reads

**Status:** PASS on Linux. Native Windows verification remains in Task 8.

## Evidence boundary

- Store: `/home/murphy/.miller/task7a-evidence-LvqrXG/snapshot`
- Family: `a271f2bd-7368-4da6-b5aa-24ffad69fb1f`
- View: `7857a50b-4b5a-47ba-8c45-d4df703cc79e`
- Baseline: `/home/murphy/.miller/task7b-baseline.Yi4fuo/eight-arm.jsonl`
- Fallback-only candidate: `/home/murphy/.miller/task7b-baseline.Yi4fuo/eight-arm-candidate.jsonl`
- Final: `/home/murphy/.miller/task7b-baseline.Yi4fuo/eight-arm-final.jsonl`

The opt-in Scale harness opens the copied family read-only and records reverse/forward × exact/fallback ×
1/100 candidate arms. Each cell has a cold run, a warmup, and three warm measured runs. The observation seam
records the exact command plan, requested candidates, raw rows, returned evidence, statement count, elapsed
time, cache state, and a stable result digest. Every measured `ReadManyObserved` result is compared with the
ordinary public `ReadMany` result.

## Baseline owner

The lifecycle rotation removed the retained-history burden but left two single-ID reverse planner defects:

- Inbound fallback scanned `idx_read_identifiers_name_kind`, took a `2,475.0633 ms` warm median, read one raw
  row, and returned no fallback evidence. The 100-ID form sought the same index by `name` and took
  `951.5607 ms`.
- Inbound exact scanned the active resolution base despite both target indexes being present. Its single-ID
  warm median was `145.5446 ms`; the 100-ID form sought both target indexes and took `653.8616 ms`.

No producer index or statistics mutation was needed. Both defects were join-order choices in the Miller
reader. The measured repair forces the already-materialized target set to remain outside the identifier and
base joins with SQLite's existing `CROSS JOIN ... ON` pattern.

## Before and after

| Arm | IDs | Baseline median | Final median | Change |
|---|---:|---:|---:|---:|
| reverse inbound exact | 1 | 145.5446 ms | 1.3852 ms | -99.05% |
| reverse inbound exact | 100 | 653.8616 ms | 652.3907 ms | -0.22% |
| reverse inbound fallback | 1 | 2,475.0633 ms | 0.9536 ms | -99.96% |
| reverse inbound fallback | 100 | 951.5607 ms | 943.4084 ms | -0.86% |
| forward outgoing exact | 1 | 29.1686 ms | 29.1620 ms | unchanged |
| forward outgoing exact | 100 | 54.5287 ms | 54.5132 ms | unchanged |
| forward outgoing fallback | 1 | 180.7663 ms | 180.4082 ms | unchanged |
| forward outgoing fallback | 100 | 208.9609 ms | 209.8988 ms | unchanged |

The final reverse exact plan seeks both
`idx_read_resolution_identifiers_target(target_version_id,target_symbol_id)` and
`idx_read_resolution_pending_target(target_version_id,target_symbol_id)`. The final reverse fallback plan seeks
`idx_read_identifiers_name_kind(name)`. No reverse base scan remains. The slowest final arm is the realistic
100-ID reverse fallback at `943.4084 ms`.

## Correctness and verification

- Baseline and final have 40 unique cells each.
- Stable result digests, raw row counts, and returned evidence counts match for every corresponding cell.
- `ReferenceEvidenceReaderTests`: 28 passed.
- `StoreResolutionReaderTests`: 7 passed.
- `FamilyStoreReadSessionTests` plus `SqliteSymbolGraphIndexTests`: 59 passed.
- Final opt-in Task 7B Scale run: 1 passed in 56 seconds.
- Focused build: 0 warnings and 0 errors.
- Code review found no high- or medium-severity defect in the observation seam, harness, or two query-shape
  corrections.

Task 7B is complete. Task 8 owns the full Linux gate, native Windows parity, semantic soak, memory ceilings,
and end-to-end budget verdict.
