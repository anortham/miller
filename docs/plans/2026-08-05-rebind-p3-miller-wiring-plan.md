# Rebind P3 — Miller Wiring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** A fresh linked worktree of an already-indexed repo bootstraps by copying the main checkout's artifact, retargeting it with the julie-extract 2.27.0 `rebind` verb, and delta-scanning — instead of paying a full extraction.

**Architecture:** Implements §5–§9 of the frozen contract
[`2026-08-05-rebind-contract-design.md`](2026-08-05-rebind-contract-design.md) (P3 phase of
[`2026-08-02-worktree-delta-rebind-program.md`](2026-08-02-worktree-delta-rebind-program.md)).
Pure eligibility decisions (LeadershipEligibility pattern) + a page-stepped SQLite online-backup
copier + a dedicated bootstrap orchestration sequence that seeds `symbols.db.rebuild`, validates the
snapshot, runs `rebind` then a non-force `scan`, and promotes via the existing
`FullRebuildPromotion`. Registry gains lineage columns for sibling lookup and restart-proof
path-reuse detection.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite + SQLitePCLRaw.bundle_e_sqlite3 3.0.3 (already
direct deps of `Miller.Indexing` — the raw `sqlite3_backup_init`/`sqlite3_backup_step(N)` loop
needs no new package), julie-extract 2.27.0 (pinned; `rebind` verb per
`docs/contracts/cli.md` §rebind in julie-extractors).

**Architecture Quality:** Approved shape is the frozen contract doc — new pure decision statics in
`Miller.Indexing` (`RebindEligibility`), one new I/O helper (`SqliteOnlineBackup`), one new
orchestrator (`RebindBootstrap`), argv/report seams added to `JulieExtractRunner`, lineage columns
in `WorkspaceRegistry`, wiring confined to `IndexBootstrapService`'s `!dbExists` arm. Main risk:
orchestration-path mistakes that route through forbidden machinery (`Scan(force: true)` deletes the
seed; bootstrap level wiring emits a conflicting `--level`). Workers report plan mismatch rather
than redesigning locally.

## Global Constraints

- **The source (main checkout) ARTIFACT stays byte-untouched — no writer lock, no checkpoint, and
  no page of `symbols.db` or its `-wal`.** The backup reads through a read-only connection;
  everything else touches only the target's staging file. The copy still takes part in the standard
  Miller WAL-reader protocol (`-shm` creation/update, a one-time directory writability probe), so
  rebind needs source-directory writability exactly as every existing cross-workspace read does.
- **Never route rebind through `JulieExtractRunner.Scan(force: true)`** — its
  `PrepareRebuildTarget` deletes the seed (`src/Miller.Indexing/JulieExtractRunner.cs:480-493`).
- **No new `ScanIntent`, and explicitly NOT `ScanIntent.RootRebind`** for rebind failures — record
  under W8 as `ScanIntent.IncrementalReconcile` with a null exit code where no subprocess ran.
- **The delta scan runs at the snapshot's recorded `index_level`** — never through the bootstrap
  level wiring (`newArtifact` emits `--level symbols` under progressive policy and julie rejects
  the conflict, `IndexBootstrapService.cs:574-576`).
- **No new MCP tools.** Provenance surfaces through existing `workspace status`/`health` JSON, the
  CLI, and the dashboard.
- `Miller.Core` stays I/O-free; new I/O lives in `Miller.Indexing`/`Miller.Server`.
- Lexical-only search output stays byte-identical; sidecars (`search.db`/`content.db`/`vectors.db`)
  are NOT copied — they converge through existing `artifact_id`-keyed paths.
- Fast suite stays fast; anything spawning julie-extract is `[Trait("Category","Scale")]` via
  `ScaleTestSupport.RequireJulieServer()`.
- Build must stay 0 warnings (warnings-as-errors).
- Whole rebind bootstrap (copy + verb + delta scan) runs under ONE machine-wide governor admission
  and the target's bootstrap writer lease. No source `SingleWriterLock` is ever taken.
- New metadata keys read from the artifact: `rebound_from_root`, `rebound_from_artifact_id`,
  `rebound_at` (additive, optional — absent on never-rebound artifacts).
- New env vars: `MILLER_REBIND_COPY_BUDGET` (seconds or `TimeSpan`, default 3 minutes — the
  backup-loop wall-clock budget, `MILLER_PROMOTE_RETRY_TIMEOUT` parsing precedent) and
  `MILLER_WORKTREE_REBIND=off` (kill switch, repo default-on/off-switch precedent — an assumption
  consistent with `MILLER_SEARCH_SIDECAR`/`MILLER_SEMANTIC`; flag to the user in the final report).
- Rebind is ineligible when `MILLER_FULL_REBUILD_INPLACE` is set (`JulieExtractRunner.cs:500-505`).

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (testing split, build guards) + `scripts/test.sh`.

**Worker red/green scope:** `scripts/test.sh` (fast suite, must stay green and fast). For a single
new test class during TDD: `dotnet test --filter "FullyQualifiedName~<ClassName>"`.

**Worker ceiling:** fast suite. Scale suite belongs to the lead.

**Worker gate invariant:** each task's acceptance criteria below; every pure decision and parsing
seam is fast-suite covered before wiring.

**Lead affected-change scope:** `scripts/test.sh scale` after Batch C lands (rebind touches the
indexing/extract path).

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh all` before
handoff/PR.

**Replay/metric evidence:** none — behavioral gates only. Scale-test assertions (source artifact
byte-identical, debris absent, `no_change` on byte-identical trees) are hard gates.

**Escalation triggers:** any change touching `IndexBootstrapService` start sequence or
`FullRebuildPromotion` requires the scale suite before commit of that batch.

**Assigned verification failure:** workers stop and report when assigned verification fails.

**Verification ledger:** record invariant, command, scope label, commit SHA, result, timestamp in
the task report. Reuse passing evidence at the same HEAD instead of rerunning expensive gates.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Registry lineage columns + sibling lookup | Batch A | Modify `src/Miller.Indexing/WorkspaceRegistry.cs`; Test `tests/Miller.Tests/Indexing/WorkspaceRegistryTests.cs` | No | None - safe parallel batch. |
| Task 2: Bootstrap lineage capture + replacement consumption rule | None - serial | Modify `src/Miller.Server/Hosting/IndexBootstrapService.cs`; Test `tests/Miller.Tests/Server/BootstrapReplacedRootTests.cs` | Yes | Consumes Task 1's row fields and `UpsertSeen` shape. |
| Task 3: RebindEligibility pure decisions | Batch A | Create `src/Miller.Indexing/RebindEligibility.cs`; Test `tests/Miller.Tests/Indexing/RebindEligibilityTests.cs` | No | None - safe parallel batch. |
| Task 4: SqliteOnlineBackup page-stepped copier | Batch A | Create `src/Miller.Indexing/SqliteOnlineBackup.cs`; Test `tests/Miller.Tests/Indexing/SqliteOnlineBackupTests.cs` | No | None - safe parallel batch. |
| Task 5: JulieExtractRunner rebind verb seams | Batch A | Modify `src/Miller.Indexing/JulieExtractRunner.cs`; Test `tests/Miller.Tests/Indexing/JulieExtractRunnerRebindTests.cs` (fast) + `tests/Miller.Tests/Indexing/RebindVerbScaleTests.cs` (Scale) | No | None - safe parallel batch. |
| Task 6: RebindBootstrap orchestration + bootstrap wiring | Batch C | Create `src/Miller.Indexing/RebindBootstrap.cs`; Modify `src/Miller.Server/Hosting/IndexBootstrapService.cs`; Test `tests/Miller.Tests/Indexing/RebindBootstrapTests.cs` (fast) + `tests/Miller.Tests/Server/RebindBootstrapScaleTests.cs` (Scale) | Yes | Consumes Tasks 2, 3, 4, 5 contracts; shares `IndexBootstrapService.cs` with Task 2. |
| Task 7: Provenance surfacing + contract docs | Batch C | Modify `src/Miller.Server/Tools/WorkspaceRender.cs`, `src/Miller.Dashboard/DashboardData.cs`, `docs/contracts/cli-eros-v1.md`; Test `tests/Miller.Tests/Server/WorkspaceRenderTests.cs` | No | None - safe parallel batch (no file overlap with Task 6; end-to-end JSON assertion runs at branch gate). |

Batch order: **Batch A** (1, 3, 4, 5 in parallel) → **Task 2** (serial) → **Batch C** (6, 7 in
parallel). Commit mode: **parallel-lead-commit** for Batch A and Batch C; **serial-worker-commit**
for Task 2.

---

### Task 1: Registry lineage columns + sibling lookup

**Files:**
- Modify: `src/Miller.Indexing/WorkspaceRegistry.cs` (schema head near :8-21, `UpsertSeen`
  :77-123, the duplicate-column-tolerant `ALTER TABLE ADD COLUMN` migration pattern used for
  `level_policy` around :337-370, `PruneDuplicatePathRowsUnderLock` untouched)
- Test: `tests/Miller.Tests/Indexing/WorkspaceRegistryTests.cs`

**Interfaces:**
- Consumes: `GitWorktreeLayout` (`GitDir`, `CommonDir`, `MainCheckoutRoot`, `IsLinkedWorktree` —
  `src/Miller.Indexing/GitWorktreeLayout.cs:32-39`), `WorkspaceRootIdentity`
  (`src/Miller.Indexing/WorkspaceRootIdentity.cs:27`), `PathCanonicalizer`.
- Produces: four nullable columns on `workspaces` — `git_common_dir TEXT` (canonicalized),
  `git_is_linked INTEGER`, `git_dir TEXT`, `git_dir_created_at TEXT` (ISO-8601 round-trip) —
  surfaced as nullable members on `WorkspaceRegistryRow` (`GitCommonDir`, `GitIsLinked`, `GitDir`,
  `GitDirCreatedAtUtc`); an `UpsertSeen` signature extended with an optional lineage argument
  (single record parameter `WorkspaceLineage?` preferred over four positionals); a query
  `WorkspaceRegistryRow? FindMainCheckoutByCommonDir(string canonicalCommonDir)` returning the
  non-linked row whose `git_common_dir` matches, or null.

**Contract inputs:** contract design §5. `git_common_dir` MUST be canonicalized through
`PathCanonicalizer` before storage (raw `GetFullPath` strings silently miss on macOS
`/var`→`/private/var`). Columns are additive and nullable — invisible to older Millers. A null
lineage argument leaves existing stored lineage untouched (an upsert from a context without git
resolution must not erase identity another process persisted).

**File ownership:** Modify `src/Miller.Indexing/WorkspaceRegistry.cs`; Test `tests/Miller.Tests/Indexing/WorkspaceRegistryTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The registry persistence for repository lineage: which repo family a workspace
belongs to (`git_common_dir`), whether it is the main checkout or a linked worktree, and both
halves of the checkout-generation identity so path-reuse detection survives restarts.

**Approach:** Follow the existing `level_policy` migration one-off but generalize it into a small
loop over (column, type) pairs so the four new columns and future additions share one
duplicate-column-tolerant path. Store timestamps ISO-8601 like the existing `*_at` columns. Lookup
matches with `ArtifactRootIdentity.ComparisonFor` semantics (case-insensitivity on
Windows/macOS) — compare via SQL `COLLATE NOCASE` only if the existing registry already does so
for paths; otherwise filter in C# for consistency with `ArtifactRootIdentity.Matches`.

**Acceptance criteria:**
- [x] Lineage columns migrate on an existing registry DB (fixture with the old schema opens
      cleanly and reads null lineage), and round-trip values exactly, including the
      creation-timestamp half.
- [x] `FindMainCheckoutByCommonDir` returns the main-checkout row among mixed rows, ignores linked
      rows and other repos, and applies platform path-comparison semantics.
- [x] Null-lineage upsert preserves previously stored lineage.
- [x] Worker-scope verification passes and the change is handed to the lead per
      parallel-lead-commit.

### Task 2: Bootstrap lineage capture + replacement consumption rule

**Files:**
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs` (`UpsertSeen` call sites; the
  decision fold around `DecideBootstrapScan` :907-926 / `EscalateForReplacedRoot` :938-946)
- Test: `tests/Miller.Tests/Server/BootstrapReplacedRootTests.cs`

**Interfaces:**
- Consumes: Task 1's `WorkspaceLineage` record + extended `UpsertSeen` + row fields;
  `WorkspaceRootIdentity.Capture`/`IsReplacement`
  (`src/Miller.Indexing/WorkspaceRootIdentity.cs:40,69-79`); the existing in-memory replacement
  escalation path.
- Produces: a pure fold `static bool DisqualifiesRebind(WorkspaceRegistryRow? stored, WorkspaceRootIdentity current)`
  (name at implementer's discretion, but pure and fast-suite-tested) that Task 6 calls: true when
  the stored persisted identity is known and `IsReplacement(stored, current)`; the bootstrap
  escalates to `EscalateForReplacedRoot` in exactly that case, BEFORE any rebind attempt.

**Contract inputs:** contract design §5 consumption rule (load-bearing): a replaced root both
escalates the scan decision to `ScanIntent.RootRebind` AND disqualifies rebind for that open —
the on-disk artifact and registry row describe a different checkout generation. Columns refresh
via the normal `UpsertSeen` afterward. Missing/unknown stored identity NEVER counts as a
replacement (missing evidence must not cost a rebuild).

**File ownership:** Modify `src/Miller.Server/Hosting/IndexBootstrapService.cs`; Test `tests/Miller.Tests/Server/BootstrapReplacedRootTests.cs`

**Serialization required:** Yes

**Dependency reason:** Consumes Task 1's row fields and `UpsertSeen` shape.

**What to build:** Persist lineage at every bootstrap `UpsertSeen`, and make the persisted
identity feed the existing replacement escalation so `git worktree remove`+`add` while no Miller
runs is detected on the next open (today the identity sample is in-memory only).

**Approach:** Capture `GitWorktreeLayout.Resolve` + `WorkspaceRootIdentity.Capture` once per
bootstrap (they are already resolved nearby for the presence monitor — reuse, don't re-probe).
Compare stored-vs-current BEFORE the first `UpsertSeen` refreshes the row. Extend the existing
`BootstrapReplacedRootTests` scenario style: persisted-identity replacement (no live monitor
involvement) escalates and would-disqualify.

**Acceptance criteria:**
- [x] A registry row carrying a different known persisted identity escalates the bootstrap
      decision to `RootRebind` (via `EscalateForReplacedRoot`) with no live
      `WorkspaceRootPresenceMonitor` involvement.
- [x] Unknown stored identity or unknown current identity never escalates and never disqualifies.
- [x] Lineage is persisted on bootstrap and refreshed after the decision (stored generation is
      the CURRENT one post-open).
- [x] Worker-scope verification passes and the worker commits per serial-worker-commit.

### Task 3: RebindEligibility pure decisions

**Files:**
- Create: `src/Miller.Indexing/RebindEligibility.cs`
- Test: `tests/Miller.Tests/Indexing/RebindEligibilityTests.cs`

**Interfaces:**
- Consumes: `LeadershipEligibility`'s numeric `major.minor.patch` comparison
  (`src/Miller.Indexing/LeadershipEligibility.cs` — reuse/extract its version-triple parser rather
  than duplicating), `ArtifactRootIdentity.Matches`, `IndexLevels.ResolveForWorkspace` semantics,
  `MillerExtractContract.PinnedJulieExtractVersion`.
- Produces: pure statics Task 6 calls, split in two stages —
  `RebindPrefilter.Evaluate(RebindPrefilterInputs) → RebindDecision` (registry-level, cheap,
  provisional) and `RebindSnapshotValidation.Evaluate(RebindSnapshotInputs) → RebindDecision`
  (authoritative, against the copied `.rebuild`). `RebindDecision` carries eligible/ineligible + a
  human-readable reason string (surfaced in logs/status). Inputs are plain records (bools,
  strings, versions) — NO I/O in this file; callers gather facts.

**Contract inputs:** contract design §6, all eight numbered conditions. Prefilters: linked
worktree + `!dbExists` + no replacement (Task 2's fold) + registered main-checkout sibling with an
existing `symbols.db` + numeric-triple pin equality + NO standing W8 failure record (any record —
conservative, §7.4) + `MILLER_FULL_REBUILD_INPLACE` unset + `MILLER_WORKTREE_REBIND` not `off`
(env read happens in the caller; the pure input is a bool). Snapshot validation: schema/contract
compatible + `hash_algorithm = blake3` + recorded `root_path` matches the SOURCE root + at least
one committed extraction revision (`ServableFor` alone is NOT sufficient — crash shells pass it) +
`binary_version` numeric equality re-check + recorded `index_level` satisfies the target's
resolved level policy (full satisfies all; symbols satisfies SymbolsOnly/Progressive but NOT
Full). Level changes require a fresh force rebuild, never a rebind.

**File ownership:** Create `src/Miller.Indexing/RebindEligibility.cs`; Test `tests/Miller.Tests/Indexing/RebindEligibilityTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Every go/no-go decision in the rebind path as I/O-free, fast-suite-testable
statics, in the `LeadershipEligibility` style. This is the P3 acceptance item "eligibility as
pure, fast-suite-testable decisions".

**Approach:** One test per condition per stage, plus the crash-shell case: a snapshot input with
`hasCommittedRevision: false` and everything else valid is ineligible with a reason naming the
missing committed revision. If `LeadershipEligibility`'s triple parser is private, extract it to a
shared internal helper (do not change its public behavior).

**Acceptance criteria:**
- [x] Each §6 condition flips the decision independently (table-driven tests, both stages).
- [x] Crash-shell (no committed revision) is ineligible at snapshot validation even though
      `ServableFor`-style facts pass.
- [x] Version comparison uses the numeric triple, proven by a case raw string equality would get
      wrong (e.g. `2.27.0` vs `v2.27.0` spelling divergence).
- [x] Worker-scope verification passes and the change is handed to the lead per
      parallel-lead-commit.

### Task 4: SqliteOnlineBackup page-stepped copier

**Files:**
- Create: `src/Miller.Indexing/SqliteOnlineBackup.cs`
- Test: `tests/Miller.Tests/Indexing/SqliteOnlineBackupTests.cs`

**Interfaces:**
- Consumes: `SQLitePCL.raw` (`sqlite3_backup_init`, `sqlite3_backup_step`, `sqlite3_backup_finish`,
  `sqlite3_backup_remaining`/`pagecount`) via the already-referenced
  `SQLitePCLRaw.bundle_e_sqlite3`; `SqliteReadOnlyAccess` conventions for the source open
  (read-only, `Pooling=false`).
- Produces: `SqliteOnlineBackup.Copy(string sourceDb, string destinationDb, TimeSpan budget, Func<DateTimeOffset> clock, CancellationToken ct) → BackupOutcome`
  where `BackupOutcome` is `Completed | BudgetExhausted | Failed(reason)`. A public
  `static TimeSpan ResolveBudget()` reading `MILLER_REBIND_COPY_BUDGET` (seconds or `TimeSpan`
  format, default 3 minutes — same parsing shape as `MILLER_PROMOTE_RETRY_TIMEOUT`).

**Contract inputs:** contract design §4: page-stepped loop (NOT `Microsoft.Data.Sqlite`'s
`BackupDatabase` — one uncancellable `step(-1)` makes the budget unenforceable); budget checked
between steps; a source write restarting the backup is expected behavior the budget bounds;
zero writes to the source (read-only open, no checkpoint). Destination is the caller-supplied
`.rebuild` path; on `BudgetExhausted`/`Failed` the helper deletes its partial destination trio
before returning.

**File ownership:** Create `src/Miller.Indexing/SqliteOnlineBackup.cs`; Test `tests/Miller.Tests/Indexing/SqliteOnlineBackupTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The bounded, cancellable artifact snapshot: a raw SQLite backup loop stepping N
pages (start at 1024; constant, not configurable) with the wall-clock budget and cancellation
token checked between steps.

**Approach:** Fast tests use small real SQLite files in temp dirs (registry tests already do this
in the fast suite). Prove: a live-writer copy is consistent (write to the source between steps via
a hook seam or small page count, destination still passes `PRAGMA integrity_check`), and budget
exhaustion via an injected clock that jumps past the budget after the first step — no real
waiting. Verify the source file's bytes/mtime are untouched after a copy.

**Acceptance criteria:**
- [x] Copy of a populated DB passes `PRAGMA integrity_check` and row-count equality.
- [x] Budget exhaustion (injected clock) returns `BudgetExhausted`, deletes the partial
      destination trio, and leaves the source byte-identical.
- [x] Source opened read-only: a copy of a write-locked/live source succeeds without writing to it.
- [x] `ResolveBudget` parses seconds and `TimeSpan` spellings and defaults sanely.
- [x] Worker-scope verification passes and the change is handed to the lead per
      parallel-lead-commit.

### Task 5: JulieExtractRunner rebind verb seams

**Files:**
- Modify: `src/Miller.Indexing/JulieExtractRunner.cs`
- Test: `tests/Miller.Tests/Indexing/JulieExtractRunnerRebindTests.cs` (fast, pure seams) and
  `tests/Miller.Tests/Indexing/RebindVerbScaleTests.cs` (`[Trait("Category","Scale")]`, via
  `ScaleTestSupport.RequireJulieServer()`)

**Interfaces:**
- Consumes: the runner's existing argv-builder/`ParseReport`/`Interpret` pure-seam pattern and
  typed-outcome conventions (exit 0 report / 1 failed / 2 usage / 3 incompatible).
- Produces: `JulieExtractRunner.Rebind(string dbPath, string newRoot, CancellationToken ct) → RebindReport`
  (live), plus pure seams `BuildRebindArgs(dbPath, newRoot)` →
  `rebind --root <ABS_ROOT> --db <ABS_DB> --strict-schema --json` and rebind-report parsing
  exposing `previous_root`, `new_root`, `previous_artifact_id`, `new_artifact_id`, `changed`.
  Typed refusals: `fingerprint_mismatch` and `no_committed_revision` (exit 3, map to
  `IncompatibleExtractException` family with the code preserved) and `artifact_changed` (exit 1,
  recoverable — surfaces as a failed-outcome the orchestrator treats as rebind failure, not a
  crash).

**Contract inputs:** julie-extractors `docs/contracts/cli.md` §`rebind` and
`docs/contracts/reports.md` §Rebind Section (v2.27.0): validation order, the same-root success
no-op (`changed: false`, exit 0), the additive top-level `rebind` report object, and that a
refused rebind never creates an artifact (no-CREATE write open).

**File ownership:** Modify `src/Miller.Indexing/JulieExtractRunner.cs`; Test `tests/Miller.Tests/Indexing/JulieExtractRunnerRebindTests.cs` (fast) + `tests/Miller.Tests/Indexing/RebindVerbScaleTests.cs` (Scale)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The subprocess seam for the new verb, following the runner's argv-builder +
report-parser + typed-outcome pattern exactly (`update`/`delete` are the closest precedents).

**Approach:** Fast tests pin argv shape and report parsing against contract-faithful fixture JSON
(carry the extractor's REAL emitted report fields — unfaithful fixtures masked 4 real bridge bugs
in the v2.8.0 work). One Scale test runs the real binary: scan a small fixture tree, copy the
artifact, rebind the copy at a second identical tree, assert the report fields and that a
follow-up non-force scan reports `no_change`.

**Acceptance criteria:**
- [x] Argv builder emits the exact contract argv (absolute paths, `--strict-schema --json`).
- [x] Report parser round-trips all five fields; same-root no-op parses `changed: false`.
- [x] `fingerprint_mismatch`, `no_committed_revision`, `artifact_changed` map to typed outcomes
      preserving the code.
- [x] Scale test proves live rebind + `no_change` follow-up scan on a real artifact copy.
- [x] Worker-scope verification passes and the change is handed to the lead per
      parallel-lead-commit.

### Task 6: RebindBootstrap orchestration + bootstrap wiring

**Files:**
- Create: `src/Miller.Indexing/RebindBootstrap.cs`
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs` (`!dbExists` arm of the scan path;
  plain-bootstrap fallback entry)
- Test: `tests/Miller.Tests/Indexing/RebindBootstrapTests.cs` (fast — sequence/fallback/recording
  logic against seams) and `tests/Miller.Tests/Server/RebindBootstrapScaleTests.cs` (Scale —
  end-to-end with the real binary)

**Interfaces:**
- Consumes: Task 2's disqualification fold; Task 3's two-stage decisions; Task 4's
  `SqliteOnlineBackup.Copy` + `ResolveBudget`; Task 5's `Rebind` runner call;
  `FullRebuildPromotion.PrepareRebuildTarget`/`RebuildDbPathFor`/`Promote`
  (`src/Miller.Indexing/FullRebuildPromotion.cs:74-113`); `ScanGovernorAdmission.TryAcquire`;
  `IScanFailurePolicy.RecordFailure(ScanIntent, int?, int)` + `Evaluate`
  (`src/Miller.Indexing/ScanFailurePolicyStore.cs:21,44,47`); Task 1's
  `FindMainCheckoutByCommonDir`.
- Produces: `RebindBootstrap.TryRebind(...) → RebindBootstrapOutcome`
  (`Promoted | Ineligible(reason) | Failed(stage, reason)`) that `IndexBootstrapService` calls in
  the `!dbExists` arm when eligible; on anything but `Promoted` the existing plain bootstrap scan
  proceeds unchanged. Also the provenance facts Task 7 renders (the promoted artifact carries the
  metadata keys — no extra plumbing beyond promotion itself).

**Contract inputs:** contract design §7 verbatim. Sequence (§7.1): (1)
`PrepareRebuildTarget(liveDb)` at entry AND on every failure exit AND at plain-bootstrap fallback
entry — a dead rebind must never strand a multi-GB `.rebuild` trio; (2) backup-seed under budget,
with a best-effort skip while the source's `scan.progress` heartbeat is fresh; (3) snapshot
validation (Task 3 stage 2) against the `.rebuild` file; (4) `rebind --db <symbols.db.rebuild>
--root <target root>`; (5) non-force scan against the `.rebuild` path at the snapshot's RECORDED
level; (6) `Promote`; (7) normal `UpsertSeen` + `MarkScanned` with refreshed lineage. Recovery
(§7.2): failure before promote deletes staging and falls back; death after promote is SUCCESS —
on a `Promote` exception, probe the live path (root matches + committed revision) before declaring
failure, because `Promote` can throw after the move. Recording (§7.3): W8
`RecordFailure(ScanIntent.IncrementalReconcile, exitCodeOrNull, jobs)`; no new intent. All under
ONE governor admission + the bootstrap writer lease.

**File ownership:** Create `src/Miller.Indexing/RebindBootstrap.cs`; Modify `src/Miller.Server/Hosting/IndexBootstrapService.cs`; Test `tests/Miller.Tests/Indexing/RebindBootstrapTests.cs` (fast) + `tests/Miller.Tests/Server/RebindBootstrapScaleTests.cs` (Scale)

**Serialization required:** Yes

**Dependency reason:** Consumes Tasks 2, 3, 4, 5 contracts; shares `IndexBootstrapService.cs` with Task 2.

**What to build:** The dedicated bootstrap sequence and its wiring: when a fresh linked worktree
opens with an eligible sibling artifact, run copy → validate → rebind → delta-scan → promote
instead of a full extraction; on any failure, clean staging, record under W8, and fall back to the
plain bootstrap scan.

**Approach:** Keep `RebindBootstrap` I/O-orchestration thin over injectable seams (copier, runner,
validator, promotion) so the fast tests drive every branch — including the promote-exception
probe — without a subprocess. The Scale test builds a real main-checkout artifact via
`ScaleTestSupport`, creates a real linked worktree (`git worktree add`), opens it, and asserts:
rebind ran (provenance keys present, `artifact_id` differs from source), byte-identical tree
produced a `no_change` delta, the SOURCE artifact is byte-identical afterward (hash before/after),
and a killed/failed rebind leaves no `.rebuild` debris after the fallback completes. Never invoke
`JulieExtractRunner.Scan(force: true)` anywhere in this path; the delta scan is a non-force scan
argv pointed at the `.rebuild` file with the recorded level.

**Acceptance criteria:**
- [x] Fresh linked-worktree open with an eligible sibling artifact runs rebind, not a full scan
      (Scale test; provenance keys present; source untouched by hash comparison).
- [x] Byte-identical tree → delta scan reports `no_change`.
- [x] Every failure stage (budget exhausted, snapshot invalid, rebind refused, scan failed,
      promote failed-before-move) cleans staging, records under W8 with
      `ScanIntent.IncrementalReconcile`, and falls back to the plain scan (fast tests).
- [x] Promote-exception probe adopts a post-move artifact as success.
- [x] Plain-bootstrap fallback entry runs staging cleanup (debris-free after a simulated dead
      rebind).
- [x] Worker-scope verification passes and the change is handed to the lead per
      parallel-lead-commit.

### Task 7: Provenance surfacing + contract docs

**Files:**
- Modify: `src/Miller.Server/Tools/WorkspaceRender.cs` (`WorkspaceRender` :325+),
  `src/Miller.Dashboard/DashboardData.cs` (workspace detail),
  `docs/contracts/cli-eros-v1.md` (additive `rebound_from` section beside `scan_failure` :205)
- Test: `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`

**Interfaces:**
- Consumes: the three artifact metadata keys (`rebound_from_root`, `rebound_from_artifact_id`,
  `rebound_at`) read through the existing artifact-metadata read path that already serves
  `workspace status` facts; the registry (to resolve the source root to a display id when
  registered).
- Produces: an OPTIONAL additive `rebound_from` object in `workspace status --json` and
  `workspace health --json` — `{ "source_root": ..., "source_workspace": <display id or null>,
  "source_artifact_id": ..., "rebound_at": ... }` — present only when the artifact carries the
  keys; a one-line compact-status rendering ("rebound from `<display id>` at `<rebound_at>`");
  the same facts on the dashboard workspace detail.

**Contract inputs:** contract design §8 provenance surfacing; the `scan_failure` section of
`docs/contracts/cli-eros-v1.md:205` as the additive-conditional-object precedent (document shape,
optionality, and JSON stability the same way). No new MCP tools; no new CLI verbs — this rides
the existing status/health payloads.

**File ownership:** Modify `src/Miller.Server/Tools/WorkspaceRender.cs`, `src/Miller.Dashboard/DashboardData.cs`, `docs/contracts/cli-eros-v1.md`; Test `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch (no file overlap with Task 6; end-to-end JSON assertion runs at branch gate).

**What to build:** Make a rebound workspace say so: status/health JSON, compact status output, and
the dashboard each render the rebind provenance; the Eros-facing contract doc records the additive
object.

**Approach:** Fast tests drive the render from fixture metadata (keys present/absent —
absent renders nothing, no empty object). Follow `scan_failure`'s conditional-object pattern
exactly for JSON shape and doc language. Source display id resolves via the registry when the
source root is registered; otherwise `source_workspace` is null and the raw root still renders.

**Acceptance criteria:**
- [x] `workspace status --json` and `health --json` include `rebound_from` exactly when the
      artifact carries the provenance keys; never an empty object.
- [x] Compact status renders the one-line provenance; dashboard detail shows the same facts.
- [x] `docs/contracts/cli-eros-v1.md` documents the additive object in the `scan_failure` style.
- [x] Worker-scope verification passes and the change is handed to the lead per
      parallel-lead-commit.

---

## Program bookkeeping on completion

After Batch C lands and the branch gate passes, tick the P3 acceptance boxes in
`docs/plans/2026-08-02-worktree-delta-rebind-program.md` (§P3) and the P3 items in
`docs/plans/2026-08-05-rebind-contract-design.md` §9, citing the test evidence. P4 (scale
validation) remains a separate phase and is NOT part of this plan.
