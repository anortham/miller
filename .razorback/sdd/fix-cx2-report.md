# fix-cx2 — SQLite KNN failures escape the fail-open boundary

**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-p3`
**Branch:** `worktree-semantic-p3` (base HEAD `511602d`)
**Status:** FIXED, verified, committed. Not pushed.

## Finding

`VectorStore.Search` executed SQLite commands with no exception wrapping. A corrupt, missing, or
unreadable vector table threw a raw `Microsoft.Data.Sqlite.SqliteException`, which the retrieval arm's
fail-open catch (`SemanticSearchArm.cs:182` — `VectorStoreException or InvalidOperationException or
IOException`) does not cover. The raw exception propagated past the arm and broke the lexical search it is
only supposed to augment, violating the ADR-0003 guarantee that lexical-only output stays byte-identical.

## Fix

Fixed at the storage boundary, not by widening the arm's catch. The contract is that `VectorStore` speaks
`VectorStoreException`; the arm is correct as written.

Added a private `Guard(operation, work)` pair to `VectorStore` that catches `SqliteException` and rethrows
as `VectorStoreException` with the original as inner, matching the file's existing `ReadMeta` pattern.

Audited every public member for the same gap and guarded all of them, not just `Search`:

- `Search`, `MappedUnits`, `MappedCount`, `ResolveGlob`, `TableColumns` (reads)
- `Meta`, `SetMeta`, `Upsert`, `CommitBatch` (writes)
- `Create`'s schema/meta write, and `OpenConnection` (so `Create`/`Open`/`ReadMetaAt` on an unreadable
  file also surface as `VectorStoreException`)
- `AllMeta`/`ReadIdentity` were already covered via `ReadMeta`

Span-taking members (`Search`, `Upsert`) quantize to a `byte[]` before entering the guarded lambda, since
a `ReadOnlySpan<sbyte>` cannot be captured. `VectorLiteral()` is likewise hoisted out so its own
`VectorStoreException` is not re-wrapped.

## Tests (TDD — red first)

Red run with the fix reverted: **3 failed, 0 passed**. The arm-level failure stack trace was exactly the
reported escape path — `SqliteException: 'no such table: symbol_vectors'` through
`VectorStore.Search` → `VectorStoreSearchPort.Search` → `SemanticSearchArm.Recall` → out past
`QueryAsync`'s catch and into the caller.

- `VectorStoreTests.Search_AgainstAMissingVectorTable_FailsAsAVectorStoreException`
- `VectorStoreTests.MappedUnits_AgainstAMissingMappingTable_FailsAsAVectorStoreException`
- `SemanticSearchArmScaleTests.AnArtifactWhoseVectorTableIsGone_DegradesWithAReasonInsteadOfThrowingSqlite`

Each builds a real artifact in a temp dir, drops the table through a second raw connection with the pinned
extension loaded, and asserts the typed failure (store level) or empty-plus-reason (arm level). All three
are in classes already `[Trait("Category","Scale")]` because they load the native extension, and funnel
through `SqliteVecTestSupport.RequireExtension()`, so they skip rather than fail on an unrestored machine.

## Verification

- `dotnet test --filter "FullyQualifiedName~VectorStoreTests|FullyQualifiedName~SemanticSearchArm"` —
  **34 passed, 0 failed, 0 skipped** (extension present, so the new Scale tests really ran).
- `scripts/test.sh` — 4158 passed, 2 skipped, **1 failed**: `IndexerServiceScanTests.
  StartAsync_WhenNotLeader_DoesNotCreateOpsOrRunStartupScan`. Foreign: a Miller.Server file I do not own,
  and it **passes in isolation** on retry. Load-induced flake with 4 fix workers running concurrently.
- `dotnet build src/Miller.Indexing` — 0 warnings, 0 errors.

## Commit

`45d1254` — owned files only (`VectorStore.cs`, `VectorStoreTests.cs`, `SemanticSearchArmTests.cs`).
`HybridSearchTests.cs` and the Miller.Server files staged by parallel workers were deliberately excluded
via a pathspec commit. Not pushed.

## Concerns

- The suite ran 2m07s against its <30s tripwire, purely from concurrent-worker CPU contention. Worth a
  clean re-run at the re-gate once the workers are done.
- `VectorSidecar.TryOpen` is the arm's artifact gate and classifies failures ahead of this path; it was not
  in scope here, but it is the other place a raw SQLite fault could surface. It already catches
  `VectorStoreException`, which the `OpenConnection` guard now makes reliable for corrupt-file opens.
