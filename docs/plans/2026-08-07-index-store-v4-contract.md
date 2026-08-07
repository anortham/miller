# v4 Store Contract — versioned index store + views

**Status: DRAFT — freeze blocked on** the cycle-3 cross-model re-attack (codex + grok, §17).
The binding-mechanism proof is COMPLETE with verdict GO
([`../findings/2026-08-07-index-store-binding-proof.md`](../findings/2026-08-07-index-store-binding-proof.md)
— the Ph0 §9 red-gate discharge; G3b marginal, re-proven in Ph2 per its carried condition 1).
Do not implement against a DRAFT contract.

**Program:** [`2026-08-06-index-store-views-program.md`](2026-08-06-index-store-views-program.md).
**Gate:** [`../findings/2026-08-06-index-store-ph0-gate.md`](../findings/2026-08-06-index-store-ph0-gate.md).
Every section below cites the Ph0 evidence it rests on. Where this contract sets a number, the
number is a **default with a named tunable**, not folklore: defaults come from Ph0 measurements
and carry the instrument that produced them.

This document is the inter-repo seam. julie-extractors implements the write side (Ph2); Miller
implements the read side and sidecars (Ph3). The v3 artifact contract remains in force until the
store ships; `store export` (§10) preserves the copyable-artifact property afterwards.

---

## 1. Store layout and family identity

```
~/.miller/stores/<family-id>/
  CURRENT                # one line: active generation name, e.g. "gen-001"; atomic rename to flip
  gen-001/
    store.db             # julie-extract writes (store-writer lease)
    search.db            # Miller writes (sidecar-converger lease)
    vectors.db           # Miller writes
    content.db           # Miller writes
  coord.db               # coordinator queue + leases (own WAL; never inside a generation)
  spool/                 # julie-extract spool (supervision policy carries from v3)
```

- A **generation** is the promotion unit for the repair tier only (§12): corruption heal,
  incompatible-epoch migration, compaction, secure-purge escalation. Routine writes never create
  a generation. Readers resolve `CURRENT` once per read session and pin the generation they
  opened; a promoted-away generation is retained until no pinned reader remains (§12 capacity
  formula counts it).
- `coord.db` sits outside generations: the queue's history must survive a generation promotion,
  and its WAL must never contend with bulk import transactions in `store.db`.

**Family identity.** A family is a git common-dir lineage (rebind program's registry lineage
columns); a non-git workspace is a family of one.

- `family_id` is a **UUID minted at family creation** and stored in three places: the store's
  `store_meta`, the registry's family row, and each member workspace's pointer file. It is NOT a
  hash of the common-dir path — path hashing recreates the workspace-id path-reuse trap
  (`git worktree remove && git worktree add` yielding the same identity for a different lineage).
- The registry family row records `(common_dir_canonical, common_dir_identity)` where identity =
  the admin dir's creation evidence (the `WorkspaceRootIdentity` pattern from
  `WorkspaceRootPresenceMonitor`). Resolution on open: canonical common-dir looks up the family
  row; an identity mismatch across a disappearance means a new lineage → a NEW family (the old
  store ages out by retention). Missing identity evidence never counts as a replacement.
- Workspace → family resolution happens **once per read session** (§8) via the pointer file,
  validated against the registry; the store's `views` table is the source of truth for view
  identity, and the registry row + pointer file are caches reconciled idempotently on open
  (doubt-pass finding 12).

**What remains in `<workspace>/.miller/`:** the pointer file (family id + view id), logs,
`scan.progress`, spool (for v3-mode workspaces), `scan-failure.json` (now keyed per view), and
`history.db` (branch-local metric trends). Nothing else.

## 2. Version identity, determinism, and the schema classes

**Version identity is input-keyed:** a `file_version` is
`(relative path, blake3 content hash, extraction-identity epoch)` — §1's fingerprint semantics
define the epoch. Output bytes never participate in identity: an import that finds a complete
version at the required level skips extraction entirely (pre-merge finding 4 in the gate).

**Determinism is a v4 prerequisite with its own CI gate.** The extractor's `metadata_json` is
byte-nondeterministic today (`Option<HashMap>` on seven structs — purity audit §4; 98.6% of
miller files carry an unstable row). v4 requires: the seven declarations become ordered maps
(`BTreeMap` or sorted-key serialization), and julie-extractors CI carries the gate *scan the
same tree twice → every extraction table byte-identical*. This does not gate dedup (identity is
input-keyed) — it gates the store's row-equivalence proofs, the binding diff producer (§14), and
reproducibility. It lands in Ph2 before any equivalence gate is credible.

**Schema classes** (from the purity audit's 24-table inventory, carried into v4):

| Class | Tables | v4 home |
|---|---|---|
| Per-version (immutable, content-addressed) | `symbols`, `symbol_annotations`, `reference_sites`, `identifiers` (post-surgery), `relationships`, `pending_relationships`, `type_facts`, `type_argument_usages`, `type_arguments`, `literals`, `source_regions`, `structural_facts`, `complexity_metrics`, `parse_diagnostics`, and the pure columns of `files` (path, language, content_hash, content_bytes, line_count, metadata_json) | `store.db`, keyed `(version_id, local_id)` |
| Store-global | `file_versions` (new — the version registry, §3), `store_log` (new — the append log, §13), `store_meta` (new — epochs/floors, §7) | `store.db` |
| Fingerprint-global | `parser_inventory`, `language_capabilities`, `language_capability_fixtures`, `language_capability_gaps` | `store.db`, one copy per extraction-identity epoch — never per view |
| View-scoped | `views`, `view_manifest` (new), per-view scan bookkeeping (absorbs `files.indexed_at`/`last_revision_id`/`status` — surgeries V-2/V-3/V-4), resolution bases + view deltas (§14), `pending_resolutions` (86% cross-file targets — resolution layer, not extraction) | `store.db` |
| Retired | `extraction_revisions`, `revision_file_changes` (replaced by `store_log`), `identifiers.target_symbol_id` + `idx_identifiers_target` (surgery V-1) | — |

**The purity surgeries are contract requirements** (purity audit §3):

- **V-1:** `identifiers.target_symbol_id` is dropped; resolution outcomes live only in the
  resolution layer. Miller's `SqliteSymbolGraphIndex.cs:295` `COALESCE` reads must survive one
  extractor version or land together (cross-repo sequencing recorded in the Ph2 work list, §16).
- **V-2/V-3/V-4:** `files.indexed_at`, `last_revision_id`, `status` move to the view's manifest
  row. A `FailedPreserved` extraction never produces a version row at all — a failed scan is a
  view-side condition, not content.
- **V-5:** the writer's `SymbolLookup` narrows to the file's own symbols for extraction tables
  (empirically inert today — 0 cross-file links over 703k rows — but structurally repo-scoped;
  a store extracting files in isolation must not depend on it staying inert).

## 3. `file_versions` and per-level completeness

One row per version: `(version_id INTEGER PK, path, content_hash, extraction_epoch, size_class
metadata…)`, plus **per-level completeness stamps**: `complete_l1`, `complete_l2`, `complete_l3`
(NULL = not extracted, else the store-log sequence at which the level's last child row committed).

- **A stamp is written in the same transaction as the level's last child row** (write-mechanics
  finding 3: without the same-transaction marker, a crash published a truncated version that
  survived resume with `quick_check = ok`). Dedup at level N trusts only `complete_lN NOT NULL`.
- **Level membership** (level-composition decision table; the SPLIT is a row-subset, not a new
  table):

| Level | Tables (per-version rows) | Bytes (measured, miller) |
|---|---|---|
| L1 — symbol core | `symbols`, `symbol_annotations`, `relationships`, `pending_relationships`, `type_facts`, `complexity_metrics`, `parse_diagnostics`, `files`-pure columns, and the relationship-evidence subset of `reference_sites` (spanless + relationship-span rows) | 27.5% |
| L2 — reference layer | `identifiers`, the identifier-walk subset of `reference_sites` (span-present) | 53.8% |
| L3 — text/facts | `source_regions`, `structural_facts`, `type_argument_usages`, `type_arguments`, `literals` | 18.7% |

- `reference_sites` carries a `level` discriminator (1 or 2); the two subsets are disjoint and
  each level's stamp covers exactly its subset. `type_facts` stays L1 but receives **no
  version-qualified index budget** until a consumer exists (zero consumers in Miller `src/`
  today — level-composition §2).
- Convergence order is **L1 → L2 → L3** (traffic 14.4× favors L2; L2 owns 42% of the deferred
  write cost). Tool degradation while a view's L2/L3 converge: `trace`/`impact` return
  "reference layer converging" with per-level progress; `search regions=doc_comment` (and the
  regions arm generally) reports L3 convergence rather than silently returning empty — the
  degradation matrix line the gate's §3 amendment owed.
- The **resolution layer is not a level**: bases/deltas (§14) key off L2 completeness (identifier
  rows are resolution's input) and carry their own readiness (§14's state machine).

## 4. Composite identity and the index-direction rule

- Every per-version table keys `(version_id INTEGER, local_id)`; `version_id` replaces the
  37-char TEXT `file_id` on all child rows. Measured on the three biggest tables this is net
  **−11.3%** vs today's schema (read-path §6) — the saving that funds divergence headroom.
- `stable_location_id` semantics are unchanged *within* a version; cross-version collisions are
  legal by design (same-length in-place edit), which is why **no retained ID is ever unqualified**:
  primary keys, FK references, sidecar keys, GC keys, and delta targets are all
  version-qualified composites.
- **Index-direction rule** (write-mechanics §1: version-leading btrees reclaim 36–39% on a 40%
  cohort delete; non-version-leading reclaim 2.9–7.1% — their pages strand). Every secondary
  index on a version-keyed table is classified at schema time, in the DDL's comment header:
  - **`gc-aligned`** — leads with `version_id`. Required for: PKs, `(version_id, file-scope)`
    access paths, and any index whose queries always carry a version (path-keyed reads enter
    through the manifest and arrive version-qualified — read-path §4 hybrid shape).
  - **`read-aligned`** — leads with its query key (`name`, `containing_symbol_id`,
    `reference_site_id`); used by candidate-set recall where the version is filtered AFTER the
    seek via the session visibility probe (read-path: integer-rowid probe applied first, ~7×
    cheaper than the manifest join). These indexes are **accepted as
    unreclaimable-until-rebuild**: their fragmentation (measured ≤7% recovery per sweep) is a
    line item in the growth model, and a **scheduled index rebuild** (per-index `REINDEX` in a
    maintenance window, or the §12 compaction promotion) is the reclamation path.
  - The classification is per-index and exhaustive — a v4 DDL review that finds an unclassified
    index fails. `idx_identifiers_target` is deleted with V-1 (1.26% of artifact bytes).
- The smaller per-file tables' v4 DDL was not audited row-by-row in Ph0 (gate §1 caveat); the
  Ph2 implementation carries the audit as a review gate: every FK on every per-version table
  must reference a version-qualified composite.

## 5. Write path: verbs, transactions, durability

julie-extract grows the store verb set; Miller is the only caller.

| Verb | Effect | Failure semantics |
|---|---|---|
| `store import --root <path> --view <id> [--level l1\|full]` | Hash tree; skip versions complete at the required level; extract missing (L1-first; L2/L3 queued per version); append rows in chunk transactions; write new manifest generation; flip the view pointer (one transaction) | Crash: incomplete versions invisible (no stamp), manifest unflipped; resume skips complete versions (measured: reuse == committed in 15/15 trials). The scan-failure journal records per view. |
| `store update --file <path> --view <id>` | Append one version (all levels or L1 per policy) + repoint one manifest row | Same guarantees at single-file scope; never mutates version rows |
| `store delete --file <path> --view <id>` | Remove one manifest row (tombstone in the next manifest generation) | Pure manifest op; version rows untouched (GC owns them) |
| `store gc` | Retention sweep (§6) + staged `incremental_vacuum` + sidecar merge feed entries | Resumable; each stage is its own transaction; a killed sweep leaves a valid store with a longer freelist |
| `store export --view <id> --out <file>` | Materialize a single-file artifact for the view (the copyable-artifact adapter; rollback path §11) | Writes to `<out>.partial`, atomic rename on completion |
| `store import --from-artifact <symbols.db>` | Migration transform (§11) | Preflight-gated; resumable at chunk granularity |

**Transaction granularity (write-mechanics §2, all three findings carried):**

- **Per-chunk commits**, completion stamps in the same transaction as the chunk's last child
  row. Default chunk: **100 versions or a 128 MB WAL budget, whichever binds first** (measured:
  WAL 142 MB at 100 versions, 8.6 MB at 1; single-transaction peaks at 100.6% of the database —
  ~15.9 GB at dotnet/runtime scale — and leaves zero reusable work behind a crash, 3/3 trials).
  Tunable: `MILLER_STORE_CHUNK_VERSIONS` (0 = per-version, for extraction-dominated corpora).
- **`synchronous=FULL`** on every store writer (measured inside noise vs NORMAL); WAL
  autocheckpoint 8,000 pages during bulk import (buys back 1.7× throughput for a 38.5 MB WAL),
  1,000 otherwise.
- **Dedup reads only stamped-complete versions at the required level.** No exceptions; the
  no-marker arm published a truncated version that survived resume and passed `quick_check`.
- Store files are created with `auto_vacuum=INCREMENTAL`, FTS5 `secure-delete` on (sidecars),
  and `page_size=4096`; creation-time pragmas are **verified by reading them back** on every
  created file (setting `auto_vacuum` late is a silent no-op — write-mechanics pragma probes).
  Core `secure_delete` is per-connection and never stored: **every writer connection re-asserts
  it**; the connection factories in both repos own this invariant.

## 6. Retention — the byte, latency, and growth contract

Retention is the central contract (gate: it is the byte lever §5, the latency lever §7, and the
growth guard §10 at once). All parameters live in `store_meta` with dashboard/CLI surfacing.

- **Live** = referenced by any view manifest, any resolution base/delta in use, an in-progress
  rebase, or a pinned reader/generation. Live versions are never collected, never demoted.
- **Default window: 7 days for non-live versions, demoted to L1 at the first sweep after they
  become non-live.** Demotion = physically deleting the version's L2/L3 rows (its L1 rows and
  stamps remain; a branch switched back re-serves L1 instantly and re-extracts L2/L3 in the
  background — exactly the L1-first import path, §3). Measured basis: full-level retention
  breaches the 1.2× budget at any window (7d = 1.39×/1.25× on miller/julie-extractors); L1-demoted
  7d = 1.11×/1.07×, leaving 0.09–0.13× for view deltas (growth model §2.3–2.5).
- **Byte ceiling: prune oldest non-live first when the store exceeds 1.25× the live full-level
  bytes** (the window is a proxy; a large-file churn burst breaks the proxy). Tunable
  `retention_byte_ceiling`.
- **Per-path version cap: 24 non-live versions per path** (default; tunable `retention_path_cap`).
  Git history is a lower bound on version production — the watcher indexes uncommitted states
  and agent fleets churn hot files; window and ceiling are both blind to one hot file churning
  hundreds of versions a day.
- 14 days is documented tunable-up (1.16–1.28×); >4 weeks is opt-in for disk-rich machines.
- **Latency coupling is part of this contract:** retained-version multiples raise read cost
  (retrieval §7 amendment: 1×→20× retained costs word-arm 4.6×, trigram 5.4×, vector KNN 3.6× —
  KNN is brute force over total store rows). The retention telemetry therefore reports the
  retained-version multiple alongside bytes, and the dashboard's family panel shows both.

## 7. Epochs, floors, and serve-while-converging

Two epochs, split per cycle-2 finding 6:

- **Extraction-identity epoch** — part of version identity (§2). A *compatible* extractor change
  (same epoch, new binary) re-extracts nothing. An extractor upgrade that changes extraction
  output bumps the epoch; versions of the old epoch **keep serving** views that reference them
  while new-epoch extraction converges per file in the background; a view flips per-file as new
  versions land. Epoch mismatch means "re-extract owed," never "absent" — upgrade-day cold
  outage is rejected (doubt-pass finding 11).
- **Resolver-output epoch** — julie's `RESOLUTION_VERSION` (currently 6). Base/delta identity
  includes it (§14); a resolver upgrade with an unchanged manifest must not reuse a base built
  under old semantics.
- **Incompatible changes in either epoch** (extraction shape that cannot mix per-file, or
  resolver output that cannot mix per-row) build a **shadow manifest + one coherent resolution
  generation** and flip atomically while the old view keeps serving. Blanket per-file mixing
  across incompatible epochs is not a legal state.

`store_meta` records: `store_format_epoch`, `min_reader_version`, `min_writer_version`,
`created_by_version`, plus the monotonic `binary_version` floor carried from v3 (never goes
backwards; `MILLER_ALLOW_EXTRACTOR_DOWNGRADE=1` remains the escape hatch). A process below a
floor degrades honestly: read-only below `min_writer_version`, not-ready-with-reason below
`min_reader_version`. A newer process migrating the store bumps `store_format_epoch` via
generation promotion (§12), never in place.

## 8. Read path: the view-aware read session

Ph3 introduces one seam — the **read session / connection factory** — as the single way readers
obtain connections; the raw `IndexDbPath` retires from the read contract. A session:

1. Resolves (family, view) once via pointer file + registry, validated against `views`.
2. Resolves `CURRENT`, opens the generation read-only, pins `(store_instance_id, view_id,
   manifest_generation, per-level stamps, resolution generation)` — the **freshness token**. A
   token component changing is the only staleness signal; revision counters are gone.
3. Builds the **session visibility temp table once** (0.23 ms at 1,417 versions — read-path §4;
   built per session, never per query — Miller's open-per-query readers change accordingly).
4. Supplies **view-local ranking state**: BM25 `(doc_count, avgdl)` cached per
   `(view_id, manifest_generation)` (256 bytes for eight views vs 13.8 ms/query re-scanning);
   `df` stays view-local by construction. The canonical DocId is **an order, not a stored
   ordinal**: `score DESC, path ASC, start_line ASC, symbol_id ASC` (retrieval §8;
   `AssignStableDocIds` retires from the store path — the two shipped DocId histories disagree
   at position 0 and a history-dependent ordinal is fatal in a shared store). If the Eros-facing
   `doc_id` UNIQUE column truly needs a dense per-view ordinal, the fallback is the materialized
   mapping (1.82 MB/view, 193.9 ms rebuild per flip) budgeted against save frequency.

**Query routing (read-path §4 hybrid, the answered open question):** path-keyed reads seek
`view_manifest(view_id, path)` (PK seek; the only shape flat under retained history);
name/candidate-set reads seek their `read-aligned` index and filter through the session
visibility probe (integer-rowid first) **before every ranking window and limit** — trigram 200,
vector 500, content `ORDER BY rank LIMIT` all apply visibility inside retrieval (retrieval §7:
the naive post-filter starves 112/120 queries at 20× retention; sqlite-vec pre-applies
`rowid IN (…)` and returns the exact dedicated top-K; post-filtering has no correctness
guarantee at any k).

Reader transactions stay **bounded** (open-read-close per query) so pinned generations cannot
hold the WAL open; checkpoint policy and WAL-size telemetry are store-contract items (§5, §13).
Degradation is truthful per §3's matrix. Acceptance bar unchanged: per-view results
row-equivalent to a dedicated index, lexical output **byte-identical** — achieved via view-local
statistics, verified by the equivalence gate on a multi-language fixture including adversarial
retention histories.

## 9. Sidecars: converge-once, cursors, stamps

`store_log` (§13) replaces `revision_file_changes` as the converge feed. Each sidecar consumes
through an **idempotent cursor** (its own table in the sidecar: last applied log sequence) and
publishes a completeness stamp the read session validates; a crash between store commit and
sidecar commit replays cleanly — no cross-WAL atomicity exists to rely on (doubt-pass finding 8).

- **search.db** — FTS rows keyed `(version_id, …)`; word arm + collapsed-trigram arm carry
  forward; recall-only stays true.
  **Trigram ordering-key decision (gate §7 amendment): the window's ordering changes
  `rank` → stored `collapsed_len`, and it ships EARLY — in the per-workspace sidecar, before the
  store cutover.** Rationale: FTS5 `rank` bakes whole-table statistics into the ordering key, so
  a shared store contaminates it with no filter placement that can fix it (a probe with only
  non-matching hidden rows moved 98 of 200 window members); `collapsed_len` is
  corpus-independent, measured faster at 20×, states the window's documented intent directly,
  and is independently correct on today's per-workspace sidecar. Shipping early gives the change
  its own equivalence gate on the simple architecture and removes a confound from the store's
  own gate. It is a shipped-contract change: the gate compares old-vs-new window membership and
  final ranked output on real corpora before it lands. **`content.db` inherits the same rank
  finding and was never instrumented (recorded gap):** the early change includes the content-arm
  audit; if content ordering also uses corpus-dependent rank where a stored key exists, it
  changes under the same gate.
- **vectors.db** — family-shared (retrieval §7 answered: pre-filter returns the exact dedicated
  top-K). Caveats carried: brute-force KNN scales with total store rows (retention couples,
  §6); the byte crossover vs private copies ≈ 8× retained multiple. The embedding *cache* is
  family-shared regardless; broker, accelerator lease, `MILLER_SEMANTIC=off` zero-work, and
  ADR-0003 ownership stand unchanged.
- **content.db** — tree-derived text keys by version; explicit external/web imports scope to the
  **family** (an import from one worktree is searchable from siblings — an upgrade over today's
  per-workspace silos).

Writer roles stay parallel by file split: julie-extract owns `store.db` under the store-writer
lease; one Miller sidecar-converger lease per family owns the sidecar files; separate files →
separate WALs → no contention. `ATTACH` serves multi-file reads; no cross-file FKs (version keys
are logical joins); no atomic commit across WALs (the cursor-and-stamp posture is designed for
exactly that).

## 10. GC and secure purge

- **Sweep:** version-cohort deletes under retention (§6) — the delete is the budgeted cost
  (58.09 s for 70,818 FTS docs vs 0.25 s of merging; chunked under the coordinator scheduler,
  §15) — then bounded FTS5 `merge` rounds (64 pages; converges to 2–3 segments; `optimize`
  stays a maintenance-window option), then staged `PRAGMA incremental_vacuum` (2,000-page
  stages; measured max 0.104 s/stage, independent of store size). Implementation note that cost
  a prototype 2,000× the intended statements: the incremental-vacuum statement must be **stepped
  to completion**, not merely executed.
- **Reclaimed bytes are measured after `incremental_vacuum` only** — DELETE and merge leave the
  file byte-identical (the dashboard claim is conditioned on this).
- **Purge (CLI + dashboard POST per ADR-0002):** deletes the named versions across store and
  sidecars, truncates WALs, runs the page-limited merges and staged vacuum, cleans
  spool/temp/export partials and superseded generations. Erasure guarantees hold only where both
  secure-delete switches were active for the data's lifetime (write-mechanics sentinel matrix:
  either switch alone leaves recoverable copies; neither leaves MORE copies — the FTS tombstone
  segment); where they were not (e.g. content migrated from a v3 artifact), purge **escalates
  honestly to a generation rebuild** via §12.
- GC roots: view manifests, resolution bases/deltas and in-progress rebases (§14), pinned
  readers/generations, and the sidecar cursors' unconsumed log window (§13).

## 11. Migration and rollback

- `store import --from-artifact` is a **full transformation**: split the denormalized resolution
  column into the resolution layer, mint version-qualified composite identities, restamp levels,
  rebuild sidecars. It is not metadata ingestion.
- **Capacity preflight before any migration or promotion** (§12 formula). One family at a time
  under the governor; `disk-blocked` posture (vectors-v1 precedent) when preflight fails, old
  artifact serving read-only. Old `<workspace>/.miller` db files are marked reclaimable and
  surfaced in the dashboard — never silently deleted.
- **Rollback honesty:** once views advance in store mode, per-workspace artifacts are stale.
  Switching the store off triggers a current-view `store export` per active workspace (or an
  honest not-ready until one completes) — never a silently served stale artifact. The store
  on/off switch ships v1 (default-on vs opt-in is the Ph5 validation decision, user-owned).

## 12. Generation promotion and the capacity formula

Promotions (repair tier only: corruption heal, incompatible-epoch migration, compaction,
secure-purge escalation) build `gen-<n+1>/` beside the live generation and flip `CURRENT`
atomically. The v3 lesson carries: never point a rebuild at the served files.

**Preflight formula (write-mechanics §3, corrected — a MAX over phases, not a sum):**

```
required = max over phases of (
    live generations still addressable        # incl. any pinned by readers (+926.2 MB measured for one)
  + the generation being written
  + all sidecars of both
  + WAL/temp live in THAT phase               # a retention sweep's WAL alone measured 56% of store size
)
```

Measured accuracy where terms coexist: −0.03% / −0.02%; the summing formula over-reserved 17.35%
on the retention-first arm because the sweep's WAL checkpoints away before the rebuild starts.
**Retention cleanup runs before capacity is judged** (measured: peak −29%, final store −40%).
Every promotion preflights; failure = `disk-blocked` with the old generation serving read-only;
promotions are resumable at chunk granularity. The preflight also **verifies creation pragmas**
(`auto_vacuum=INCREMENTAL`, FTS5 secure-delete) by read-back on every file it creates (§5).

## 13. `store_log` — the append log

One store-global, monotonically-sequenced log of durable events: version level-completions,
manifest generation flips, resolution generation flips, GC sweeps, purges, promotions. It is:

- the sidecar converge feed (§9 cursors),
- the freshness substrate (§8 tokens name the sequences they pinned),
- the coordinator's committed-effect record for idempotency (§15).

Entries are written in the same transaction as the effect they record. The log is pruned to the
oldest unconsumed cursor minus a safety window; cursor liveness is a GC root (§10).

## 14. Resolution bases and view deltas — state machine

**Producer: the proven serve-base + background-converge mechanism**
([`../findings/2026-08-07-index-store-binding-proof.md`](../findings/2026-08-07-index-store-binding-proof.md)
— G1/G2/G4/G5 PASS, G3 overhead marginal and re-proven in Ph2 per carried condition 1; the
refuted P1a scoped pass appears nowhere in this contract). Storage shape per Ph0 §5: shared
base ≈ 11.5% of store bytes, per-view deltas ≈ 1.9% for seven siblings.

**Objects and identity.**

- **Base** = a complete, consistent resolution set for one manifest, keyed
  `(manifest_hash, resolver_output_epoch)`. **A base is its own database file**
  (`bases/base-<key>.db` in the generation dir) — the separate-file shape is the bulk-rate
  precondition (§16.4): built with `journal_mode=MEMORY` into a scratch path, made **ready by
  atomic rename**, immutable afterwards, deleted whole by GC (physical reclamation is file
  deletion — no vacuum needed). A `bases` row in `store.db` records the key, file, byte size,
  row count (table counts are authoritative — scan-report counters run 3–13 rows high, proof
  condition 5), and ready state.
- **Delta** = one **cumulative** per-view row set in `store.db` (no chains in v1), keyed
  `(view_id, delta_generation)`, rows version-qualified like everything else. Two row forms:
  - *Replacement:* a full outcome row for a `(version_id, identifier_id)` — used for versions
    absent from the base's manifest AND for shared versions whose outcome diverges (the
    cross-file effects; measured 2.4–9.7% of base rows on typical sibling pairs).
  - *Tombstone:* "no row" for a version-qualified key. `identifier_resolutions` is a total
    function (one row per identifier), so its delta needs replacements only — but the contract
    keeps tombstones because `pending_resolutions` is a partial relation whose rows genuinely
    disappear (purity audit E-3: 4 → 2 rows on an unrelated add). The store equivalence gate
    verifies the totality assumption rather than assuming it.
- **Precedence:** delta beats base for the same version-qualified key; visibility (the view
  manifest) filters both first, so base rows for versions outside the view's manifest need no
  tombstones at all. Reads resolve `COALESCE(delta, base)` under visibility — the read-path
  `NOT EXISTS` cost (+8.6–15.1%) is the measured price, with the materialized per-view
  effective-resolution index as the named fallback if it regresses at scale (read-path §4).

**View resolution states.**

```
unbound → bound(base_id, delta_gen, exact=false)   # foreground bind: manifest + base pointer
        → bound(base_id, delta_gen, exact=true)    # delta published atomically
```

- **Foreground bind is O(manifest):** write manifest rows, point at the nearest ready base
  (v1 nearest = the family base sharing the most manifest versions at the same resolver
  epoch). Measured 2.0–3.4 ms for 1,081–1,700 rows, zero identifier work (proof G5).
- **Background convergence:** fresh-output resolution pass over the view's corpus at the bulk
  rate (71–85k rows/s measured) into a scratch file → diff vs the base → delta rows. The diff
  runs as a **streaming merge-join over sorted natural keys or SQL-side** (proof condition 3:
  a naive in-memory diff is ~10 GB at dotnet/runtime scale). Publishing is one transaction:
  insert delta rows, flip `(delta_gen, exact=true)`, append the `store_log` entry.
- **CAS publish/rebase:** the converge job records the `(manifest_generation, delta_gen)` it
  computed against; the publish transaction compare-and-swaps on both. A manifest that moved
  mid-converge aborts the publish; the job re-diffs against the new manifest (versions already
  resolved in the scratch output are reused — the pass is corpus-keyed, not manifest-keyed).
- **First view of a family** (bootstrap): no base exists — the scratch output *becomes* the
  base (atomic rename), delta empty, exact immediately. Same pipeline, no diff.
- **Identical manifests share:** views with equal `(manifest_hash, epoch)` bind the same base
  with empty deltas (dedup of the resolution layer across same-commit worktrees).

**Serve-window honesty (contract posture, supersedes "binds in seconds"):** during
convergence a view serves the base's resolution for shared versions; identifiers of
non-base versions have no resolution rows yet. `trace`/`impact` and `workspace status` report
`resolution: converging` with the enumerated gap — **rows and files, never "N files changed"**
(the delta spills past changed files by the nature of the resolution graph; measured worst
in-band 29.6% of rows / 170 files). Enumeration cost is bounded by the diff itself (G4).
**SLO:** foreground milliseconds; time-to-exact = corpus resolution at the bulk rate + diff
(measured 4.1–7.6 s at ~1,400 files under load; ≈ 232 s projected at dotnet/runtime — flagged
inference, Ph5 measures). Exact-equivalence for resolution-derived reads applies at
`exact=true`; during the window the program's equivalence bar is explicitly relaxed to
"base-consistent + honestly reported" (this sentence is the program-text statement the gate's
§9 amendment required).

**Rebase policy:** a background job folds a view into a fresh base when its delta exceeds
**10% of base rows** (typical gaps measured 2.4–9.7%; the worst measured pair, 29.6%, would
rebase — correctly) or when read-path telemetry shows precedence cost regressing. Rebase = the
same converge pipeline with "scratch becomes a new base" + CAS; the old base is retired when
its last referent moves (GC root rules below).

**Epoch interaction (§7):** a resolver-output-epoch bump obsoletes base identities; old-epoch
bases keep serving their views while new-epoch bases build (serve-while-converging); the flip
is the §7 shadow-generation flip. Bases never mix epochs.

**GC roots for the resolution layer:** ready bases referenced by any view; building bases and
in-progress rebases (registered as coordinator queue entries — a crash leaves the queue row,
and successor recovery either resumes or releases it); every live view's delta; bases
referenced by pinned read sessions. An unreferenced retired base is a file delete.

## 15. Concurrency: the family coordinator

**Model: coordinator-executes-queued-requests over a durable protocol** (doubt-pass finding 10 +
cycle-2 finding 4 — today's converge queue claims by file-rename and deletes expired claims;
acceptable for freshness nudges, not for import/repoint/GC).

- **The coordinator is the store-writer lease holder.** The lease is **time-boxed with heartbeat
  and takeover** — not v3's lifetime leadership. Eligibility keeps the version-aware invariants
  (binary floor monotonic; newer-writer displacement via the queue; equal versions never
  displace a LIVE holder but MAY take over an expired lease). The sidecar-converger lease is the
  Miller-side mirror for sidecar files.
- **Queue (in `coord.db`):** `requests(request_id, idempotency_key UNIQUE, kind, payload_json,
  state, requester_id, requester_deadline, claim_owner, claim_heartbeat_at, result_json,
  error_json, created_at, updated_at)` with states
  `queued → claimed → committed → acknowledged | failed`.
  - *Claim* is a CAS on `(state='queued')` with owner + heartbeat; a successor coordinator
    **re-claims** requests whose owner heartbeat is stale — it never deletes them.
  - *Committed* is written in the same transaction as the effect's `store_log` entry;
    re-execution of a claimed-but-uncommitted request after takeover is safe because every verb
    is idempotent at the store layer (version identity is input-keyed; manifest flips are
    generation-CAS; GC stages are re-runnable). The `idempotency_key` dedups requester retries.
  - *Result delivery:* requester polls its request row; `requester_deadline` lets the
    coordinator drop acknowledgment obligations for dead requesters (the row is kept for the
    log-pruning window).
- **Scheduling:** long operations (imports, rebases, migrations, GC) run **chunked** (§5's chunk
  = the scheduling quantum); between chunks the coordinator services queued single-file and
  repoint requests. Fairness: two classes — interactive (update/delete/repoint/open) and batch
  (import/GC/rebase/migration) — with interactive always draining first between batch chunks;
  starvation is bounded by the chunk quantum (measured worst chunk commit ~seconds at default
  size). Head-of-line blocking of sibling views by one import is thereby structural, not
  best-effort.
- **Lock order (global, mandatory):** machine governor → store-writer lease →
  sidecar-converger lease. Locks are acquired in order and released in reverse; no process
  waits on an earlier lock while holding a later one. **Deadlock analysis:** all `store.db`
  writes execute in one coordinator (no writer-writer cycles); the only multi-lock chain is the
  ordered triple; sidecar convergence holds its own lease and reads `store_log` without taking
  the writer lease — so the wait-for graph is acyclic by construction. The analysis lives here
  as a contract test obligation (Ph2/Ph3): a lock-order violation test and a
  coordinator-takeover crash test are named acceptance items.
- The machine governor remains **admission control only** (fleet-safety line unchanged).

## 16. Ph2 work list (julie-extractors)

Fix surfaces audited with citations in
[`spike/index-store-ph1/julie-path-audit/results.md`](../../spike/index-store-ph1/julie-path-audit/results.md)
(Task 2). Anchor correction it recorded: the writer lives in
`crates/julie-extract-artifact/src/writer.rs` (Ph0 docs cite a `julie-extract-cli` path that
does not exist).

1. **metadata_json determinism** (§2): the seven `Option<HashMap>` declarations →
   `BTreeMap` (plus the `metadata_flag` signature, `extraction.rs:962-968`). The CI gate must
   run the two scans in **separate processes** (`RandomState` reseeds per process — a
   same-process double scan passes while the defect is live), assert `(pk, metadata_json)`
   equality across all six carrying tables, assert **at least one multi-key object** (vacuity
   guard), on a **multi-language fixture** (C#-only misses ~60k of the ~121k exposed rows).
   Proven shipping today: 61/118 symbols rows differed across two processes on a 3-file
   fixture; invisible to `operations_contract.rs:2809`, which compares only overlay tables.
   Note for release notes: the fix is an artifact-content change (one-time byte churn for
   byte-wise artifact diffing; Miller freshness is content-hash-keyed and unaffected).
2. **The purity surgeries** (§2): V-1 with cross-repo sequencing (Miller's `COALESCE` fallback
   survives one version or lands together); V-5 SymbolLookup narrowing; V-2/3/4 column moves.
3. **Resolution scope cost — three measured tiers** (Task 2 §2.1; carried as julie's shipped
   converge machinery, NOT the view binder):
   - **Crossover re-denomination FIRST**: `delta_scope_crosses_over`
     (`resolution.rs:2681-2694`) compares scope **file count** vs `files × 0.7`; measured on
     120 sampled saves it fires **never** (median widened scope = 35.6% of files holding
     **87.3% of identifiers**), parking every save on the path Ph0 measured as slower at high
     coverage. Re-denominate in identifier rows (~5 lines + re-running the
     `resolution_perf.rs` sweep that sets the constant). This also improves SHIPPED Miller
     behavior immediately — the watcher's `update --file` pays 16–18 s of near-full resolution
     per typical save today (measured; `miller edit apply=true` pays it too on the leader).
   - Kind-based name filtering (drop `SymbolKind::Variable` from cross-file unions) is sound
     (tier analysis: tier 1 is same-file; tier 4 never admits Variable) but measured **1.1× on
     typical files** — do not budget it as the fix.
   - **Row-level scoping is the real redesign** (the file arm is the amplifier: 47 names →
     1.6% of rows but 27% of files holding 80.2% of identifiers); only this reaches
     delta-sized cost. Gates: `resolution_scope_equivalence.rs` + the four delta-hazard cases
     + `writer_contract.rs` scope tests.
4. **Bulk-path eligibility is a write-target property with a structural precondition** (Task 2
   §2.2): two of the three bulk effects are connection-global
   (`journal_mode=MEMORY`/`synchronous=OFF`; `drop_secondary_indexes` drops every index in the
   database), so **the bulk win exists only if the fresh resolution output is written to its
   own database file** (attached or own-connection; discarded whole on a torn write). The v4
   contract adopts that shape: §14's base builds write to a separate resolution file per
   generation — the binding proof's instrument models exactly this (fresh `$TMPDIR` artifact).
   `verify_foreign_keys` becomes O(output) by scoping the check to the new file.
5. **A `resolve` verb (resolution-only, no extraction)** (Task 2 §2.3): FEASIBLE —
   `resolve_workspace(tx, scope)` is already public and extracts nothing; blocked today by
   writer-coupled entry points, revision bookkeeping, and undefined freshness for
   `is_full_scan: true, whole_corpus: false`. Contract requirements: explicit caller-stated
   scope; MUST NOT set `whole_corpus`/`corpus_current` (it hash-checked nothing — the contract
   names the third freshness state "resolution current at revision N, corpus currency
   unchanged"); idempotent; output byte-equivalent to a full scan's overlay
   (`resolution_scope_equivalence.rs:166` promoted to a contract test); bulk output per item 4.
   It is a composability fix, not a cost fix — cost is item 3.
6. **The store verb set** (§5), store schema (§2–4), `store_log` (§13), coordinator protocol
   (§15 store side), GC/purge (§10), promotion preflight (§12).
7. **Equivalence gates**: store row-equivalence (incremental-converged ≡ from-scratch, closing
   the P1a oracle caveat), crash-point matrix, mixed-version/floor matrix — all on a
   multi-language fixture (language-parity rule).

## 17. Review record

**[PENDING Task 5 — codex cycle-3 re-attack + grok review, with per-finding dispositions. The
held-open doubt items this review must close: cycle-1 #2 (bootstrap cost — closes via §14's
proof), #9 (GC physical reclamation — closed by §10/write-mechanics), #11 (fingerprint
compatibility — closed by §7); cycle-2 #2 (state machine — §14 on GO), #4 (durable queue —
§15), #7 (commit granularity — §5).]**

---

## Cross-reference: gate price list → contract sections

| Gate amendment | Resolved in |
|---|---|
| 1. metadata_json determinism | §2 (requirement + CI gate), §16.1 |
| 2. Trigram `rank` → `collapsed_len` (+ content.db gap) | §9 (ships early, own gate, content audit included) |
| 3. Binding mechanism NO-GO | §14 (replacement proven GO — findings doc; G3b re-proof carried to Ph2) |
| 4. Retention as the central contract | §6 (defaults + tunables + latency coupling) |
| 5. Durability contract | §5 (per-chunk + marker + FULL) |
| 6. Index-direction reconciliation | §4 (`gc-aligned` / `read-aligned` classification per index) |
| 7. Promotion preflight | §12 (max-over-phases + sweep-WAL term + pragma read-back) |
