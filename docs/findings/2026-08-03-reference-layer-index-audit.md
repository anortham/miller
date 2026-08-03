# Reference-layer index audit — five droppable indexes, 3.7 GiB (16%) of the dotnet artifact

Follow-up to [`2026-08-03-dotnet-runtime-v2231-baseline.md`](2026-08-03-dotnet-runtime-v2231-baseline.md):
the dotnet/runtime artifact spends ~8.7 GiB on `identifiers`/`reference_sites` indexes. This audit
determined which of them any consumer actually uses. Schema changes land in julie-extractors
(ride the next minor with the #18 fixes); this document is the evidence.

## Method

1. Exhaustive SQL inventory of both codebases (two independent sweep agents): every statement
   touching `identifiers`/`reference_sites` in julie-extractors (writer, resolution,
   resolution_store, locator, jsonl export, reports, FK cascades, the reference_sites identity
   trigger) and in Miller `src/` (ReferenceEvidenceReader, ReferenceExportReader,
   SqliteSymbolGraphIndex, SymbolGraphReader, DeadCodeCandidateReader, ExtractReader).
2. `EXPLAIN QUERY PLAN` for every distinct query shape against a real artifact (Miller's own
   `symbols.db`), collecting the set of indexes the planner chooses. julie-extract never runs
   `ANALYZE`, so plans are schema-deterministic (no `sqlite_stat1` drift).

## Verdict

Chosen by at least one real query — KEEP:

| Index | dotnet size | Who needs it |
| --- | --- | --- |
| `sqlite_autoindex_identifiers_1` (PK) | 0.56 GiB | every resolution read/write, all Miller joins |
| `idx_identifiers_target` | 0.25 GiB | Miller inbound refs (trace/inspect/graph reverse) |
| `idx_identifiers_containing` | 0.48 GiB | Miller outgoing refs/callees/dependencies |
| `idx_identifiers_name_kind` | 0.36 GiB | julie delta worklists by name; Miller name fallbacks + dead-code counts |
| `idx_identifiers_file` | 0.56 GiB | julie delta worklists/locator by file, report attribution, FK cascade |
| `sqlite_autoindex_reference_sites_1` (PK) | 0.94 GiB | identity trigger + every Miller join |
| `idx_reference_sites_file` | 0.68 GiB | report attribution, FK cascade |

Chosen by NO query in either codebase — DROP:

| Index | dotnet size | Why it exists / why it's dead |
| --- | --- | --- |
| `idx_identifiers_path` | 0.95 GiB | original 2026-05-31 schema; no consumer ever filters identifiers by path (file_id everywhere) |
| `idx_identifiers_file_line_name` | 0.72 GiB | added with schema-v4 overlay 2026-07-06 for a locator pattern that shipped as an in-memory `IdentifierLocator` instead; planner picks `idx_identifiers_file` for every file-scoped query |
| `idx_identifiers_reference_site` | 0.68 GiB | all joins run identifiers→reference_sites via the reference_sites PK, never the reverse |
| `idx_reference_sites_span` | 0.76 GiB | no consumer filters reference_sites by span; `start_byte` appears only in ORDER BY after a PK join |
| `idx_reference_sites_containing_symbol` | 0.59 GiB | no consumer filters reference_sites by containing symbol (identifiers carries that axis) |

Total: **3.70 GiB = 16.2% of the 22.84 GiB artifact**, and those bytes are also build time — they
are part of the ~7-minute finalize phase and of every bulk-load row insert.

## Caveats and riders

- Eros is archived and Julie retired; no third consumer exists. If one appears, it gets indexes
  when it shows queries, not speculatively.
- Observed missing-index gaps recorded for completeness, NOT acted on (resolution is
  CPU-bound in `tier_candidates` post-#18, so plan changes are not the current bottleneck):
  `identifier_resolutions.outcome` is unindexed (`propagation_owned_identifiers` filters on it),
  and the `json_extract(metadata_json,'$.receiver')` disjunction in the resolved-worklist query is
  unindexable by construction.
- Validation for the schema change: `EXPLAIN QUERY PLAN` parity for the KEEP set after the drop,
  plus a real-repo scan shape comparison (the baseline doc's method) confirming finalize-phase
  shrinkage and no resolution regression.
