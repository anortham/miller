# Miller content corpus v1 contract

Status: planned contract. Phase 0 records this before production code writes `.miller/content.db`.

`content.db` is a Miller-owned sidecar for chunked text content. It is separate from `symbols.db` and
`search.db`: `symbols.db` remains the `julie-extract` structured artifact, `search.db` remains symbol and
source-region search, and `content.db` stores raw chunk text for explicit text search, bounded reads,
external/web imports, and Eros semantic ingestion.

## Content kinds

Every source and chunk has exactly one `content_kind`.

| Kind | Owner | Meaning | Lifecycle |
|---|---|---|---|
| `workspace_source` | Miller workspace refresh | Source-like files from `symbols.db.files` that are not docs/config. | Rebuilt or incrementally updated from workspace files after BLAKE3 verification. |
| `workspace_docs` | Miller workspace refresh | Documentation/prose files such as Markdown, reStructuredText, AsciiDoc, Org, and docs paths. | Rebuilt or incrementally updated from workspace files after BLAKE3 verification. |
| `workspace_config` | Miller workspace refresh | Text configuration/data files such as JSON, YAML, TOML, INI, CFG, and plain text when classified as config rather than prose. | Rebuilt or incrementally updated from workspace files after BLAKE3 verification. |
| `external_file` | Explicit content import | User-provided text outside the workspace, such as logs, reports, traces, or generated text artifacts. | Added, replaced, read, searched, exported, and removed through Miller content commands. |
| `web` | Explicit content import | Browser or fetch output imported as page text/markdown with a URL. | Added, replaced, read, searched, exported, and removed through Miller content commands. |

Unknown content kinds are invalid for schema v1. Future kinds require a schema/contract revision.

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
| `source_bytes` | `INTEGER NOT NULL` | yes | UTF-8 byte count of the full source text used to build chunks. |
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
| `byte_start` | `INTEGER NOT NULL` | yes | Zero-based inclusive start byte in the source text. |
| `byte_end` | `INTEGER NOT NULL` | yes | Zero-based exclusive end byte in the source text. |
| `raw_text` | `TEXT NOT NULL` | yes | Chunk text stored as UTF-8 text. |
| `doc_len` | `INTEGER NOT NULL` | yes | Token count used by Miller ranking. |
| `is_test` | `INTEGER NOT NULL` | yes | `0` or `1`; duplicated for filtering. |
| `containing_symbol_id` | `TEXT NULL` | no | Best containing symbol when the chunk or hit line is in a known symbol range. |
| `containing_symbol_name` | `TEXT NULL` | no | Human-facing containing symbol name. |

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
| `schema_version` | `INTEGER NOT NULL` | yes | Must be `1` for this contract. |
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
a 25 MiB default cap unless the caller explicitly chooses a higher cap.

Chunks must preserve line and byte ranges from the original normalized UTF-8 text. Overlap means
`stored_raw_bytes` can be larger than `indexed_source_bytes`.

## Lifecycle rules

- Workspace-derived rows are built only from `symbols.db.files` rows with `status = indexed`.
- Workspace-derived file bytes are reread from disk and verified against the recorded BLAKE3 `content_hash` before indexing.
- A missing, unreadable, non-UTF-8, oversized, stale-hash, or non-indexed workspace file is skipped and recorded in corpus facts.
- Workspace-derived content is rebuilt atomically for full scans. Incremental refresh may replace rows for changed files in a transaction.
- External/web content is Miller-owned. Imports, replacements, and removals run in transactions and do not require `julie-extract`.
- Removing an external/web source deletes its source row, chunk rows, and FTS rows. It must not delete the original file or browser cache.
- A stale workspace content DB is not silently accepted for explicit text search. It must either converge or return an actionable stale/corrupt status.
- Corrupt or schema-mismatched `content.db` files fail visibly and should be rebuilt by the writer path, not opened with an in-memory fallback.

## JSONL export

Eros and other consumers read deterministic chunk rows through an export API. Each line is one JSON object:

| Field | Required | Description |
|---|---:|---|
| `schema_version` | yes | `1`. |
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
| `byte_start` | yes | Zero-based inclusive byte start. |
| `byte_end` | yes | Zero-based exclusive byte end. |
| `source_bytes` | yes | Full source byte count. |
| `content_hash` | yes | Source hash used for deterministic freshness. |
| `chunk_text` | yes | Raw chunk text. |
| `doc_len` | yes | Token count used by Miller ranking. |
| `is_test` | yes | Boolean. |
| `containing_symbol_id` | no | Containing symbol ID, when known. |
| `containing_symbol_name` | no | Containing symbol name, when known. |

Export order is stable: `content_kind`, `display_path`, `line_start`, `chunk_id`.

## Privacy and storage

`content.db` stores full chunk text, including source code, docs, external logs, and imported web text. It must stay
under the workspace `.miller/` directory or Miller-owned state directories and must not be uploaded, pushed, or
included in release archives. External and web imports can contain secrets; commands and docs must describe that the
raw text is persisted locally until removed.

## Eros boundary

Miller owns deterministic local chunk extraction, FTS recall, metadata, and export. Eros owns embeddings, semantic
ranking, cross-workspace semantic stores, and commercial UI/analysis features. Eros must treat this contract as an
input format and must not require Miller to store embedding vectors in `content.db`.
