### Task 2 report: Bootstrap lineage capture + replacement consumption rule

**Status:** DONE
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/rebind-p3-miller-wiring`
**Branch:** `rebind-p3-miller-wiring`
**HEAD at start:** `504b1a2f` — **HEAD after commit:** `0d8fb3e7`

## What I implemented

1. **The pure fold (Task 6 calls this):**
   `internal static bool DisqualifiesRebind(WorkspaceRegistryRow? stored, WorkspaceRootIdentity current)`
   (`src/Miller.Server/Hosting/IndexBootstrapService.cs:977`). True only when the row exists AND
   `WorkspaceRootIdentity.IsReplacement(new WorkspaceRootIdentity(stored.GitDir, stored.GitDirCreatedAtUtc), current)`.
   No known-ness logic of its own: `IsReplacement` already answers false when either side is unknown, so the null
   row is the only case the fold adds.

2. **One lineage capture per bootstrap run:**
   `internal static WorkspaceLineage? CaptureLineage(string canonicalRoot)` (`:995`) resolves
   `GitWorktreeLayout.Resolve` once, captures `WorkspaceRootIdentity.Capture` once, and returns
   `WorkspaceLineage(layout.CommonDir, layout.IsLinkedWorktree, identity.GitDir, identity.GitDirCreatedAtUtc)`.
   Null for a non-git root, which `UpsertSeen` reads as "leave the stored lineage alone".
   `internal static WorkspaceRootIdentity IdentityOf(WorkspaceLineage? lineage)` (`:1008`) projects the
   generation half so the run needs no second sample.

3. **Consumption rule wired into the EXISTING escalation** (`RunBootstrap`, `:454-464` and `:491-496`):
   the capture and the stored-row read happen right after `Directory.CreateDirectory(millerDir)`, before any
   registry write; `ReadDecision` now folds `rootReplaced || persistedRootReplaced` into the same
   `EscalateForReplacedRoot` call. No second escalation path, no new service. A detected persisted replacement
   also logs one warning naming the root.

4. **Lineage persisted after the decision:** `BootstrapRunResult` carries `WorkspaceLineage? Lineage`;
   `PublishBoundWorkspace` passes it to both arms (`MarkRegistryScanned` / `RegisterBootstrapWorkspace`), so the
   stored generation post-open is the current one.

## Verification

| Field | Value |
| --- | --- |
| Scope label | worker-red-green |
| Command (class) | `dotnet test Miller.slnx -c Release --no-build --filter "FullyQualifiedName~BootstrapReplacedRootTests"` |
| Result | **Passed — 19 passed, 0 failed, 74 ms** |
| Command (worker ceiling) | `scripts/test.sh` (fast suite, `Category!=Scale`) |
| Result | **Passed — 6069 passed, 0 failed, 2 skipped, 27 s (30 s wall)** |
| Build | `dotnet build Miller.slnx -c Release` — 0 warnings, 0 errors |
| Timestamp | 2026-08-05 |

Sibling Task 4 (`SqliteOnlineBackupTests`) was already committed by the time I ran the ceiling suite; it compiles
and passes. No red-only-in-Task-4 noise to report.

**Red state:** the new tests could not compile at HEAD `504b1a2f` —
`git show HEAD:src/Miller.Server/Hosting/IndexBootstrapService.cs | grep -c "DisqualifiesRebind\|CaptureLineage\|IdentityOf"`
returns `0`. Implementation followed.

### Invariant each new test proves

- `APersistedGenerationDifferentFromTheCurrentOneDisqualifiesRebind` — a different creation timestamp at the same
  admin path is a replacement.
- `APersistedAdminPathDifferentFromTheCurrentOneDisqualifiesRebind` — a different admin path is a replacement
  (worktree re-added under another name).
- `ThePersistedGenerationOfTheSameCheckoutDoesNotDisqualifyRebind` — an unchanged generation is not.
- `AnUnregisteredWorkspaceDoesNotDisqualifyRebind` — a null row is missing evidence, never a replacement.
- `ARowMissingEitherHalfOfTheIdentityDoesNotDisqualifyRebind` (3 cases) — a pre-lineage row (either half null) is
  missing evidence.
- `AnUnreadableCurrentLayoutDoesNotDisqualifyRebind` — unknown CURRENT identity, both as
  `WorkspaceRootIdentity.Unknown` and as `IdentityOf(null)`, never escalates.
- `APersistedReplacementEscalatesAReuseDecisionWithNoLiveMonitor` — the persisted path alone turns a reuse
  decision into `ShouldScan` + `ScanIntent.RootRebind`; no `WorkspaceRootPresenceMonitor` involved.
- `AnUnchangedPersistedGenerationLeavesTheReuseDecisionAlone` — the reuse decision survives untouched
  (`LoadedExisting`), so the rule costs nothing in the normal case.
- `CaptureLineageReadsTheCommonDirAndGenerationOfANormalCheckout` — capture yields common dir, `IsLinkedWorktree`
  false, and a KNOWN identity.
- `CaptureLineageOfANonGitRootIsNull` — non-git root captures nothing and reads as unknown.
- `BootstrapRegistrationPersistsTheCapturedLineage` — bootstrap registration writes all four columns
  (common dir canonicalized), and the round-tripped row does not disqualify against its own generation.
- `AScanRefreshesThePersistedLineageToTheCurrentGeneration` — the pre-open row disqualifies, the post-scan row
  does not: the stored generation post-open is the current one.
- `ARegistrationWithoutLineageLeavesTheStoredGenerationUntouched` — the error path's lineage-free `UpsertSeen`
  keeps the stored generation, so a failed bootstrap still owes the rebuild next open.
- The four pre-existing escalation tests are unchanged and still pass.

## Files changed

- `src/Miller.Server/Hosting/IndexBootstrapService.cs` (+96 / −18)
- `tests/Miller.Tests/Server/BootstrapReplacedRootTests.cs` (+236 / −18)

Nothing else. Commit `0d8fb3e7` staged exactly those two paths.

## Miller calls used and what each confirmed

| Call | Confirmed |
| --- | --- |
| `inspect target='src/Miller.Server/Hosting/IndexBootstrapService.cs' limit=10` | file symbol list with line numbers; `BootstrapScanDecision :180`, `BootstrapRunResult :90` |
| `inspect target='WorkspaceRootIdentity' depth=full` | full body of `IsKnown`, `Capture`, `IsReplacement` — both-sides-known rule verified in source, not inferred |
| `search`/`trace` fallback | the worktree index is a pre-Task-1 generation: it has no `WorkspaceLineage`, no lineage row members, and no `FindMainCheckoutByCommonDir`. `trace target='EscalateForReplacedRoot'` and symbol lookups for the new API returned nothing usable, so I read the committed code at HEAD instead (`git show HEAD:src/Miller.Indexing/WorkspaceRegistry.cs`, direct `Read` of the working tree). Reported per the brief's stale-index note. |

## API-shape evidence

- **`UpsertSeen` lineage parameter** — `src/Miller.Indexing/WorkspaceRegistry.cs:107-114` (working tree at HEAD):
  trailing `WorkspaceLineage? lineage = null`; `$has_lineage` CASE arms at `:144-151` prove null leaves all four
  stored values untouched; `:164` proves `GitCommonDir` is canonicalized at write time.
- **Row members** — `src/Miller.Indexing/WorkspaceRegistryRow.cs:26-30`: `GitCommonDir`, `GitIsLinked` (`bool?`),
  `GitDir`, `GitDirCreatedAtUtc`, all trailing optional.
- **`IsReplacement` semantics** — `WorkspaceRootIdentity.cs:69-79` via `inspect depth=full`: `if (!before.IsKnown || !after.IsKnown) return false;`
  then admin-path comparison (OS-aware) OR timestamp inequality. Both sides covered — the fold adds no
  known-ness logic.
- **Decision fold** — `IndexBootstrapService.cs:907-926` (`DecideBootstrapScan`) and `:938-946`
  (`EscalateForReplacedRoot`, `ScanIntentPolicy.Strongest` with `RootRebind`).
- **Existing capture sites** — `WorkspaceRootIdentity.Capture` is called only from
  `src/Miller.Indexing/WorkspaceRootPresenceMonitor.cs:41,50,79`, constructed at
  `src/Miller.Server/Hosting/IndexerService.cs:425`. See the plan mismatch below.
- **`WorkspaceLineage`** — `WorkspaceRegistry.cs:588-611`, plus `CanonicalizeCommonDir` at `:604`.
- **Registry read** — `WorkspaceRegistry.Get(string workspaceId)` at `:335` returns `WorkspaceRegistryRow?`.

## Plan mismatch (reported per brief)

The brief and the task text say `GitWorktreeLayout.Resolve` + `WorkspaceRootIdentity.Capture` are "already
resolved nearby for the presence monitor — reuse, don't re-probe". They are **not** resolved anywhere in
`IndexBootstrapService`. The only capture site is `WorkspaceRootPresenceMonitor`, constructed in
`IndexerService.cs:425` — a different file, not mine, and a service that starts AFTER bootstrap binds. Threading
its sample into bootstrap would have meant editing `IndexerService` and inverting the startup order.

Smallest plan-consistent path taken: capture ONCE per bootstrap run inside `RunBootstrap` (`CaptureLineage`), and
reuse that single sample for the comparison, the escalation, and both publish-time `UpsertSeen` calls. The
"capture once per bootstrap" intent is honored; the "reuse an existing capture" premise did not exist.

## Judgment calls

1. **`MarkRegistryScanned` gained an overload, not an optional parameter.**
   `WorkspaceRegistryScanPublisher.cs:14` binds it as a `Func<WorkspaceContext, string, long?, WorkspaceRegistryRow>`
   method group, which an optional parameter cannot satisfy (CS1503). Since that file is not mine, I kept the
   3-parameter shape as a thin overload delegating to the 4-parameter one (`:1486-1495`). `RegisterBootstrapWorkspace`
   has no method-group caller, so it took a plain trailing optional parameter.
2. **The error and missing paths do NOT refresh lineage.** `MarkRegistryError` / `MarkRegistryMissing` keep their
   signatures. This is deliberate, not an omission: if bootstrap FAILS on a replaced root, no artifact was rebuilt,
   so the rebuild is still owed and the stale stored generation must keep escalating the next open. Refreshing
   there would silently discharge the replacement. Pinned by
   `ARegistrationWithoutLineageLeavesTheStoredGenerationUntouched`.
3. **The stored-row read is not wrapped in a catch.** A registry that cannot be opened already fails the same run
   at publish time (`RegisterBootstrapWorkspace` opens it unconditionally), so a defensive catch would only move
   the failure, not prevent it.
4. **`GitWorktreeLayout.Resolve` runs twice per capture** — once in `CaptureLineage`, once inside
   `WorkspaceRootIdentity.Capture`. Removing the second would need a `Capture(GitWorktreeLayout)` overload in
   `WorkspaceRootIdentity.cs`, a file this task does not own. Cost is two lexical path probes on one directory,
   once per bootstrap.
5. **One warning log on detection**, matching `RebootstrapForReplacedRoot`'s existing warning, so the rebuild has
   a stated cause in the log rather than appearing as an unexplained force scan.

## Self-review findings

- The live-monitor path (`rootReplaced: true`) and the persisted path can both be true in the same run — the
  monitor detects the replacement, and the registry row still holds the old generation until publish. The OR
  applies `EscalateForReplacedRoot` exactly once, so there is no double escalation and no intent drift.
- After a replaced-root bootstrap, publish refreshes lineage, so the NEXT open does not re-escalate. Verified by
  `AScanRefreshesThePersistedLineageToTheCurrentGeneration`.
- Workspaces registered before Task 1 have NULL `git_dir`; they never escalate. Correct per the missing-evidence
  rule, and they gain lineage on their first successful open after this change.
- `DateTimeOffset` round-trips exactly through the registry ("O" format, `RoundtripKind` parse), so a stored
  generation compares equal to the one just captured — otherwise every open would read as a replacement. Pinned by
  the persist and refresh tests.
- Zero comments in tests; the two production comments state why (the pre-write ordering, the method-group overload),
  never what.

## Concerns

- `DisqualifiesRebind` currently has ONE production consumer (the escalation). Task 6 must call the same fold for
  the rebind prefilter (§6 item 1) rather than re-deriving the condition — two derivations would drift.
- The fold is named for Task 6's use, so at the escalation call site it reads slightly indirect. The local is named
  `persistedRootReplaced` to keep the bootstrap line readable.
- Filesystems with no birth time degrade `GitDirCreatedAtUtc` to a change/modify time (documented on
  `WorkspaceRootIdentity`). On such a filesystem an ordinary metadata touch of the admin dir could read as a
  replacement and cost one rebuild. Pre-existing behavior, not introduced here — but persisting the sample means it
  can now fire across restarts too.

## Exact signature for Task 6

```csharp
internal static bool DisqualifiesRebind(WorkspaceRegistryRow? stored, WorkspaceRootIdentity current)
```

Supporting seams in the same class:

```csharp
internal static WorkspaceLineage? CaptureLineage(string canonicalRoot)          // null when no git layout
internal static WorkspaceRootIdentity IdentityOf(WorkspaceLineage? lineage)     // Unknown when lineage is null
```

All three are `internal static` on `Miller.Server.IndexBootstrapService`, pure except `CaptureLineage`'s two
filesystem probes, and covered by the fast suite.
