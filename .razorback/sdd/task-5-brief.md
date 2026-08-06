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
- [ ] Argv builder emits the exact contract argv (absolute paths, `--strict-schema --json`).
- [ ] Report parser round-trips all five fields; same-root no-op parses `changed: false`.
- [ ] `fingerprint_mismatch`, `no_committed_revision`, `artifact_changed` map to typed outcomes
      preserving the code.
- [ ] Scale test proves live rebind + `no_change` follow-up scan on a real artifact copy.
- [ ] Worker-scope verification passes and the change is handed to the lead per
      parallel-lead-commit.

