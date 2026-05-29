# M2 — MCP host + `search` + `inspect` + telemetry (first dogfood)

Implementation spec for M2. All contracts below are verified against the ModelContextProtocol C# SDK 1.3.0
(decompiled + empirically run), `docs/findings/miller-toolbox.md` (the agreed tool surface), and the M1 read
layer. Grounded; no `«recon»` placeholders.

## Goal
Make Miller usable from Claude Code for **find + inspect** (82% of real usage). Build the in-memory index at
startup from a julie extract DB, serve `search` + `inspect` over MCP stdio, and record a telemetry row for
every tool call. **Exit: I can use Miller daily for search + inspect.**

## The seam
- **Miller.Core** untouched (zero-I/O). **Miller.Indexing** gains on-demand detail reads (it already owns the
  SQLite read). **Miller.Server** gains the tools, telemetry, smart-string resolution, and startup wiring.
- Default test suite stays < 10s; anything needing the live julie-server binary is `[Trait("Category","Scale")]`.

---

## Decision log (resolved contradictions — do not re-litigate)
1. **Telemetry = ONE central `CallToolFilter`, NOT per-tool scopes.** Verified: `builder.WithRequestFilters(f =>
   f.AddCallToolFilter(...))` wraps every `tools/call` including reflection-discovered (`WithToolsFromAssembly`)
   tools — the SDK builds a composite CallTool handler and `BuildFilterPipeline` wraps it; empirically fired
   4/4 (greet/stats/boom/unknown). The contrary "filters only wrap the not-found fallback" claim was an
   unverified reading of XML-doc wording and is **wrong**. **Pin it with a test** (see Testing) that asserts the
   filter records a telemetry row for a discovered tool; the `TelemetryScope` API is the fallback path if it
   ever regresses.
2. **`search` `mode` enum = `auto|text|symbol|file`** (miller-toolbox.md L74), NOT `and/or`. Core's
   `SearchMode{Or,And}` is an internal axis (default `Or`); AND is never surfaced. `mode=auto` picks the
   *interpretation* (path markers → file; single identifier-ish token → symbol; multi-word → phrase).
3. **`inspect depth=full` ships fully in M2** = refs + one-hop callers/callees + children + body (the deep_dive
   equivalent). The **resolved** cross-reference graph + cross-language bridge stays M4 `trace` (that's the
   milestone boundary, not a cut). Refs are **name-based** because `identifiers.target_symbol_id` is ALWAYS
   NULL at extract (resolution is the consumer's job — julie's fast_refs works the same way).
4. **Test-ness is cross-language: consume julie's persisted `is_test`, do NOT re-derive a per-language heuristic.**
   VERIFIED against a live v7.12.2 extract: julie persists `is_test` into `symbols.metadata` (compact JSON, key
   present **only when true**) for every language its `crates/julie-extractors/src/test_detection.rs` covers
   (go/python/csharp/… confirmed: a `[Fact]` method → `{"is_test":true}`). This is the 34-language canonical
   signal — Miller's read layer surfaces it. BUT julie's detection is **symbol-level** (e.g. `detect_csharp`
   flags `[Fact]`/`[Theory]` *methods*, NOT `[TestClass]` *classes* — verified, and confirmed by the julie
   enrichment plan line 172). So a test class and non-test helpers inside a test file get no `is_test`. Miller
   therefore ORs in a **language-agnostic path fallback** for that residual:
   `IsTest(symbol) = symbol.IsTest /*julie metadata, all langs*/ || IsTestPath.Check(symbol.FilePath) /*fallback*/`.
   The fallback must be language-agnostic (path segments + filename test-boundaries), NOT a switch over a handful
   of extensions — that narrow scoping is the exact anti-pattern the cross-language principle (CLAUDE.md
   "Multi-Language: scope for every capable language") forbids.

---

## MCP SDK 1.3.0 facts (the patterns Miller adopts)
- Tools: a `sealed class` with `[McpServerToolType]`; each tool a method with `[McpServerTool(Name="...")]` +
  `[Description("...")]`. `WithToolsFromAssembly()` (already in Program.cs) discovers them.
- **Instance tool methods** are created per-call via `ActivatorUtilities.CreateInstance(request.Services, type)`
  → **constructor injection works** (per-call instance). Use ctor injection for the index + readers + telemetry.
- **Method-parameter DI**: `CancellationToken`, `IServiceProvider`, `McpServer`, `IProgress<…>`,
  `[FromKeyedServices]`, and any DI-registered service param are injected and **excluded from the input schema**.
- Param descriptions via `[System.ComponentModel.Description]`; defaults → non-required (with `default` in
  schema). Tool description from `[Description]` on the method.
- Return: `string` → one text block; a record/object → JSON-serialized text block. Miller tools return the
  rendered **compact string** (or the JSON string when `format=json`) — keep marshalling simple, one text block.
- **STDIO purity**: nothing may touch stdout except the protocol. Program.cs already routes Serilog Console to
  stderr — keep all logging there; never `Console.WriteLine`. The julie-server subprocess's stdout is captured
  by `JulieExtractRunner` (RedirectStandardOutput), so it never leaks.
- Exceptions: a non-`McpException` thrown by a tool → generic redacted message to client (real message logged
  server-side). So tools should catch, set telemetry outcome, and return a clean compact error string rather
  than throwing raw.

---

## Components

### 1. `WorkspaceContext` (Miller.Server, singleton)
Holds the resolved paths/ids so tools + readers + startup share one source of truth:
```csharp
public sealed record WorkspaceContext(
    string WorkspaceRoot,   // Environment.CurrentDirectory (the repo Claude Code launched us in)
    string ExtractDbPath,   // <root>/.miller/symbols.db   (julie extract; Miller reads Mode=ReadOnly)
    string TelemetryDbPath, // <root>/.miller/telemetry.db (Miller-owned, writable)
    string ToolsRoot,       // AppContext.BaseDirectory/.tools  (where pinned julie-server ships — NOT the repo)
    string? WorkspaceId);   // from external_extract_metadata after scan (nullable until known)
```

### 2. Read-layer extensions (Miller.Indexing — on-demand, keep the index lean)
The in-memory `IndexedSymbol` stays lean, with **one** added field: `bool IsTest` (the cross-language test
signal, decision-4). The M1 bulk SELECT in `SqliteSymbolReader.Read` gains the `metadata` column:
`SELECT id, name, signature, kind, language, file_path, start_line, parent_id, metadata FROM symbols WHERE
name IS NOT NULL ORDER BY file_path, start_line, id;` (column order LOCKED to the GetX ordinals). `IsTest` is
parsed from `metadata` **only when the raw text contains `"is_test"`** (a cheap `Ordinal` substring guard so the
~90% of rows without it skip JSON parsing entirely); when present, parse the JSON and read the `is_test` boolean
(julie writes it only when true, compact serde). Absent/false/unparseable → `false`. `ToSearchableDocument()` is
UNCHANGED (is_test is not a scoring field); the `Build` DocId contract and ordering are unchanged. The
`JulieDbFixture` test DB DDL must add the `metadata TEXT` column and seed `{"is_test":true}` rows.
Detail/refs are fetched on demand per inspect call (inspect is far lower-volume than search). Add to Miller.Indexing:
- `SymbolDetail ExtractReader.ReadDetail(string dbPath, string symbolId)` →
  `SymbolDetail(string? DocComment, string? Visibility, string? CodeContext, int? BodyStartByte, int? BodyEndByte, int? BodyStartLine, int? BodyEndLine)`
  via `SELECT doc_comment, visibility, code_context, body_start_byte, body_end_byte, body_start_line, body_end_line FROM symbols WHERE id = $id`.
- `IReadOnlyList<SymbolRef> ExtractReader.ReadReferences(string dbPath, string name)` →
  `SELECT name, kind, file_path, start_line, containing_symbol_id FROM identifiers WHERE name = $name` (name-based;
  `target_symbol_id` is NULL). callers = distinct `containing_symbol_id`; callees =
  `... WHERE containing_symbol_id = $targetId AND kind = 'call'`.
- `string? ExtractReader.ReadBody(string dbPath, string filePath, int startByte, int endByte)` → `SELECT content
  FROM files WHERE path = $path`, then slice `[startByte, endByte)` (files.content holds full file text — verified).
  Fall back to a line-slice if byte spans are NULL.
- `ExtractReader` opens `Mode=ReadOnly` (reuse the M1 connection-string + writable-dir discipline). All queries
  parameterized. These run AFTER the gate already passed at startup, so they can skip re-gating (or cheaply re-open).

### 3. `SmartTargetResolver` (Miller.Server or Indexing)
Resolves a `target`/`query` string per miller-toolbox.md L47-56:
```
1. contains '/' OR '\'                                  → FILE path  (language-agnostic)
2. matches an id shape (32-hex MD5 | contains '::' | starts 'file_')  → SYMBOL ID, use directly (no search)
3. matches an indexed file path (exact, or unique basename) → FILE path
4. has an extension that appears among indexed file paths   → FILE path  (fallback, non-indexed file)
5. otherwise → SYMBOL NAME → name lookup in the index; 0 → not found; 1 → that symbol; >1 → return candidates
```
**Cross-language (decision-4 / CLAUDE.md):** rules 3–4 REPLACE the old hardcoded ~22-extension whitelist
(`.cs .ts .py …`) — that named a handful of languages out of julie's 34 and is exactly the narrow-scoping the
principle forbids. The "is this a file" decision is instead **derived from the indexed data itself**: the index
exposes `bool IsIndexedFilePath(string)` and a `KnownExtensions` set computed at `Build` from the distinct
extensions of every indexed `FilePath`. That set is precisely the languages julie actually emitted for THIS
repo — all-language, zero hardcoding, self-updating as julie adds languages. `Math.PI`-style dotted names whose
extension (`.PI`) was never indexed correctly fall through to a NAME lookup (rule 5).
Overrides: `scope` (constrain a name to a file before disambiguating), `as="symbol"|"file"` (force the kind).
Returns a discriminated result (File path | Symbol(IndexedSymbol) | Candidates(list) | NotFound).
`MillerRepositoryIndex` gains `IsIndexedFilePath(path)` (O(1) over `_byFilePath`) and `KnownExtensions`
(`IReadOnlySet<string>`, lowercased leading-dot, built in `Build`).

### 4. `search` tool (`[McpServerToolType] SearchTool`)
Description (carries the steer): *"Search indexed code and return ranked results. Use this before shell
rg/grep/cat or reading whole files. Pass a symbol name, an identifier, or a natural-language phrase. Test code
is hidden for natural-language queries unless you ask for it. Returns compact text by default; pass format=json
to chain results."*

| param | req | default | type | notes |
|---|---|---|---|---|
| `query` | ✅ | — | string | name / identifier / phrase |
| `mode` | | `auto` | `auto\|text\|symbol\|file` | interpretation axis |
| `limit` | | `10` | int | |
| `exclude_tests` | | `null` | `bool?` | tri-state |
| `format` | | `compact` | `compact\|json` | |

- Maps to `MillerRepositoryIndex.Search(query, limit, SearchMode.Or)`. Ordering is already score-DESC, DocId-ASC
  with the 1.5x exact-name boost — **do not re-sort in the renderer**.
- `exclude_tests`: `null` → hide test rows **only** for NL-phrase queries lacking test/def intent; `true` →
  always hide; `false` → always include. The test predicate is **cross-language (decision-4)**:
  `IsTest(sym) = sym.IsTest || IsTestPath.Check(sym.FilePath)`. `sym.IsTest` is julie's persisted 34-language
  `is_test` (primary, AST-accurate, symbol-level). `IsTestPath` is the **language-agnostic** fallback for what
  julie's symbol-level detection misses (test classes, helpers in test files): directory segments
  (`test|tests|__tests__|spec|specs|testdata|fixtures`) + filename test-boundaries — a stem matching
  `(?i)(^|[._-])(test|tests|spec|specs)([._-]|$)` or a PascalCase `…(Test|Tests|Spec|Specs)$` boundary, applied
  **regardless of extension** (NO per-language `.cs/.go/.py` switch — that covers go/python/csharp/java/kotlin/
  ts/js/ruby/rust/… uniformly). The fallback is lossy (its `fixtures`/PascalCase rules over-match), so the `null`
  default only auto-hides for NL phrases to bound false positives; the precise `is_test` signal carries no such
  caveat.
- compact row (one line, no blank lines): `<name>  <kind>  <file>:<line>  <signature?>` (truncate signature
  ~100-120 chars). Empty → `No results.` Over `limit` → append `… N more (raise limit)`, never silently drop.
- json: array of `{name, kind, file, line, signature, score, symbol_id}`.

### 5. `inspect` tool (`[McpServerToolType] InspectTool`)
Description: *"Inspect a file or symbol you can already name. Give a file path to list its symbols, or a symbol
name to see its definition, signature, and docs. Add depth=full to also get references, callers/callees, and the
body. Use this before reading an entire file."*

| param | req | default | type | notes |
|---|---|---|---|---|
| `target` | ✅ | — | string | smart-resolved path or symbol |
| `depth` | | `summary` | `summary\|full` | |
| `kind` | | `null` | `string?` | filter a file listing (function/class/...) |
| `scope` | | `null` | `string?` | disambiguate an ambiguous name to a file |
| `limit` | | `50` | int | for file listing |
| `format` | | `compact` | `compact\|json` | |

- Resolve `target` via `SmartTargetResolver`.
  - **File + summary**: list the file's symbols (kind, name, line, signature?), `kind`-filtered, `limit`-bounded.
    Unknown path → `No indexed symbols in <path>` (not an error). The index can answer this by filtering
    `IndexedSymbol.FilePath == path`.
  - **Symbol + summary**: name, kind, signature, `file:line`, doc_comment (via `ReadDetail`).
  - **Symbol + full**: summary **plus** children (`IndexedSymbol` where `ParentId == target.SymbolId`), refs
    (`ReadReferences(target.Name)` → file:line list), one-hop callers (distinct containing symbols of those
    refs) / callees (`ReadReferences` by containing=target.id, kind=call), and body (`ReadBody`). If body spans
    are NULL, omit body with a one-line note (graceful degradation).
  - **Candidates** (ambiguous name): return the candidate list (name + file:line + kind) — never pick-first,
    never error.
- compact = terse markdown; json = `{symbol|file, children[], refs[], callers[], callees[], body?}`.
- **Token note:** inspect is the heaviest consumer (julie get_symbols = 21.6 MB). `limit=50` + signature
  truncation are load-bearing.

### 6. Telemetry (`Miller.Server.Telemetry`)
- **`TelemetryLedger`** (singleton): owns a writable `Mode=ReadWriteCreate` connection to
  `<root>/.miller/telemetry.db`; sets `PRAGMA journal_mode=WAL; synchronous=NORMAL; busy_timeout=…` on connect
  (WAL pragmas are per-connection); creates the table; exposes `Measure(tool, op) → TelemetryScope` and
  best-effort `Record(in TelemetryRecord)` that **never throws** (swallows + `DroppedWrites++`). Prepared INSERT,
  reused. `Prune(retentionDays=30)`.
- **DDL** (STRICT, append-only):
  ```sql
  CREATE TABLE IF NOT EXISTS tool_telemetry (
      id TEXT PRIMARY KEY, ts TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
      tool TEXT NOT NULL, op TEXT, workspace_id TEXT,
      duration_ms INTEGER NOT NULL CHECK (duration_ms >= 0),
      outcome TEXT NOT NULL CHECK (outcome IN ('ok','empty','error')), error_kind TEXT,
      result_count INTEGER,
      bytes_examined INTEGER NOT NULL DEFAULT 0 CHECK (bytes_examined >= 0),
      bytes_returned INTEGER NOT NULL DEFAULT 0 CHECK (bytes_returned >= 0),
      source_bytes  INTEGER NOT NULL DEFAULT 0 CHECK (source_bytes >= 0),
      est_tokens INTEGER, index_fresh INTEGER CHECK (index_fresh IS NULL OR index_fresh IN (0,1)),
      target_hash TEXT, metadata_json TEXT NOT NULL DEFAULT '{}'
  ) STRICT;
  CREATE INDEX IF NOT EXISTS idx_tool_telemetry_ts ON tool_telemetry(ts);
  CREATE INDEX IF NOT EXISTS idx_tool_telemetry_tool ON tool_telemetry(tool);
  ```
- **Central capture via the `CallToolFilter`**: wraps every call → `Measure(toolName, op)`; after the inner
  handler returns, set `outcome` (IsError → `error`; otherwise `empty` if zero results else `ok`),
  `bytes_returned` (serialized content length), `est_tokens` (`TokenEstimator.Count` — M2 uses a UTF-8 bytes/4
  heuristic behind the seam; swappable for `Microsoft.ML.Tokenizers` later), `index_fresh` (mtime check), and
  `duration_ms` (`Stopwatch.GetElapsedTime`, clamp ≥0). `id = Guid.CreateVersion7()` (time-ordered).
- **`result_count` / `bytes_examined`** (tool-internal): exposed to the filter via an `AsyncLocal<TelemetryScope>`
  current-scope the tool body may enrich (`Telemetry.Current?.ResultCount = n`). M2 sets `result_count`;
  `bytes_examined` may stay 0 (work-proxy, lower priority than the bytes_returned/est_tokens north-star KPI).
- **Privacy**: store `target_hash = SHA256(query/target)`, NEVER the raw string (secrets/bloat). The Serilog
  debug log may carry a truncated/hashed query separately.
- **Must NOT** write into the julie extract DB (it's Mode=ReadOnly and `scan --force` recreates it).

### 7. Startup wiring (Program.cs + `IndexBootstrapService : IHostedService`)
Register BEFORE the `AddMcpServer` chain so the index is ready before the MCP host accepts calls (the generic
host runs hosted-service `StartAsync` in registration order; `WithStdioServerTransport` adds its own hosted
service after):
```
StartAsync:
  ctx = WorkspaceContext from Environment.CurrentDirectory + AppContext.BaseDirectory/.tools
  Directory.CreateDirectory(<root>/.miller)
  runner = JulieExtractRunner.Locate(ctx.ToolsRoot)          // FileNotFoundException(restore msg) if absent → fail loudly
  if (!File.Exists(ctx.ExtractDbPath)) runner.Scan(ctx.WorkspaceRoot, ctx.ExtractDbPath)   // one-time; File.Exists, NOT info
  symbols = SqliteSymbolReader.Read(ctx.ExtractDbPath)
  index = MillerRepositoryIndex.Build(symbols)
  publish index singleton (a holder) ; log timing to stderr/file
```
DI: `WorkspaceContext` (singleton), `MillerRepositoryIndex` (singleton via holder set by the bootstrap),
`ExtractReader` (singleton), `TelemetryLedger` (singleton), `SmartTargetResolver` (singleton). Re-scan /
watcher / incremental freshness is **M3** — M2 does one initial scan + build. `index_fresh` for M2 = compare a
touched file's disk mtime vs `files.last_modified` (NULL if unknown); the content-hash check is M3.

---

## Test strategy
**Default suite (< 10s, no julie-server binary):**
- `SqliteSymbolReaderTests` (extend M1): a `metadata` row `{"is_test":true}` → `IndexedSymbol.IsTest==true`;
  absent metadata / `{}` / non-test JSON / malformed JSON → `false`; assert across ≥3 languages (go/python/csharp
  rows) to pin the cross-language consumption, not just C#.
- `SmartTargetResolverTests`: file (`/`, `\`); **indexed-path → file** and **indexed-extension → file** (rules
  3–4, NOT a hardcoded list — seed the fixture with e.g. `.rs`/`.go`/`.vue` paths and assert they resolve as
  files purely because they're indexed); a dotted name with a non-indexed extension (`Math.PI`) → NAME; id-shape
  (32-hex, `::`, `file_`); name→1, name→>1 (candidates); `scope`/`as` overrides; NotFound.
- `IsTestPathTests`: language-agnostic positive cases across conventions (`*_test.go`, `test_x.py`, `XTests.cs`,
  `x.test.ts`, `x_spec.rb`, `tests/` dir, `__tests__/`) + negative cases (`fastest.cs`, `contest.py`,
  `latest.ts`, `attestation.go` must NOT match) incl. the lossy `fixtures`/PascalCase edges. Parameterized.
- `SearchToolTests` (against a synthesized `JulieDbFixture` index, reusing M1's fixture): compact + json
  rendering, `limit` + `… N more`, `exclude_tests` tri-state (null/true/false) behavior driven by BOTH a
  julie-`is_test` row (a `[Fact]`-style method flagged via metadata) AND a path-only test row (a `*Tests.cs`
  class with no metadata flag) — proving the `||` predicate, empty → `No results.`, ordering preserved (don't re-sort).
- `InspectToolTests` (synthesized fixture, extended with identifiers + a file row): file→symbols (kind filter,
  limit), symbol→summary (doc_comment), symbol→full (children via parent_id, name-based refs, callers/callees,
  body slice), ambiguous→candidates, unknown path note, NULL-body graceful note.
- `ExtractReaderTests`: ReadDetail (NULLs), ReadReferences (name-based, multiple), ReadBody (byte slice + NULL
  fallback) against the synthesized DB.
- `TelemetryLedgerTests`: table created; a `Measure` scope writes one row with the right outcome (ok/empty/error),
  duration ≥0, est_tokens, target_hash (not raw), append-only; `Record` never throws on a bad row; `Prune`.
- **`CallToolFilterTelemetryTests` (the decision-1 pin):** stand up an in-process MCP server with a trivial
  `[McpServerTool]` registered via `WithToolsFromAssembly` + the telemetry `CallToolFilter`, invoke the tool
  through the SDK, and **assert a `tool_telemetry` row was recorded** — proving the central filter fires for
  reflection-discovered tools. If this can't be done in-process cheaply, tag it Scale; but the assertion must
  exist. If it fails, fall back to per-tool `using Measure()` and document it.

**Scale suite (`[Trait("Category","Scale")]`, excluded by default):**
- `LiveSearchInspectTests`: restore julie-server → startup bootstrap scans a tiny throwaway repo → `search` +
  `inspect` (summary + full) return correct results → a `tool_telemetry` row exists per call. End-to-end proof.

Banned-test discipline: assert on rendered values, cover empty/ambiguous/NULL/error paths, parameterize, no
smoke-only/tautological tests, keep the default suite green incl. the existing 90 tests.

## Implementation order (strict TDD)
1. csproj: Miller.Server already has ModelContextProtocol; add `Microsoft.Data.Sqlite 10.0.8` to Miller.Server
   (telemetry write) — or keep telemetry in Miller.Indexing which already references it. Tests project unchanged.
2. `IsTestPath` + `SmartTargetResolver` (pure-ish, fast) → red→green.
3. `ExtractReader` (ReadDetail/ReadReferences/ReadBody) against the extended fixture → red→green.
4. `TelemetryLedger` + `TelemetryScope` + DDL → red→green.
5. `SearchTool` + compact/json renderers → red→green.
6. `InspectTool` (summary + full) → red→green.
7. `CallToolFilter` telemetry interceptor + the in-process filter-fires test → red→green.
8. `WorkspaceContext` + `IndexBootstrapService` + Program.cs DI wiring.
9. `LiveSearchInspectTests` (Scale).

**Verify:** `dotnet build Miller.slnx -c Release` → 0/0. `dotnet test --filter "Category!=Scale"` → all green
(existing 90 + new), < 10s. Then attempt the live Scale path. Leave changes uncommitted for the lead.

**Exit:** point Claude Code at Miller (mcp-config.json already set) → `search` + `inspect` work on a real repo;
a `tool_telemetry` row lands per call. Dogfooding starts.
