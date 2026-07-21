
## Fix F2 — Unix disk preflight probes wrong volume

**Status: DONE.**

Miller-first: `inspect DiskPreflight.cs` was unavailable (MCP inspect not needed — read the file directly; the class is a single 104-line file). Reviewed `ProbeAvailableFreeBytes`, `Check`, and the ctor doc.

**Change (`src/Miller.Indexing/Semantic/DiskPreflight.cs`):**
- `ProbeAvailableFreeBytes` now probes the nearest-existing ancestor directly: on non-Windows `new DriveInfo(fullProbe)` (Unix DriveInfo maps the path to its containing mount, so `/Volumes/...` and `/mnt/...` preflight against their real volume); on Windows keeps `Path.GetPathRoot` (drive-letter mount granularity).
- Fault-to-negative (-1) contract and the exception filter left exactly as-is.
- Updated the ctor XML doc line to state the probe reads the volume that actually contains the path (directory/mount on Unix, drive letter on Windows).

**Tests (`tests/Miller.Tests/Indexing/DiskPreflightTests.cs`):**
- `DefaultProbe_OnADeepExistingDirectory_ReturnsNonNegativeFreeSpace` — default probe on a real deep tempdir returns >= 0.
- `DefaultProbe_OnAMissingDeepPath_WalksUpToAnExistingAncestor` — missing deep path walks up to an existing ancestor and returns >= 0, not unknown.
- No fabricated mount-point fixtures.

**Verification:**
- `dotnet test --filter FullyQualifiedName~DiskPreflightTests` → 12 passed, 0 failed.
- `scripts/test.sh` (fast) → 4225 passed, 2 skipped, **1 failed**.

**Concern:** the single fast-suite failure is `VectorGenerationManagerTests.Promote_Incompatible_StampsRetentionTimeSoAnIdleWorkspaceKeepsItsRollbackGeneration` — in `VectorGenerationManagerTests.cs`, a file the other fix worker is actively editing (confirmed modified in `git status`). Not in my ownership and unrelated to the DiskPreflight change (different subsystem). My scoped files build clean and all DiskPreflight tests pass.
