# QML First-Class Indexing and Resolution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make Miller ingest, expose, and correctly resolve QML module/component evidence from Julie’s first-class artifact.

**Architecture:** `Miller.Indexing` translates producer-owned import, qmldir, and qmltypes facts into a small typed QML visibility catalog. `Miller.Core` resolves pending QML instantiations only through `IResolutionFacts.QmlTypesVisibleTo`, preserving existing public tool schemas and ambiguity semantics.

**Tech Stack:** .NET 10, C#, SQLite, Julie extraction artifacts, xUnit v3, Miller query-time resolution and tool surfaces.

**Architecture Quality:** A new typed resolver seam prevents structural-fact formats from leaking into the core while keeping QML policy local. The main risk is parity across store/artifact loaders and precedence against generic resolution. Architecture risk: high.

## Global Constraints

- Julie owns QML parsing and artifact semantics; Miller must not parse QML source.
- Upgrade `MillerExtractContract`, `JulieSchemaGate`, packaged extractor pins, and artifact fixtures together, after a compatible Julie artifact exists.
- Generic `ImportBinding` rows come only from import-symbol metadata, not `qml.import_statement.v1`.
- The resolver consumes typed QML visibility candidates, never raw structural-fact JSON.
- Store-backed and artifact-backed revision facts must be logically identical.
- QML component uses must not fall back to global unique-name resolution.
- Preserve ambiguity and unresolved evidence rather than choosing a weak candidate.
- Keep public search, inspect, trace, patterns, impact, context, and edit schemas unchanged.
- Do not change semantic-card eligibility or corpus generation in this plan.
- Use TDD, retain fast/Scale separation, support Windows, and preserve unrelated working-tree changes.

---

## Architecture Quality

**Affected modules:** extractor/schema compatibility, `Miller.Core/Resolution`, `Miller.Indexing/Resolution`, pattern facts, edit readiness, and server tool tests.

**Caller-facing interface:** existing Miller tool and CLI contracts. The new internal interface is `IResolutionFacts.QmlTypesVisibleTo(long versionId)` returning typed `QmlVisibleType` values.

**Depth/locality check:** artifact-specific decoding stays in the loader; visibility/precedence stays in a focused core policy; tool implementations consume normal indexed rows.

**Test surface:** public revision-cache constructors, `QueryTimeResolver.Resolve`, and existing tool entry points over one authoritative QML fixture.

**Seams/adapters:** `QmlVisibilityCatalog` is an immutable per-revision index. It earns its seam by representing implicit directory/module visibility that `ImportBinding` cannot express.

**Rejected shortcuts:** expanding `ImportBinding` until it models unrelated implicit visibility, structural-fact parsing in the resolver, global-name resolution, only one loader path, and language-aware semantic corpus filtering.

**Architecture risk:** high.

## Verification Strategy

**Project source of truth:** `AGENTS.md`, `tests/Miller.Tests/Miller.Tests.csproj`, `scripts/test.sh`, extractor compatibility tests, and query-time resolution contracts.

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Debug --filter "FullyQualifiedName~<OwnedTestClass>"`, narrowed to the new test during RED and run as the whole class before handoff.

**Worker ceiling:** Owned fast test classes and one assigned Scale integration method. Workers do not run `scripts/test.sh`, `scripts/test.sh all`, or Windows suites.

**Worker gate invariant:** Tests prove exact artifact decoding, store/artifact parity, visibility candidate sets, resolution precedence/cardinality, and unchanged public tool output contracts.

**Lead affected-change scope:** After each coherent batch, run focused owned classes, `dotnet build Miller.slnx -c Release --no-restore`, and `scripts/test.sh`.

**Branch gate:** Run `scripts/test.sh all` once at the final HEAD because extractor ingestion and resolution paths changed; record whether every assigned Scale fixture actually executed rather than treating a toolchain skip as evidence.

**Security scope:** `security-secrets` through `razorback:security-review`; `security-deps` if the Julie binary/package pin or any NuGet dependency changes.

**Replay/metric evidence:** Hard gates are store/artifact candidate parity, exact module-resolution matrices, no QML global fallback, unchanged tool JSON contracts, and all required tests executed. Candidate counts, loader time, and retained memory are report-only unless an existing Scale budget applies.

**Escalation triggers:** Producer schema mismatch, changed public tool JSON, corpus-generation impact, or Scale resolution regression requires stopping the affected lane for lead review. Path/artifact changes require clean-SHA Windows verification: `win-test sync miller` then `win-test run miller -- powershell -File scripts/test.ps1`.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. For replay or metric evidence, also record hard-gate metrics and report-only metrics. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Lock the Julie QML artifact contract | None - serial | `src/Miller.Indexing/MillerExtractContract.cs`; `src/Miller.Indexing/JulieSchemaGate.cs`; packaged extractor pin/config files; `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`; `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs`; `tests/Miller.Tests/Indexing/JulieDbFixtureCurrentSchemaTests.cs`; Julie artifact test fixtures | Yes | The remaining tasks require the final producer schema and fixture artifact. |
| Task 2: Add typed QML visibility facts | None - serial | `src/Miller.Core/Resolution/IResolutionFacts.cs`; create `src/Miller.Core/Resolution/QmlVisibleType.cs`; create `src/Miller.Core/Resolution/QmlVisibilityPolicy.cs`; core resolution test doubles; create `tests/Miller.Tests/Core/Resolution/QmlVisibilityPolicyTests.cs` | Yes | Establishes the internal interface and precedence contract used by loader and resolver tasks. |
| Task 3: Build store/artifact QML visibility catalogs | None - serial | `src/Miller.Indexing/Resolution/RevisionFactCacheLoader.cs`; `src/Miller.Indexing/Resolution/RevisionFactCache.cs`; create `src/Miller.Indexing/Resolution/QmlVisibilityCatalog.cs`; `tests/Miller.Tests/Indexing/Resolution/RevisionFactCacheStoreTests.cs`; `tests/Miller.Tests/Indexing/Resolution/RevisionFactCacheArtifactTests.cs`; QML fixture support | Yes | Requires Task 1 artifact shape and Task 2 typed interface. Both loader paths share catalog ownership and must land together. |
| Task 4: Resolve QML instantiations with module scope | None - serial | `src/Miller.Core/Resolution/QueryTimeResolver.cs`; `src/Miller.Core/Resolution/ResolutionPolicy.cs`; create `tests/Miller.Tests/Core/Resolution/QmlResolutionTests.cs`; resolution parity fixtures | Yes | Requires populated QML visibility facts from Task 3 and changes shared resolver precedence. |
| Task 5: Prove QML across Miller tools | None - serial | create `tests/Miller.Tests/Server/QmlToolEvidenceTests.cs`; `src/Miller.Indexing/PatternFactsReader.cs` only if new registered fact families need routing; edit language policy files and tests located by Miller; QML Scale integration fixture and support docs | Yes | End-to-end evidence requires the final artifact loader and resolver behavior from Tasks 1-4. |

Commit mode: all tasks use `serial-worker-commit`; each task changes shared contracts or consumes the prior task’s final interface.

### Task 1: Lock the Julie QML artifact contract

**Files:**
- Modify: `src/Miller.Indexing/MillerExtractContract.cs`
- Modify: `src/Miller.Indexing/JulieSchemaGate.cs`
- Modify: packaged extractor pin/config files located by Miller
- Modify: `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`
- Modify: `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs`
- Modify: `tests/Miller.Tests/Indexing/JulieDbFixtureCurrentSchemaTests.cs`
- Replace/Add: Julie extraction artifact fixtures used by indexing tests

**Interfaces:**
- Consumes: released Julie artifact containing normalized QML imports, qmldir facts, qmltypes facts, and pending instantiations.
- Produces: one verified extractor/schema/contract pin and an immutable multi-file QML fixture for Tasks 2-5.

**Contract inputs:** producer versions and fixture checksums from the approved Julie implementation; existing incompatibility error behavior.

**File ownership:** `src/Miller.Indexing/MillerExtractContract.cs`; `src/Miller.Indexing/JulieSchemaGate.cs`; packaged extractor pin/config files; `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`; `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs`; `tests/Miller.Tests/Indexing/JulieDbFixtureCurrentSchemaTests.cs`; Julie artifact test fixtures

**Serialization required:** Yes

**Dependency reason:** The remaining tasks require the final producer schema and fixture artifact.

**What to build:** Upgrade all producer compatibility pins and test artifacts as one slice. Add assertions that the QML fixture actually contains the required symbol metadata, manifest/typeinfo facts, and pending relationship rows before downstream tests rely on it.

**Approach:** Treat version constants and fixture contents as one contract migration. Preserve existing fail-closed behavior for older/newer incompatible artifacts and do not add compatibility shims for missing QML facts.

**Acceptance criteria:**
- [ ] Extractor, schema, and contract pins identify one released Julie build.
- [ ] Compatibility tests accept that artifact and reject incompatible versions with existing error codes.
- [ ] Fixture preflight proves all required QML-family row kinds are present.
- [ ] Worker-scope verification passes and the worker commits per `serial-worker-commit`.

### Task 2: Add typed QML visibility facts

**Files:**
- Modify: `src/Miller.Core/Resolution/IResolutionFacts.cs`
- Create: `src/Miller.Core/Resolution/QmlVisibleType.cs`
- Create: `src/Miller.Core/Resolution/QmlVisibilityPolicy.cs`
- Modify: core resolution test doubles implementing `IResolutionFacts`, located with `trace` before editing
- Create: `tests/Miller.Tests/Core/Resolution/QmlVisibilityPolicyTests.cs`

**Interfaces:**
- Consumes: version ids, symbol keys, QML type names, module/directory scopes, versions, aliases, internal/singleton flags, and evidence spans.
- Produces: `IReadOnlyList<QmlVisibleType> IResolutionFacts.QmlTypesVisibleTo(long versionId)` and a pure candidate-filter/order policy.

**Contract inputs:** same-file, same-directory, directory import, URI module, alias, version, internal visibility, ambiguity, and no-global-fallback rules from the design.

**File ownership:** `src/Miller.Core/Resolution/IResolutionFacts.cs`; create `src/Miller.Core/Resolution/QmlVisibleType.cs`; create `src/Miller.Core/Resolution/QmlVisibilityPolicy.cs`; core resolution test doubles; create `tests/Miller.Tests/Core/Resolution/QmlVisibilityPolicyTests.cs`

**Serialization required:** Yes

**Dependency reason:** Establishes the internal interface and precedence contract used by loader and resolver tasks.

**What to build:** Define the smallest immutable candidate record and pure filtering policy needed by the resolver. Keep database/artifact types out of Core and return all equally valid candidates so ambiguity remains observable.

**Approach:** Use `trace` on `IResolutionFacts` before modifying every implementation/test double. Test precedence and negative cases through the public policy seam; do not add speculative Qt plugin or C++ registration models.

**Acceptance criteria:**
- [ ] The interface carries every required visibility constraint and no artifact-format object.
- [ ] Policy tests cover aliases, versions, internal types, duplicate names, and missing manifest evidence.
- [ ] No valid tie is broken by lexical/global uniqueness.
- [ ] Worker-scope verification passes and the worker commits per `serial-worker-commit`.

### Task 3: Build store/artifact QML visibility catalogs

**Files:**
- Modify: `src/Miller.Indexing/Resolution/RevisionFactCacheLoader.cs`
- Modify: `src/Miller.Indexing/Resolution/RevisionFactCache.cs`
- Create: `src/Miller.Indexing/Resolution/QmlVisibilityCatalog.cs`
- Modify: `tests/Miller.Tests/Indexing/Resolution/RevisionFactCacheStoreTests.cs`
- Modify: `tests/Miller.Tests/Indexing/Resolution/RevisionFactCacheArtifactTests.cs`
- Modify/Create: QML artifact/store fixture support under `tests/Miller.Tests/Support/`

**Interfaces:**
- Consumes: Task 1 artifact rows and Task 2 `QmlVisibleType` contract.
- Produces: immutable, version-scoped candidate lists with identical logical output from store and artifact cache construction.

**Contract inputs:** import symbols supply generic import metadata; qmldir/qmltypes facts supply module exports; source file paths supply directory scope.

**File ownership:** `src/Miller.Indexing/Resolution/RevisionFactCacheLoader.cs`; `src/Miller.Indexing/Resolution/RevisionFactCache.cs`; create `src/Miller.Indexing/Resolution/QmlVisibilityCatalog.cs`; `tests/Miller.Tests/Indexing/Resolution/RevisionFactCacheStoreTests.cs`; `tests/Miller.Tests/Indexing/Resolution/RevisionFactCacheArtifactTests.cs`; QML fixture support

**Serialization required:** Yes

**Dependency reason:** Requires Task 1 artifact shape and Task 2 typed interface. Both loader paths share catalog ownership and must land together.

**What to build:** Decode producer facts once per revision into a compact catalog keyed by consumer version. Join imports, manifests, typeinfo, component symbols, and paths without letting raw JSON escape the indexing boundary.

**Approach:** Add equivalent store and artifact fixture tests before implementation. Normalize paths with existing cross-platform helpers, intern repeated strings through current cache facilities, and bound candidate expansion to manifest/directory scopes.

**Acceptance criteria:**
- [ ] Store and artifact constructors return identical ordered candidate records.
- [ ] Internal, aliased, versioned, directory, and URI visibility are represented without guessed module names.
- [ ] Unknown/malformed facts follow existing compatibility diagnostics and do not create candidates.
- [ ] Worker-scope verification passes and the worker commits per `serial-worker-commit`.

### Task 4: Resolve QML instantiations with module scope

**Files:**
- Modify: `src/Miller.Core/Resolution/QueryTimeResolver.cs`
- Modify: `src/Miller.Core/Resolution/ResolutionPolicy.cs`
- Create: `tests/Miller.Tests/Core/Resolution/QmlResolutionTests.cs`
- Modify: `tests/Miller.Tests/Indexing/Resolution/QueryTimeResolutionParity.cs` or the exact parity fixture identified by Miller

**Interfaces:**
- Consumes: pending `instantiates` relationships and Task 2/3 `QmlTypesVisibleTo` candidates.
- Produces: existing resolution results with QML module/directory provenance, confidence, exact/ambiguous state, and target symbol ids.

**Contract inputs:** QML precedence rules; generic resolution behavior for every non-QML language remains unchanged.

**File ownership:** `src/Miller.Core/Resolution/QueryTimeResolver.cs`; `src/Miller.Core/Resolution/ResolutionPolicy.cs`; create `tests/Miller.Tests/Core/Resolution/QmlResolutionTests.cs`; resolution parity fixtures

**Serialization required:** Yes

**Dependency reason:** Requires populated QML visibility facts from Task 3 and changes shared resolver precedence.

**What to build:** Route QML instantiation uses through the visibility catalog before generic tiers and suppress global fallback for that use kind/language. Preserve all candidates for ambiguity and existing unresolved output when none are visible.

**Approach:** Add a full resolution matrix first, then a parity case through the indexed read path. Keep QML branching at one policy boundary and verify non-QML resolver suites unchanged.

**Acceptance criteria:**
- [ ] Same-directory, directory-imported, and URI-module types resolve to the expected target.
- [ ] Aliases, versions, and internal boundaries are enforced.
- [ ] Duplicate visible types are ambiguous; invisible global names do not resolve.
- [ ] Existing non-QML resolution parity remains green.
- [ ] Worker-scope verification passes and the worker commits per `serial-worker-commit`.

### Task 5: Prove QML across Miller tools

**Files:**
- Create: `tests/Miller.Tests/Server/QmlToolEvidenceTests.cs`
- Modify: `src/Miller.Indexing/PatternFactsReader.cs` only if registered QML/qmldir fact families are not already generic
- Modify: edit language policy files and tests identified with Miller before implementation
- Create: QML Scale integration fixture under the existing test-fixture convention identified with Miller
- Modify: QML support/capability documentation located with Miller

**Interfaces:**
- Consumes: final indexed QML fixture and resolved graph from Tasks 1-4.
- Produces: evidence that existing search, inspect, trace, patterns, and edit interfaces treat QML as supported.

**Contract inputs:** no public tool-schema additions; semantic-card eligibility and corpus generation remain unchanged.

**File ownership:** create `tests/Miller.Tests/Server/QmlToolEvidenceTests.cs`; `src/Miller.Indexing/PatternFactsReader.cs` only if new registered fact families need routing; edit language policy files and tests; QML Scale integration fixture and support docs

**Serialization required:** Yes

**Dependency reason:** End-to-end evidence requires the final artifact loader and resolver behavior from Tasks 1-4.

**What to build:** Exercise QML through the same user-facing tools other first-class languages use. Assert symbols and patterns are discoverable, component edges trace correctly, ambiguity is honest, and span-safe edits work.

**Approach:** Prefer one shared fixture and assert tool contracts rather than private indexes. If patterns already routes fact families generically, add tests only; do not add a redundant QML switch.

**Acceptance criteria:**
- [ ] Search, inspect, trace, patterns, and edit each have positive QML evidence plus meaningful negative controls.
- [ ] Tool JSON/text schemas are unchanged and resolution provenance is visible through existing fields.
- [ ] No semantic corpus generation/version file changes.
- [ ] Fast, Scale, and triggered Windows gates pass; the worker commits per `serial-worker-commit`.

## Execution Handoff

- The user reviews and approves this plan before implementation begins.
- Julie’s extraction plan and compatible released artifact are hard dependencies for Task 1.
- Create or reuse a dedicated Miller task worktree after approval and execute with `razorback:subagent-driven-development`, Miller impact/trace checks, TDD, inline lead review, and serial worker commits.
