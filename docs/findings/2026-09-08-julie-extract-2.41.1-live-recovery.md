# julie-extract 2.41.1 live store recovery

The first resident dogfood after the 2.41.1 pin exposed three independent failures in the existing
seven-view Miller family. New readers returned `reader_admission_busy` while a full import held the
writer lease. That refusal was correct: reader admission must not certify a snapshot after a writer
has begun.

The first import committed request `8ecc2f3b62654921a764b628f589bda2`, manifest generation 4383
and 6,316 log rows before its caller failed. Julie released the writer lease, then reopened
`store.db` to build the success report. A successor writer entered during that post-commit window,
so Miller received `database is locked` for work that had already committed. Julie main commit
`0725ae23` now builds committed reports from the durable coordinator receipt. Its deterministic
successor-writer regression failed in 5.02 seconds before the fix and passed in 0.01 seconds after.

The serving database also had persistent B-tree corruption. `PRAGMA quick_check` first named root
page 85 (`idx_read_structural_facts_pattern_language_path`); a full check also found damaged
identifier indexes at root pages 62, 63 and 66. The time and cause of the first corrupt page are not
proved. The running Miller native library was SQLite 3.50.4, which is inside SQLite's documented
multi-process WAL-reset corruption range; julie-extract 2.41.1 used fixed SQLite 3.53.1. This is a
matching exposure, not proof that the WAL-reset defect created these pages.

Producer maintenance `inspect` and repair planning reported `failure_class=none` on the damaged
database. Julie `0725ae23` adds structural integrity checks with bounded diagnostics for inspect/repair, preserves
Busy and operational error classes, and directs corruption recovery to generation promotion.
Promote remains able to reconstruct readable logical rows into a validated generation. Its
destination already ran quick, full integrity and foreign-key checks before publication.

## Recovery

An in-place rebuild repaired the structural-fact index but the three identifier indexes could not
be reindexed or dropped: SQLite returned Error 11. The untouched pre-repair evidence remains in the
archived family under
`/home/murphy/.miller/stores/.corrupt-a271f2bd-7368-4da6-b5aa-24ffad69fb1f-20260908T1409Z`.
The registry backup is
`/home/murphy/.cache/miller-recovery/workspaces.db.before-a271f2bd-7368-4da6-b5aa-24ffad69fb1f-reset`.

The user chose a fresh source rebuild over the much slower logical history promotion. Four Miller
backends rooted at this checkout exited through SIGTERM. The corrected SQLite 3.53.4 build was
installed into the configured source output by a staged directory swap; its Linux x64 native SHA-256
is `eddcd4aa561d5b8f252db77e8272e7d1aed96bcab9fda3f177ca542f916290bf`.
The reset preserved workspace registry rows and every source checkout, removed only the corrupt
family bindings, archived the six extant `store.json` pointers, and rebuilt current main. Other
worktrees will join the new family and rebuild on demand.

Fresh family `abb68560-246c-4d0e-8753-c0e05d4a621c`, view
`11c0014e-23d9-4521-99f6-83df41a37e61`, published full-level `gen-001` in 170,382 ms. It contains
2,374 files, 238,949 symbols, 761,969 identifiers and 113,061 structural facts at revision 7,444.
Full `PRAGMA quick_check` returned `ok`; direct queries used both repaired identifier and structural-
fact indexes. Fixed-runtime status reported current search/content sidecars, a fresh index and an
empty queue. Vector convergence requires the normal resident-backend restart.

## Verification

- Producer affected contracts: 97 passed across import, operations and maintenance.
- Producer full workspace suite: every target passed; the main extractor unit target reported
  3,969 passes and seven expected ignores.
- Miller SQLite guard: 3.50.4 failed and 3.53.4 passed; Release build completed with zero warnings
  and errors. Miller main commit `464286e8` carries the dependency floor and guard.
- Miller fast suite: 10,424 passed, nine expected skips and zero failures.
- No release, push or Miller repin was performed. The bundled 2.41.1 producer remains the published
  artifact; the two producer corrections are local source until separately released.
