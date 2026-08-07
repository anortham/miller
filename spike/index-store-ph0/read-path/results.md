# Ph0 Task 3 — Read path + physical bytes (versioned index store)

Instrument for the [Ph0 prototype gate](../../../docs/plans/2026-08-06-index-store-ph0-plan.md)
of the [versioned index store program](../../../docs/plans/2026-08-06-index-store-views-program.md)
(§1 store/schema shape, §3 read path). Throwaway prototype code (razorback:prototyping); one entry
script, `./run.sh`, rebuilds every number in about two minutes and deletes the databases it made.

## Verdicts

| # | Question | Answer |
|---|---|---|
| 1 | **8-view family store vs ≤1.2× a single index** (hard gate) | **1.027× → PASS** (510.8 MiB vs 497.3 MiB) at the sampled task-branch divergence. 8 dedicated copies cost **8.000×** (3,978 MiB). |
| 2 | Composite `(version_id, local_id)` key amplification | **+4.4%** if `file_id` is kept beside `version_id`; **−11.3%** in the real v4 shape, where `version_id` *replaces* the 37-char `file_id` on every child row. Composite identity is byte-positive, not a tax. |
| 3 | Cheapest visibility shape | **Query-dependent, and both are cheap at v1 scale.** Path-keyed reads: manifest seek (−0.5%). Name-keyed and reference reads: per-connection temp visibility table (+2.4% / +0.1%). The manifest join costs +17.8% on name lookup; the temp table costs +23.2% on path-keyed reads once history is retained. |
| 4 | Do the visibility shapes change results? | **No.** 900 sampled keys per view (2,700 query executions), 82,290 (view 1) / 82,123 (view 8) rows compared, **0 mismatches** against a dedicated per-view copy. |
| 5 | Where the model breaks | **Retention, not view count.** Seven siblings all diverged at the p90 of real history cost 1.252× (over target); two retained history generations cost 2.563×. |

Contract input confirmed: the resolution layer is **11.5%** of store bytes (base) — the program
doc's "≈12%" is right — and the seven per-view deltas together add **1.9%** (0.27% per view).

## 1. What was built

`run.sh` transforms the real Miller artifact (opened only as
`file:/Users/murphy/source/miller/.miller/symbols.db?mode=ro`) into four prototype databases:

| database | schema | content |
|---|---|---|
| `single.db` | today's single-key schema | one view's data — the baseline and the gate denominator |
| `keepfile.db` | composite PK, `file_id` retained | isolates the pure cost of composite identity |
| `v4single.db` | v4 composite schema, no views | isolates the v4 row shape from visibility |
| `store.db` | v4 composite schema + views | 1,417 shared base versions, 8 view manifests, one resolution base, 7 view deltas |

Tables modeled: `files`/`file_versions`, `symbols`, `identifiers`, `reference_sites`, and the
resolution layer (`identifier_resolutions` → `resolution_base_entries` + `resolution_deltas`),
with every secondary index the live artifact carries on them. That is **70.2% of the live
artifact's 771.3 MiB** (`dbstat` arithmetic, computed in this session). The unmodeled remainder is
`source_regions` (34.9 MiB), `pending_relationships` (29.2 MiB), `structural_facts` (21.9 MiB) and
smaller per-file tables; the per-file-pure ones dedup exactly like the modeled ones, so the ratio
should hold. `pending_relationships` is derived state and belongs to Task 1's purity audit.
Sidecars (`search.db`, `content.db`, `vectors.db`) are out of scope — Task 4 owns `search.db`.

Both sides of every comparison drop `identifiers.target_symbol_id` (the resolution write-back the
v4 surgery removes), so the measurement isolates versioning rather than that separate change.

### Divergence distribution and why

`lib/divergence.sh` samples the last 25 merge commits of this repo: for each merge M it takes
`git diff --name-only $(git merge-base M^1 M^2) M^2` — the files a task branch changed — and counts
those with an indexed extension against the artifact's 1,417 indexed files
(`out/divergence.tsv`):

```
n=25  min 0.28%  p25 0.56%  median 1.20%  mean 2.24%  p75 3.32%  p90 6.07%  max 8.26%
```

That confirms the plan's stated 0.5–5% band from real history rather than assumption. The seven
sibling worktrees are drawn at even quantiles of that sample — 0.423, 0.565, 0.847, 1.200, 3.035,
3.317, 6.069 % — so the family is a realistic mix of small and large task branches, not a uniform
best case. View 1 is `main` at zero divergence. A changed file becomes a new `file_versions` row
whose `symbols`/`identifiers`/`reference_sites` rows are re-extracted (modeled as the same row
shapes under a new `version_id`); the manifest points that path at the new version.

Resolution deltas are built from real fan-out, not a percentage: for each view the delta holds
(a) every resolution row of the changed files' own identifiers and (b) every resolution row from
an *unchanged* file that targets a symbol in a changed file. Across the 8-view store that is
62,764 delta rows against a 380,720-row base (16.5%) for 15.5 percentage-points of total
divergence.

### How the reads were timed

Wall-clock on this machine is noisy (five sibling Ph0 agents were running), so the harness
defends the numbers three ways: every shape gets the same 200 MB private page cache
(`PRAGMA cache_size=-200000`) after a full warm-up sweep, the 15 measured passes are **interleaved
and rotated** across shapes so a load burst hits all four equally, and every sweep is also counted
deterministically in **VDBE instructions** (`sqlite3_progress_handler`) so engine work is visible
independent of the clock. Reported time is the median sweep of 300 keys; the harness floor
(`SELECT 1`) is 0.7–0.8 µs/query, under 1% of every measured query.

## 2. Composite-key amplification, with the DDL diff

```diff
 CREATE TABLE symbols (
-  symbol_id TEXT PRIMARY KEY,            -- rowid table + sqlite_autoindex on a 32-char TEXT id
-  file_id TEXT NOT NULL,                 -- 37-char TEXT on every row
+  version_id INTEGER NOT NULL,           -- replaces file_id; integer, 1-3 bytes
   path TEXT NOT NULL,
   ...
+  symbol_id TEXT NOT NULL,
+  PRIMARY KEY (version_id, symbol_id)    -- composite identity: same id may exist per version
 );
-CREATE INDEX idx_symbols_file ON symbols(file_id);
+CREATE INDEX idx_symbols_version ON symbols(version_id);
-CREATE INDEX idx_symbols_path      ON symbols(path);
+CREATE INDEX idx_symbols_path      ON symbols(path, version_id);
-CREATE INDEX idx_symbols_name_kind ON symbols(name, kind);
+CREATE INDEX idx_symbols_name_kind ON symbols(name, kind, version_id);
-CREATE INDEX idx_symbols_parent    ON symbols(parent_symbol_id);
+CREATE INDEX idx_symbols_parent    ON symbols(parent_symbol_id, version_id);

 -- identifiers and reference_sites take the same surgery:
 --   PRIMARY KEY (version_id, identifier_id) / (version_id, reference_site_id)
 --   every secondary index gains version_id as its last column, so visibility filters
 --   inside the index instead of after a table fetch
+CREATE TABLE file_versions (            -- replaces files
+  version_id INTEGER PRIMARY KEY,
+  path TEXT NOT NULL, content_hash TEXT NOT NULL,
+  extractor_fingerprint TEXT NOT NULL, complete_level INTEGER NOT NULL,
+  UNIQUE (path, content_hash, extractor_fingerprint)
+);
-CREATE TABLE identifier_resolutions (identifier_id TEXT PRIMARY KEY, target_symbol_id TEXT, ...);
+CREATE TABLE resolution_base_entries (   -- resolution leaves the shared rows entirely
+  base_id INTEGER NOT NULL, version_id INTEGER NOT NULL, identifier_id TEXT NOT NULL,
+  target_version_id INTEGER, target_symbol_id TEXT, ...,
+  PRIMARY KEY (base_id, version_id, identifier_id));
+CREATE TABLE resolution_deltas (         -- the only per-view storage
+  view_id INTEGER NOT NULL, version_id INTEGER NOT NULL, identifier_id TEXT NOT NULL,
+  target_version_id INTEGER, target_symbol_id TEXT, tombstone INTEGER NOT NULL DEFAULT 0, ...,
+  PRIMARY KEY (view_id, version_id, identifier_id));
```

Full DDL: `lib/instrument.py` (`SINGLE_SCHEMA`, `KEEPFILE_SCHEMA`, `V4_SCHEMA`, `VIEW_SCHEMA`).
Measured on identical data, one view's worth of rows (`out/bytes-*.json`, `dbstat` per object):

| object group | today single-key | composite + `file_id` kept | v4 (`version_id` replaces `file_id`) |
|---|---:|---:|---:|
| symbols (table + indexes) | 55.6 MiB | 56.5 MiB (+1.6%) | 51.8 MiB (−7.0%) |
| identifiers | 115.8 MiB | 118.3 MiB (+2.2%) | 103.4 MiB (−10.7%) |
| reference_sites | 128.9 MiB | 132.1 MiB (+2.5%) | 113.8 MiB (−11.7%) |
| resolution rows | 44.1 MiB | 48.2 MiB (+9.4%) | 48.2 MiB (+9.4%) |
| files / file_versions | 0.4 MiB | 0.5 MiB (+29.9%) | 0.5 MiB (+29.9%) |
| **total physical** | **497.3 MiB** | **519.4 MiB (+4.4%)** | **440.9 MiB (−11.3%)** |

Reading: composite keys cost **+4.4%** (wider primary-key autoindexes plus `version_id` appended
to four secondary indexes per table). The v4 shape pays that and still lands **11.3% smaller**,
because a 37-char `file_id` TEXT column on 981,710 child rows is worth more than the composite key
costs. The resolution table is the one group that grows in both shapes (+9.4%): it gains `base_id`
and `target_version_id`, which is the price of sharing one base across views.

## 3. Physical bytes: 8 views vs the ≤1.2× target

| configuration | physical bytes | × single index |
|---|---:|---:|
| single index today (single-key schema) | 521,433,088 B (497.3 MiB) | 1.000× |
| **8-view store, sampled divergence** | **535,650,304 B (510.8 MiB)** | **1.027×** |
| one dedicated copy of diverged view 8 | 521,441,280 B (497.3 MiB) | 1.000× |
| 8 dedicated copies (view 1 + 7 × view 8, both measured) | 3,978 MiB | 8.000× |
| 8-view store, every view at p90 divergence | 652,767,232 B (622.5 MiB) | 1.252× |
| 8-view store + 2 retained history generations | 1,336,573,952 B (1,274.7 MiB) | 2.563× |

**GATE: 1.027× ≤ 1.2× → PASS.** The store holds 1,640 file versions (1,417 shared + 223
divergent), 137,863 symbols, 440,830 identifiers, 554,295 reference sites, one 380,720-row
resolution base, 62,764 delta rows and 11,336 manifest entries — i.e. 12–16% more rows than a
single index, for eight checkouts. The dedicated denominator is physical, not assumed: view 8 was
materialized back into today's schema and measured at 521,441,280 B, 8 KB (0.002%) from
`single.db`.

Store composition (`out/bytes-store.json`): resolution base **11.5%**, per-view resolution deltas
**1.9%** total, view manifests **0.4%**, shared version rows the rest. This is the arithmetic the
program doc's §"Resolution sharing is v1-required" depends on: a private resolution copy per view
would put the same family at `0.885 + 8 × 0.115 ≈ 1.80×` — a fail — while shared base + deltas
lands at 1.027×.

Two boundaries the gate does not clear, both worth carrying into Ph1:

- **Divergence headroom.** Seven siblings *all* diverged at the p90 of real history (6.21–6.28%
  actual each, 43.6 points summed) cost **1.252×** — just over target. The gate holds for a
  realistic mix and fails for a family of seven simultaneously large branches. The two measured
  points (15.74 points → 1.027×; 43.61 points → 1.252×) fit
  `ratio ≈ 0.900 + 0.0081 × (summed divergence points)`, and the fitted intercept matches the
  measured zero-divergence store (v4 rows + manifests = 442.8 MiB = 0.890×) — the v4 shape's own
  −11% saving is what buys the headroom. The 1.2× budget is therefore ≈37 summed points: seven
  siblings averaging ~5.3% each, or five of seven simultaneously at the observed p90.
- **Retention dominates everything.** Two retained history generations (every path keeping two
  extra versions no view references) cost **2.563×**. Retention window and GC are the byte lever,
  not view count — exactly the risk §5 of the program doc flags, and the reason
  `auto_vacuum=INCREMENTAL` at creation has to be a Ph1 contract item.

## 4. Result-set equivalence

Both visibility shapes were compared row-for-row against the dedicated copy on 300 keys per query
class per view (`out/verify-view1.json`, `out/verify-view8.json`):

| view | name_lookup | file_symbols | refs_by_symbol | mismatches |
|---|---:|---:|---:|---:|
| 1 (base manifest) | 43,520 rows | 28,685 rows | 10,085 rows | **0** |
| 8 (6.28% diverged) | 43,520 rows | 28,685 rows | 9,918 rows | **0** |

View 8 matters: its manifest hides 89 base versions and exposes 89 divergent ones, and the store
still returns exactly one version per path. This is the read-path half of the equivalence bar;
the FTS/ranking half belongs to Task 4.

## 5. Read overhead per query class

Three shapes over the same store, against the dedicated per-view copy as baseline. `v4_novis` is
the same v4 rows with **no** visibility predicate — the control that separates "v4 row shape" from
"visibility". Median of 15 interleaved passes over 300 keys; VDBE steps are the deterministic
work counter.

### View 1 (`out/reads-view1.json`)

| query class | shape | µs/query | vs dedicated | vs v4-no-visibility | VDBE steps/sweep |
|---|---|---:|---:|---:|---:|
| name_lookup | dedicated | 161.7 | — | — | 351,200 |
| name_lookup | v4_novis | 162.5 | +0.5% | — | 351,200 |
| name_lookup | **manifest_join** | 190.5 | **+17.8%** | +17.2% | 689,100 |
| name_lookup | **temp_vis** | 165.6 | **+2.4%** | +1.9% | 459,500 |
| file_symbols | dedicated | 83.0 | — | — | 462,900 |
| file_symbols | v4_novis | 84.1 | +1.4% | — | 462,900 |
| file_symbols | **manifest_join** | 82.6 | **−0.5%** | −1.8% | 465,300 |
| file_symbols | **temp_vis** | 84.2 | **+1.5%** | +0.1% | 527,900 |
| refs_by_symbol | dedicated | 107.3 | — | — | 135,000 |
| refs_by_symbol | v4_novis | 99.0 | −7.8% | — | 237,100 |
| refs_by_symbol | **manifest_join** | 113.9 | **+6.1%** | +15.1% | 463,700 |
| refs_by_symbol | **temp_vis** | 107.5 | **+0.1%** | +8.6% | 414,200 |

View 8 (`out/reads-view8.json`) tracks it: name_lookup +16.6% / +2.0%, file_symbols −1.4% / +0.2%,
refs_by_symbol +12.1% / +4.7% (manifest_join / temp_vis). Divergence does not move read cost.

### The same store with retained history (3.15× stored versions, `out/reads-inflated.json`)

| query class | manifest_join | temp_vis |
|---|---:|---:|
| name_lookup | **+43.1%** | **+17.7%** |
| file_symbols | −0.9% | **+23.2%** |
| refs_by_symbol | +4.4% | −0.4% |

**Visibility cost scales with the retained-version multiple, not with the number of views.** At
1× (v1: one version per path per family plus divergence) both shapes are within a few percent of a
dedicated index. At 3.15× the shapes separate sharply and in opposite directions.

### Recommendation for Ph3

- **Route by key, not by dogma.** A path-keyed read should enter through the manifest
  (`view_manifest(view_id, path)` is a primary-key seek that *hands back* the version; nothing to
  filter) — it is the only shape that stays flat as history grows (−0.9% at 3.15×). A name-keyed
  or candidate-set read should filter through a per-connection temp visibility table: rowid probes
  beat index probes per candidate row (+2.4% vs +17.8% at 1×, +17.7% vs +43.1% at 3.15×).
- **The temp table is cheap to build**: 0.23 ms for 1,417 versions, once per connection
  (`vis_build_ms` in the reads JSON). Miller's readers open per query today; the view-aware read
  session from §3 of the program doc should build it once per session and keep it, or the build
  cost (~0.2 ms) would swamp a 0.16 ms query.
- **The delta-precedence `NOT EXISTS` subquery is the reference path's real cost** (+8.6% to
  +15.1% over no-visibility, and 1.7–2.0× the VDBE work of the baseline join). If that shows up as
  a regression at dotnet/runtime scale, the fix is a materialized per-view *effective* resolution
  index rather than a cleverer join.
- Query plans for every shape are captured in `out/query-plans.json`; all twelve are index seeks —
  no shape degrades into a scan.

## 6. Threats to validity

- **Divergent versions are modeled as re-extracted copies of the original file's rows.** A real
  edit changes row *contents* and shifts counts a few percent; it does not change how many rows a
  file has by an order of magnitude. The byte model is therefore right in shape and slightly
  conservative in the store's favour only to the extent that an edited file gains rows.
- **70.2% of artifact bytes are modeled** (§1). The unmodeled per-file tables should dedup
  identically; `pending_relationships` (29.2 MiB) is derived state whose classification is Task 1's.
- **Timing ran on a loaded machine** (five sibling agents). Mitigated by interleaving, warm private
  caches and the VDBE counter; the read-overhead percentages are report-only, not the hard gate.
  Overhead is stable across two independent full runs (name_lookup manifest_join +15.6% then
  +17.8%).
- **Python driver overhead is present in every shape equally** and measured at 0.7–0.8 µs/query
  (<1%). It slightly compresses reported percentages relative to a pure C# reader.
- **No FTS, vectors, BM25 or DocId work here** — Task 4 owns filtered retrieval and ranking
  equivalence, which is where the harder visibility problem (recall windows) lives.

## 7. Reproduction

```bash
spike/index-store-ph0/read-path/run.sh          # ~2 min, ~2.1 GB scratch peak, self-cleaning
MILLER_PH0_KEEP=1 spike/index-store-ph0/read-path/run.sh   # keep the scratch databases
```

Every number above traces to `out/`: `divergence.tsv` (git sample), `store-build.json` (what was
built per view), `bytes-*.json` (`dbstat` per object + row counts), `verify-view*.json`
(equivalence), `reads-*.json` (timings + VDBE work), `query-plans.json`, `summary.md` (generated
tables), `environment.txt` (host, commit, sqlite version).

Scratch databases live in `$TMPDIR/miller-ph0-readpath` and are deleted by the exit trap; nothing
larger than 100 KB is committed. The real artifact is only ever opened through a `mode=ro` URI —
verified in this session by an attempted write, which failed with
`attempt to write a readonly database (8)`.

## Verification ledger

| item | value |
|---|---|
| commands | `bash lib/divergence.sh`, `./run.sh` (twice, end to end), `python3 lib/instrument.py {build-single,build-keepfile,build-v4,build-store,build-dedicated-view,inflate,bytes,verify,plans,measure}` |
| worktree | `/Users/murphy/source/miller/.claude/worktrees/index-store-ph0`, branch `worktree-index-store-ph0`, commit `0b0f8faf` at measurement time (`0ec78eec` at hand-off — sibling-task commits by the lead, no effect on this instrument) |
| host | Darwin arm64, python 3.14.6 / sqlite 3.53.4 |
| hard gate | 8-view physical bytes 1.027× vs ≤1.2× target → **PASS** |
| report-only | composite-key amplification +4.4% (raw) / −11.3% (v4 shape); read overhead +17.8%/+2.4% (name), −0.5%/+1.5% (path), +6.1%/+0.1% (refs) for manifest-join/temp-table |
| equivalence | 0 mismatches, 164,413 rows compared across 1,800 key lookups (5,400 query executions) in 2 views |
| result | complete; entry script re-runs clean and removes every generated database |
| timestamp | 2026-08-06 (UTC) — see `out/environment.txt` |
