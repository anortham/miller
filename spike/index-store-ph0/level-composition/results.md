# Level composition inputs — Ph0 Task 2

**Date:** 2026-08-06 · **Worktree:** `.claude/worktrees/index-store-ph0` @ `0b0f8faf` ·
**Binary:** `julie-extract 2.27.0` (pinned, `scripts/julie-pins.json`)

## Headline

Levels are **already shipped end to end**, and the shipped boundary is not the strawman. The
strawman's three-level split (L1 symbol core / L2 reference layer / L3 regions+facts) is a *design
sketch*; `julie-extract --level symbols` implements a **two-level** split whose L1 is materially
larger and materially better-chosen than the strawman's. Measured on a controlled symbols-vs-full
extract of the same tree:

| Level | Bytes | Share | Serves |
|---|---:|---:|---|
| **L1 (shipped `--level symbols`)** | 211,386,368 | **27.54%** | search, inspect (core), context, metrics, health |
| **L2 (reference layer, deferred)** | 413,069,312 | **53.81%** | trace, impact, inspect refs/callers, edit rename-safety, dead-code candidates |
| **L3 (text/facts, deferred)** | 143,167,488 | **18.65%** | patterns, region search, markers, bridge trace |
| Total (full artifact) | 767,623,168 | 100% | |

Three corrections to the inherited numbers, all measured below:

1. The levels doc's **74% / 17% / 9%** split is stale. Under the doc's own grouping the current
   artifact is **66.9% / 18.2% / 14.9%**. Under the *shipped* boundary it is **53.8% / 18.7% / 27.5%**.
2. `reference_sites` and `pending_relationships` are **not** wholly L2. The strawman put both in the
   reference layer; the shipped level keeps `pending_relationships` (7.13%) and the
   relationship-derived 22% of `reference_sites` (5.08%) in L1.
3. The **"generated code ~43 identifiers/KB vs real code ~5–10"** claim is **refuted in both halves**
   across 7 repos and ~19,000 files. Real code runs 20–41/KB at the median; 43/KB is roughly the
   *p90 of ordinary hand-written code*. No file in any repo exceeds 100/KB.

---

## 1. Decision table — every artifact table

Byte columns are `dbstat` on a controlled pair of fresh extracts of `/Users/murphy/source/miller`
(same tree, same binary, `--jobs 4`), so the level column is a *measurement*, not an assignment.
`Δ vs L1` is the byte cost the level defers. Shares are of the 767,623,168-byte full artifact.

| Table | Full bytes | Share | At `--level symbols` | Level | Tool surfaces served | Extraction-cost note |
|---|---:|---:|---:|---|---|---|
| `identifiers` | 220,004,352 | 28.66% | 28,672 (empty) | **L2** | trace refs, impact, inspect refs/callers, edit rename-safety, `references candidates` | The identifier walk; the single largest extraction *and* write cost |
| `reference_sites` | 177,131,520 | 23.08% | 38,961,152 | **SPLIT** | same as `identifiers` (L2 part); relationship evidence (L1 part) | 97,579 spanless + 6,556 span-present rows come free with relationships; 374,148 span-present rows ride the identifier walk |
| `source_regions` | 91,762,688 | 11.95% | 24,576 (empty) | **L3** | `search regions=`, `search.db` regions arm, `inspect` doc-comment flag, dead-code literal filter | Text collector; cheap per row, high volume (87.5% is `string_literal`) |
| `symbols` | 83,697,664 | 10.90% | 83,697,664 | **L1** | search, inspect, context, everything | The core parse; unavoidable |
| `identifier_resolutions` | 54,935,552 | 7.16% | 12,288 (empty) | **L2** | trace/impact target resolution overlay | Write-phase resolution pass — **+4,227 ms of the +10,050 ms deferred write (42%)** |
| `pending_relationships` | 54,734,848 | 7.13% | 54,734,848 | **L1** | trace path, impact (unresolved edges), resolution input | Relationship walk; already paid at L1 |
| `structural_facts` | 46,469,120 | 6.05% | 24,576 (empty) | **L3** | `patterns`, `search mode=markers`, `trace mode=bridge`, report/health | Query-pack collector; 86.8% of rows are `json.*` and 10.3% `markdown.*` on this tree |
| `relationships` | 10,878,976 | 1.42% | 10,878,976 | **L1** | inspect callers/callees, trace path, impact | Extraction-direct edges |
| `type_facts` | 10,170,368 | 1.32% | 10,170,368 | **L1** ⚠ | **none — zero consumers in `src/`** | Cheap, already in L1; see §2 |
| `complexity_metrics` | 7,843,840 | 1.02% | 7,843,840 | **L1** | `inspect` symbol complexity, `metrics complexity`, `report`, `workspace health`, dashboard | Cheap; computed during the symbol walk |
| `type_argument_usages` | 2,539,520 | 0.33% | 16,384 (empty) | **L3** | `trace mode=bridge` (CreateMap grouping) | Byproduct of the identifier walk, stripped at symbols level |
| `type_arguments` | 2,461,696 | 0.32% | 16,384 (empty) | **L3** | `trace mode=bridge` | Cascades off `type_argument_usages` — empty when usages are empty |
| `pending_resolutions` | 2,441,216 | 0.32% | 2,441,216 | **L1** | resolution overlay for relationship edges | Free with the relationship pass |
| `symbol_annotations` | 1,335,296 | 0.17% | 1,335,296 | **L1** | inspect attributes, bridge route facts | Free with the symbol walk |
| `files` | 618,496 | 0.08% | 618,496 | **L1** | every tool (freshness, paths) | Discovery |
| `revision_file_changes` | 229,376 | 0.03% | 229,376 | **L1** | sidecar convergence, freshness | Bookkeeping |
| `language_capabilities` | 122,880 | 0.02% | same | **L1** | `capabilities`, health | Static per binary |
| `language_capability_gaps` | 86,016 | 0.01% | same | **L1** | `capabilities`, health | Static per binary |
| `language_capability_fixtures` | 45,056 | 0.01% | same | **L1** | `capabilities` | Static per binary |
| `literals` | 32,768 | 0.00% | 16,384 (empty) | **L3** | `trace mode=bridge` (url/sql literals) | Stripped explicitly — see `strip_to_symbols_level` |
| `parser_inventory` | 20,480 | 0.00% | same | **L1** | schema gate, health | Static per binary |
| `parse_diagnostics` | 16,384 | 0.00% | same | **L1** | health, extraction diagnostics | Free with the parse |
| `artifact_metadata` | 8,192 | 0.00% | same | **L1** | freshness, level, version invariants | Required by every reader |
| `extraction_revisions` | 4,096 | 0.00% | same | **L1** | freshness, converge | Bookkeeping |
| `sqlite_schema` | 32,768 | 0.00% | same | n/a | — | — |

Level sums (deltas, so empty-btree overhead is not double-counted):

```
L1 = 211,386,368  (27.54%)   ← measured directly: dbstat total of symbols-level.db
L2 = 219,975,680 (identifiers) + 138,170,368 (reference_sites Δ) + 54,923,264 (identifier_resolutions Δ)
   = 413,069,312  (53.81%)
L3 =  91,738,112 (source_regions Δ) + 46,444,544 (structural_facts Δ)
     + 2,523,136 (type_argument_usages Δ) + 2,445,312 (type_arguments Δ) + 16,384 (literals Δ)
   = 143,167,488  (18.65%)
L1 + L2 + L3 = 767,623,168 = dbstat total of full-level.db  ✓
```

### Evidence — the controlled extract pair

```bash
OUT=/tmp/level-comp-53909
.tools/julie-extract scan --root /Users/murphy/source/miller --db "$OUT/symbols-level.db" \
    --level symbols --jobs 4 --json
.tools/julie-extract scan --root /Users/murphy/source/miller --db "$OUT/full-level.db" \
    --level full --jobs 4 --json
```

Row totals reported by the two runs (`counts.totals`), identical tree, 1,417 files:

| | `--level symbols` | `--level full` |
|---|---:|---:|
| `symbols` | 122,707 | 122,707 |
| `relationships` | 17,161 | 17,161 |
| `pending_relationships` | 86,974 | 86,974 |
| `reference_sites` | **104,135** | **478,283** |
| `identifiers` | **0** | 380,720 |
| `identifier_resolutions` | **0** | 380,720 |
| `source_regions` | **0** | 168,182 |
| `structural_facts` | **0** | 60,143 |
| `type_argument_usages` / `type_arguments` / `literals` | **0 / 0 / 0** | 7,319 / 9,570 / 50 |
| `type_facts` | **49,859** | 49,859 |
| `complexity_metrics` | **13,100** | 13,100 |
| `symbol_annotations` | **5,934** | 5,934 |
| `pending_resolutions` | **10,395** | 10,395 |
| artifact file size | **211,386,368 B** | **767,623,168 B** |

`reference_sites` splits cleanly by provenance, which is what makes it a SPLIT row rather than an L2 row:

```sql
-- symbols level: spanless 97,579 + target_token 6,556 = 104,135
-- full level:    spanless 97,579 + target_token 380,704 = 478,283
SELECT provenance, is_exact, COUNT(*) FROM reference_sites GROUP BY 1,2;
```

Same split confirmed on the live artifact (identifier-derived rows are exactly the ones an
identifier row points at):

```sql
-- file:/Users/murphy/source/miller/.miller/symbols.db?mode=ro
SELECT COUNT(*) FROM reference_sites;                                   -- 478,283
SELECT COUNT(*) FROM reference_sites rs WHERE EXISTS
  (SELECT 1 FROM identifiers i WHERE i.reference_site_id=rs.reference_site_id);  -- 380,704
SELECT COUNT(*) FROM reference_sites rs WHERE NOT EXISTS (...);                  -- 97,579
```

### Cross-check — the live 808 MB artifact

`dbstat` rolled up to logical tables (table pages + all index pages), read-only:

```bash
sqlite3 "file:/Users/murphy/source/miller/.miller/symbols.db?mode=ro" \
"SELECT COALESCE(m.tbl_name,d.name), SUM(d.pgsize),
        ROUND(100.0*SUM(d.pgsize)/(SELECT SUM(pgsize) FROM dbstat),2)
 FROM dbstat d LEFT JOIN sqlite_master m ON m.name=d.name GROUP BY 1 ORDER BY 2 DESC;"
```

| table | bytes | share | | table | bytes | share |
|---|---:|---:|---|---|---:|---:|
| `identifiers` | 230,912,000 | 28.55% | | `relationships` | 11,673,600 | 1.44% |
| `reference_sites` | 182,927,360 | 22.62% | | `type_facts` | 10,661,888 | 1.32% |
| `source_regions` | 94,601,216 | 11.70% | | `complexity_metrics` | 8,429,568 | 1.04% |
| `symbols` | 86,740,992 | 10.73% | | `pending_resolutions` | 3,072,000 | 0.38% |
| `identifier_resolutions` | 66,437,120 | 8.21% | | `type_argument_usages` | 2,752,512 | 0.34% |
| `pending_relationships` | 57,987,072 | 7.17% | | `type_arguments` | 2,682,880 | 0.33% |
| `structural_facts` | 47,017,984 | 5.81% | | `symbol_annotations` | 1,437,696 | 0.18% |
| | | | | everything else | < 700 KB each | < 0.1% |

Total 808,751,104 B (= file size; freelist is empty). Shares track the fresh extract within 1 pp on
every table, so the fresh pair is representative and the drift claim below is not an artifact of
using a rebuilt DB.

**Doc-number drift.** Recomputing the levels doc's own grouping on this artifact:

| Grouping (levels doc §Why) | Doc claim (934 MB) | Measured now (808.8 MB) |
|---|---:|---:|
| reference layer (`identifiers` + `reference_sites` + resolutions + pending) | 74% | **66.93%** (541,335,552 B) |
| regions / facts / literals | 17% | **18.19%** (147,091,456 B) |
| symbol core | 9% | **14.88%** (120,324,096 B) |

The doc understated the symbol core because `type_facts`, `complexity_metrics` and
`pending_relationships`/`pending_resolutions` were not counted in it. Under the *shipped* boundary
(which keeps all four in L1) the symbol core is 27.54%, three times the doc's figure. **Design call
input: L1 is not "9% of the artifact". Budget it at ~28%.**

### Extraction and write cost

Phase timings from the two runs (`profile.phases`, ms):

| Phase | symbols | full | Δ |
|---|---:|---:|---:|
| `extraction_spool` (parse + walks) | 2,639 | 4,518 | **+1,879** |
| `artifact_write` total | 2,679 | 12,729 | **+10,050** |
| ↳ `artifact_write_resolution` | 693 | 4,920 | +4,227 (**L2-exclusive**) |
| ↳ `artifact_write_child_rows` | 706 | 3,764 | +3,058 |
| ↳ `artifact_write_index_build` | 497 | 2,002 | +1,505 |
| ↳ `artifact_write_foreign_key_check` | 211 | 967 | +756 |
| ↳ `artifact_write_commit` | 182 | 656 | +474 |
| **total_duration_ms** | **5,381** | **17,332** | **3.22× slower at full** |

C# extraction wall-clock went 6,601 → 11,977 ms (+81%) — that is the identifier walk plus the
text/facts collectors. The write side is where the levels win is: **+375%**, and 42% of that delta
is the L2-only resolution pass.

Splitting the deferred write between L2 and L3 (estimate, labelled as such — the binary has no
mid-level to measure directly): the resolution phase is 100% L2; the remaining +5,823 ms splits
pro-rata by deferred child rows (L2 1,135,588 rows = 82.2%, L3 245,264 rows = 17.8%), giving
**L2 ≈ 9.0 s (90%) and L3 ≈ 1.0 s (10%)** of the deferred write.

---

## 2. Open question 1 — where do `type_facts` and `complexity_metrics` land?

**Recommendation: both in L1. This is already shipped; do not relitigate it. Separately, flag
`type_facts` as an unconsumed table before it gets a version-qualified index budget in the v4 store.**

Evidence that it is already decided, from the extractor:

`crates/julie-extractors/src/base/results_normalization.rs:81` — `strip_to_symbols_level`, documented
as "the single authority on what a `Symbols`-level extraction may carry", clears exactly five
families and neither of these two:

```rust
self.identifiers.clear();
self.type_argument_usages.clear();
self.literals.clear();
self.source_regions.clear();
self.structural_facts.clear();
```

Confirmed on the real symbols-level extract: `type_facts` 49,859 rows and `complexity_metrics`
13,100 rows, identical to the full extract.

Cost: 10,170,368 + 7,843,840 = **18,014,208 B = 2.35% of the artifact, 8.5% of L1**. Cheap.

`complexity_metrics` clearly earns L1 — it is read by `inspect`'s per-symbol complexity
(`src/Miller.Indexing/ExtractReader.cs:79`), `ComplexityRankingReader`, `MetricSnapshotAggregates`,
`WorkspaceHealthReader`, `ComplexityExportReader`, `WorkspaceRender` and the dashboard. `inspect` is
52.4% of all tool calls.

`type_facts` does **not** earn it, and the store program should know that:

```bash
grep -rn "type_facts" src spike scripts    # → no matches
grep -rn "TypeFact\|resolved_type" src     # → no matches
grep -rn "type_facts" tests | wc -l        # → 18, all fixture DDL / JSON report fixtures
```

It is also absent from `JulieSchemaGate`'s required-table list
(`src/Miller.Indexing/JulieSchemaGate.cs:20-27`). So **1.32% of every stored version pays for a table
no Miller surface reads.** Keeping it in L1 is right (it is free at extraction time and the store
dedups it across versions), but the Ph1 table inventory should record it as consumer-less: it needs
row storage, not a version-qualified index budget, and it is the first candidate if a purge/GC
policy ever needs a lever.

## 3. Open question 2 — does L3 precede L2 in background convergence?

**Recommendation: NO. Order is L1 → L2 → L3.** One bounded carve-out is worth the design call's time
(§3.1).

Usage evidence, all-time telemetry (`~/.miller/telemetry.db`, 40,728 calls):

```sql
SELECT tool, COUNT(*) FROM tool_telemetry GROUP BY tool ORDER BY 2 DESC;
SELECT tool, op, COUNT(*) FROM tool_telemetry
 WHERE tool IN ('search','content','patterns','trace','impact','edit') GROUP BY 1,2;
```

| | calls | share | depends on |
|---|---:|---:|---|
| `inspect` | 21,357 | 52.44% | L1 core; **L2** for refs/callers enrichment |
| `search` | 12,400 | 30.45% | L1 (+ `content.db`); **L3** for `regions` (7) and `markers` (37) |
| `workspace` | 1,951 | 4.79% | L1 |
| `impact` | 1,544 | 3.79% | **L2** |
| `trace` | 1,468 | 3.60% | **L2** (`refs` 1,424, `path` 18); **L3** (`bridge` 26) |
| `context` | 1,053 | 2.59% | L1 |
| `edit` | 442 | 1.09% | **L2** for rename safety |
| `content` | 376 | 0.92% | `content.db`, not the artifact |
| `patterns` | 137 | 0.34% | **L3** |

- **L2-dependent traffic:** 2,986 exclusive calls (7.33% — `impact` 1,544 + `trace refs` 1,424 +
  `trace path` 18) plus degraded enrichment on 21,357 `inspect` calls (52.4%) plus `edit` rename
  safety (1.1%).
- **L3-exclusive traffic:** `patterns` 137 + `search markers` 37 + `search regions` 7 +
  `trace bridge` 26 = **207 calls = 0.51%**.

That is a **14.4× traffic ratio in L2's favour on exclusive calls alone**, and the gap is far wider
once `inspect`'s degraded enrichment is counted. The cheap-first argument for L3 (143 MB vs 413 MB)
loses because the ordering criterion is *time to restore the most-used degraded surface*, not
time to finish a layer. Building L3 first delays the fix for `trace`/`impact`/`inspect`-refs by the
whole L3 build for the benefit of 0.51% of calls.

The cost data agrees: L2's exclusive resolution phase is the single largest deferred cost
(+4,227 ms, 42% of the deferred write). Deferring it *last* maximises the window in which the
artifact's most expensive owed work is still owed.

### 3.1 The one carve-out worth discussing

`source_regions` is not purely a `patterns`-adjacent table. It has a consumer on the **default search
path**: `SearchTool.Search` → `ReadHasDocCommentBestEffort` → `SqliteSourceRegionReader.ReadHasDocComment`
(`src/Miller.Server/Tools/SearchTool.cs:283`, `:475`, `:3904`;
`src/Miller.Indexing/SqliteSourceRegionReader.cs:81`), plus `SearchIndexWriter.InsertRegions`
(`src/Miller.Indexing/SearchIndexWriter.cs:653`) which builds `search.db`'s regions arm. It is
best-effort and degrades to an empty set, so it never blocks — but every one of the 12,400 `search`
calls loses its doc-comment annotation while L3 is owed.

The kind split makes a carve-out cheap:

```sql
SELECT kind, COUNT(*) FROM source_regions GROUP BY kind ORDER BY 2 DESC;
-- string_literal 147,161 (87.5%) | doc_comment 13,217 (7.9%) | comment 7,024 (4.2%) | embedded 780 (0.5%)
```

Promoting **only `kind='doc_comment'`** costs ~7.9% of `source_regions` ≈ **7.2 MB, 0.94% of the
artifact** (row-count pro-rata; region rows are fixed-width spans, so the byte estimate tracks the
row share), and restores the search annotation immediately. Caveat for the design call: this is a
*kind*-level gate, finer than the "table-set gates only" rule the levels doc lists as a non-goal, so
it is a real julie-extractors change request, not free. Present it as an option; the default
recommendation stands at plain L1 → L2 → L3.

## 4. Open question 3 — does a per-file identifier cap ship alongside?

**Recommendation: NO cap. The density lever the levels doc proposes does not exist in the data. If
the gate insists on a lever, the only defensible one is an absolute per-file identifier budget at
5,000 identifiers/file with a visible marker — and it is a minified/vendored-asset guard, not a
generated-code lever.**

### 4.1 Density distribution — Miller

```sql
-- file:/Users/murphy/source/miller/.miller/symbols.db?mode=ro
WITH d AS (SELECT f.path, f.content_bytes,
             COUNT(i.identifier_id)*1024.0/f.content_bytes AS ids_per_kb
           FROM files f JOIN identifiers i ON i.file_id=f.file_id
           WHERE f.content_bytes >= 1024 GROUP BY f.file_id),
 ord AS (SELECT ids_per_kb, ROW_NUMBER() OVER (ORDER BY ids_per_kb) rn, COUNT(*) OVER () n FROM d)
SELECT ...;
```

| files ≥1 KB with identifiers | p50 | p75 | p90 | p95 | p99 | max |
|---:|---:|---:|---:|---:|---:|---:|
| 755 | **32.17** | 38.11 | 43.27 | 45.70 | 51.79 | **58.62** |

Per language (per-file aggregation, no join fan-out): csharp 32.57, python 31.92, javascript 34.50,
powershell 25.48, bash 25.33, css 24.38, razor 19.82, html 3.80. Markdown/JSON/YAML emit zero
identifiers (595 files, 7.4 MB of the tree) — they are pure `symbols` + `structural_facts` content.

### 4.2 Density distribution — six more repos

```sql
-- per repo, files ≥1 KB with ≥1 identifier
```

| repo | files | p50 | p90 | p99 | max | files > 60/KB | files > 100/KB |
|---|---:|---:|---:|---:|---:|---:|---:|
| miller | 755 | 32.2 | 43.3 | 51.8 | 58.6 | 0 | **0** |
| openclaw | 8,905 | 28.3 | 39.0 | 50.2 | 67.3 | 11 | **0** |
| guava | 3,125 | 19.5 | 40.4 | 57.9 | 75.6 | 30 | **0** |
| samples (dotnet) | 2,825 | 22.9 | 35.7 | 51.4 | 76.0 | 6 | **0** |
| moltbot | 2,653 | 33.0 | 44.1 | 56.7 | 76.0 | 17 | **0** |
| AccessIQ | 411 | 25.9 | 46.3 | 58.5 | 68.5 | 5 | **0** |
| Terraform | 232 | 40.9 | 55.2 | 65.2 | 85.5 | 12 | **0** |

**18,906 files across 7 repos. Zero files above 100 identifiers/KB. There is no density tail.**

The claim fails in both directions:
- "real code ~5–10/KB" — real code runs **19.5–40.9/KB at the median**, 2–8× the claim.
- "generated code ~43/KB" — 43/KB is between p90 (miller 43.3) and p95 of *ordinary hand-written
  code*. A 43/KB cap would truncate roughly the top 10% of normal source files.

The reason is structural: density has a lexical ceiling. 43 ids/KB is one identifier per 24 bytes;
the observed maximum (85.5) is one per 12 bytes. Source text cannot get much denser than that, so
generated code cannot separate itself from hand-written code on this axis.

### 4.3 The tail is absolute size, not density

```sql
WITH d AS (SELECT f.path, f.content_bytes,
             (SELECT COUNT(*) FROM identifiers i WHERE i.file_id=f.file_id) AS ids FROM files f)
SELECT COUNT(*), SUM(ids) FROM d WHERE ids > 2000;   -- etc.
```

| repo | total ids | files >1,000 ids | their share | files >2,000 ids | their share | files >5,000 ids | their share |
|---|---:|---:|---:|---:|---:|---:|---:|
| openclaw | 1,995,890 | 272 | 21.3% | 46 | 6.3% | 1 | 0.3% |
| guava | 765,669 | 164 | 37.2% | 33 | 14.6% | 6 | 4.8% |
| samples | 285,718 | 12 | 16.2% | 7 | 13.7% | 2 | 6.6% |
| moltbot | 555,372 | 40 | 10.0% | 3 | 1.5% | 0 | 0.0% |

Per-file identifier count percentiles: openclaw p50 72 / p90 430 / p99 1,405 / max 5,165;
guava p50 89 / p90 631 / p99 2,032 / max 7,188; samples p50 34 / p90 156 / p99 616 / max 11,804;
moltbot p50 100 / p90 432 / p99 1,050 / max 2,941.

**What the top files actually are** — and this is the decisive point against a cap:

| repo | top file | ids | density |
|---|---|---:|---:|
| guava | `guava-tests/test/…/cache/LocalCacheTest.java` | 7,188 | 61.5 |
| guava | `android/guava-tests/test/…/concurrent/FuturesTest.java` | 5,908 | 41.4 |
| openclaw | `apps/ios/Sources/Model/NodeAppModel.swift` | 5,165 | 27.2 |
| openclaw | `extensions/memory-core/src/memory/qmd-manager.test.ts` | 4,669 | 30.2 |
| samples | `core/profiling/common/sdk/corprof.h` | 11,804 | 12.4 |
| samples | `orleans/…/wwwroot/css/bootstrap/bootstrap.min.css` | 6,922 | 30.4 |
| miller | `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` | 9,432 | 43.7 |

The tail is **large test files and large hand-written headers/models**, not generated code. Exactly
one entry (`bootstrap.min.css`) is machine-produced, and its density (30.4) is *below* the miller
median. Capping identifiers per file would silently break `trace refs` and `impact` for the biggest
test files in the repo — the files agents most often ask "who calls this" about.

### 4.4 Concrete recommendation

- **Do not ship a density cap.** The threshold the doc implies (43/KB) sits inside the normal band
  and would damage ~10% of ordinary files.
- **Do not ship an absolute cap as part of levels.** The measured win is ≤14.6% of L2 rows in the
  worst repo (guava, a test-heavy library) and 1.5–6.3% typically, in exchange for silently wrong
  reference results on the affected files. Levels already defer 100% of those rows off the
  first-open path, which is the same latency win without the correctness hole.
- **If a lever is mandated:** a per-file identifier budget at **5,000 identifiers/file**, applied
  with a visible per-file marker (`workspace health` + the file's row) rather than silently. 5,000 is
  above p99 in every repo measured; it would touch 1 file in openclaw (0.3% of ids), 6 in guava
  (4.8%), 2 in samples (6.6%), 0 in moltbot. Framed honestly, that is a guard against minified and
  vendored assets, which `.gitignore`/`.julieignore` rules already solve better.

---

## 5. Design-call summary

| Question | Recommendation | Basis |
|---|---|---|
| `type_facts`, `complexity_metrics` level | **L1 both** (already shipped) | 2.35% of bytes; `complexity_metrics` serves `inspect` (52% of calls); `type_facts` has zero consumers → flag for the Ph1 inventory, no index budget |
| L3 before L2? | **No — L1 → L2 → L3** | L2 exclusive traffic 7.4% + degraded `inspect` 52%, vs L3 exclusive 0.51%; L2 owns 42% of the deferred write cost |
| Optional carve-out | Promote `source_regions kind='doc_comment'` (0.95% of bytes) if a kind-level gate is acceptable | It is on the default `search` path, not just `patterns` |
| Per-file identifier cap | **No** | Zero files above 100 ids/KB across 18,906 files in 7 repos; the "43 vs 5–10" claim is refuted; the real tail is large test files, not generated code |
| L1 byte budget for the store contract | **~28% of a full artifact**, not 9% | Measured 211,386,368 / 767,623,168 on a controlled extract pair |

## 6. Reproduction

Read-only queries against the live artifact:

```bash
sqlite3 "file:/Users/murphy/source/miller/.miller/symbols.db?mode=ro" "<query>"
sqlite3 "file:$HOME/.miller/telemetry.db?mode=ro" "<query>"
sqlite3 "file:$HOME/.miller/workspaces.db?mode=ro" "SELECT display_id, canonical_root FROM workspaces;"
```

Controlled level pair (writes only to `/tmp`, never to any `.miller`):

```bash
OUT=$(mktemp -d)
.tools/julie-extract scan --root /Users/murphy/source/miller --db "$OUT/symbols-level.db" --level symbols --jobs 4 --json
.tools/julie-extract scan --root /Users/murphy/source/miller --db "$OUT/full-level.db"    --level full    --jobs 4 --json
for L in symbols full; do sqlite3 "$OUT/$L-level.db" \
  "SELECT COALESCE(m.tbl_name,d.name), SUM(d.pgsize) FROM dbstat d
   LEFT JOIN sqlite_master m ON m.name=d.name GROUP BY 1 ORDER BY 2 DESC;"; done
```

Note: freshly written artifacts are in WAL mode with no `-shm` present, so `?mode=ro` returns
SQLITE_CANTOPEN (14) on them; open the temp copies read-write or with `immutable=1`. The live
artifact has a live `-shm` and reads fine read-only.

## 7. Code references

| Claim | Location |
|---|---|
| Symbols level strips exactly 5 families | `julie-extractors crates/julie-extractors/src/base/results_normalization.rs:81` |
| `ExtractionLevel` = `{Symbols, Full}`, uniform across languages | `julie-extractors crates/julie-extractors/src/base/types.rs:514` |
| Registry gate on the identifier walk | `julie-extractors crates/julie-extractors/src/registry.rs:51,95,140,183,262,…,824` |
| Level policy + `artifact_metadata.index_level` | `src/Miller.Indexing/IndexLevels.cs:1-196`, `:205` (`ExtractIndexLevelReader`) |
| Per-workspace level policy column | `~/.miller/workspaces.db` → `workspaces.level_policy` |
| `source_regions` on the default search path | `src/Miller.Server/Tools/SearchTool.cs:283,475,3904` → `src/Miller.Indexing/SqliteSourceRegionReader.cs:81` |
| `source_regions` → `search.db` regions arm | `src/Miller.Indexing/SearchIndexWriter.cs:653` |
| Region-search level guard | `src/Miller.Server/Workspaces/WorkspaceRegionSearchContext.cs:11` |
| `structural_facts` consumers | `PatternsTool`, `MarkerFactReader`, `SqliteBridgeReader`, `ReportTool`, `WorkspaceHealthReader`, `MetricSnapshotAggregates`, `DeadCodeCandidateReader` |
| `complexity_metrics` on the `inspect` path | `src/Miller.Indexing/ExtractReader.cs:79` |
| `type_facts` has no consumer | no match in `src/`, `spike/`, `scripts/`; absent from `src/Miller.Indexing/JulieSchemaGate.cs:20-27` |
| Shipped symbols-level row shape assertion | `tests/Miller.Tests/Indexing/IndexLevelContextTests.cs:134` |
