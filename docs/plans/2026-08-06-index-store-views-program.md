# Versioned Index Store + Views — Program Plan

> **For agentic workers:** this is a two-repo program plan, not a single-session execution plan.
> Each phase below becomes its own razorback implementation plan (razorback:writing-plans →
> razorback:subagent-driven-development) in its owning repo when picked up. Do not begin
> implementation from this document without explicit user approval.

**Goal:** replace copy-per-worktree indexing with **one content-addressed index store per repo
family plus a small view per checkout**. A new worktree or a branch switch becomes a manifest
repoint plus a delta, not a rescan or a copy. Disk for N worktrees ≈ one index + per-view diffs +
retention overhead. Time for a new worktree of an indexed family ≈ seconds.

**Naming:** no codename (user decision 2026-08-06) — plain descriptive naming throughout: the
**versioned index store** (one store per repo family, a view per checkout).

**Status:** design converged in the 2026-08-06 brainstorm (all six sections user-approved in
discussion); amended same day after Doubt Pass cycles 1–2 (codex adversarial review — see the
doubt-pass records at the end; the storage model survived, several v1 scope decisions and
contract claims did not survive unamended; the third cycle is reserved as the Ph1
contract-freeze re-attack). **User-approved 2026-08-06**, with two same-day decisions: the codename is dropped
(plain versioned-index-store naming) and the progressive levels program is **folded into this
program** (see the levels section below). Implementation has not started; each phase still
requires its own approval to begin. The
deferred v1.17.0 FTS5 sidecar-copy follow-up is **cancelled** in favor of this program — copy
choreography retires wholesale when the store ships.

**Provenance:** user-driven storage rethink after v1.17.0. The multi-worktree program line
(fleet-safety → rebind) was driven by the 2026-08-01 field report on multi-agent, multi-worktree
workflows; this program is the structural answer to the same feedback.

## Why now (measured 2026-08-06)

- **~19 GB of `.miller` indexes across main checkouts** on the primary dev machine before any
  worktree copy exists (openclaw alone 4.7 GB). The work machine has a 512 GB SSD and ~30 active
  projects.
- Miller itself: **22.6 MB tracked source → ~1.1 GB index (~48×)** — symbols.db 771 MB,
  search.db 201 MB, content.db 105 MB, vectors.db 14 MB.
- The dotnet/runtime benchmark artifact is **21.9 GB — per worktree** under the copy model.
- The shipped rebind program
  ([`2026-08-02-worktree-delta-rebind-program.md`](2026-08-02-worktree-delta-rebind-program.md),
  P4 validated 2026-08-06) fixed worktree **time, not bytes**: its copy protocol is a page-stepped
  online backup — a full physical copy per worktree — and sidecar copies would only add more.
- Byte split ([levels program](2026-08-03-progressive-indexing-levels-program.md) data): the
  reference layer is 74% of artifact bytes serving 7% of tool calls. That is the *diet's* problem;
  this program attacks **multiplication and churn**, which the diet cannot touch. The two compound.

## The model

Git already solved this problem for itself: worktrees never copy the object database — they share
one `.git`, and each worktree is a ref plus a checkout. This program applies the same move to the
index:

- **Store** — content-addressed, version-keyed index data, one per repo family
  (family = git common-dir lineage, resolved via the registry's existing lineage columns from the
  rebind program; a non-git workspace is a family of one).
- **View** — a checkout's manifest (path → version) plus a **shared-base + delta** resolution
  binding (below).

Dedup applies across **worktrees** (unchanged files share rows, IDs included) and across **time**
(a version once extracted is never re-extracted while retained — branch switches, stash pops, and
rebases repoint the manifest instead of rescanning).

### Relationship to the base+overlay refutation

[`2026-08-05-rebind-p1-cost-model.md`](../findings/2026-08-05-rebind-p1-cost-model.md) §5 refuted
base+overlay: `stable_location_id` folds the byte span, so one edit re-IDs symbols below it and a
naive base∪overlay **union** silently loses references. That refutation kills artifact-union. It
does **not** kill content-addressed versioning, because the two models differ exactly where the
union failed:

- Per-file extraction output — symbols, identifiers, reference_sites, source_regions,
  structural_facts, and every other per-file table — becomes a **pure function of (relative path,
  file content, extractor fingerprint) by construction in the v4 schema**. This is a schema
  requirement, not a property of today's artifact: the resolution pass currently denormalizes
  `identifier_resolutions.target_symbol_id` back into `identifiers.target_symbol_id` (verified
  2026-08-06 on the live artifact: 156,953 of 380,720 identifier rows carry the write-back;
  `SqliteSymbolGraphIndex` reads it via `COALESCE(i.target_symbol_id, ir.target_symbol_id)`).
  v4 strips resolution state out of shared rows entirely; it lives only in resolution bases and
  view deltas. Miller's readers change accordingly.
- The resolution layer is **never unioned across differing content**: a view binds to a
  **resolution base** (a complete, consistent resolution set keyed by manifest hash) plus a
  **view delta** produced by P1a delta-scoped invalidation — explicit replacements and tombstones
  with defined precedence. The manifest selects exactly one version per path, and the delta has
  deletion semantics, so the reproduced union failure (a stale base row surviving beside re-ID'd
  symbols) cannot occur.

### Resolution sharing is v1-required, not an optimization

Two independent arguments force this (doubt-pass findings 1–2, both verified):

- **Arithmetic.** The resolution layer is ~12% of store bytes. If every view carried a full
  private copy, 8 views cost `0.88 + 8×0.12 ≈ 1.84×` a single index — failing the ≤1.2× success
  criterion before retention is counted.
- **Bootstrap.** P1a is an incremental mechanism over existing resolution state; with no prior
  state it falls back to a full whole-workspace pass (12.86M identifiers at dotnet/runtime
  scale). A new view with an empty private overlay would pay exactly the cost this program
  exists to eliminate.

So v1 semantics are: a new view binds to the nearest existing base (typically the sibling's),
converges a delta from the manifest diff, and a background job may **rebase** a long-diverged
view into a fresh base. Views with identical manifest hashes share one base and an empty delta.

Two bounds on this, from doubt-pass cycle 2 (both verified in julie's resolver):

- **Base and delta identity includes the resolver epoch**, not manifest hash alone: julie stamps
  `RESOLUTION_VERSION` (currently 6) and bumps it whenever observable resolver output changes. A
  resolver upgrade with an unchanged manifest must not reuse a ready base built under old
  semantics.
- **The delta path is bounded by divergence.** P1a promotes delta resolution to a full pass at a
  measured crossover (`DELTA_SCOPE_CROSSOVER = 0.7` today) — correctly, because past that point
  the full pass is cheaper. The bootstrap guarantee is therefore "base + delta up to the
  crossover; an honest full **rebase** beyond it," and the time SLO is scoped to typical
  task-branch divergence, not promised unconditionally.

### What carries forward, what retires

Carries forward as foundations: fleet-safety (governor, spool supervision, failure journal,
root-presence monitor, `GitWorktreeLayout`), the registry lineage columns, P1a delta-scoped
resolution (hard prerequisite — confirm landed status in julie-extractors during Ph0), and the
rebind equivalence-gate testing pattern.

Retires **for routine writes** (store mode): the per-worktree copy choreography, rebind's local
page-stepped copy (its role passes to `store export`), and the per-poll reader-reopen freshness
gymnastics. **Store-generation promotion is retained** for the repair tier: corruption heal,
incompatible schema migration, compaction, and secure purge still build a new store generation
and atomically promote it (doubt-pass finding 7 — append-only rows do not fix pager corruption,
and the promote machinery is the right tool where the artifact cannot be trusted).

### Progressive levels: folded in (user decision, 2026-08-06)

The [progressive indexing levels program](2026-08-03-progressive-indexing-levels-program.md) is
**approved and folded into this program** rather than sequenced beside it, so the v4 contract is
designed level-aware once instead of retrofitted:

- A version's per-file rows group by **table-set level** (L1 symbol core; L2 reference layer;
  L3 regions/facts), and the completion marker generalizes to **per-level completeness stamps**
  on `file_versions`. "Complete" for dedup means complete *at a level*.
- `store import` runs **L1-first**: L1 rows land and serve (search/inspect/context — ~86% of
  calls) while L2/L3 extraction converges per version in the background — the same
  serve-while-converging machinery specified for extractor fingerprints, applied per level.
- Tool degradation follows the levels plan: `trace`/`impact` return "reference layer converging"
  with progress; per-level state surfaces in status/health/dashboard.
- The levels doc remains the design source for level composition and the julie-extract flag
  shape; its open P0 questions merge into this program's Ph0/Ph1 gates.

The fold is what makes the two byte problems compose in one contract: levels shrinks what every
version costs (the 74% reference layer becomes deferred background work), and the store dedups
whatever is stored, across worktrees and time.

## Design

### 1. Store and schema shape

One directory per family: `~/.miller/stores/<family-id>/`, containing `store.db` (julie-extract
writes) and the re-keyed `search.db`, `vectors.db`, `content.db` (Miller writes).

Inside `store.db`:

- **`file_versions`** — one row per unique (relative path, blake3 content hash, extractor
  fingerprint), carrying a **completion marker**: a version becomes dedup-visible only when its
  last child row is durable, in the same transaction. Dedup checks trust only complete versions
  (doubt-pass finding 7: without the marker, a crash between the version row and its child rows
  makes the next import "find" and permanently serve an incomplete version). **Transaction
  granularity is an explicit Ph0/Ph1 item** (cycle-2 finding 7): julie's bulk writer currently
  commits a whole snapshot in one transaction, which cannot leave reusable complete versions
  behind a crash and holds locks for the whole import; the v4 contract must define per-version
  or per-chunk commit units and prove WAL peak, crash-reuse, and throughput at scale.
- **Per-file tables re-keyed with composite identity.** All per-file tables point at a
  `version_id`, and **every retained ID is version-qualified**: `stable_location_id` hashes path
  + name + span, not content, so two versions of a file can legally collide on `symbol_id`
  (same-length in-place edit). Primary keys, foreign keys, sidecar keys, GC keys, and delta
  targets are composites such as `(version_id, symbol_id)`. The Ph0 audit covers the **complete
  table inventory** — relationships, annotations, literals, type facts/arguments, complexity,
  diagnostics, capability metadata, revision tables — not only the five headline tables
  (doubt-pass finding 4). Rows for a complete version are immutable.
- **`views` + `view_manifest`** — a view is a checkout: root, workspace link, and a manifest of
  (path → version_id). Repoints are transactional: write the new manifest generation, flip one
  pointer. Manifest entries record the extractor fingerprint they were satisfied at.
- **Resolution bases + view deltas** — as specified above. Base sets are keyed by (manifest
  hash, resolver epoch) with an atomic ready pointer; a manifest flip never exposes a partially
  built base or delta (generation-keyed, doubt-pass finding 7). Ph1 owes the full **state
  machine**, not adjectives: the resolution key, cumulative-delta vs chain semantics, precedence
  ordering, tombstone scope, compare-and-swap rebase against (manifest generation, delta head)
  with abort/retry on concurrent change, and GC roots covering bases, deltas, pinned readers,
  and in-progress rebases (cycle-2 finding 2).
- **Store-format epoch + writer/reader floors** — the store records its format epoch, the
  minimum reader version, and the minimum writer version. A process below the floor degrades
  honestly (read-only or not-ready with the reason); a newer process migrating the store bumps
  the epoch via store-generation promotion, never in place. The existing monotonic
  "binary_version never goes backwards" invariant carries into store scope (doubt-pass
  finding 11).
- **Extractor fingerprint semantics — serve-while-converging, never outage.** After an extractor
  upgrade, old-fingerprint rows **keep serving** views that reference them while new-fingerprint
  extraction converges in the background (status surfaces the mix); a view flips per-file as new
  versions land. Fingerprint mismatch means "re-extract owed," not "absent" — a family-wide cold
  outage on upgrade day is explicitly rejected (doubt-pass finding 11). **The compatibility
  contract splits into two epochs** (cycle-2 finding 6): an *extraction-identity* epoch and a
  *resolver-output* epoch. Compatible extraction changes may flip per file; an incompatible
  change in either epoch must build a shadow manifest plus one coherent resolution generation
  and flip atomically while the old view keeps serving — blanket per-file mixing across
  incompatible epochs is not a legal state.

**Store location rationale:** in-checkout storage was rejected because the store must survive
checkout churn — deleting the main checkout, and `git clean -xdf` (which deletes ignored files;
common in .NET "clean build" workflows) would otherwise vaporize the whole family's index in one
command. With the home-dir store, `git clean -xdf` costs a pointer file and a re-hash.

### 2. Write path

julie-extract grows a small store verb set (the v4 contract; Miller its only caller):

- `store import` — scan a root against the store: hash files, skip every **complete** version
  already present, extract only the missing ones, append their rows, then write the view's new
  manifest generation and flip the pointer.
- `store update` / `store delete` — single-file verbs: update appends a version and repoints one
  manifest row; delete removes one manifest row. Neither mutates version rows in place.
- Resolution converges after import: bind or rebase the view's base, then apply P1a delta
  invalidation for the manifest diff.

**Family concurrency contract (replaces "the lease generalizes").** Doubt-pass finding 10,
verified against the current model: today's writer lock is a *lifetime* leadership lease and
equal extractor versions deliberately never yield — carried unchanged to family scope, one
worktree process would own the family forever and same-version siblings could never write. The
family contract is therefore **coordinator-executes-queued-requests** — and the queue is a
**durable execution protocol**, not the existing best-effort converge queue (cycle-2 finding 4,
verified: today's queue claims by file-rename and *deletes* expired claims from a crashed
leader — acceptable for freshness nudges, not for import/repoint/GC). The protocol carries
durable request IDs, idempotency keys, claimed/committed/acknowledged states, stale-claim
recovery by a successor coordinator, result/error delivery, and requester timeouts. Long
operations (imports, rebases, migrations, GC) run chunked under a scheduler so they cannot
head-of-line block sibling views. One
mandatory global lock order — machine governor → store-writer lease → sidecar-converger lease —
with no waiting on a lower lock while holding a higher one; the starvation/deadlock analysis is
a Ph1 contract deliverable with tests. The machine governor remains admission control only.

Event costs under the model:

| Event | Cost |
|---|---|
| New worktree | hash tree; near-total dedup; extract divergence only; bind sibling resolution base + delta |
| File save | append one version + one manifest row; delta-invalidate the view's resolution |
| Branch switch | manifest repoint for changed paths; retained versions cost zero extraction |
| Extractor upgrade | serve-while-converging per file; no fleet rescan storm, no outage |

**Crash semantics:** an interrupted import leaves incomplete versions (never dedup-visible,
swept by GC) and unflipped generations. A view changes only at the single manifest-pointer
transaction; resolution bases/deltas flip by ready pointer. The persisted scan-failure journal
carries forward keyed per view.

### 3. Read path

- **A workspace resolves to (family, view) once — behind a new read-session seam.** Doubt-pass
  finding 6, verified: `WorkspaceReadContext` exposes a raw `IndexDbPath` and many readers open
  their own connections from it. That seam is insufficient here: readers need the family, view,
  pinned generations, attached files, and visibility state together. Ph3 introduces a
  **view-aware read session / connection factory** as the single way readers obtain connections;
  the raw path retires from the read contract. Tool cores above the reader interfaces still do
  not change.
- **Visibility is applied inside retrieval, before every limit.** Doubt-pass finding 5, verified
  in `FtsSymbolSearchIndex`: the trigram arm admits 200 FTS-ranked candidates, the semantic arm
  500, content search applies `ORDER BY rank LIMIT` — a global recall pass could fill those
  windows with versions invisible to the view and starve real hits. Every FTS/vector/content
  query therefore joins visibility **before** ranking windows and limits.
- **Ranking statistics are view-local.** The C# BM25 arm consumes corpus statistics
  (`_documentCount`, `_avgdl`, per-query df) and deterministic per-view `DocId` ordinals. The
  shared sidecar stores per-version rows; the read session supplies per-view statistics and
  derives per-view `DocId` ordering (the contiguous `ORDER BY path, start_line, symbol_id`
  contract), so lexical output stays **byte-identical to a dedicated per-view index** — that
  remains the acceptance bar, achieved with view-local stats rather than assumed. One conflict
  must be resolved first (cycle-2 finding 5, verified): today there are **two DocId histories** —
  a fresh index assigns contiguous ordinals by `ROW_NUMBER() OVER (ORDER BY path, start_line,
  symbol_id)`, while incremental converge deliberately preserves old DocIds — and BM25 metadata
  recomputes corpus totals with full-table scans. Ph0 chooses the canonical per-view history and
  the projection-maintenance design, and measures their cost and eight-view bytes explicitly.
- **Vectors fall back per-view if the engine cannot pre-filter.** If sqlite-vec KNN cannot apply
  visibility before top-K with acceptable cost, `vectors.db` stays per-view (it is the smallest
  sidecar; the embedding *cache* stays family-shared either way, so the embedding-compute win
  survives). Ph0 measures; the design does not assume.
- **Snapshot reads:** a reader pins the manifest generation and per-layer stamps it opened.
  Immutable version rows + WAL (N readers, 1 writer) make a pinned generation a consistent
  snapshot. Reader transactions are **bounded** (open-read-close per query, as FTS readers do
  today) so pinned generations cannot hold the WAL open indefinitely; checkpoint policy and
  WAL-size telemetry are part of the store contract (doubt-pass finding 8).
- **Freshness token = (store instance id, view id, manifest generation, per-layer stamps).** Not
  a bare counter: store-generation promotion (repair tier) replaces the instance id, and each
  sidecar carries its own completeness stamp the reader validates, degrading honestly when a
  layer lags (search fresh / vectors converging, etc.).
- **Degradation stays truthful:** while a view's resolution delta converges, `trace`/`impact`
  return "reference layer converging" (the levels-program posture); search/inspect/context serve
  immediately from shared version rows.

### 4. Sidecars, re-keyed

**Rule: converge once per version, ever.** `store.db`'s append log (versions added, manifests
flipped) replaces `revision_file_changes` as the converge feed. Each sidecar consumes the feed
through an **idempotent cursor** and publishes a completeness stamp; a crash between store commit
and sidecar commit replays cleanly (no cross-file atomicity exists to rely on — doubt-pass
finding 8).

- **search.db** — FTS rows keyed by `(version_id, …)`. The word arm + collapsed-trigram arm
  design carries forward; recall-only stays true; visibility and per-view statistics per §3.
- **vectors.db** — embeddings keyed by version; family-shared if KNN pre-filtering proves out in
  Ph0, per-view otherwise (embedding cache family-shared regardless). The semantic-sidecar
  broker, accelerator lease, `MILLER_SEMANTIC=off` zero-work guarantee, and ADR-0003 ownership
  all stand unchanged.
- **content.db** — tree-derived text keys by version. Explicit external/web imports scope to the
  family (an import made from one worktree is searchable from siblings — an upgrade over today's
  per-workspace silos).

**Writer roles stay parallel by file split:** julie-extract holds the store-writer lease for
`store.db`; a Miller-side sidecar-converger lease (one per family) owns the sidecar files, under
the global lock order from §2. Separate files → separate write locks and WALs → no contention.
(SQLite `ATTACH` makes multi-file reads equivalent to single-file for this workload; accepted
caveats: no cross-file foreign keys — version keys are logical joins — and no atomic commit
across WAL files, which the cursor-and-stamp posture above is designed around.)

**What remains in `<workspace>/.miller/`:** ephemera and genuinely view-local state only — a
pointer file (store + view id), logs, scan progress, spool, and `history.db` metric trends
(branch history differs per checkout).

### 5. Lifecycle: retention, GC, migration, escape hatches

- **GC = refcount + retention window, and it must reclaim physical bytes.** Doubt-pass
  finding 9: with SQLite's default `auto_vacuum=NONE` (the live artifact's setting), DELETE only
  freelists pages — the file never shrinks — and full `VACUUM` needs up to 2× the db size
  (a capacity event at 21.9 GB). The store therefore chooses **`auto_vacuum=INCREMENTAL` at
  creation** (it cannot be enabled later without a rewrite), runs staged
  `PRAGMA incremental_vacuum` after sweeps, and schedules **bounded** FTS5 segment merges on the
  sidecars via the page-limited `merge` command (`optimize` is an unbounded whole-index merge,
  reserved for maintenance windows — cycle-2 finding 8). The dashboard's "reclaims measured bytes" claim is conditioned
  on this machinery and validated in Ph5.
- **Immediate purge path, with real erasure guarantees.** DELETE + checkpoint + merge do **not**
  physically erase content (cycle-2 finding 8): core `PRAGMA secure_delete` is off by default
  and does not scrub FTS shadow tables, which have their own persistent FTS5 `secure-delete`
  option. The store and sidecars therefore enable both **from creation**. A purge request (CLI +
  dashboard) deletes the named versions across store and sidecars, truncates WALs, runs the
  page-limited merges, and cleans spool/temp/export files and superseded generations. Where the
  guarantees were not active for the data's lifetime (e.g., content migrated from a v3
  artifact), purge escalates honestly to a generation rebuild via repair-tier promotion.
- **Dashboard family-store panel:** per-family size, live-vs-reclaimable split, per-view stats,
  retention setting, prune + purge buttons — antiforgery POST per ADR-0002. No new MCP tools.
- **View lifecycle rides existing machinery with softer failure modes.** The root-presence
  monitor drops a vanished worktree's view and releases its manifests. Path reuse degrades from
  "different tree served under the old index" to a manifest reconcile. **One source of truth for
  view identity:** the store's `views` table is authoritative; the registry row and the
  per-checkout pointer file are caches reconciled idempotently on open (doubt-pass finding 12 —
  three persistence domains need a nominated owner and crash reconciliation).
- **Migration is a full transformation with a capacity preflight.** `store import
  --from-artifact` ingests an existing v3 `symbols.db` — but that means splitting the
  denormalized resolution column, generating composite version-qualified identities, and
  rebuilding sidecars, not metadata ingestion. Peak disk during migration includes the old
  artifact + new store + WAL + sidecars; on a 512 GB machine the migration runs one family at a
  time under the governor with a disk preflight (the vectors-v1 `disk-blocked` posture), and old
  `.miller` db files are marked reclaimable and surfaced in the dashboard, not silently deleted.
- **One promotion-capacity formula for every generation promotion** (cycle-2 finding 9) — epoch
  upgrades, corruption repair, compaction, and secure purge all build replacement generations,
  not only `--from-artifact`. Required space = old generation + new generation + sidecars +
  WAL/temp + generations retained for pinned readers. Every promotion preflights it; failing is
  `disk-blocked` with the old generation serving read-only; promotions are resumable, and
  retention cleanup runs before capacity is judged.
- **Escape hatches:** a store on/off switch at ship time (`MILLER_SEMANTIC` precedent — honest
  degradation to per-workspace mode). **Rollback honesty:** once views have advanced in store
  mode, pre-existing per-workspace artifacts are stale — switching off triggers a current-view
  `store export` per active workspace (or an honest not-ready until one completes), never
  silently serving the stale artifact. Whether the first release defaults on or opt-in is
  decided at validation time. `store export --view <id>` materializes a single-file artifact —
  the second adapter on the store seam, keeping the copyable-artifact story for CI/Eros and
  doubling as the rollback path. Rebind's local copy protocol retires; the export verb inherits
  its role.

## Architecture quality (gate summary)

- **Affected modules:** julie-extract writer + resolver + CLI (v4 store contract); Miller.Indexing
  bootstrap/leadership/freshness **and every SQLite reader** (via the read-session seam);
  sidecars re-keyed; registry lineage; dashboard. Tool cores and MCP/CLI surfaces unchanged.
- **Caller-facing interface:** agents keep the same nine tools and selectors. Internal seam
  change: the raw `IndexDbPath` read contract is replaced by a view-aware read session. New
  external surfaces: the julie-extract store contract (Miller sole caller), dashboard
  prune/purge endpoints.
- **Depth:** a deep-module move — dedup, retention, and family sharing hide behind interfaces
  callers already use.
- **Test surface:** the equivalence gate; Miller's existing contract suite passing unchanged;
  pure-logic policy tests in Miller.Core; Scale tests for family bootstrap/convergence/GC;
  crash/restart and mixed-version matrices (per the doubt pass).
- **Seams:** the store contract replaces the v3 artifact contract as the inter-repo seam;
  `store export` is the second adapter proving it. No conflict with ADR-0001/0002/0003; an ADR
  for the store decision is owed once approved.
- **Rejected shortcuts:** base+overlay union (refuted); single shared db file (write-lock
  serialization); clonefile CoW (FS-specific, decays, dies on rebuild); not indexing worktrees;
  Miller-side store writer (re-earns Rust bulk-writer tuning for nothing); per-view private
  resolution copies (fails the disk arithmetic and the bootstrap cost — see resolution sharing).
- **Risk: high** — new persistent format, two-repo contract, family-level concurrency, GC
  correctness. Doubt Pass cycles 1–2 run 2026-08-06 (codex): 13 + 9 findings, all accepted,
  dispositions recorded below; the third cycle is reserved as the Ph1 contract-freeze re-attack.

## Constraints

- **Ownership is performance-driven, not law** (user, 2026-08-06, retiring the prior hard
  ownership framing): julie-extract writes `store.db` because the tuned bulk writer lives there
  (artifact write measured at ~90% of small-repo cold start; savepoint/memory-journal bulk path
  proven at 21.9 GB). Miller writes the derived sidecars. When this program is picked up,
  CLAUDE.md's ownership language is amended accordingly (and AGENTS.md regenerated).
- Multiple files, one shared version key, `ATTACH` for reads (write-parallelism rationale above).
- **FS-agnostic:** no clonefile/reflink dependence — Windows and Linux get identical wins.
- No new MCP tools (stinginess rule); new state surfaces through `workspace status`/`health`
  JSON, CLI verbs, and the dashboard.
- Language parity: store schema and verbs are language-uniform table-set operations; the
  equivalence gate runs on a multi-language fixture.
- `Miller.Core` stays I/O-free; fast suite stays fast; julie-spawning tests are Scale-tagged.
- Lexical-only search output stays byte-identical (achieved via view-local statistics — see §3).
- No release/publish/pin-bump without explicit user approval.

## Success criteria

- [ ] A family of N worktrees costs roughly one index plus per-view diffs, not N indexes
      (validation target: 8-worktree dotnet/runtime family ≤ ~1.2× a single index at typical
      task-branch divergence, **measured as physical bytes on disk after GC**).
- [ ] New worktree of an indexed family serves in seconds-to-low-tens-of-seconds — ≥10× faster
      than the shipped rebind copy path on the same fixture — **including resolution binding**
      (base + delta at typical task-branch divergence; an honest full rebase beyond the measured
      crossover).
- [ ] Branch switch is a manifest repoint; retained versions cost zero extraction.
- [ ] Per-view query results row-equivalent to a fresh dedicated index of the same checkout
      (equivalence gate, per language), including under adversarial retention histories
      (candidate windows crowded by invisible versions).
- [ ] Miller's existing contract suite passes unchanged; lexical output byte-identical.
- [ ] Crash-point matrix green: SIGKILL at any import/flip/converge/GC boundary leaves complete
      versions serving, incomplete versions invisible, and replay idempotent.
- [ ] Mixed-version matrix green: older Miller/julie processes degrade honestly against a newer
      store (epoch/floor gates), never corrupt or silently serve wrong data.
- [ ] GC demonstrably reclaims physical bytes; purge removes named content from store, sidecars,
      and WALs.
- [ ] All platforms equally (no FS-specific behavior).
- [ ] Out of scope: single-copy baseline size — owned by the
      [levels program](2026-08-03-progressive-indexing-levels-program.md); the programs compound.

## Phases

### Ph0 — Prototype gate (hard go/no-go). ~2–3 sessions.

Decisive proofs before any contract work; the program does not proceed past a red gate
(doubt-pass finding 13).

- Purity audit across the **complete** table inventory in julie-extractors, including the
  denormalized-resolution split and composite-identity audit; confirm P1a landed status.
- Final level composition, folded from the levels program's P0: table-set membership (where
  type_facts/complexity land), L2/L3 ordering, and whether a per-file identifier cap ships
  alongside.
- Throwaway instruments (razorback:prototyping), at realistic version counts and
  dotnet/runtime-scale row counts where the claim depends on scale:
  - read-path overhead: manifest join vs temp visibility table, via the read-session shape;
  - filtered-retrieval equivalence: FTS word/trigram arms and vector KNN with visibility inside
    retrieval, including adversarial histories (>200 hidden trigram matches, >500 hidden vector
    matches crowding the windows);
  - new-view resolution binding cost (base + delta vs full pass);
  - eight-view physical byte projection with shared bases + deltas;
  - composite-key size amplification on the biggest tables;
  - DocId + BM25 projection: reconcile the two existing DocId histories (fresh ordinal
    assignment vs incremental stable reuse), choose the canonical per-view history, and measure
    projection maintenance cost and its eight-view bytes;
  - import transaction granularity: per-version/per-chunk commit units vs today's
    single-transaction snapshot — WAL peak, crash-reuse of complete versions, throughput;
  - GC physical reclamation with `auto_vacuum=INCREMENTAL` + FTS merge behavior;
  - migration peak-disk model.
- Store growth model under real churn (retention-window sizing input).
- Acceptance:
  - [ ] Every proof above recorded in a findings doc with a go/no-go call per assumption.
  - [ ] Purity achieved-by-schema (or violations enumerated with the v4 surgery specified).

### Ph1 — Store contract design doc. ~1–2 sessions.

- v4 schema (tables, composite identity, completion markers + commit granularity, per-level
  table-set gates + completeness stamps, two-epoch compatibility + floors), verb shapes, family-id derivation, fingerprint
  serve-while-converging semantics, resolution base/delta **state machine** (keys incl. resolver
  epoch, precedence, tombstone scope, CAS rebase, GC roots), GC + secure-purge contract,
  promotion-capacity formula, WAL/checkpoint policy, failure semantics (interrupted import,
  lease loss, journal integration), **concurrency contract** (durable coordinator-queue
  execution protocol — request IDs, idempotency, claim states, successor recovery, chunked long
  operations — plus lock order, fairness, deadlock analysis), migration + rollback contract.
- Cross-model review gate (Codex + Grok) before freeze, per repo convention — this is doubt-pass
  cycle 3's natural home if cycle 2 leaves open items.
- Acceptance:
  - [ ] Contract doc in `docs/plans/` with cross-model review recorded.

### Ph2 — julie-extractors implementation. ~3–4 sessions + release approval.

- Store schema + `store import/update/delete/gc/export` + `--from-artifact` migration transform +
  resolution bases/deltas on the P1a machinery + level-gated extraction (L1-first import,
  background L2/L3 deepening per version). Crate tests: dedup correctness,
  interrupted-import recovery, GC + physical reclamation, fingerprint mixing, epoch/floor gates,
  the equivalence gate on a multi-language fixture.
- Acceptance:
  - [ ] Equivalence gate green; release shipped; Miller pin bumped (user approval).

### Ph3 — Miller wiring. ~4–6 sessions.

- View-aware read session / connection factory replacing raw `IndexDbPath` across all readers;
  per-view ranking statistics + DocId derivation; provider (family, view) resolution; registry
  family columns + source-of-truth reconciliation; coordinator queue + lock order over the
  governor; L1-first bootstrap orchestration + per-level tool degradation; sidecar re-key with
  idempotent cursors + stamps; migration flow with preflight;
  off-switch with export-on-rollback; status/health/dashboard provenance; CLAUDE.md
  ownership-language amendment + AGENTS.md sync.
- Acceptance:
  - [ ] Fresh worktree of an indexed family serves via the store (no copy path).
  - [ ] Existing contract suite green; fast-suite budget intact.
  - [ ] Off-switch degrades honestly (export or not-ready; never a stale artifact).

### Ph4 — Dashboard family-store panel. ~1 session.

- Size/reclaimable/per-view stats, retention config, prune + purge POSTs (ADR-0002 pattern).
- Acceptance:
  - [ ] Prune reclaims measured physical bytes end-to-end from the dashboard.

### Ph5 — Scale validation. ~1–2 sessions.

- dotnet/runtime family: every success-criteria box above, including the crash-point and
  mixed-version matrices, GC reclamation, and adversarial retention histories; plus the L1
  first-open target (~10 minutes serving at 58k files, the levels-program P3 target) while L2
  converges behind it.
- Acceptance:
  - [ ] All success-criteria boxes checked in a findings doc.

**Estimated total: ~13–19 agent sessions** across both repos (phase sums 12–18 plus integration
margin; the folded levels work replaces the separate ~6-session levels program), plus human
approval points: the julie-extractors release/pin bumps, the Miller release,
and the default-on decision at validation time.

## Open questions (owed to Ph0/Ph1, with current leans)

- Read-path shape: manifest join vs temp visibility table inside the read session (Ph0 measures;
  no lean).
- Vector sharing: family-shared with pre-filtered KNN vs per-view vectors + shared embedding
  cache (Ph0 measures; lean: whichever keeps byte-identical ranking cheap — the compute win
  survives either way).
- Retention-window default and growth curve under agent churn (Ph0 models; lean: weeks, tunable,
  dashboard-visible).
- Family-id derivation details across git edge cases (Ph1; lean: common-dir identity via the
  existing lineage adapter, registry-mediated for path reuse).
- Rebase policy for long-diverged views (when does a delta chain get folded into a fresh base —
  Ph1; lean: threshold on delta size vs base size).
- First-release default: store on vs opt-in (decided at Ph5 validation, not now).

## Non-goals with standing triggers

- ~~Single-copy index size (the diet)~~ — **no longer a non-goal**: the levels program is folded
  into this program (2026-08-06), so the baseline diet and the multiplication fix ship through
  one level-aware contract.
- **Cross-project dedup** (two different repos sharing versions) — trigger: evidence of
  meaningful cross-repo content overlap on real machines. Family scope is deliberate.
- **Central machine daemon** — unchanged from the rebind program: trigger is sustained 20+
  heavy-churn worktrees with governor-wait pain. The family coordinator is still a lease-holder
  in an existing process, not a server.
- **Storage-engine swap (LanceDB et al.)** — unchanged trigger (~100× embedding growth). The
  store remains plain SQLite files; `store export` preserves the copyable-artifact property.

## Verification strategy

**Project source of truth:** Miller `CLAUDE.md` (testing split + build guards); julie-extractors
`cargo test` conventions.

**Worker red/green scope:** Miller — `scripts/test.sh` per change; pure seams for
view/retention/GC policy. julie-extractors — targeted crate tests per change.

**Lead affected-change scope:** `scripts/test.sh scale` for bootstrap/store paths;
julie-extractors full `cargo test` + `xtask dogfood`.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh all`
before any PR; julie-extractors release checklist before pin bumps.

## Doubt-pass record (cycle 1, 2026-08-06, codex)

Verdict on the original draft: REFUTED as written; the CAS+views model itself endorsed with
amendments ("prototype the family store as an immutable extraction CAS, but make resolution
sharing a v1 prerequisite"). Thirteen findings; dispositions:

| # | Finding | Disposition |
|---|---|---|
| 1 | 8 private overlays ⇒ 1.84×, fails ≤1.2× | **Accepted** — resolution sharing (base + delta) is v1-required |
| 2 | P1a cannot bootstrap an empty overlay (full pass at 12.9M identifiers) | **Accepted** — new views bind a sibling base + delta |
| 3 | `identifiers.target_symbol_id` denormalization breaks purity | **Accepted, verified live** (156,953/380,720 rows; `COALESCE` reader) — v4 schema surgery |
| 4 | Span-folded IDs collide across versions; composite identity everywhere; audit full inventory | **Accepted** — §1 composite-identity requirement |
| 5 | Shared recall + post-filter breaks byte-identical BM25 and starves candidate windows | **Accepted, verified** (`_documentCount`/`_avgdl`/df; trigram window 200) — visibility inside retrieval, view-local stats, vector fallback |
| 6 | Raw `IndexDbPath` seam insufficient; read-session seam needed; Ph3 wider | **Accepted** — §3 seam, Ph3 re-estimated |
| 7 | Append-only ≠ promote retirement: completion markers, ready pointers, repair-tier promotion, richer freshness key | **Accepted** — promote retained for repair tier; markers + stamps specified |
| 8 | No cross-WAL atomicity; cursors/stamps; WAL growth policy | **Accepted** — idempotent cursors + per-layer stamps + checkpoint policy |
| 9 | GC reclaims no physical bytes without `auto_vacuum` decided at creation; FTS tombstones; secrets purge | **Accepted** — `auto_vacuum=INCREMENTAL`, staged merges, purge path |
| 10 | Lifetime lease + equal-version no-yield cannot scope to family; lock-order deadlock | **Accepted** — coordinator-executes-queued-requests + global lock order |
| 11 | Fingerprints ≠ rolling compatibility; invisibility = upgrade-day outage | **Accepted** — epochs/floors + serve-while-converging |
| 12 | Migration is a full transformation; peak disk; rollback staleness; three persistence domains | **Accepted** — preflight, export-on-rollback, store as source of truth |
| 13 | Ph0 must be a hard go/no-go gate with adversarial proofs | **Accepted** — Ph0 rewritten as the gate |

Parked (out of the doubt pass's scope, user's strategic call): codex's preference to ship the
levels program first and keep rebind as-is. **Resolved by the user 2026-08-06:** the levels
program is folded *into* this program (level-aware v4 contract, L1-first import) rather than
shipped before or beside it — see "Progressive levels: folded in."

## Doubt-pass record (cycle 2, 2026-08-06, codex)

Verdict on the cycle-1-amended draft: REFUTED **for contract freeze** ("the CAS+views direction
remains viable, but the amended spec is not ready for contract freeze") — consistent with this
document's structure, where the contract freezes in Ph1, not here. Codex audited all 13 cycle-1
dispositions (none ignored; #2/#9/#11 rated partially addressed) and raised nine findings. All
nine were verified (resolver constants, writer transaction shape, queue claim mechanics, and
both DocId behaviors confirmed in code) and accepted:

| # | Finding | Disposition |
|---|---|---|
| 1 | Base identity must include the resolver epoch (`RESOLUTION_VERSION`), not manifest hash alone | **Accepted** — base/delta keys amended |
| 2 | Base/delta needs a real state machine (keys, precedence, tombstones, CAS rebase, GC roots) | **Accepted** — Ph1 deliverable |
| 3 | "Never a full pass" is false: P1a promotes to full at `DELTA_SCOPE_CROSSOVER = 0.7` | **Accepted** — SLO bounded by divergence |
| 4 | Converge queue is best-effort (rename-claim, expired claims deleted); imports need a durable execution protocol | **Accepted, verified** — §2 rewritten |
| 5 | Two conflicting DocId histories + full-scan BM25 stats; per-view projection economics unmeasured | **Accepted, verified** — named Ph0 proof |
| 6 | Blanket per-file fingerprint mixing is not a legal compatibility rule | **Accepted** — two-epoch contract, shadow-manifest flip |
| 7 | Completion markers conflict with julie's single-transaction snapshot writer | **Accepted** — commit-granularity Ph0 proof + Ph1 contract |
| 8 | Purge lacks secure-delete guarantees; `optimize` is unbounded (use page-limited `merge`) | **Accepted** — secure_delete + FTS5 secure-delete from creation; purge escalation |
| 9 | Capacity preflight must cover every generation promotion, not only migration | **Accepted** — promotion-capacity formula |

The doubt pass closed at cycle 2 (of the 3-cycle cap): every surviving refutation is folded
above, and the remaining findings are contract-level detail that only exists to attack once Ph1
drafts the contract. The third cycle is therefore reserved as the Ph1 freeze re-attack (the Ph1
cross-model gate), where cycle-1 #2/#9/#11 and cycle-2 #2/#4/#7 close for good.
