# Ph0 Task 1 — Artifact purity audit + P1a status

**Date:** 2026-08-06
**Question:** which artifact tables are pure functions of `(relative path, file content, extractor fingerprint)`?
**Sources of truth:** julie-extractors `main` @ `ab7b16a` (v2.27.0, read-only) and the live Miller artifact
`/Users/murphy/source/miller/.miller/symbols.db` (`binary_version=2.27.0`, `schema_version=5`,
`extract_contract_version=4`, opened `mode=ro`).

Doc claims were not trusted. Every classification below is backed by a write site (`file:line`) and, where a
query can decide it, by an empirical result on real data.

---

## 1. Verdict

| Claim | Status |
|---|---|
| Per-file extraction tables are semantically pure functions of (path, content, extractor) | **CONFIRMED**, with two exceptions |
| Exception 1: `identifiers.target_symbol_id` is resolver write-back | **CONFIRMED** — the only impure column in any extraction table |
| Exception 2: `files.indexed_at` / `files.last_revision_id` are scan-scoped | **CONFIRMED** — trivial byte weight |
| Program-doc hypothesis "resolution layer ≈ 12% of store bytes / ~88% pure" | **CORRECTED UPWARD**: **89.42% pure / 10.49% resolution / 0.09% other global** |
| **NEW BLOCKER (not in the program doc):** `metadata_json` bytes are nondeterministic across scans of identical content | **CONFIRMED** — 98.6% of files affected; content-addressing yields ~0% dedup until fixed |
| P1a delta-scoped resolution landed | **YES** — v2.27.0, commit `bbbdce2c` (2026-08-05) |
| Equivalence gate exists and passes | **YES** — `resolution_scope_equivalence.rs`, 9/9 pass (run below) |

The purity premise of the content-addressed store **survives** this audit. The surgery it needs is one column
plus two `files` columns. But a **second, previously unidentified defect** — nondeterministic JSON key order —
must be fixed before content-addressed dedup can work at all. It is a small, local fix (7 type declarations),
not an architectural problem.

---

## 2. Complete table inventory

Table list is the writer's own DDL, `crates/julie-extract-artifact/src/schema.rs:84-538` (`SCHEMA_TABLES_SQL`),
cross-checked against `sqlite_master` on the live artifact: **24 tables, 1 trigger, 51 explicit indexes**. No
table in one list is absent from the other.

Classes:

- **PURE** — row set and every column byte are a function of (relative path, file content, extractor fingerprint).
- **PURE\*** — as above, but a byte-determinism defect makes the bytes unstable (see §4). Semantically pure.
- **MUTATED** — per-file rows carrying at least one column written from repo-global or scan-global state.
- **GLOBAL-repo** — content is a function of the whole repository (resolution overlay, revision ledger).
- **GLOBAL-fingerprint** — content is a function of the extractor binary alone; identical for every workspace
  scanned by the same binary. Shared once in a store, never per-view.

| # | Table | Class | Primary write site | Evidence |
|---|---|---|---|---|
| 1 | `artifact_metadata` | GLOBAL-repo | `writer.rs:1472-1483` (`write_metadata`); also `metadata.rs:62,88,139`, `resolution_store.rs:1141`, `artifact_access.rs:993` | E-1: `root_path`, `artifact_id`, `created_at`, `updated_at` all differ across two roots with identical content |
| 2 | `parser_inventory` | GLOBAL-fingerprint | `writer/capabilities.rs:132-160` (`upsert_parser_inventory`), delete at `:62` | E-1: byte-identical across roots. Source is a compiled-in constant (`capability_snapshot.rs:131-136`) |
| 3 | `extraction_revisions` | GLOBAL-repo | `writer.rs:1550-1568` (`insert_revision`); counts UPDATE `writer.rs:1656` | E-1: `started_at`, `completed_at`, `input_root` differ across roots. `input_root` stores the **absolute** root |
| 4 | `revision_file_changes` | GLOBAL-repo | `writer/rows.rs:416-425`; `writer.rs:1801` | Keyed on the global `revision_id` counter. Live artifact: 1,596 rows across 182 revisions |
| 5 | `files` | **MUTATED** | insert `writer/rows.rs:396-414`; update `writer/rows.rs:438-468` | E-1: `indexed_at` is the ONLY differing column. E-2: 1,417 files carry 88 distinct `indexed_at` / `last_revision_id` values |
| 6 | `symbols` | **PURE\*** | insert `writer/rows.rs:481-527`; parent UPDATE `writer/rows.rs:529-544` | E-3: byte-identical when an unrelated file is added. E-2: 0 of 103,584 linked parents are cross-file. E-4: `metadata_json` key order unstable |
| 7 | `symbol_annotations` | PURE | `writer/rows.rs:546-568` | E-2: 0 of 5,934 rows reference a symbol outside their own file |
| 8 | `reference_sites` | PURE | `writer/rows.rs:671-838` (`INSERT OR IGNORE`); guard trigger `schema.rs:340-363` | E-2: 0 of 477,002 linked `containing_symbol_id` cross-file. ID embeds `file_id` (`extraction.rs:981-1001`), so cross-file collision is impossible |
| 9 | `identifiers` | **MUTATED** | insert `writer/rows.rs:840-876`; **write-back** `resolution_store.rs:295-298`, `:322-325`, `:580-585`, `:623-630` | E-3: differs when an unrelated file is added; **differs in exactly one column**, `target_symbol_id`. `metadata_json` here is byte-stable (built from an ordered `serde_json::Map`, `extraction.rs:463`) |
| 10 | `relationships` | PURE\* | `writer/rows.rs:878-908`; presence gate `writer/rows.rs:947-953` | E-2: 0 of 17,161 rows have a cross-file `to_` or `from_symbol_id`. Cross-file edges live in `pending_*`. E-4: 318 of 464 metadata rows are multi-key |
| 11 | `pending_relationships` | PURE | `writer/rows.rs:910-945`; presence gate `:955-960` | E-2: 0 of 86,974 cross-file `from_` / `caller_scope`. E-3: byte-identical when an unrelated file is added |
| 12 | `pending_resolutions` | GLOBAL-repo | `resolution_store.rs:222-251`, `:499-530` | E-2: 8,949 of 10,395 targets (86%) are cross-file. E-3: row count changed 4 → 2 when one unrelated file was added |
| 13 | `identifier_resolutions` | GLOBAL-repo | `resolution_store.rs:259-300`, `:574-586`, `:588-631` | E-2: 380,720 rows = one per identifier; 156,953 `resolved`. E-3: outcomes flip when an unrelated file is added |
| 14 | `type_facts` | PURE | `writer/rows.rs:962-985` | E-2: 0 of 49,859 cross-file symbol links |
| 15 | `type_argument_usages` | PURE | `writer/rows.rs:987-1008` | E-2: 0 of 7,319 cross-file. Keyed through `IdentifierLookup::from_file` (`rows.rs:1623-1631`) — file-local by construction |
| 16 | `type_arguments` | PURE | `writer/rows.rs:1010-1030` | Keyed through `TypeArgumentUsageLookup::from_file` (`rows.rs:1643-1651`) — file-local by construction |
| 17 | `literals` | PURE | `writer/rows.rs:1032-1059` | E-2: 0 of 50 cross-file |
| 18 | `source_regions` | PURE\* | `writer/rows.rs:1061-1103`; multi-row `:1331` | E-2: 0 of 166,884 cross-file. E-4: 712 of 780 metadata rows are multi-key |
| 19 | `structural_facts` | **PURE\*** | `writer/rows.rs:1105-1357`; multi-row `:1320` | E-2: 0 of 55,109 cross-file. E-4: **all** 60,143 metadata rows are multi-key |
| 20 | `complexity_metrics` | PURE | `writer/rows.rs:1359-1408`; multi-row `:1342` | E-2: 0 of 12,284 cross-file. E-4: 0 multi-key metadata rows — unaffected |
| 21 | `parse_diagnostics` | PURE | `writer/rows.rs:1410-1441`; replace `:470-479` | Only FK is `file_id`. No symbol link |
| 22 | `language_capabilities` | GLOBAL-fingerprint | `writer/capabilities.rs:161-222` | E-5: all `actual_*` are 0/1 capability flags, not repo counts; every value is `1` on the live artifact. Source is the compiled-in `capabilities.json` |
| 23 | `language_capability_fixtures` | GLOBAL-fingerprint | `writer/capabilities.rs:224-245` | Same source; 211 rows describe the extractor's own fixture suite |
| 24 | `language_capability_gaps` | GLOBAL-fingerprint | `writer/capabilities.rs:247-280` | Same source; 128 rows |

Trigger `reference_sites_identity_guard` (`schema.rs:340-363`) is first-write-wins arbitration **within one
file's passes**; it cannot couple two files because the site ID folds in `file_id`.

### The whole-artifact `SymbolLookup` — a latent global coupling, empirically inert

Every child-row insert filters its symbol FKs through `SymbolLookup`
(`writer/rows.rs:1444-1453`, `valid_symbol_id` at `:1611-1616`), and that lookup is
**local symbols ∪ symbols already in the DB** (`load_symbol_lookup_for_requested_ids`,
`writer/rows.rs:1558-1577`, DB join at `:1590-1595`). So in principle a child row's FK — and for
`relationships`, its very existence (`relationship_is_insertable`, `writer/rows.rs:947-953`) — depends on the
rest of the repository.

Empirically it does not, for any table except `identifiers`. Every cross-file count in the table above is
**zero**, over 703,000 linked rows on the live artifact. That is structural, not luck: cross-file targets are
routed to `pending_relationships` (which requires only `from_symbol_id`, `writer/rows.rs:955-960`) and resolved
into the `pending_resolutions` overlay, where 86% of targets are indeed cross-file. The extraction tables never
carry a cross-file edge.

**Consequence for v4:** the coupling is real in code and must not be relied on staying inert. A store that
extracts one file in isolation and expects a byte-identical blob needs the lookup restricted to the file's own
symbols for these tables — which is what the data says it already resolves to.

---

## 3. Purity violations and the v4 surgery

### V-1 — `identifiers.target_symbol_id` (the one that matters)

**Write sites (all in `crates/julie-extract-artifact/src/resolution_store.rs`):**

- `resolution_store.rs:295-298` — `record_identifier_outcome` single-row denorm write.
- `resolution_store.rs:322-325` — `demote_identifier` clears the column.
- `resolution_store.rs:580-585` — batched demote clears the column for a chunk.
- `resolution_store.rs:623-630` — batched upsert re-reads the overlay via a correlated subquery and writes the
  column for a chunk. *(This is the site the contract input located as "the `:571` area".)*

There is also an insert-time bind of an extractor-supplied target, filtered by the whole-artifact lookup:
`writer/rows.rs:852` (`let target = valid_symbol_id(symbol_lookup, identifier.target_symbol_id.as_deref())`).
That path is **fully overwritten** by the resolver: the live artifact has an overlay row for every one of the
380,720 identifiers and **0 disagreements** between `identifiers.target_symbol_id` and
`identifier_resolutions.target_symbol_id`. The column is 100% overlay-derived today.

**Empirical proof of impurity (E-3):** scanning a 4-file fixture, then re-scanning it with **one unrelated file
added** (`src/other.cs`, `namespace Other { public class Widget { } }`):

```
identifiers, ALL columns identical:            False
identifiers, MINUS target_symbol_id identical: True
  differing column: target_symbol_id
```

The affected rows in `src/app.cs`, which was not touched:

```
  A=('Render','resolved','015ea0c42885a26b26147091c59bb00c',None)   D=('Render','missing',None,None)
  A=('Widget','resolved','e9d8be13b0b537f6474badf2e087358b',None)   D=('Widget','ambiguous',None,2)
```

**v4 surgery:** strip `target_symbol_id` from the `identifiers` table entirely; it lives only in
`identifier_resolutions` (view overlay). Drop `idx_identifiers_target`. Miller's reader
`src/Miller.Indexing/SqliteSymbolGraphIndex.cs:295` drops the `COALESCE(i.target_symbol_id, ir.target_symbol_id)`
to a plain `ir.target_symbol_id` join. Delete the four write sites above; `record_identifier_outcome` and the
batched flush write only the overlay row.

### V-2 — `files.indexed_at`

**Write sites:** `writer/rows.rs:408` (bind in `insert_file_row`), `writer/rows.rs:450` (bind in
`update_failed_preserved_file`). Value origin: `crates/julie-extract-cli/src/commands.rs:335`
(`let indexed_at = now_rfc3339()`).

**Empirical proof (E-1/E-2):** `indexed_at` is the only column of `files` that differs between two scans of
identical content; the live artifact carries 88 distinct values across 1,417 files.

**v4 surgery:** move `indexed_at` out of the shared file row into the per-view manifest row (when this view
last observed this version). The blob keeps `file_id, path, language, content_hash, content_bytes, line_count,
metadata_json`.

### V-3 — `files.last_revision_id`

**Write site:** `writer/rows.rs:409` (binds the caller's `revision_id`), `writer/rows.rs:451` (same, in the
preserved-failure update).

**Evidence:** bound to the global `extraction_revisions` counter, whose value depends on scan history, not on
file content. 88 distinct values on the live artifact.

**v4 surgery:** same as V-2 — a manifest/view-level column, not a shared file-row column.

### V-4 — `files.status`

**Write sites:** `writer/rows.rs:410` and `:462`. `FileStatus::FailedPreserved`
(`writer/rows.rs:427-436`) records "extraction failed, the prior rows were preserved" — a property of the scan,
not of the content.

**Evidence:** all 1,417 live rows are `indexed`, so this is latent rather than active. It is listed because a
store that assumes `files` is pure would silently key a blob on a transient failure state.

**v4 surgery:** keep `status` per view (it describes what a scan did), or promote `FailedPreserved` to an
explicit non-blob condition so a failed extraction never produces a content-addressed blob at all.

### V-5 — the `SymbolLookup` global coupling (latent)

**Write sites:** `writer/rows.rs:1558-1577` (`load_symbol_lookup_for_requested_ids`), consumed at
`writer/rows.rs:852`, `:918`, `:951-952`, `:553`, `:690-718`, and every other `valid_symbol_id` call.

**Evidence:** empirically inert (all cross-file counts zero, §2) but structurally repo-scoped.

**v4 surgery:** for the extraction tables, restrict the lookup to the file's own symbols
(`collect_file_symbol_ids`, `writer/rows.rs:1469-1471`) and drop the DB-join branch. Cross-file targets already
belong to `pending_relationships` / the overlay.

---

## 4. NEW BLOCKER — `metadata_json` bytes are nondeterministic

This was not in the program doc and is the most consequential finding of this audit.

**Root cause:** the extractor models row metadata as
`pub metadata: Option<HashMap<String, serde_json::Value>>` on seven structs —
`crates/julie-extractors/src/base/types.rs:78` (`SourceRegion`), `:124` (`StructuralFact`), `:178`
(`ComplexityMetric`), `:294` (`Symbol`), `:449` (`Relationship`), `:487` (`TypeInfo`), `:496`
(`SymbolOptions`) — serialized verbatim by `optional_json`
(`crates/julie-extract-cli/src/extraction.rs:948-956`, called at `:348, 614, 738, 842, 868, 898`). Rust's
`HashMap` uses a per-process randomized hash seed, so `serde_json` emits the keys in a different order on
every run.

**Not affected: `identifiers`.** Its metadata is built as a `serde_json::Map`
(`crates/julie-extract-cli/src/extraction.rs:463`, emitted at `:500`), and with no `preserve_order` feature
enabled `serde_json::Map` is a `BTreeMap` — key-sorted and therefore byte-stable. The fixture confirms
`identifiers.metadata_json` is identical across scans, though its 2 metadata rows were single-key, so the
determinism claim for this table rests on the map type, not on the fixture.

**Empirical proof (E-4):** two scans of the *same* root, same content, same binary:

```
same-root scan1 vs scan2 identical: False
same-root scan2 vs scan3 identical: False
non-metadata symbol columns identical (a vs a2): True
symbols with differing metadata_json bytes (same root, two scans): 6 of 17
of those, semantically different: 0
```

Every other column of `symbols` is byte-stable. Only the JSON key order moves. Example:

```
A: {"parameters":[],"returnType":": number","typeParameters":[],"isGenerator":false,"isStatic":false,"isAsync":false}
B: {"isAsync":false,"isStatic":false,"isGenerator":false,"parameters":[],"typeParameters":[],"returnType":": number"}
```

**Blast radius on the live Miller artifact** — HashMap-backed tables only, counting rows whose `metadata_json`
has ≥2 top-level keys (a 1-key object is order-stable, so only multi-key rows are unstable):

| table | rows with metadata | multi-key rows | distinct files made unstable |
|---|---:|---:|---:|
| `symbols` | 122,700 | 59,863 | 1,229 |
| `structural_facts` | 60,143 | **60,143** | 652 |
| `source_regions` | 780 | 712 | 180 |
| `relationships` | 464 | 318 | 2 |
| `complexity_metrics` | 13,100 | 0 | 0 |
| `type_facts` | 0 | 0 | 0 |
| **union** | 197,187 | 121,036 | **1,397** |

(`identifiers` is excluded — 118,391 metadata rows, 8,727 of them multi-key, all byte-stable per the map-type
argument above. Had it been HashMap-backed the file count would rise to 1,400.)

`complexity_metrics` and `type_facts` show zero unstable rows on this artifact, but both are HashMap-backed in
code (`types.rs:178`, `types.rs:487`). They are not immune — they simply emit no multi-key metadata for the
languages in this repo. Treat the fix as covering all seven structs, not the four with observed damage.

**1,397 of 1,417 files (98.6%)** carry at least one byte-unstable row. Under today's serializer, a
content-addressed store would compute a different blob hash for 98.6% of files on every single scan —
**dedup hit rate ≈ 0%**, and the store would grow without bound.

**Fix:** change the seven `HashMap<String, serde_json::Value>` declarations to `BTreeMap`, or serialize through
a sorted-key writer. Nested `serde_json::Value::Object` is already a `BTreeMap` (no `preserve_order` feature is
enabled — `serde_json = "1.0"` plain in `crates/julie-extractors/Cargo.toml:73`,
`crates/julie-extract-cli/Cargo.toml:59`, `crates/julie-extract-artifact/Cargo.toml:23`), so only the outer map
is at fault. This is a one-commit change in julie-extractors plus a determinism regression test
(scan twice, assert byte-identical extraction tables).

**Ph0 recommendation:** treat byte-determinism as a hard prerequisite of the store, with its own gate — a
"scan the same tree twice, every extraction table byte-identical" test that runs in julie-extractors CI. The
purity property is worth nothing to a content-addressed store without it.

---

## 5. Corrected pure-vs-global byte split (dbstat arithmetic)

**Artifact:** `/Users/murphy/source/miller/.miller/symbols.db`, opened `mode=ro`.
`PRAGMA page_size = 4096`, `PRAGMA page_count = 197449`, `PRAGMA freelist_count = 0`.
`197449 × 4096 = 808,751,104` bytes, which equals `SUM(pgsize)` over `dbstat` and the on-disk file size — so
the accounting below covers 100% of the artifact with no unattributed pages.

Query (indexes attributed to their parent table via `sqlite_master.tbl_name`):

```sql
WITH obj AS (
  SELECT d.name AS obj_name, SUM(d.pgsize) AS bytes,
         COALESCE((SELECT m.tbl_name FROM sqlite_master m WHERE m.name = d.name), d.name) AS tbl
  FROM dbstat d GROUP BY d.name
)
SELECT class, SUM(bytes), ROUND(100.0*SUM(bytes)/(SELECT SUM(pgsize) FROM dbstat),2) FROM (...) GROUP BY class;
```

**As-shipped, by object:**

| class | bytes | % |
|---|---:|---:|
| PURE per-file (tables + their indexes) | 728,358,912 | 90.06 |
| GLOBAL overlay — `identifier_resolutions`, `pending_resolutions` + indexes | 69,509,120 | 8.59 |
| `idx_identifiers_target` (index over the denormalized column) | 10,170,368 | 1.26 |
| GLOBAL revision — `extraction_revisions`, `revision_file_changes`, `artifact_metadata` + indexes | 405,504 | 0.05 |
| GLOBAL fingerprint — `language_capabilities*`, `parser_inventory` + indexes | 274,432 | 0.03 |
| `sqlite_schema` | 32,768 | 0.00 |
| **total** | **808,751,104** | **100.00** |

**After the V-1 surgery** the denormalized column's payload moves from the pure side to the resolution layer.
Payload measured directly: `SELECT SUM(LENGTH(target_symbol_id)+1) FROM identifiers WHERE target_symbol_id IS
NOT NULL` = **5,179,449** bytes over 156,953 non-null rows. (This is cell payload, not page-accurate; the true
page saving is slightly larger because of per-cell overhead, and is not realized until a vacuum/rebuild. It is
the conservative direction for the pure share.)

```
pure       = 728,358,912 − 5,179,449              = 723,179,463   (89.42%)
resolution =  69,509,120 + 10,170,368 + 5,179,449 =  84,858,937   (10.49%)
other      =     405,504 +    274,432 +    32,768 =     712,704   ( 0.09%)
                                                    -----------
                                                    808,751,104   (100.00%)
```

**Correction to the program doc.** `docs/plans/2026-08-06-index-store-views-program.md` §"Resolution sharing is
v1-required" states "The resolution layer is ~12% of store bytes … 8 views cost `0.88 + 8×0.12 ≈ 1.84×`".
Measured: the resolution layer is **10.49%**, the pure share **89.42%**. Redoing the arithmetic with real
numbers: `0.8942 + 8 × 0.1049 = 1.73×`. **The conclusion is unchanged** — private per-view resolution copies
blow the ≤1.2× criterion by a wide margin — but the hypothesis was conservative by ~1.5 points and should be
restated as measured.

Two notes on how to read this split:

- The "other global" 0.09% is not per-view cost at all. 274,432 of those bytes are GLOBAL-fingerprint: identical
  for every workspace scanned by the same binary, so a store holds one copy for the whole fleet.
- The mutated `files` columns are negligible: the entire `files` table is 352,256 bytes (0.04%), of which
  `indexed_at` payload is 38,358 bytes. V-2/V-3 cost nothing to move.

---

## 6. P1a status — definitive

### Landed: YES, in julie-extract **v2.27.0**

Commit `bbbdce2cff2498bb24dc1a4631c60a89e2fe926b`, *"perf: let a whole-repo scan scope resolution when no path
moved"*, Wed 2026-08-05. `git tag --contains bbbdce2c` → `v2.26.0`, `v2.27.0`. The live Miller artifact already
reports `artifact_metadata.binary_version = 2.27.0`, so Miller is running the delta-scoped resolver today.

The P1a background doc (`docs/plans/2026-08-02-worktree-delta-rebind-program.md` §P1a) recorded both whole-repo
write sites as hard-coded `is_full_scan: true` at `writer.rs:1087` and `:1390`. Current state:

| site (current line) | function | current value |
|---|---|---|
| `writer.rs:1421` (was `:1390`) | `write_scan_spooled_snapshot_in_mode` | `is_full_scan: structure_changed \|\| revision.mode == Some(WriteMode::Force)` — **delta-scoped** |
| `writer.rs:1099` (was `:1087`) | `write_scan_snapshot_in_mode` | `is_full_scan: true` — **still hard-Full** |

The remaining hard-Full site is **not reachable from the production scan path**. The CLI `scan` verb calls only
`write_scan_spooled_preserving_missing_paths_with_resolution`
(`crates/julie-extract-cli/src/commands.rs:429`), which routes to the spooled path:
`writer.rs:520-547` → `write_scan_spooled_snapshot` (`writer.rs:1137`) →
`write_scan_spooled_snapshot_in_mode` (`writer.rs:1178`), whose scope is the delta-scoped one at `:1421`. The
in-memory `write_scan` entry (`writer.rs:443-467`) is a library/test surface with no CLI caller.
**P1a is fully landed for every scan Miller performs.**

### Mechanism entry points

| concern | location |
|---|---|
| Scope contract handed to the resolver | `ResolutionScopeInput`, `crates/julie-extract-artifact/src/writer.rs:181-187` |
| The two flags' distinct meanings (do not conflate) | `writer.rs:164-180` — `is_full_scan` = "re-derive the whole overlay" (dispatch switch); `whole_corpus` = "this write hash-checked every file" (what `status` / `last_full_revision` report on) |
| Dispatch on the flag | `crates/julie-extract-cli/src/resolution.rs:1644` — `let requested_full = scope.is_full_scan \|\| prior.is_none();` |
| Resolver epoch | `RESOLUTION_VERSION = 6`, `crates/julie-extract-cli/src/resolution.rs:1502` (live artifact: `reference_resolution_version = 6`) |
| Promote-to-full crossover | `DELTA_SCOPE_CROSSOVER = 0.7`, `crates/julie-extract-cli/src/resolution.rs:2674`. Per the commit message the crossover decision lives in `run_resolution`, not the writer, because `scope_files` does not exist until `delta_scope_files` has run |
| Overlay write primitives (the only sanctioned path) | `crates/julie-extract-artifact/src/resolution_store.rs:216-327` (single-row) and `:339-633` (batched flush) |
| When a whole-repo scan still forces Full | `writer.rs:1416-1421` — a path appeared or vanished (`structure_changed`), or `WriteMode::Force`. `prior.is_none()` forces Full inside the hook |

### Equivalence gate: EXISTS and PASSES

**Test file:** `crates/julie-extract-cli/tests/resolution_scope_equivalence.rs` — 9 tests.

**Run (this audit, 2026-08-06):**

```
cd /Users/murphy/source/julie-extractors
CARGO_TARGET_DIR=/tmp/ph0-purity-target cargo +1.97.1 test -p julie-extract-cli \
  --test resolution_scope_equivalence
```

(The workspace requires rustc ≥ 1.95; the machine default is 1.94, so the installed 1.97.1 toolchain was used.
`CARGO_TARGET_DIR` was redirected out of the checkout — **no build artifact was written into
julie-extractors**, honoring the read-only constraint.)

```
running 9 tests
test deleting_a_same_name_shadow_file_matches_a_full_rederivation ... ok
test aliased_import_filled_by_a_delta_matches_a_full_rederivation ... ok
test a_shadow_file_with_disjoint_exports_matches_a_full_rederivation ... ok
test deleting_a_disjoint_shadow_file_matches_a_full_rederivation ... ok
test restored_receiver_type_uniqueness_matches_a_full_rederivation ... ok
test receiver_type_ambiguity_demoted_by_a_delta_matches_a_full_rederivation ... ok
test module_shadowing_applied_by_a_delta_matches_a_full_rederivation ... ok
test shadowing_then_unshadowing_converges_to_a_full_rederivation ... ok
test a_multi_step_edit_sequence_matches_a_full_rederivation ... ok

test result: ok. 9 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 2.00s
```

The multi-step case `a_multi_step_edit_sequence_matches_a_full_rederivation`
(`resolution_scope_equivalence.rs:311-338`) is the N-incremental-steps case specifically: scan, then three
successive `update` calls (add, rewrite-removing-a-symbol, re-add), then assert equivalence.

**Caveat the gate's own header states, and Ph0 must not gloss over.** The oracle is a full **re-derivation over
the artifact's existing rows** — copy the DB, wipe the overlay, re-resolve at `is_full_scan: true`, compare —
**not** a from-scratch scan of the final tree. The module doc explains why
(`resolution_scope_equivalence.rs:5-15`): a fresh scan is a different artifact, because a relationship row in an
unchanged file whose target symbol died is FK-cascaded away and never re-extracted, so the two artifacts differ
in rows that have nothing to do with resolution scope.

So what is proven is: **the overlay is scope-independent given a fixed row set.** What is *not* proven is that
the incremental **row set** equals the from-scratch row set. For the content-addressed store that gap is
narrower than it sounds — §2 shows the extraction tables carry no cross-file edges, so an FK cascade cannot
reach across a file boundary in the extraction layer — but the store's own equivalence gate should close it
explicitly rather than inherit this one's scope.

---

## 7. Method and reproduction

### Evidence codes

- **E-1** — two roots, identical content. `/tmp/ph0-fix/wtA` and `/tmp/ph0-fix/wtB` (byte-identical 4-file
  tree: TS with cross-file imports, C# with a receiver-typed member call, a Markdown doc), each scanned into its
  own DB, every table compared column by column.
- **E-2** — direct queries against the live Miller artifact (`mode=ro`), mostly cross-file FK share.
- **E-3** — contamination test: `/tmp/ph0-fix/wtD` = wtA plus one unrelated file
  (`src/other.cs`, `namespace Other { public class Widget { } }`), comparing only rows belonging to wtA's
  original four paths.
- **E-4** — determinism test: the same root scanned three times into three DBs.
- **E-5** — semantics check on `language_capabilities` values.

The binary used for E-1/E-3/E-4 is the debug build produced by the gate run at `/tmp/ph0-purity-target/debug/julie-extract`
(v2.27.0, `ab7b16a`).

### Constraint compliance

- julie-extractors was **read-only**: only `git log` / `git show` / file reads / `rg`. The one build wrote to
  `/tmp/ph0-purity-target` via `CARGO_TARGET_DIR`; `git status` in that checkout stays clean.
- Every Miller-artifact read used `file:/Users/murphy/source/miller/.miller/symbols.db?mode=ro`. No writable
  open, and this worktree's own `.miller` was never touched.
- All fixtures live under `/tmp/ph0-fix`, outside both repositories.

### Open items for the Ph0 go/no-go

1. **Byte-determinism is a hard prerequisite**, currently unmet (§4). It gates the store's central claim more
   sharply than purity does. Small fix, but it must land and be gated before any dedup measurement is credible.
2. **`identifiers.target_symbol_id` surgery is a cross-repo change**: schema + resolver in julie-extractors,
   reader in Miller (`SqliteSymbolGraphIndex.cs:295`). Sequence it so Miller's `COALESCE` fallback survives one
   extractor version, or the two land together.
3. **The `SymbolLookup` DB-join branch (V-5)** should be narrowed for the extraction tables before a store
   extracts files in isolation, even though it is empirically inert today.
