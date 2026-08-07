# Ph0 — filtered-retrieval instrument: FTS, vectors, DocId/BM25

Throwaway instrument for the versioned index store program
([`docs/plans/2026-08-06-index-store-views-program.md`](../../../docs/plans/2026-08-06-index-store-views-program.md)
§3 read path, §4 sidecars). Nothing under `src/` or `tests/` changed.

Run with `./run.sh` (add `--keep` to preserve `work/`). The script builds every prototype
database under `work/`, reads the live `search.db` / `vectors.db` read-only, and deletes `work/`
on exit. Peak scratch 2.0 GB, wall clock about 4 minutes on an M2 Ultra.

Environment for every number below: macOS, Python 3.14.6 with SQLite 3.53.4 (FTS5 with the
trigram tokenizer), sqlite-vec v0.1.9 (`.tools/vec0.dylib`, the version Miller pins). Corpus:
the live Miller sidecar — 122,707 symbols across 1,412 paths, `avgdl` 9.9605, and the live
`vectors.db` symbol lane — 14,246 `int8[384] distance_metric=cosine` vectors with their real paths.

Every table below is the verbatim `summarize.py` output of one recorded run (2026-08-06). Corpus
construction is seeded, so **equivalence outcomes and row counts reproduce exactly**; timings move
about 10–25% run to run, so treat the millisecond columns as ratios rather than absolutes.

---

## Verdicts

| Question | Verdict |
|---|---|
| Does visibility inside the query reproduce a dedicated per-view index? | **Yes for the word arm at every multiple. For the trigram arm, only after the 200-row window stops being ordered by FTS5 `rank`.** Visibility-inside is necessary and not sufficient. |
| Can sqlite-vec pre-filter before top-K? | **Yes.** `rowid IN (SELECT rid FROM <per-view projection>)` reproduces the dedicated top-K exactly at every multiple, 43.6 ms vs 12.0 ms at 20×. Vectors can stay family-shared. |
| What is the canonical per-view DocId? | **The fresh-ordinal history (`ORDER BY path, start_line, symbol_id`), carried as an ORDER and not as a stored dense integer.** The incremental stable-reuse history must retire from the store. |

The one blocking finding: **today's trigram window rule cannot be carried into a family-shared
sidecar.** Everything else in the read path holds.

---

## 1. FTS recall-set equivalence

### What was built

A family store holding `M` versions of every path. Version 0 of each path is what view 0 sees;
versions 1..M-1 are hidden from it, and 35% of their symbols are mutated so hidden versions carry
different text. FTS rowid is the store surrogate key; a view is a manifest of path → version_id.
The oracle is a dedicated index built from view 0's rows alone, so its FTS corpus statistics are
view-local by construction.

Query shapes, all mirroring `FtsSymbolSearchIndex`:

- **postfilter** — the shipped shape with no visibility predicate, filtered by the client after
  `ORDER BY rank LIMIT` (word arm uncapped at `FtsSymbolSearchIndex.cs:312`, trigram arm windowed
  at `:298`/`:330`).
- **prefilter** — `JOIN view_manifest` inside the query, before the window.
- **temp table** — visibility materialised into a session `TEMP` rowid table, joined inside.
- **projection** — visibility read from a persisted `view_projection(view_id, rid, doc_id)` table.

Two trigram window rules:

- **rank** — today's rule, `ORDER BY symbols_trigram.rank, length(name), <doc order>`.
- **density** — `ORDER BY collapsed_len, length(name), <doc order>`, a stored per-row key.

Equivalence is an exact Python set comparison on `(version_id, symbol_id)`; every mismatch reports
its symmetric difference and up to three missing/extra keys. Query sets: 120 word tokens, 120
interior substrings drawn from real collapsed names, and 5 adversarial substrings chosen as the
most crowded interior substrings in the corpus.

### Adversarial history

| multiple | store rows | visible | hidden | store MB | hidden trigram matches for the adversarial queries (median / max) |
|---|---|---|---|---|---|
| x1 | 122,707 | 122,707 | 0 | 72 | 0 / 0 |
| x5 | 614,835 | 122,707 | 492,128 | 361 | 24,124 / 71,708 |
| x20 | 2,455,440 | 122,707 | 2,332,733 | 1,449 | 113,614 / 339,638 |

The bar was >200 hidden trigram matches crowding a 200-row window. At 20× the worst adversarial
query has **339,638** invisible matches, and the injected short-collapsed-name decoys outrank every
real hit. Ordinary (non-adversarial) queries reach a 1,054 median and 24,073 maximum, so the bar is
cleared by the realistic history too, not only by the injected one.

### Results

| multiple | arm | window | prefilter | temp table | projection | post-filter starved |
|---|---|---|---|---|---|---|
| x1 | word (uncapped) | n/a | PASS | PASS | PASS | n/a |
| x1 | trigram | rank | PASS | PASS | PASS | 0/120 |
| x1 | trigram | density | PASS | PASS | PASS | 0/120 |
| x1 | trigram adversarial | rank | PASS | PASS | PASS | 0/5 |
| x1 | trigram adversarial | density | PASS | PASS | PASS | 0/5 |
| x5 | word (uncapped) | n/a | PASS | PASS | PASS | n/a |
| x5 | trigram | rank | **FAIL (4)** | **FAIL (4)** | **FAIL (4)** | 70/120 |
| x5 | trigram | density | PASS | PASS | PASS | 71/120 |
| x5 | trigram adversarial | rank | **FAIL (1)** | **FAIL (1)** | **FAIL (1)** | 5/5 |
| x5 | trigram adversarial | density | PASS | PASS | PASS | 5/5 |
| x20 | word (uncapped) | n/a | PASS | PASS | PASS | n/a |
| x20 | trigram | rank | **FAIL (4)** | **FAIL (4)** | **FAIL (4)** | 112/120 |
| x20 | trigram | density | PASS | PASS | PASS | 112/120 |
| x20 | trigram adversarial | rank | **FAIL (1)** | **FAIL (1)** | **FAIL (1)** | 5/5 |
| x20 | trigram adversarial | density | PASS | PASS | PASS | 5/5 |

Three separate facts sit in that table.

**The naive post-filter starves, exactly as the program plan predicted.** At 20×, 112 of 120
ordinary trigram queries lose visible hits, and every adversarial query loses almost everything:

| query | visible hits owed | survivors after post-filter | lost | hidden matches in store |
|---|---|---|---|---|
| `tion` | 200 | 2 | 198 | 339,638 |
| `coun` | 200 | 7 | 193 | 113,614 |
| `utra` | 110 | 13 | 97 | 2,090 |

**The word arm is safe.** It is uncapped, so joining visibility anywhere produces the same set;
0 mismatches over 120 queries at every multiple. The cost is candidate amplification, not recall
(section 1.2).

**The trigram arm is not safe under today's window rule.** Visibility joined inside the query still
diverged from the dedicated index on 4 of 120 ordinary queries and 1 of 5 adversarial queries, at
both 5× and 20×, identically for all three visibility shapes. The symmetric difference is small (2–4
rows out of 200) and always sits at the window boundary — e.g. `trunc` at 20× loses 2 rows and gains
2 different ones.

### Why: FTS5 `rank` is a corpus-wide statistic

`symbols_trigram.rank` is FTS5's bm25, whose length normalisation reads the **whole table's**
average document length. A family-shared store and a dedicated per-view index do not share that
average, so the same visible row can order differently in the two indexes and the 200-row cut lands
in a different place. Filtering earlier does not help; the ordering key itself is contaminated.

The mechanism probe isolates it: 300 visible rows with phrase frequency 1–3 and widely varying
collapsed length, plus 40,000 long **non-matching** hidden rows whose only effect is on the corpus
average.

| window rule | k | set equal | order equal | symmetric difference |
|---|---|---|---|---|
| rank | 50 | False | False | 60 |
| rank | 100 | False | False | 80 |
| rank | 200 | False | False | 98 |
| density | 50 | True | True | 0 |
| density | 100 | True | True | 0 |
| density | 200 | True | True | 0 |

Under an engineered history the rank window disagrees with the dedicated index on 98 of 200
members. On the real corpus the disagreement is 2–4 rows, because most matched rows have phrase
frequency 1 — where the corpus average cancels out of the comparison — but the mechanism is live
and grows with how unlike each other the retained versions are.

### The fix: order the window by a stored key

`ORDER BY collapsed_len` is corpus-independent by construction, so the shared store and a dedicated
index cut the window identically at every k and every multiple: 0 mismatches across 120 + 5 queries
at 1×, 5× and 20×, and 0 symmetric difference in the mechanism probe.

It also states the window's existing intent directly instead of through a proxy. The hardening test
[`Search_TrigramWindowOverflow_BestInteriorMatchSurvivesTheWindow`](../../../tests/Miller.Tests/Indexing/FtsSymbolSearchIndexHardeningTests.cs)
says in its own comment that rank is used because "a shorter collapsed name has higher trigram
density, hence a better bm25 for the same matched phrase". `collapsed_len` is that property,
measured rather than inferred, and its shortest-name-first ordering satisfies that test by
definition. It is also cheaper: 1.340 ms vs 2.563 ms median at 20×.

**Owed to Ph1:** the density rule changes today's shipped trigram ordering for queries where phrase
frequency differs across candidates. That is a deliberate contract change and needs an equivalence
gate against the current index before it lands.

### 1.2 Query cost, median ms

| multiple | arm | no visibility | manifest join | temp table | projection | dedicated |
|---|---|---|---|---|---|---|
| x1 | word (uncapped) | 0.446 | 0.470 | 0.500 | 0.674 | 1.177 |
| x1 | trigram, rank | 0.472 | 0.450 | 0.476 | 0.475 | 0.461 |
| x1 | trigram, density | 0.262 | 0.258 | 0.250 | 0.261 | 0.255 |
| x1 | trigram adversarial, rank | 8.274 | 9.173 | 9.347 | 10.311 | 8.089 |
| x5 | word (uncapped) | 4.346 | 3.822 | 0.881 | 0.761 | 1.602 |
| x5 | trigram, rank | 1.399 | 1.100 | 1.039 | 1.006 | 0.482 |
| x5 | trigram, density | 0.725 | 0.594 | 0.534 | 0.561 | 0.250 |
| x5 | trigram adversarial, rank | 40.424 | 25.828 | 15.957 | 15.614 | 7.984 |
| x20 | word (uncapped) | 16.679 | 14.786 | **2.051** | 14.058 | 1.368 |
| x20 | trigram, rank | 4.445 | 3.332 | 2.678 | 2.563 | 0.502 |
| x20 | trigram, density | 2.148 | 1.813 | 1.425 | **1.340** | 0.259 |
| x20 | trigram adversarial, rank | 177.453 | 109.001 | 46.062 | 42.049 | 9.123 |

Overhead of the temp-table shape, measured against the **x1 store's own no-visibility query**
(0.446 ms word, 0.262 ms trigram density). The x1 store is byte-identical to the dedicated oracle —
122,707 rows and 72,388,608 bytes for both — so it is the honest per-view baseline. The `dedicated`
column above is a second file read after the store file in each iteration and carries a
cross-file cache asymmetry, which is why it is not used for ratios.

| multiple | word arm | trigram arm (density) |
|---|---|---|
| x1 | 1.1× | 1.0× |
| x5 | 2.0× | 2.0× |
| x20 | 4.6× | 5.4× |

Candidate rows the word arm hands to the C# ranker (median per query):

| multiple | with no visibility | visible only | amplification |
|---|---|---|---|
| x1 | 742 | 742 | 1.0× |
| x5 | 3,710 | 742 | 5.0× |
| x20 | 14,840 | 742 | 20.0× |

Amplification tracks the version multiple exactly, which is the cost the visibility join exists to
cut. It matters because the word arm is uncapped by design: without the join, the ranker would score
20× the rows and then discard 95% of them.

**The visibility probe must be an integer-rowid lookup, and it must run first.** The manifest join
reads a wide `store_symbols` row for every FTS hit before it can test `version_id`; the temp table
rejects invisible hits on an `INTEGER PRIMARY KEY` probe first. On the word arm at 20× that is
14.786 ms vs 2.051 ms — about 7× from join shape alone (6.8× on the confirmation run). The persisted
`view_projection(view_id, rid)` table (`WITHOUT ROWID`, composite key) sits between the two: it wins
on the trigram arm (1.340 vs 1.425 ms) and loses badly on the word arm (14.058 vs 2.051 ms), because
the composite-key probe costs about 7× an integer-rowid probe and the word arm performs 20× more of
them. Materialising the per-view rowid set into a session temp table costs **0.020 s once** at
read-session open (measured at every multiple) and then gives the fastest probe on both arms.

Query plans confirm the shape, e.g. the trigram prefilter at 20×:

```
SCAN symbols_trigram VIRTUAL TABLE INDEX 0:M2
SEARCH s USING INTEGER PRIMARY KEY (rowid=?)
SEARCH m USING PRIMARY KEY (view_id=? AND version_id=?)
USE TEMP B-TREE FOR ORDER BY
```

The `USE TEMP B-TREE FOR ORDER BY` is present in every windowed shape including the dedicated
index's, so the join does not cost an extra sort — FTS5's rank-limit optimisation is already
unavailable once the query joins anything.

---

## 2. sqlite-vec pre-filtering

### What was built

The live `vectors.db` symbol lane, read-only, projected into the same store shape: version ids are
per **file** (803 distinct visible versions over 14,246 vectors), hidden versions are perturbed
copies, and for each of 12 probes **600 hidden vectors** sit closer than any visible vector. That is
above the 500-candidate semantic window (`SemanticSearchArm.cs:147`), so a post-filter must starve.
Insertion uses `vec_int8(?)`, matching `VectorStore.VectorLiteral` (`VectorStore.cs:720`).

### Results

| multiple | mechanism | supported | top-K matches dedicated | median ms | p95 ms |
|---|---|---|---|---|---|
| x1 | postfilter | yes | True | 12.044 | 12.461 |
| x1 | postfilter_overfetch_max_k | yes | True | 19.934 | 20.297 |
| x1 | metadata_eq | yes | not applicable | 1.648 | 1.865 |
| x1 | metadata_in | yes | True | 14.467 | 15.591 |
| x1 | rowid_in_list | yes | True | 14.208 | 16.373 |
| x1 | rowid_in_select | yes | True | 14.411 | 16.015 |
| x1 | partition_key | yes | True | 12.150 | 12.577 |
| x1 | dedicated_per_view | yes | True | 12.214 | 12.512 |
| x5 | postfilter | yes | **False** | 62.754 | 65.949 |
| x5 | postfilter_overfetch_max_k | yes | True | 99.259 | 102.588 |
| x5 | metadata_eq | yes | not applicable | 5.839 | 6.786 |
| x5 | metadata_in | yes | True | 36.457 | 37.853 |
| x5 | rowid_in_list | yes | True | 20.209 | 22.047 |
| x5 | rowid_in_select | yes | True | 20.601 | 21.053 |
| x5 | partition_key | yes | True | 11.452 | 12.283 |
| x5 | dedicated_per_view | yes | True | 11.592 | 12.552 |
| x20 | postfilter | yes | **False** | 236.003 | 247.629 |
| x20 | postfilter_overfetch_max_k | yes | True | 374.848 | 382.094 |
| x20 | metadata_eq | yes | not applicable | 23.034 | 33.957 |
| x20 | metadata_in | yes | True | 117.220 | 124.972 |
| x20 | rowid_in_list | yes | True | 43.600 | 55.625 |
| x20 | **rowid_in_select** | yes | **True** | **43.607** | 56.014 |
| x20 | partition_key | yes | True | 11.996 | 12.492 |
| x20 | dedicated_per_view | yes | True | 11.994 | 12.352 |

sqlite-vec 0.1.9 accepts every constraint form tried: metadata `=`, metadata `IN`,
`rowid IN (<list>)`, `rowid IN (SELECT …)`, and a `partition key` column. All of them are applied
**before** top-K — each returns the dedicated index's exact 500-unit set while the unconstrained
query returns 1.

**Named mechanism: `rowid IN (SELECT rid FROM view_projection WHERE view_id = ?)`.** It reproduces
the dedicated top-K at every multiple and costs 43.6 ms at 20× against 12.0 ms dedicated (3.6×). The
inlined-rowid variant is equivalent in both correctness and cost (43.6 ms) but needs a 14,246-element
SQL literal, so the subquery form is preferred. `metadata_in` also works but costs 117 ms because the
IN list holds 803 version ids that must be tested per row.

`metadata_eq` cannot express a view: a view spans 803 versions, so a single-version equality returns
0 of the wanted rows. It is listed to record that the obvious cheap filter is not applicable, not as
a candidate.

`partition_key` matches the dedicated cost exactly (11.996 ms) but is not family sharing — it stores
one copy of every vector per view. Eight views cost **46,858,240 bytes** against **6,090,752** for a
single view, i.e. 7.7×.

### Over-fetch has a hard ceiling and is not a strategy

sqlite-vec rejects `k` above 4096:

```
OperationalError: k value in knn query too large, provided 10000 and the limit is 4096
```

With 600 hidden-nearer vectors per probe, over-fetching to k=4096 (8.2× the window) does recover the
correct set — at 374.8 ms, 8.6× the pre-filter. Raise the hidden density past the ceiling and it
collapses:

| strategy (6,000 hidden vectors nearer than any visible one) | visible rows recovered of 500 | ms |
|---|---|---|
| post-filter, k=500 | 1 | 17.205 |
| post-filter, k=4096 (engine maximum) | **1** | 27.787 |
| `rowid IN (SELECT …)` pre-filter | **500** | 15.069 |

The required over-fetch grows with hidden density while the ceiling is fixed, so post-filtering has
no correctness guarantee at any k. Only the pre-filter does.

### Byte comparison

| artifact | bytes |
|---|---|
| dedicated per-view vectors, 1 view | 6,090,752 |
| dedicated per-view vectors × 8 (projected) | 48,726,016 |
| partition-key table, 8 views | 46,858,240 |
| family-shared store, x1 | 6,418,432 |
| family-shared store, x5 | 34,791,424 |
| family-shared store, x20 | 129,204,224 |

### Verdict

**`vectors.db` can stay family-shared.** The pre-filter is real, exact, and costs 3.6× a dedicated
index at a 20× version multiple; the per-view fallback in the program plan is not needed on
correctness grounds. Two caveats for Ph1:

- sqlite-vec 0.1.9 KNN is brute force, so the pre-filtered query still grows with **total** store
  rows (14.4 → 20.6 → 43.6 ms across 1×/5×/20×), not with the visible count. Retention policy
  therefore has a direct read-latency price, unlike the FTS arms.
- At a 20× multiple the shared store is 129 MB against 48.7 MB for eight private copies. Family
  sharing wins on bytes only when the retained-version multiple stays below about 8× — otherwise it
  wins on embedding compute (which the plan keeps family-shared regardless) rather than on storage.

---

## 3. DocId and BM25 statistics

### The two shipped histories disagree

Replaying `AssignStableDocIds` (`SearchIndexWriter.cs:436`) over one file replacement — 2,000
symbols, one file removed, one file added that sorts first — and comparing against the fresh-ordinal
rule (`SqliteSymbolReader.cs:61`, `ROW_NUMBER() OVER (ORDER BY path, start_line, symbol_id) - 1`) on
the **identical** final symbol set:

- orders identical: **False**
- first divergent position: **0**
- positions differing: **888 of 1,824**

The stable-reuse history recycles freed ids, so a newly added file that sorts first receives high or
recycled ids and sorts last. This is not a store problem — it means a Miller index that converged
incrementally already emits a different tie-break order from a freshly rebuilt one. In a family
store, where one row set is read through eight views, a history-dependent ordinal cannot exist at
all: it would have to be per-view **and** per-convergence-history.

**The incremental stable-reuse history must retire from the store.**

### The two per-view DocId options, measured at 20× on the full corpus

| option | median ms/query | p95 ms | bytes for 8 views | manifest-flip maintenance |
|---|---|---|---|---|
| query-time `ROW_NUMBER()` over the visible set | **133.077** | 154.172 | 0 | none |
| materialised per-view mapping | **2.244** | 15.168 | **14,561,280** | 193.9 ms full rebuild |
| stored sort key, no ordinal at all | **2.621** | 18.226 | **0** | **none** |

The query-time window function is unusable: every query sorts all 122,707 visible rows to number
them, 133 ms before any ranking happens, for a median of 342 candidates.

The materialised mapping is fast (2.244 ms) and costs 14,561,280 bytes for eight views — 1.82 MB per
view, **14.83 bytes per row** (`view_projection(view_id, rid, doc_id)`, `WITHOUT ROWID`,
`PRIMARY KEY(view_id, rid)`, measured through `dbstat`). Building all seven sibling views takes
1.01 s. Its problem is maintenance: a **single file** changing version shifts the contiguous ordinal
of **122,528 of 122,707 rows**, so the flip owes a 193.9 ms full rebuild of that view's mapping —
on every file save.

The third option drops the dense ordinal and orders by the stored
`(path, start_line, symbol_id)` triple the ordinal was derived from. It is order-identical to the
fresh-ordinal rule by construction, costs 0.38 ms more per query, 0 extra bytes, and 0 maintenance
on a manifest flip.

### Recommendation

**Canonical DocId history = the fresh-ordinal rule, expressed as an order.**

- The comparator becomes `score DESC, path ASC, start_line ASC, symbol_id ASC` — order-identical to
  today's `score DESC, DocId ASC` on a freshly built index, and independent of both the view and the
  convergence history. The same triple replaces `s.doc_id` in the trigram window's tie-break.
- Document **identity** (the key `MillerSearchIndex._docLen`, `documentsById` and
  `FtsSymbolSearchIndex.Resolve(int docId)` need) comes from the store's version-row surrogate `rid`,
  which is unique, stable and view-independent. Those structures are already
  `FrozenDictionary<int,…>` lookups, so they do not require contiguous ids.
- `DocId` is therefore not a per-view quantity at all, which is why this option costs nothing per
  view and nothing per flip.

If Ph1 finds a consumer that genuinely needs a dense per-view ordinal (the `search_symbols.doc_id`
`UNIQUE` column is a published Eros-facing contract), the materialised mapping is the fallback at
14.83 bytes/row and 1.82 MB per view — but its 193.9 ms per-flip rebuild has to be budgeted against
save-frequency, and it should then also carry the visibility bit so one table serves both jobs.

### BM25 corpus statistics per view

`FtsSymbolSearchIndex` reads `_documentCount` and `_avgdl` from `meta` (stamped by
`SearchIndexWriter.ReadStats`, `:592`) and computes per-query `df` from the returned candidate rows
(`FtsSymbolSearchIndex.cs:232`). Only the first two need view-local maintenance; `df` becomes
view-local automatically once visibility is joined, because it is counted over the candidates the
query returned.

| option | median ms/query | bytes for 8 views |
|---|---|---|
| scan the visible set per query (`COUNT(*)`, `SUM(doc_len)` through the manifest) | 13.801 | 0 |
| same scan through the per-view projection | 11.648 | 0 |
| cached per (view, manifest generation) | **0.0** | **256** |

A per-query scan costs 13.8 ms — 6× the whole retrieval query — for two numbers that only change
when the manifest changes. Cache them: one row per (view_id, manifest_generation) holding
`doc_count` and `sum_doc_len`, 32 bytes per view, 256 bytes for eight, invalidated by the same
pointer flip that publishes the manifest. Measured values match the live sidecar exactly
(`doc_count` 122,707, `avgdl` 9.960540), which confirms the visible set reproduces the real corpus.

---

## 4. What Ph1 owes, from these measurements

1. **Replace the trigram window's ordering key.** `ORDER BY symbols_trigram.rank` cannot be used in a
   family-shared sidecar. Use the stored `collapsed_len` (plus name length and the canonical triple).
   Gate the change with an equivalence test against the current per-workspace index, because it
   changes shipped ordering for candidates with unequal phrase frequency.
2. **Visibility is an integer-rowid probe, applied first.** Join order and key width move the word
   arm by 7.2× at 20×. Materialising the view's rowid set into a session temp table at read-session
   open costs 0.020 s and is the fastest probe on both arms.
3. **Keep `vectors.db` family-shared** with `rowid IN (SELECT rid FROM <view projection>)`. Record
   the two caveats: brute-force KNN scales with total store rows, and the shared store passes eight
   private copies on bytes at roughly an 8× retained-version multiple.
4. **Retire `AssignStableDocIds` from the store** and make the canonical order the fresh-ordinal
   triple. Cache `(doc_count, avgdl)` per manifest generation rather than scanning.
5. Retention policy is now a **read-latency** input, not only a bytes input: between a 5× and a 20×
   version multiple the trigram arm grows 2.0× → 5.4×, the word arm 2.0× → 4.6×, and the vector arm
   1.8× → 3.6×.

## 5. Limits of this instrument

- Row counts are Miller-scale (122,707 symbols, 14,246 vectors) multiplied by the version count, not
  dotnet/runtime-scale. The equivalence claims are structural and scale-free; the millisecond figures
  are not, and the vector arm in particular scales linearly with total store rows.
- SQLite 3.53.4 via Python, not the SQLitePCLRaw build Miller ships. FTS5 bm25 and the trigram
  tokenizer are the same implementation, but absolute timings will differ.
- Hidden versions are synthetic mutations of real rows (35% of symbols per hidden version) plus
  injected decoys. Real branch divergence differs in shape; it was made adversarial on purpose,
  since the question is whether the window can be starved, not how often it is.
- Only the symbol arms were measured. `content.db` text search applies its own `ORDER BY rank LIMIT`
  and inherits the same rank-is-corpus-wide finding; it was not instrumented here.
