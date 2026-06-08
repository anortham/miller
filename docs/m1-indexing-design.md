# M1 — Miller.Indexing design (read layer + extract wrapper + restore)

> Historical status: this milestone design is implementation history. It may mention old schema versions,
> command names, or startup assumptions. For current behavior, start with [`docs/README.md`](README.md),
> [`README.md`](../README.md), and active contracts under [`docs/contracts/`](contracts/).

Implementation spec for M1. Architecture is settled here; the exact literal strings (connection string,
SELECT column list, extract argv, report JSON field names, release asset names) are marked `«recon»` and
filled from the `m1-indexing-recon` workflow (wp4odbrjx) before implementation starts.

Grounded in: `docs/findings/julie-contract-verified.md` (D1–D7), `docs/miller-mvp-plan.md` (M1).

## Goal
Index a repo and answer a ranked query in-process, no MCP yet. Miller.Indexing reads a julie extract DB
(read-only, WAL-safe) into `IndexedSymbol`s, feeds their projections to `MillerSearchIndex.Build`, and can
drive `julie-server extract` as a subprocess. Contract-tested against a synthesized tiny julie-schema DB —
NOT a re-test of julie's extraction (julie owns that).

## The logic↔infra seam (non-negotiable)
- **Miller.Core** stays zero-I/O, zero-dep. It already owns `SearchableDocument`, `MillerSearchIndex`,
  `CodeTokenizer`. M1 does NOT touch Core.
- **Miller.Indexing** owns everything with I/O: SQLite read, the schema/contract gate, the subprocess
  wrapper. It depends on Core. Adds the one infra dependency: `Microsoft.Data.Sqlite 10.0.8`.
- Contract tests live in Miller.Tests and synthesize a tiny julie DB in setup (Microsoft.Data.Sqlite). The
  default suite stays < 10s. Live extraction is `[Trait("Category","Scale")]`, excluded by default (D6).

## Components

### 1. `MillerExtractContract` (version pins — single source of truth)
The D5 gate constants, centralized so the M4 bump (→ schema 28 / contract 2 when the julie enrichment lands)
is a one-line change:
```csharp
namespace Miller.Indexing;
internal static class MillerExtractContract
{
    // Miller pins julie-server v7.12.2 → schema 26 / extract_contract_version 1.
    // Bumps to (28, 2) at M4 when the bridge-anchor extraction enrichment is consumed.
    public const long ExpectedSchemaVersion = 26;
    public const long ExpectedExtractContractVersion = 1;
    public const string PinnedJulieServerVersion = "7.12.2";
}
```

### 2. `JulieSchemaGate` (D5)
Runs before any read. Confirms the DB is a compatible julie extract:
- `SELECT COALESCE(MAX(version), 0) FROM schema_version` == `ExpectedSchemaVersion` (julie's own query).
- `SELECT value FROM external_extract_metadata WHERE key = 'extract_contract_version'` → parse the TEXT to int
  == `ExpectedExtractContractVersion`. (ALL metadata values are stored as TEXT strings; value `'1'` today.)
Throws `IncompatibleExtractException` with an actionable message:
- newer than expected → "DB schema/contract is newer than this Miller build expects; upgrade Miller or
  re-pin julie-server."
- older / table missing → "DB is not a v7.12.2 julie extract (schema X, contract Y); re-run restore +
  `extract scan` with the pinned julie-server."
Gating on the tables' existence also fails fast on a non-julie / corrupt DB.

### 3. `IndexedSymbol` (Indexing-layer record — the join-key carrier)
`SearchableDocument` deliberately carries no julie id. Indexing retains it for M4 (identifiers/relationships/
types join on the opaque symbol id):
```csharp
namespace Miller.Indexing;
public sealed record IndexedSymbol(
    int DocId,          // Miller-assigned, 0-based row ordinal (opaque to the index)
    string SymbolId,    // julie opaque MD5-hex id — the M4 join key (treat as opaque)
    string Name,
    string? Signature,
    string Kind,
    string Language,
    string FilePath,    // relative-unix to root_path
    int StartLine,      // 1-based
    string? ParentId)   // julie parent_id (containment; M4)
{
    public SearchableDocument ToSearchableDocument() =>
        new(DocId, Name, Signature, Kind, Language, FilePath, StartLine);
}
```

### 4. `SqliteSymbolReader` (D4 — read layer)
- Connection string: `new SqliteConnectionStringBuilder { DataSource = absDbPath, Mode = SqliteOpenMode.ReadOnly }`
  → emits `Data Source=<abs>;Mode=ReadOnly`. Do NOT set `Cache=Shared` (MS discourages with WAL), nor Foreign
  Keys / Recursive Triggers. Pooling left default. No `SQLitePCLRaw.Batteries_V2.Init()` (bundled provider
  auto-inits — confirmed empirically).
- D4 = `Mode=ReadOnly` + **require the DB's directory be writable** (the WAL -shm/-wal sidecar trap: under a
  live writer SQLite must create/refresh the sidecars; a read-only dir makes Open()/first read throw
  `SqliteException.SqliteErrorCode == 8` "attempt to write a readonly database"). Verified: Mode=ReadOnly reads
  even uncheckpointed -wal rows correctly; `immutable=1` silently drops them — so reserve `immutable=1`
  (`Data Source=file:///<abs>?immutable=1`) ONLY for a known-static snapshot with no live writer. Reader probes
  dir-writability at startup (create+delete a temp file) and throws a clear `InvalidOperationException` if not.
- Missing -wal (DB was checkpointed) is a non-issue — do NOT special-case it.
- Opens connection → runs `JulieSchemaGate` → runs the SELECT below → maps each row to `IndexedSymbol` with
  `DocId = ordinal++`, ordinal reads (faster than name lookup; keep SELECT order locked to the `Get` ordinals),
  `IsDBNull` guards on nullable columns. **`start_line` is nullable INTEGER** → `IsDBNull ? 0 : GetInt32`.
- The SELECT (deterministic DocId ordering):
  ```sql
  SELECT id, name, signature, kind, language, file_path, start_line, parent_id
  FROM symbols
  WHERE name IS NOT NULL
  ORDER BY file_path, start_line, id;
  ```
  Map: `id`→`SymbolId` (opaque, NOT NULL), `name`→`Name` (NOT NULL), `signature`→`Signature` (nullable),
  `kind`→`Kind`, `language`→`Language`, `file_path`→`FilePath` (relative-unix, NOT NULL),
  `start_line`→`StartLine` (nullable→0), `parent_id`→`ParentId` (nullable). Do NOT read `doc_comment`
  (SearchableDocument has no field for it; D3 baseline = name+signature), nor `file_hash`/`semantic_group`/
  `reference_score` (always NULL/empty/0 from a plain `scan`).
- Returns `IReadOnlyList<IndexedSymbol>`. Sync (startup, single pass — Microsoft.Data.Sqlite async is
  synchronous internally). Dispose connection+command+reader via `using`. (If the DB file must later be
  replaced, `SqliteConnection.ClearAllPools()` first.)
- NULL/empty discipline: `symbols.file_hash` is always NULL (use `files.hash` for freshness later);
  `semantic_group` empty; `reference_score` 0 unless `--analyze` — Miller ignores these in M1.

### 5. `MillerRepositoryIndex` (thin facade tying read → Core index)
The M1 in-process deliverable. Builds the Core index from the read symbols and retains the DocId→IndexedSymbol
map for hydration:
```csharp
public sealed class MillerRepositoryIndex
{
    private readonly MillerSearchIndex _index;
    private readonly IReadOnlyList<IndexedSymbol> _byDocId; // DocId == list index here, by construction
    public static MillerRepositoryIndex Build(IReadOnlyList<IndexedSymbol> symbols);
    public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or);
    public IndexedSymbol Resolve(int docId); // SearchHit.Document.DocId → julie symbol id + parent
}
```
(Build assigns DocId == ordinal, so `_byDocId[docId]` is O(1). This is where the opaque-string-id →
int-DocId bridge lives.)

### 6. `JulieExtractRunner` (subprocess wrapper)
- Locates `julie-server` (`.tools/julie-server[.exe]` first, then a configurable path / PATH). Surfaces a clear
  error if absent pointing at the restore script.
- Builds argv via `ProcessStartInfo.ArgumentList` (no shell), all paths **absolute**:
  - scan: `extract --db <ABS_DB> --root <ABS_ROOT> --json scan` (append `--force` for full rebuild / root change)
  - info: `extract --db <ABS_DB> --json info` (no `--root`; info opens read-only, takes NO flock — safe under a writer)
  - `--json` is **mandatory** (default output is unparseable TEXT, still exit 0).
- `Scan` must `Directory.CreateDirectory(Path.GetDirectoryName(absDb))` first (no mkdir in julie's path; the
  `.db` file itself may be absent = fresh). First call on a fresh DB must be `scan` (binds workspace_id + root;
  `analyze`/`update` on a metadata-less DB errors).
- Exit-code mapping (capture stdout & stderr **separately**):
  - `0` → success: stdout is pretty-printed `ExtractReport` JSON. Parse it.
  - `1` → operation failed: stdout STILL holds an `ExtractReport` with `status=="failed"` + `errors[]` (covers
    first-call-not-scan, flock 30s timeout, root mismatch, newer-schema, data-loss guard). Throw
    `JulieExtractFailedException(report.Errors, stderr)`.
  - `2` → usage/argv error: **no stdout**; clap usage text on stderr. Throw `JulieExtractUsageException(stderr)`.
  - else → `JulieExtractException`.
  - `delete` of an absent file → `status=="not_found"`, exit **0** (tolerant — NOT a failure).
- Deserializes into ONE flat record (info reuses the same shape — counts come as `*_total`, NOT nested):
  ```csharp
  static readonly JsonSerializerOptions JsonOpts = new() {
      PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };
  public sealed record ExtractReport(
      [property: JsonPropertyName("status")] string Status,
      [property: JsonPropertyName("operation")] string Operation,
      [property: JsonPropertyName("db_path")] string DbPath,                          // serde renames `db`→`db_path`
      [property: JsonPropertyName("root")] string? Root,
      [property: JsonPropertyName("schema_version")] int? SchemaVersion,              // expect 26
      [property: JsonPropertyName("schema_state")] string? SchemaState,               // missing|older|current|newer
      [property: JsonPropertyName("extract_contract_version")] int? ExtractContractVersion, // expect 1
      [property: JsonPropertyName("analysis_state")] string? AnalysisState,
      [property: JsonPropertyName("files_scanned")] ulong FilesScanned,
      [property: JsonPropertyName("symbols_extracted")] ulong SymbolsExtracted,
      [property: JsonPropertyName("files_total")] ulong FilesTotal,                   // info counts land here
      [property: JsonPropertyName("symbols_total")] ulong SymbolsTotal,
      [property: JsonPropertyName("relationships_total")] ulong RelationshipsTotal,
      [property: JsonPropertyName("identifiers_total")] ulong IdentifiersTotal,
      [property: JsonPropertyName("types_total")] ulong TypesTotal,
      [property: JsonPropertyName("errors")] IReadOnlyList<ExtractError> Errors);
  public sealed record ExtractError(
      [property: JsonPropertyName("code")] string Code,
      [property: JsonPropertyName("message")] string Message,
      [property: JsonPropertyName("path")] string? Path);
  ```
- After deserialize, the runner can cross-check `SchemaVersion`/`ExtractContractVersion` against
  `MillerExtractContract` and surface a typed mismatch (julie only self-rejects a *newer* DB, so the gate is
  Miller's job — same constants as `JulieSchemaGate`).
- M1 surface: `Scan(root, db, force=false)`, `Info(db)`; `Update`/`Delete` thin (fully exercised in M3).

### 7. Restore (`scripts/restore-julie-server.sh` + `.ps1` + `scripts/julie-pins.json`)
- `julie-pins.json` — version + the 4 published triples with **download-verified** archive sha256s (julie
  publishes no checksum assets; these were captured from the release and are committed for reproducibility):
  ```json
  {
    "version": "7.12.2",
    "urlTemplate": "https://github.com/anortham/julie/releases/download/v{VER}/{asset}",
    "assets": {
      "aarch64-apple-darwin":    { "name": "julie-v7.12.2-aarch64-apple-darwin.tar.gz",    "sha256": "5113bac946a66dceda1508b075b410c14f5480c2b24422a571e13c67ce9eb034" },
      "x86_64-apple-darwin":     { "name": "julie-v7.12.2-x86_64-apple-darwin.tar.gz",     "sha256": "39122a57545fec313d1072775fe52596c017516269a1ee12c78170b466383381" },
      "x86_64-unknown-linux-gnu":{ "name": "julie-v7.12.2-x86_64-unknown-linux-gnu.tar.gz","sha256": "e2b8528e72c3ea549bad1b764df00134224f915c3e5811eb67dbaf2e80c995c1" },
      "x86_64-pc-windows-msvc":  { "name": "julie-v7.12.2-x86_64-pc-windows-msvc.zip",     "sha256": "fb5b5c1432a05a6b6073719db4227c08029be4b0a4d09e22b2828350e6d5c1d9" }
    }
  }
  ```
- Script: detect platform (`uname -s`/`-m`) → triple → asset/sha from pins → `curl -fsSL` to `.tools/` →
  **verify sha256** (`shasum -a 256 -c`) → extract **only `julie-server`** from the flat, multi-binary archive
  (`tar -xzf <f> julie-server`; the archive also holds `julie-adapter`+`julie-daemon` — don't extract them) →
  `chmod +x` → on macOS `xattr -d com.apple.quarantine` (ignore if absent) → `rm` the archive. `.ps1` mirror
  for Windows (`Expand-Archive`, `Get-FileHash`). `.tools/` is gitignored (already).
- **Fail loudly** on unsupported platforms: only 4 triples exist — NO linux-arm64, NO windows-arm64.
- CI calls the script before the Scale suite only (the default <10s suite needs no binary).

## csproj changes
- `src/Miller.Indexing/Miller.Indexing.csproj`: add `<PackageReference Include="Microsoft.Data.Sqlite"
  Version="10.0.8" />` (replace the "added in M1" comment). No `Batteries.Init()` call — Microsoft.Data.Sqlite
  (non-Core) bundles e_sqlite3 and self-inits (confirmed).
- `tests/Miller.Tests/Miller.Tests.csproj`: add the same `Microsoft.Data.Sqlite 10.0.8` PackageReference
  (tests synthesize the DB directly — explicit, not transitive).

## Test strategy
**Contract suite (default, < 10s):**
- `JulieDbFixture` test helper: creates a temp SQLite file, CREATEs `symbols` + `schema_version` +
  `external_extract_metadata` (+ minimal `files`) matching julie's verified DDL (symbols: `id TEXT PK, name
  TEXT NOT NULL, kind TEXT NOT NULL, language TEXT NOT NULL, file_path TEXT NOT NULL, signature TEXT,
  start_line INTEGER, start_col INTEGER, end_line INTEGER, end_col INTEGER, start_byte INTEGER, end_byte
  INTEGER, doc_comment TEXT, visibility TEXT, code_context TEXT, parent_id TEXT, metadata TEXT, content_type
  TEXT, file_hash TEXT, semantic_group TEXT, reference_score REAL NOT NULL DEFAULT 0.0`; schema_version:
  `version INTEGER PK, applied_at INTEGER NOT NULL, description TEXT NOT NULL`; external_extract_metadata:
  `key TEXT PK, value TEXT NOT NULL, updated_at INTEGER NOT NULL`). INSERTs ~12 known rows with realistic
  MD5-hex ids (mixed kinds/languages, some NULL signatures, at least one NULL start_line, parent/child pairs
  via parent_id, distinct file paths) and the version rows (`schema_version`→26, metadata
  `extract_contract_version`→'1'). NOTE: `is_test` is NOT a column (it is path-derived in julie) — do not add it.
- `SqliteSymbolReaderTests`: reads the fixture → asserts exact `IndexedSymbol` list (DocId ordinals 0..n-1,
  opaque ids retained, NULLs mapped to null, 1-based StartLine). Asserts directory-not-writable throws.
- `JulieSchemaGateTests`: passes at (26,1); throws `IncompatibleExtractException` at (27,1), (26,2), and when
  `schema_version`/`external_extract_metadata` is missing — assert the message names the offending value.
- `MillerRepositoryIndexTests` (end-to-end): build from the fixture → query a known term → assert ranked
  order + exact-name boost on the known rows; `Resolve(hit.Document.DocId).SymbolId` returns the right id.
- `JulieExtractRunnerTests`: argv builder produces the exact `«recon argv»` for scan/info (no binary needed);
  report parser deserializes sample scan/info JSON to the records; exit-code→outcome mapping (0/1/2) via the
  parser + a fake process result. (No live process in the default suite.)

**Scale suite (`[Trait("Category","Scale")]`, excluded by default, D6):**
- `LiveExtractIndexTests`: restore julie-server (or skip with a clear message if `.tools/` empty) → `Scan` a
  small bundled fixture repo → read → build → assert a known symbol is found. Network/binary dependent; never
  in the < 10s gate.

Banned-test discipline (CLAUDE.md): assert on values, cover NULL + error paths (gate failures, missing dir,
exit 1/2), parameterize the row cases — no smoke-only or tautological tests.

## Implementation order (strict TDD, dependency order)
1. csproj: add Microsoft.Data.Sqlite to Indexing + Tests. Build green (no new code yet).
2. `JulieDbFixture` test helper (synthesize the tiny DB).
3. `MillerExtractContract` + `JulieSchemaGate` (+ `IncompatibleExtractException`) — red→green via gate tests.
4. `IndexedSymbol` + `SqliteSymbolReader` — red→green via reader tests.
5. `MillerRepositoryIndex` — red→green via end-to-end query test.
6. `JulieExtractRunner` + report records — red→green via argv/parser/exit tests.
7. Restore scripts + `julie-pins.json` + `.gitignore` confirm `.tools/` ignored (already is).
8. `LiveExtractIndexTests` (Scale-tagged).

**Verify:** `dotnet build Miller.slnx -c Release` → 0 warnings/0 errors (TreatWarningsAsErrors). `dotnet test
--filter "Category!=Scale"` → all green, < 10s. CI unchanged (already filters Category!=Scale).

**Exit:** the read core works and is fast; a synthesized DB yields correct ranked queries; the extract wrapper
+ restore exist for the Scale path and M3.
