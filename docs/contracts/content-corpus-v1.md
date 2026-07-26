# Miller content corpus v1 contract

Status: active local contract for Miller `.miller/content.db` schema version 2.

Schema version 2 adds read-path indexes for source-window reads, source deletes, and containing-symbol chunk
lookups. The content kinds, chunker version, table fields, and JSONL field set remain unchanged from schema
version 1.

`content.db` is a Miller-owned sidecar for chunked text content. It is separate from `symbols.db` and
`search.db`: `symbols.db` remains the `julie-extract` structured artifact, `search.db` remains symbol and
source-region search, and `content.db` stores raw chunk text for explicit text search, bounded reads,
external/web imports, and Eros semantic ingestion.

Workspace rebuilds preserve active `external_file` and `web` rows by their required table/column shape, not by an
exact corpus schema-version match, so compatible schema upgrades do not strand imports. If the imported-content
shape cannot be copied exactly, promotion is refused, the existing database remains in place, and Miller records a
fingerprinted preservation failure beside it. Workspace status then reports `preservation_blocked` with the error
while that unchanged database remains. The marker is diagnostic, not a permanent latch: later rebuilds retry
preservation so transient failures can heal. Miller records it only after proving active imports exist; an unreadable
derived corpus with no proven imports remains eligible for corruption recovery. Corrupt or incompatible proven
imports are never silently discarded.

Agent-facing bounded list/shape behavior is versioned separately in
[`content-mcp-v3.md`](content-mcp-v3.md). Bulk JSONL export is a CLI-only
process contract.

## Content kinds

Every source and chunk has exactly one `content_kind`.

| Kind | Owner | Meaning | Lifecycle |
|---|---|---|---|
| `workspace_source` | Miller workspace refresh | Source-like files from `symbols.db.files` that are not docs/config. | Rebuilt or incrementally updated from workspace files after BLAKE3 verification. |
| `workspace_docs` | Miller workspace refresh | Documentation/prose files such as Markdown, reStructuredText, AsciiDoc, Org, and docs paths. | Rebuilt or incrementally updated from workspace files after BLAKE3 verification. |
| `workspace_config` | Miller workspace refresh | Text configuration/data files such as JSON, YAML, TOML, INI, CFG, and plain text when classified as config rather than prose. | Rebuilt or incrementally updated from workspace files after BLAKE3 verification. |
| `external_file` | Explicit content import | User-provided text outside the workspace, such as logs, reports, traces, or generated text artifacts. | Added, replaced, read, searched, exported, and removed through Miller content commands. |
| `web` | Explicit content import | Browser or fetch output imported as page text/markdown with a URL. | Added, replaced, read, searched, exported, and removed through Miller content commands. |

Unknown content kinds are invalid for this contract. Future kinds require a schema/contract revision.

## Tables

### `content_sources`

One row per logical source document.

| Field | Type | Required | Description |
|---|---|---:|---|
| `source_id` | `TEXT PRIMARY KEY` | yes | Stable source identity. Workspace rows use workspace ID plus relative path and content kind. External/web rows use an import ID. |
| `content_kind` | `TEXT NOT NULL` | yes | One of the v1 content kinds above. |
| `workspace_id` | `TEXT NULL` | no | Miller workspace ID for workspace-derived rows. Null for external/web rows unless explicitly attached to a workspace. |
| `workspace_revision` | `INTEGER NULL` | no | `symbols.db` revision used when the row was built. Null for standalone external/web rows. |
| `path` | `TEXT NULL` | no | Workspace-relative path or external local path. Uses forward slashes for workspace paths. |
| `url` | `TEXT NULL` | no | Canonical URL for web rows. Null for file rows. |
| `display_path` | `TEXT NOT NULL` | yes | Human-facing path/URL label used in search results. |
| `language` | `TEXT NOT NULL` | yes | Extracted language or text classifier label. Use empty string only when unknown. |
| `content_hash` | `TEXT NOT NULL` | yes | BLAKE3 hash for workspace/external bytes, or deterministic hash of imported web text. Include algorithm prefix when available. |
| `source_bytes` | `INTEGER NOT NULL` | yes | UTF-8 byte count of the original source bytes before line-ending normalization. |
| `line_count` | `INTEGER NOT NULL` | yes | One-based text line count. Empty text counts as one line. |
| `is_test` | `INTEGER NOT NULL` | yes | `0` or `1`; set from existing Miller test-path heuristics for workspace rows. |
| `status` | `TEXT NOT NULL` | yes | `active`, `stale`, `deleted`, or `error`. Only `active` rows are searched by default. |
| `indexed_at_utc` | `TEXT NOT NULL` | yes | UTC timestamp in ISO-8601 round-trip format. |

### `content_chunks`

One row per searchable chunk. Chunk rows store the raw chunk text so Miller can answer searches and bounded reads
without reopening large external files or fetched web pages.

| Field | Type | Required | Description |
|---|---|---:|---|
| `chunk_id` | `TEXT PRIMARY KEY` | yes | Stable chunk identity derived from `source_id`, line/byte range, and chunker version. |
| `source_id` | `TEXT NOT NULL` | yes | Parent `content_sources.source_id`. |
| `content_kind` | `TEXT NOT NULL` | yes | Duplicated from source for filtering without a join. |
| `path` | `TEXT NULL` | no | Duplicated source path. |
| `url` | `TEXT NULL` | no | Duplicated source URL. |
| `display_path` | `TEXT NOT NULL` | yes | Duplicated result label. |
| `language` | `TEXT NOT NULL` | yes | Duplicated source language/classifier label. |
| `line_start` | `INTEGER NOT NULL` | yes | One-based inclusive start line in the source text. |
| `line_end` | `INTEGER NOT NULL` | yes | One-based inclusive end line in the source text. |
| `byte_start` | `INTEGER NOT NULL` | yes | Zero-based inclusive UTF-8 byte offset in the normalized text (`CRLF` and lone `CR` become `LF`). |
| `byte_end` | `INTEGER NOT NULL` | yes | Zero-based exclusive UTF-8 byte offset in the normalized text (`CRLF` and lone `CR` become `LF`). |
| `raw_text` | `TEXT NOT NULL` | yes | Chunk text stored as UTF-8 text. |
| `doc_len` | `INTEGER NOT NULL` | yes | Token count used by Miller ranking. |
| `is_test` | `INTEGER NOT NULL` | yes | `0` or `1`; duplicated for filtering. |
| `source_bytes` | `INTEGER NOT NULL` | yes | Full source byte count, duplicated for result telemetry without joining. |
| `containing_symbol_id` | `TEXT NULL` | no | Best containing symbol when the chunk or hit line is in a known symbol range. |
| `containing_symbol_name` | `TEXT NULL` | no | Human-facing containing symbol name. |

### `content_symbol_spans`

One row per symbol span associated with a text source. This lets Miller attach containing-symbol metadata to the
actual best hit line inside a chunk instead of only the chunk start line.

| Field | Type | Required | Description |
|---|---|---:|---|
| `source_id` | `TEXT NOT NULL` | yes | Parent `content_sources.source_id`. |
| `symbol_id` | `TEXT NOT NULL` | yes | Stable symbol id from `symbols.db`. |
| `symbol_name` | `TEXT NOT NULL` | yes | Human-facing symbol name. |
| `path` | `TEXT NOT NULL` | yes | Workspace-relative source path. |
| `start_line` | `INTEGER NOT NULL` | yes | One-based inclusive symbol start line. |
| `end_line` | `INTEGER NOT NULL` | yes | One-based inclusive symbol end line. |

### `content_fts`

FTS5 virtual table for recall:

```sql
CREATE VIRTUAL TABLE content_fts USING fts5(
    chunk_id UNINDEXED,
    body,
    tokenize = 'unicode61 remove_diacritics 0'
);
```

FTS5 is recall-only. Miller may rerank and snippet candidates in C# using existing search/tokenization helpers.
Schema v1 does not include a full-corpus trigram index.

### `content_meta`

Singleton key/value facts for status, freshness, and dashboard display.

| Field | Type | Required | Description |
|---|---|---:|---|
| `schema_version` | `INTEGER NOT NULL` | yes | Must be `2` for this contract. |
| `workspace_revision` | `INTEGER NULL` | no | Workspace revision the workspace-derived partition matches. |
| `chunker_version` | `TEXT NOT NULL` | yes | Version string for line/byte chunking behavior. |
| `source_count` | `INTEGER NOT NULL` | yes | Active source rows. |
| `chunk_count` | `INTEGER NOT NULL` | yes | Active chunk rows. |
| `indexed_source_bytes` | `INTEGER NOT NULL` | yes | Sum of active source byte counts. |
| `stored_raw_bytes` | `INTEGER NOT NULL` | yes | Sum of active chunk raw text bytes. Includes overlap. |
| `updated_at_utc` | `TEXT NOT NULL` | yes | Last successful corpus update timestamp. |

## Search modes

The contract supports these mode meanings:

| Mode | Content kinds searched | Default inclusion |
|---|---|---|
| `content` | `workspace_docs`, `workspace_config` | Explicit mode only. Preserves existing docs/config meaning. |
| `source` | `workspace_source` | Explicit mode only. Does not change default symbol search. |
| `external` | `external_file` | Phase 3 explicit mode/tool search. |
| `web` | `web` | Phase 4 explicit mode/tool search. |
| `all-text` | All active content kinds | Phase 6 explicit union mode after quality gates. |

Default symbol search must not include `content.db` hits unless a later contract revision explicitly changes that.

## Chunking

Default workspace chunking is 160 lines with 20 lines of overlap. A chunk must not exceed the per-source byte cap
without being split further. Phase 1 workspace files use the existing 1 MiB file cap. Phase 3 external imports use
a 25 MiB default cap unless the caller explicitly chooses a higher cap. The raised-cap streaming path rejects
logical lines over 65,536 UTF-16 characters and caps stored raw chunks at 1,048,576 UTF-16 characters. When the
character cap triggers before the normal line cap, the writer retains as much of the 20-line overlap as fits with
the next line.

Chunks preserve line and byte ranges in normalized UTF-8 text: `CRLF` and lone
`CR` line endings become one `LF` byte before offsets are calculated.
`source_bytes` and `content_hash` still describe the original input bytes, so
the last `byte_end` can be smaller than `source_bytes` for CRLF input.
Streaming and non-streaming imports use this same convention. Overlap means
`stored_raw_bytes` can be larger than `indexed_source_bytes`.

## Lifecycle rules

- Workspace-derived rows are built only from `symbols.db.files` rows with `status = indexed`.
- Workspace-derived file bytes are reread from disk and verified against the recorded BLAKE3 `content_hash` before indexing.
- A missing, unreadable, non-UTF-8, oversized, stale-hash, or non-indexed workspace file is skipped and recorded in corpus facts.
- Workspace-derived content is rebuilt atomically for full scans. Incremental refresh may replace rows for changed files in a transaction.
- External/web content is Miller-owned. Imports, replacements, and removals run in transactions and do not require `julie-extract`.
- A corpus with external/web imports but no workspace build has `workspace_revision = null` and reports
  `state=imports_only`; imported search/read remains available while workspace source/docs/config search directs
  the caller to `workspace refresh`.
- A workspace rebuild must preserve every active external/web source before promotion. If an existing SQLite
  corpus contains imports but its schema or rows cannot be copied, the rebuild refuses promotion and leaves the
  existing artifact unchanged.
- Removing an external/web source deletes its source row, chunk rows, and FTS rows. It must not delete the original file or browser cache.
- A stale workspace content DB is not silently accepted for explicit text search. It must either converge or return an actionable stale/corrupt status.
- Corrupt or schema-mismatched `content.db` files fail visibly and should be rebuilt by the writer path, not opened with an in-memory fallback.

## JSONL export

Eros and other consumers read deterministic chunk rows through an export API. Each line is one JSON object.
Optional fields are emitted with explicit `null` values when unavailable so consumers can depend on a stable
field set.
Export is also the non-destructive preservation path for an older or shape-incompatible corpus: it reads the
columns that exist, supplies stable null/zero defaults for missing optional fields, and reports the artifact's
actual schema version instead of refusing it. Required source/chunk tables must still exist.

| Field | Required | Description |
|---|---:|---|
| `schema_version` | yes | Artifact schema version; current writers emit `2`, while recovery exports may be older. |
| `workspace_id` | no | Workspace ID, when available. |
| `workspace_revision` | no | Workspace revision, when available. |
| `source_id` | yes | Source row ID. |
| `chunk_id` | yes | Chunk row ID. |
| `content_kind` | yes | One v1 content kind. |
| `path` | no | Workspace-relative or external path. |
| `url` | no | URL for web rows. |
| `display_path` | yes | Human-facing source label. |
| `language` | yes | Language/classifier label. |
| `line_start` | yes | One-based inclusive line start. |
| `line_end` | yes | One-based inclusive line end. |
| `byte_start` | yes | Zero-based inclusive byte start in normalized UTF-8 text. |
| `byte_end` | yes | Zero-based exclusive byte end in normalized UTF-8 text. |
| `source_bytes` | yes | Full source byte count. |
| `content_hash` | yes | Source hash used for deterministic freshness. |
| `chunk_text` | yes | Raw chunk text. |
| `doc_len` | yes | Token count used by Miller ranking. |
| `is_test` | yes | Boolean. |
| `containing_symbol_id` | no | Containing symbol ID, when known. |
| `containing_symbol_name` | no | Containing symbol name, when known. |
| `source_status` | yes | Source lifecycle status, normally `active` for exported chunks. |
| `indexed_at_utc` | yes | Source indexing/import timestamp in ISO-8601 round-trip format. |

Export order is stable: `content_kind`, `display_path`, `line_start`, `chunk_id`.
Exports may be scoped by `content_kind` and by stored `workspace_id`. Miller does not create embeddings,
call Eros code, or add vector columns to `content.db`.

## Privacy and storage

`content.db` stores full chunk text, including source code, docs, external logs, and imported web text. It must stay
under the workspace `.miller/` directory or Miller-owned state directories and must not be uploaded, pushed, or
included in release archives. External and web imports can contain secrets; commands and docs must describe that the
raw text is persisted locally until removed.

## Eros boundary

Miller owns deterministic local chunk extraction, FTS recall, metadata, and export. Eros owns embeddings, semantic
ranking, cross-workspace semantic stores, and commercial UI/analysis features. Eros must treat this contract as an
input format and must not require Miller to store embedding vectors in `content.db`.
