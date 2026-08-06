# Rebind Contract — P1 Design (v1: copy-and-rebind)

**Status:** P1 deliverable of the
[worktree delta-rebind program](2026-08-02-worktree-delta-rebind-program.md). Freezes the contract
P2 implements in julie-extractors and P3 consumes in Miller. Gated evidence:
[P1 cost model](../findings/2026-08-05-rebind-p1-cost-model.md) (artifact is root-portable; rebind
surface is one metadata row; base+overlay refuted) and julie-extract 2.26.0 (P1a delta-scoped
resolution: leaf-file whole-repo scan 12.20 s → 2.88 s, delta-vs-full equivalence gate 9/9,
crossover self-promotion at 0.7 — `docs/release-notes/v2.26.0.md` in julie-extractors).

**Goal restated:** a fresh linked worktree of an already-indexed repo becomes ready in
seconds-to-low-minutes by copying the main checkout's artifact, retargeting its recorded root, and
delta-scanning only the files that differ — instead of paying a full extraction.

Citations below are julie-extractors @ `d803e70` (v2.26.0) and Miller @ `436fe884`. Cross-model
review (Codex + Grok, §11) reshaped the copy protocol and failure semantics from the first draft.

## 1. Decisions (summary)

| Question | Decision |
|---|---|
| v1 shape | **Copy-and-rebind.** Snapshot the source artifact into the target's `symbols.db.rebuild` via SQLite online backup, retarget via a new julie-extract `rebind` verb, delta-scan the `.rebuild` file non-force, promote via `FullRebuildPromotion`. Base+overlay is refuted (cost-model doc §5), not deferred. |
| Who rewrites metadata | **julie-extract**, via the new `rebind` verb (§3). Miller never rewrites extractor metadata privately (program constraint). |
| Copy protocol | **SQLite online backup API, no source lock, ever** (§4). The source `SingleWriterLock` is the lifetime leadership lease (`src/Miller.Server/Hosting/EditWriteLock.cs:7-10`), not a briefly-acquirable fence — a live main leader holds it until shutdown, so any lock-based protocol makes rebind unavailable exactly in the fleet case the program targets. The backup API is consistent under a live writer and writes nothing to the source. |
| Orchestration | **Dedicated bootstrap sequence** (§7.1): clean staging → backup-seed `.rebuild` → validate the snapshot → `rebind` → non-force `scan` against the `.rebuild` path at its recorded level → `Promote`. Never routed through `JulieExtractRunner.Scan(force: true)` — its `PrepareRebuildTarget` deletes the seed (`src/Miller.Indexing/JulieExtractRunner.cs:480-493`). |
| Workspace identity | **Unchanged.** `WorkspaceId.FromCanonicalRoot` stays as-is; sibling lookup uses new **registry lineage columns** (§5). No workspace_id re-derivation. |
| Artifact identity across rebind | **New random `artifact_id`**, provenance recorded in additive metadata keys (§3.3). |
| Extractor identity | Miller-side numeric-triple version prefilter; the **rebind verb hard-validates the artifact's parser/capability fingerprints** against the running binary (§3.2, §6.3). |
| Scan intent | **No new `ScanIntent`**, and explicitly NOT `ScanIntent.RootRebind` (a force repair intent with own-intent-only discharge and symbols-level rebuild routing — `src/Miller.Core/Freshness/ScanIntent.cs:26-30`, `src/Miller.Indexing/IndexLevels.cs:185-187`). Rebind is a bootstrap strategy for the existing `!dbExists → IncrementalReconcile` arm (§7.3). |
| Sidecars | Not copied. `search.db`/`content.db`/`vectors.db` converge from the rebound artifact through the existing `artifact_id`-keyed paths (§8). |

## 2. Why this shape is now safe to freeze

The cost-model doc refused to freeze this contract on 2.25.0 because rebind's delta scan paid a
whole-artifact resolution pass regardless of delta size (verdict, cost-model doc §6). 2.26.0
removed that: whole-repo scans take the scoped resolution branch when no path moved
(`crates/julie-extract-artifact/src/writer.rs:1417-1422` sets `is_full_scan` only on structure
change or force; `crates/julie-extract-cli/src/resolution.rs:1644` forces Full only for
`scope.is_full_scan || prior.is_none()`), self-promote to Full past the 0.7 crossover
(`resolution.rs:2658-2694`), and are gated by a 9/9 delta-vs-full equivalence suite
(`tests/resolution_scope_equivalence.rs`).

Portability is already proven: every path column in all twelve path-bearing tables is
root-relative, IDs hash the relative path, and exactly one metadata key (`root_path`) is
root-derived (cost-model doc §1). `file_id = stable_id("file", [root_relative_path])`
(`crates/julie-extract-cli/src/extraction.rs:229,262,295`), so a byte-identical tree rebinds to a
**zero-file delta** and the scan exits `no_change` in ~0.1 s.

One structural caveat carries into acceptance: a fresh worktree whose branch diverges from the
main checkout by added/deleted files makes the first delta a *structure-changed* scan, which sets
`is_full_scan: true` (`writer.rs:1417-1422`) and pays a full resolution pass. Extraction is still
delta-scoped, so the rebind remains far cheaper than a from-scratch build, but the P4 numbers must
report the add/delete case separately from the modify-only case.

## 3. The julie-extract `rebind` verb (P2 contract)

### 3.1 Invocation

```
julie-extract rebind --db <artifact.db> --root <new-root> [--json] [--strict-schema]
```

Follows every existing verb convention (`crates/julie-extract-cli/src/args.rs:16-24`,
`docs/contracts/cli.md:47-55`): clap subcommand, `--json` report, `--strict-schema`, canonicalized
paths at the CLI boundary (`paths.rs:27-40`), `CommandOutcome` report + coarse exit codes
(0 completed, 1 failed, 2 usage, 3 incompatible — `docs/contracts/cli.md:355-360`).

### 3.2 Validation (before any write)

- Artifact exists and passes the same `check_versions` gate as other artifact verbs
  (`artifact_access.rs:319-375`): newer schema ⇒ `schema_incompatible` exit 3; contract mismatch ⇒
  `contract_incompatible` exit 3; `--strict-schema` inequality ⇒ `schema_migration_required`.
- **Extractor-identity gate:** the artifact's recorded `parser_inventory_fingerprint` and
  `capability_snapshot_fingerprint` must equal the running binary's — a new typed exit-3 refusal.
  `binary_version` alone is not extractor identity (two builds can share `CARGO_PKG_VERSION`), and
  a later delta scan restamps both fingerprints to the current binary
  (`commands.rs:2318-2327`) while leaving unchanged rows as-extracted — so the gate must run
  BEFORE the retarget, in the verb that owns fingerprint computation.
- The artifact must have at least one committed extraction revision — refuse to retarget a
  metadata-only crash shell (the same invariant Miller's bootstrap holds via
  `HasCommittedRevision`, `src/Miller.Server/Hosting/IndexBootstrapService.cs:1110-1120`).
- `--root` must exist and canonicalize; usage errors are exit 2.
- **Rebinding to the recorded root is a success no-op** (`no_change`-style, exit 0) so retries are
  idempotent.
- The verb does NOT require the new root's tree to match the artifact — reconciliation is the
  follow-up `scan`'s job.

### 3.3 Effects — one WAL transaction

All writes in a single transaction (the writer's every-write-is-one-transaction convention,
`writer.rs:1195-1199`), atomic under WAL; an interrupted rebind leaves the artifact either fully
retargeted or metadata-identical to the seed.

- `root_path` ← `display_path(canonicalized new root)`.
- `artifact_id` ← freshly generated **with a random component** (UUID-class, not the existing
  clock-only `artifact-<unix nanos>` of `commands.rs:2352-2359` — concurrent worktree rebinds must
  not be able to collide). Rationale for a new id: `artifact_id` is Miller's generation identity
  (`src/Miller.Indexing/SymbolsArtifactIdentity.cs:26-30`); a rebound copy is a new lineage.
- `updated_at` refreshed; `created_at` preserved (the extraction data's age is unchanged).
- New **additive, optional** provenance keys: `rebound_from_root` (the previous recorded root),
  `rebound_from_artifact_id`, `rebound_at`. Additive keys follow the `index_level` precedent
  (`metadata.rs:84-106`) and stay OUT of `REQUIRED_METADATA_KEYS` (`metadata.rs:7-19`), so older
  binaries and Miller readers (which read only seven keys, all tolerant of extras) are unaffected.
- Everything else is untouched: `binary_version` and both fingerprints (refreshed by the follow-up
  scan's `refreshed_metadata`), `reference_resolution_*` (path-relative, still valid),
  `index_level`, all data tables, and `extraction_revisions` history (`input_root` rows keep the
  old absolute root as honest history; nothing validates them on open — verified against
  `schema.rs:112` and the open paths).

### 3.4 Report

`--json` report carries: `previous_root`, `new_root`, `previous_artifact_id`, `new_artifact_id`,
and a `changed` boolean (false for the same-root no-op). New `ReportCode` entries (success code,
the fingerprint-mismatch and no-committed-revision refusals) bump the size-asserted
`ALL`/`ERROR_CODES` arrays (`reports.rs:391-441`). CLI contract version bumps per
`docs/contracts/cli.md:12-22`; no SQLite schema change (`SQLITE_SCHEMA_VERSION` stays 5), no
change to existing verbs' argv.

### 3.5 Explicitly out of the verb

- **No copying.** The verb operates on the artifact at `--db` in place; producing that copy is the
  caller's job (§4). This keeps the verb a pure, atomic metadata retarget.
- **No scanning.** Reconciliation is the existing `scan` verb, which passes its own
  root-match check once `root_path` is rewritten (`artifact_access.rs:295-317`).
- **No sensitive-root policy.** Root safety stays Miller's concern (`WorkspaceRootSafety`), as for
  every other verb.

## 4. Copy protocol — online backup, no source lock (amends the P0 citation)

Two facts kill any lock-based copy fence, and the P0 clone measurement must not be cited without
them:

1. The P0 audit's "clonefile is free" number was measured on a **quiescent** artifact
   ([P0 audit](../findings/2026-08-05-rebind-p0-measurement-audit.md)); cloning a live
   `symbols.db` + `-wal` non-atomically can produce a torn copy, and making it quiescent requires
   a `wal_checkpoint(TRUNCATE)` — a WRITE to the source database file.
2. The source's `SingleWriterLock` is the **leadership lease, held for the life of the process**
   by a live leader (`src/Miller.Server/Hosting/EditWriteLock.cs:7-10`,
   `src/Miller.Server/Hosting/IndexerService.cs` leadership session) — it is never briefly
   acquirable while a main-checkout Miller runs, which is precisely the fleet case this program
   exists for.

**v1 protocol:**

1. The entire rebind bootstrap — copy, verb, delta scan — runs under the target's normal
   machine-wide governor admission (`ScanGovernorAdmission.TryAcquire`). The copy of a multi-GB
   artifact is the same class of machine load the governor exists to bound
   (`src/Miller.Indexing/ScanGovernor.cs:64-73`); N worktrees must not run N concurrent backups.
   No source `SingleWriterLock` is ever taken, so the governor's lock-order rule
   (`ScanGovernor.cs:80-82`) is never in tension.
2. Snapshot via the **SQLite online backup API** from a read-only source connection into the
   target's `symbols.db.rebuild`. Consistent by construction under a live writer; **the source
   ARTIFACT is untouched** — no writer lock, no checkpoint, and no page of `symbols.db` or its
   `-wal` is written. The copy does take part in the standard WAL-reader protocol every Miller
   cross-workspace read already uses (`SqliteReadOnlyAccess.Open`): the wal-index `-shm` is
   created or updated, and the DB's directory is probed once per process for writability. So
   rebind requires source-directory writability exactly as every existing reader does; the
   `immutable=1` alternative that would avoid even that is rejected repo-wide because it silently
   drops uncheckpointed `-wal` rows under a live julie writer. Cost is a byte copy (~0.25 s at
   755 MiB; tens of seconds at the 22.84 GiB dotnet/runtime artifact — noise against a 25–40 min
   full scan). NOT `Microsoft.Data.Sqlite`'s `BackupDatabase` wrapper: it is one synchronous
   uncancellable `sqlite3_backup_step(-1)`, which makes the budget below unenforceable (a timeout
   would either hold the governor indefinitely or release it while the copy still runs). Use a
   **page-stepped raw backup loop** (`SQLitePCL.raw` `sqlite3_backup_init`/`sqlite3_backup_step(N)`)
   that checks the budget between steps.
3. **Bounded budget:** a source write during backup restarts the copy, so an actively-scanning
   source can livelock it. The copy carries a wall-clock budget (default on the order of a few
   minutes, env-overridable) enforced between backup steps; exhaustion abandons rebind → plain
   bootstrap scan. Best-effort pre-check: skip rebind while the source's `scan.progress`
   heartbeat is fresh.
4. APFS `clonefile`/reflink is a **deferred optimization**, not v1: it needs source quiescence
   that has no order-safe protocol while a leader lives. If P4 shows the backup copy dominating
   rebind wall-clock at scale, a cooperative snapshot performed by the source's own leader is the
   follow-up shape — never a second process taking the source writer lock.

## 5. Identity: registry lineage columns (final call)

`WorkspaceId.FromCanonicalRoot` (`src/Miller.Indexing/WorkspaceId.cs:10-24`) is unchanged. Its
blast radius made re-derivation a non-starter: it is the registry PK, the display-ID input, and
the join key for telemetry, metric history, canary ledger, and level policy.

Sibling lookup adds **lineage columns** to `~/.miller/workspaces.db` `workspaces`
(`WorkspaceRegistry.cs:8-21`), populated at `UpsertSeen` time from the W7 adapter:

- `git_common_dir TEXT` — `GitWorktreeLayout.Resolve(root)?.CommonDir`
  (`src/Miller.Indexing/GitWorktreeLayout.cs:33,48-83`), **canonicalized through
  `PathCanonicalizer`** before storage. `GitWorktreeLayout` uses `Path.GetFullPath` only — no
  symlink resolution (`GitWorktreeLayout.cs:53,141-148`) — while registry roots are
  symlink-canonicalized; raw string equality would silently disqualify eligible mains on
  macOS `/var`→`/private/var`-style layouts. The repository-lineage key: all worktrees of one
  repo share it. NULL for non-git roots (never eligible for rebind).
- `git_is_linked INTEGER` — `IsLinkedWorktree` (`GitWorktreeLayout.cs:38-39`). Distinguishes the
  main checkout (rebind source) from linked worktrees (rebind targets).
- `git_dir TEXT` and `git_dir_created_at TEXT` — **both halves** of `WorkspaceRootIdentity`
  (`src/Miller.Indexing/WorkspaceRootIdentity.cs:27,44-62`; `IsReplacement` compares gitdir path
  AND creation time, `:69-79`). Persisting only the timestamp would rebuild a different identity
  than the monitor compares. This makes path-reuse detection restart-proof: today the identity
  sample is in-memory only, so `git worktree remove`+`add` while no Miller runs is invisible.

**Consumption rule (P3, load-bearing):** on bootstrap, when the registry row carries a known
persisted identity and `WorkspaceRootIdentity.IsReplacement(stored, current)` is true, escalate
the bootstrap decision to `ScanIntent.RootRebind` (the existing
`EscalateForReplacedRoot` fold, `IndexBootstrapService.cs:935-946`) and **disqualify rebind** for
that open — the on-disk artifact and the registry row describe a different checkout generation.
The columns are then refreshed by the normal `UpsertSeen`. A column without this rule closes
nothing.

**Lookup rule (P3):** target resolves its own `GitWorktreeLayout`; if `IsLinkedWorktree`, the
rebind source candidate is the registry row with the same canonicalized `git_common_dir` whose
`canonical_root` matches the canonicalized `MainCheckoutRoot` (`ArtifactRootIdentity.Matches`
comparison semantics, `src/Miller.Indexing/ArtifactRootIdentity.cs:22-34`) — main checkout only in
v1, no transitive worktree-to-worktree sourcing. This lookup is **provisional** (§6): the copied
snapshot is what gets validated. Freshness of the source artifact is irrelevant to correctness:
the delta scan reconciles against the target tree by blake3 regardless of how stale the copy is.

## 6. Rebind eligibility

Registry-level prefilters (cheap, provisional):

1. Target root is a **linked worktree** (`GitWorktreeLayout.IsLinkedWorktree`), the artifact is
   absent (`!dbExists` arm of `DecideBootstrapScan`, `IndexBootstrapService.cs:906-925`), and no
   root replacement was detected (§5 consumption rule).
2. A registered **main-checkout sibling** exists (§5 lookup) whose `symbols.db` file exists.
3. `binary_version` prefilter: the source row's artifact version equals the pinned extractor
   version under the **numeric `major.minor.patch` comparison** `LeadershipEligibility` already
   uses (`src/Miller.Indexing/LeadershipEligibility.cs:31,110-141`) — never raw string equality
   (probes and metadata spell versions differently).
4. No standing W8 failure record for this workspace (§7.4).
5. `MILLER_FULL_REBUILD_INPLACE` is not set: that escape hatch exists for environments whose
   `.miller` directory cannot hold two artifacts at once (`JulieExtractRunner.cs:500-505`), and
   rebind inherently stages a full-size `.rebuild` beside the live path.

Snapshot-level validation (authoritative — runs against the copied `.rebuild` file after the
backup, eliminating the check/use race between registry probe and copy; the registry row can be a
generation behind by the time the backup finishes, but the snapshot cannot):

6. Schema/contract compatible (`JulieSchemaGate.Verify` equivalents), `hash_algorithm = blake3`,
   recorded `root_path` matches the SOURCE root (`ArtifactRootIdentity.Matches` — proves the copy
   is the sibling artifact, not debris), and **at least one committed extraction revision**.
   `ArtifactRootIdentity.ServableFor` alone is NOT sufficient — it checks existence, root, and
   schema (`ArtifactRootIdentity.cs:43-63`) but not committed history, and a metadata-only crash
   shell would otherwise be copied and silently pay a from-scratch scan. (The rebind verb refuses
   such a shell too, §3.2 — this check just fails cheaper and earlier.)
7. `binary_version` equality re-checked on the snapshot (numeric triple). The authoritative
   extractor-identity gate — parser/capability fingerprints — is the rebind verb's own refusal
   (§3.2), which runs next and cannot be raced.
8. The snapshot's recorded `index_level`, **retained as-is**, satisfies the target's resolved
   level policy (`IndexLevels.ResolveForWorkspace`): a full-level snapshot satisfies every
   policy; a symbols-level snapshot satisfies `SymbolsOnly` and `Progressive` (the standing
   `LevelUpgrade` latch re-arms from the artifact, `IndexLevels.UpgradeOwed`) but NOT `Full`.
   Level changes require a fresh force rebuild, never a rebind.

Any prefilter or validation failure → plain bootstrap scan (§7), after staging cleanup.

## 7. Orchestration and failure semantics

**Invariant: no rebind step writes the source ARTIFACT — no writer lock, no checkpoint, and no
page of `symbols.db` or its `-wal`.** The backup API reads through a read-only connection;
everything else operates on the target's staging file. The read-only open is the standard Miller
WAL-reader protocol (§4.2): wal-index `-shm` creation/update and a one-time directory writability
probe are permitted and expected, so rebind needs source-directory writability exactly as every
existing cross-workspace read does.

### 7.1 The sequence (all under the target's bootstrap writer lease + one governor admission)

1. `FullRebuildPromotion.PrepareRebuildTarget(liveDb)` — mandatory staging hygiene BEFORE seeding.
   The existing call sites run it only on the force-scan path (`JulieExtractRunner.cs:480-493`);
   the plain-bootstrap fallback never does, so rebind must own its own cleanup at entry and on
   every failure exit, or a dead rebind strands a multi-GB `.rebuild` trio indefinitely.
2. Backup-seed `symbols.db.rebuild` (§4), bounded budget.
3. Snapshot validation (§6.6–8) against the `.rebuild` file.
4. `julie-extract rebind --db <symbols.db.rebuild> --root <target root>` (§3).
5. Non-force `julie-extract scan --root <target root> --db <symbols.db.rebuild>` at the
   **snapshot's recorded level** — NOT through the existing bootstrap level wiring, which treats
   a missing live DB as a new artifact (`newArtifact: !scanDecision.Force`,
   `IndexBootstrapService.cs:574-576`) and under progressive/symbols-only policy emits
   `--level symbols`; julie hard-rejects a requested level that differs from the recorded one
   (`commands.rs:311-323`). The seeded copy is an EXISTING artifact: inherit its level.
   Also never through `JulieExtractRunner.Scan(force: true)` — its `PrepareRebuildTarget` deletes
   the seed.
6. `FullRebuildPromotion.Promote(liveDb)` — unchanged semantics; the target's freshness plumbing
   sees a normal generation change via the new `artifact_id`.
7. Normal registry bookkeeping (`UpsertSeen` + `MarkScanned`, lineage columns refreshed).

### 7.2 Recovery, by phase

- **Failure or death before promotion (steps 1–5):** the live path never existed; the only debris
  is the `.rebuild` trio, deleted on the failure exit path and — belt-and-braces — by step 1 of
  ANY next rebind attempt. The plain-bootstrap fallback additionally runs the same staging
  cleanup at entry so a SIGKILLed rebind cannot strand debris beside the fallback's fresh
  artifact. The source is untouched in every case (it was never written).
- **Death after promotion (step 6):** the target holds a complete, valid generation — this is
  success, not a failure state. The next bootstrap's `ReadBootstrapScanDecision` adopts it
  (committed revision present, root matches) exactly as it adopts any existing artifact.
- **`Promote` throwing is not proof promotion did not happen:** it checkpoints and deletes
  sidecars both before AND after the live-file move (`FullRebuildPromotion.cs:103-145`), so an
  exception can postdate the move. On any promotion exception, probe the live path
  (`ReadBootstrapScanDecision` semantics: root matches + committed revision) — if the moved
  artifact is there, adopt it as success; only an absent/foreign live path is a pre-promotion
  failure.

### 7.3 Recording — no new `ScanIntent`

A failed rebind records under W8 as a failure of the bootstrap scan it stood in for — intent
`IncrementalReconcile`, the `!dbExists` arm's intent (`IndexBootstrapService.cs:911-913`). A new
enum value would default into repair semantics (own-intent-only discharge,
`ScanIntentPolicy.Satisfies` fall-through) and be discarded whole by older Millers reading the
shared journal (`ScanFailureJournal` name-parses intents, `ScanFailureJournal.cs:167-179`).
Explicitly NOT `ScanIntent.RootRebind` (§1). Steps with no subprocess exit code (backup failure,
budget exhaustion, snapshot validation failure) record a null exit code; the W8 record's purpose
here is the rebind-suppression marker and backoff input, not exit-code forensics.

### 7.4 Retry policy — honest about what W8 can and cannot promise

Bootstrap runs `Evaluate(intent, bypassBackoff: true)` (`IndexBootstrapService.cs:566`) — it never
defers on the timer. So rebind's "one-shot" discipline is carried by the RECORD, not the clock:

- Suppression is **conservative**: eligibility prefilter 4 skips rebind when ANY standing W8
  record exists, because `ScanFailureRecord` carries no stage/origin field
  (`ScanFailurePolicy.cs:19`, `ScanFailureJournal.cs:159`) — a "rebind failed here" marker cannot
  be represented, and `IncrementalReconcile` + null exit code also describes ordinary local
  failures. At the `!dbExists` bootstrap this is the right bias anyway: any standing record means
  the last attempt to produce this artifact failed, and the plain build is the honest recovery.
  The record clears on scan success per the existing `ClearsFailureRecord` rule, and a later
  `workspace remove` + re-open re-arms rebind. If telemetry shows this suppressing rebind too
  eagerly, the refinement is an additive, tolerated-by-old-readers `stage` field in the journal —
  a contract change deferred until evidence demands it.
- An **unrecorded** crash (SIGKILL cannot write the journal) may legitimately retry rebind on the
  next bootstrap — this is safe, not a loophole: every attempt starts by discarding staging and
  taking a fresh snapshot (§7.1 step 1), so a retry is a clean re-run, and repeated crashing
  eventually records via the surviving process's failure paths.

## 8. Interactions kept explicit

- **Leadership:** eligibility (§6.7) plus the verb's fingerprint gate mean the promoted artifact's
  `binary_version` equals the local pin, so the target instance claims leadership normally,
  `ExtractorUpgrade` does not fire, and same-version yield suppression holds
  (`IndexerLeadershipCoordinator.cs:163-165`).
- **Sidecars:** the rebound artifact carries the source's revision counter but a **new
  `artifact_id`**, so `SymbolsArtifactIdentity.MatchesArtifact`
  (`SymbolsArtifactIdentity.cs:106-113`) correctly treats it as a fresh generation and
  search/content/vectors build through their existing revision-keyed converge paths. No sidecar
  copying in v1: `search.db` builds in seconds, and copied `vectors.db` rows would need the same
  staleness reconcile they get from convergence anyway.
- **Registry hygiene:** the target row is a normal `UpsertSeen` + `MarkScanned`;
  `PruneDuplicatePathRowsUnderLock` (`WorkspaceRegistry.cs:390-416`) is unaffected because the
  target's root/db pair is unique. Lineage columns migrate via the duplicate-column-tolerant
  `ALTER TABLE ADD COLUMN` pattern (`WorkspaceRegistry.cs:337-370`), generalized from the
  `level_policy` one-off; additive and nullable, invisible to older Millers.
- **Provenance surfacing (P3):** `workspace status`/`health` JSON and the dashboard render
  "rebound from `<source display id>` at `<rebound_at>`" from the provenance metadata keys. No new
  MCP tools.

## 9. Acceptance criteria (feeding P2/P3)

P2 (julie-extractors) — **implemented 2026-08-05** (branch `rebind-verb` merged to main at
`13182d9`; release + pin bump pending approval). One addition beyond this spec, from the Codex
pre-merge review: the write transaction re-verifies the validated `root_path`/`artifact_id` and
refuses with a new `artifact_changed` code (exit 1, recoverable), and the write connection opens
without `SQLITE_OPEN_CREATE` — defense-in-depth for direct CLI callers; Miller's staging protocol
is unchanged.
- [x] `rebind` verb per §3 with crate tests: retarget correctness, same-root no-op, interrupted-
      transaction atomicity, fingerprint-mismatch and no-committed-revision refusals, report
      shape, random-component artifact id.
- [x] Row-level equivalence: backup-copy → rebind → delta scan of tree B is row-equivalent to a
      fresh scan of tree B on a **multi-language fixture** (language-parity rule), for
      (a) byte-identical trees, (b) modify-only deltas, (c) add/delete deltas. Exclusions, as
      refined by the shipped gate (`tests/rebind_equivalence.rs`): `artifact_id`, timestamp keys,
      provenance keys, and **revision ids and scan-time stamps wherever they live** — including
      `files.indexed_at`, `files.last_revision_id`, `*.resolved_at_revision`, the
      `reference_resolution_last_full_revision` metadata key, and the
      `extraction_revisions`/`revision_file_changes` history tables. `*_json` columns are compared
      content-wise (sorted keys): extractor hash-map serialization makes artifact bytes
      non-reproducible across identical scans — a standing julie-extractors finding, not a rebind
      artifact.
- [x] Contract docs updated (`docs/contracts/cli.md`, `docs/contracts/reports.md`).

P3 (Miller):
- [ ] Eligibility (§6) as pure, fast-suite-testable decisions; snapshot validation proven against
      a crash-shell fixture (no committed revision ⇒ ineligible).
- [ ] Copy under one governor admission with a live-writer source (backup API), budget-exhaustion
      fallback proven by test.
- [ ] Orchestration (§7.1) including recorded-level scan invocation (no `--level` conflict under
      progressive policy) and the forbidden-path guards (no `Scan(force: true)`, no
      `ScanIntent.RootRebind`).
- [ ] Failure fallback recorded under W8 with the source artifact byte-identical afterward;
      staging debris absent after a killed rebind followed by a plain bootstrap.
- [ ] Lineage columns + replacement consumption rule (§5); provenance in
      `workspace status --json`.

## 10. Deferred (with triggers)

- **clonefile/reflink fast path** — trigger: P4 shows backup-copy time dominating rebind
  wall-clock at the 74k-file tier. Shape: cooperative snapshot by the source's own leader (§4.4).
- **Worktree-to-worktree sourcing** — trigger: fleets where the main checkout has no artifact but
  siblings do. Adds lock-graph and staleness questions v1 deliberately avoids.
- **Sidecar copying** — trigger: measured sidecar rebuild cost at scale materially delays
  first-query readiness after promote.

## 11. Cross-model review record (2026-08-05)

Per repo convention, the contract froze only after independent Codex and Grok review of the first
draft, both read-only against both repos at the pinned commits. Verdicts: both "not ready to
freeze" — 1 critical + 8 major + 2 minor (Codex), 3 critical + 6 major + 3 minor (Grok),
substantially overlapping. Every accepted finding was re-verified against code before
incorporation; the material corrections were:

- Source `SingleWriterLock` is the lifetime leadership lease, never a briefly-acquirable copy
  fence → copy protocol replaced with lock-free online backup under the governor (§4). [both]
- The draft's orchestration collided with `JulieExtractRunner.Scan`'s force path (seed deletion)
  and non-force path (no promote) → dedicated sequence (§7.1). [Grok; Codex confirmed the arm]
- Progressive bootstrap level wiring emits `--level symbols` and julie rejects the conflict with a
  full-level seed → inherit the snapshot's recorded level. [both]
- Checkpoint-based fast path contradicted source immutability → removed from v1; deferred as a
  leader-cooperative snapshot. [both]
- `.rebuild` debris reclamation did not hold on the non-force fallback path → cleanup owned by the
  rebind orchestration and the fallback entry. [both]
- Registry lookup was symlink-unsafe (`GetFullPath` vs canonicalized roots) → canonicalize lineage
  columns, `ArtifactRootIdentity.Matches` semantics. [Grok]
- Persisted lineage omitted `git_dir` (half the replacement identity) and had no bootstrap
  consumption rule → both columns + the §5 consumption rule. [Codex, Grok]
- `ServableFor` admits committed-revision-less crash shells; registry probe→copy is a check/use
  race → snapshot-level validation including committed history (§6.5). [Codex]
- `binary_version` equality is not extractor identity → verb-side fingerprint gate (§3.2). [Codex]
- W8 `bypassBackoff: true` at bootstrap made the draft's timer-based eligibility rule vacuous and
  the "one-shot" promise undurable under SIGKILL → record-based suppression + honest crash-retry
  semantics (§7.4). [both]
- Clock-only `artifact-<unix nanos>` id generation is collision-prone across concurrent worktree
  rebinds → random-component id for the rebind verb (§3.3). [Codex]
- Post-promotion death is success, not "artifact-less" → phase-based recovery (§7.2). [Codex]

Findings both reviewers independently marked sound and unchanged: copy-and-rebind over
base+overlay, julie-extract owning the metadata rewrite, new `artifact_id` per rebind, no sidecar
copying, no new MCP tools, main→linked-only sourcing, and the P1a/2.26.0 payoff story.

A second Codex pass verified the revision and returned four residual findings, all incorporated:
the `BackupDatabase` wrapper is uncancellable so the budget needs a page-stepped raw backup loop
(§4.2); `MILLER_FULL_REBUILD_INPLACE=1` environments cannot stage a `.rebuild` so rebind is
ineligible there (§6.5); `Promote` can throw after the live-file move so recovery probes instead
of assumes (§7.2); and the W8 record cannot carry a rebind-stage marker so suppression is
conservative on any standing record (§7.4).

**2026-08-05 pre-merge review (P3 implementation).** Codex read the shipped P3 branch against this
contract and flagged that "zero writes to the source" over-stated what the copy does: the read-only
open probes the source directory's writability once per process and, as a WAL reader, creates or
updates the source `-shm`. The copy protocol is unchanged — that is the deliberate house protocol
for every Miller reader, and `immutable=1` stays rejected because it drops uncheckpointed `-wal`
rows. §4.2 and the §7 invariant were reworded to the precise claim (the source ARTIFACT — database
and `-wal` — is byte-untouched; wal-index and probe activity are permitted), and
`RebindBootstrapScaleTests` now fingerprints `symbols.db` **and** its `-wal` before and after a
rebind bootstrap rather than the database alone. The same pass also found the fallback bootstrap
scan reusing the pre-rebind `ScanAttemptDecision` — which bypassed the post-SIGKILL `--jobs 1`
clamp a just-OOM-killed rebind delta had earned (fixed: `RebindBootstrap.FallbackAttemptAfterRebind`
re-evaluates after a failed attempt) — and a `status=partial` reconciling scan reported as a clean
rebind (fixed: `RebindBootstrapOutcome.Warning`, logged on the promoted arm).
