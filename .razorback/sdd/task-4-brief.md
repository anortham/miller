### Task 4: Carry the julie-extract exit code on IncompatibleExtractException into W8 records

**Files:**
- Modify: `src/Miller.Indexing/IncompatibleExtractException.cs` (add `public int? ExitCode { get; }` + constructor overload; existing constructors unchanged)
- Modify: `src/Miller.Indexing/JulieExtractExceptions.cs` (`ExitCodeOf` `:39` also reads `IncompatibleExtractException.ExitCode`)
- Modify: `src/Miller.Indexing/JulieExtractRunner.cs` (only the rebind exit-3 refusal throw sites: pass exit code 3)
- Test: `tests/Miller.Tests/Indexing/JulieExtractExceptionExitCodeTests.cs` (new)

**Interfaces:**
- Consumes: `JulieExtractException.ExitCodeOf(Exception?)` (`JulieExtractExceptions.cs:39`), the rebind refusal mapping in `JulieExtractRunner.Rebind` (exit 3 = `fingerprint_mismatch`/`no_committed_revision` → `IncompatibleExtractException`).
- Produces: `IncompatibleExtractException.ExitCode` (nullable, additive — every existing construction site compiles unchanged); `ExitCodeOf` returns 3 for rebind refusals, so `ScanFailureJournal` records `exit_code: 3` instead of null.

**Contract inputs:** P3 standing note (progress ledger + P3 morning report): "exit-3 refusals record null exit code in W8 (no Code property on IncompatibleExtractException)". `RebindBootstrap.cs:516` already calls `JulieExtractException.ExitCodeOf(ex)` on the failure path — no change there.

**File ownership:** Modify: `src/Miller.Indexing/IncompatibleExtractException.cs`, `src/Miller.Indexing/JulieExtractExceptions.cs`, `src/Miller.Indexing/JulieExtractRunner.cs` (rebind exit-3 throw sites only). Test: `tests/Miller.Tests/Indexing/JulieExtractExceptionExitCodeTests.cs` (new)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** An additive nullable `ExitCode` on `IncompatibleExtractException`, populated at the rebind exit-3 throw sites in `JulieExtractRunner`, surfaced through `ExitCodeOf` so the W8 journal's `exit_code` field carries 3 for rebind refusals. Do NOT touch other `IncompatibleExtractException` construction sites (schema gate, version gate) — they stay null, which is honest (no subprocess exit is involved there).

**Approach:** Follow the existing exception style in the file. Tests: `ExitCodeOf` returns the code for an `IncompatibleExtractException` built with one, null for one built without, and still works for `JulieExtractException`; plus one test on the runner's rebind refusal mapping if it is reachable without a subprocess (the parse/mapping helpers are internal — use them; if the mapping is only reachable via the real binary, the mapping test is already covered by `JulieExtractRunnerRebindTests` at Scale and the unit tests stop at `ExitCodeOf`).

**Acceptance criteria:**
- [ ] `ExitCodeOf` returns 3 for a rebind-refusal `IncompatibleExtractException` and null for legacy construction sites.
- [ ] No existing construction site changed behavior (build clean, fast suite green).
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode.

---

## Out of scope (recorded, not planned)

- **Sidecar copy/rebind for worktree opens** (the ~200 s search-sidecar build dominating the 457 s open) — a feature with its own design questions (revision-keyed identity across artifact ids), not a finding fix. Needs its own plan if pursued.
- **Failed rebind consumes the W8 slot** — intentional design bias (design doc §7.4).
- **SQLITE_BUSY copy branch untested** — unreachable via the production path by design.
- **`DefaultBootstrapScanLockWait` tuning** — with Task 1 the admission holds shrink to scan length; the 10-minute budget becomes generous rather than starvable. Revisit only if the fleet re-validation still shows waits near the cap.
