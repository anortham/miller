# v4 Store Contract — versioned index store + views

**Status: AMENDED v4.6 2026-08-10** (original freeze 2026-08-07; cycle-3 review record in §17).
The original freeze and its G3b decision remain historical evidence. The v4.1, v4.2, v4.3, v4.4, and v4.5 amendments record
execution-contract corrections discovered after the first producer/Miller implementation review;
the corrections are normative and must not be represented as completed until the named gates pass.
Post-amendment changes require another versioned amendment and a recorded reason, not silent edits.

**Program:** [`2026-08-06-index-store-views-program.md`](2026-08-06-index-store-views-program.md).
**Gate:** [`../findings/2026-08-06-index-store-ph0-gate.md`](../findings/2026-08-06-index-store-ph0-gate.md).
Every section below cites the Ph0 evidence it rests on. Where this contract sets a number, the
number is a **default with a named tunable**, not folklore: defaults come from Ph0 measurements
and carry the instrument that produced them.

This document is the inter-repo seam. julie-extractors implements the write side (Ph2); Miller
implements the read side and sidecars (Ph3). The v3 artifact contract remains in force until the
store ships; `store export` (§10) preserves the copyable-artifact property afterwards.

## 0. v4.2 post-freeze amendment register

The following amendments are deliberately marked because the shipped code did not yet satisfy
the corresponding v4 text. A1-A6 have implementation and focused evidence. A7 and A8 remain
open implementation gates; the physical-byte aggregate and default-on decision stay in Ph5. These
are normative amendments, not retrospective acceptance claims.

| Amendment | Normative correction | State on 2026-08-09 |
|---|---|---|
| A1 — physical GC | `incremental_vacuum` is stepped to completion or an explicit bounded continuation; GC reports physical bytes after the final truncate checkpoint and proves shrinkage in a contract test. | Implemented and verified in producer maintenance contracts |
| A2 — physical retention/C7 | Logical bytes select candidates, while producer-owned physical store/base/delta/scratch bytes are remeasured after GC. A persistent physical-target breach records and triggers the §12 compaction escalation; the physical ceiling remains a separate pressure guard, and Miller-owned sidecars are a separate Ph5 aggregate. | Implemented and verified; end-to-end sidecar aggregate remains Ph5 |
| A3 — capacity preflight | Store import, update, and artifact migration preflight the documented peak capacity before allocating mutation state and return the typed capacity refusal without partial mutation. | Implemented and verified in import/update/migration contracts |
| A4 — language parity | Equivalence, crash, mixed-version, and resolution comparisons run the complete 38-language fixture matrix and assert the observed language set. | Implemented and verified against the producer catalog |
| A5 — rollback safety | Invalid pointer metadata may be discarded only with a forced source reconciliation before legacy serving. A valid pointer whose store cannot open is preserved and remains not-ready. Cross-workspace refresh follows the same rule. | Implemented and verified in bootstrap/refresh tests |
| A6 — store deepening | Store level-upgrade decisions read the family-store session's committed level, not the legacy artifact. Progressive L1 stores must schedule and complete the Full upgrade. | Implemented and verified in Miller Scale coverage |
| A7 — lock/freshness contract | The coordinator enforces machine governor → store-writer → sidecar-converger acquisition order, and the freshness token includes the pinned store instance/view/generation and per-level state required by §8. | **Open.** Miller records the store identity, view, generation, manifest, level, resolution, and log stamps, but the read session creates no durable `coord.db` pin/heartbeat/expiry/release. Live Miller acquisition is `SingleWriterLock → ScanGovernor → _opsGate → sidecar lease`, not the frozen triple. |
| A8 — store sidecar convergence | Store sidecars consume `store_log` through idempotent cursors and converge incrementally. | **Open.** `EnsureStoreCurrent` rewrites a full current-view search/content sidecar whenever its stamp is stale; cursor-incremental convergence and its local reproducible cost gate remain Ph5 work. |
| A9 — store freshness cursor | Store-mode sidecar metadata, history, and freshness rechecks use `store_log.sequence`; legacy artifacts may continue to use the extraction revision. | Implemented and verified in Miller review-fix tests and store Scale coverage |
| A10 — resolution authority | A Full extraction level does not imply exact identifier resolution. Usage-dependent consumers refuse or warn while `resolution_state != exact`. | Implemented and verified in reference-consumer and store Scale coverage |
| A11 — pruned delta history | A revision delta whose baseline manifest is unavailable returns an explicit unavailable/pruned-history result; it must not claim a complete deletion set from an invented baseline. | Implemented and verified in Miller regression tests |
| A12 — store request liveness | Historical v4.2 wording: `store import` and `store resolve` use a one-hour default request window, configurable with `MILLER_STORE_REQUEST_TIMEOUT`. | Superseded by A18 below; the one-hour wording was too short for the shipped process hard cap |
| A13 — resolution consumer coverage | Full-level store reads must refuse usage-dependent results while `resolution_state != exact` across trace, context usage, inspect overview/full, impact, edit rename, and reference exports. | Implemented and verified in interactive MCP guard tests and the store read-context seam |
| A14 — vector sidecar locality | Ph3 store mode keys `vectors.db` per view, matching the shipped sidecar catalog. Family-shared vectors remain a Ph5 design target and require a new visibility/pre-filter and cost gate before default-on adoption. | Implemented as the shipped Ph3 behavior; family-shared vectors are deferred and explicitly disclosed |

## 0. v4.4 post-freeze amendment register

The final Miller review found four execution seams where the v4 contract needed to name the shipped
recovery and liveness behavior. These amendments are normative for the v1.18.0 release candidate.

| Amendment | Normative correction | State on 2026-08-10 |
|---|---|---|
| A15 — rollback cleanup durability | Before a rollback producer export begins, Miller persists a prepared marker through the primary or recovery path. After validation it records the staged artifact and digest before promotion; retry recovers a valid staged or promoted artifact, and otherwise fails closed for source reconciliation instead of repeating the producer export. | Implemented and verified in rollback recovery tests |
| A16 — family-store schema visibility | Health readers recognize producer-owned TEMP VIEW projections, including the family-store `files` view, as valid schema objects. | Implemented and verified in TEMP-view health tests |
| A17 — producer progress coverage | From-artifact store waits include coordinator, published generation databases, root spool/scratch, and generation resolution-base activity through bounded shallow directory samples; a capped sample is treated as unknown activity until the absolute hard cap. | Implemented and verified in local progress-stamp tests |
| A18 — store request liveness | `store import` and `store resolve` use the same effective timeout for the producer request and Miller's process hard cap (four hours by default, honoring `MILLER_EXTRACT_HARD_CAP`); `MILLER_STORE_REQUEST_TIMEOUT` accepts seconds or a `TimeSpan` and update/delete retain five minutes. | Implemented and verified in coordinator tests |

## 0. v4.5 post-freeze amendment register

The follow-up adversarial review found that the v4.4 wording still overstated three implementation details.
These refinements are normative for the v1.18.0 release candidate.

| Amendment | Normative correction | State on 2026-08-10 |
|---|---|---|
| A19 — prepared rollback state | The rollback marker is written before producer invocation, upgraded with a SHA-256 digest after validation, and is required before promotion. An unreconcilable matching marker returns source-rebuild-required; it never silently starts another export. | Implemented and verified in rollback recovery tests |
| A20 — bounded progress sampling | Producer-owned directory progress sampling is recursive but capped. The capped sample uses deterministic entry-count, size, and modification-time facts; nested activity is visible without a clock-based false-progress signal, and the hard cap remains the termination bound. | Implemented and verified in local progress-stamp tests |
| A21 — timeout alignment | Import/resolve request controls drive the producer request timeout and may raise Miller's hard wait cap, but never lower the configured/default process cap; the environment override applies only to import/resolve and bare numeric configuration is interpreted as seconds consistently. | Implemented and verified in wait-policy/coordinator tests |

## 0. v4.6 post-freeze amendment register

The final adversarial pass found four remaining correctness and liveness seams in the v4.5 wording.
These refinements are normative for the v1.18.0 release candidate.

| Amendment | Normative correction | State on 2026-08-10 |
|---|---|---|
| A22 — rollback view binding | A ready rollback marker carries the exported manifest generation, manifest hash, and current-view store-log sequence. Recovery may promote only a digest-validated ready artifact whose store identity still matches; a started marker never promotes its staged file. An observed producer failure clears the marker after staged cleanup so the next attempt can retry export. | Implemented and verified in rollback recovery tests |
| A23 — store freshness cost | Store freshness polling uses a bounded store-log probe instead of rebuilding the temporary compatibility projection on every tick. Exact resolution-base hashes are cached only by canonical path, byte length, modification stamp, and recorded digest; a full read session still validates and attaches the base. | Implemented and verified in freshness/read-session tests |
| A24 — process wait separation | `MILLER_STORE_REQUEST_TIMEOUT` is scoped to import/resolve producer requests. Miller's hard process cap remains the configured/default cap unless the long-operation request explicitly raises it; update/delete retain five minutes. | Implemented and verified in wait-policy/coordinator tests |
| A25 — deterministic capped progress | Producer progress samples include nested spool/scratch/base entries up to the cap and summarize capped observations deterministically. Sampling never advances from wall-clock reads, so a wedged producer can still reach the stall verdict before the hard backstop. | Implemented and verified in local progress-stamp tests |

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
    bases/               # resolution base files (§14): base-<manifest_hash>-<epoch>.db, immutable, ready-by-rename
  coord.db               # coordinator queue + leases (own WAL; never inside a generation)
  spool/                 # julie-extract spool (supervision policy carries from v3)
  scratch/               # converge scratch dbs (§14); disposable, reaped like spool
```

- A **generation** is the promotion unit for the repair tier only (§12): corruption heal,
  incompatible-epoch migration, compaction, secure-purge escalation. Routine writes never create
  a generation. The intended read contract resolves `CURRENT` once per read session and pins the
  generation it opened; the current Miller implementation keeps the generation's read-only
  connection open but does not yet register the durable coordinator pin required for retention.
  Promoted-away-generation reclamation therefore remains an A7 gate, not a shipped acceptance fact.
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

| Level | Tables (per-version rows) | v4 bytes (post-resolution-split) |
|---|---|---|
| L1 — symbol core | `symbols`, `symbol_annotations`, `relationships`, `pending_relationships`, `type_facts`, `complexity_metrics`, `parse_diagnostics`, `files`-pure columns, and the relationship-evidence subset of `reference_sites` (spanless + relationship-span rows) | ≈27.2% |
| L2 — reference layer | `identifiers` (28.7%), the identifier-walk subset of `reference_sites` (18.0%) | ≈46.7% |
| L3 — text/facts | `source_regions`, `structural_facts`, `type_argument_usages`, `type_arguments`, `literals` | ≈18.7% |
| (not a level) resolution layer | `identifier_resolutions`, `pending_resolutions` — §14 bases/deltas | ≈10.5% |

Level shares are restated for v4: the level-composition doc's historical groupings (L2 =
53.8%, "reference layer = 66.9%") counted `identifier_resolutions` (7.2%) and, in L1,
`pending_resolutions` (0.3%) — v4 moves both out of levels into the resolution layer, which
is view-scoped state, not per-version content. Freeze-era text cites only the v4 shares;
the historical figures are labeled as the old doc-grouping wherever quoted.

- `reference_sites` carries a `level` discriminator (1 or 2); the two subsets are disjoint and
  each level's stamp covers exactly its subset. `type_facts` stays L1 but receives **no
  version-qualified index budget** until a consumer exists (zero consumers in Miller `src/`
  today — level-composition §2).
- Convergence order is **L1 → L2 → L3** (traffic 14.4× favors L2; and the resolution layer —
  the +42% deferred-write share the level-composition instrument measured as the resolution
  pass — takes L2's identifier rows as its input, so L2-first also unblocks §14 convergence
  soonest). Tool degradation while a view's L2/L3 converge: `trace`/`impact` return
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
  become non-live.** Demotion = physically deleting the version's L2/L3 rows **and clearing
  `complete_l2`/`complete_l3` in the same transaction** — only `complete_l1` survives, so a
  stamp can never claim completeness for deleted rows (§3's dedup trusts stamps alone). L1
  rows and the L1 stamp remain; a branch switched back re-serves L1 instantly and re-extracts
  L2/L3 in the background — exactly the L1-first import path, §3. Measured basis: full-level retention
  breaches the 1.2× budget at any window (7d = 1.39×/1.25× on miller/julie-extractors); L1-demoted
  7d = 1.11×/1.07×, leaving 0.09–0.13× for view deltas (growth model §2.3–2.5).
- **Byte target and prune trigger are two numbers, and the target owns acceptance** (pre-merge
  review: a trigger-only contract legally stabilizes at 1.20–1.25× and fails the program's
  ≤~1.2× Ph5 criterion). The **post-GC target is ≤1.20×** and the **prune trigger is 1.25×** —
  deliberate hysteresis so GC is not re-triggered by every write — but a sweep does not stop at
  the trigger: once triggered, it prunes until the target (or runs out of non-live prunable
  versions, which is an honest dashboard state, not conformance). Both numbers share one
  denominator: **composed physical bytes** — store.db + all sidecars + resolution base files +
  deltas — over the same composed bytes of one live full-level index. Tunables
  `retention_byte_target` / `retention_byte_ceiling`. **The target is a physical-bytes contract
  with an escalation path:** after each sweep's staged vacuum, physical file bytes are re-measured; a breach
  that pruning plus vacuum cannot clear (read-aligned index pages strand — §4 accepts them
  as unreclaimable-until-rebuild) escalates, after a tunable number of consecutive breached
  sweeps, to the §12 **compaction promotion**, which rebuilds every index. Stranded-index
  bytes are a growth-model line item and the compaction's rebuild capacity is part of the
  §12 preflight — without the escalation the target is unenforceable under adversarial
  fragmentation.
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
  (same epoch, new binary) re-extracts nothing. **Same-epoch compatibility is a gated claim,
  never an assumption:** julie's CI runs the previous and current binaries over the
  multi-language fixture (after §16.1's canonical serialization lands) and requires
  byte-equivalent per-version output for an unchanged epoch; ANY difference forces an epoch
  bump plus an explicit compatible/incompatible classification (§16.8). An extractor upgrade
  that changes extraction output bumps the epoch; versions of the old epoch **keep serving** views that reference them
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

**v4.2 implementation status:** `FamilyStoreReadSession` resolves `CURRENT` once, validates the
view/manifest, builds the compatibility projection, and exposes a freshness token containing the
store/view/generation and per-level state. It does not yet create or heartbeat the §8 `coord.db`
reader-pin row. The durable pin lifecycle is deferred to the A7 Ph4/Ph5 gate.

Ph3 introduces one seam — the **read session / connection factory** — as the single way readers
obtain connections; the raw `IndexDbPath` retires from the read contract. A session:

1. Resolves (family, view) once via pointer file + registry, validated against `views`.
2. Resolves `CURRENT`, opens the generation read-only, pins `(store_instance_id, view_id,
   manifest_generation, per-level stamps, resolution generation)` — the **freshness token**. A
   token component changing is the only staleness signal; revision counters are gone. A Full
   extraction level does not certify the resolution layer: usage-dependent consumers require
   `resolution_state=exact` and must report or refuse a converging view.
   **Pins have a lifecycle, because a pin is a GC root** (§10, §12, §14): each pin is a
   `coord.db` row `(pin_id, view_id, generation, holder_pid, heartbeat_at, expires_at)` with
   a bounded lifetime; the holder heartbeats long sessions, and an expired or dead-pid pin
   drops root status at the next sweep — a reader crash can delay reclamation, never prevent
   it. Platform note: on Unix, GC may unlink files under a live expired reader (the inode
   survives its open handles); on Windows, deletion defers until handles close, so the §12
   addressable set counts a promoted-away generation until its pins are gone AND its files
   are actually deletable.
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
In store mode, that sequence is also the freshness cursor for sidecar metadata, history, and
rechecks. Legacy standalone artifacts retain their extraction-revision cursor; the two axes are
not interchangeable.

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
- **vectors.db** — **per-view in shipped Ph3 store mode** (A14): the sidecar catalog keys the
  artifact by `view_id`, so each worktree gets an independently stamped vector projection. The
  frozen family-shared design remains a Ph5 target: it needs the §7 visibility/pre-filter proof,
  a measured multi-view cost gate, and a versioned amendment before default-on adoption. The
  embedding *cache* is family-shared regardless; broker, accelerator lease,
  `MILLER_SEMANTIC=off` zero-work, and ADR-0003 ownership stand unchanged.
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
  spool/converge-scratch/temp/export partials and superseded generations, and rebuilds (or
  deletes and re-derives) any resolution base file whose manifest covers a purged version —
  base files are immutable, so purging content out of one means replacing the file. Erasure guarantees hold only where both
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
  A rollback export writes a prepared marker before producer work, records the validated staged artifact and its
  current-view manifest/log identity before promotion, and retries cleanup or staged promotion from either durable
  marker path without repeating the producer export. A started or view-advanced marker cannot promote a staged file;
  a matching marker that cannot be reconciled fails closed for source rebuild.

## 12. Generation promotion and the capacity formula

Promotions (repair tier only: corruption heal, incompatible-epoch migration, compaction,
secure-purge escalation) build `gen-<n+1>/` beside the live generation and flip `CURRENT`
atomically. The v3 lesson carries: never point a rebuild at the served files.

**Preflight formula (write-mechanics §3, corrected — a MAX over phases, not a sum):**

```
required = max over phases of (
    live generations still addressable        # incl. any pinned by readers (+926.2 MB measured for one)
  + the generation being written
  + all sidecars AND resolution base files of both
  + WAL/temp/converge-scratch live in THAT phase   # a retention sweep's WAL alone measured 56% of store size
)
```

Measured accuracy where terms coexist: −0.03% / −0.02%; the summing formula over-reserved 17.35%
on the retention-first arm because the sweep's WAL checkpoints away before the rebuild starts.
**Retention cleanup runs before capacity is judged** (measured: peak −29%, final store −40%).
Every promotion preflights; failure = `disk-blocked` with the old generation serving read-only;
promotions are resumable at chunk granularity. The preflight also **verifies creation pragmas**
(`auto_vacuum=INCREMENTAL`, FTS5 secure-delete) by read-back on every file it creates (§5).

**The `CURRENT` flip is the commit point, and it is reconciled, not assumed** (a filesystem
rename cannot share a transaction with any database row): the promotion runs as a coordinator
request (§15) whose intent row in `coord.db` — which lives outside generations and survives
the flip — records the target generation. Recovery reads `CURRENT`: if it names the new
generation, the promotion committed-in-fact and the request row is flipped without re-running;
if it still names the old one, the half-built `gen-<n+1>/` is scaffolding — resumed or reaped
per the request's state. A generation directory named by neither `CURRENT` nor any pinned
reader nor a live promotion request is always reapable.

## 13. `store_log` — the append log

One store-global, monotonically-sequenced log of durable events: version level-completions,
manifest generation flips, resolution generation flips, GC sweeps, purges, promotions. It is:

- the sidecar converge feed (§9 cursors),
- the freshness substrate (§8 tokens name the sequences they pinned),
- the coordinator's committed-effect record for idempotency (§15).

Entries are written in the same transaction as the effect they record — for effects that ARE
database writes. A filesystem effect (base rename, `CURRENT` flip) cannot share a transaction
with its log entry; those follow the two-phase publish/reconcile rules where they are defined
(§14 bases, §12 promotion). The log is pruned to the oldest unconsumed cursor minus a safety
window; cursor liveness is a GC root (§10).

**Sequence continuity across promotion:** the new generation's `store.db` opens its log at the
predecessor's last sequence + 1, beginning with a `promotion` entry naming the predecessor
generation and that last sequence. Sidecar cursors therefore survive a promotion unbroken; a
cursor pointing before the promotion entry replays from the new generation's content (the
promotion rebuilt it), which the entry makes detectable rather than silent.

## 14. Resolution bases and view deltas — state machine

**Producer: the serve-base + background-converge mechanism**
([`../findings/2026-08-07-index-store-binding-proof.md`](../findings/2026-08-07-index-store-binding-proof.md)
— G1/G2/G3a/G3c/G4/G5 passed; **the gate is RED on G3b** per the plan's any-FAIL rule, so
this section is design direction, not a frozen contract; the refuted P1a scoped pass appears
nowhere in this contract). **Gate consequence:** implementation acceptance is blocked until
the G3b decision resolves (header): user acceptance of the marginal measurement, or a passing
re-proof under a predeclared policy (diff + delta write ≤ +50% of the resolution phase in the
store-shaped pipeline, ceiling unchanged, all pairs in all runs). A failure on that re-proof
puts the mechanism itself back on the table. Storage shape per Ph0 §5: shared
base ≈ 11.5% of store bytes, per-view deltas ≈ 1.9% for seven siblings.

**Objects and identity.**

- **Base** = a complete, consistent resolution set for one manifest, keyed
  `(manifest_hash, resolver_output_epoch)`. **A base is its own database file**
  (`bases/base-<key>.db` in the generation dir) — the separate-file shape is the bulk-rate
  precondition (§16.4): built with `journal_mode=MEMORY` into a scratch path, made **ready by
  atomic rename**, immutable afterwards, deleted whole by GC (physical reclamation is file
  deletion — no vacuum needed). A `bases` row in `store.db` records the key, file, byte size,
  row count (table counts are authoritative — scan-report counters run 3–13 rows high, proof
  condition 5), and ready state. **Publication is two-phase, because a rename cannot share a
  transaction with a row** (§9's no-cross-WAL posture extends to the filesystem): the
  `building` row is written before the build, the rename lands the file, the `ready` flip
  follows. Recovery reconciles every torn state from the filesystem, which is authoritative
  for whether the effect happened: a named base file present with a non-ready row is
  integrity-checked and confirmed ready, or deleted; a ready row whose file is missing is
  reset to `building` and the base rebuilt; an unnamed file in `bases/` is deleted.
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
  **A session consults the delta only when the delta's `exact_at` equals the manifest
  generation the session pinned** (§8 token). A session pinned at the new generation while
  the delta is still exact-at an older one serves the base alone (base-consistent + honest,
  the serve-window bar) — never a mix of base rows and a stale delta's cross-file outcomes,
  which is a third consistency state the contract does not permit. A session still pinned at
  the older generation keeps its matching delta; that is why a superseded delta generation
  lives until its last pinned session closes (deleted at the next CAS publish, below).

**View resolution states.**

```
unbound → bound(base_id, delta_gen, exact_at=NULL)     # foreground bind: manifest + base pointer
        → bound(base_id, delta_gen, exact_at=G)        # delta published atomically against manifest generation G
        → (any content-changing manifest mutation)     # exact_at < current generation ⟹ view is CONVERGING again
```

**`exact` is a relation to the manifest generation, never a flag:** a view is exact iff
`exact_at = current manifest_generation`. Every content-changing manifest mutation —
`store update`, `store delete`, an import's pointer flip — leaves `exact_at` behind in the
same transaction that flips the manifest, which re-enters the converging state, re-enumerates
the gap (the new versions' rows plus any delta rows now stale), and enqueues re-convergence.
A manifest flip that provably changes no version (an identical-set generation) is the only
mutation that may carry `exact_at` forward.

- **Foreground bind is O(manifest):** write manifest rows, point at the nearest ready base
  (v1 nearest = the family base sharing the most manifest versions at the same resolver
  epoch). Measured 2.0–3.4 ms for 1,081–1,700 rows, zero identifier work (proof G5).
- **Background convergence:** fresh-output resolution pass over the view's corpus at the bulk
  rate (71–85k rows/s measured) into a scratch file → diff vs the base → delta rows. The diff
  runs as a **streaming merge-join over sorted natural keys or SQL-side** (proof condition 3:
  a naive in-memory diff is ~10 GB at dotnet/runtime scale). Publishing is one transaction:
  insert delta rows, flip `(delta_gen, exact=true)`, append the `store_log` entry.
- **CAS publish/rebase:** the converge job records the `(manifest_generation, delta_gen)` it
  computed against; the publish transaction compare-and-swaps on both and, in the same
  transaction, deletes the superseded delta generation's rows (live delta generations = the
  currently bound one plus any pinned by open read sessions; §10's cohort-delete + staged
  vacuum reclaims the pages). A manifest that moved mid-converge aborts the publish and
  **invalidates the scratch output entirely** — resolution is a whole-corpus function, so
  rows computed against the old corpus are not reusable after a content change (the same
  cross-file trap that killed base+overlay union); the job re-runs the full pass against the
  new manifest. Only a provably identical-set generation bump may keep the scratch.
- **First view of a family** (bootstrap): no base exists — the scratch output *becomes* the
  base (atomic rename), delta empty, exact immediately. Same pipeline, no diff.
- **Identical manifests share:** views with equal `(manifest_hash, epoch)` bind the same base
  with empty deltas (dedup of the resolution layer across same-commit worktrees).

**Serve-window honesty (contract posture, supersedes "binds in seconds"):** during
convergence a view serves the base's resolution for shared versions; identifiers of
non-base versions have no resolution rows yet. The gap report has **two honest states,
because exact row enumeration requires the diff and the diff completes near the end of
convergence** (G4 measured enumeration in-band with the diff, not at bind time):

1. *Converging (pre-diff):* a manifest-computable **lower bound** — the versions and files
   the base does not cover, stated as a lower bound ("at least N files' identifiers, plus
   cross-file effects").
2. *Converged enumeration (with the diff):* the exact gap — **rows and files, never "N files
   changed"** (the delta spills past changed files by the nature of the resolution graph;
   measured worst in-band 29.6% of rows / 170 files). Enumeration cost is bounded by the
   diff itself (G4).

`trace`/`impact` and `workspace status` report `resolution: converging` with whichever state
is available and label it.
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

**v4.2 implementation status:** the durable coordinator-executes-queued-requests protocol and its
global lock-order proof are contract targets, not current Miller implementation evidence. Current
Miller paths acquire `SingleWriterLock`, then machine scan admission, then the process-local
`_opsGate`, and finally the family sidecar lease. This differs from the normative
machine-governor → store-writer → sidecar-converger triple and remains open under A7.

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
  - *Committed* is an **ordered two-phase record — no cross-WAL atomicity is assumed** (§9's
    posture applies to `coord.db` too): phase 1 commits the request's **final effect together
    with a unique TERMINAL `store_log` entry carrying the `request_id`** inside `store.db`;
    phase 2 CASes the queue row to `committed`, recording the log sequence. **A chunked
    request (§5) commits many transactions before that one:** each chunk's transaction
    carries a `(request_id, chunk_index)` progress record, and **only the final chunk's
    transaction writes the terminal entry** — a progress record is never an idempotency
    anchor. The terminal entry alone means committed-in-fact: a successor finding it flips
    the queue row without re-executing; a successor finding only progress records resumes
    from the highest chunk (chunks are idempotent at the store layer: version identity is
    input-keyed; manifest flips are generation-CAS; GC stages are re-runnable). A partial
    chunk sequence can therefore never be mistaken for a committed request. The
    `idempotency_key` dedups requester retries.
  - *Result delivery:* requester polls its request row; `requester_deadline` lets the
    coordinator drop acknowledgment obligations for dead requesters (the row is kept for the
    log-pruning window).
- **Scheduling:** long operations (imports, rebases, migrations, GC) run **chunked** (§5's chunk
  = the scheduling quantum); between chunks the coordinator services queued single-file and
  repoint requests. Fairness: two classes — interactive (update/delete/repoint/open) and batch
  (import/GC/rebase/migration) — with a **bounded interactive burst** between batch chunks:
  the coordinator drains interactive requests up to a burst cap (count or wall-clock,
  tunable), then runs the next batch chunk unconditionally. Both classes get a stated
  maximum wait: interactive ≤ one chunk commit + its queue position within the burst
  (measured worst chunk commit ~seconds at default size); batch ≥ one chunk per burst
  window — a sustained interactive stream slows batch to the burst cadence but can never
  starve it. Head-of-line blocking of sibling views by one import is thereby structural and
  bounded, not best-effort.
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
     `resolution_perf.rs` sweep that sets the constant).
     **DONE — shipped as julie-extract v2.28.0 (2026-08-07), with a measured correction:**
     the save-shape A/B refuted the predicted save win (Full ≈ or slower than the widened
     delta on a 1-changed-file scope; promotion sheds only per-changed-file worklist
     overhead). Shipped: identifier denomination for multi-file deltas (−13% resolution on
     the 737-file scan shape), a single-changed-file promotion exemption (save behavior
     byte-identical to 2.27.0), and `corpus_current = whole_corpus` only. The watcher's
     16–18 s save cost therefore STANDS until row-level scoping (tier 3 below) or the
     store's background converge; evidence in
     `spike/index-store-ph1/julie-path-audit/probes/out/results3.json` / `results4.json`.
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
   the P1a oracle caveat), crash-point matrix, mixed-version/floor matrix, and **synthetic
   path-deletion and multi-delete fixtures** (binding-proof carried condition 4 — real history
   offered n=1 deletion merges) — all on a multi-language fixture (language-parity rule).
   **The determinism/exactness gates cover the FULL resolution layer:** the binding proof
   diffed `identifier_resolutions` only, while `pending_resolutions` — a partial relation
   whose rows genuinely disappear (purity audit E-3), the very reason §14 keeps tombstones —
   was never diffed. Ph2's G1/G2-equivalent gates natural-key, diff, apply, and compare both
   tables, including disappearing shared-version rows.
8. **Extractor compatibility gate** (§7): previous vs current binary over the multi-language
   fixture, byte-equivalent per-version output required for an unchanged extraction-identity
   epoch (runs after item 1's canonicalization; two separate processes, same vacuity guard);
   any difference forces an epoch bump plus a compatible/incompatible classification.

## 17. Review record — cycle-3 cross-model freeze gate (2026-08-07)

Two independent adversarial reviewers ran the same freeze-gate mandate (read-only, schema
output, full repo access) against this contract, the binding-proof findings doc, and the
committed evidence. Every accepted finding below was verified against the contract text or
the instrument before folding; grok folds landed as commit 4abfb1db, codex folds in the
commit that carries this record.

### grok (grok-4.5) — verdict as returned: needs-attention, 10 findings

| # | Sev | Finding | Disposition |
|---|---|---|---|
| 1 | critical | §14 had no post-write re-entry: `exact=true` survived store update/delete/import | ACCEPTED — folded: `exact_at` is a relation to the manifest generation, never a flag; every content-changing mutation re-enters converge |
| 2 | critical | §15 claimed same-transaction committed↔store_log across `coord.db`/`store.db`, which §9 forbids | ACCEPTED — folded: ordered two-phase record; `store_log` terminal entry is the idempotency anchor |
| 3 | high | Mid-converge scratch reuse was cross-file-unsafe | ACCEPTED — folded: manifest movement invalidates the scratch output entirely |
| 4 | high | §3's L2 53.8% byte share still counted `identifier_resolutions`, which v4 moves out of levels | ACCEPTED — folded: §3 restated for the v4 shape; historical figures labeled |
| 5 | high | G3b MARGINAL/GO softens a fixed binary criterion after measurement | ACCEPTED — superseded by codex C1's stronger form: the verdict itself was corrected to gate-RED (below) |
| 6 | high | §17 falsely closed cycle-2 #2 and #4 | ACCEPTED — this record replaces the placeholder; cycle-2 #2/#4 close only via the folds listed here |
| 7 | medium | Superseded delta generations had no reclamation rule | ACCEPTED — folded: the CAS publish transaction deletes the superseded delta generation |
| 8 | medium | Carried condition 4 (synthetic deletion fixtures) never landed in the contract | ACCEPTED — folded: §16.7 |
| 9 | medium | §1 layout omitted `bases/`; generation inventory incomplete | ACCEPTED — folded: §1 `bases/` + `scratch/`, §12 formula, §10 purge |
| 10 | low | Gate §3 "66.9% reference layer" vs contract L2 53.8% easy to misread | ACCEPTED — resolved by the §3 restatement |

### codex (cycle-3 re-attack, xhigh) — verdict as returned: needs-attention, freeze REFUTED, 11 findings

| # | Sev | Finding | Disposition |
|---|---|---|---|
| C1 | critical | The fixed G3b red gate was being waived, not discharged | ACCEPTED — the plan's own rule ("any FAIL → the gate is red… NO-GO and the contract freeze blocks") governs: one of three runs measured 0.5069 > 0.50 with no predeclared aggregation policy. The findings doc's GO verdict is RETRACTED; the gate is recorded RED on G3b; this contract stays DRAFT until the user accepts the marginal measurement or a predeclared re-proof passes (header). Where grok proposed freeze-with-hard-carry and codex proposed no-freeze, the plan's fixed rule decides for codex |
| C2 | critical | A partial chunk could be mistaken for a fully committed request | ACCEPTED — folded (§15): per-chunk progress records; a unique TERMINAL `store_log` entry written only with the final chunk's transaction is the sole committed-in-fact signal |
| C3 | high | Manifest mutation re-entered convergence without retiring the stale delta from reads | ACCEPTED — folded (§14): a session consults the delta only when its `exact_at` equals the session's pinned manifest generation; otherwise base-only + honesty |
| C4 | high | G2 exactness never covered `pending_resolutions` (the instrument diffed `identifier_resolutions` only) | ACCEPTED — verified in `bind.py` (`SCHEMA_EVIDENCE_TABLES` has no `pending_resolutions`); findings doc G1/G2 claims re-scoped; Ph2 gates extended (§16.7) |
| C5 | high | The enumerated serve-window gap is unavailable until the diff runs, near the end of convergence | ACCEPTED — folded (§14): two-state gap honesty — manifest-computable lower bound pre-diff, exact rows/files post-diff; G4 is not cited as foreground enumeration |
| C6 | high | L1 demotion left `complete_l2`/`complete_l3` stamps on deleted rows | ACCEPTED — folded (§6): demotion clears both stamps in the deleting transaction |
| C7 | high | The byte ceiling had no enforceable reclamation path for read-aligned index fragmentation | ACCEPTED — folded (§6): post-vacuum physical re-measure; persistent breach escalates to the §12 compaction promotion; stranded bytes + rebuild capacity in the growth/preflight model |
| C8 | high | `store_log` cannot atomically record filesystem promotions or base renames | ACCEPTED — folded (§14 two-phase base publication with per-crash-point reconciliation; §12 `CURRENT` as reconciled commit point anchored in `coord.db`; §13 log-sequence continuity across promotion) |
| C9 | high | Same-epoch extractor compatibility was asserted, not gated | ACCEPTED — folded (§7 gated-claim language; §16.8 previous-vs-current binary CI gate) |
| C10 | medium | Pinned readers were GC roots without a pin lifecycle | ACCEPTED — folded (§8): pin rows with heartbeat/expiry, dead-pid reclamation, Unix/Windows deletion semantics |
| C11 | medium | Interactive-first draining did not bound batch starvation | ACCEPTED — folded (§15): bounded interactive burst; stated maximum wait for both classes |

### Held-open doubt items

Cycle-1 #2 (bootstrap cost) closes for new views of an indexed family via the §14 measured
bind; #9 (GC physical reclamation) closes via §10 + the C7 escalation fold; #11 (fingerprint
compatibility) closes via §7 + the C9 gate fold. Cycle-2 #2 (state machine) and #4 (durable
queue) close via the grok-1/2 + C2/C3 folds; #7 (commit granularity) closes via §5 + the C2
terminal-record fold. **The freeze itself remains blocked on C1's G3b decision — that is the
one historical open item from cycle 3, and it is the user's. The v4.2 implementation gates below
are separate current-state blockers for the corresponding Ph4/Ph5 acceptance claims.**

### v4.2 implementation recheck — 2026-08-09

The post-freeze Miller wiring recheck found two execution deviations that must not be hidden by
the v4.1 register:

| # | Sev | Finding | Disposition |
|---|---|---|---|
| A7.1 | high | Family-store reads carry a rich in-memory freshness token but do not create the durable `coord.db` reader pin, heartbeat, expiry, or release protocol required by §8 and §10. | OPEN — defer durable reader lifecycle and retention proof to the Ph4/Ph5 gate; the contract remains explicit about the missing behavior. |
| A7.2 | high | Miller's live acquisition order is `SingleWriterLock → ScanGovernor → _opsGate → sidecar lease`; it is not the frozen coordinator triple, and no Miller-side coordinator lease proof closes that gap. | OPEN — implement and test the coordinator order before claiming A7. |
| A8.1 | medium | Store search/content convergence materializes the current view and rewrites a full sidecar when the stamp is stale; it has no store-log cursor path. | OPEN — keep store mode opt-in and make cursor-incremental convergence plus a local reproducible cost gate a Ph5 entry criterion. |

---

### v4.2 review-fix recheck — 2026-08-09

The post-freeze Miller implementation recheck also found four concrete consumer-side deviations. They
were fixed before the release candidate was reconsidered:

| # | Sev | Finding | Disposition |
|---|---|---|---|
| A9.1 | high | Store history and sidecar freshness mixed the extraction revision with the store-log sequence. | ACCEPTED — folded: store-mode capture, recheck, and search-sidecar metadata use `store_log.sequence`; legacy artifacts retain the extraction revision. |
| A9.2 | high | A store view could report Full extraction while usage-dependent reference candidates read an inexact resolution layer. | ACCEPTED — folded: candidate export refuses a non-exact resolution view and reference export labels it as converging. |
| A9.3 | medium | A pruned revision baseline could return a complete delta with deleted paths omitted. | ACCEPTED — folded: missing historical manifests return `Unavailable(pruned_history)` for nonzero spans. |
| A9.4 | low | The fixed coordinator request window was too short for large import/resolve operations. | ACCEPTED — superseded by A18: import/resolve follow the four-hour default hard cap with `MILLER_STORE_REQUEST_TIMEOUT` as the explicit override; update/delete retain five minutes. |

These fixes do not close A7 or A8. In particular, the full-view sidecar rewrite remains an explicit
Ph5 implementation and local-cost gate, and the durable reader pin/lock-order work remains a Ph4/Ph5
entry condition. No GitHub Actions wall-clock threshold is an acceptance criterion.

### v4.4 implementation recheck — 2026-08-10

The final Miller review found four additional execution deviations; each is now recorded above and
implemented in the release candidate:

| # | Sev | Finding | Disposition |
|---|---|---|---|
| A15 | high | A rollback marker write failure could leave a promoted legacy artifact without durable pointer-cleanup state. | ACCEPTED — folded: primary/recovery marker paths and a no-repeat-export retry regression |
| A16 | medium | Health table detection ignored family-store TEMP VIEWs and reported the `files` section unavailable. | ACCEPTED — folded: shared schema-object detection includes temporary tables and views |
| A17 | medium | From-artifact progress sampling omitted producer spool, scratch, and resolution-base work. | ACCEPTED — folded: store progress includes those producer-owned paths and their file/directory activity |
| A18 | medium | The one-hour import/resolve request window could expire before Miller's four-hour process hard cap. | ACCEPTED — folded: default request liveness follows the process hard cap; explicit store timeout remains available |

### v4.5 implementation recheck — 2026-08-10

The follow-up Claude and Grok adversarial review found three refinements in the v4.4 implementation. They were
fixed before the release candidate was reconsidered:

| # | Sev | Finding | Disposition |
|---|---|---|---|
| A19 | high | Rollback state was recorded after promotion, leaving crash and marker-failure paths able to repeat the producer export. | ACCEPTED — folded: prepared/ready markers, staged-artifact digest, recovery, and fail-closed source reconciliation |
| A20 | medium | Progress sampling recursively walked producer-owned trees on every poll. | ACCEPTED — folded: bounded shallow samples with an explicit unknown-activity stamp when capped |
| A21 | medium | The producer request timeout and Miller hard cap could disagree, and bare numeric timeout text parsed differently across paths. | ACCEPTED — folded: request-driven process cap and shared seconds-first duration parsing |

## Cross-reference: gate price list → contract sections

| Gate amendment | Resolved in |
|---|---|
| 1. metadata_json determinism | §2 (requirement + CI gate), §16.1 |
| 2. Trigram `rank` → `collapsed_len` (+ content.db gap) | §9 (ships early, own gate, content audit included) |
| 3. Binding mechanism NO-GO | §14 (replacement mechanism measured; gate RED on G3b — freeze blocked pending the header's decision, §17) |
| 4. Retention as the central contract | §6 (defaults + tunables + latency coupling) |
| 5. Durability contract | §5 (per-chunk + marker + FULL) |
| 6. Index-direction reconciliation | §4 (`gc-aligned` / `read-aligned` classification per index) |
| 7. Promotion preflight | §12 (max-over-phases + sweep-WAL term + pragma read-back) |
