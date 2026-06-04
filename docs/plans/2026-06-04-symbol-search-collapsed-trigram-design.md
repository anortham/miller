# Symbol search: on-disk FTS5 collapsed-trigram index — design

- **Date:** 2026-06-04
- **Status:** Phases 0–5 implemented 2026-06-04 — the on-disk `search.db` sidecar is **on by default** (opt out
  with `MILLER_SEARCH_SIDECAR=0`). BOTH writer paths wired (indexer leader + `CrossWorkspaceRefreshService`); the
  Phase-5 recall eval CLEARED (interior recall 0.000→0.763, zero word-class regression, word-arm ranking parity
  exact 0/521) so the flag was flipped on; the diacritics parity caveat is closed (`remove_diacritics 0`,
  `SchemaVersion` 1→2) and a "disk path taken" telemetry counter added. (TDD, fast suite 1434 green / 0 warnings,
  scale 22/22; routing, both writers, and Phase 5 adversarially reviewed.) (FTS5-first; Codex-reviewed 2026-06-04.)
- **Origin:** codenav trigram experiment (`~/source/codenav/docs/plans/2026-06-04-trigram-symbol-search-design.md`)
- **Aligns with:** `docs/plans/2026-06-04-free-core-boundary-and-aot-release.md` (Eros consumes Miller's data),
  `docs/findings/gemini-search-suggestion.md` (3-pillar search), `docs/plans/2026-06-02-search-projections-design.md`

## Practical Answer

Build symbol search as an **on-disk, Eros-shareable search artifact** — a Miller-owned sidecar SQLite DB
(`<workspace>/.miller/search.db`) with a **stable schema contract**, not an in-memory map. FTS5 is the
*internal* mechanism for substring discovery inside that artifact; the durable thing Eros depends on is
the schema, not our use of FTS5. (Framing from the Codex review — it keeps us free to change the lexical
engine later without breaking Eros.)

Two reasons, in order:

1. **Eros can build on it.** The free-core plan says Eros consumes Miller's data directly and "must not
   depend on private .NET types or internal indexes." An in-memory index is a private internal index —
   Eros can't read it. An on-disk SQLite table is shareable data. **This is the deciding reason.**
2. **It's the foundation for the 3-pillar search roadmap** (graph / embeddings / scope-aware lexical).
   A schema with real metadata columns now means later capabilities are added `WHERE`-clause by new-table,
   not by a rewrite.

FTS5 is **not** chosen for speed — search is already fast (~1.8s/+55MB on OpenClaw via the shipped
projection split, not the 6.7s/+582MB full-index cliff). Speed isn't the driver; **shareability is.**

Plain-English glossary: *FTS5* = SQLite's built-in full-text search. *Trigram* = 3-letter chunks, so a
fragment matches the middle of a name. *Collapsed name* = an identifier with separators stripped
(`format_external_extract` → `formatexternalextract`). *AST* = the code's structure tree from
tree-sitter (via julie-extract).

## What exists / what's missing / what's next

| Pillar (from gemini-search-suggestion.md) | What it is | Status in Miller |
|---|---|---|
| 1. Code graph | precise structural queries ("who implements X", "callers of Y") | **Exists** — symbol graph + trace/impact/references over julie's `symbols.db` |
| 2. Meaning-based search | embeddings over AST-bounded chunks | **Missing by design** — this is Eros's commercial layer; free-core keeps embeddings out |
| 3. Scope-aware lexical | keyword/trigram search tagged with code structure ("`TODO` only in comments") | **Missing** — this design lays its foundation |

Next: ship pillar-3's *foundation* (this doc) without blocking pillars 1 and 2.

## Verified Miller baseline (source-confirmed)

- Symbol search today = in-memory BM25 (`src/Miller.Core/Search/MillerSearchIndex.cs`), exact
  token-**equality** match (`FrozenDictionary`, `StringComparer.Ordinal`). No substring/ngram/fuzzy.
- Indexes only `Name + " " + Signature` text. **No qualified-name** text.
- Tokenizer (`CodeTokenizer.cs`) splits CamelCase + snake_case into components and emits a whole-run
  token — **but only for delimiter-free runs**, so `format_external_extract` never produces a
  `formatexternalextract` key (snake/camel asymmetry).
- One rule on BM25 (k1=1.2, b=0.75): a 1.5× boost when the query equals `doc.Name`. Modes: `Or`, `And`.
- Search path uses the lean `SymbolSearchProjection` (not the full `RepositoryIndexLoader.Load`).
- Two build sites funnel through `MillerSearchIndex.Build`/`Search`.

### Recall gap this closes

| Query | Example | Today | After |
|---|---|---|---|
| whole component | `provider` → `IAuthenticationProvider` | ✅ | ✅ (regression check) |
| interior substring | `thenti` → `…Authentication…` | ❌ | ✅ |
| boundary-crossing | `tionprov` → `IAuthenticationProvider` | ❌ | ✅ |
| exact name | full name | ✅ | ✅ (regression check) |
| snake_case interior | `external` in `format_external_extract` | ⚠️ component-only | ✅ uniform |

Evidence (codenav, external): collapsed-trigram recall is a strict superset of word recall with **zero
ranking regression** across 6 languages; Miller's own C# corpus measured interior recall **0.57 → 0.84**.

## Design

### Storage: a Miller-owned sidecar `<workspace>/.miller/search.db`

- **Not** inside julie's `symbols.db` — julie replaces that file (new inode) on every scan, which would
  destroy the FTS tables. Miller already owns read-write SQLite DBs (`telemetry.db`, `workspaces.db`);
  this follows that precedent.
- Built **read-write** by the workspace's own indexer; opened **read-only** by readers and by Eros.
- Keyed to the julie-extract **revision** so staleness is a cheap integer compare; rebuilt on revision bump.
- Self-contained: it carries the metadata it needs so Eros (and scope filtering) can query it **without
  Miller's process and without julie's `symbols.db`**. Join key everywhere is julie's stable `symbol_id`.

### Schema (built for the 3 pillars from day one)

```sql
-- Recall arm 1 (word/component): store the EXACT CodeTokenizer token stream as the body, space-separated
-- and INCLUDING DUPLICATES (term frequency + doc length depend on multiplicity). Tokens are pre-split and
-- alphanumeric, so FTS5 only re-splits on the spaces we inserted — it reproduces our tokens exactly. Do
-- NOT rely on a built-in tokenizer to do the code-aware splitting; that diverges from today's recall.
CREATE VIRTUAL TABLE symbols_fts USING fts5(symbol_id UNINDEXED, body);

-- Recall arm 2 (interior substring) over the COLLAPSED form (the codenav win)
CREATE VIRTUAL TABLE symbols_trigram USING fts5(
    symbol_id UNINDEXED, name_collapsed, qual_collapsed, tokenize='trigram');

-- Self-contained, STABLE artifact contract: candidate filtering + Eros queries + AST chunk boundaries.
CREATE TABLE search_symbols(
    symbol_id        TEXT PRIMARY KEY,   -- julie's stable id; join key for Eros + the graph (pillar 1)
    name TEXT, kind TEXT, language TEXT,
    path TEXT,
    start_line INT, end_line INT,
    start_byte INT, end_byte INT,        -- byte spans for AST-bounded embedding chunks (pillar 2)
    parent_symbol_id TEXT,               -- hierarchy for chunk context (pillar 2)
    is_test INT,
    doc_len INT);                        -- token count of the word body (BM25 length norm)
CREATE INDEX ix_search_symbols_kind ON search_symbols(kind);
CREATE INDEX ix_search_symbols_lang ON search_symbols(language);

-- Freshness + corpus-wide BM25 constants only. Per-term document-frequency is read from FTS5's `fts5vocab`
-- at query time; per-candidate term-frequency + doc-len are recomputed in C# from the resident symbol.
CREATE TABLE meta(revision INT, doc_count INT /* N */, avgdl REAL, schema_version INT);
```

**Why these columns are the foundation, not gold-plating:**
- `symbol_id` → joins back to julie's relationship graph (pillar 1); we don't duplicate the graph.
- `start_byte` / `end_byte` / `parent_symbol_id` (+ line/path) → the exact AST boundaries + hierarchy
  Eros needs for AST-bounded embedding chunks (pillar 2). We store them; Eros uses them. **Note:** julie
  emits byte spans but Miller's current lean reader drops them — the writer must read and persist them.
- `kind` / `language` → let symbol results be *filtered* by scope. This is **not** full scope-aware
  lexical search ("`TODO` only in comments") — that needs the reserved `source_regions` table below.
  These columns make that additive; they don't deliver it.

**Reserved for later (do not build now, but the schema makes it additive):** a region-typed trigram
table over julie-extract v2.1.0's `source_regions` (comments / doc-comments / string-literals /
embedded-language spans). That is what turns "search only in comments" / "exclude string literals" on.
This is already Miller's "consume next" item — this schema lays its track.

### Query + rank flow (ranking stays in Miller's C#)

FTS5 does **recall only**; Miller keeps **ranking authority**. Eros gets the shareable tables; it doesn't
inherit our scorer unless it wants to.

1. `SearchTool.Run` → `WorkspaceSymbolSearchContext.Index` (already typed `ISymbolLookupIndex` — clean seam).
2. New `FtsSymbolSearchIndex` opens `search.db` read-only. Per query:
   - Tokenize with the same `CodeTokenizer`; compute the collapsed form.
   - **Word arm (parity-critical): do NOT cap it.** Fetch *all* docs matching the query terms (strict
     `AND`, broad `OR` fallback when under-filled) — this is exactly the set the in-memory index would
     score, so re-ranking it reproduces today's top-N. No FTS-rank `LIMIT` on this arm (an FTS-rank cut
     could strand a result C# would have ranked higher).
   - **Trigram arm (additive recall): windowed.** Require **all** ≥3-char query trigrams (`AND`);
     `LIMIT` ~200, since it is pure extra substring recall. Skip entirely for <3-char queries (word-only).
   - `UNION` candidates by `symbol_id` → resident `IndexedSymbol`.
   - **Filters stay in C#.** The `ISymbolSearchIndex.Search(query, limit, mode)` interface carries no
     filter args today and `SearchTool` filters tests *after* the search — so apply `exclude_tests`/kind
     post-fetch with conservative overfetch, as now. (Pushing filters into SQL means widening the search
     interface — a later option, not v1.)
   - Re-rank with Miller's **unchanged** BM25 + 1.5× exact-name boost. Stats: `N`/`avgdl` from `meta()`,
     per-term **DF from `fts5vocab`**, per-candidate **TF + doc-len recomputed in C#** by re-tokenizing
     the resident symbol. Trigram-only hits floored below word hits; excluded under `And`.
3. `index.Resolve(docId)` returns the full symbol from the resident `IndexedSymbol[]` (unchanged tool surface).

### Self-heal

If `search.db` is missing / stale-revision / schema-incompatible / FTS5 unavailable, fall back to the
current in-memory `SymbolSearchProjection` and (leader only) trigger a rebuild — mirrors the existing
auto-heal posture. Correctness never depends on the sidecar. **Guard:** a Scale test must assert the disk
path is taken (the fallback silently reverts to the slow path and would pass functional tests).

## Writer lifecycle (resolved by the Codex review)

The sidecar must be written **only by a legitimate writer holding the workspace `SingleWriterLock`**, and
swapped in atomically so a reader never sees a half-built DB.

- **Build under the lock, swap atomically:** write a temp DB, then atomic-replace `search.db`; readers
  compare `meta.revision` before trusting it. Note bootstrap currently *scans before* taking the writer
  lock (`IndexBootstrapService.cs:121` vs `IndexerService.cs:121`), so the sidecar build must sit behind
  the same writer discipline as scans/updates — it cannot piggyback on the pre-lock scan.
- **External workspaces:** build in `CrossWorkspaceRefreshService` — it already acquires the workspace
  lock *before* scanning (`CrossWorkspaceRefreshService.cs:103`), so it is the one safe writer. It holds
  only an `ExtractReport`, so it does one symbol read to build the sidecar (off the search hot path). This
  resolves the earlier open question — the lazy reader never writes; the lock-holding refresh path does.
- **Current workspace:** build at indexing time under the same writer lock (not the pre-lock bootstrap
  scan).

## Implementation phases

0. ✅ **`CollapseName`** in `Miller.Core.Tokenization` — pure logic, fast-suite tests. Shared by everything.
1. ✅ **`SearchIndexWriter`** (Miller.Indexing) — builds a temp `search.db`, creates the schema, fills both
   FTS tables (word body = exact token stream incl. duplicates) + `search_symbols` (now **incl. `signature`**,
   + byte-span columns reserved/NULL + `doc_len`) + `meta` in one transaction, then **atomic-replaces** the
   live file. Caller holds the workspace `SingleWriterLock` — now wired on BOTH writer paths: the leader
   (`IndexerService`, current workspace) and the external refresh (`CrossWorkspaceRefreshService`).
2. ✅ **`FtsSymbolSearchIndex`** (Miller.Indexing, `: ISymbolLookupIndex`) — read-only `search.db` reader;
   uncapped word fetch + windowed trigram fetch (floored below word hits, excluded under AND) + C# re-rank.
   Drops into the existing seam; lookups delegate to the shared `SymbolLookupTables`.
3. ✅ **Route + flag** — branch the loader to the sidecar when present and `meta.revision`-fresh; else
   self-heal to the in-memory projection. Default off until eval clears it.
4. ✅ **External build** — build the sidecar in `CrossWorkspaceRefreshService` (holds the lock before scan;
   one extra symbol read).
5. *(later)* `source_regions` region-typed table → scope-aware lexical (pillar 3 proper); optional
   collapsed qualified-name recall; widen the search interface to push filters into SQL if profiling wants it.

### As built — Phases 0–2 (2026-06-04, TDD)

Done strictly test-first; full fast suite green (1365 tests, 0 warnings); an adversarial multi-lens review
(parity / FTS-SQL / lifetime / contract) found **no code bugs** — only two test-coverage gaps, both since closed.

Decisions and deviations from the doc above:

- **`signature` added to `search_symbols`.** The schema as first written omitted it, but `Resolve` must return
  the full symbol and the artifact is meant to be self-contained (Eros renders results without julie's
  `symbols.db`). One TEXT column, added before the schema shipped. The reader is now a **pure `search.db`
  consumer** — it opens nothing else.
- **DF via `COUNT(*) … WHERE body MATCH "term"`, not `fts5vocab`.** A read-only connection can't `CREATE VIRTUAL
  TABLE … fts5vocab`; the count over a single-term MATCH is the exact document frequency and needs no schema
  change. (If a vocab table is ever wanted, the *writer* would have to materialize it.)
- **TF / doc-len re-tokenized from the resident `Name + " " + Signature`** (not read from the stored body) —
  identical token multiplicities to the writer, so BM25 scores match. `meta.avgdl`/`doc_count` give avgdl/N.
- **Ranking parity is enforced structurally**, not hand-copied: `Bm25` (constants/IDF/term-score/boost) was
  extracted to `Miller.Core.Search` and **both** `MillerSearchIndex` and `FtsSymbolSearchIndex` score through
  it. A fast-suite test asserts the FTS word arm == in-memory `SymbolSearchProjection` (DocId order **and**
  scores to 1e-9) for OR **and** AND queries.
- **DocId reconstruction:** the reader re-derives the 0-based DocId via `ORDER BY path, start_line, symbol_id`
  — byte-for-byte `SqliteSymbolReader`'s order — so DocIds (and thus the tie-break + `Resolve` identity) match
  the in-memory index.
- **Lifetime:** resident snapshot + `meta` loaded once at `Open`; **each query opens a short-lived read-only
  `Pooling=false` connection**, so the reader holds no file handle between queries (no atomic-replace race, no
  lock, thread-safe). Not `IDisposable`.
- **Lookup reuse:** `SymbolLookupTables` (lookup maps without the BM25 postings) was extracted from
  `SymbolSearchProjection`; both backends share it.

Open caveat carried to the **eval (Phase 5)**: FTS5's default `unicode61` folds diacritics, so per-term DF can
drift from the in-memory DF for non-ASCII identifiers with accent collisions (recall stays exact — C#
re-tokenization drops FTS false positives; only scores can shift). ASCII parity is exact and tested. Resolve
before defaulting the sidecar on (e.g. `tokenize='unicode61 remove_diacritics 0'` on `symbols_fts`).

### As built — Phase 3 (2026-06-04, TDD + adversarial review)

Route + flag landed test-first; full fast suite green (1400 tests, 0 warnings), scale suite 21/21. A 5-lens
adversarial review (routing/self-heal, caching/concurrency, OFF-path parity, flag/DI/lifecycle, test quality)
found **one real defect** (since fixed) and **one test gap** (since closed); OFF-path parity and DI lifecycle
were confirmed safe.

- **`SymbolSearchSidecar`** (`src/Miller.Indexing/SymbolSearchSidecar.cs`) — the routing gate. Holds the
  `Enabled` flag (`Disabled` singleton = the default), derives the sibling `search.db` path
  (`SearchDbPathFor`), parses the env flag (`IsEnabledValue`), and `TryOpen(symbolsDbPath, expectedRevision)`
  returns the disk `FtsSymbolSearchIndex` **only** when enabled + present + `meta.revision == expectedRevision`,
  else `null`. **Load-bearing contract: `TryOpen` never throws** — path derivation is inside the guard and the
  catch covers `SqliteException`/`InvalidOperationException`/`IOException`/`UnauthorizedAccessException`/
  `ArgumentException`/`NotSupportedException`, so a missing/stale/corrupt/schema-incompatible/malformed artifact
  (or an unusable path) always degrades to the in-memory path.
- **Flag = env var `MILLER_SEARCH_SIDECAR`, default OFF.** Read once in the composition root
  (`MillerServiceRegistration`) into a `SymbolSearchSidecar` singleton; no bootstrap-getter read at
  construction (host lifecycle contract preserved).
- **Routing in `WorkspaceIndexProvider`.** Both the registered (`_loadSymbolSearch`) and the holder-backed
  current-workspace paths resolve through `_sidecar.TryOpen(...) ?? in-memory`, unified into ONE revision-keyed,
  single-flight, evicting cache (`GetOrLoadSymbolSearch(key, dbPath, fallback)`). Registered fallback = the
  lean `SymbolSearchProjection`; current fallback = the holder's full `MillerRepositoryIndex`. When the flag is
  OFF the current path returns the holder index directly (no cache entry) — **byte-identical to pre-Phase-3**.
- **Revision contract.** The freshness key is julie's `extraction_revisions` cursor:
  `IndexHolder.BuiltRevision` (current) / registry `row.LastRevision` (registered) must equal the `meta.revision`
  the Phase-1/4 writers stamp into `search.db`. Strict equality — a stale OR ahead artifact is rejected.
- **Backend is observable** via the concrete type of `context.Index` (`FtsSymbolSearchIndex` vs in-memory) — no
  schema change to `WorkspaceSymbolSearchContext`; tests and the Phase-5 disk-path Scale assertion check it with
  `IsType`.

Deferred to later phases (NOT Phase-3 gaps): build wiring — **now done** (leader `IndexerService` + external
`CrossWorkspaceRefreshService`); triggering a rebuild on a read-miss (the build path now exists, so this is
unblocked but still deferred); the recall eval + Scale disk-path assertion + telemetry counter (Phase 5); and an
FTS5 build-capability probe so an FTS-less build fails loudly rather than at first MATCH (release-plan scope;
harmless while the flag is OFF).

### As built — Phase 4 (2026-06-04, TDD + adversarial review)

External-workspace writer landed test-first; full fast suite green (1409 tests, 0 warnings), scale 21/21. A
3-lens adversarial review (build/lock/concurrency, best-effort/freshness/failure-modes, wiring/OFF-path/tests)
found **one real defect** (fixed) and **one test gap** (closed); one lens finding was **refuted by execution**
and dropped (see below).

- **`SymbolSearchSidecar.EnsureBuilt(symbolsDbPath, revision)`** — the lock-holding writer's build entry point.
  Cheap freshness gate first (`ReadArtifactRevision`: a one-row `SELECT revision FROM meta`, no resident
  snapshot) so an unchanged refresh never rebuilds a large artifact; on missing/stale/unreadable it reads the
  extract's symbols (`SqliteSymbolReader.Read` — the one extra read the design budgets) and writes via
  `SearchIndexWriter`. Returns whether it (re)built; `false` when disabled or already fresh. MAY throw on a
  genuine build failure (unlike the read gate `TryOpen`).
- **`CrossWorkspaceRefreshService`** — the ONE safe external writer (it takes the workspace `SingleWriterLock`
  before scanning). After `MarkScanned`, **inside the lease scope**, it calls `EnsureBuilt` via `TryBuildSidecar`,
  which swallows build exceptions so a sidecar failure NEVER turns a successful scan into a `Failed` result. Build
  runs on BOTH `Refreshed` and `Unchanged` (so a flag-flip on an already-scanned workspace still builds a missing
  artifact); the freshness gate makes the `Unchanged`-and-already-fresh case a cheap no-op. Lock-busy / missing /
  failed paths never build.
- **Flag-gated + consistent across processes:** the env flag is read in the server composition root AND in
  `DashboardData.RefreshWorkspace` (a dashboard-triggered refresh is also a lock-holding safe writer). Default
  OFF ⇒ no build, byte-identical to pre-Phase-4.

Review notes:
- **Fixed (HIGH):** `ReadArtifactRevision` could let `Convert.ToInt64` throw `FormatException`/`OverflowException`/
  `InvalidCastException` out of the lock-holding writer on a corrupt `meta.revision`. Now caught ⇒ treated as
  "unreadable, rebuild". (Reproduced by a corrupt-revision test, then fixed.)
- **Refuted:** the review also claimed `SqliteSymbolReader.Read`'s `GetInt32`/`GetBoolean` could throw
  `InvalidCastException` on a malformed column and escape `TryBuildSidecar`. Execution showed
  Microsoft.Data.Sqlite **coerces** blob/text numeric reads (no throw), so that path is not reachable;
  `TryBuildSidecar`'s existing filter already covers everything the read/write actually throw. No change made.
- **Closed gap:** added a test that an `Unchanged`-status refresh with the flag on still builds a missing artifact.

### As built — Phase 1 / leader build wiring (2026-06-04, TDD + adversarial review)

The CURRENT workspace's writer. The indexer LEADER (the instance holding the cross-process `SingleWriterLock`)
now (re)builds `<workspace>/.miller/search.db` after its own scans. Landed test-first; full fast suite green
(1414 tests, 0 warnings), scale 21/21. A 4-lens adversarial review (concurrency/locking, revision-correctness,
best-effort isolation, OFF-path parity), refute-by-default, found **0 confirmed defects / 9 refuted**; one test
gap was closed.

- **`IndexerService` injects `SymbolSearchSidecar`** (4th public-ctor param / 7th internal-ctor param, both
  null-checked; DI supplies the same flag singleton the Phase-3/4 consumers read). It builds at exactly the two
  sites where the leader holds the lock AND has a fresh `ExtractReport`: `RunStartupDeltaScan` (after
  `ops.Scan(force:false)`) and `TryScanAsLeader` (the `workspace refresh`/`full` path). The build runs **under
  `_opsGate`** — the same lock that serializes Miller's `extract` subprocesses — so the `symbols.db` read never
  races an extract that could replace the file. This is an accepted Phase-1 tradeoff: a large rebuild briefly
  blocks the debounce drain; the cost is bounded to scan moments, not per-edit.
- **NOT built on the per-edit path** (`TryReindexAsLeader` / the debounce drain). A full FTS rebuild per
  file-save is O(corpus) and would not scale; between an incremental edit and the next scan the read path
  **self-heals** to the always-fresh in-memory holder (the sidecar is simply stale ⇒ `TryOpen` returns null).
- **Best-effort isolation.** `TryScanAsLeader` was restructured so the build sits OUTSIDE the scan's
  `try/catch(⇒Failed)` (still inside `_opsGate`): a sidecar build issue can never flip a successful scan to
  `Failed`. The shared `TryBuildSidecar` helper swallows the build's expected throwables behind a `when` filter
  (`SqliteException`/`IOException`/`InvalidOperationException`/`UnauthorizedAccessException`/`ArgumentException`/
  `NotSupportedException`/`IncompatibleExtractException`) and logs; it stamps `report.Revision` (the strict-equality
  routing key) and is a no-op when disabled / already-fresh / revision-less.
- **OFF-path parity.** The disabled gate short-circuits before any work, and the `_bootstrap.Workspace` getter
  (which throws before bootstrap) is read ONLY when the flag is on — so an un-started, no-workspace unit-test
  instance on the OFF path never touches it. Byte-identical to pre-wiring behavior.

Review notes:
- **0 confirmed defects.** The build-under-`_opsGate` cost, `SearchIndexWriter`'s `ClearAllPools`, the Windows
  mid-`File.Move` reader race (absorbed by `TryOpen`'s catch on BOTH reader paths), and the OFF-path parity were
  each examined and refuted as non-defects.
- **Intentional difference from Phase 4 (refuted as latent-only):** Phase 4 falls back to a freshness read when
  `report.Revision` is null; Phase 1 simply skips the build. A real julie scan always carries a revision, and a
  missing build self-heals to the in-memory index regardless, so the indexer is not given a second `symbols.db`
  read it would essentially never use.
- **Closed gap:** added `StartAsync_WhenEnabledLeaderAndSidecarBuildFails_StillMarksRegistryScanned` — the
  startup-site build is inside the outer try whose catch calls `MarkRegistryError`, so this pins that a failed
  build leaves the registry `Scanned`/`Ready` (not errored) and writes no artifact.

### As built — Phase 5 (2026-06-04, eval cleared → default ON)

The recall eval landed and CLEARED, so the sidecar is now **on by default** (opt out with
`MILLER_SEARCH_SIDECAR=0`). Three code changes + the eval harness; full fast suite 1434 green / 0 warnings,
scale 22/22; a 5-lens adversarial review (parity / telemetry / flip blast-radius / eval-gaming / test-quality),
refute-by-default.

- **Diacritics parity fix (closes the carried caveat).** `symbols_fts` is now built
  `tokenize='unicode61 remove_diacritics 0'`. The default `unicode61` folds diacritics (`café` → `cafe`), which
  inflated the word arm's per-term DF (`COUNT(*) … WHERE body MATCH`) above the in-memory Ordinal DF and drifted
  BM25 scores for accented identifiers — recall stayed exact (the C# re-tokenization drops the FTS false positive),
  only the score shifted. Empirically confirmed (folded matched 2/2 rows; `remove_diacritics 0` matched 1/2) and
  pinned by a `Café`/`Cafe` word-arm parity test. `SearchIndexWriter.SchemaVersion` bumped **1 → 2** so a
  stale-tokenizer artifact at a still-matching revision is rejected by `FtsSymbolSearchIndex.Open` and rebuilt
  (revision equality alone could not catch a same-revision rebuild under the old tokenizer).
- **"Disk path taken" telemetry counter.** Every symbol search stamps the telemetry row's `metadata_json` with
  `{"search_backend":"disk"}` when `context.Index is FtsSymbolSearchIndex`, else `"memory"` — so a silent
  self-heal to the in-memory index is observable (the dashboard can count `$.search_backend = 'disk'`). Set on the
  symbol-search branch only; never clobbers metadata another component set (search left it `{}`).
- **Default flipped ON (opt-out).** `SymbolSearchSidecar.FromEnvironment()` (used by BOTH composition roots —
  `MillerServiceRegistration` and `DashboardData`) is enabled unless `MILLER_SEARCH_SIDECAR` is an explicit falsy
  token (`0/false/off/no`, via `IsDisabledValue`). The pure env→sidecar mapping (`FromEnvValue`) is unit-tested
  without mutating the process env (so nothing leaks across xUnit's parallel collections). Self-heal is unchanged:
  a missing/stale/corrupt/schema-incompatible artifact still degrades to the in-memory path, so default-on can
  never break search. Blast radius was tiny — every service test pins `SymbolSearchSidecar.Disabled`/`enabled:true`
  explicitly via its test helper, so only the two env-reading composition roots and the flag-semantics tests moved.
- **Eval harness (`tests/Miller.Tests/Search/SearchRecallEval.cs`).** A label-free recall cross-eval keyed on the
  stable julie `symbol_id` (never a per-index DocId): sample seeded C# identifiers, derive `exact`/`camel`/
  `lasttok`/`interior` queries (interior = an all-letter, boundary-crossing collapsed window that equals no word
  token, with a corpus-popularity cap), measure recall@5 + MRR for the in-memory baseline vs the on-disk candidate.
  A fast methodology test pins the pure derivation + metric math and a tiny end-to-end superset; the Scale test runs
  the real corpus and asserts the gate + that the routing actually takes the disk path.

**Result (Miller's own corpus — 5,558 symbols / 3,588-identifier C# frame, seed 20260604, sample 200):**

| class | N | baseline recall@5 | candidate recall@5 |
|---|---|---|---|
| exact | 200 | 0.945 | 0.945 |
| camel | 164 | 0.317 | 0.317 |
| lasttok | 157 | 0.357 | 0.357 |
| **interior** | 169 | **0.000** | **0.763** |

Word-arm ranking parity: **521 queries compared, 0 violations** (identical DocId order + scores to 1e-9). Interior
recall **0 → 0.76** with zero word-class regression. `search.db` build 0.11 s / 4.1 MB; first search 3 ms. The
decision rule (recall up, zero regression, parity exact, disk-path test green) is met with wide margin.

**Corpus note:** OpenClaw (~565k) and Hermes (~237k) could NOT feed the eval — their on-disk `symbols.db` are an
older, incompatible artifact (no `artifact_metadata`, schema < 2) and would need a multi-minute re-extraction. The
eval is therefore a **C# single-language** certification on Miller's own corpus; the 6-language superset claim is
inherited from codenav, not reproduced here. The harness is corpus-agnostic — re-extracting OpenClaw/Hermes to a
schema-2 artifact would let them feed it unchanged.

## Eval plan (evidence-first, Scale-tagged)

Replicate codenav's label-free crosseval against Miller's own DBs (OpenClaw ~565k, Hermes ~237k):

- Sample ~120 symbols (seeded); derive `exact` / `camel` / `lasttok` / `interior` queries from each.
- Measure **recall@5** and **MRR** per class, baseline (in-memory) vs candidate (FTS5), by language.
- **Pass = strict superset:** interior recall rises; exact/camel/lasttok do **not** regress.
- **Ranking-parity test:** for **word-arm** queries (uncapped), identical top-N order/scores vs the
  in-memory index — proves the DF-from-`fts5vocab` + C#-recomputed-TF/doc-len plumbing. (Trigram-only
  hits are additive recall, not part of the parity claim.)
- Record `+build-time`, `search.db` size, first-search latency + RSS.
- Tag `[Trait("Category","Scale")]`, obtain julie via `ScaleTestSupport.RequireJulieServer()`, skip when
  absent. Fast suite stays `Category!=Scale`, <10s.

Decision rule: default the sidecar on for a path only when its eval clears (recall up, zero regression,
parity exact, disk-path test green).

## Risks

- **Word-arm fidelity:** feed the *exact* `CodeTokenizer` token stream **including duplicates** as the FTS
  body (TF + doc-len depend on multiplicity); let FTS only re-split on the spaces we insert. A built-in
  tokenizer that re-folds or re-splits diverges. Resolve before claiming zero regression.
- **Stats parity:** BM25 needs per-term DF + per-doc TF + doc-len, not just `avgdl`/`N`. Plan: DF from
  `fts5vocab`, TF + doc-len recomputed in C# from the resident symbol; a ranking-parity test gates it.
- **Candidate-window stranding (trigram arm only):** the word arm is uncapped so it can't strand; the
  trigram arm is windowed but purely additive. Guard with a recall test on hot tokens (`parse`, `service`).
- **Byte spans for pillar 2:** julie emits byte spans but Miller's lean reader drops them; the writer must
  read and persist `start_byte`/`end_byte` or AST-bounded embedding chunks stay impossible for Eros later.
- **Fallback hides the slow path:** Scale test must assert the disk path is taken; add a telemetry counter.
- **FTS5 must survive AOT:** the release plan ships `libe_sqlite3`; add a build-time capability probe so an
  FTS-less build fails loudly, not silently into fallback.
- **search.db duplicates some symbol metadata:** deliberate — the price of a shareable artifact decoupled
  from julie's per-scan file replacement.

## Brief update (FTS5 no longer parked)

New posture: symbol search moves to an **on-disk, Eros-shareable FTS5 sidecar** (`.miller/search.db`),
revision-keyed, ranking still in Miller's C#. Driver is **data-sharing with Eros + the 3-pillar
foundation**, not speed. Miller stays a read-only consumer of julie's `symbols.db` and additionally owns
a rebuildable derived `search.db` (same pattern as `telemetry.db`/`workspaces.db`). Out of scope here:
embeddings/semantic (Eros), and the `source_regions` scope-aware layer (next, but the schema is ready).
