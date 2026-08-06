# Task 6 report — RebindBootstrap orchestration + bootstrap wiring

Worktree: `/Users/murphy/source/miller/.claude/worktrees/rebind-p3-miller-wiring`
Branch: `rebind-p3-miller-wiring` · HEAD at start `0d8fb3e7`, at report time `c10e0254` (the lead committed
Task 7 mid-run; my four files sit uncommitted on top of it) · commit SHA: none - parallel-lead-commit

## What I implemented

### `src/Miller.Indexing/RebindBootstrap.cs` (new)

The dedicated bootstrap sequence of design §7, as a thin orchestrator over injectable seams.

Public surface:

- `RebindStage` — `Copy | Validate | Rebind | DeltaScan | Promote` (the steps that can fail; prefilter and
  heartbeat refusals are `Ineligible`, not `Failed`).
- `RebindBootstrapOutcome` — `Result` (`Promoted | Ineligible | Failed`), `Reason`, `Stage?`, `Revision?`,
  `SourceRoot?`, `SourceDisplayId?`.
- `RebindBootstrapRequest` — required facts: `TargetRoot`, `TargetDbPath`, `RegistryDbPath`,
  `RootReplacementDetected`, `TargetLevelPolicy` (ALREADY resolved), `FailurePolicy`, `Jobs`.
- `RebindBootstrapSeams` — every side effect as a delegate. Defaults for `ResolveLayout`, `FindMainCheckout`,
  `ReadArtifactBinaryVersion`, `ReadEnvironmentVariable`, `ReadSourceHeartbeatUtc`, `UtcNow`, `CopySnapshot`,
  `ReadSnapshotInputs`, `PrepareStaging`, `Promote`, `LiveArtifactUsable`; `Rebind` and `RunDeltaScan` are
  `required` because they need the caller's located `JulieExtractRunner`.
- `RebindBootstrapSeams.ReadSnapshotFacts(snapshotDb, sourceRoot, policy)` — the production snapshot reader: ONE
  read-only connection answering `JulieSchemaGate.Verify`, `hash_algorithm`, `root_path`, `binary_version`,
  `index_level`, and `MAX(revision_id) > 0`. Every read failure answers "not a compatible artifact" instead of
  throwing.
- `RebindBootstrap.DiscardStaging(liveDbPath)` — best-effort `PrepareRebuildTarget`, used internally on every
  failure exit and by the plain-bootstrap fallback entry.
- `RebindBootstrap.TryRebind(request, seams, ct)` — the sequence.
- `RebindBootstrap.EnabledEnvVar` (`MILLER_WORKTREE_REBIND`), `RebindBootstrap.SourceScanHeartbeatWindow` (30s).

Sequence, exactly §7.1: kill-switch/env facts → layout + main-checkout sibling → `RebindPrefilter.Evaluate` →
source heartbeat pre-check → `PrepareStaging(liveDb)` → `CopySnapshot` under the resolved budget →
`RebindSnapshotValidation.Evaluate` against the `.rebuild` copy → `Rebind(stagingDb, targetRoot)` → NON-force
delta scan against the staging path at the snapshot's RECORDED level → `Promote(liveDb)` → `Promoted` with the
delta revision (registry `UpsertSeen`/`MarkScanned` with refreshed lineage then happens through the bootstrap's
existing `DidScan` path — step 7 needed no new code).

Recovery, exactly §7.2/§7.3:

- Every failure exit runs `DiscardStaging` then
  `RecordFailure(ScanIntent.IncrementalReconcile, exitCodeOrNull, request.Jobs)`. No new `ScanIntent`; never
  `RootRebind`.
- One `try` spans steps 1–5 with a stage tracker, so an unexpected `IOException`/`SqliteException`/
  `JsonException` at any step is treated as this attempt's failure (clean + record + fall back) instead of
  escaping into the bootstrap. `OperationCanceledException` is deliberately NOT in that set: a shutdown clears
  staging and rethrows without recording.
- `Promote` throwing is probed, not trusted: `LiveArtifactUsable(liveDb, targetRoot)` (default
  `ArtifactRootIdentity.ServableFor` + a committed revision — `ReadBootstrapScanDecision` semantics) adopts a
  post-move artifact as `Promoted`.
- `Changed == false` (same-root no-op) is success and still promotes.

### `src/Miller.Server/Hosting/IndexBootstrapService.cs` (modified)

Two edits, no restructuring:

1. Inside the existing `if (scanDecision.ShouldScan && bootstrapLease is not null)` block, AFTER the governor
   admission and the `failurePolicy.Evaluate(..., bypassBackoff: true)` — so the whole sequence runs under the
   ONE admission and the bootstrap writer lease already held. `IndexLevels.ResolveForWorkspace` is hoisted into
   a local (`levelPolicy`) shared by the rebind attempt and the plain scan. On `Promoted`: `scanned = true`,
   `scanRevision = rebind.Revision`, one info log. Otherwise: a warning naming the stage when `Failed`,
   `RebindBootstrap.DiscardStaging(canonicalDbPath)` at the fallback entry, then the previously-existing plain
   scan verbatim.
2. `private RebindBootstrapOutcome TryRebindFromMainCheckout(...)` — builds the request from bootstrap locals
   (`rootReplaced || persistedRootReplaced` is Task 2's fold, reused not re-derived) and wires the two required
   seams: `runner.Rebind(...)` and `runner.Scan(canonicalRoot, snapshotDb, force: false, jobs, level)` — the
   shared scan chokepoint, so the delta inherits `--jobs`, the invariant ignore file, and supervision paths
   resolved from the staging file's own `.miller` directory. Passes `_shutdown.Token`.

Exact wiring diff shape (unified, elided):

```
                     ScanAttemptDecision attempt = failurePolicy.Evaluate(scanDecision.Intent, bypassBackoff: true);
+                    IndexLevelPolicy levelPolicy = IndexLevels.ResolveForWorkspace(ctx.RegistryDbPath, stableWorkspaceId);
+                    RebindBootstrapOutcome rebind = TryRebindFromMainCheckout(
+                        canonicalRoot, canonicalDbPath, ctx, runner, failurePolicy, attempt, levelPolicy,
+                        rootReplaced || persistedRootReplaced);
+                    if (rebind.Result == RebindBootstrapOutcome.Kind.Promoted)
+                    {
+                        scanned = true; scanRevision = rebind.Revision; _logger.LogInformation(...);
+                    }
+                    else
+                    {
+                        if (rebind.Result == RebindBootstrapOutcome.Kind.Failed) _logger.LogWarning(...);
+                        RebindBootstrap.DiscardStaging(canonicalDbPath);
                         ExtractIndexLevel bootstrapLevel = IndexLevels.LevelForScan(
-                            attempt.EffectiveIntent, newArtifact: !scanDecision.Force,
-                            IndexLevels.ResolveForWorkspace(ctx.RegistryDbPath, stableWorkspaceId));
+                            attempt.EffectiveIntent, newArtifact: !scanDecision.Force, levelPolicy);
                         ExtractReport report = RunRecordedScan(failurePolicy, attempt, () => runner.Scan(...));
                         scanned = true; scanRevision = report.Revision; ... (unchanged)
+                    }
```

## Verification

| Invariant | Scope | Command | Result | Timestamp (UTC) |
|---|---|---|---|---|
| Red first (API defined by tests, no implementation) | task | `dotnet build tests/Miller.Tests` | 4 × CS0246 as expected | 2026-08-05 ~23:5x |
| Every fast branch: happy path, level inheritance, no-op rebind, 6 ineligible reasons, 6 failure stages, promote-after-move adoption, entry staging cleanup | task | `dotnet test --filter "FullyQualifiedName~RebindBootstrapTests"` | 22 passed, 0 failed (73 ms) | 2026-08-06T00:20Z |
| Live end-to-end: real git worktree, real binary, provenance keys, source byte-identical, no_change delta, fallback clears debris | your-class-only (NOT the scale gate) | `dotnet test --filter "FullyQualifiedName~RebindBootstrapScaleTests"` | 3 passed, 0 failed | 2026-08-06T00:31Z |
| Both classes after the final tweaks | task | `dotnet test --filter "FullyQualifiedName~RebindBootstrap"` | 26 passed, 0 failed | 2026-08-06T00:34Z |
| Worker ceiling — whole fast suite unbroken (includes Task 7's landed tests) | branch fast suite | `scripts/test.sh` | 6112 passed, 2 skipped, 0 failed, 28s | 2026-08-06T00:35:58Z |
| Same, re-run after the lead committed Task 7 (`c10e0254`) | branch fast suite | `scripts/test.sh` | 6112 passed, 2 skipped, 0 failed, 29s | 2026-08-06T00:38:15Z |
| Warnings-as-errors clean | branch | `dotnet build Miller.slnx -c Release` | 0 warnings / 0 errors | 2026-08-06T00:36:04Z |

Scale-test observations worth recording:

- The full production bootstrap of a fresh `git worktree add` checkout rebound the main checkout's artifact:
  `root_path` = the worktree root, `rebound_from_root` / `rebound_from_artifact_id` / `rebound_at` present,
  `artifact_id` different from the source's, and the SOURCE `symbols.db` SHA-256 identical before and after.
- The byte-identical worktree's delta scan reported `no_change`.
- Pre-seeded `.rebuild` + `.rebuild-wal` debris was gone after a plain (rebind-ineligible) bootstrap.
- KNOWN GAP from Task 4 (`SQLITE_BUSY`/`SQLITE_LOCKED` copy branch) is still unexercised. I did not find a cheap,
  non-flaky way to hold a real writer on the source for the duration of a page-stepped backup, and the heartbeat
  pre-check deliberately stands rebind down in exactly that state, so the Scale suite cannot reach the branch
  through the production path.

## Files changed

- Created `src/Miller.Indexing/RebindBootstrap.cs`
- Modified `src/Miller.Server/Hosting/IndexBootstrapService.cs` (+95 / -19)
- Created `tests/Miller.Tests/Indexing/RebindBootstrapTests.cs` (22 fast tests)
- Created `tests/Miller.Tests/Server/RebindBootstrapScaleTests.cs` (3 Scale tests)

Nothing else touched. `WorkspaceRender.cs` / `DashboardData.cs` / `cli-eros-v1.md` (Task 7) untouched.

## Miller calls used

| Call | What it confirmed |
|---|---|
| `inspect(target='IndexBootstrapService', depth=overview)` | Class shape, hosted-service constructor rule, the member list I then read directly. Index predates this branch, so the Task 2 members (`DisqualifiesRebind`, `CaptureLineage`) were absent from the index — read at HEAD instead. |
| `inspect(target='FullRebuildPromotion', depth=full)` | `RebuildDbPathFor` / `PrepareRebuildTarget` / `Promote` bodies, and that `Promote` clears sidecars both BEFORE and AFTER the move (the §7.2 probe's justification). |

HEAD/direct reads used where the index is behind this branch (stated explicitly): `RebindEligibility.cs`,
`SqliteOnlineBackup.cs`, `WorkspaceRegistry.cs` (lineage + `FindMainCheckoutByCommonDir`),
`git show HEAD~4 -- JulieExtractRunner.cs` (Task 5's `Rebind`/`BuildRebindArgs`/exit-code routing),
`IndexBootstrapService.cs` (`RunBootstrap`, `DecideBootstrapScan`, `ReadBootstrapScanDecision`,
`RunRecordedScan`, `AcquireBootstrapScanAdmission`), `ScanGovernor.cs`, `ScanFailurePolicyStore.cs`,
`IndexLevels.cs`, `ExtractSupervision.cs`, `GitWorktreeLayout.cs`, `ArtifactRootIdentity.cs`,
`BootstrapScanLockScaleTests.cs` (the real-bootstrap Scale fixture pattern I reused).

## API-shape evidence

- `SqliteOnlineBackup.Copy(source, dest, budget, clock, ct) → BackupOutcome` with `Result`/`FailureReason` and
  self-deleting partials — `src/Miller.Indexing/SqliteOnlineBackup.cs:101,148`.
- `JulieExtractRunner.Rebind(dbPath, newRoot, ct) → RebindReport`; exit 3 → `IncompatibleExtractException`
  (no `Code` property), exit 1 → `JulieExtractFailedException` (`ExitCode` 1) — `JulieExtractRunner.cs:~740`,
  `JulieExtractExceptions.cs:47,56`.
- `JulieExtractRunner.Scan(root, db, force: false, jobs, level)` non-force branch runs
  `Run(BuildScanArgs(absDb, absRoot, force, jobs, ignoreFiles, supervision, level))` with supervision resolved
  from `absDb`'s directory — `JulieExtractRunner.cs:511-534`, `ExtractSupervision.cs:79-96`. This is why the
  delta scan points at the `.rebuild` path through the same chokepoint rather than a hand-built argv.
- `WorkspaceRegistry.FindMainCheckoutByCommonDir` filters `git_is_linked = 0` and compares with
  `ArtifactRootIdentity.Matches`, so the caller MUST pass `WorkspaceLineage.CanonicalizeCommonDir` output —
  `WorkspaceRegistry.cs:380-404,604`.
- `IScanFailurePolicy.RecordFailure(ScanIntent, int?, int)` / `Read()` — `ScanFailurePolicyStore.cs:47,50`.
- `ScanGovernor` is NOT re-entrant on one thread (`ScanGovernor.cs:73-78`), which forced the design decision
  below: the rebind takes NO admission of its own.

## Judgment calls

- `src/Miller.Server/Hosting/IndexBootstrapService.cs:584-590` — the attempt is wired INSIDE the existing
  admission rather than taking its own. Chose this over an admission inside `RebindBootstrap` because the
  governor throws on same-thread re-entry (`ScanGovernor.cs:73-78`), and §7.1 asks for exactly one admission
  covering copy + verb + delta scan.
- `RebindBootstrap.cs:365-381` — the rebind attempt is gated by the PREFILTER's own facts
  (`TargetArtifactExists`, `RootReplacementDetected`) rather than by an extra `!scanDecision.Force` check in the
  wiring. Every force intent implies one of those two facts, so a second condition would be a duplicate rule
  that could drift.
- `RebindBootstrap.cs:365-374` — the kill switch and an existing target artifact short-circuit the git-layout
  probe and the registry open respectively. Both refusals rank above the sibling conditions in
  `RebindPrefilter`, so the reported reason is unchanged, and `MILLER_WORKTREE_REBIND=off` stays a genuine
  zero-work guarantee.
- `RebindBootstrap.cs:327` — `SourceScanHeartbeatWindow = 30s`, per the brief's guidance. Documented cost: julie
  does not delete `scan.progress` when a scan finishes, so a worktree opened within 30s of the SOURCE's last
  scan completing falls back to a full extraction. Chose the design's mtime rule over a two-sample liveness
  probe (which would be more precise but adds a delay and departs from the written contract). The Scale tests
  backdate the heartbeat to reach the rebind path, which is itself the evidence for this cost.
- `RebindBootstrap.cs:~455` — snapshot-validation failure is `Failed` (records W8), not `Ineligible`. §7.3 lists
  snapshot validation among the steps that record a null exit code.
- `RebindBootstrap.cs:~500` — `RecordSuccess(ScanIntent.IncrementalReconcile)` on `Promoted`, mirroring
  `RunRecordedScan`. It is a near-no-op (the prefilter already refuses when a record stands) but keeps the
  invariant "every completed build reports to the policy".
- `RebindBootstrap.cs:~420` — one `try` with a stage tracker across steps 1–5 instead of per-step catches. An
  unexpected I/O failure gets the same honest treatment as a julie refusal, and a rebind attempt can never fail
  the bootstrap.
- `RebindBootstrapSeams.IsInPlaceRebuildEnabled` uses `Trim() == "1"` — the spelling both readers that honor the
  hatch use (`JulieExtractRunner.ForceScanInPlace`, `IndexLevels.FromEnvValues`), rather than "any value set".

## Self-review

- No writes to the source artifact anywhere: the only source paths passed are `source.IndexDbPath` (read-only
  backup) and `source.CanonicalRoot` (heartbeat stat). The Scale test asserts the source hash byte-for-byte.
- No `Scan(force: true)` on this path; the delta is non-force against the staging file at the snapshot's
  recorded level (`IsSymbolsLevel(recorded) ? Symbols : Full`, so an absent key means full-level semantics and
  no `--level` flag).
- `PrepareRebuildTarget` runs at attempt entry, on every failure exit, on cancellation, and at the plain-scan
  fallback entry — the four places §7.1/§7.2 name.
- The fast tests assert `!File.Exists(targetDb)` alongside every failure, so "failed before promote" really does
  leave the live path absent.
- `Miller.Core`'s zero-I/O rule is untouched (this is `Miller.Indexing`), and `RebindEligibility` stays pure —
  all fact gathering lives in `RebindBootstrap`/its seams.

## Concerns

1. **Exit 3 records a null exit code.** `IncompatibleExtractException` carries no `Code` property, so a
   fingerprint-mismatch refusal records `exitCode: null` in the W8 journal. Per the brief I did not add surface
   outside my ownership. The record's job here is the suppression marker, so nothing behaves wrongly; forensics
   just cannot distinguish exit 3 from a copy/validation failure. A `Code` on `IncompatibleExtractException`
   would be the additive fix if telemetry ever needs it.
2. **The 30s heartbeat window suppresses rebind right after a source scan finishes** (see judgment calls). In a
   fleet where the main checkout's leader reconciles often, this may fire more than the design anticipated. It
   is one constant (`RebindBootstrap.SourceScanHeartbeatWindow`) if evidence says to shrink it.
3. **The `SQLITE_BUSY`/`SQLITE_LOCKED` copy branch remains untested** (Task 4's known gap), and the heartbeat
   pre-check makes it hard to reach from the production path.
4. **A failed rebind spends this workspace's one W8 slot**, so the plain scan that follows starts with a
   standing record. That is design §7.4's deliberate conservative bias, and the record clears on the plain
   scan's success — but a rebind failure followed by a scan failure will read as a two-failure streak in the
   journal.
