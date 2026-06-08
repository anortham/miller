# julie ⇄ Miller contract — VERIFIED ground truth (2026-05-29)

> Historical origin evidence. This predates the `julie-extract` migration and may mention `julie-server`, schema 26,
> or old contract versions. Current extractor contract facts are surfaced by `miller capabilities --json` and active
> Miller contracts under [`../contracts/`](../contracts/).

Source-of-truth verification for everything Miller M1 builds against. Produced by a 6-agent recon over
`~/source/julie` source **and** by running the prebuilt `julie-server extract` binary end-to-end. Every fact below
is cited to a `file:line` in julie or a NuGet/SQLite doc URL. Pinned versions: **julie v7.12.2, SQLite schema
version 26, `extract_contract_version` 1.**

> Gate on these. Miller's SQLite reader is written against schema_version=26 / contract=1; surface a typed error if a
> DB reports anything else rather than silently misreading.

---

## 1. SQLite schema written by `julie-server extract`

The extract path opens the same `SymbolDatabase` as the daemon, migrates to `LATEST_SCHEMA_VERSION = 26`, and bulk-inserts
into **`files`, `symbols`, `identifiers`, `types`, `relationships`** (+ `symbol_annotations`, `external_extract_metadata`,
and infra tables). All five core tables the plan named are real. FK enforcement is ON; DB is WAL.

### Core table DDL (verbatim from `src/database/schema.rs`)

```sql
CREATE TABLE IF NOT EXISTS files (
    path TEXT PRIMARY KEY,          -- RELATIVE unix-style path to the extract root
    language TEXT NOT NULL,
    hash TEXT NOT NULL,             -- BLAKE3 hex of full file bytes (use for freshness)
    size INTEGER NOT NULL,
    last_modified INTEGER NOT NULL,
    last_indexed INTEGER DEFAULT 0,
    parse_cache BLOB,
    symbol_count INTEGER DEFAULT 0,
    content TEXT,                   -- full file text ('' when None/binary)
    line_count INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS symbols (
    id TEXT PRIMARY KEY,            -- md5 hex, 8-field span id (see §1.1)
    name TEXT NOT NULL,
    kind TEXT NOT NULL,
    language TEXT NOT NULL,
    file_path TEXT NOT NULL REFERENCES files(path) ON DELETE CASCADE,
    signature TEXT,
    start_line INTEGER, start_col INTEGER, end_line INTEGER, end_col INTEGER,
    start_byte INTEGER, end_byte INTEGER,
    doc_comment TEXT,
    visibility TEXT,                -- Visibility::as_storage_str
    code_context TEXT,
    parent_id TEXT REFERENCES symbols(id),   -- self-FK, nullable
    metadata TEXT,                  -- JSON
    file_hash TEXT,                 -- ALWAYS NULL from extract (do not use for freshness)
    last_indexed INTEGER DEFAULT 0, -- forced 0 from extract
    semantic_group TEXT,            -- NEVER populated by extract (greenfield xlang column)
    confidence REAL DEFAULT 1.0,
    content_type TEXT DEFAULT NULL, -- NULL = code; 'documentation' = markdown docs
    body_start_line INTEGER, body_start_col INTEGER, body_end_line INTEGER, body_end_col INTEGER,
    body_start_byte INTEGER, body_end_byte INTEGER, body_hash TEXT,
    reference_score REAL NOT NULL DEFAULT 0.0   -- 0.0 unless extract ran with --analyze
);

CREATE TABLE IF NOT EXISTS identifiers (
    id TEXT PRIMARY KEY,            -- md5 hex, same 8-field span scheme
    name TEXT NOT NULL,
    kind TEXT NOT NULL,            -- 'call' | 'variable_ref' | 'type_usage' | 'member_access'
    language TEXT NOT NULL,
    file_path TEXT NOT NULL REFERENCES files(path) ON DELETE CASCADE,
    start_line INTEGER NOT NULL, start_col INTEGER NOT NULL, end_line INTEGER NOT NULL, end_col INTEGER NOT NULL,
    start_byte INTEGER, end_byte INTEGER,
    containing_symbol_id TEXT REFERENCES symbols(id) ON DELETE CASCADE,  -- POPULATED (enclosing symbol)
    target_symbol_id TEXT REFERENCES symbols(id) ON DELETE SET NULL,     -- ALWAYS NULL from extract
    confidence REAL DEFAULT 1.0,
    code_context TEXT,
    last_indexed INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS types (
    symbol_id TEXT PRIMARY KEY REFERENCES symbols(id) ON DELETE CASCADE,  -- 1:1 with symbols
    resolved_type TEXT NOT NULL,    -- "String", "Vec<User>", "Promise<Data>"
    generic_params TEXT,            -- JSON array ["T","U"] or NULL
    constraints TEXT,               -- JSON array ["T: Clone"] or NULL
    is_inferred INTEGER NOT NULL,   -- 0 explicit / 1 inferred
    language TEXT NOT NULL,
    metadata TEXT,                  -- JSON
    last_indexed INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS relationships (
    id TEXT PRIMARY KEY,
    from_symbol_id TEXT NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    to_symbol_id TEXT NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    kind TEXT NOT NULL,
    file_path TEXT NOT NULL DEFAULT '',
    line_number INTEGER NOT NULL DEFAULT 0,   -- 1-based
    confidence REAL DEFAULT 1.0,
    metadata TEXT,                  -- JSON
    created_at INTEGER DEFAULT 0    -- not written by extract (stays 0)
);

CREATE TABLE IF NOT EXISTS external_extract_metadata (
    key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at INTEGER NOT NULL
);
-- keys: julie_version, sqlite_schema_version, extract_contract_version, workspace_id,
--       root_path, created_at, updated_at, analysis_state, analyzed_revision
```

Indexes exist on: symbols(name,kind,language,file_path,semantic_group,parent_id, reference_score DESC partial);
files(language,last_modified); identifiers(name,file,containing,target,kind, + composite file/line/kind, file/name,
kind/containing, name/kind/containing); types(language,resolved_type,is_inferred); relationships(from,to,kind,file).

### 1.1 Load-bearing gotchas (the read layer MUST honor these)

- **Symbol/identifier `id` = lowercase MD5 hex (32 chars) of `{file_path}:{name}:{start_line}:{start_col}:{end_line}:{end_col}:{start_byte}:{end_byte}`** — 8 fields, start+end. `start_line`/`end_line` 1-based; cols 0-based; bytes absolute u32. (`crates/julie-extractors/src/base/types.rs:258-271`.) Treat IDs as **opaque**. A legacy 4-field `generate_id` exists but is NOT the canonical path.
- **`identifiers.target_symbol_id` is ALWAYS NULL** from extract — resolution is explicitly the consumer's job ("resolved on-demand in C#"). `containing_symbol_id` **is** populated. (`creation_methods.rs:120`, `schema.rs:284-367`.)
- **Symbol-ID churn**: any edit that shifts byte offsets rewrites the IDs of every symbol below the edit point (span identity, not content identity). → Never persist/cache resolved cross-file links keyed on symbol ID across a file update without re-resolving. (`julie-eros-audit §2`.)
- **No FTS5.** `symbols_fts`/`files_fts` were created in migration 005 and **dropped** in 007; search is external (Tantivy in julie; Miller builds its own in-memory index). Do not depend on any `*_fts` table. (`migrations.rs:574-596`.)
- **Paths are RELATIVE unix-style** to `external_extract_metadata.root_path`. Join to `root_path` for absolute paths; normalize to forward slashes when matching. (`files.rs:582-588`.)
- **Freshness**: use `files.hash` (BLAKE3) + `files.last_modified`. `symbols.file_hash` is always NULL from extract.
- **`reference_score`** is 0.0 unless `extract` ran with `--analyze`; check `external_extract_metadata.analysis_state == 'current'` before trusting it.
- **`relationships` is sparse and within-language** (MyraNext: 499 rows); don't use it for cross-language links — mine `identifiers` + signatures. `semantic_group` is always empty. Dangling relationships are dropped at insert. (`search-and-storage.md:25-36`.)
- **`vec0` virtual tables** (`symbol_vectors` via migration 010) may be created by the migration chain and require the `sqlite-vec` extension to open. **OPEN RISK** (needs a live check): if present, opening the DB without `sqlite-vec` loaded could error. Verify against a real extract DB before hardcoding.
- JSON columns (`metadata`, `types.generic_params`, `types.constraints`) parse defensively; `is_inferred`/`content_type` per above.

---

## 2. `julie-server extract` CLI contract (verified empirically)

Binary is **`julie-server`** (Cargo `[[bin]]`; also ships `julie-adapter`, `julie-daemon` — Miller invokes `julie-server`
directly; the adapter/daemon are for julie's interactive MCP serving, NOT for one-shot extract).

```
julie-server extract --db <ABS_DB> --root <ABS_ROOT> --json <SUBCOMMAND> [opts]
  SUBCOMMANDS: scan [--force] | update --file <PATH> | delete --file <PATH> | analyze | info
```

- **`--db <PATH>` REQUIRED for every subcommand**; `--root` required for scan/update/delete (optional for analyze/info).
- **Default output is TEXT — Miller MUST pass `--json`.** `--format text|json|markdown` also exists (`--format` wins).
- **Parent directory of `--db` must already exist** (julie creates the `.db` + `-wal`/`-shm` + lock files, but does NOT `mkdir`). Use absolute paths for `--db`/`--root` (relative resolves against CWD; `db_path` is echoed back un-canonicalized).
- **Exit codes**: `0` success → parse stdout JSON report (`status` ∈ scanned/rebuilt/changed/unchanged/ignored/deleted/not_found/analyzed); `1` operation failure → stdout JSON report with `status:"failed"`, `errors[]`; `2` argv/usage error → **plain text on STDERR** (not JSON). Capture stdout/stderr separately; only parse stdout as JSON.
- **First call on a new DB must be `scan`** (creates metadata, workspace_id, binds root). update/delete/analyze/info on a metadata-less DB → exit 1.
- **Incremental**: `scan` (no `--force`) is hash-delta (skips unchanged, prunes orphans); `scan --force` = full rebuild / required when changing the bound `--root`. `update --file` = single-file hash-gated upsert; `delete --file` removes one file's rows (tolerates already-absent → `not_found`, exit 0). `update` on a now-ignored file silently converts to delete (`status:"ignored"`, treat as "symbols removed").
- **Single-writer**: each op takes a 30s exclusive `flock` on `<db>.julie-extract.lock`. Concurrent extracts on one DB serialize then fail on timeout → Miller must enforce single-writer.
- **`.julieignore` and `.gitignore` are honored automatically** (no flag). `--ignore-file <PATH>` (repeatable) adds out-of-tree ignore files (and excludes that file itself).
- **Discovery silently skips**: files > 1 MiB, `*.min.*`/`*.bundle.*`, blacklisted dirs (.git, .julie, node_modules…), unsupported extensions. → Not every file under `--root` is in the DB; no error for skips.
- **Data-loss guard**: a scan/update can fail (exit 1) if re-extraction would drop previously-good symbols (e.g. transient parse failure). Surface, don't swallow.
- `--analyze` (or the `analyze` subcommand) runs cross-ref analysis → populates `reference_score`. `--strict-schema` refuses to migrate an older DB. A DB **newer** than the binary always errors.

JSON report keys (`report.rs`): `status, operation, workspace_id, db_path, root, julie_version, schema_version,
schema_state, extract_contract_version, revision, analyzed_revision, analysis_state, missing_metadata_keys[],
files_scanned, files_updated, files_deleted, symbols_extracted, files_total, symbols_total, relationships_total,
identifiers_total, types_total, errors[]`.

---

## 3. Obtaining the binary (restore script)

Repo **`anortham/julie`** publishes GitHub Releases with prebuilt assets. **Pin `v7.12.2`** (latest, 2026-05-28; matches
Cargo.toml). Do NOT use the stale binary checked into julie's tree (7.12.1) and do NOT build from source for supported platforms.

```
URL: https://github.com/anortham/julie/releases/download/v<VER>/julie-v<VER>-<TRIPLE>.<EXT>
  macos arm64  → aarch64-apple-darwin.tar.gz   (primary target)
  macos x64    → x86_64-apple-darwin.tar.gz
  linux x64    → x86_64-unknown-linux-gnu.tar.gz
  windows x64  → x86_64-pc-windows-msvc.zip
```
- Archives are **flat**: extract and pick `julie-server` (`.exe` on Windows); set the exec bit on Unix. (They also bundle `julie-adapter`/`julie-daemon` — ignore for extract.)
- **No `linux-arm64` asset** → fall back to `cargo build --release --bin julie-server`, or mark unsupported (not a primary target).
- **No upstream checksums/signatures.** → Miller computes and pins its own sha256 per asset in repo config; verify on download. macOS binaries are ad-hoc codesigned (not notarized) — may need quarantine clear.
- **Risk**: URLs are public today; if the repo goes private, an unauthenticated fetch 404s and restore needs a `gh`/token-authenticated download.
- Install into a gitignored `.tools/julie-server/<version>/` cache; pinned version lives in repo config.

---

## 4. .NET integration facts

- **`Microsoft.Data.Sqlite` 10.0.8** (latest stable; aligns with the repo's Microsoft.Extensions.* 10.0.8 pins; zero transitive Microsoft.Extensions.* deps → no conflict). Add the **bundled** package (not `.Core`) to **`Miller.Indexing`** — it auto-initializes the `e_sqlite3` provider; **do NOT** call `Batteries_V2.Init()`.
- **`FrozenDictionary` is in-box in net10.0** (`System.Collections.Frozen`, shipped in the shared runtime). No NuGet package — verified by compiling on net10.0.
- **WAL read-only trap** (biggest M1 gotcha): `Mode=ReadOnly` on a WAL DB **still requires write access** to the `-shm`/`-wal` files (or the DB directory) because a WAL reader writes a mark to the wal-index. The only true zero-write open is the `immutable=1` URI flag — but that returns stale/incorrect data if a writer (julie) is active. **Decision (D4): open `Mode=ReadOnly` and require the DB dir be writable** (Miller controls these dirs); surface `SQLITE_CANTOPEN`/`READONLY` as a clear actionable error; do NOT default to `immutable=1`.
- **Do NOT set `Cache=Shared`** on a WAL connection (MS docs: mixing is discouraged).
- ADO.NET async on SQLite runs synchronously — use WAL for concurrency, not `ReadAsync`.
- AOT: Microsoft.Data.Sqlite is AOT/trim-safe since EF Core 8; only `<PublishAot>` needs a RID for the native lib. Not a blocker now; don't foreclose it.

Recommended connection string:
```csharp
new SqliteConnectionStringBuilder { DataSource = absoluteDbPath, Mode = SqliteOpenMode.ReadOnly }.ToString()
```

---

## 5. Spike port spec (Miller.Core search core)

From `spike/Codesearch.Spike/` (`SearchBench.cs`, `CodeTokenizer.cs`, `ContractCheck.cs`). Measured on 565k symbols
(M2 Ultra, .NET 10): in-memory build **0.91 s, ~35 MB RAM, ranked top-50 in 25.2 µs** (115,748 terms / 3.6M postings).

- **`CodeTokenizer`** (pure, zero-alloc `ReadOnlySpan<char>`): scans word runs where a word char = ASCII letter/digit or Unicode letter/digit; `_ . -`/whitespace are delimiters (snake/dotted split naturally). For each run, emit the **full lowercased word first**, then component parts only if a split occurred. Boundary rules: (1) lower/digit→UPPER; (2) UPPER UPPER lower → split before trailing upper (acronym: `HTTPServer`→`HTTP|Server`); (3) any letter↔digit (`Vector512`→`vector|512`). No stopwords, no min-length, no stemming. → `getHTTPResponseCode` ⇒ `[gethttpresponsecode, get, http, response, code]`.
- **Index** = `FrozenDictionary<string,int[]>` of term→doc-ids (sorted ascending), built from `Dictionary<string,List<int>>`, with a parallel `int[] docLen` and precomputed `avgdl`. **Postings hold doc-id only — no tf, no positions.**
- **BM25**: `k1=1.2, b=0.75`; `idf = ln(1 + (N - df + 0.5)/(df + 0.5))`; per-doc `score = idf * (k1+1) / (1 + k1*(1 - b + b*docLen[id]/avgdl))` with **`tf` hardcoded to 1**. `docLen` counts every emitted token (full word + components + duplicates), set before per-symbol de-dup. Bounded top-50 via linear min-tracking; **no tie-break**.
- **Indexed text** = `name` + `' '` + `signature` (signature tokens matter for cross-language tracing). Loaded via `SELECT name, COALESCE(signature,'') FROM symbols WHERE name IS NOT NULL`.

### Spike limitations → M1 must improve (not copy blindly)
- **`tf=1`** ⇒ BM25 degenerates to IDF × length-norm. → **Decision (D1): store term frequency** (`posting = {int DocId; int Tf}`) for honest BM25; the freed embeddings headroom easily affords it (~2× a tiny postings footprint). Compute `docLen` from the pre-dedup token stream; pin tokenizer + docLen semantics with table-driven tests.
- **Single-term query only.** → **Decision (D2): multi-term OR with per-doc score accumulation**, an all-terms-present boost, and a **deterministic tie-break** (ascending doc id, with an exact-name-match boost).
- Move ALL of this into **Miller.Core with zero I/O**: tokenizer (span→tokens), index builder (takes a stream of `(docId, text)` → frozen index + docLen + avgdl), BM25 scorer/top-K. `Miller.Indexing` owns the SQLite read + row→`(docId,name,signature)` mapping.

---

## 6. Resolved M1 design decisions

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | Postings store `tf` (`{int DocId; int Tf}`) | Honest BM25; spike's tf=1 was an IDF-only shortcut. Cheap with embeddings gone. |
| D2 | Multi-term OR + score accumulation + all-terms boost + deterministic tie-break (doc id; exact-name boost) | Spike was single-term, non-deterministic ties. |
| D3 | Index `name + signature` for M1 baseline; doc_comment/code_context **TBD** (folding with the tree-sitter gap analysis) | Matches the bench; richer text decided alongside the extraction-enrichment findings. |
| D4 | SQLite `Mode=ReadOnly`, require writable DB dir, no `immutable=1` default | WAL readers need `-shm` write access; `immutable=1` is unsafe under a concurrent julie writer. |
| D5 | Gate on `schema_version==26 && extract_contract_version==1`; read `root_path`/`workspace_id`/`analysis_state` from `external_extract_metadata`; typed error on mismatch | Don't silently misread a future/older schema. |
| D6 | Live-repo extract+index test = `[Trait("Category","Scale")]` (excluded from the <10s default suite) | Subprocess+index won't fit the 10s budget; default suite uses a synthesized tiny DB. |
| D7 | Restore script downloads pinned `v7.12.2` from anortham/julie releases into `.tools/`; self-pin sha256; linux-arm64 → source fallback | Prebuilt assets exist for the supported platforms; no upstream checksums. |

### Open items to resolve with a live extract DB (before hardcoding)
1. Whether `vec0`/`symbol_vectors` tables get created on a v26 extract DB (would require `sqlite-vec` to open). **Probe a real DB.**
2. Confirm the `--analyze` exit behavior + data-loss-guard exit code so the subprocess wrapper maps them meaningfully.
3. Finalize D3 (index doc_comment/code_context?) after the tree-sitter gap analysis.
