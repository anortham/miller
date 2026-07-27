# Cross-Repo Dogfood Contract Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Replace the defective extraction, tool, and semantic contracts with one verified breaking contract across `julie-extractors` and Miller.

**Architecture:** `julie-extractors` owns source-attested reference-site identity, capability status, and marker facts. Miller consumes those facts through schema-gated readers and exposes strict, lossless, bounded tool/process contracts. Eros is an unreleased downstream consumer and is repaired only after Miller and its direct producers are optimal.

**Tech Stack:** Rust 1.96, SQLite, JSONL, .NET 10/C#, xUnit, MCP, Miller CLI.

**Architecture Quality:** High-risk contract-first change. The artifact and process seams remain the caller-facing interfaces; tests exercise producer artifacts through those interfaces rather than duplicating private normalization in consumers.

## Global Constraints

- The approved design is `docs/plans/2026-07-26-cross-repo-dogfood-contract-repair-design.md`.
- SQLite schema is `5`, extract contract is `4`, and JSONL schema is `4`.
- Eros is unreleased and excluded from this completion gate; downstream breakage is repaired later without compatibility parsers, dual fields, version ranges, or `identifier_id` fallback.
- Reference-site identity is the producer hash of `(file_id, start_byte, end_byte)` for an attested target-token span; kind and target are not physical-site identity.
- Exact assertions key by `(reference_site_id, target_symbol_id, canonical_kind)` and fallback assertions by `(reference_site_id, target_name, canonical_kind)`.
- Spanless evidence stays explicitly non-exact and never merges with spanned or editable evidence.
- Capability-gap status is closed to `open | exception`; the pinned 36-language/two-tier2-language contract contains exactly 70 open reference-resolution rows.
- `code.marker.v1` is one fact per actionable physical comment/doc-comment line with vocabulary `TODO | FIXME | HACK | XXX`.
- All MCP responses retain the 12 KiB final UTF-8 envelope; stricter tool bounds remain active and truncation is lossless.
- Continuations carry a kind discriminator and relevant-population fingerprint; unrelated workspace revisions do not invalidate them.
- Semantic policy version is the integer `2`; routing and admission are separate, a sole lexical hit is protected but allows expansion, and decisive multi-hit evidence is rerank-only.
- No new MCP tool is added.
- Use test-first red/green/refactor for every behavior change.
- No tag, push, publication, deployment, or release is authorized.

## Architecture Quality

**Affected modules:** `julie-extractors` canonical extraction and artifact writer; Miller artifact readers, tool renderers, continuation codecs, diagnostics, semantic serving/evaluation, and CLI exports.

**Caller-facing interface:** schema-5 SQLite/JSONL artifacts, Miller's existing nine MCP tools, `patterns --json` schema 2, `references export --jsonl` schema 2, and `capabilities --json`.

**Depth/locality check:** physical occurrence identity and marker intent are decided once by the source owner. Paging policy is centralized around typed population identities. Semantic admission is one pure policy reused by every serving path.

**Test surface:** producer contract/golden artifacts plus Miller public tool and CLI entry points.

**Seams/adapters:** the SQLite/JSONL artifact and Miller subprocess JSON are the only cross-language seams. No transitional adapter is introduced.

**Rejected shortcuts:** consumer overlap deduplication, legacy status aliases, global-revision cursors, duplicate Trace unions, raw-string diagnostics, marker rescanning, one-hit dominance, and downstream compatibility lanes.

**Architecture risk:** high. Contract-first ordering, TDD, real all-language artifacts, exact version gates, and cross-repo branch gates control it.

## Verification Strategy

**Project source of truth:** Miller `CLAUDE.md`; `julie-extractors` `AGENTS.md`, `docs/testing-strategy.md`, and `docs/contracts/`.

**Worker red/green scope:** the narrowest named test class/target for the changed interface. Miller uses filtered `dotnet test`; Julie uses `cargo +1.96.0 test -p <crate> <target>`; Eros uses filtered project tests.

**Worker ceiling:** one repo's focused test projects or one Julie default tier. Workers do not own the cross-repo real-artifact gate, Miller scale/all gate, or Julie certification.

**Worker gate invariant:** each focused test must prove the named contract through its caller-facing seam and record its observed RED failure before implementation.

**Lead affected-change scope:** Miller `scripts/test.sh`; Julie `cargo +1.96.0 xtask test default` plus `cargo +1.96.0 xtask test contract`.

**Branch gate:** Miller `dotnet build Miller.slnx -c Release` and fast/scale suites; Julie `cargo +1.96.0 fmt --check`, workspace check/tests/doctests, and strict language-quality/certification gates required by shared extraction changes.

**Replay/metric evidence:** hard gates are schema/status/reference-site invariants, per-language marker disposition, valid/bounded/paged JSON, semantic open+sealed gates, and live nine-tool completion. Token counts, latency, calls, and irrelevant rows are reported alongside hard correctness gates.

**Escalation triggers:** shared extractor normalization requires all-language certification; indexing/pin changes require Miller scale/all and full-rebuild dogfood; semantic changes require open then sealed evaluation.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless this plan explicitly changes that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in `.razorback/sdd/verification-ledger.md`. Reuse evidence only for the same repo HEAD and scope.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Producer reference and artifact contract | Batch A | Julie reference models/normalization, artifact schema/writer, capability snapshot, JSONL and their tests/docs | No | Separate Julie worker worktree and no Task 2 file overlap. |
| Task 2: Producer marker fact contract | Batch A | Julie marker collector, source-region marker helpers, structural-fact registry, marker parity tests/docs | No | Separate Julie worker worktree and no Task 1 file overlap. |
| Task 3: Miller request, paging, diagnostics, and source hygiene | Batch A | Miller continuation/Inspect/Patterns/Trace/Edit/Content request-rendering files, NUL fixture, focused tests | No | Separate repository from Tasks 1 and 2. |
| Task 4: Miller semantic admission policy v2 | None - serial after Task 3 | Miller semantic policy, serving/canary/eval wiring, semantic tests | Yes | Shares Search/CLI serving paths with the request-contract audit in Task 3. |
| Task 5: Miller schema-5 consumer and exports | None - serial after Tasks 1-4 | Miller schema gate, reference/marker/health readers, Julie pin/fixtures, process exports/capabilities, tests/docs | Yes | Requires final producer schema and Miller paging/semantic baseline. |
| Task 6: Eros atomic process-contract replacement | Deferred downstream | Eros Miller models/parsers/constants/fixtures and all exact impacted callers/tests | Yes | Excluded until Miller and its direct producers are complete. |

### Task 1: Producer reference and artifact contract

**Files:**
- Modify: `julie-extractors/crates/julie-extractors/src/base/relationship_resolution.rs`
- Modify: `julie-extractors/crates/julie-extractors/src/base/results_normalization.rs`
- Modify: `julie-extractors/crates/julie-extract-artifact/src/model.rs`
- Modify: `julie-extractors/crates/julie-extract-artifact/src/schema.rs`
- Modify: `julie-extractors/crates/julie-extract-artifact/src/writer/rows.rs`
- Modify: `julie-extractors/crates/julie-extract-cli/src/extraction.rs`
- Modify: `julie-extractors/crates/julie-extract-cli/src/capability_snapshot.rs`
- Test: `julie-extractors/crates/julie-extract-artifact/tests/schema_contract.rs`
- Test: `julie-extractors/crates/julie-extract-artifact/tests/writer_contract.rs`
- Test: `julie-extractors/crates/julie-extract-artifact/tests/jsonl_contract.rs`
- Test: `julie-extractors/crates/julie-extract-cli/tests/operations_contract.rs`
- Test: `julie-extractors/crates/julie-extract-cli/tests/resolution_contract.rs`
- Modify/Create: producer-owned contract authority under `julie-extractors/docs/contracts/`

**Interfaces:**
- Consumes: approved schema-5 reference-site and closed gap-status contract.
- Produces: schema-5/contract-4/JSONL-4 artifact, canonical `reference_sites`, row foreign keys, exact 70-row status invariant, and authoritative normalized DDL/contract fingerprint.

**Contract inputs:** target-token span rule, assertion keys, provenance precedence, `open | exception`, 36 languages, TypeScript+JavaScript tier 2.

**File ownership:** Julie reference models/normalization, artifact schema/writer, capability snapshot, JSONL and their tests/docs.

**Serialization required:** No.

**Dependency reason:** Separate Julie worker worktree and no Task 2 file overlap.

**What to build:** Introduce the canonical physical-site domain and carry site identity from extraction into identifier, relationship, pending, resolution, writer, export, and revision accounting. Replace `open_gaps` row values with typed statuses, validate unknown values, and make the producer contract fixture authoritative.

**Approach:** Start with failing schema/writer/CLI tests. Do not infer sites in Miller. Relationship constructors/normalization must expose exact target-token spans; unresolved spanless rows remain non-exact. The producer-created SQLite catalog must compare equal to the checked-in normalized DDL authority.

**Acceptance criteria:**
- [x] Schema/contract/JSONL versions are 5/4/4 and incompatible old artifacts fail clearly.
- [x] Identifier, relationship, and pending evidence for one attested token share a site; same-line distinct tokens do not.
- [x] Same-site distinct targets/kinds survive as separate assertions.
- [x] No post-hoc overlap, line-only, or nearest-token identity is required by consumers.
- [x] The certified snapshot has exactly 70 `open` reference-resolution gaps and no unknown statuses.
- [x] Focused artifact/CLI tests pass and the worker commit/report records RED and GREEN evidence.

### Task 2: Producer marker fact contract

**Files:**
- Create: `julie-extractors/crates/julie-extractors/src/base/marker_structural_facts.rs`
- Modify: `julie-extractors/crates/julie-extractors/src/base/mod.rs`
- Modify: `julie-extractors/crates/julie-extractors/src/base/source_regions.rs`
- Modify: `julie-extractors/crates/julie-extractors/src/registry.rs`
- Modify: `julie-extractors/crates/julie-extractors/src/base/structural_fact_registry.rs`
- Create/Modify: marker-focused tests under `julie-extractors/crates/julie-extractors/src/tests/`
- Modify: `julie-extractors/docs/contracts/structural-fact-patterns.json`

**Interfaces:**
- Consumes: canonical source regions and the existing generic `StructuralFact` artifact path.
- Produces: `code.marker.v1` facts with `capture_name=marker`, normalized metadata, exact per-line spans, and per-language applicable/not-applicable evidence.

**Contract inputs:** vocabulary `TODO | FIXME | HACK | XXX`, line-first semantic token rule, block-line decoration handling, comment/doc-comment node kinds, confidence 1.0.

**File ownership:** Julie marker collector, source-region marker helpers, structural-fact registry, marker parity tests/docs.

**Serialization required:** No.

**Dependency reason:** Separate Julie worker worktree and no Task 1 file overlap.

**What to build:** Add one shared line-oriented marker collector over normalized comment regions and register it as a generic structural fact family. Cover single-line, block, doc-comment, owner, description, prose-only, and commentless-language cases.

**Approach:** Write failing generic collector and language-matrix tests first. Reuse source-region facts rather than adding per-language regexes. Emit one fact per actionable physical line; normalize marker names and retain exact semantic spans.

**Acceptance criteria:**
- [x] Actionable markers emit one exact fact per physical line with the required metadata.
- [x] Prose mentioning marker words later in a sentence emits nothing.
- [x] Multi-line block/doc comments are classified line by line.
- [x] Every supported language is proven applicable with fixtures or explicitly `not_applicable`.
- [x] Structural-fact registry/docs stay machine-checked and focused tests pass with RED/GREEN evidence.

### Task 3: Miller request, paging, diagnostics, and source hygiene

**Files:**
- Modify: `miller/src/Miller.Server/Tools/ToolContinuation.cs`
- Modify: `miller/src/Miller.Server/Tools/InspectTool.cs`
- Modify: `miller/src/Miller.Server/Tools/PatternsTool.cs`
- Modify: `miller/src/Miller.Server/Tools/TraceTool.cs`
- Modify: `miller/src/Miller.Server/Tools/EditService.cs`
- Modify: `miller/src/Miller.Server/Tools/EditTool.cs`
- Modify: `miller/src/Miller.Server/Tools/ContentTool.cs`
- Modify: `miller/tests/Miller.Tests/Support/FakeSemanticSidecar.cs`
- Test: `miller/tests/Miller.Tests/Server/ToolContinuationTests.cs`
- Test: `miller/tests/Miller.Tests/Server/InspectToolTests.cs`
- Test: `miller/tests/Miller.Tests/Server/PatternsToolTests.cs`
- Test: `miller/tests/Miller.Tests/Server/TraceToolTests.cs`
- Test: `miller/tests/Miller.Tests/Server/EditToolTests.cs`
- Test: content diagnostics tests under `miller/tests/Miller.Tests/Server/`
- Create/Modify: tracked-text control-byte gate under `miller/tests/Miller.Tests/Conventions/`

**Interfaces:**
- Consumes: existing nine-tool parameter surfaces and final 12 KiB MCP filter.
- Produces: strict enum refusal matrix, discriminated population-bound cursors, fair Patterns paging, non-duplicated Trace JSON, structured Edit/Content diagnostics, and clean tracked text.

**Contract inputs:** cursor kinds, population fingerprint rule, `continuation_kind_mismatch`, `stale_continuation`, actual-emitted-row advancement, 12 KiB final envelope.

**File ownership:** Miller continuation/Inspect/Patterns/Trace/Edit/Content request-rendering files, NUL fixture, focused tests.

**Serialization required:** No.

**Dependency reason:** Separate repository from Tasks 1 and 2.

**What to build:** Make invalid enum-like values typed refusals; add lossless Inspect file and Patterns continuation over canonical order; remove Trace's compatibility union and align its local budget with actual rendered rows; route Edit/Content failures through `ToolDiagnostic`; remove literal control bytes and enforce the invariant.

**Approach:** Add one failing behavior test at a time. Keep Context's token bound and other stricter bounds; the 12 KiB envelope is universal, not the only bound. Avoid semantic-policy changes in this task.

**Acceptance criteria:**
- [x] Unknown Inspect depth and all other invalid enum-like inputs refuse without defaulting.
- [x] Inspect file and Patterns pages replay deterministically, survive unrelated revisions, and reject changed populations/kinds.
- [x] Free-text Patterns fairly represents matched families.
- [x] Trace schema 2 has no duplicate union and advances only emitted rows under 12 KiB.
- [x] Edit/Content diagnostics preserve typed outcome/class/channel across MCP, CLI, JSON, and telemetry.
- [x] Tracked text contains no disallowed binary control bytes.
- [x] Focused tests and `scripts/test.sh` pass with RED/GREEN evidence.

### Task 4: Miller semantic admission policy v2

**Files:**
- Modify: `miller/src/Miller.Core/Search/SemanticQueryPolicy.cs`
- Modify: `miller/src/Miller.Core/Search/RrfFusion.cs` only if the approved evaluation gate requires a weight correction
- Modify: `miller/src/Miller.Server/Tools/SearchRouteExecutor.cs`
- Modify: `miller/src/Miller.Server/Tools/SearchTool.cs`
- Modify: `miller/src/Miller.Server/Tools/ContextTool.cs`
- Modify: `miller/src/Miller.Server/Cli/CliDispatch.cs`
- Modify: `miller/src/Miller.Server/Telemetry/CanaryTelemetry.cs`
- Modify: canary aggregate/export cohort files under `miller/src/Miller.Server/Telemetry/`
- Modify: semantic evaluation programs/manifests under `miller/eval/`
- Test: `miller/tests/Miller.Tests/Core/SemanticQueryPolicyTests.cs`
- Test: `miller/tests/Miller.Tests/Server/CanarySearchTests.cs`
- Test: `miller/tests/Miller.Tests/Server/CanaryContentSearchTests.cs`
- Test: `miller/tests/Miller.Tests/Server/CanaryShadowPopulationTests.cs`

**Interfaces:**
- Consumes: lexical and semantic ranked populations plus fusion class.
- Produces: pure route and admission decisions, policy version 2 on every canary/eval row, protected single-hit behavior, and measured Conceptual rerank quality.

**Contract inputs:** zero-hit expand; one-hit expand with lexical protection; decisive multi-hit positive runner-up and ratio >=1.25 rerank-only; other multi-hit expand; lexical-only shape routes.

**File ownership:** Miller semantic policy, serving/canary/eval wiring, semantic tests.

**Serialization required:** Yes.

**Dependency reason:** Shares Search/CLI serving paths with the request-contract audit in Task 3.

**What to build:** Separate route selection from candidate admission and use the admission result consistently in symbol, content, context, CLI, forced/canary, and evaluation paths. Remove policy-version defaults and wire the single integer source explicitly.

**Approach:** Start with pure policy tests, then serving tests for one-hit protection and Conceptual multi-hit admission. Run open evaluation before freezing/running the sealed slice. Change weights only if the approved lexical-population ordering hard gate fails.

**Acceptance criteria:**
- [x] One-hit evidence is never classified decisive and remains first while semantic expansion stays available.
- [x] Decisive multi-hit evidence blocks semantic-only population entry for every hybrid class.
- [x] Weak/zero-hit recall remains available; lexical-only route bytes remain identical.
- [x] Policy version 2 is explicit in all canary/shadow/cohort/export/eval paths.
- [x] Open semantic gates pass, including Conceptual rerank quality.
- [ ] Sealed semantic gate remains blocked because the user-owned sealed set was not available.
- [x] Focused tests and `scripts/test.sh` pass with RED/GREEN evidence.

### Task 5: Miller schema-5 consumer and exports

**Files:**
- Modify: `miller/scripts/julie-pins.json`
- Modify: `miller/src/Miller.Indexing/JulieSchemaGate.cs`
- Modify: `miller/src/Miller.Indexing/ReferenceEvidenceReader.cs`
- Modify: `miller/src/Miller.Indexing/ReferenceExportReader.cs`
- Modify: `miller/src/Miller.Indexing/MetricSnapshotAggregates.cs`
- Modify: `miller/src/Miller.Server/Tools/MarkerSearch.cs`
- Modify: `miller/src/Miller.Server/Tools/ReportTool.cs`
- Modify: `miller/src/Miller.Server/Tools/WorkspaceRender.cs`
- Modify: `miller/src/Miller.Server/Tools/InspectTool.cs`
- Modify: `miller/src/Miller.Server/Tools/TraceTool.cs`
- Modify: `miller/src/Miller.Server/Cli/CliCapabilities.cs`
- Modify: schema/contract fixtures under `miller/tests/Miller.Tests/Fixtures/`
- Test: `miller/tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs`
- Test: `miller/tests/Miller.Tests/Indexing/ReferenceEvidenceReaderTests.cs`
- Test: `miller/tests/Miller.Tests/Server/MarkerSearchTests.cs`
- Test: `miller/tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- Modify: affected contracts under `miller/docs/contracts/`

**Interfaces:**
- Consumes: final producer schema-5 artifact and authoritative DDL/contract fingerprint.
- Produces: exact reference assertions, structural marker search/report counts, correct health status, references export schema 2, Patterns schema 2/capability advertisement, and a pinned rebuilt extractor.

**Contract inputs:** committed producer SHAs/contract fixtures from Tasks 1-2 and tool envelopes from Tasks 3-4.

**File ownership:** Miller schema gate, reference/marker/health readers, Julie pin/fixtures, process exports/capabilities, tests/docs.

**Serialization required:** Yes.

**Dependency reason:** Requires final producer schema and Miller paging/semantic baseline.

**What to build:** Restore `julie-extract` from the producer worktree, bump the pin, require schema 5/contract 4, replace overlap dedup with site assertion keys, consume `code.marker.v1`, and emit version-2 reference/pattern contracts. Force a real full rebuild before acceptance.

**Approach:** Begin with incompatible-schema, exact-site, export-shape, marker, and 70-row health tests. Fixture guards compare normalized DDL and contract fingerprints to producer authority. Do not keep legacy fields or schema-4 compatibility.

**Acceptance criteria:**
- [x] Miller hard-requires schema 5/contract 4 and the pin matches the restored producer binary.
- [x] Reference readers/renderers/export use site-target-kind assertions and provenance without overlap guesses.
- [x] Marker search/report/metric paths consume only `code.marker.v1`.
- [x] Workspace health reports 70 open rows on the pinned producer artifact and rejects unknown statuses.
- [x] `patterns --json`, `references export --jsonl`, and `capabilities --json` advertise/emit exact schema-2 replacements.
- [x] Real producer artifact, focused tests, Release build, fast and scale gates pass.

### Deferred downstream: Task 6: Eros atomic process-contract replacement

This task is intentionally excluded from the Miller/direct-producer completion gate. Eros is unreleased and may remain broken until the upstream contracts are final.

**Files:**
- Modify: `eros/src/Eros.Miller/MillerBridge.cs`
- Modify: `eros/src/Eros.Miller/MillerBridge.Exports.cs`
- Modify: `eros/src/Eros.Miller/MillerBridge.Patterns.cs`
- Modify: `eros/src/Eros.Miller/MillerModels.cs`
- Modify: `eros/src/Eros.Miller/IMillerBridge.cs`
- Modify: `eros/src/Eros.Fleet/FleetSync.cs`
- Test: `eros/tests/Eros.Miller.Tests/MillerBridgeExportTests.cs`
- Test: affected exact callers under `eros/tests/Eros.Fleet.Tests/`, `eros/tests/Eros.Eval.Tests/`, `eros/tests/Eros.Cli.Tests/`, `eros/tests/Eros.Dashboard.Tests/`, `eros/tests/Eros.Hub.Tests/`, and `eros/tests/Eros.Semantic.Tests/`

**Interfaces:**
- Consumes: final Miller Patterns schema 2, References export schema 2, and capabilities advertisement.
- Produces: one exact unreleased Eros model/parser surface with reference-site identity and continuation-bearing pattern envelopes.

**Contract inputs:** schema-2 JSON/JSONL fixtures emitted by Task 5; no compatibility lane.

**File ownership:** Eros Miller models/parsers/constants/fixtures and all exact impacted callers/tests.

**Serialization required:** Yes.

**Dependency reason:** Requires the final Miller schema-2 process envelopes.

**What to build:** Replace Eros's version constants, `MillerReferenceExportRow`, pattern result envelopes, parsers, sync mapping, fixtures, and every exact caller. Delete assumptions about `identifier_id`.

**Approach:** Add failing parser/bridge tests using real Task-5 fixture rows, then update the model and exact Miller-indexed callers. Reject old schema versions rather than supporting both.

**Acceptance criteria:**
- [ ] Eros requires Patterns schema 2 and References schema 2 exactly.
- [ ] `MillerReferenceExportRow` carries site, target, kind, tier, span, and provenance without `IdentifierId`.
- [ ] Pattern continuations are parsed and exposed through `IMillerBridge`.
- [ ] Every exact impacted caller compiles and its meaningful behavior tests pass.
- [ ] `dotnet build -c Release` and full `dotnet test` pass.

## Final Integration Gate

- [x] Integrate the reviewed marker producer work into the Julie repair branch with no overlapping ownership.
- [x] Build the final producer binary from Julie HEAD and restore it into Miller.
- [x] Force a full Miller rebuild and prove schema/status/reference-site/marker invariants with direct SQL and public tools.
- [x] Run Miller success, empty, invalid, paging, continuation-kind mismatch, stale-population, output-budget, and cross-workspace paths for all nine tools.
- [x] Run open semantic evaluation without tuning after results are visible.
- [ ] Run the user-owned sealed semantic evaluation when the sealed set is supplied.
- [x] Run the Julie and Miller branch gates and record exact HEADs.
- [x] Run `git diff --check` and final worktree/branch/dirty-state checks in every involved worktree.
- [x] Produce a concise finding-to-fix matrix with token/call/latency/irrelevance measurements and genuine blocked/report-only items.
