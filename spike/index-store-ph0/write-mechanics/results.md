# Ph0 write-side mechanics — results

Throwaway instrument for the versioned index store program
([`docs/plans/2026-08-06-index-store-views-program.md`](../../../docs/plans/2026-08-06-index-store-views-program.md)).
It answers three Ph0 questions: does GC reclaim physical bytes, what commit granularity
does the durability contract need, and does the promotion-capacity formula hold.

Run it with `./run.sh` (full scale) or `./run.sh --scale quick`. Every number below comes from
[`out/summary.md`](out/summary.md), which `summarize.py` renders from `out/*.json`. Raw evidence:
`out/gc.json`, `out/granularity.json`, `out/promotion.json`, `out/pragma-probes.json`,
`out/row-shapes.txt`, `out/run-log.txt`.

## Environment

- SQLite library 3.53.4 (Python 3.14.6), system `sqlite3` CLI 3.51.0, both with FTS5.
- Darwin 25.6.0 arm64 (Apple M2), APFS on NVMe.
- All databases: `page_size=4096`, `journal_mode=WAL`, `synchronous=NORMAL` unless stated.

## Row shapes

Synthetic rows are sized from the live Miller artifact, sampled read-only
(`sample_row_shapes.sh` → `out/row-shapes.txt`). The artifact holds 1,417 files, 122,707 symbols,
478,283 reference sites and 380,720 identifiers in 808.8 MB, giving the per-file averages the
generator uses: **87 symbols, 338 reference sites, 269 identifiers = 694 rows per file version**.
Column widths match the artifact's measured averages (identifier ids 32 chars, reference-site ids
47, paths 47–57, symbol signature 45, doc comment 49, metadata 55, and the 79.6% exact-span
fraction on reference sites).

The synthetic store lands at **476.3 bytes/row including indexes**. The artifact's three
equivalent tables plus their full index set come to 500,580,352 bytes over 981,710 rows =
**509.9 bytes/row**, so the synthetic store is **6.6% lighter per row**; the synthetic schema
carries 12 of the artifact's 17 indexes on those tables. Byte results below are therefore mildly
conservative, and ratios (percent reclaimed, peak/baseline) are unaffected.

---

## 1. GC — physical reclamation

**Verdict: PASS.** Version-cohort deletes plus staged `PRAGMA incremental_vacuum` shrink the file.
The same deletes reclaim nothing without `auto_vacuum=INCREMENTAL`.

Store: 6,100 file versions over 1,220 paths × 5 generations = 4,233,400 rows = **2,016.2 MB**.
Retention drops the 2 oldest generations of every path (2,440 versions, 40%).

| arm | auto_vacuum | built | after DELETE | freelist | after incremental_vacuum | reclaimed | full VACUUM |
|---|---|---|---|---|---|---|---|
| `inc_retention` | INCREMENTAL | 2,016.2 MB | 2,016.2 MB | 156,755 pages | **1,373.3 MB** | **31.9%** | 1,152.9 MB |
| `inc_epoch` | INCREMENTAL | 2,016.2 MB | 2,016.2 MB | 164,586 pages | **1,341.2 MB** | **33.5%** | 1,153.3 MB |
| `none_retention` | NONE | 2,013.7 MB | 2,013.7 MB | 156,755 pages | **2,013.7 MB** | **0.0%** | 1,151.5 MB |

- **Negative control confirmed, and it fails silently.** On the `NONE` store `PRAGMA
  incremental_vacuum(10000000)` raised no error, took 0.0 s, left the freelist at 156,755 pages
  and the file byte-identical. Nothing in the API tells a caller the store cannot reclaim.
- **The staged vacuum is genuinely bounded.** 79 stages of 2,000 pages, 4.90 s total, **max stage
  0.104 s**, mean 0.062 s, freelist drained to 0. Stage cost is set by the page budget, not by the
  store size, so a background GC can hold any latency bound it likes.
- **Delete pattern barely matters at version granularity.** The realistic scattered retention
  sweep reclaimed 31.9% and the best-case contiguous epoch sweep 33.5%. One version is ~330 KB of
  rows (~80 pages), so its rows free whole pages either way. (The pathological case is real but
  finer-grained: `out/pragma-probes.json` shows deleting every second ROW of a table frees **0**
  freelist pages, versus 12,530 for a contiguous range.)
- **Incremental vacuum leaves 220.4 MB that only a full VACUUM reclaims** — 1,373.3 MB versus
  1,152.9 MB, i.e. 16.0% of the incrementally vacuumed file is fragmentation it cannot reach. A
  full VACUUM took 5.82 s but needs ~2× the file in transient space.

### Where the residue lives — a schema rule falls out

Per-btree bytes before and after the staged vacuum (`out/gc.json`, `arms[0].build.btree_bytes`
versus `arms[0].incremental_vacuum.btree_bytes`), with 40% of versions deleted:

| btree | leading column | built | after vacuum | reclaimed |
|---|---|---|---|---|
| `identifiers` (PK `version_id, identifier_id`) | version_id | 477.4 MB | 291.3 MB | **39.0%** |
| `reference_sites` (PK composite) | version_id | 440.4 MB | 268.5 MB | **39.0%** |
| `symbols` (PK composite) | version_id | 217.9 MB | 133.2 MB | **38.9%** |
| `idx_identifiers_file (version_id, file_id)` | version_id | 92.1 MB | 58.0 MB | **37.0%** |
| `idx_reference_sites_file (version_id, file_id)` | version_id | 115.7 MB | 73.7 MB | **36.3%** |
| `idx_identifiers_reference_site` | reference_site_id | 103.3 MB | 98.6 MB | **4.5%** |
| `idx_identifiers_containing` | containing_symbol_id | 76.5 MB | 71.7 MB | **6.4%** |
| `idx_identifiers_target` | target_symbol_id | 41.0 MB | 38.3 MB | **6.7%** |
| `idx_identifiers_name_kind` | name | 51.2 MB | 49.7 MB | **2.9%** |
| `idx_reference_sites_containing` | containing_symbol_id | 96.4 MB | 89.5 MB | **7.1%** |

**Every secondary index that does not lead with `version_id` strands its pages.** Version-leading
btrees give back 36–39% for a 40% cohort delete; the rest give back 2.9–7.1%, because a version's
rows are scattered across the whole index and leave partly-filled pages that never reach the
freelist. This is the entire 220 MB gap to full VACUUM. **Contract input for Ph1: on a
version-keyed table, a secondary index must lead with `version_id`, or the index must be accepted
as unreclaimable-until-rebuild and its cost counted in the growth model.**

### FTS5 sidecar

83.1 MB, 2,033 versions, 176,871 documents, 23 segments (automerge disabled during the load).
Retention deletes 70,818 documents.

| step | file | `symbols_fts` segids | freelist |
|---|---|---|---|
| built | 83.1 MB | 23 | — |
| after DELETE of 70,818 docs (58.09 s) | 83.1 MB | 23 | 3,873 |
| after 58 page-limited `merge` rounds (0.251 s) | 83.1 MB | **2** | 6,529 |
| after `incremental_vacuum` (0.079 s, 4 stages) | **56.3 MB** | — | 0 |

- **The chain is delete → merge → incremental_vacuum, and only the last step moves the file.**
  Neither the delete nor the merge changed a single byte of file size; the merge's job is to
  collapse segments and release pages to the freelist, and only `incremental_vacuum` truncates.
  Reporting reclaimed bytes after a merge would report zero.
- **`merge` is bounded as advertised.** 58 rounds at 64 pages, 56 of which did work; total
  0.251 s, **max round 14.8 ms**, mean 4.3 ms. The `total_changes` signal works: +2 when a round
  did work, +1 when it did not (`out/pragma-probes.json`).
- **`optimize` is one uninterruptible call and buys almost nothing here.** On an identical clone:
  0.215 s in a single call (14.5× the longest bounded round), reaching 1 segid and 56.2 MB — just
  0.15 MB (0.3%) better than the bounded path. Bounded merges are sufficient; `optimize` stays a
  maintenance-window option, as the plan says.
- **Bounded merges do not fully compact.** They converge to 2–3 segments and then report no work,
  because FTS5 only merges a level once it holds `usermerge` (default 4) segments. The residual
  tail is cheap (0.3% of bytes), but a contract that promises "one segment" needs `optimize`.
- **The delete is the expensive step**, not the merge: 58.09 s for 70,818 documents (~1,220
  docs/s) versus 0.25 s of merging. GC budgeting should be sized on the delete.

### secure-delete — both switches are required

A unique sentinel string is written into one document, the document is deleted, then the raw file
bytes are scanned for the sentinel (`out/gc.json`, `secure_delete_probes`):

| FTS5 `secure-delete` | core `secure_delete` | before | after DELETE | after merge | after vacuum |
|---|---|---|---|---|---|
| off | off | 3 | **4** | 4 | 4 |
| **on** | off | 3 | **2** | 2 | 2 |
| off | **on** | 2 | **2** | 2 | 2 |
| **on** | **on** | 2 | **0** | 0 | 0 |

- With neither enabled, deleting the document **increased** the number of copies from 3 to 4 — the
  delete writes a tombstone segment that also contains the term.
- Each switch alone leaves residue: the FTS5 option scrubs the index but not the `_content` shadow
  row, and core `secure_delete` scrubs ordinary pages but not the FTS5 index. **Only both together
  reach 0.** This confirms cycle-2 finding 8 and shows the two options are not substitutes.

### SQLite facts verified (`out/pragma-probes.json`)

| claim | verdict |
|---|---|
| `auto_vacuum=INCREMENTAL` must be set before the first table is created | **CONFIRMED — and setting it later raises NO error, silently stays 0.** Only a full VACUUM converts (then it persists as 2). A migration that forgets the create-time pragma looks healthy and reclaims nothing forever. |
| `PRAGMA incremental_vacuum(N)` frees up to N pages | **CONFIRMED only if the statement is stepped to completion.** In Python, a bare `Connection.execute` freed **1** page; adding `.fetchall()` freed the requested 2,000. A staged GC written the naive way would run ~2,000× more statements than intended. |
| page-limited `merge` is bounded, `optimize` is not | **CONFIRMED** (see above). |
| core `secure_delete` and FTS5 `secure-delete` differ | **CONFIRMED.** Core `secure_delete` is **per-connection and NOT stored in the file** — it read back 0 on a fresh connection after being set to 1, so **every writer connection must re-assert it**. The FTS5 option IS persisted in the `%_config` shadow table. Writing the first secure delete raises the stored FTS5 structure version from 4 to 5; the system `sqlite3` 3.51.0 CLI still reads the file. |

---

## 2. Transaction granularity

**Verdict: single-transaction import cannot leave reusable work behind a crash; per-commit-unit
modes can, and the completion marker is what makes the reuse safe.**

2,000 file versions per import = 1,388,000 rows = 660.5 MB. Three SIGKILL trials per mode at
randomized points (seed 20260806, fractions in [0.20, 0.80] of the measured clean-run duration).
Only the harness's own child pid is signalled. After each kill the store is verified, the same
import is resumed, and the store is verified again.

| mode | commits | rows/s | clean s | WAL peak | reusable after SIGKILL (min/mean/max) | truncated after resume |
|---|---|---|---|---|---|---|
| `single` | 1 | **93,360** | 14.87 | **664.6 MB** | **0 / 0 / 0** | 0 |
| `per_chunk` (100) | 21 | 70,121 | 19.79 | 142.0 MB | 1400 / 1500 / 1600 | 0 |
| `per_version` | 2,000 | 17,589 | 78.91 | **8.6 MB** | 1221 / 1414 / 1612 | 0 |
| `per_version_nomarker` | 4,000 | 18,524 | 74.93 | 8.4 MB | 1130 / 1372 / 1526 | **1** |
| `per_version` + WAL headroom (autockpt 8,000) | 2,000 | 29,694 | 46.74 | 38.5 MB | 656 / 933 / 1187 | 0 |
| `per_version` + `synchronous=FULL` | 2,000 | 19,855 | 69.91 | 8.6 MB | 918 / 1144 / 1498 | 0 |

`PRAGMA quick_check` returned `ok` after every one of the 18 kills. No orphan child rows appeared
in any trial: SQLite's WAL recovery rolled back the interrupted transaction cleanly every time.

### The three findings

1. **`single` loses everything, every time.** All three trials left 0 versions marked complete and
   the resume re-imported all 2,000. Its WAL peaked at **664.6 MB — 100.6% of the finished
   database**, because no checkpoint can run while the transaction is open. This is julie's
   current snapshot-writer shape, and it is exactly what cycle-2 finding 7 predicted.

2. **Per-commit-unit modes reuse everything they committed.** In every trial of `per_chunk`,
   `per_version`, WAL-headroom and `synchronous=FULL`, the versions marked complete, the versions
   that verified intact, and the versions the resume skipped were the **same number**, and
   `skipped + imported = 2,000` exactly. `per_chunk` quantises the loss to the chunk: a crash
   discards up to `chunk − 1` = 99 versions of in-flight work.

3. **Without the completion marker, a crash can permanently publish an incomplete version.**
   `per_version_nomarker` commits the version row and its child rows in two transactions.
   Trial 3 was killed in that window: 1,131 versions were dedup-visible, but only **1,130** had
   their full child rows. The resume trusted the version row, skipped it, and the truncated
   version **survived into the final store** (`final_truncated = 1`, `quick_check = ok`). It hit
   once in three trials — the window is one inter-transaction gap per version — and nothing in the
   database flags it. This reproduces doubt-pass finding 7 as an observed defect, not a
   hypothesis. **The marker must be set in the same transaction as the last child row, and dedup
   must read only `complete = 1`.**

### Cost of durability

- Per-version commit costs **5.3× throughput** against single (17,589 vs 93,360 rows/s), and
  **1.7× of that is recoverable** by raising `wal_autocheckpoint` from 1,000 to 8,000 pages
  (29,694 rows/s) at the price of a 38.5 MB WAL instead of 8.6 MB.
- `synchronous=FULL` is **not measurably more expensive** than `NORMAL` here — 19,855 vs 17,589
  rows/s, with FULL nominally faster, i.e. inside run-to-run noise. Machine-crash durability is
  effectively free at this shape, and it is worth taking: these SIGKILL trials prove safety
  against **process** death only (an OS that survives keeps committed WAL frames), not against
  power loss.
- Per-chunk at 100 versions keeps **75% of single's throughput** with a WAL bounded at 21% of the
  artifact, and reuse quantised to 100 versions.

**Recommendation for the Ph1 durability contract:** per-chunk commit with the completion marker
written inside the same transaction, chunk size chosen from a WAL budget (measured: WAL peak
scales with the chunk — 8.6 MB at 1 version, 142.0 MB at 100), `synchronous=FULL`, and dedup that
reads only complete versions. Per-version commit stays available where a file's extraction cost
dominates its insert cost.

---

## 3. Promotion capacity

**Verdict: the formula holds to within 0.03% when its terms genuinely coexist — but it is a
maximum over phases, not a sum over the whole operation.**

2,500 file versions per generation (825.8 MB store + 100.4 MB sidecar = 926.2 MB per generation).
A sampler walks the family directory every 50 ms and records the peak plus the file-by-file
breakdown at that peak.

| arm | old gen | new gen | sidecars | WAL/temp | reader-retained | formula | measured | delta |
|---|---|---|---|---|---|---|---|---|
| `no_reader` | 825.8 | 825.8 | 200.8 | 117.0 | 0 | 1,969.4 MB | **1,968.8 MB** | **−0.03%** |
| `pinned_reader` | 825.8 | 825.8 | 200.8 | 117.0 | 926.2 | 2,895.6 MB | **2,895.0 MB** | **−0.02%** |
| `retention_first` | 562.5 | 495.2 | 162.0 | 466.0 | 0 | 1,685.7 MB | **1,393.2 MB** | **−17.35%** |

The file breakdown at each peak is recorded in `out/promotion.json` (`peak_file_breakdown`) and
accounts for the measured number exactly — for `pinned_reader`, three 825.8 MB stores, three
sidecars, and 117.0 MB of rebuild WAL.

- **Terms confirmed.** `no_reader` and `pinned_reader` land within 0.03% of the prediction, and
  the `reader_retained` term is isolated by the comparison: a generation a reader still holds
  adds its full 926.2 MB. Promotion peaked at **2.13× the family baseline** with no pinned reader
  and **1.56×** with one (on a larger baseline).

- **Correction: the formula is a max over phases.** `retention_first` over-predicts by 17.35%
  because its peak (1,393.2 MB) occurred at t=3.59 s **during the retention sweep** — store
  825.8 + WAL 466.0 + sidecar 100.4 — not during the rebuild at all. The sweep's WAL is
  checkpointed away before the rebuild starts, so the two WAL peaks never coexist and must not be
  added. A preflight that sums every term over the whole operation reserves more than it needs.

- **The retention sweep's own WAL is a term the plan does not name.** Deleting 40% of versions
  from an 825.8 MB store produced a **466.0 MB WAL — 56% of the store size** — before any rebuild
  began. Any preflight that only models the rebuild will under-reserve for a purge or a
  retention-then-rebuild sequence.

- **Retention-first pays.** Sweeping before the rebuild cut the peak from 1,968.8 MB to
  1,393.2 MB (**−29%**) and left the family at 556.8 MB instead of 926.2 MB, because the rebuild
  carried only the survivors. The plan's "retention cleanup runs before capacity is judged" is
  measured, not asserted.

**Corrected formula for Ph1:**

```
required = max over phases of (
    live generations still addressable      # incl. any pinned by readers
  + the generation being written
  + all sidecars of both
  + WAL/temp live in THAT phase             # a retention sweep's WAL can be ~56% of the store
)
```

---

## 4. Projection to dotnet/runtime scale

The 4 GB cap keeps every arm below dotnet/runtime scale, so these are stated multiplications, not
measurements. The multiplier is taken from identifier counts: the plan's benchmark is 12.86M
identifiers per worktree in a 21.9 GB artifact; the live Miller artifact holds 380,720 identifiers
in 808.8 MB (identifier ratio 33.8×, byte ratio 27.1×).

| measurement | measured at | multiplier | projected |
|---|---|---|---|
| staged incremental_vacuum of a whole freelist | 4.90 s reclaiming 642.9 MB (1.64M identifiers) | 7.8× | ~38 s reclaiming ~5.0 GB |
| one staged vacuum step (2,000 pages) | max 0.104 s | 1× (page-bounded) | max 0.104 s — independent of store size |
| full VACUUM instead | 5.82 s, ~2× the file transient | 7.8× | ~46 s, **~21.5 GB transient** |
| cold import, `single` | 14.87 s, WAL 664.6 MB (538k identifiers) | 23.9× | ~5.9 min, **WAL ~15.9 GB** |
| cold import, `per_chunk` (100) | 19.79 s, WAL 142.0 MB | 23.9× | ~7.9 min, WAL stays 142.0 MB |
| cold import, `per_version` | 78.91 s, WAL 8.6 MB | 23.9× | ~31.4 min, WAL stays 8.6 MB |
| cold import, `per_version` + WAL headroom | 46.74 s, WAL 38.5 MB | 23.9× | ~18.6 min, WAL stays 38.5 MB |
| promotion peak, `no_reader` | 1,968.8 MB (672k identifiers) | 19.1× | ~37.6 GB |
| promotion peak, `pinned_reader` | 2,895.0 MB | 19.1× | ~55.3 GB |
| promotion peak, `retention_first` | 1,393.2 MB | 19.1× | ~26.6 GB |

The WAL projection is the load-bearing one. **`single` needs a WAL as large as the whole artifact
— ~15.9 GB at dotnet/runtime scale — while every per-commit-unit mode's WAL is bounded by the
commit unit and does not grow with the import at all.** On a 512 GB machine that is the difference
between a routine import and a capacity event.

---

## What this decides

| question | answer |
|---|---|
| Does GC reclaim physical bytes? | **Yes**, with `auto_vacuum=INCREMENTAL` set at creation: 31.9% of a 2 GB store for a 40% retention sweep, in bounded 0.104 s steps. Without it, 0% — silently. |
| Is the dashboard's "reclaims measured bytes" claim safe? | **Yes, if it measures after `incremental_vacuum`**, never after a DELETE or an FTS5 merge — both leave the file byte-identical. |
| Are secure-delete guarantees real? | **Yes, only with both** core `secure_delete` (re-asserted per connection) and the FTS5 `secure-delete` option (persistent). Either alone leaves recoverable copies; neither leaves a copy *more*. |
| What commit granularity does the contract need? | **Per-chunk (or per-version) with the completion marker in the same transaction as the last child row.** Single-transaction import provably leaves zero reusable work and a whole-artifact WAL. |
| Is the completion marker load-bearing? | **Yes — observed, not theorised.** Without it a crash published a truncated version that survived the resume and no integrity check flagged it. |
| Does the promotion-capacity formula hold? | **Yes to within 0.03%**, with two corrections: it is a max over phases, not a sum; and the retention sweep's WAL (56% of the store) is a phase the plan does not currently name. |

## Known limits of this instrument

- Row shapes are synthetic. They match the artifact's averages but not its distribution tails
  (long doc comments, generated files with thousands of symbols), and the synthetic schema carries
  12 of the artifact's 17 indexes on the three modelled tables.
- The SIGKILL trials prove crash safety against **process** death, not power loss. That is the
  realistic Miller failure (the OOM killer, exit 137), and `synchronous=FULL` measured free enough
  to cover the rest.
- Throughput is measured on the DB-insert path only. Real imports also pay extraction, which
  dominates per file and changes the economics of losing in-flight work.
- Single machine, APFS on NVMe, one run per configuration (3 trials per crash mode). Timings are
  indicative; the byte measurements are exact and repeatable.
- An earlier promotion run reported a 24% over-peak for `pinned_reader`. It was an instrument
  bug: `os.walk` counted one 825.8 MB file under both its old and new path while a rename was in
  flight. The sampler now dedupes by `(st_dev, st_ino)`, and the committed run records the
  file-by-file breakdown at the peak so the number is self-checking.

## Verification ledger

| item | value |
|---|---|
| commands | `./run.sh --scale full` per experiment + final `--scale quick` end-to-end (exit 0, work dir empty and removed); 18 SIGKILL trials (6 modes x 3), `quick_check` ok + 0 orphan rows after every kill |
| worktree | index-store-ph0 @ 982dcfd7 at report (lead commit 7b367a13) |
| invariants | GC reclamation with negative control; secure-delete sentinel matrix; granularity table with crash-reuse counts; promotion peaks vs formula (-0.03%/-0.02% where terms coexist) |
| result | complete; one instrument bug (peak-sampler double-count) found, fixed, disclosed; superseded run labelled in out/run-log.txt |
| timestamp | 2026-08-07T03:34Z (lead-recorded from worker report) |
