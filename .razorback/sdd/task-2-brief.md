### Task 2: disk preflight + disk-blocked producer

**Files:**
- Create: `src/Miller.Indexing/Semantic/DiskPreflight.cs`
- Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`
- Test: `tests/Miller.Tests/Indexing/DiskPreflightTests.cs`, `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`

**Interfaces:**
- Consumes: Task 1's transition-stamping pattern; `VectorSidecar` consumer already renders `disk-blocked` (P2 B6).
- Produces: `DiskPreflight.Check(path, requiredBytes)` → pure verdict record (ok / blocked with free-bytes fact) with an injectable free-space probe (so tests never depend on the real disk); `converge_pause_state=disk-blocked` stamped when a shadow build or bounded batch is refused for space; cleared when a later preflight passes.

**Contract inputs:** Design §4.4 ("Disk preflight before download") and §5.1 state vocabulary. Estimate `requiredBytes` conservatively from the work list size × observed bytes-per-unit of the current artifact, floor 256 MiB — a stated heuristic, not a contract.

**File ownership:** Create: `src/Miller.Indexing/Semantic/DiskPreflight.cs`; Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`; Test: `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`, `tests/Miller.Tests/Indexing/DiskPreflightTests.cs`

**Serialization required:** Yes

**Dependency reason:** Follows Task 1 in Lane 1 (same files).

**What to build:** Refuse to start a shadow rebuild (and to continue bounded batches) when free disk under `.miller/` cannot hold the projected shadow artifact; surface the refusal as `disk-blocked` with the free/required numbers in the reason, instead of failing mid-build with a corrupt half-artifact. Task 3 reuses `DiskPreflight` before model download.

**Approach:** Pure logic in `Miller.Core`-style (no I/O in the verdict; probe injected — default probe uses `DriveInfo`). Wire the check at `BuildShadowAsync` entry and at each bounded-batch slice boundary. Preflight failure is a hold (RecordError + pause stamp), never an exception.

**Acceptance criteria:**
- [ ] Preflight verdict is pure and unit-tested (blocked/ok boundaries, probe injected).
- [ ] A blocked shadow build stamps `disk-blocked` with free+required bytes in the reason and leaves no `.rebuild`/shadow debris.
- [ ] Recovery (probe reports space) clears the pause on the next wake and the build proceeds.
- [ ] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

