# source_regions / pillar-3 scope-aware lexical search — design

> Historical implementation design. The 2.1.1 unblock evidence below is preserved for provenance; current Miller
> pins `julie-extract` v2.1.3 and exposes source-region search through explicit `regions=` when the region index is
> enabled.

- **Date:** 2026-06-04
- **Status:** ✅ **UNBLOCKED by julie-extract 2.1.1.** The pinned 2.1.1 binary was restored and a fresh extract
  of this repo verified `source_regions` for C# (`comment=3607`, `doc_comment=5505`,
  `string_literal=11062`) plus JavaScript, JSON, Markdown embedded regions, PowerShell, Bash, and YAML. The
  prior 2.1.0 blocker was real: that release emitted only JavaScript regions on this repo. Design agreed in
  brainstorming and revised twice after Codex adversarial review; build from this document after preserving the
  verified corrections below.
- **Scope:** Consume julie-extract schema-v2's `source_regions` table, using 2.1.1 all-language emission, to deliver pillar-3 *scope-aware lexical
  search*: an **inclusive region-text search** ("find TODO inside comments", "find `localhost` inside string
  literals") backed by a new region-text index in the `search.db` sidecar, plus a cheap `has_doc` annotation
  on symbol search. Exact match-inside-region (the region text *is* the indexed unit).
- **Aligns with:** `docs/plans/2026-06-03-julie-extract-2.1.0-bump-design.md` ("Deferred to consume next"),
  `docs/plans/2026-06-04-symbol-search-collapsed-trigram-design.md` (this builds the reserved region table in
  `search.db`), `docs/plans/2026-06-04-free-core-boundary-and-aot-release.md` (the region index is an
  Eros-shareable derived artifact).

## Resume evidence and corrections

Resume check completed 2026-06-05 with pinned julie-extract 2.1.1:

```sql
SELECT language, kind, COUNT(*) FROM source_regions GROUP BY 1,2;
```

Relevant result on this repo: `csharp|comment|3607`, `csharp|doc_comment|5505`,
`csharp|string_literal|11062`; total `source_regions=20887`. If a future extractor bump changes coverage,
repeat this query before shipping.

The two Codex reviews verified five corrections that the body below must reflect when building (the prose
below was written before the live-data check; these notes win on conflict):

1. **`has_doc` comes from `symbols.doc_comment`, NOT `source_regions`.** 2.1.1 now emits `doc_comment` regions,
   but `symbols.doc_comment` is already symbol-owned and result-bounded. Derive `has_doc` from
   `symbols.doc_comment IS NOT NULL` (query `symbols` by result `symbol_id`); use `doc_comment` regions for
   explicit `regions=doc_comment` search and optional line-range enrichment. (`inspect` already surfaces
   `symbols.doc_comment` at `InspectTool.cs:239` — unchanged.)
2. **`regions_fts.body` stores the `CodeTokenizer` token stream, not raw text** — exactly like the symbol arm
   (`SearchIndexWriter`), so FTS re-splits only on inserted spaces and DF/TF/doc_len match Miller's BM25.
   Store the **raw** region text in a separate `search_regions` column for snippet display only.
3. **Region build is gated by its OWN flag with size caps, separate from `MILLER_SEARCH_SIDECAR`.** Adding the
   region tables to the shared sidecar schema would otherwise make region build+storage default behavior on every
   refresh. Add e.g.
   `MILLER_REGION_INDEX` (default OFF until a Scale build-cost probe clears), and cap/skip oversized
   `string_literal` bodies (numeric byte budget) so a repo full of huge string constants can't blow up
   `search.db`.
4. **`EnsureBuilt`/`SearchIndexWriter.Write` must take `workspaceRoot`** (they currently take only
   `symbolsDbPath`, `symbols`, `revision`) — the region build reads source files from disk and needs the root.
   Confirm both writer paths can supply it (leader `IndexerService` AND external `CrossWorkspaceRefreshService`).
5. **Region reads MUST NOT reuse the fail-OPEN symbol gate** (`SymbolSearchSidecar.TryOpen` swallows errors →
   memory fallback). Region search has no in-memory fallback, so it needs a **distinct fail-closed** read path
   with actionable error states (see "Error handling" below).
6. **Per-language coverage reporting.** `regions=comment` must not look authoritative on a language julie
   doesn't cover. Surface a coverage note/warning by language; add acceptance tests for both a covered and an
   uncovered language.

## Practical answer

Miller currently **tolerates-and-ignores** `source_regions`. The capability "search the text inside comments /
string literals" cannot ride the existing `mode=content` path — that path is **docs-only by design**
(`ContentFileClassifier.IsDocsLike` indexes prose/markup/config and explicitly skips source so it doesn't
overlap symbol search). Comments and string literals live in `.cs/.rs/.ts` source, which content search never
sees.

So there is **no query-time shortcut**: to search text inside regions, Miller must **index region text at
build time**. The design therefore builds a region-text index in the `search.db` sidecar (the lock-holding
writer slices file bytes at each `source_regions` span, indexes the text tagged by `kind`), and serves
inclusive region search from it. This is the Eros-shareable derived-artifact pattern already proven by the
symbol FTS tables — the region table is the slot that design explicitly reserved.

Plain-English glossary: *region* = a span of source text julie tagged with a kind (comment / doc-comment /
string-literal / embedded). *Region-text search* = full-text search over just those spans' text, so a match is
*by construction* inside a region of that kind. *Sidecar* = `<workspace>/.miller/search.db`, Miller's
rebuildable derived index (same pattern as `telemetry.db`).

## Revision after review (what changed and why)

A Codex adversarial review of the first draft (verified against the code) found the "lean in-memory overlay on
the existing content index" approach **broken**, plus four correctness refinements. All are folded in:

| Codex finding | Sev | Resolution in this design |
|---|---|---|
| `mode=content` excludes source files (`ContentFileClassifier`), so an overlay can't search source comments | CRITICAL | **Abandoned the overlay.** Build a dedicated region-text index in `search.db`; region text is sliced from file bytes at build time. |
| `WHERE path IN (…)` not index-backed (`source_regions` indexes are on `file_id`) | HIGH | Build reads `source_regions` in **bulk** (full pass, no per-path filter). The one residual lazy query (symbol `has_doc`) keys on `containing_symbol_id`, which **is** indexed (`idx_source_regions_symbol`). |
| `regions` can't ride the `exclude_tests` post-fetch pattern; `mode=auto` never routes to content | HIGH | `regions=` is an **explicit router** to a distinct region-search path (its own index, own result shape), not a post-fetch filter on another mode. Defined below. |
| Line-only filtering / snippet straddle gives false confidence | MEDIUM | **Moot** — the indexed unit *is* the region text, so a match is inside the region by construction. Snippets are sliced from the region span, not a ±2 window. |
| Fail-*open* is unsafe for an explicit filter | MEDIUM | Explicit region queries **fail closed**: missing/stale/disabled region index → actionable error (+ leader-triggered rebuild), never silent unfiltered results. |

Also confirmed by the review: `symbols.doc_comment` exists and `inspect` **already surfaces it**
(`InspectTool.cs:239`) — so Path 2 shrinks to a `has_doc` annotation on symbol search; no new inspect work.

## Verified schema (source-confirmed)

From `julie-extractors/crates/julie-extract-artifact/src/schema.rs` (v2). 13 columns; **no text column** (text
is obtained by slicing the file at `[start_byte, end_byte]`):

```sql
CREATE TABLE source_regions (
    source_region_id     TEXT PRIMARY KEY,
    file_id              TEXT NOT NULL,                 -- FK files.file_id ON DELETE CASCADE
    path                 TEXT NOT NULL,                 -- relative-unix file path
    language             TEXT NOT NULL,
    kind                 TEXT NOT NULL,                 -- 'comment' | 'doc_comment' | 'string_literal' | 'embedded'
    containing_symbol_id TEXT,                          -- nullable; FK symbols.symbol_id ON DELETE SET NULL
    start_line           INTEGER NOT NULL,              -- 1-based
    start_column         INTEGER NOT NULL,
    end_line             INTEGER NOT NULL,
    end_column           INTEGER NOT NULL,
    start_byte           INTEGER NOT NULL,              -- UTF-8 byte offsets
    end_byte             INTEGER NOT NULL,
    metadata_json        TEXT                           -- nullable; {embedded_language, host_node_kind} for 'embedded'
);
-- idx_source_regions_file_span (file_id, start_byte, end_byte)
-- idx_source_regions_kind_file (kind, file_id, start_byte)
-- idx_source_regions_symbol    (containing_symbol_id)
```

`containing_symbol_id` attachment (from `source_regions.rs`): `doc_comment` → the **next** symbol after the
region; `comment`/`string_literal` → the **smallest** symbol containing the region; `embedded` → host-node
based, `metadata_json = {embedded_language, host_node_kind}`.

**Byte offsets are UTF-8** (julie is Rust). Slicing must operate on the file's raw bytes and UTF-8-decode the
`[start_byte, end_byte]` slice — never index into a .NET UTF-16 `string` by these offsets.

## Goal (Phase 1, agent-facing)

- `search "TODO" regions=comment` → BM25-ranked hits whose text is **inside** a comment, returning
  `path:line`, the region snippet, the `kind`, and the containing symbol (when present).
- `search "TODO" regions=comment,doc_comment` → union of those kinds.
- `search "localhost" regions=string_literal` → string-literal text search.
- Symbol search results carry a `has_doc` annotation (symbol has a `doc_comment` region).

## Non-goals (Phase 1)

- **`exclude_regions` ("find X but NOT in string literals")** — true exclusion is a filter over *all* source
  text, which would require indexing all source text (the cost symbol search deliberately avoids). Deferred
  until/unless we commit to a full source-text index. The `regions=` (inclusive) path is the headline.
- **`embedded` region bodies** — `embedded` spans can be whole `<script>`/`<style>` blocks (large). v1 indexes
  `comment`, `doc_comment`, `string_literal`; the schema carries `kind` so `embedded` is additive later. This
  bounds artifact size.
- **Interior-substring (trigram) over region text** — v1 is word-FTS (`unicode61`), which already splits
  `http://localhost:8080` into `http/localhost/8080` so `localhost` matches. A collapsed-trigram arm over
  region text is a later add (same pattern as the symbol trigram arm).
- **`trace`/bridge changes**, a new `region_search` tool, `ISymbolSearchIndex.Search` widening.

## Design

### Storage: region tables in `search.db` (introduced at `SearchIndexWriter.SchemaVersion` 3)

Added to the existing sidecar schema (built/owned by the lock-holding writer, opened read-only by readers and
by Eros):

```sql
-- Region-text recall. Body = the UTF-8-decoded region slice. Word FTS, diacritics preserved (parity with
-- symbols_fts).
CREATE VIRTUAL TABLE regions_fts USING fts5(
    region_id UNINDEXED, body, tokenize='unicode61 remove_diacritics 0');

-- Self-contained region metadata: scope filtering + resolve + Eros. Join key = julie's source_region_id.
CREATE TABLE search_regions(
    region_id            TEXT PRIMARY KEY,   -- julie's source_region_id
    kind                 TEXT NOT NULL,      -- comment | doc_comment | string_literal  (embedded reserved)
    path                 TEXT NOT NULL,
    language             TEXT NOT NULL,
    containing_symbol_id TEXT,               -- nullable; join to search_symbols / julie's graph (pillar 1)
    start_line           INT NOT NULL,
    end_line             INT NOT NULL,
    start_byte           INT NOT NULL,
    end_byte             INT NOT NULL,
    doc_len              INT NOT NULL);      -- token count of body (BM25 length norm)
CREATE INDEX ix_search_regions_kind ON search_regions(kind);

-- meta gains region_count + region_avgdl for BM25 over the region corpus (own corpus stats, separate from
-- the symbol corpus). These columns were introduced when schema_version moved 2→3.
```

Bumping `SchemaVersion` 2→3 meant any existing `search.db` (symbol-only) was rejected by
`FtsSymbolSearchIndex.Open`/the region reader and rebuilt — the same revision/schema gate remains in place for
later sidecar rebuilds.

### Build: slice region text under the writer lock (`SearchIndexWriter`, both writer paths)

The region tables are built in the same `SearchIndexWriter` pass that builds the symbol tables, under the
workspace `SingleWriterLock`, on **both** writer paths (leader `IndexerService` + external
`CrossWorkspaceRefreshService`) — the discipline already established for the symbol sidecar. Steps:

1. `SqliteSourceRegionReader.Read(dbPath)` — **bulk**, D4/D6, `ORDER BY path, start_byte`. Returns spans + kind
   + path + `containing_symbol_id` + byte offsets for the indexed kinds.
2. For each distinct file, read its **bytes from disk** (the workspace source, resolved from the region `path`
   against the workspace root — the same disk access the content projection uses), confirm freshness against
   `files.content_hash` (skip + log a file whose hash no longer matches what julie extracted, so we never index
   text against stale offsets), then UTF-8-decode each region's `[start_byte, end_byte]` slice. One read per
   file; bounded by repo size; **at scan time, off the search hot path**.
3. Insert `body` into `regions_fts` and metadata (incl. `doc_len`) into `search_regions`; stamp
   `meta.region_count`/`region_avgdl`. All in the existing temp-DB-then-atomic-replace transaction.

`SqliteSourceRegionReader` lives in `Miller.Indexing` (SQLite I/O). The byte-slice + UTF-8 decode + token
counting are **pure** helpers in `Miller.Core` (no I/O dep), so `Miller.Core` purity is preserved.

### Query: `FtsRegionSearchIndex` (`Miller.Indexing`, read-only)

A new reader over `search.db`, mirroring `FtsSymbolSearchIndex`'s lifetime discipline (resident metadata
snapshot at `Open`; a short-lived `Pooling=false` connection per query; never holds a file handle between
queries). Per query:
- Tokenize the query with the same `CodeTokenizer`; FTS-match `regions_fts.body` filtered to the requested
  `kind`s (join `search_regions`).
- **Rank in C#** with the shared `Miller.Core.Search.Bm25` over the **region corpus** stats (`region_count` /
  `region_avgdl` from `meta`; per-term DF via `COUNT(*) … WHERE body MATCH`; per-region TF/doc-len from the
  resident snapshot) — ranking authority stays in Miller; FTS is recall-only, exactly like the symbol arm.
- Resolve each hit to a `RegionSearchHit { Path, StartLine, Kind, Snippet, ContainingSymbolName? }` (snippet =
  the region slice, trimmed to a sane width). `exclude_tests` is honored via the existing path heuristic
  (`IsTestPath`) on the region's `path`.

### Routing + API (explicit, per Codex HIGH)

`SearchTool.Search` gains one optional param: **`regions`** — a comma list over `comment,doc_comment,
string_literal` (alias `docstring`→`doc_comment`). Routing rule:
- **`regions` present ⇒ region search.** It routes to `FtsRegionSearchIndex` regardless of `mode` (region text
  is its own corpus). If a conflicting `mode` is given (e.g. `mode=symbol regions=comment`), `regions` wins and
  the response notes it. This is a *distinct* path, not a post-fetch filter — so it does not depend on
  `mode=auto` routing.
- **`regions` absent ⇒ unchanged** (symbol/file/content/auto exactly as today).
- The CLI `search` verb maps `--regions` identically (CLI reuses the same mode/param mapping).
- `MILLER_AGENT_INSTRUCTIONS.md` documents `regions` + the `has_doc` annotation; `AgentInstructionsTests`
  stays green. `exclude_regions` is intentionally **not** added (documented as future).

### Path 2 — symbol `has_doc` annotation (cheap)

After symbol search, `SqliteSourceRegionReader.ReadHasDocComment(dbPath, resultSymbolIds)` — a result-bounded
query keyed on `containing_symbol_id` (index-backed by `idx_source_regions_symbol`) AND `kind='doc_comment'` —
stamps a `has_doc` flag on each hit (compact) / the doc-comment line range (verbose). Annotation only, not a
filter (avoids a confusing dual meaning of `regions=`). `inspect` is **unchanged** — it already surfaces
`detail.DocComment`.

### Data flow

```
search("TODO", regions=comment)                      → region search path
  └─ FtsRegionSearchIndex.Search("TODO", {comment})   (regions_fts MATCH + C# BM25 over region corpus)
  └─ [RegionSearchHit{path, line, kind, snippet, symbol?}]

search("foo", mode=symbol)                            → symbol search (unchanged)
  └─ SqliteSourceRegionReader.ReadHasDocComment(db, hitIds)  → has_doc annotation

build (leader / external refresh, under SingleWriterLock):
  SqliteSourceRegionReader.Read(db) → spans
  → read file bytes (freshness via content_hash) → UTF-8 slice region text
  → regions_fts + search_regions + meta(region_count, region_avgdl)   [SearchIndexWriter, current schema]
```

### Error handling / self-heal (fail **closed** for explicit requests)

- An explicit `regions=` query needs the `search.db` region table. There is **no in-memory region fallback**
  (region text is only indexed in the sidecar). So:
  - Sidecar disabled (`MILLER_SEARCH_SIDECAR=0`) → `regions=` returns an actionable error: "region search
    requires the search sidecar (currently disabled); unset MILLER_SEARCH_SIDECAR and refresh."
  - Region table missing / too-old `schema_version` / revision-stale → actionable "region index is stale/missing;
    refreshing" error, and on the **leader** trigger a rebuild (the build path exists). **Never** silently
    return unfiltered or symbol results — that would be a false answer to a scoped query.
- The `has_doc` **annotation** (optional, additive) stays best-effort: a read failure simply omits the flag.
- Readers use the standard read-only / `JulieSchemaGate` discipline; a schema-incompatible extract fails with
  the existing actionable artifact error, not raw SQLite.

## Test plan (TDD)

### Fast suite (no julie subprocess)
- **Pure slicing/decoding** (`Miller.Core`): UTF-8 byte-span slice correctness incl. multibyte chars (so a
  `//  café TODO` comment slices correctly); token counting; trimming.
- **`SqliteSourceRegionReader`** against synthetic `JulieDbFixture` rows: bulk `Read` ordering/kinds;
  `ReadHasDocComment` keyed by `containing_symbol_id`; NULL discipline (`containing_symbol_id`,
  `metadata_json`).
- **`SearchIndexWriter` region build** from a fixture extract + an in-memory/temp file set: `regions_fts` +
  `search_regions` populated for the three kinds; `embedded` excluded; `meta.region_count`/`region_avgdl`
  stamped; `schema_version=3`; stale-`content_hash` file skipped.
- **`FtsRegionSearchIndex`**: `regions=comment` returns the in-comment hit and excludes a same-token code
  occurrence; kind union; `string_literal` text search; BM25 ranking over the region corpus; `exclude_tests`
  via path heuristic.
- **Routing** through `SearchTool.Run`: `regions` present routes to region search regardless of `mode`;
  conflicting `mode` noted; absent `regions` is byte-identical to today.
- **Fail-closed**: `regions=` with sidecar disabled / region table absent / schema v2 → actionable error, not
  unfiltered/symbol results.
- **`has_doc`** annotation stamped when a `doc_comment` region exists.
- **Fixtures/guards**: `JulieDbFixture` builds `source_regions` (`SourceRegionRow` + DDL);
  `JulieDbFixtureV1SchemaTests` → `JulieDbFixtureV2SchemaTests` asserts the `source_regions` schema.

### Scale suite (real `miller` + julie, `[Trait("Category","Scale")]` via `ScaleTestSupport.RequireJulieServer`)
- End-to-end: extract a fixture source tree with a real `julie-extract`; assert `source_regions` is populated;
  build the sidecar; `search "<token-only-in-a-comment>" regions=comment` returns the comment hit and excludes
  the same token in code; assert the **region table is actually present in `search.db`** (the disk path is
  taken, not a silent miss).
- **Build-cost probe**: record region-build time + `search.db` size delta on a real extract (the new disk-read
  pass is the main new cost; this is the number that informs whether `embedded`/trigram are worth adding and
  whether default-on is safe on large repos). 2026-06-05 scale fixture evidence:
  `region_build_ms=9.4`, `search_db_bytes=98304`, `source_regions=2`, `search_regions=2`.

`ScaleTraitConventionTests` stays green — only the real-binary tests are Scale.

## Acceptance criteria

- [x] `SearchIndexWriter` builds `regions_fts` + `search_regions` (kinds: comment, doc_comment, string_literal)
      by slicing UTF-8 region text from disk under the writer lock, on both writer paths; current sidecar schema;
      stale-`content_hash` files skipped.
- [x] `search "<q>" regions=<kinds>` returns BM25-ranked region hits whose text is inside those region kinds,
      with `path:line`, snippet, kind, and containing symbol; excludes same-token code occurrences.
- [x] String-literal text search works (`regions=string_literal`).
- [x] `regions` present routes to region search regardless of `mode`; absent `regions` is byte-identical to
      current behavior; CLI `--regions` mirrors it.
- [x] Explicit `regions=` queries **fail closed** (actionable error + leader rebuild) when the region index is
      disabled/missing/stale — never silent unfiltered/symbol results.
- [x] Symbol search results carry a `has_doc` annotation (result-bounded, `containing_symbol_id`-indexed);
      `inspect` unchanged.
- [x] `Miller.Core` stays pure (slice/decode/BM25 in Core; SQLite + disk I/O in `Miller.Indexing`); no
      `ISymbolSearchIndex.Search` widening; rankers reuse `Miller.Core.Search.Bm25`.
- [x] `MILLER_AGENT_INSTRUCTIONS.md` documents `regions` + `has_doc`; `AgentInstructionsTests` green.
- [x] `JulieDbFixture` builds `source_regions`; `JulieDbFixtureV2SchemaTests` asserts the schema.
- [x] Scale build-cost probe recorded; `dotnet build Miller.slnx -c Release` 0/0; `scripts/test.sh` and
      `scripts/test.sh scale` pass; `ScaleTraitConventionTests` green.

## Phases

1. **Phase 1 (this design)** — region-text index in `search.db` (comment/doc_comment/string_literal),
   inclusive `regions=` search, `has_doc` annotation. Default-on follows the **symbol sidecar precedent only
   after the build-cost probe clears** (don't flip on large repos blind).
2. **Phase 2 (deferred, measured)** — `embedded` bodies; a collapsed-trigram arm over region text for interior
   substring; `exclude_regions` (requires a full source-text index — separate decision); push region
   metadata to Eros consumers as a documented contract.

## Risks

- **Build cost + artifact size** — region text (esp. string literals) is a large fraction of source; the
  disk-read-and-slice pass and the stored bodies grow `search.db`. **Mitigation:** measured Scale probe before
  default-on; `embedded` excluded; trigram deferred. This is the honest cost the capability requires.
- **UTF-8 vs UTF-16 offsets** — julie's byte offsets are UTF-8; slicing a .NET string by them corrupts
  multibyte text. **Mitigation:** slice raw bytes, decode after; a multibyte fast-suite test pins it.
- **Stale offsets** — a file edited between julie's extract and the slice would mis-slice. **Mitigation:**
  `content_hash` freshness check per file; mismatches skipped + logged (the build is under the same lock right
  after the scan, so this is rare).
- **Sidecar dependency** — region search has no in-memory fallback; if the sidecar is off, the capability is
  unavailable. **Mitigation:** fail closed with an actionable message; the sidecar is default-on and
  self-healing for the symbol arm already.
- **External-workspace disk access** — the external writer must read that workspace's source to slice text;
  `CrossWorkspaceRefreshService` already runs `julie-extract` against that on-disk tree, so the source is
  present. **Verify during TDD** that the region build resolves paths against the external workspace root.
```
