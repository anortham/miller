# Rust CT Impact — Miller `tests[]` + Live Eros Gate Implementation Plan

> **For Hermes:** This is a plan-only handoff. Use the `executing-plans` / implementation-worker lane for code. Marco does not implement production code from this card.

**Goal:** A non-test Rust source edit in `~/source/julie-extractors` makes Miller return a non-empty `impact.tests[]`, then Eros CT enqueues `scope=impacted` with `selected` far smaller than the enrolled 3003-case workspace.

**Architecture:** Keep the fix Miller-side unless live evidence proves the extractor contract is wrong. Miller already owns the public `impact --json --from-index-revision N --from-artifact-id ID` contract and partitions reached graph nodes by `symbols.is_test`; Eros consumes only the public CLI JSON and maps `tests[]` to provider-managed CT cases by source path + test name. julie-extractors changes are conditional, not the default path.

**Tech Stack:** .NET 10 / C# for Miller and Eros, Rust `julie-extractors`, Miller CLI `1.4.3+216de3ea3b36` or the locally built candidate, Eros CT hub.

**Status:** LOCKED for implementation. No Miller tag, julie-extractors tag, Eros pin bump, or release-prep push until the live gate below passes.

---

## Source documents and inspected evidence

Read first / current epic context:
- Eros epic note: `/Users/murphy/source/eros/docs/plans/2026-07-05-rust-ct-impact-single-release.md`
- Prior live acceptance: `/Users/murphy/source/eros/docs/findings/2026-07-05-revision-delta-acceptance.md`

Inspected Miller paths:
- `src/Miller.Indexing/SqliteSymbolReader.cs`
  - Reads `symbols.is_test` by column name and passes it into `IndexedSymbol.IsTest`.
- `src/Miller.Indexing/IndexedSymbol.cs`
  - Carries `bool IsTest` as the language-agnostic test classifier.
- `src/Miller.Indexing/MillerRepositoryIndex.cs`
  - Builds graph nodes with the `IndexedSymbol.IsTest` flag.
- `src/Miller.Server/Tools/ImpactTool.cs`
  - `Run(...)` partitions reached nodes into `impacted` vs `tests` with `symbol.IsTest ? tests : impacted`.
  - `RenderIndexRevisionDelta(...)` uses the same partition for the CT delta envelope.
- `src/Miller.Server/Cli/CliDispatch.cs`
  - `impact --from-index-revision` is an exclusive delta mode; when the complete delta has changed paths it loads the symbol index + graph and renders the delta envelope.
- `src/Miller.Server/Cli/CliCapabilities.cs`
  - Advertises `impact_index_revision_delta` in `features`.
- `tests/Miller.Tests/Server/ImpactToolTests.cs`
  - Current source already contains Rust synthetic unit contracts around lines 819-916. Treat those as the regression floor, not live acceptance.

Inspected Eros paths:
- `src/Eros.Miller/MillerBridge.Refresh.cs`
  - `ImpactDeltaAsync(...)` passes `--from-index-revision` and optional `--from-artifact-id`; `Raw` carries the full Miller payload including `tests[]`.
- `src/Eros.Hub/ContinuousTesting/HubMillerImpactSource.cs`
  - Capability-gates `impact_index_revision_delta`, maps raw `tests[]` into `ContinuousTestImpactedTest`, and degrades safely on unavailable deltas.
- `src/Eros.ContinuousTesting/ContinuousTestImpactSelector.cs`
  - `AddImpactedTestEvidence(...)` matches Miller test path + method name to provider-managed cases and emits `impacted_test` evidence.
  - `impacted_test` is in the exoneration allowlist.
- `src/Eros.ContinuousTesting/ContinuousTestDaemonQueue.cs`
  - Logs `ct enqueue ... scope=impacted ... selected=<n> stale=<n>`.
- `tests/Eros.ContinuousTesting.Tests/ContinuousTestImpactSelectorTests.cs`
  - Already covers `ImpactedTests` narrowing without canonical source->test linkage.
- `tests/Eros.Hub.Tests/HubMillerImpactSourceTests.cs`
  - Already covers mapping raw Miller `tests[]` through the hub impact source.

Live facts captured while planning:
- `~/.local/bin/miller version` returned `1.4.3+216de3ea3b36`.
- `~/.local/bin/miller capabilities --json` included `features: ["impact_index_revision_delta"]`.
- `miller impact --workspace-id /Users/murphy/source/julie-extractors --changed-path crates/julie-extractors/src/base/framework_structural_facts/axum.rs --limit 50` already reports one likely test: `crates/julie-extractors/src/tests/base.rs:test_create_identifier_basic_call`.

---

## Architecture Quality

**Affected modules:**
- Miller read/index/impact path: `SqliteSymbolReader`, `IndexedSymbol`, `MillerRepositoryIndex`, `ImpactTool`, `CliDispatch`, `CliCapabilities`, `RevisionDeltaReader`.
- Eros CT path: `MillerBridge.Refresh`, `HubMillerImpactSource`, `ContinuousTestRevisionPoller`, `ContinuousTestImpactSelector`, `ContinuousTestDaemonQueue`.
- julie-extractors only if the live DB shows Rust test functions are not emitted with `is_test=1` or relationships from tests to source are absent.

**Caller-facing interface:**
- Miller public CLI JSON: `miller impact --json --from-index-revision N --from-artifact-id ID` must emit a typed delta envelope with `delta_status`, `changed_paths`, `impacted[]`, and `tests[]`.
- Eros public behavior: CT hub logs `scope=impacted selected=<small>` and `tests status --json` reports green without a full 3003-case run.

**Depth/locality check:**
- The preferred path is local to Miller classification/partitioning and Eros JSON consumption. Do not add new MCP tools, Eros private SQLite reads, or extractor schema churn unless live evidence forces it.

**Test surface:**
- Miller unit/CLI tests prove Rust `IsTest` partitioning and delta envelope `tests[]`.
- Eros unit tests prove raw `tests[]` survives the bridge and narrows provider-managed cases.
- Live gate proves the end-to-end path on the real julie-extractors CT workspace.

**Seams/adapters:**
- Use existing public CLI contract as the only seam. If a julie-extractors fix is required, keep the artifact schema stable if possible; only rev/pin if the emitted contract must change.

**Rejected shortcuts:**
- No Miller release tag before live acceptance.
- No accepting a synthetic unit pass as proof of CT narrowing.
- No forcing Eros workspace-scope and calling it safe.
- No fake `tests[]` data in Eros tests that bypasses Miller live behavior as the final gate.
- No re-enabling the `julie` monolith CT workspace in this epic.

**Architecture risk:** Medium. The unit path is straightforward, but the release decision depends on live index-revision behavior, Rust graph edges, and Eros provider inventory matching the Miller-reported test names.

---

## Implementation plan

### Task 1: Prepare branches and freeze the release rule

**Objective:** Ensure implementation happens on isolated branches and nobody tags/pins early.

**Files:**
- Read: `/Users/murphy/source/miller/AGENTS.md`
- Read: `/Users/murphy/source/eros/AGENTS.md`
- No production file changes in this task.

**Steps:**
1. In Miller:
   ```bash
   cd /Users/murphy/source/miller
   git status --short --branch
   git switch -c rust-ct-impact-single-release
   ```
   If the branch already exists, switch to it only after verifying it has no unrelated work.
2. In Eros:
   ```bash
   cd /Users/murphy/source/eros
   git status --short --branch
   git switch -c rust-ct-impact-single-release
   ```
3. Record the release freeze in the implementation notes: no `git tag`, no `release: prepare`, no `MinimumMillerVersion` bump until the live gate passes.
4. Leave the current `julie` monolith CT workspace disabled; this epic uses `~/source/julie-extractors` only.

**Verification:**
- Both repos have clean or intentionally documented worktrees.
- The implementation notes explicitly say `no tags until live gate passes`.

---

### Task 2: Lock Miller Rust `tests[]` contracts before changing code

**Objective:** Make the Rust source→test path executable before touching implementation.

**Files:**
- Modify if missing/incomplete: `tests/Miller.Tests/Server/ImpactToolTests.cs`
- Do not modify production code in this task.

**Required tests:**
1. `Run_RustAttributedTestFunction_ReachedByGraph_ClassifiesAsTest_NonTestHelperExcluded`
   - Fixture symbols:
     - Source: `parse_config`, Rust, path `crates/config/src/parser.rs`, `IsTest: false`.
     - Test: `test_parse_config_rejects_empty_input`, Rust, path `crates/config/src/tests/parser_tests.rs`, `IsTest: true`.
     - Helper: `make_fixture_config`, same test file, `IsTest: false`.
   - Edges:
     - test → source, kind `calls`.
     - helper → source, kind `calls`.
   - Expected JSON:
     - `tests[]` contains `test_parse_config_rejects_empty_input` with the test file path.
     - `impacted[]` contains `make_fixture_config`.
     - `impacted[]` does not contain the test function.
2. `RenderIndexRevisionDelta_RustAttributedTestFunction_ClassifiesAsTest`
   - Calls `ImpactTool.RenderIndexRevisionDelta(...)` with changed path `crates/config/src/parser.rs`.
   - Expected JSON:
     - `delta_status` remains `complete`.
     - `tests[]` contains the Rust test function.
     - non-test helper remains in `impacted[]`.

**Steps:**
1. Check whether the current tests already exist. If yes, do not duplicate them.
2. If they do not exist, add the fixture and tests above.
3. Run the targeted tests:
   ```bash
   cd /Users/murphy/source/miller
   dotnet test tests/Miller.Tests/Miller.Tests.csproj \
     --filter "FullyQualifiedName~ImpactToolTests" \
     --logger "console;verbosity=normal"
   ```
4. If the tests pass before any production change, record that Miller already has the synthetic Rust classification path and move to the live gate. Do not invent a production diff just to make the branch look busy.
5. If they fail, proceed to Task 3.

**Verification:**
- `ImpactToolTests` passes.
- Test output is saved in the implementation handoff.

---

### Task 3: Fix Miller only if the Task 2 contracts fail

**Objective:** Repair the smallest Miller-side link that prevents Rust reached tests from landing in `tests[]`.

**Files likely to change, in preferred order:**
- `src/Miller.Indexing/SqliteSymbolReader.cs`
- `src/Miller.Indexing/IndexedSymbol.cs`
- `src/Miller.Indexing/MillerRepositoryIndex.cs`
- `src/Miller.Server/Tools/ImpactTool.cs`
- `src/Miller.Server/Cli/CliDispatch.cs`
- Tests in `tests/Miller.Tests/Server/ImpactToolTests.cs`
- CLI/delta tests if the CLI path is the failing link:
  - `tests/Miller.Tests/Server/Cli/ImpactRevisionDeltaCliTests.cs` or the current equivalent in the repo.

**Do not change julie-extractors yet.** The contract says `symbols.is_test` is already language-agnostic. First prove whether Miller is dropping or mispartitioning the bit.

**Debug checklist:**
1. Confirm `SqliteSymbolReader` selects `is_test` and constructs `IndexedSymbol(..., IsTest: isTest)`.
2. Confirm `MillerRepositoryIndex.Build(...)` carries `symbol.IsTest` into `GraphNode`.
3. Confirm both impact paths partition on `symbol.IsTest`:
   - `ImpactTool.Run(...)`
   - `ImpactTool.ReachFromChangedPaths(...)` used by `RenderIndexRevisionDelta(...)`.
4. Confirm `CliDispatch.ImpactIndexRevisionDelta(...)` loads both index and `SqliteSymbolGraphIndex` when `changedPaths.Count > 0`.
5. Confirm JSON rendering uses `WriteReachedArray(w, tests)` under the `tests` property.

**Implementation rule:**
- Prefer a one-line Miller fix at the broken link. Do not add path/name heuristics for Rust tests unless live extractor data proves `is_test` is absent. Heuristics can create false `tests[]` and false exoneration.

**Verification:**
```bash
cd /Users/murphy/source/miller
dotnet test tests/Miller.Tests/Miller.Tests.csproj \
  --filter "FullyQualifiedName~ImpactToolTests|FullyQualifiedName~ImpactRevisionDeltaCliTests|FullyQualifiedName~RevisionDeltaReaderTests" \
  --logger "console;verbosity=normal"
scripts/test.sh
```

Expected:
- Targeted tests pass.
- Full Miller fast suite passes under the wrapper.

---

### Task 4: Only if live evidence proves extractor data is wrong, patch julie-extractors

**Objective:** Keep extractor churn out of the release unless the Rust artifact itself is missing the needed facts.

**Trigger for this task:**
- Miller source/unit code is correct, but live `impact --from-index-revision ...` returns empty `tests[]`, and inspection shows Rust `#[test]` / `#[tokio::test]` functions in the live extract do not have `is_test=1`, or the expected Rust test→source relationship edges are not emitted.

**Files to inspect/change in julie-extractors:**
- Rust test detection / symbols:
  - Search under `crates/julie-extractors/src/rust/` and `crates/julie-extractors/src/test_calls.rs`.
- Rust relationship extraction:
  - Search under `crates/julie-extractors/src/rust/relationships*.rs` and related language modules.
- Rust fixtures/tests:
  - `crates/julie-extractors/src/tests/rust/...` if present.
  - Add minimal regression fixtures only for missing Rust facts.

**Rules:**
1. Do not change artifact schema unless a stable existing column cannot represent the fact.
2. If schema or pinned extractor version changes, this becomes a three-repo bundle:
   - julie-extractors release.
   - Miller pin bump via `scripts/julie-pins.json` and restore script.
   - Eros Miller pin/version bump after Miller release.
3. If only extractor logic changes with the same schema, still treat it as a release prerequisite before Miller can consume it in packaged form.

**Verification:**
- Rust extractor tests pass.
- A fresh local extract of `~/source/julie-extractors` shows Rust tests with `is_test=1` and relevant relationships.
- Return to Task 5 and re-run the live Miller gate.

---

### Task 5: Run the live Miller gate on `julie-extractors` before any tag

**Objective:** Prove the actual enrolled Rust workspace produces non-empty `tests[]` from a real non-test `.rs` edit.

**Workspace:** `/Users/murphy/source/julie-extractors`

**Preferred probe file:**
- `crates/julie-extractors/src/base/framework_structural_facts/axum.rs`

Rationale: direct current-impact probing already reports a likely test for this non-test source path: `crates/julie-extractors/src/tests/base.rs:test_create_identifier_basic_call`.

**Steps:**
1. Capture a fresh base revision and artifact id:
   ```bash
   export MILLER=/Users/murphy/.local/bin/miller
   export JULIE=/Users/murphy/source/julie-extractors

   cd "$JULIE"
   git status --short --branch

   "$MILLER" refresh --workspace-id "$JULIE" --json --wait > /tmp/julie-base-refresh.json
   ```
2. Extract these two fields from `/tmp/julie-base-refresh.json`:
   - `revision`
   - `artifact_id`

   Use `jq` if available; otherwise use Python on the saved local file, not a pipe from Miller output.
3. Make a reversible comment-only edit to the probe file on the implementation branch:
   ```bash
   printf '\n// CT impact acceptance probe; remove before final commit.\n' \
     >> crates/julie-extractors/src/base/framework_structural_facts/axum.rs
   ```
4. Refresh Miller after the edit:
   ```bash
   "$MILLER" refresh --workspace-id "$JULIE" --json --wait > /tmp/julie-after-refresh.json
   ```
5. Run the delta impact from the captured base:
   ```bash
   "$MILLER" impact \
     --workspace-id "$JULIE" \
     --json \
     --from-index-revision "$BASE_REVISION" \
     --from-artifact-id "$BASE_ARTIFACT_ID" \
     --limit 100 > /tmp/julie-rust-impact.json
   ```
6. Acceptance checks on `/tmp/julie-rust-impact.json`:
   - `delta_status == "complete"`.
   - `changed_paths` includes `crates/julie-extractors/src/base/framework_structural_facts/axum.rs`.
   - `tests` length is greater than zero.
   - At least one `tests[]` row has a Rust `.rs` test path and a non-empty `name`.
7. Remove the probe edit before any commit:
   ```bash
   git checkout -- crates/julie-extractors/src/base/framework_structural_facts/axum.rs
   git status --short
   ```

**Failure handling:**
- If `delta_status` is `unavailable`, stop. Diagnose artifact-id/base revision mismatch; do not tag.
- If `tests[]` is empty but direct `miller impact --changed-path ...` has tests, diagnose `CliDispatch.ImpactIndexRevisionDelta(...)` / graph loading.
- If both direct impact and delta impact have empty `tests[]`, return to Task 3 or Task 4 depending on whether live extractor facts have `is_test=1` and relationship edges.

**Verification artifact:**
- Save the relevant command output snippets into the final Eros findings doc update in Task 8.

---

### Task 6: Keep Eros wiring minimal; only patch if the live hub does not narrow

**Objective:** Eros should consume Miller's `tests[]` without new code-fact surfaces.

**Expected current behavior:**
- `MillerBridge.ImpactDeltaAsync(...)` keeps the raw payload.
- `HubMillerImpactSource.MapImpactedTests(...)` maps Miller `tests[]` rows using `symbol_id`, `file`/`path`, `name`, `line`, `hop`.
- `ContinuousTestImpactSelector.AddImpactedTestEvidence(...)` matches `Path` to provider-managed `metadata.source_path` and `Name` to test `name`, qualified name, or selector trailing segment (`.` and `::` supported).

**Patch only if live hub evidence fails despite Task 5 passing.** Likely minimal patch sites:
1. `src/Eros.Hub/ContinuousTesting/HubMillerImpactSource.cs`
   - If Miller emits a field spelling not accepted by `MapImpactedTest(...)`, add that key and cover it in `HubMillerImpactSourceTests`.
2. `src/Eros.ContinuousTesting/ContinuousTestImpactSelector.cs`
   - If Rust provider selectors differ from Miller names, extend `TestNameMatches(...)` narrowly and add a Rust selector fixture in `ContinuousTestImpactSelectorTests`.
3. `src/Eros.Miller/MillerBridge.Refresh.cs`
   - Only if `Raw` stops carrying `tests[]`; otherwise leave it alone.

**Tests if patched:**
```bash
cd /Users/murphy/source/eros
dotnet test tests/Eros.Hub.Tests/Eros.Hub.Tests.csproj \
  --filter "FullyQualifiedName~HubMillerImpactSourceTests" \
  --logger "console;verbosity=normal"
dotnet test tests/Eros.ContinuousTesting.Tests/Eros.ContinuousTesting.Tests.csproj \
  --filter "FullyQualifiedName~ContinuousTestImpactSelectorTests" \
  --logger "console;verbosity=normal"
```

**Verification:**
- Existing or patched Eros tests prove `ImpactedTests` flows from raw Miller JSON into selected CT test case ids.

---

### Task 7: Run the live Eros hub gate on the enrolled CT workspace

**Objective:** Prove the running Eros hub narrows `julie-extractors` CT to impacted scope.

**Workspace id:** `workspace:30a0bd2590c5639fc50fdc3c`

**Steps:**
1. Build Eros and start/restart the hub with the candidate Miller binary:
   ```bash
   cd /Users/murphy/source/eros
   dotnet build

   export EROS_MILLER_BINARY=/Users/murphy/.local/bin/miller
   dotnet run --project src/Eros.Cli/Eros.Cli.csproj -- hub restart --timeout 30
   ```
   If implementing with a locally built Miller candidate, point `EROS_MILLER_BINARY` to that candidate binary instead of `~/.local/bin/miller`.
2. Confirm CT projects are present for the enrolled workspace:
   ```bash
   dotnet run --project src/Eros.Cli/Eros.Cli.csproj -- \
     tests projects workspace:30a0bd2590c5639fc50fdc3c --json
   ```
3. Trigger the same reversible non-test Rust edit from Task 5 and let the hub poll, or force the hub to observe the new revision using the existing CT poll path. Do not manually enqueue workspace-scope as the proof.
4. Watch the hub log:
   ```bash
   tail -f /Users/murphy/.eros/logs/hub-$(date +%Y-%m-%d).log
   ```
5. Acceptance log line:
   ```text
   ct enqueue workspace=workspace:30a0bd2590c5639fc50fdc3c ... scope=impacted revision=<rev> selected=<small> stale=<about 3003>
   ```
   `selected` must be far smaller than 3003. A single-digit value is expected for the `axum.rs` probe if the provider inventory contains the Miller-reported test.
6. After the run completes, query CT status:
   ```bash
   dotnet run --project src/Eros.Cli/Eros.Cli.csproj -- \
     tests status --workspace-id workspace:30a0bd2590c5639fc50fdc3c --json
   ```
7. Remove the probe edit from julie-extractors before committing/releasing.

**Failure handling:**
- `scope=workspace`: Eros did not consume the changed delta; inspect `HubMillerImpactSource` reason logs (`no_capability`, `no_delta_base`, `delta_status_unavailable`, `to_revision_mismatch`, `bridge_error`). Do not release.
- `scope=impacted selected=3003`: selector fell back or matched too broadly; inspect `ContinuousTestImpactSelector` evidence tiers. Do not release.
- `tests status` red/stale after the run: keep the gate failed and record the exact failing/stale rows.

---

### Task 8: Document live acceptance in Eros findings

**Objective:** Leave audit-grade evidence for the release decision.

**Files:**
- Modify: `/Users/murphy/source/eros/docs/findings/2026-07-05-revision-delta-acceptance.md`

**Required addendum:**
Add a section named `Rust narrowing live gate` with:
- Miller binary path and version.
- Eros commit/binary used for the hub.
- Base `from_revision`, `from_artifact_id`, and post-edit `to_revision`.
- Probe file path.
- `miller impact --from-index-revision ...` summary: `delta_status`, `changed_paths`, `tests` count, first test name/path.
- Hub log summary: exact `ct enqueue ... scope=impacted ... selected=<n> stale=<n>` line(s).
- `tests status --json` summary after the run.
- Explicit note that the probe edit was reverted.

**Verification:**
- The finding doc contains enough raw values for a reviewer to understand why release is allowed or blocked.

---

### Task 9: Run branch gates

**Objective:** Prove the code/docs bundle is dev-complete before release.

**Miller gate:**
```bash
cd /Users/murphy/source/miller
scripts/test.sh
```

If Task 4 changed extractor pins or restore behavior, also run the appropriate scale/pin checks from `AGENTS.md` after restoring the extractor.

**Eros gate:**
```bash
cd /Users/murphy/source/eros
dotnet build
dotnet test
```

If Eros hub/CT wiring changed, also run the targeted hub smoke from `AGENTS.md`:
```bash
dotnet build
dotnet test
eros doctor
eros hub start
eros dashboard
eros status --json
eros hub stop
```

**Verification:**
- All required commands pass with real output captured in the implementation handoff.
- No unrelated `.memories/`, logs, probe edits, or local-only worktrees are left dirty.

---

### Task 10: Single release checklist, only after Tasks 5-9 pass

**Objective:** Ship at most one coherent release train after live acceptance.

**Decision tree:**
1. If no Miller production code changed and live acceptance passes with `1.4.3`, do not tag Miller again. Document that `1.4.3` is sufficient and only land Eros findings/docs if needed.
2. If Miller code changed, tag/release Miller once after all gates pass.
3. If julie-extractors changed, release julie-extractors first, then update Miller's pinned extractor and release Miller once.
4. Bump Eros `MinimumMillerVersion` and package metadata only after the Miller release exists and assets are verified.

**Miller release prerequisites:**
- `scripts/test.sh` pass.
- README / plugin manifests / version metadata updated according to Miller release rules.
- GitHub release assets verified live after tag/dispatch.

**Eros release/pin prerequisites:**
- `src/Eros.Miller/MillerConfig.cs` `MinimumMillerVersion` updated only if a new Miller release is required.
- Packaging tests updated only if version changed.
- Eros build/test pass.
- Eros findings doc includes Rust narrowing evidence.

**Forbidden:**
- No Miller tag before live `julie-extractors` Rust gate passes.
- No Eros pin bump to an unpublished Miller version.
- No release-prep commit pushed without publishing/verifying the matching Miller assets in the same session.

---

## Acceptance criteria for this epic

This epic is complete when all are true:

- Miller direct or delta impact from a non-test Rust source edit in `~/source/julie-extractors` returns `tests[]` non-empty.
- The negotiated delta command returns:
  - `delta_status: complete`
  - matching `from_artifact_id` / `artifact_id` generation
  - changed path for the Rust source edit
  - non-empty `tests[]`
- Eros hub logs an enqueue for `workspace:30a0bd2590c5639fc50fdc3c` with:
  - `scope=impacted`
  - `selected` far smaller than 3003
- `tests status --workspace-id workspace:30a0bd2590c5639fc50fdc3c --json` is green after the run.
- Eros findings document records the live evidence.
- Miller and Eros gates pass.
- Any probe edit in `julie-extractors` is reverted.
- Only after the above: perform the single release/pin path selected in Task 10.

---

## Open risks for implementer/reviewer

- Miller MCP sidecars for `/Users/murphy/source/miller` were stale during planning (`search.db` schema 6 vs expected 7, `content.db` schema 1 vs expected 2), so Miller source was inspected by direct file reads after attempting MCP refresh/full. Do not treat MCP sidecar health as product evidence; use the actual CLI gates above.
- Current synthetic Miller Rust tests prove `IsTest` partitioning, not live extraction or hub selection.
- Direct current-impact probing of `axum.rs` produced one likely test, but the accepted proof must use the index-revision delta path after a real edit.
- Eros selection can still degrade if provider inventory source paths or Rust test names differ from Miller `tests[]`; that is why Task 7 is mandatory.
