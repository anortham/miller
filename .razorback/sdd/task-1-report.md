# Task 1 report — Registry lineage columns + sibling lookup

**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/rebind-p3-miller-wiring`
**Branch:** `rebind-p3-miller-wiring`
**HEAD at start:** `b0d96b75`
**Commit SHA:** none — `parallel-lead-commit`

## What I implemented

### Schema (`src/Miller.Indexing/WorkspaceRegistry.cs`)

Four additive nullable columns on `workspaces`, in the `CREATE TABLE` head for fresh registries and in a
migration for existing ones:

- `git_common_dir TEXT` — the repository's shared git directory, canonicalized before storage.
- `git_is_linked INTEGER` — `0` for a main checkout, `1` for a linked worktree.
- `git_dir TEXT` — the `WorkspaceRootIdentity.GitDir` half of the checkout generation.
- `git_dir_created_at TEXT` — the `WorkspaceRootIdentity.GitDirCreatedAtUtc` half, ISO-8601 `"O"` round-trip,
  the same format the existing `*_at` columns use.

### Migration

`EnsureLevelPolicyColumn` / `AddLevelPolicyColumnToleratingConcurrentAdder` became
`EnsureAdditiveColumns` / `AddColumnToleratingConcurrentAdder(connection, column, type)`, driven by a static
`AdditiveColumns` array of `(Name, Type)` pairs that now lists `level_policy` plus the four lineage columns.
Each column keeps the original two-step shape: a `pragma_table_info` fast-path probe, then a
duplicate-column-tolerant `ALTER TABLE ... ADD COLUMN`. The old test
`AddLevelPolicyColumn_ToleratesAConcurrentAdderWinningTheRace` became
`AddAdditiveColumn_ToleratesAConcurrentAdderWinningTheRace` and exercises both a text and a lineage column.

### Row

`WorkspaceRegistryRow` gained four trailing optional members — `GitCommonDir`, `GitIsLinked` (`bool?`),
`GitDir`, `GitDirCreatedAtUtc` — after `LevelPolicy`, so every existing positional construction still compiles.

The three `SELECT` sites (`List`, `GetUnderLock`, the new lookup) now share one `RowColumns` constant, because
their column order has to agree with `ReadRow`'s ordinals and three hand-maintained copies would drift.

### `UpsertSeen`

A trailing optional `WorkspaceLineage? lineage = null` parameter, after `seenAtUtc`. Semantics:

- `null` leaves all four stored values untouched. Implemented with a `$has_lineage` flag and a
  `CASE WHEN $has_lineage = 1 THEN excluded.x ELSE workspaces.x END` per column in the `ON CONFLICT` update.
- A non-null lineage replaces all four values together. I deliberately did **not** use per-column
  `COALESCE(excluded.x, workspaces.x)`: `GitDirCreatedAtUtc` is legitimately null on a filesystem with no
  birth time, and `COALESCE` would then let a previous generation's timestamp survive beside a new `git_dir` —
  a hybrid identity that never existed.
- `GitCommonDir` is canonicalized at write time through `WorkspaceLineage.CanonicalizeCommonDir`, so a caller
  cannot store a raw `GitWorktreeLayout` path. Documented in the parameter's XML doc.

### `WorkspaceLineage`

A record in `WorkspaceRegistry.cs`: `(string GitCommonDir, bool IsLinkedWorktree, string? GitDir,
DateTimeOffset? GitDirCreatedAtUtc)`, plus one static `CanonicalizeCommonDir(string)` that both the write path
and the lookup caller use, so the two sides cannot disagree on spelling.

`CanonicalizeCommonDir` uses `PathCanonicalizer.CanonicalizeFile(absolute, absolute)` rather than
`CanonicalizeRoot`. Both resolve every existing symlink component identically for a directory that exists, but
`CanonicalizeRoot` throws `DirectoryNotFoundException` when the directory is gone, and registering a workspace
must not fail because a git directory disappeared between layout resolution and the upsert. Named arguments
make it explicit that the base-directory argument is inert (the path is already absolute).

### `FindMainCheckoutByCommonDir`

`public WorkspaceRegistryRow? FindMainCheckoutByCommonDir(string canonicalCommonDir)`. SQL narrows to
`git_common_dir IS NOT NULL AND git_is_linked = 0` with a deterministic `ORDER BY workspace_id`, then the
path comparison runs in C# via `ArtifactRootIdentity.Matches` — the exact function the design doc §5 names,
so the platform rule (`OrdinalIgnoreCase` on Windows/macOS, `Ordinal` on Linux) and the Windows
verbatim-prefix strip come from one place instead of being restated. Returns the first matching row, or null.

## Verification

| | |
|---|---|
| Scope label | `worker-red-green` |
| TDD command | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~WorkspaceRegistryTests"` |
| Result | **Passed — 34 passed, 0 failed, 0 skipped, 367 ms** |
| Ceiling command | `scripts/test.sh` (full fast suite) |
| Result | **Passed — 6054 passed, 0 failed, 2 skipped, 6056 total.** The script's wall-clock tripwire fired (103s vs a 30s ceiling) — see "Fast-suite result". |
| Timestamp | 2026-08-05 |

Red-first was observed: before implementation the filtered run failed to compile with `CS0117` /`CS1061`
/`CS0246` on `AddColumnToleratingConcurrentAdder`, `WorkspaceRegistryRow.GitCommonDir`, `GitIsLinked`,
`GitDir`, `GitDirCreatedAtUtc`, and `WorkspaceLineage` — the right reason (API absent), not an assertion typo.

### Invariants the new tests prove

| Test | Invariant |
|---|---|
| `Open_CreatesSchemaAndConfiguresWalNormalSyncAndBusyTimeout` (extended) | A fresh registry carries all four lineage columns with the declared SQLite types, all nullable. |
| `Open_APreLineageRegistryGainsTheLineageColumns_AndReadsNullLineage` | A pre-levels registry DB opens cleanly, reads null lineage, and accepts a lineage write afterwards — the migration is not a fresh-DB-only path. |
| `UpsertSeen_RoundTripsLineageIncludingTheGitDirCreationTimestamp` | All four values survive a write and a re-read, including sub-second creation-timestamp ticks — `WorkspaceRootIdentity.IsReplacement` compares that timestamp for equality, so a lossy round-trip would read as a replaced checkout on every restart. |
| `UpsertSeen_StoresAMainCheckoutLineageWithoutACreationTimestamp` | A half-known identity (`GitDirCreatedAtUtc` null) stores as null rather than as a default value. |
| `UpsertSeen_CanonicalizesTheStoredCommonDirThroughASymlinkedAncestor` | The stored common dir is symlink-resolved, so a `/var`→`/private/var`-style layout still matches a symlink-canonicalized registry root. |
| `UpsertSeen_WithoutLineage_LeavesPreviouslyStoredLineageIntact` | A lineage-free upsert does not erase identity another process persisted. |
| `FindMainCheckoutByCommonDir_ReturnsTheMainCheckoutAndIgnoresLinkedWorktreesAndOtherRepos` | The lookup picks the non-linked row of the right repo among a linked sibling, another repo's main checkout, and a lineage-free row. |
| `FindMainCheckoutByCommonDir_ReturnsNull_WhenOnlyLinkedWorktreesShareTheCommonDir` | No worktree-to-worktree sourcing. |
| `FindMainCheckoutByCommonDir_OnAnEmptyRegistry_ReturnsNull` | The empty-registry path returns null rather than throwing. |
| `FindMainCheckoutByCommonDir_AppliesPlatformPathComparisonSemantics` | Path case follows `ArtifactRootIdentity.ComparisonFor` — a match on Windows/macOS, a miss on Linux. |
| `FindMainCheckoutByCommonDir_OnADisposedRegistry_Throws` | The new read honours the same disposal guard as every other public method. |

### Fast-suite result

The full fast suite could not be built for a stretch of this task because sibling P3 workers were mid-red-phase
in the SAME assemblies: `JulieExtractRunnerRebindTests.cs` / `RebindVerbScaleTests.cs` referenced
`JulieExtractRunner.BuildRebindArgs`, `ParseRebindReport`, `Rebind`, and `RebindReport` before Task 5 wrote
them, `JulieExtractRunner.cs` referenced `RebindReport` before it existed, and Task 4's
`SqliteOnlineBackup.cs:149` failed the Release build with `CS0162 Unreachable code detected`. None of these are
in my owned files. I polled the build until it cleared, then ran the suite three times:

1. **6053 passed, 1 failed** — the failure was
   `SqliteOnlineBackupTests.Copy_BudgetElapsedBetweenSteps_ReportsExhaustedAndDeletesThePartialDestination`
   (Task 4, timing-sensitive, not mine).
2. **6054 passed, 0 failed, 2 skipped** — that test passed on the next run.
3. **6054 passed, 0 failed, 2 skipped** — confirmed.

**The wall-clock tripwire fired on the clean runs: 103s against a 30s ceiling.** This is machine contention
from the parallel batch, not a slow test I introduced:

- Load average was 18.5 with 15 concurrent `dotnet` processes while the suite ran.
- The FIRST full run in this same session, on the same code, reported `Duration: 30 s` for the same 6056 tests.
- The slowest individual tests are all pre-existing (`CanarySearchTests` 951 ms,
  `MillerExtractContractTests` 928 ms, `WorkspaceIndexProviderTests` 925 ms); no rebind-program test appears
  near the top.
- `WorkspaceRegistryTests` alone — all 34, including my 10 — runs in **367 ms**.

The lead should re-run `scripts/test.sh` on a quiet machine once every worker has reported, to confirm the
tripwire is clear.

## Miller calls used

| Call | What it confirmed |
|---|---|
| `inspect target='src/Miller.Indexing/WorkspaceRegistry.cs'` | The class's method inventory and line ranges: `Open :32-75`, `UpsertSeen :77-123`, `SetLevelPolicy :247-265`, and that no lineage surface existed. |
| `inspect target='WorkspaceRegistryRow' depth=full` | The record is a `sealed record` at `src/Miller.Indexing/WorkspaceRegistryRow.cs:16` — **not** in `WorkspaceRegistry.cs` — with 10 members ending in `LevelPolicy`, and 141 references across the codebase. |
| `trace target='UpsertSeen' mode=refs` | 17 exact call sites, all positional, in `CliDispatch.cs:3348`, `WorkspaceTool.cs:1297`, and 15 test sites. This is why lineage is a trailing optional parameter rather than an inserted one. |
| `inspect target='PathCanonicalizer' depth=overview` | `CanonicalizeRoot` (throws on a missing directory), `CanonicalizeFile(canonicalRoot, path)` (tolerates a non-existent tail), `StripWindowsVerbatimPrefix`. |
| `inspect target='ArtifactRootIdentity' depth=overview` | `Matches(string? recordedRootPath, string canonicalRoot)` and `ComparisonFor(bool, bool)` — the exact comparison the design doc names, and a shape that takes a nullable recorded value, so the stored column feeds it directly. |

## API-shape evidence

- **`UpsertSeen` signature** — proven by `inspect` (`public WorkspaceRegistryRow UpsertSeen(string workspaceId,
  string displayId, string canonicalRoot, string indexDbPath, WorkspaceRegistryState state = Ready,
  DateTimeOffset? seenAtUtc = null)`) and by `trace mode=refs` showing every call site passes positionally up to
  `seenAtUtc`. A trailing optional parameter breaks none of them; the build confirms it.
- **Row record shape** — `inspect target='WorkspaceRegistryRow' depth=full`, plus a direct read of
  `WorkspaceRegistryRow.cs` for the trailing `string? LevelPolicy = null` that establishes the
  optional-trailing-member convention.
- **Migration pattern** — read directly at `WorkspaceRegistry.cs:337-370` (`pragma_table_info` probe +
  duplicate-column-tolerant `ALTER`); Miller listed the symbols, the exact text came from the file.
- **`PathCanonicalizer` entry point** — `inspect depth=overview` listed `CanonicalizeRoot` and
  `CanonicalizeFile`; the throwing/tolerant distinction came from their XML docs in the file.
- **`GitWorktreeLayout` / `WorkspaceRootIdentity`** — read directly (small pure files); their member names
  (`CommonDir`, `IsLinkedWorktree`, `GitDir`, `GitDirCreatedAtUtc`) are what `WorkspaceLineage` mirrors.

## Judgment calls

- **`src/Miller.Indexing/WorkspaceRegistryRow.cs` — edited, though the brief listed only
  `WorkspaceRegistry.cs`.** The brief requires four nullable members on `WorkspaceRegistryRow`, and Miller
  proved that record lives in its own file, not in `WorkspaceRegistry.cs` as the file list assumed. The edit is
  four trailing optional parameters and nothing else. Flagging it because a sibling task may touch the same
  file.
- **`WorkspaceRegistry.cs:~500 — `CanonicalizeCommonDir` uses `CanonicalizeFile` over `CanonicalizeRoot`.**
  Identical resolution for an existing directory; `CanonicalizeRoot` additionally throws when the directory is
  gone, which would turn a vanished git dir into a failed workspace registration. One code path, no exception
  control flow.
- **`WorkspaceRegistry.cs` `UpsertSeen` — canonicalization at write time, not required of the caller.** The
  brief allowed either; write-time enforcement means no caller can store a raw path. The lookup keeps the
  brief's `canonicalCommonDir` parameter name and the caller (Task 6) is directed to
  `WorkspaceLineage.CanonicalizeCommonDir` by the XML doc, so both sides run the same function.
- **`FindMainCheckoutByCommonDir` — C# comparison via `ArtifactRootIdentity.Matches`, not SQL `COLLATE
  NOCASE`.** `PruneDuplicatePathRowsUnderLock` does compare paths in SQL, so the brief permitted either.
  SQLite's `NOCASE` is ASCII-only while `ComparisonFor` yields full-Unicode `OrdinalIgnoreCase`, and `Matches`
  also strips the Windows verbatim prefix — reusing it means zero drift from the semantics §5 names. The
  registry holds tens of rows, so the scan cost is irrelevant.
- **`WorkspaceLineage` has no `RootIdentity` convenience property.** I wrote one, then removed it: Task 2's
  consumption rule rebuilds `WorkspaceRootIdentity` from the ROW, not from this record, so it was dead surface.
- **`RowColumns` shared constant.** Three `SELECT`s must agree with `ReadRow`'s ordinals; adding a fourth
  hand-maintained copy for the lookup invited exactly the drift bug that reads a column into the wrong member.
- **`ORDER BY workspace_id` in the lookup.** One repo should have one main checkout, but an unordered scan
  would pick nondeterministically if it ever had two.

## Self-review

- **Acceptance criteria** — all four met. Migration on an existing DB with null reads: covered. Exact
  round-trip including the creation timestamp: covered. Main checkout among mixed rows, ignoring linked rows,
  other repos, and platform comparison: covered. Null-lineage preservation: covered.
- **Quality** — no narration comments added; the one existing block comment above the migration was updated
  because the lines were already being changed. Test bodies carry zero comments. The single pre-existing
  comment inside `SetLevelPolicy_StoresClearsAndSurvivesUpsertSeen` was left alone (not a line I changed).
- **YAGNI** — the delivered surface is exactly: four columns, four row members, one `WorkspaceLineage` record
  with one static, one lookup method. No generic lineage framework, no query-by-repo helper, no
  `SetLineage` mutator.
- **Tests assert meaningful values** — the round-trip test asserts against a timestamp carrying odd ticks and
  checks both the returned row and a re-read row; the canonicalization test asserts equality with an
  independently canonicalized real path AND inequality with the symlinked input, so it cannot pass by storing
  the input verbatim; the platform test asserts a null on Linux rather than skipping.
- **Duplication removed** — `Open_APreLevelsRegistryGainsTheLevelPolicyColumn` now shares the
  `WriteLegacyPreLevelsSchema()` helper with the new migration test instead of carrying its own inline DDL.

## Concerns

1. **Shared-assembly collisions during the parallel batch.** Tasks 4 and 5 were mid-red-phase in
   `Miller.Indexing` and `Miller.Tests` while I verified, so the suite was uncompilable for stretches through
   no fault of any single task. The lead should re-run `scripts/test.sh` once all workers report.
2. **`WorkspaceRegistryRow.cs` is outside my declared ownership** but had to change (see judgment calls). If
   Task 2 also edits it, the lead should reconcile.
3. **The lookup's contract depends on the caller canonicalizing.** A raw path passed to
   `FindMainCheckoutByCommonDir` fails silently — it returns null, so a rebind degrades to a plain bootstrap
   scan rather than doing anything wrong, but Task 6 must call `WorkspaceLineage.CanonicalizeCommonDir` first.
   The XML doc says so; the type system does not enforce it.
4. **No test covers two non-linked rows sharing one `git_common_dir`.** It should not happen (one main checkout
   per repository), and the `ORDER BY` makes the pick deterministic if it ever does.

## Files changed

- `src/Miller.Indexing/WorkspaceRegistry.cs` — schema head, `RowColumns`, `AdditiveColumns`, generalized
  migration, `UpsertSeen` lineage parameter and SQL, `FindMainCheckoutByCommonDir`, `ReadRow` ordinals,
  `WorkspaceLineage` record.
- `src/Miller.Indexing/WorkspaceRegistryRow.cs` — four trailing optional record members.
- `tests/Miller.Tests/Indexing/WorkspaceRegistryTests.cs` — extended schema assertion, renamed concurrent-adder
  test, 10 new tests, two shared helpers (`MakeDirectory`, `WriteLegacyPreLevelsSchema`), `SkipIfNoSymlinks`.
