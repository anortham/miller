# Pre-merge fix report — `docs/contracts/vectors-v1.md`

Worktree `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`, branch
`worktree-semantic-p1`. Only `docs/contracts/vectors-v1.md` was modified. Amended in place under the
contract's own pre-ship exception; an explicit amendment record was added to the frozen-means-frozen preamble
(the previous "No amendments have been made" sentence was replaced).

## Finding 1 (high) — single-file layout could not deliver rollback

Replaced the single-file layout with **generation-addressed retention**.

- **Generation tag.** `first 16 lowercase hex of SHA-256("<encoder_fingerprint>\n<storage_schema>")`, where
  `<encoder_fingerprint>` is the full `sha256:<64 hex>` string and `<storage_schema>` the full lane string,
  joined by one `\n` with no trailing newline. Filename `vectors.gen-<tag>.db`. The tag excludes
  `corpus_generation`, `reader_compatibility`, and `fusion_profile` — none makes a generation unreadable by a
  matching encoder. At most one retained file per tag.
- **Compatible vs incompatible promotes.** New subsection. Same tag ⟹ compatible ⟹ no retention (escalated
  full rebuild, fragmentation compaction, `corpus_generation`-only rebuild). Different tag ⟹ incompatible ⟹
  retain. A `min_reader_version` bump alone is not a tag change and is explicitly called out.
- **Ordering/atomicity.** Retain-then-promote, both under one hold of the single-writer lock. Retain failure
  aborts the promote (active artifact untouched, shadow left as `.rebuild`). Promote failure after a
  successful retain is a documented recoverable state — `unavailable (promote interrupted — rebuilding)` —
  because the retained file is a complete `ready` generation.
- **WAL/sidecar handling.** Any file about to be renamed (retained or promoted) must be self-contained: WAL
  folded, `-wal`/`-shm` deleted first. Retained files are immutable and carry no `-wal`/`-shm`. Reader
  connections open with pooling disabled.
- **Discovery.** Three-step reader resolution: active `vectors.db` first (no sibling enumeration on the happy
  path); else enumerate `vectors.gen-*.db` newest-mtime-first and take the first matching
  encoder/storage/`ready`/`min_reader_version`; else `incompatible` → lexical. Readers open retained files
  read-only and never write, delete, or rebuild them.
- **GC.** Targets are exactly stale `.rebuild` trios plus soaked-out `vectors.gen-*.db`; never `vectors.db`.
  Never within soak (mtime-measured), never with a known live compatible reader, never the only `ready`
  generation (if `vectors.db` is absent or not `ready`, all retained files are off-limits), one file per tag,
  oldest-mtime first past the retention cap.
- **Windows.** Plain renames on every platform, explicitly no inode tricks, with the rationale that an open
  handle dies on close, is invisible to GC, and does not exist on Windows. Promote-retry timeout policy
  applies to both renames.
- **Sections updated for the new layout:** file-placement table (active / shadow / retained rows);
  `MILLER_SEMANTIC=off` zero-work definition (no sibling enumeration or stat); invalidation-matrix rows 1–2
  (now name retention explicitly); corruption recovery (per-file rules — corrupt active rebuilds without
  touching retained; corrupt retained deletes on its own without touching active and is not rebuilt; corrupt
  `.rebuild` restarts the shadow); status vocabulary (`ready` identical for active vs retained,
  `incompatible` means no generation *anywhere* matches); JSON-facts line (serving tag, active-vs-retained,
  retained inventory); Conformance item 5 plus a new item 6 for retention/discovery/GC.

## Finding 2 (high) — chunk-cursor gate unsafe across full rebuilds

Rebound the gate to **artifact identity first, ordering only within an identity**. Four numbered rules:

1. **Artifact binding.** `vectors_meta.artifact_id` and a new `chunk_source_artifact_id` cursor key must both
   equal the current `symbols.db` `artifact_metadata.artifact_id`. On any `artifact_id` change the chunk
   cursor is **reset to 0** and restamped *before* any comparison against *R* — so a reset cursor cannot
   accept a stale higher number.
2. **Schema and chunker agreement.** `content_meta.schema_version` == `chunk_content_schema_version` (pinned
   `2`), and `content_meta.chunker_version` == the chunker component of `corpus_generation`.
3. **Ordering within the bound artifact.** `content_meta.workspace_revision >= R`, valid only after rule 1.
4. **Per-source hash agreement.** Every active workspace-derived `content_sources` row backing a committed
   chunk must have `content_hash` equal to that path's `symbols.db` `files.content_hash` (both normalized,
   both `blake3:`). Disagreeing units are deferred, not embedded; the cursor advances only when no
   hash-deferred unit remains in the *R* span.

`chunk_source_artifact_id` was added to both the cursor table and the `vectors_meta` key table.

**Content-corpus binding: it does NOT exist.** Verified against `docs/contracts/content-corpus-v1.md`
§`content_meta` and the real DDL in `src/Miller.Indexing/ContentCorpusSchema.cs` — `content_meta` carries
`schema_version`, `workspace_revision`, `chunker_version`, counts, `updated_at_utc`, and skip counters, with
**no `artifact_id` column** and no other binding to the source `symbols.db`. `content_sources` does carry a
per-row BLAKE3 `content_hash` and `workspace_id`/`workspace_revision`, which is what rule 4 exploits. An
explicit blockquote records the strongest-implementable-today rationale and flags the **lead-owned P2b
follow-up**: extend `content_meta` with the source `symbols.db` `artifact_id` (a content-corpus contract
amendment). `content-corpus-v1.md` was NOT edited.

## Finding 3 (medium) — matrix/trigger contradiction

Escalation trigger 3 now reads "Either `encoder_fingerprint` or `storage_schema` changed."
`corpus_generation` was removed, plus a clarifying paragraph: a `corpus_generation` change takes the targeted
re-embed path and reaches shadow **only** through trigger 2 (the changed-ratio threshold), never
automatically; and such an escalation yields a same-tag shadow, i.e. a compatible promote that retains
nothing.

## Finding 4 (medium) — glob prefiltering not implementable

`symbol_vector_map` gained `path TEXT NOT NULL`, plus
`CREATE INDEX symbol_vector_map_path ON symbol_vector_map(path);`. New prose states both mapping tables carry
`path` because the mapping tables — not vec0 metadata columns — are the glob-resolution surface for both unit
kinds, that `symbol_vector_map.path` uses the same workspace-relative forward-slash form as
`symbol_vectors.path`, and that mapping and metadata values for a given `rowid_ref` are always identical
(metadata for equality filters, mapping for `LIKE`/`GLOB`). § Query rules now names both columns explicitly.

## Verification checks

| # | Check | Result |
|---|---|---|
| a | No remaining text assumes a single `vectors.db` where retention matters. `grep` for `the live one`, `live artifact`, `old generation`, `previous generation`, `overwrite-move`, `inode`. | PASS. `overwrite-move` eliminated (now "rename"). Invalidation-matrix rows 1–2 rewritten to name retention. Remaining "live"/"inode" hits are the intentional Windows-semantics paragraph explaining why inode tricks are *not* used, and the compatible-promote row where overwriting is correct. |
| b | Gate/trigger/matrix trio contradiction-free. Every one of the 11 `corpus_generation` occurrences read in context. | PASS. Matrix row = targeted re-embed w/ ratio escalation; trigger 3 no longer lists it; new clarifying paragraph ties them together; generation-tag section explains its exclusion from the tag; chunk-gate rule 2 ties chunker_version to it consistently. No occurrence asserts automatic shadow rebuild. |
| c | DDL and prose agree on the new path column. | PASS. `symbol_vector_map` DDL has `path TEXT NOT NULL`; index created; mapping-table prose and § Query rules both name `symbol_vector_map.path` and `chunk_vector_map.path`; amendment note in the preamble matches. |
| d | Cross-references still accurate. | PASS. `FullRebuildPromotion.cs`, `SymbolSearchSidecar.cs`, `SidecarCorruptionRecovery.cs`, `ContentCorpusSchema.cs`, the design plan, `content-corpus-v1.md`, and `semantic-sidecar-protocol-v1.md` all exist on disk. `FullRebuildPromotion` is now cited in two places (shadow lifecycle preamble, chunk-gate rationale) and both statements match its documented revision-counter-restart behavior. Design §5.1's rollback requirement ("last compatible generation preserved… GC after a soak window") is now actually delivered rather than asserted. |
| e | § Conformance and status vocabulary consistent with the new layout. | PASS. Conformance item 2 now requires all four chunk-gate rules and forbids a bare revision comparison; item 3 requires `path` on both mapping tables; item 5 extends zero-work to sibling enumeration; new item 6 covers retention/discovery/GC. Status vocabulary: `ready` explicitly covers retained serving, `incompatible` requires no match across active *and* retained, JSON-facts line lists tag/active-vs-retained/inventory. |
| f | All in-document anchors resolve to real headings. | PASS. 8 distinct anchors used, all matching headings, including the new `#compatible-vs-incompatible-promotes`. |

## Concerns

- **`content_meta` has no artifact binding** (the main one). Rule 4's per-source hash agreement is a genuine
  identity gate and is implementable today, but it is per-unit work at commit time rather than a single
  cheap meta comparison. The P2b content-corpus amendment would make the gate both cheaper and stronger.
- Retention cap and soak window are named as knobs but not given numeric values, consistent with how the
  contract already treats the escalation ratio threshold. If the lead wants them pinned at contract level,
  that is a small follow-up edit.
- "A known live compatible reader" as a GC precondition is stated as a rule without pinning the detection
  mechanism; P2b will need a concrete liveness signal (the existing writer-lock/registry machinery is the
  obvious candidate).
