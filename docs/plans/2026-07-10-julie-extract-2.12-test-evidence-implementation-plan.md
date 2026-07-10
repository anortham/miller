# julie-extract 2.12 Test-Evidence Consumption Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Pin Miller to the released `julie-extract` 2.12.0 binaries and preserve the release's exact test-role and source-currency evidence through SQLite reads, the default search sidecar, impact JSON, symbol export, workspace health, and capability negotiation.

**Architecture:** Add a compact immutable `TestRoleEvidence` value in `Miller.Indexing`, derive it only from schema-v4 typed columns plus per-file status and parse-diagnostic facts, and carry it through `IndexedSymbol` and the rebuildable `search.db` sidecar. Render additive evidence on existing JSON/export contracts while leaving Core graph shape, traversal, legacy partitions, compact output, and ownership boundaries unchanged.

**Tech Stack:** .NET 10, C# 13, Microsoft.Data.Sqlite, System.Text.Json, xUnit, SQLite/FTS5, shell/PowerShell restore scripts, released `julie-extract` 2.12.0.

**Architecture Quality:** Approved medium-risk cross-module change. Indexing owns producer-role translation and per-file currency; the search sidecar preserves that same compact value; public renderers expose facts without computing completeness or verdicts. `Miller.Core.Graph`, the MCP tool count, and Eros remain unchanged. The main risk is silent loss or false freshness in the default-on sidecar, addressed by a schema bump, full and incremental round-trip tests, stale-schema rejection, and sidecar-on/off impact parity.

## Global Constraints

- Pin exactly `julie-extract` `2.12.0`; keep SQLite schema `4`, extract contract `3`, report schema `3`, JSONL schema `3`, and `symbols export` schema `1` unchanged.
- Use the published v2.12.0 SHA-256 values exactly: `aarch64-apple-darwin=249ed102deece8841c2965d7ad370ef08e63a82d093315a21f374a4457e57812`, `x86_64-apple-darwin=29ce60fbfc96d636eb1500df3d563c8739dd7bf1ef8097f00bda531c6ca467b5`, `x86_64-pc-windows-msvc=b4c428bc25638381e9ad46603cc3f30cd5ebb0065f0df83134afdda43b6df9ef`, and `x86_64-unknown-linux-gnu=578946c36965e80407a26f774ea730c0bce9bd536b20ce7e46e96098ed3006a2`.
- Derive `test_case` exactly as `is_test && !test_lifecycle`; do not classify from names, paths, annotations, frameworks, or runner configuration.
- Per-row evidence is `current` only when its `files` row has `status=indexed` and the same path has no `parse_diagnostics`; all other states are `unknown` with one of `file_status`, `parse_diagnostics`, `file_status_and_parse_diagnostics`, or `file_evidence_unavailable`.
- Normal and revision-delta impact rows add `test_evidence`; every result-bearing impact JSON adds `test_evidence_scope={status:"candidate_only",absence:"unknown"}`.
- Preserve existing impact array membership, ordering, traversal fields, counts, and compact output. A legacy `tests[]` or compact `likely tests` row may be a non-runnable lifecycle hook; consumers must use `test_evidence.test_case` for the narrower role.
- `symbols export --jsonl` retains all existing fields and ordering and adds `test_case`, `test_container`, `test_lifecycle`, `test_evidence_status`, and `test_evidence_reason`.
- Advertise feature and JSON contract name `impact_test_role_evidence`, schema version `1`, independently from existing impact feature gates.
- Keep `WorkspaceHealthReader.ParseKindCoverage` and `WorkspaceRender.WriteKindCoverageJson` generic; prove `kind_coverage.test_detection` with regression tests instead of adding another reader.
- Do not modify `GraphNode`, `SymbolGraph`, existing `IsTest` compatibility behavior, MCP tool count, Eros code, plugin/release versions, or release state.
- Do not push, publish, tag, or release. Preserve the unrelated untracked `.memories/2026-07-10/` directory in the main checkout.
- Use @razorback:test-driven-development for production behavior. The pin-data edit, released binary restore, documentation, and the explicit unchanged-Core shape guard are planned non-production/red-green exceptions; all other production changes begin with a failing focused test.

---

## Verification Strategy

**Project source of truth:** `CLAUDE.md` / generated `AGENTS.md`, especially the fast-vs-Scale split, the build guard, language-parity rule, release packaging matrix, and warnings-as-errors Release build.

**Worker red/green scope:** Run only the assigned class or method filters with `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~<assigned scope>"`. Each production worker must capture the expected failing assertion before implementation and the passing result afterward.

**Worker ceiling:** Workers may run their owned focused filters and `dotnet build` only when their change needs compile feedback. They must not run the full fast suite or whole Scale suite. Task 5 owns only its two named Scale classes because real-binary proof is its assigned behavior.

**Worker gate invariant:** Task 1 proves exact pin/version/integrity agreement; Task 2 proves one role/currency derivation across full/path reads, symbol export, and generic health passthrough; Task 3 proves full and incremental sidecar preservation plus schema freshness; Task 4 proves additive public contracts, independent negotiation, legacy compatibility, unchanged Core graph shape, and sidecar parity; Task 5 proves the released binary and language matrix rather than fixture-only behavior.

**Lead affected-change scope:** After Batch A, run the Task 1 and Task 2 focused filters together. After Tasks 3 and 4, run `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SqliteSymbolReaderTests|FullyQualifiedName~SymbolExportReaderTests|FullyQualifiedName~SearchIndexWriterTests|FullyQualifiedName~FtsSymbolSearchIndexTests|FullyQualifiedName~SymbolSearchSidecarTests|FullyQualifiedName~ImpactToolTests|FullyQualifiedName~ImpactRevisionDeltaCliTests|FullyQualifiedName~CliDispatchTests|FullyQualifiedName~WorkspaceHealthReaderTests|FullyQualifiedName~WorkspaceRenderTests|FullyQualifiedName~SymbolGraphTests"` and `dotnet build Miller.slnx -c Release`.

**Branch gate:** With the restored 2.12.0 binary present, run `scripts/test.sh` and `dotnet build Miller.slnx -c Release`; both must pass, and the build must report zero warnings and zero errors.

**Replay/metric evidence:** Hard gates are exact role/status/reason values, unchanged legacy arrays/counts/order, sidecar-on/off equality, automatic schema-stale rebuild, stale-reader rejection, exact 2.12.0 version/hash restore, Razor/Vue regression positives, negative controls, and exactly-one classification of every language/role cell across `supported`, `not_applicable`, and `open_gaps`. Extracted counts and wall-clock timings are report-only and must not be presented as completeness claims.

**Escalation triggers:** Because the extractor pin and live extract path change, run `scripts/test.sh scale` after the focused live tests. Any schema-gate, sidecar corruption/freshness, language-matrix, cross-platform restore, or build-warning failure expands verification to the directly implicated suite before branch acceptance.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. Put released-binary, role-count, capability-matrix, negative-control, fast-suite, Scale-suite, and Release-build evidence in `docs/findings/2026-07-10-julie-extract-2.12-test-evidence-dogfood.md`; reuse a passing same-HEAD ledger entry instead of rerunning an identical expensive gate.

## Parallel Execution Contract

The plan document and its task checkboxes are lead-owned coordination state; workers must not edit it.

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Pin and restore the 2.12.0 release | Batch A | Modify `scripts/julie-pins.json`, `src/Miller.Indexing/MillerExtractContract.cs`, `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`, and the pinned-version assertion in `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`; restore the ignored `.tools/julie-extract` binary. | No | None - safe parallel batch. |
| Task 2: Derive and export role/currency evidence | Batch A | Create `src/Miller.Indexing/TestRoleEvidence.cs` and `tests/Miller.Tests/Indexing/SymbolExportReaderTests.cs`; modify `src/Miller.Indexing/IndexedSymbol.cs`, `src/Miller.Indexing/SqliteSymbolReader.cs`, `src/Miller.Indexing/SymbolExportReader.cs`, `tests/Miller.Tests/Indexing/SqliteSymbolReaderTests.cs`, `tests/Miller.Tests/Indexing/WorkspaceHealthReaderTests.cs`, and `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`. | No | None - safe parallel batch. |
| Task 3: Preserve evidence in the default search sidecar | None - serial | Modify `src/Miller.Indexing/SearchIndexWriter.cs`, `src/Miller.Indexing/FtsSymbolSearchIndex.cs`, `tests/Miller.Tests/Indexing/SearchIndexWriterTests.cs`, `tests/Miller.Tests/Indexing/FtsSymbolSearchIndexTests.cs`, and `tests/Miller.Tests/Indexing/SymbolSearchSidecarTests.cs`. | Yes | Requires Task 2's finalized `IndexedSymbol.TestEvidence` shape and shared full/path reader semantics. |
| Task 4: Expose impact, export, capability, and documentation contracts | None - serial | Create `docs/contracts/impact-test-role-evidence-v1.md`; modify `src/Miller.Server/Tools/ImpactTool.cs`, `src/Miller.Server/Cli/CliCapabilities.cs`, `tests/Miller.Tests/Server/ImpactToolTests.cs`, `tests/Miller.Tests/Server/Cli/ImpactRevisionDeltaCliTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`, `tests/Miller.Tests/Graph/SymbolGraphTests.cs`, `docs/contracts/impact-traversal-evidence-v1.md`, `docs/contracts/cli-eros-v1.md`, and `docs/README.md`. | Yes | Requires Task 2 evidence semantics, Task 3 default-sidecar rehydration, and Task 1's version assertion update before touching the same CLI test file. |
| Task 5: Prove the released binary and language matrix | None - serial | Create `tests/Miller.Tests/Indexing/LiveTestRoleEvidenceScaleTests.cs` and `docs/findings/2026-07-10-julie-extract-2.12-test-evidence-dogfood.md`; modify `tests/Miller.Tests/Indexing/JulieExtractLanguagesScaleTests.cs`. | Yes | Requires the restored 2.12.0 binary and all consumer paths to be complete so dogfood exercises the shipped behavior. |

Batch A uses `parallel-lead-commit`: workers hand back verified diffs without committing; the lead reviews the combined diff, runs the affected-change gate, and commits the batch. Tasks 3 through 5 use `serial-worker-commit`: each worker commits only its owned files after focused verification and reports the path, branch, commit, and dirty state for lead reconciliation.

### Task 1: Pin and restore the 2.12.0 release

**Files:**
- Modify: `scripts/julie-pins.json`
- Modify: `src/Miller.Indexing/MillerExtractContract.cs:26`
- Test: `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs:13-45`
- Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs:285`
- Runtime artifact: `.tools/julie-extract` (ignored; restored, not committed)

**Interfaces:**
- Consumes: the live `v2.12.0` asset names and four published SHA-256 values in Global Constraints.
- Produces: `MillerExtractContract.PinnedJulieExtractVersion == "2.12.0"`, matching restore metadata, capability output, packaging matrix inputs, and a local binary whose `--version` is `julie-extract 2.12.0`.

**Contract inputs:** `scripts/restore-julie-extract.sh` selects the host target, verifies `scripts/julie-pins.json`, and installs to `.tools/`; `Miller.Server.csproj` rejects a present stale binary during build.

**File ownership:** Modify `scripts/julie-pins.json`, `src/Miller.Indexing/MillerExtractContract.cs`, `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`, and the pinned-version assertion in `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`; restore the ignored `.tools/julie-extract` binary.

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Move every pin surface from 2.11.0 to the live 2.12.0 release while leaving all schema/contract constants unchanged. Restore the host binary through the existing script so its integrity and version guards execute rather than copying a local build.

**Approach:** Update the pin assertions first so the focused test is red, then update the contract and all four JSON asset digests. Run `scripts/restore-julie-extract.sh`, assert `.tools/julie-extract --version`, and let the Release build guard independently confirm the installed binary matches the pin.

**Acceptance criteria:**
- [x] All four asset names still expand from `julie-extract-v{VER}-<triple>` and carry the exact 2.12.0 digests.
- [x] `PinnedJulieExtractVersion`, `julie-pins.json`, and `capabilities --json` agree on `2.12.0`.
- [x] SQLite schema 4, extract contract 3, report schema 3, and hash algorithm `blake3` remain unchanged and tested.
- [x] Restore succeeds from the released archive, verifies SHA-256, and `.tools/julie-extract --version` prints `julie-extract 2.12.0`.
- [x] Worker-scope verification passes and the change is handed to the lead per `parallel-lead-commit`.

### Task 2: Derive and export role/currency evidence

**Files:**
- Create: `src/Miller.Indexing/TestRoleEvidence.cs`
- Create: `tests/Miller.Tests/Indexing/SymbolExportReaderTests.cs`
- Modify: `src/Miller.Indexing/IndexedSymbol.cs:12-31`
- Modify: `src/Miller.Indexing/SqliteSymbolReader.cs:30-147`
- Modify: `src/Miller.Indexing/SymbolExportReader.cs:17-86`
- Test: `tests/Miller.Tests/Indexing/SqliteSymbolReaderTests.cs:15-190`
- Test: `tests/Miller.Tests/Indexing/WorkspaceHealthReaderTests.cs:22-106`
- Test: `tests/Miller.Tests/Server/WorkspaceRenderTests.cs` health JSON coverage

**Interfaces:**
- Consumes: schema-v4 `symbols.is_test`, `symbols.test_container`, `symbols.test_lifecycle`, `files.path/status`, `parse_diagnostics.path`, and generic `language_capabilities.kind_coverage_json`.
- Produces: `public readonly record struct TestRoleEvidence(bool IsTest, bool IsContainer, bool IsLifecycle, string Status, string? Reason)` with `IsCase => IsTest && !IsLifecycle` and a single artifact-fact factory; compatible trailing `IndexedSymbol` fields plus `TestEvidence`; identical full/path reader behavior; additive v1 symbol-export fields.

**Contract inputs:** Exact statuses/reasons and export field names from Global Constraints. Existing manual `IndexedSymbol` construction must compile through trailing defaults and yield false roles with `unknown/file_evidence_unavailable` when no artifact evidence was supplied.

**File ownership:** Create `src/Miller.Indexing/TestRoleEvidence.cs` and `tests/Miller.Tests/Indexing/SymbolExportReaderTests.cs`; modify `src/Miller.Indexing/IndexedSymbol.cs`, `src/Miller.Indexing/SqliteSymbolReader.cs`, `src/Miller.Indexing/SymbolExportReader.cs`, `tests/Miller.Tests/Indexing/SqliteSymbolReaderTests.cs`, `tests/Miller.Tests/Indexing/WorkspaceHealthReaderTests.cs`, and `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`.

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Add one allocation-free value that owns role derivation and currency vocabulary. Extend both SQLite symbol queries and symbol export with a bounded per-path diagnostic aggregation joined to `files`, then call the same value factory so status/reason semantics cannot drift.

**Approach:** Keep raw flags typed and keep `IsTest` as the compatibility partition. Resolve file and diagnostic evidence once per path in SQL, use the existing shared `ReadRows` path for `Read` and `ReadForPaths`, and store only booleans plus shared constant string references on `IndexedSymbol`. In export, keep schema version 1 and deterministic row ordering. For health, add only reader/render regression tests for `test_detection` with `supported`, `open_gaps`, and `not_applicable`; do not modify the generic production reader/renderer unless a failing test disproves the approved seam.

**Acceptance criteria:**
- [x] `IsCase` is exactly `IsTest && !IsLifecycle`; container and lifecycle flags are never inferred.
- [x] Full and path-filtered reads agree for indexed/current, non-indexed, diagnostic-only, combined, and missing-file evidence.
- [x] `failed_preserved` or any other non-`indexed` file state is `unknown`, never current.
- [x] Existing `IndexedSymbol` call sites compile without mass constructor edits and retain legacy `IsTest` behavior.
- [x] Symbol export remains schema 1 and adds the five exact deterministic fields while retaining `is_test` and all prior fields.
- [x] `workspace health --json` preserves the `test_detection` domain, structured open-gap metadata, and all three classification arrays through the existing generic seam.
- [x] Worker-scope verification passes and the change is handed to the lead per `parallel-lead-commit`.

### Task 3: Preserve evidence in the default search sidecar

**Files:**
- Modify: `src/Miller.Indexing/SearchIndexWriter.cs:36-83,146-201,264-343,493-518`
- Modify: `src/Miller.Indexing/FtsSymbolSearchIndex.cs:279-319,334-570`
- Test: `tests/Miller.Tests/Indexing/SearchIndexWriterTests.cs`
- Test: `tests/Miller.Tests/Indexing/FtsSymbolSearchIndexTests.cs`
- Test: `tests/Miller.Tests/Indexing/SymbolSearchSidecarTests.cs:169-190,236-565,611-650`

**Interfaces:**
- Consumes: Task 2's `IndexedSymbol.TestEvidence` and `SqliteSymbolReader.ReadForPaths` semantics.
- Produces: `SearchIndexWriter.SchemaVersion == 8`; `search_symbols` columns `test_container`, `test_lifecycle`, `test_evidence_status`, and nullable `test_evidence_reason`; FTS lookups that reconstruct role evidence exactly after full build and `ApplyFileChanges`.

**Contract inputs:** `search.db` is derived data. Existing `SymbolSearchSidecar.EnsureBuilt`/`EnsureCurrent` revision-plus-schema checks must rebuild schema 7 at the same extract revision; `FtsSymbolSearchIndex.Open` must reject incompatible schema rather than synthesize false/default role data.

**File ownership:** Modify `src/Miller.Indexing/SearchIndexWriter.cs`, `src/Miller.Indexing/FtsSymbolSearchIndex.cs`, `tests/Miller.Tests/Indexing/SearchIndexWriterTests.cs`, `tests/Miller.Tests/Indexing/FtsSymbolSearchIndexTests.cs`, and `tests/Miller.Tests/Indexing/SymbolSearchSidecarTests.cs`.

**Serialization required:** Yes

**Dependency reason:** Requires Task 2's finalized `IndexedSymbol.TestEvidence` shape and shared full/path reader semantics.

**What to build:** Advance the rebuildable search-sidecar schema by one and persist all role/currency fields next to `is_test`. Update every disk-symbol projection and reconstruction path, including qualification lookups and batched `FindBySymbolIds`, so impact receives identical symbols whether the sidecar is on or off.

**Approach:** Start with failing writer/reader round-trip assertions, then update DDL, insert parameters, select order, ordinals, and reconstruction together. Extend the existing incremental sidecar test so a changed file replaces case/container/lifecycle evidence through `ReadForPaths -> ApplyFileChanges -> InsertSymbols`, while an unchanged parent still supports qualified-name behavior. Reuse existing automatic rebuild and stale-reader guards; do not add an in-place migration.

**Acceptance criteria:**
- [x] Full-build writer/reader round-trip preserves every role flag, status, reason, and legacy `is_test` value.
- [x] Incremental convergence replaces stale role/currency evidence for changed files without disturbing stable doc IDs or unchanged qualification parents.
- [x] A schema-7 sidecar at the current revision rebuilds to schema 8 automatically.
- [x] Direct open of a stale/incompatible sidecar fails visibly instead of defaulting the new columns.
- [x] Existing FTS ranking, lookup, region-index, revision, and corruption tests remain green.
- [x] Worker-scope verification passes and the worker commits only owned files per `serial-worker-commit`.

### Task 4: Expose impact, export, capability, and documentation contracts

**Files:**
- Create: `docs/contracts/impact-test-role-evidence-v1.md`
- Modify: `src/Miller.Server/Tools/ImpactTool.cs:475-526,809-840`
- Modify: `src/Miller.Server/Cli/CliCapabilities.cs:19-54,107-136,142-337`
- Test: `tests/Miller.Tests/Server/ImpactToolTests.cs`
- Test: `tests/Miller.Tests/Server/Cli/ImpactRevisionDeltaCliTests.cs:64-376`
- Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs:249-310,1054-1083`
- Test: `tests/Miller.Tests/Graph/SymbolGraphTests.cs:31-420`
- Modify: `docs/contracts/impact-traversal-evidence-v1.md`
- Modify: `docs/contracts/cli-eros-v1.md`
- Modify: `docs/README.md`

**Interfaces:**
- Consumes: Task 2's `IndexedSymbol.TestEvidence`, Task 3's sidecar round-trip, existing `ImpactTool.WriteReachedArray`, normal `RenderJson`, revision-delta `RenderDeltaJson`, and capability negotiation helpers.
- Produces: nested per-row `test_evidence`, top-level `test_evidence_scope`, `CliCapabilities.ImpactTestRoleEvidenceFeature == "impact_test_role_evidence"`, an independently gated schema-1 JSON contract, public symbol-export assertions, and the new contract document.

**Contract inputs:** Exact JSON shapes, compatibility rules, candidate-only/absence-unknown semantics, and Miller/Eros ownership boundaries from Global Constraints. Usage/note-only error envelopes that do not carry result arrays need no scope object.

**File ownership:** Create `docs/contracts/impact-test-role-evidence-v1.md`; modify `src/Miller.Server/Tools/ImpactTool.cs`, `src/Miller.Server/Cli/CliCapabilities.cs`, `tests/Miller.Tests/Server/ImpactToolTests.cs`, `tests/Miller.Tests/Server/Cli/ImpactRevisionDeltaCliTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`, `tests/Miller.Tests/Graph/SymbolGraphTests.cs`, `docs/contracts/impact-traversal-evidence-v1.md`, `docs/contracts/cli-eros-v1.md`, and `docs/README.md`.

**Serialization required:** Yes

**Dependency reason:** Requires Task 2 evidence semantics, Task 3 default-sidecar rehydration, and Task 1's version assertion update before touching the same CLI test file.

**What to build:** Add role/currency facts to each reached JSON row and add the structural uncertainty scope to both normal and revision-delta result envelopes. Advertise the feature and contract independently, freeze the unchanged Core graph API with an explicit shape guard, extend the public CLI symbol-export assertion, and document the contract without claiming runnable-test completeness.

**Approach:** Write failing JSON tests first for current and unknown rows, empty results, normal impact, revision delta, and independent feature gates. Exercise both `MillerRepositoryIndex` and `FtsSymbolSearchIndex` with the same fixture and compare the role-bearing result arrays after a full build and an incremental update. Add a reflection/shape-lock test that `GraphNode` still exposes only `Id` and `IsTest`; this unchanged-contract guard is expected green before and after and does not authorize Core edits. Keep compact snapshots byte-for-byte stable and keep lifecycle rows in the legacy `tests[]` partition.

**Acceptance criteria:**
- [x] Every reached normal/delta row contains exactly `is_test`, `test_case`, `test_container`, `test_lifecycle`, `status`, and nullable `reason` under `test_evidence`.
- [x] Every result-bearing normal/delta envelope contains `test_evidence_scope.status=candidate_only` and `absence=unknown`, including empty reached arrays.
- [x] Existing membership, order, `reached_count`, `returned_count`, traversal evidence, and compact text remain unchanged.
- [x] Full-build and incremental sidecar-on/off impact JSON agree on role evidence.
- [x] `capabilities --json` advertises the feature and schema-1 contract, and pure helper tests prove role, traversal, and revision-delta gates are independent.
- [x] `symbols export --jsonl` is asserted through the CLI with all five additive fields and unchanged schema 1.
- [x] `GraphNode` remains exactly `(Id, IsTest)` and existing reach tests remain green.
- [x] Docs say positive flags are evidence, absence is unknown, compact `likely tests` may contain lifecycle hooks, and Eros owns runner inventory, freshness, scheduling, results, and verdicts.
- [x] Worker-scope verification passes and the worker commits only owned files per `serial-worker-commit`.

### Task 5: Prove the released binary and language matrix

**Files:**
- Create: `tests/Miller.Tests/Indexing/LiveTestRoleEvidenceScaleTests.cs`
- Modify: `tests/Miller.Tests/Indexing/JulieExtractLanguagesScaleTests.cs`
- Create: `docs/findings/2026-07-10-julie-extract-2.12-test-evidence-dogfood.md`

**Interfaces:**
- Consumes: `.tools/julie-extract` 2.12.0, `ScaleTestSupport.RequireJulieServer()`, `JulieExtractRunner.Scan`, the upstream v2.12.0 `test-evidence-v1` capability vocabulary, and Tasks 2-4 consumer contracts.
- Produces: opt-in Scale proof for Razor/Vue fixes, case/container/lifecycle roles, negative controls, role counts by language, exactly-once capability classification, and a findings ledger tied to the verified commit.

**Contract inputs:** The real fixture must include a Razor `[Fact]`, Vue call-style cases from both `<script>` and `<script setup>`, at least one container, at least one lifecycle hook, and same-language negative controls. Minimize fixture syntax from upstream v2.12.0 regression examples rather than inventing new classifier spellings.

**File ownership:** Create `tests/Miller.Tests/Indexing/LiveTestRoleEvidenceScaleTests.cs` and `docs/findings/2026-07-10-julie-extract-2.12-test-evidence-dogfood.md`; modify `tests/Miller.Tests/Indexing/JulieExtractLanguagesScaleTests.cs`.

**Serialization required:** Yes

**Dependency reason:** Requires the restored 2.12.0 binary and all consumer paths to be complete so dogfood exercises the shipped behavior.

**What to build:** Add one real-extractor Scale fixture that scans a temporary multi-language workspace, queries typed role columns and the Miller consumer/export/impact paths, and distinguishes positives from negative controls. Extend the existing languages Scale class to retain its extension smoke test and additionally parse `languages --json`, validating the full `kind_coverage.test_detection` matrix.

**Approach:** The new Scale class must carry `[Trait("Category", "Scale")]` at class level and obtain the binary only through `ScaleTestSupport.RequireJulieServer()`. Query role counts with the design's grouped SQL and assert every fixture language appears; separately validate that every published language has each of `test_case`, `test_container`, and `test_lifecycle` in exactly one of `supported`, `not_applicable`, or `open_gaps`. Record exact command output, counts, classifications, and negative controls as evidence, but describe zero counts only as observations.

**Acceptance criteria:**
- [x] The live binary reports `julie-extract 2.12.0` and the restore/build guards agree.
- [x] Real extraction recognizes Razor `[Fact]`, Vue `<script>` and `<script setup>` call-style cases, a container, and a lifecycle hook while leaving negative controls unmarked.
- [x] Every language in the fixture is present in the grouped role-count query, and Miller's reader/export/impact evidence matches the SQLite flags.
- [x] Every language/role capability cell is classified exactly once across `supported`, `not_applicable`, and `open_gaps`; duplicates, omissions, or unknown role keys fail the test.
- [x] Focused Task 5 Scale filters pass, followed by `scripts/test.sh scale`, `scripts/test.sh`, and a zero-warning/zero-error Release build at the final HEAD.
- [x] The findings document records the verification ledger and states the candidate-only/absence-unknown boundary without a completeness claim.
- [x] Worker-scope verification passes and the worker commits only owned files per `serial-worker-commit`.

## Final Branch Review

After Task 5, the lead must:

1. Reconcile every worker commit and run `git status --short --branch`, `git rev-parse HEAD`, and `git worktree list`; inspect the main checkout too so the unrelated `.memories/2026-07-10/` state is not touched.
2. Compare the completed checkboxes against the approved design acceptance criteria and run Miller `impact` on the final git diff to confirm affected tests and absence of unintended Core/Eros/MCP changes.
3. Run the affected-change gate, `scripts/test.sh`, `scripts/test.sh scale`, and `dotnet build Miller.slnx -c Release`, recording or reusing same-HEAD evidence in the findings ledger.
4. Use @razorback:requesting-code-review for a final read-only review, resolve verified findings with @razorback:receiving-code-review, and repeat focused verification after any fix.
5. Use @razorback:verification-before-completion before claiming the branch complete, then @razorback:finishing-a-development-branch for the local handoff. Do not merge, push, tag, publish, or release without a new explicit user instruction.
