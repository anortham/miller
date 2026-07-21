
## Fix F4 — concurrent `semantic prepare` marker deletion (nonce ownership)

**Status:** COMPLETE. Fix + red-first regression test landed. No git add/commit (parallel-lead-commit).

**Change (src/Miller.Server/Cli/SemanticPrepareCli.cs):**
- Added injected `Func<string> newNonce` (6th ctor seam); `Production()` uses `Guid.NewGuid().ToString("N")`.
- `Run` generates one nonce per invocation, passes it to both `WriteMarker` and `DeleteMarker`.
- `WriteMarker` now writes a 4th field `nonce` alongside model/pid/createdUtc (existing three untouched; Task 4 `SemanticPrepareMarker.TryParse` ignores unknown fields — consumer NOT changed).
- `DeleteMarker(markerPath, nonce)` deletes only if the on-disk marker parses and its `nonce` string equals ours (`OwnsMarker`). Best-effort preserved: IOException/UnauthorizedAccessException/JsonException → leave untouched, never mask exit code.

**Miller-first:** Read SemanticPrepareCli.cs full. Calls into WriteMarker/DeleteMarker are only from `Run`'s try/finally; marker path shared via `MarkerPathFor(millerDir)` — the exact overlap point in the finding.

**Test (tests/Miller.Tests/Server/SemanticPrepareCliTests.cs):**
- New `Prepare_DoesNotDeleteMarker_OwnedByConcurrentInvocation`: first invocation's runner starts a second `SemanticPrepareCli` (different pid 9999 / nonce `second-nonce`) on a thread that writes its marker and blocks; first returns → its finally runs; asserts marker STILL EXISTS carrying second's model/pid/nonce. Then releases second → its own finally deletes (sole-owner success path also exercised). Red-first: without nonce gate, first's unconditional delete drops the marker → File.Exists false → fail.
- `Build` helper gained optional `pid`/`nonce` params; all existing lifecycle tests unchanged and green.

**Verification:**
- `dotnet test --filter FullyQualifiedName~SemanticPrepareCliTests`: 17 passed, 0 failed (class run 229 ms).
- `scripts/test.sh` (fast): 4227 passed, 2 skipped, 0 failed.

**Concerns:**
- Fast-suite wall-clock tripwire fired (44–49s > 30s ceiling) BUT this is machine contention, not this change: load avg 13.5, 14 concurrent dotnet procs from the two other active fix workers building the same solution. Test EXECUTION was 41s and all pass; my added test class is 229 ms. Re-run the fast suite once the parallel workers are idle to get a clean wall-time reading before final gate.
