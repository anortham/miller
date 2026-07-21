
## Fix F1 — retained rollback generation GC'd immediately (HIGH)

**Status: DONE — both suites green.**

### Change
- `src/Miller.Indexing/Semantic/VectorGenerationManager.cs`
  - Added `void Touch(string path)` to the internal `IVectorGenerationFiles` seam.
  - `SystemVectorGenerationFiles.Touch` = `Retry(() => File.SetLastWriteTimeUtc(path, DateTime.UtcNow))` — same bounded-backoff retry wrapper as `Move`/`Delete`.
  - In `Promote`, incompatible branch: call `_files.Touch(retainedPath)` immediately after `_files.Move(ActivePath, retainedPath)`. Retention age is now stamped at promotion instead of inherited from the superseded artifact's mtime (`File.Move` preserves mtime), so an idle-then-incompatible-upgrade workspace no longer retains a generation already outside its soak window.
- `tests/Miller.Tests/Indexing/VectorGenerationManagerTests.cs`
  - `FakeGenerationFiles`: added `Touch` (records path in `Touched`, stamps `_times[path] = TouchTime`), plus `Touched` list and controllable `TouchTime` (default `Now`).
  - New regression test `Promote_Incompatible_StampsRetentionTimeSoAnIdleWorkspaceKeepsItsRollbackGeneration`: pre-existing active file mtime = `Now-30d`; after incompatible promote, asserts touch happened on the retained trio's main path, `Retained()` reports `RetainedAt == promotedAt`, and `PlanGarbageCollection` at `Now = promotedAt` classifies it `WithinSoakWindow` (not `Deleted`). Verified RED before the Promote wiring (`Assert.Contains` on empty `Touched`), GREEN after.

### Miller-first
- `inspect VectorGenerationManager.Promote depth=full` — confirmed the incompatible branch (`RetainedPathFor` → optional `DeleteTrio` → `_files.Move(ActivePath, retainedPath)`), callees (`Move ×2`, `MakeSelfContained`, `ClearAllPools`), and 18 dependents (`VectorConvergeService.RunShadowRebuildAsync`/`Promote`, `FullRebuildPromotion`, `JulieExtractRunner.Scan`). API shapes (`IVectorGenerationFiles`, `Retained()` deriving `RetainedAt` from `LastWriteTime`, `Classify` soak rule) read directly from source.

### Verification
- `dotnet test --filter FullyQualifiedName~VectorGenerationManagerTests` → Passed 33, Failed 0, Skipped 0.
- `scripts/test.sh` (fast) → Passed 4227, Skipped 2, Failed 0; wall time 19s (< 30s ceiling) on a quiet machine.
- `Miller.Indexing` builds 0 warnings / 0 errors.

### Concerns
- First two `scripts/test.sh` runs tripped the 30s wall-clock ceiling (43s/46s) purely from CPU contention — the other two fix workers were running full suites concurrently (load ~12–16 on 24 cores, ~13 dotnet/testhost procs). Re-run on a quiet machine came in at 19s. No logic regression; my added test is in-memory sub-ms.
- No `git add`/`git commit` performed (parallel-lead-commit mode). Files left staged for the lead: the two owned files above.
