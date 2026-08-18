# References Export Contract v1

Status: **superseded by [`references-export-v2.md`](references-export-v2.md)** as of Miller 1.14.0. This
document describes the schema-1 export emitted against julie-extract schema 4 / extract contract 3, and is
retained only as a historical record. `miller references export --jsonl` now emits schema 2; read v2 for the
active contract.

`miller references export --jsonl [--workspace-id SELECTOR] [--workspace DIR]` emits deterministic usage facts
from one workspace's `identifiers` table. The export is a raw fact feed, not a ranking tool. The former
`references candidates` surface was removed on 2026-08-18 (user decision; see the retired
[`references-candidates-v1.md`](references-candidates-v1.md)).

## Ordering And Selectors

Rows are ordered by `(path, start_byte, identifier_id)` so an unchanged artifact re-exports byte-identically.

The command accepts the normal read-command selectors:

- `--workspace-id SELECTOR`
- `--workspace DIR`

A missing or incompatible index exits with the same codes as `symbols export --jsonl`:

- `0` for an ingestable JSONL payload.
- `2` for usage or selector errors.
- `3` for operational index failures such as a missing or incompatible artifact.

## Row Shape

Every line is a JSON object with `schema_version: 1`.

| Field | Type | Description |
|---|---|---|
| `schema_version` | number | Export schema version. |
| `identifier_id` | string | Julie identifier row ID. This is the source fact ID. |
| `name` | string | Identifier text. |
| `reference_kind` | string | Julie identifier kind, such as `call`, `variable_ref`, `type_usage`, or `member_access`. |
| `language` | string | Source language recorded by julie. |
| `path` | string | Workspace-relative source path. |
| `start_line` / `end_line` | number or null | 1-based line span. |
| `start_column` / `end_column` | number or null | 0-based column span from julie. |
| `start_byte` / `end_byte` | number or null | UTF-8 byte span for the exact occurrence. |
| `source_symbol_id` | string or null | Enclosing symbol ID from `identifiers.containing_symbol_id`. |
| `source_symbol_name` | string or null | Enclosing symbol name when the source symbol row still exists. |
| `source_symbol_kind` | string or null | Enclosing symbol kind when available. |
| `source_symbol_is_test` | boolean or null | Test signal for the enclosing symbol. |
| `target_symbol_id` | string or null | Resolved target symbol ID when the artifact has one. |
| `target_symbol_name` | string or null | Resolved target name when the target row still exists. |
| `target_symbol_kind` | string or null | Resolved target kind when available. |
| `target_symbol_is_test` | boolean or null | Test signal for the resolved target symbol. |
| `resolution_status` | string | `unresolved`, `resolved`, or `dangling_target`. |
| `confidence` | number or null | Extractor confidence for the identifier fact. |
| `metadata_json` | string or null | Raw julie metadata JSON for generated/framework hints and language-specific facts. |
| `artifact_id` | string or null | Current artifact identity from `artifact_metadata`. |
| `workspace_revision` | number or null | `MAX(extraction_revisions.revision_id)` for the exported artifact. |

## Resolution Semantics

Current artifacts may leave `target_symbol_id` null for most or all rows. Consumers must not treat null target
fields as absence of usage. They mean the row is name-based/unresolved.

`resolution_status` values:

- `unresolved`: `target_symbol_id` is null.
- `resolved`: `target_symbol_id` is set and joins to a current symbol row.
- `dangling_target`: `target_symbol_id` is set but the target symbol row is missing.

`source_symbol_*` fields describe the containing/enclosing symbol, not a data-flow source. They may be null for
top-level identifiers or incomplete artifacts.
