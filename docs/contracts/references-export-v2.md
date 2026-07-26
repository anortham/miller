# References export JSONL v2

`miller references export --jsonl` emits one canonical reference assertion per line, ordered by producer-owned
reference-site identity. It requires julie-extract schema 5 / extract contract 4.

Each JSON object has these fields in this order:

| Field | Type | Contract |
| --- | --- | --- |
| `schema_version` | integer | Always `2`. |
| `reference_site_id` | non-empty string | Stable producer-owned site identity. |
| `canonical_kind` | non-empty string | Producer kind normalized only by Miller's fixed aliases: `calls` → `call`, `imports` → `import`, `type_references` → `type_usage`, `implements` → `implementation`; every other kind is unchanged. |
| `language` | non-empty string | Producer language identifier. |
| `path` | non-empty string | Workspace-relative producer path. |
| `source_symbol_id` | string or null | Containing source symbol ID from the producer site. |
| `source_symbol_name` | string or null | Containing source symbol name. |
| `source_symbol_kind` | string or null | Containing source symbol kind. |
| `source_symbol_is_test` | boolean or null | Typed producer test evidence for the source symbol. |
| `span` | object or null | Null for a spanless site; otherwise all six integer fields are present: `start_line`, `start_column`, `end_line`, `end_column`, `start_byte`, `end_byte`. |
| `is_exact` | boolean | Producer `reference_sites.is_exact`; never inferred from overlap. |
| `site_provenance` | non-empty string | Producer `reference_sites.provenance`; never synthesized by this export. |
| `target_symbol_id` | string or null | Resolved target symbol ID, or null when unresolved. |
| `target_name` | non-empty string | Resolved target name or producer target display name. |
| `target_symbol_kind` | string or null | Resolved target symbol kind. |
| `target_symbol_is_test` | boolean or null | Typed producer test evidence for the target symbol. |
| `resolution_status` | string enum | Exactly `resolved` when `target_symbol_id` is non-null; otherwise `unresolved`. |
| `resolution_tier` | integer or null | Minimum non-null producer resolution tier among evidence for the assertion. |
| `confidence` | number | Maximum producer confidence among evidence for the assertion. |
| `provenance` | array of strings | Distinct evidence sources in precedence order: `identifier_direct`, `identifier_resolution`, `relationship`, `pending_resolution`, `name_fallback`. |
| `artifact_id` | string or null | Producer artifact identity from `artifact_metadata`. |
| `workspace_revision` | integer or null | Maximum extraction revision when present. |

Rows from identifiers, relationships, and resolution overlays are assertions about the same canonical site.
Miller groups them by `reference_site_id`, target, and canonical kind; it does not deduplicate by overlapping
consumer spans or fabricate site identity.

The assertion target key is `target_symbol_id` when resolved and `target_name` when unresolved. Output order is
ordinal `(path, start_byte-or-max, reference_site_id, canonical_kind, target_symbol_id, target_name)`.
