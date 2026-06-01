# Miller → julie-extractors Migration — Design

- **Date:** 2026-06-01
- **Status:** Approved for implementation planning (brainstorming complete)
- **Author:** design session (Alan + Claude)
- **Supersedes pin:** `julie-server` 7.13.2 (schema 28 / extract_contract_version 3)
- **Targets:** `julie-extract` v1 (CLI contract 1 / SQLite schema 1 / extract contract 1 / JSONL 1 / report 1)

## 1. Context

Miller is a read-only .NET consumer of julie's extraction output. Today it spawns the pinned
`julie-server extract` binary and reads the SQLite DB it produces. julie-extractors is the new
standalone extraction product: a single `julie-extract` CLI (`scan`/`update`/`delete`/`info`/`export`/
`languages`) that emits a versioned SQLite artifact (schema v1) plus an optional JSONL export. It
deliberately drops everything that is not extraction: no MCP, no daemon, no search index, no embeddings,
no workspace registry, no watcher, no analysis/test-quality tables.

This migration moves Miller's entire julie-facing seam onto that new contract. It is **not mechanical**
(every read path renames/reshapes at once) but it requires **no new Miller subsystems** — Miller already
owns everything julie-extract drops.

A full comparative analysis (66-agent workflow, adversarially verified) is saved at
`.memories/2026-06-01/julie-extractors-migration-analysis.json`. This document is the design distilled
from it plus the decisions made in the design session.

## 2. Goals / Non-goals

**Goals**
- Miller reads/produces a `julie-extract` v1 artifact instead of `julie-server` 7.13.2.
- All existing tool behavior (search, inspect, trace, impact, context, edit, workspace) is preserved
  (parity), including accurate file paths and 1-based line numbers.
- Fail loud on contract drift (Miller's existing philosophy); no silent misreads.
- Do not foreclose the planned content search.

**Non-goals (this migration)**
- Incremental in-memory index patching (stays full-reload; see §13 + `TODO.md`).
- Building a content/full-text (FTS) search index (deferred; architecture chosen below; see §6).
- Adding embeddings, a daemon, or Tantivy.
- Re-pinning/altering julie-extractors itself (it stays a content-free structural producer).

## 3. Decisions (locked in the design session)

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | **Acquisition: download prebuilt assets.** Restore downloads pinned `julie-extract` release assets (assets shipping today). From-source remains the fallback. | Matches the existing `julie-server` restore shape; unblocks once assets publish. |
| D2 | **File content: Miller reads from disk.** julie-extract stays content-free (`content_bytes` count + `content_hash` only). Miller reads body text from disk for `inspect(full)` and the edit baseline. | Keeps julie a clean structural producer; byte spans make disk slicing trivial; edit-freshness via `content_hash` is cleaner. |
| D3 | **Content search: Option B, deferred.** When built, Miller owns the content index end to end, sourcing text from disk using julie's `files` table as the manifest + `content_hash` for staleness. Not built in this migration. | julie owns structure, Miller owns search (per `workspace-registry-freshness-design.md` D12). Disk-read is the disk-lean, on-architecture choice; the double-read penalty is marginal vs the index build. |
| D3a | **Interim search win (SCOPE EXPANSION — not parity; opt-in):** widen the in-memory BM25 index to also ingest `doc_comment`, `identifiers.code_context`, and `literals.literal_text`. | Covers a large fraction of "find this text" cases cheaply; lets us dogfood before committing to a trigram FTS. **Codex review flagged this is not parity-only** — it changes search semantics and broadens bridge ingestion. Default this migration to pure parity; D3a rides along only if Alan opts in (open question §16). |
| D4 | **Test signal: drop `TestRole` string → `bool IsTest`,** read from the typed `symbols.is_test` column. `test_container`/`test_lifecycle` available for later. | v1 promotes the signal to indexed booleans; Miller only ever used `IsTest` as a predicate. Kills the JSON-parse/substring hack; enables `WHERE is_test = 0` pushdown. |
| D5 | **Scope: parity port.** Keep full-reload-on-change; defer incremental. | Migration is already a wide seam rewrite; bundling a graph-consistency feature multiplies risk. v1's richer change delta lands *with* this migration, enabling incremental later. |
| D6 | **Line/path robustness:** switch the SQLite readers from positional-ordinal reads to **by-name** reads (`GetOrdinal("…")`), and add a fixture test asserting exact `path:line`. | We rewrite every SELECT for renames anyway; by-name reads permanently close the silent column-drift trap. |
| D7 | **Version gate:** gate on `sqlite_schema_version` + `extract_contract_version` (both `1`), NOT on `binary_version`. Pin a download version separately for restore only. | v1's `binary_version` is unreliable today (working-tree 2.0.0 vs on-disk 0.1.0); schema/contract versions are the stable contract. |

## 4. Contract delta

### 4.1 Invocation (CLI)

Binary `julie-server` → `julie-extract`. The `extract` parent subcommand is **gone**; the six ops are
top-level. The `--workspace-id` flag does **not exist** in v1 and must be dropped.

| Op | OLD (`JulieExtractRunner.cs`) | NEW (`julie-extract`) |
|---|---|---|
| Scan | `extract --db D --root R --workspace-id ID --json scan [--force]` | `scan --root R --db D [--force] [--strict-schema] [--json]` |
| Info | `extract --db D --json info` | `info --db D [--strict-schema] [--json]` |
| Update | `extract --db D --root R --json update --file F` | `update --root R --db D --file F [--strict-schema] [--json]` |
| Delete | `extract --db D --root R --json delete --file F` | `delete --root R --db D --file F [--strict-schema] [--json]` |

**Exit codes:** v1 is `0/1/2/3`. `3` = incompatible schema/root/contract. Miller's `Interpret()` maps any
non-0/1/2 to a generic crash → must add a `case 3` that reads `errors[0].code` →
`IncompatibleExtractException`. Path errors (`file_outside_root`, `invalid_path`, `file_not_found`) are
exit `1`, so branch on `errors[].code`, not the exit code alone.

**`--strict-schema`:** adopt it on all *artifact* commands (`scan`/`update`/`delete`/`info`/`export`).
`languages` does not accept it (and Miller does not call `languages`). It makes a drifted/older artifact
fail fast (exit 3) instead of silently migrating — aligns with Miller's fail-loud rule.

### 4.2 JSON report

OLD = flat (`ExtractReport.cs`: `schema_version`, `hash_algorithm`, `revision`, `workspace_id`, … at top
level). NEW = nested:

```
{ report_schema_version, status, operation, mode,
  input{ db_path, root_path, file_path, root_relative_path, format, output_path },
  artifact{ db_path, root_path, artifact_id, schema_version, extract_contract_version,
            sqlite_schema_version, jsonl_schema_version, hash_algorithm,
            parser_inventory_fingerprint, capability_snapshot_fingerprint },
  tool{ binary_name, binary_version },
  revision{ latest_revision_id, created_revision_id },
  counts{ files_scanned, files_changed, files_unchanged, files_unsupported, files_deleted,
          files_failed, rows_written{…18 domains}, totals{…18 domains} },
  errors[], warnings[] }
```

`ExtractReport` must be rewritten nested. `ExtractError` → `ReportDiagnostic{ code, message, path,
root_relative_path, recoverable, details }`. Read schema/contract/hash from `report.artifact.*`. A null
`artifact` block is a gate failure, not a silent pass.

**Revision mapping (tighten):** the freshness cursor is `report.revision.latest_revision_id` (present after
any scan). `created_revision_id` is **null on a no-op scan** (no mutation), so use it only to detect whether
*this* call mutated — never as the cursor. Preserve the existing `report.Revision ?? <read latest from DB>`
fallback semantics that `WorkspaceTool` and `CrossWorkspaceRefreshService` rely on (see §10).

Transient error code rename: `flock_timeout` → `lock_timeout`. Prefer the new per-diagnostic
`recoverable: bool` over a hardcoded transient-code set.

### 4.3 SQLite schema

Convention is unchanged (verified): **lines 1-based, columns 0-based, byte offsets 0-based** in both old
julie and v1 — **no off-by-one risk.** v1 also makes `symbols.start_line` NOT NULL.

| OLD table.column | NEW v1 | Status | Miller impact |
|---|---|---|---|
| `external_extract_metadata` (key,value,updated_at) | `artifact_metadata` (key,value) | rename + drop `updated_at` column | `JulieSchemaGate`, `ExtractFileHashReader`, `ExtractReader` |
| `schema_version` table (`MAX(version)`) | gone — `artifact_metadata` keys `sqlite_schema_version`/`schema_version` | drop table | `JulieSchemaGate` — **first failure point** today |
| schema 28 / contract 3 | sqlite_schema 1 / extract_contract 1 | version collision | `MillerExtractContract` (re-pin 1/1) |
| `symbols.id` | `symbols.symbol_id` | rename | `SqliteSymbolReader`, `ExtractReader`, `SqliteBridgeReader` |
| `symbols.parent_id` | `symbols.parent_symbol_id` | rename | `SqliteSymbolReader` |
| `symbols.file_path` | `symbols.path` | rename | `SqliteSymbolReader`, `WorkspaceIndexFactsReader`, `SqliteBridgeReader` |
| `symbols.metadata` (JSON) | `symbols.metadata_json` | rename | `SqliteSymbolReader` |
| `symbols.metadata.is_test`/`test_role` (JSON) | typed `symbols.is_test` / `test_container` / `test_lifecycle` (indexed; still mirrored in `metadata_json`) | promoted to columns; `test_role` gone | read typed `is_test`; drop `TestRole` (D4) |
| `identifiers.id` | `identifiers.identifier_id` | rename | `ExtractReader` (ORDER BY) |
| `identifiers.file_path` | `identifiers.path` | rename | `ExtractReader` |
| `files.hash` (bare blake3 hex) | `files.content_hash` (`blake3:<hex>` **prefixed**) | rename + value-format change | `ExtractFileHashReader`, `FreshnessGate`, `StalenessCheck` — strip prefix before compare |
| `files.content` (TEXT) | gone — `content_bytes` (count) | **drop file text** | `ExtractReader.ReadBody`/`ReadIndexedFileText` → re-source from disk (D2) |
| `relationships` row (`id`, `file_path`, `line_number`, `metadata`, `created_at`) | `relationship_id`, `path`, full span cols, `metadata_json`, no `created_at` | row reshaped | **Miller reads only `from_symbol_id, to_symbol_id, kind` (`SymbolGraphReader.cs:78`) — those three survive unchanged, so NO Miller breakage. The reshaped columns are unread.** |
| `type_arguments(identifier_id, file_path, parent_arg_id, id)` | `type_arguments(type_argument_id, usage_id, parent_type_argument_id, ordinal, type_name)` + new `type_argument_usages(usage_id, identifier_id, file_id, path, language)` | restructure | `SqliteBridgeReader` — JOIN `type_argument_usages` |
| `literals.id`/`file_path` | `literals.literal_id`/`path` | rename | `SqliteBridgeReader` |
| `symbol_annotations` (`id`, `ordinal`, …) | `annotation_id`, … — **no `ordinal` column** | rename + **drop `ordinal`** | `SqliteBridgeReader.cs:138` reads `ordinal` and `ORDER BY symbol_id, ordinal, id` → breaks on two columns. **Decision required:** re-key deterministic order to `(symbol_id, annotation_id)`; accept that annotation order becomes opaque-id order, not the old insertion `ordinal`. |
| `canonical_revisions(revision, workspace_id)` | `extraction_revisions(revision_id, …)` — no workspace_id | replace | `FreshnessReader` |
| `revision_file_changes(revision, workspace_id, file_path, change_kind∈{added,modified,deleted})` | `revision_file_changes(revision_id, file_id, path, change_kind∈{inserted,updated,deleted,unsupported})` | re-key + new vocab | `FreshnessReader.ChangedSince` + `ParseChangeKind` |
| `external_extract_metadata.workspace_id` (key) | gone — `artifact_metadata.artifact_id`/`root_path` | drop key | `ExtractReader.ReadWorkspaceId`, bootstrap cross-check |
| (none) | `pending_relationships`, `type_facts`, `parse_diagnostics`, `parser_inventory`, `language_capabilities*` | new | opportunity, not required |
| any `*_fts` / embeddings / reference_score | absent (forbidden by v1 contract) | unchanged (absent both) | none — Miller owns these |

### 4.4 Freshness / revisions

- `FreshnessReader.LatestRevision`: `SELECT MAX(revision) FROM canonical_revisions WHERE workspace_id=…`
  → `SELECT MAX(revision_id) FROM extraction_revisions` (no workspace filter — one DB = one root).
- `FreshnessReader.ChangedSince`: query `revision_file_changes(revision_id, path, change_kind)`
  (drop `workspace_id`, rename `revision`→`revision_id`, `file_path`→`path`).
- `RevisionChangeKind` enum + `ParseChangeKind`: expand `{added,modified,deleted}` →
  `{inserted,updated,deleted,unsupported}`. Map `inserted`→Added, `updated`→Modified, `deleted`→Deleted,
  add `Unsupported` (treat as remove-from-index for freshness purposes).
- **Correctness note:** v1 has **no CHECK constraint** on `change_kind` (old julie did). The "fail loud on
  unknown" stance stays, but the in-code comment claiming a CHECK constraint must be corrected.
- `FreshnessPoller`/`IndexHolder.BuiltRevision`: source the cursor from the new report/table; behavior
  (strictly-greater rebuild + atomic swap) unchanged.

### 4.5 Packaging / restore

OLD: `scripts/restore-julie-server.{sh,ps1}` + `scripts/julie-pins.json` download `julie-server` 7.13.2
from `anortham/julie` releases (per-platform sha256-verified archives); runtime resolves
`<BaseDirectory>/.tools/julie-server`.

NEW (download assets shipping today — exact shape to confirm, see §9):
- Rename scripts to `restore-julie-extract.{sh,ps1}`; binary `julie-extract`.
- `julie-pins.json`: repoint repo + version + binary name + per-triple asset names + checksums.
- From-source fallback: `cargo build --release -p julie-extract-cli --bin julie-extract` against a
  julie-extractors checkout (`MILLER_JULIE_SOURCE`).
- `Miller.Server.csproj` Content/Link/Exec items: `.tools/julie-server` → `.tools/julie-extract`.
- `WorkspaceContext.ToolsRoot` binary name + `JulieExtractRunner.Locate()` binary name.
- `CLAUDE.md` (then regenerate `AGENTS.md` via `scripts/sync-agents.sh`): update all `julie-server` /
  `MILLER_JULIE_SOURCE=/path/to/julie` references.

## 5. Capability ownership

The five v1 non-goals cost Miller **zero** new-subsystem work — Miller already owns each (verified):

| Capability | Miller already owns it | Migration work |
|---|---|---|
| Symbol search | in-memory BM25 (`MillerSearchIndex.cs`) | none |
| Embeddings | none present (intentionally) | none |
| Workspace registry | `~/.miller/workspaces.db` (`WorkspaceRegistry.cs`) | drop the absent `workspace_id` metadata read; rework bootstrap cross-check |
| Watcher | leader-elected `FileSystemWatcher` (`IndexerService`) | none |
| Reference-scores / analysis | not consumed (only an unused `analysis_state` field) | drop the unused field |

## 6. Content search architecture (deferred; chosen shape recorded)

Content/full-text search ("find any text, not just symbols") is required to fully replace Julie, but is
**deferred** past this migration so we dogfood first.

- **Chosen shape: Option B.** Miller owns the content index end to end. It sources file text from **disk**,
  driven by julie's `files` table (the manifest of indexable, ignore-filtered, non-binary files) and
  `content_hash` (staleness). The index lives in a Miller-owned store, likely FTS5 in
  contentless/external-content mode (index only; snippets fetched from disk by line range). Engine choice
  (FTS5 trigram vs Lucene.NET) is Miller's to measure later.
- **Why disk, not stored content:** matches "julie owns structure, Miller owns search"; stores text once
  (disk); the extra disk read on a cold full build is marginal next to the trigram index build (which any
  option pays). Option C (julie streams content opt-in via `export --with-content`) is the on-contract
  fallback if measurement shows the double-read hurts on multi-GB repos. Option A (julie stores content)
  is avoided — it bloats every artifact and duplicates text.
- **Interim win in THIS migration (D3a):** widen the in-memory BM25 index to ingest `doc_comment`,
  `code_context`, and `literal_text` in addition to `name + signature`. No new store, no trigram.
- **Open measurement (before building content FTS, not before this migration):** trigram index build
  time, on-disk size, and query latency on a large real repo (per `m9-design.md` D2's flagged check).

## 7. File content handling (D2)

`files.content` is gone in v1. Its two Miller consumers move to disk:
- `inspect(full)` body slicing: read the file from disk, slice by `start_byte`/`end_byte` (or body span
  columns) from the symbol row.
- Edit baseline (`ExtractReader.ReadIndexedFileText`): compare the on-disk file's hash against the stored
  `content_hash`; the edit op already reads disk (`EditService.ReadDisk`). Edit-freshness semantics shift
  from "diff vs stored snapshot" to "is disk still at the indexed hash" — accepted.

**Hard freshness invariant (Codex review):** a disk byte-span slice is valid ONLY when the on-disk file's
BLAKE3 hash equals the stored `content_hash` (prefix-stripped). The byte offsets (`start_byte`/`end_byte`)
were computed against the indexed content; slicing them out of a drifted file silently returns the WRONG
bytes. So `inspect(full)` body slicing and the edit baseline must, before slicing: hash the disk file, compare
to `content_hash`; on mismatch trigger a refresh (or return a typed staleness error) — never slice stale.
This makes `ContentHasher` / `FreshnessGate` prerequisites of the body-slice path, not just the edit path.

## 8. Test signal handling (D4)

- `IndexedSymbol`: replace `TestRole? TestRole` with `bool IsTest` (and optionally `bool TestContainer`,
  `bool TestLifecycle` for future use — not required for parity).
- `SqliteSymbolReader`: read the typed `is_test` column directly; delete `ParseTestSignals` (JSON parse +
  substring probe) and its now-dead perf optimization.
- `SymbolDetail.TestRole` → `IsTest`. Update consumers: `RouteBridge`/`TsClientCall` (`IsRealClientCall`),
  `BridgeGraphBuilder.ReduceClientCalls`, `RepositoryIndexLoader.ProjectToSymbolDetails`.
- Delete/rewrite `TestRole.cs` and tests `TestRoleTests.cs`, `SqliteSymbolReaderTests.cs` test-signal
  cases, `RouteBridgeTests.cs` predicate cases.

## 9. Open items to confirm with upstream (julie-extractors)

These do not block design but are needed before the restore/pins code is final:
1. **Release asset shape:** repo slug (expected `anortham/julie-extractors`), tag/version, per-triple
   asset names, archive vs bare binary, and checksum sidecars.
2. **Pinned version string** for the download (separate from the runtime gate, which is schema/contract).
3. **Triple coverage** Miller needs (at least `aarch64-apple-darwin`, `x86_64-apple-darwin` if Intel Macs
   are supported, `x86_64-unknown-linux-gnu`, `x86_64-pc-windows-msvc`).
4. **`change_kind` vocabulary stability:** confirm `{inserted,updated,deleted,unsupported}` is the final
   set (v1 has no CHECK constraint enforcing it).

## 10. Required changes by subsystem

All paths under `/Users/murphy/source/miller/`.

**A — Subprocess invocation & report (`src/Miller.Indexing/`)**
- `JulieExtractRunner.cs` (HIGH): binary name; drop `extract` token from all argv builders; drop
  `--workspace-id` from scan; add `--strict-schema`; add `Interpret()` `case 3` → `IncompatibleExtractException`;
  update error strings. Correct-fix on the path: add `WaitForExit(timeout)` + `Kill` so a hung
  `julie-extract` can't block bootstrap `StartAsync` (flag scope to Alan; recommended in-scope).
- `ExtractReport.cs` (HIGH): rewrite flat → nested; `ExtractError` → `ReportDiagnostic`; drop
  `analysis_state`.
- `ExtractVersionMismatch.cs` (HIGH): read versions from `report.artifact.*`; null artifact = gate fail.
- `MillerExtractContract.cs` (HIGH): `28→1`, `3→1`, add `ExpectedReportSchemaVersion=1`,
  `ExpectedSqliteSchemaVersion=1`; rename `PinnedJulieServerVersion`→`PinnedJulieExtractVersion`; keep
  `ExpectedHashAlgorithm="blake3"`.

**B — SQLite read layer (`src/Miller.Indexing/`)** — adopt **by-name** column reads (D6) while editing:
- `JulieSchemaGate.cs` (HIGH): versions from `artifact_metadata` keys; `external_extract_metadata` →
  `artifact_metadata`; fix error strings.
- `SqliteSymbolReader.cs` (HIGH): SELECT renames (`symbol_id`, `parent_symbol_id`, `path`, `metadata_json`);
  read typed `is_test`; ORDER BY `path, start_line, symbol_id`; by-name reads.
- `ExtractReader.cs` (HIGH): `WHERE symbol_id=`; identifiers `path` + `identifier_id` ordering;
  `artifact_metadata` (or drop `ReadWorkspaceId`); **`ReadBody`/`ReadIndexedFileText` re-source from disk**.
- `SqliteBridgeReader.cs` (HIGH): `type_argument_usages` JOIN; `parent_type_argument_id`; literals/
  annotations renames; DbSet query `symbol_id`/`path`.
- `WorkspaceIndexFactsReader.cs` (HIGH): `file_path` → `path`.
- `SymbolGraphReader.cs` (MED): confirm payload survives; no `, id` tiebreaker.
- **New:** a fixture test asserting exact `path:line` for a known symbol (D6 guard).

**C — Freshness (`src/Miller.Indexing/`, `src/Miller.Server/Hosting/`, `src/Miller.Core/Freshness/`)**
- `FreshnessReader.cs` (HIGH): `extraction_revisions`/`revision_file_changes` rewrite; expand
  `ParseChangeKind`; fix the false CHECK-constraint comment.
- `ExtractFileHashReader.cs` (HIGH): `content_hash`; strip `blake3:` prefix; `artifact_metadata`.
- `FreshnessGate.cs` (HIGH): normalize prefixed `content_hash` vs bare-hex disk hash before
  `StalenessCheck`.
- `StalenessCheck.cs` / `ContentHasher.cs` (MED): pick one canonical hash form, apply consistently.

**D — Bootstrap / workspace identity & report-consuming services (`src/Miller.Server/`)**
- `IndexBootstrapService.cs` (HIGH): replace workspace_id-mismatch rebind + hard assertion with
  `artifact_metadata.root_path` comparison (or rely on exit-3 `root_mismatch`); capture revision from the
  report. Also `ReadLatestRevisionOrZero` (the DB fallback) must query `extraction_revisions`.
- **`Tools/WorkspaceTool.cs` (HIGH) — MISSED in v1 of this spec (Codex):** `workspace open` runs a scan and
  reads `report.WorkspaceId` (`:384-385`), `report.Revision` (`:398`), `report.SymbolsExtracted`. The
  `workspace_id` echo is gone in v1 — drop that cross-check (use `root_path` / exit-3) and remap revision to
  `report.revision.latest_revision_id` with the existing DB fallback.
- **`Workspaces/CrossWorkspaceRefreshService.cs` (HIGH) — MISSED in v1 of this spec (Codex):** also runs a
  scan and reads `report.WorkspaceId` (`:112-113`) and `report.Revision` (`:129`). Same remap as WorkspaceTool.
- `WorkspaceId.cs` (MED): keep SHA-256 for Miller's own registry; stop expecting julie to echo it.
- `IndexerCore.cs` (MED): `flock_timeout` → `lock_timeout`; prefer `recoverable` flag.

**E — Search (interim widen — D3a)**
- `IndexedSymbol`/`SearchableDocument`/`MillerSearchIndex` build: ingest `doc_comment` + `code_context`
  + `literal_text` into the indexed text. Update BM25 tests for the widened corpus.

**F — Test signal (D4)** — see §8.

**G — Packaging / docs** — see §4.5.

**H — Test fixtures (`tests/Miller.Tests/`) — MISSED in v1 of this spec (Codex); CO-REQUISITE, not optional**
- `Indexing/JulieDbFixture.cs` (HIGH): builds the **entire old schema** — `schema_version` table (28),
  `external_extract_metadata`, `files(hash, content, …)`, `symbols(id, file_path, parent_id, metadata, …)`,
  `identifiers(id, file_path)`, `relationships(id, file_path)`, `canonical_revisions(revision, workspace_id)`,
  `revision_file_changes(revision, workspace_id, file_path, change_kind∈{added,modified,deleted})`,
  `type_arguments`/`literals` old shapes. This is a **second implementation of the julie schema** that the
  fast suite reads. It MUST migrate to v1 schema **in lockstep** with the readers (subsystem B) — otherwise
  every fast test breaks. It also defines the canonical v1 synthetic schema the fast suite asserts against.
- `Server/LargeDbWriter.cs` (HIGH): the 50k-symbol scale fixture builder — migrate to v1 schema too.
- Update existing test expectations: `path:line` fixture (B), `ParseChangeKind` vocabulary, `IsTest` column,
  annotation `(symbol_id, annotation_id)` ordering, nested report parsing, prefix-stripped `content_hash`.

## 11. Testing strategy

- **Fast suite (`Category!=Scale`, <10s):** all contract/logic tests — report parsing (nested), schema-gate
  on synthetic v1 DBs, `ParseChangeKind` vocabulary, `content_hash` prefix normalization, by-name reader
  ordinals via tiny fixtures, `path:line` fixture assertion, widened-BM25 ordering, `IsTest` from column.
- **Scale suite (`Category=Scale`, opt-in):** real `julie-extract scan`/`update`/`delete` against a live
  binary; startup-delta convergence; cross-workspace reads; assert report `sqlite_schema_version=1` +
  `hash_algorithm=blake3`. Any test spawning the binary is `[Trait("Category","Scale")]` and obtains it via
  `ScaleTestSupport` (update that helper for the new binary name).
- Build: `dotnet build Miller.slnx -c Release` must be 0 warnings / 0 errors.

## 12. Sequencing

Land as **one atomic migration** behind a from-source build first (so it can be validated before the
download assets are wired), then switch the restore to download once §9 is confirmed. Suggested order:
1. From-source restore + binary-name plumbing (A packaging + `Locate`/`ToolsRoot`) — get a v1 DB to read.
2. Contract gate + report (`MillerExtractContract`, `ExtractReport`, `ExtractVersionMismatch`, `Interpret`).
3. Read layer renames + by-name reads (B) **with `JulieDbFixture`/`LargeDbWriter` migrated in lockstep (H)**
   + `path:line` fixture. (Fixtures and readers move together or the fast suite breaks.)
4. Freshness rewrite (C) + bootstrap identity + report-consuming services (`WorkspaceTool`,
   `CrossWorkspaceRefreshService`) (D).
5. File-content disk re-sourcing (D2) + test-signal simplification (D4/§8).
6. Interim search widen (D3a).
7. Download-asset restore + pins + docs (G) once §9 lands; regenerate `AGENTS.md`.

## 13. Out of scope / deferred (tracked in `TODO.md`)

- **Incremental in-memory rebuild** — revisit after migration with real performance testing on larger
  repos (v1's 4-value `change_kind` + stable IDs enable it).
- **Content/full-text search (Option B)** — build the Miller-owned content index (disk-sourced) when the
  dogfood gap warrants it; run the trigram measurement spike first.

## 14. Acceptance criteria

- [ ] Miller spawns `julie-extract` with v1 argv (no `extract` token, no `--workspace-id`, `--strict-schema`).
- [ ] `Interpret()` maps exit 3 to `IncompatibleExtractException` via `errors[].code`.
- [ ] `ExtractReport` deserializes the nested v1 report; version/hash read from `report.artifact.*`; null
      artifact fails the gate.
- [ ] `MillerExtractContract` gates on `sqlite_schema_version=1` + `extract_contract_version=1` + `blake3`.
- [ ] All read queries use v1 table/column names and **by-name** reads; a fixture test asserts exact
      `path:line` for a known symbol.
- [ ] `files.content` is no longer read; `inspect(full)` body + edit baseline source from disk.
- [ ] `content_hash` `blake3:` prefix is normalized before freshness comparison.
- [ ] `FreshnessReader` reads `extraction_revisions`/`revision_file_changes`; `ParseChangeKind` handles
      `{inserted,updated,deleted,unsupported}`.
- [ ] Bootstrap no longer depends on a julie-echoed `workspace_id`.
- [ ] `WorkspaceTool` (`workspace open`) and `CrossWorkspaceRefreshService` consume the nested v1 report
      (no `workspace_id` echo; revision from `latest_revision_id` + DB fallback).
- [ ] Annotations read without `ordinal`; deterministic order is `(symbol_id, annotation_id)`.
- [ ] Body-slice / edit paths verify disk hash == stored `content_hash` before slicing (stale → refresh/error,
      never wrong bytes).
- [ ] Test fixtures (`JulieDbFixture`, `LargeDbWriter`) emit v1 schema; fast suite green against them.
- [ ] Migration is pure parity; the D3a search-widen is included only if Alan opted in (§16).
- [ ] Test signal read from typed `symbols.is_test`; `TestRole` removed; consumers use `bool IsTest`.
- [ ] In-memory search index ingests `doc_comment` + `code_context` + `literal_text`; BM25 tests updated.
- [ ] Restore obtains `julie-extract` (from-source path working; download path pending §9); csproj +
      `WorkspaceContext` + docs reference the new binary; `AGENTS.md` regenerated.
- [ ] Fast suite <10s, 0 warnings; scale suite passes against a live `julie-extract`.
- [ ] `TODO.md` records the incremental-rebuild and content-FTS follow-ups.

## 15. Corrections applied (from verification)

- Test detection is **preserved and richer** (typed `is_test`/`test_container`/`test_lifecycle` + mirrored
  in `metadata_json`); `test_role` was decomposed, not dropped. The break is **loud** (schema gate first),
  not a silent `(false,null)`.
- The current-pin breakages (`files.hash`, `external_extract_metadata`) are **forward-looking** — against
  the pinned 7.13.2 binary Miller works today; they bite at migration.
- `binary_version` is unreliable (working-tree 2.0.0 vs on-disk 0.1.0) and not read at runtime — gate on
  schema/contract versions.
- Line/column/byte conventions are **identical** old↔v1 (1-based / 0-based / 0-based) — no off-by-one.
- `revision_file_changes.change_kind` has **no** CHECK constraint in v1 — confirm vocabulary (§9.4).

### Applied from Codex adversarial review (2026-06-01, verdict "not safe yet" → corrected)

Verified against source and folded in:
- **Added missed report-consuming paths** to §10D: `WorkspaceTool` (`workspace open`) and
  `CrossWorkspaceRefreshService` both run extraction and read `report.WorkspaceId`/`report.Revision`.
- **Added missed test-fixture co-requisite** (§10H): `JulieDbFixture.cs` + `LargeDbWriter.cs` build the old
  schema and must migrate in lockstep with the readers.
- **Corrected the `relationships` row** in §4.3 (the row reshapes, but Miller reads only
  `from_symbol_id/to_symbol_id/kind`, which survive — no breakage).
- **Made the `symbol_annotations.ordinal` drop an explicit ordering decision** (§4.3) rather than a "rename."
- **Scoped `--strict-schema` to artifact commands** (not `languages`) in §4.1.
- **Tightened the revision mapping** (§4.2): `created_revision_id` is null on no-op; cursor is
  `latest_revision_id` with DB fallback.
- **Added the hard disk-slice freshness invariant** (§7): never slice byte spans from a drifted file.
- **Reclassified D3a** as a non-parity scope expansion (§3, §16), per Codex's parity objection.

Codex independently confirmed solid: CLI delta (`extract` parent gone, no `--workspace-id`, exit 3),
freshness shape + `change_kind` vocab + no CHECK constraint, typed test-signal columns + metadata mirror,
and the 1-based/0-based position conventions.

## 16. Open question for Alan (raised by Codex review)

**D3a — search-widen: in this migration, or split out?** Widening the in-memory index to ingest
`doc_comment` + `code_context` + `literal_text` is a genuine search-semantics change (not parity) and pulls
broader bridge ingestion into the migration. Options:
- **(a) Keep it out (pure parity).** Migration is a clean 1:1 contract port; the widen becomes its own small
  follow-up. Lowest risk, cleanest bisection. *(Codex's implied recommendation; my default.)*
- **(b) Include it, explicitly labeled.** You wanted a cheap dogfood win; ride it along but tracked as a
  deliberate scope expansion with its own tests.

Default is (a) unless you say otherwise.
