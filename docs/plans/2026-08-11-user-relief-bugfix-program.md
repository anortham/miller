# User-Relief Bugfix Program Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Remove the confirmed post-1.18 family-store, semantic-activation, diagnostic, and Windows bootstrap failures and produce a locally verified 1.18.2 release candidate without publishing it.

**Architecture:** Keep every existing caller-facing contract stable. Miller gets a family-store-specific reference-query implementation behind `ReferenceEvidenceReader`, a recoverable semantic-not-ready state, and correct standalone JSON diagnostics. The sidecar becomes health-recoverable without process respawn, while `julie-extract` replaces shell-based capacity probes with the safe `fs4` filesystem API.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite/SQLite, xUnit, Rust, `fs4 1.1.0`, GitHub Actions, Miller family-store and semantic-sidecar protocol v1.

**Architecture Quality:** Medium risk. The public MCP/CLI, `ReferenceEvidenceReader`, sidecar wire protocol, store request, and capacity-policy interfaces remain unchanged; complexity stays behind store-read, semantic-session, broker-engine, and capacity-provider seams.

## Global Constraints

- Add no MCP tools and make no semantic-sidecar protocol-v1 changes.
- Preserve byte-identical legacy/standalone reference output and lexical-only search output.
- Preserve `MILLER_SEMANTIC=off` as a permanent zero-work path.
- Never respawn the semantic broker to activate a newly prepared model.
- Do not materialize the complete resolution corpus on every family-store read session.
- Keep `CapacityProvider` and store-capacity arithmetic unchanged; replace only filesystem-capacity acquisition.
- Miller fast and Scale suites remain separate; any real `julie-extract` process test stays `Category=Scale` and uses `ScaleTestSupport.RequireJulieServer()`.
- Do not weaken diagnostics, backoff, exact-resolution, package, or release gates.
- Do not publish, push, tag, release, or update marketplace-visible Miller manifests without explicit user approval.
- Preserve unrelated dirty files in `/home/murphy/source/miller`; `/home/murphy/source/julie-extractors` and `/home/murphy/source/julie-semantic-sidecar` were clean at planning time.

---

## Findings Rolled Into This Plan

- JSON `inspect`, `trace`, and `impact` calls during resolution convergence pass an empty payload to `ToolDiagnosticRenderer.AttachJson`, producing `invalid_json_output` instead of `resolution_converging`.
- Minimal `context` calls measured about 33 seconds; semantic search measured 122 ms and content search 330 ms, isolating reference enrichment.
- `ContextTool.PromoteTermRescueTestSubjects` can inspect six test candidates for each of up to twelve query terms before pivot selection and token packing.
- Family-store resolution compatibility views discard `version_id`. One outgoing read over the live 415,975-row base took 1.32 seconds for exact evidence and 4.86 seconds for fallback evidence; composite `(version_id, local_id)` queries returned the same 301 exact and 130 fallback rows in under 10 ms.
- `miller semantic prepare` leaves a live unready broker and Miller session latched until restart. The sidecar retains a loader but does not invoke it from `health`; Miller maps a valid `model_not_prepared` refusal to `CircuitOpen`.
- `julie-extract` runs `powershell.exe -Command "(Get-Volume -FilePath $args[0]).SizeRemaining" <path>`. Windows PowerShell consumes the path as command text instead of binding `$args[0]`, causing `capacity_probe_failed` during family-store bootstrap.
- Repeated `source=Roots` bindings are request-time recovery from `BootstrapPhase.Failed`, not a second retry timer. Fix the producer failure and its diagnostics; do not add another bootstrap retry policy in this program.
- TODO scan added the semantic-activation bug to scope. Cross-tool discoverability, stateless MCP, accidental-root policy, explicit registration, complexity, dead-code, and Eros contract entries remain product/design backlog and are excluded.

## Architecture Quality

**Affected modules:** `ToolDiagnosticRenderer`; `ReferenceEvidenceReader` and family-store read projections; `ContextTool`; semantic session/broker/prepare/health rendering; sidecar broker engine; extractor capacity provider; Windows Scale CI.

**Caller-facing interface:** Existing method signatures, MCP/CLI JSON shapes, protocol-v1 envelopes, `StoreRequestResult`, and `CapacityProvider` remain stable. The only additive internal state is `SemanticSessionState.ModelNotPrepared` and an internal readiness-probe delegate.

**Depth/locality check:** Store-specific SQL lives in a partial `ReferenceEvidenceReader` implementation rather than leaking base/delta tables into tools. Model activation lives in the broker/session seams rather than in search or vector callers. Filesystem probing remains under `CapacityProvider`.

**Test surface:** Tool JSON output; `ReferenceEvidenceReader` family-store overloads; `ContextTool.RunActionable`; semantic session and broker health/embed behavior; `SemanticPrepareCli`; `workspace health`; `julie-extract store import`; packaged Windows Scale smoke.

**Seams/adapters:** Add `ReferenceEvidenceReader.FamilyStore.cs` as an internal store adapter. Extend `EmbeddingClient` with a readiness probe. Inject a best-effort post-prepare broker activator into `SemanticPrepareCli`. Use `fs4::available_space` behind the existing extractor capacity function.

**Rejected shortcuts:** Full resolution-view materialization; indexes that still require whole-corpus scans; disabling term rescue; increasing token budgets; semantic broker respawn; treating `model_not_prepared` as transport failure; PowerShell quoting workarounds; suppressing producer errors; adding another bootstrap retry timer.

**Architecture risk:** medium. Exact/fallback reference classification and broker lifecycle are load-bearing; Task 6 runs all affected Scale and package smokes before handoff.

## Verification Strategy

**Project source of truth:** Miller `AGENTS.md`; `julie-extractors/AGENTS.md` and `.github/workflows/ci.yml`; `julie-semantic-sidecar/AGENTS.md`; Miller `docs/contracts/semantic-sidecar-protocol-v1.md`.

**Worker red/green scope:** Use focused raw `dotnet test --filter` commands for TDD because the wrapper has no symbol filter; use focused `cargo test` targets in the Rust repositories. Every worker records the failing assertion before implementation and the passing command after implementation.

**Worker ceiling:** Focused test class/module plus the repository's formatter or compile check. Workers do not run Miller's full Scale suite or cross-repository package smokes.

**Worker gate invariant:** Each task's focused tests prove its caller-visible behavior and its stated performance/query-shape invariant without relying on elapsed-time thresholds.

**Lead affected-change scope:** Miller `scripts/test.sh`; sidecar `cargo test`, `cargo clippy --all-targets -- -D warnings`, and `cargo fmt --check`; extractor `cargo fmt --check`, `cargo clippy --workspace --all-targets --all-features --no-deps -- -D warnings`, and `cargo xtask test default`.

**Branch gate:** Miller `scripts/test.sh all` and `dotnet build Miller.slnx -c Release`; extractor `cargo xtask test contract`; sidecar's four documented gates including Python script tests; from-source restores plus Miller package/semantic/Windows store smokes in Task 6.

**Security scope:** Extractor dependency addition runs `cargo deny check --all-features`; Miller and sidecar declare no additional security gate for this program.

**Replay/metric evidence:** Exact/fallback row counts, query-plan composite-index use, bounded term-rescue reference reads, diagnostic codes, semantic state transitions, and Windows store completion are hard gates. Repeated local context/inspect/trace wall-clock measurements are report-only and compared with the 2026-08-11 baseline on the same machine.

**Escalation triggers:** Any family-store schema/projection change, extractor process-path change, semantic protocol/output change, pin/version bump, or package-layout change requires the corresponding Scale/package tier. Any release/publish action requires fresh user approval and a clean-state audit in all three repositories.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, repository, commit SHA, result, and timestamp. For dogfood timings, also record query, parameters, cold/warm status, and report-only elapsed time.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Standalone JSON diagnostics | Batch A | Miller: `src/Miller.Server/Tools/ToolDiagnosticRenderer.cs`, `tests/Miller.Tests/Server/ToolDiagnosticTests.cs`, `tests/Miller.Tests/Server/ResolutionLayerGuardTests.cs` | No | None - safe parallel batch. |
| Task 2: Family-store reference and context latency | Batch A | Miller: `src/Miller.Indexing/ReferenceEvidenceReader.cs`, create `src/Miller.Indexing/ReferenceEvidenceReader.FamilyStore.cs`, `src/Miller.Server/Tools/ContextTool.cs`, `tests/Miller.Tests/Indexing/StoreResolutionReaderTests.cs`, `tests/Miller.Tests/Server/ContextToolTests.cs` | No | None - safe parallel batch. |
| Task 3: Sidecar live model activation | Batch A | Sidecar: `src/broker/engine.rs`, create `tests/broker_model_activation_tests.rs` | No | None - safe parallel batch. |
| Task 4: Miller semantic recovery and UX | Batch A | Miller: `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs`, `src/Miller.Indexing/Semantic/SemanticEmbeddingSessionBroker.cs`, `src/Miller.Indexing/Semantic/SharedSemanticBrokerConnectionFactory.cs`, `src/Miller.Indexing/Semantic/SemanticSearchArm.cs`, `src/Miller.Server/Hosting/VectorConvergeService.cs`, `src/Miller.Server/Cli/SemanticPrepareCli.cs`, `src/Miller.Server/Cli/CliDispatch.cs`, `src/Miller.Server/Tools/WorkspaceHealthFacts.cs`, `src/Miller.Server/Tools/WorkspaceRender.cs`, related semantic/CLI/health/status tests | No | The sidecar recovery contract is fixed by this plan, so fake-broker tests can proceed independently; real integration waits for Task 3. |
| Task 5: Native capacity probe and Windows source proof | Batch A | Extractor: `crates/julie-extract-cli/Cargo.toml`, `Cargo.lock`, `crates/julie-extract-cli/src/store/maintenance.rs`, `crates/julie-extract-cli/src/store/import.rs`, `.github/workflows/ci.yml`; Miller: `tests/Miller.Tests/Indexing/LiveJulieStoreClientScaleTests.cs`, `tests/Miller.Tests/Server/StoreWorkspaceIndexProviderScaleTests.cs` | No | None - safe parallel batch. |
| Task 6: Cross-repository integration and release candidate | None - serial | Miller: `TODO.md`, create `docs/findings/2026-08-11-user-relief-bugfix-verification.md`, `docs/README.md`; generated/restored `.tools` remain uncommitted | Yes | Requires verified outputs from Tasks 1-5 and owns final cross-repository evidence. |

### Task 1: Render standalone JSON diagnostics from empty tool output

**Files:**
- Modify: `src/Miller.Server/Tools/ToolDiagnosticRenderer.cs:148-177`
- Modify: `tests/Miller.Tests/Server/ToolDiagnosticTests.cs:70-118`
- Modify: `tests/Miller.Tests/Server/ResolutionLayerGuardTests.cs:62-97`

**Interfaces:**
- Consumes: `ToolDiagnosticRenderer.Attach(string tool, string output, ToolDiagnostic diagnostic, bool json, TelemetryScope? telemetry = null)`.
- Produces: Empty/whitespace JSON payloads render the same standalone envelope as `Render`; nonempty object/array attachment remains unchanged.

**Contract inputs:** Diagnostic schema version 1; exact code `resolution_converging`; scalar and malformed nonempty JSON remain `invalid_json_output`.

**File ownership:** Miller: `src/Miller.Server/Tools/ToolDiagnosticRenderer.cs`, `tests/Miller.Tests/Server/ToolDiagnosticTests.cs`, `tests/Miller.Tests/Server/ResolutionLayerGuardTests.cs`.

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Make `AttachJson` route empty or whitespace output through the existing standalone JSON renderer. Add unit coverage for empty and whitespace payloads, then run `trace`, `inspect` overview/full, `impact`, and `context` convergence guards in JSON mode and assert a valid diagnostic envelope carrying `resolution_converging`.

**Approach:** Check emptiness before `JsonNode.Parse`. Reuse `RenderJson` so standalone field names remain `schema_version`, `tool`, and `diagnostic`; do not invent a synthetic results payload. Preserve malformed/scalar rejection tests.

**Acceptance criteria:**
- [x] Empty and whitespace JSON attachment returns a valid standalone diagnostic envelope.
- [x] JSON convergence paths for trace, inspect overview/full, impact, and context return `resolution_converging`, never `invalid_json_output`.
- [x] Nonempty object/array, malformed JSON, scalar JSON, compact output, and telemetry behavior remain unchanged.
- [x] Focused red/green and worker-scope verification pass; hand off per commit mode.

### Task 2: Make family-store reference reads targeted and bound context enrichment

**Files:**
- Modify: `src/Miller.Indexing/ReferenceEvidenceReader.cs:15-812`
- Create: `src/Miller.Indexing/ReferenceEvidenceReader.FamilyStore.cs`
- Modify: `src/Miller.Server/Tools/ContextTool.cs:1117-1645`
- Modify: `tests/Miller.Tests/Indexing/StoreResolutionReaderTests.cs`
- Modify: `tests/Miller.Tests/Server/ContextToolTests.cs:2860-3190`

**Interfaces:**
- Consumes: Existing `FamilyStoreReadSession` overloads and attached `main`, `resolution_base`, `_miller_visible_entries`, and `_miller_session` schemas.
- Produces: The same `ReferenceEvidenceSet`, `ReferenceEvidenceBundle`, `OutgoingReferenceEvidenceSet`, coverage, ordering, deduplication, and bounds through composite-key targeted SQL.

**Contract inputs:** Resolution local IDs are scoped by `version_id`; current view/base/delta selection comes from `_miller_session`; legacy DB-path overloads retain current SQL and bytes.

**File ownership:** Miller: `src/Miller.Indexing/ReferenceEvidenceReader.cs`, create `src/Miller.Indexing/ReferenceEvidenceReader.FamilyStore.cs`, `src/Miller.Server/Tools/ContextTool.cs`, `tests/Miller.Tests/Indexing/StoreResolutionReaderTests.cs`, `tests/Miller.Tests/Server/ContextToolTests.cs`.

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Convert `ReferenceEvidenceReader` to a partial static class and route every `FamilyStoreReadSession` overload through store-aware SQL that first materializes only the requested target/containing symbol's visible `(version_id, local_id)` rows, then probes base and delta tables by their composite keys. Separately make term-rescue ordering deterministic, memoize per-symbol outgoing reads, and cap optional promotion reads at eight per context call.

**Approach:** Keep row parsing/deduplication shared across partial files. Query base rows with `(version_id, identifier_id)` or `(version_id, pending_relationship_id)` and apply the current delta replacement/tombstone rules before joining visible symbols/sites. Add a collision fixture where equal local IDs exist in different versions, and an `EXPLAIN QUERY PLAN` assertion that the base resolution tables are searched by composite index rather than scanned. Context tests inject the outgoing reader and assert at most eight calls while preserving sole-subject promotion, multi-subject refusal, truncation refusal, and test-intent behavior.

**Acceptance criteria:**
- [x] Family-store inbound, outgoing, outgoing-kinds, exact, and fallback results match legacy fixture output byte-for-byte.
- [x] Equal local IDs in different versions never cross-resolve.
- [x] Query-plan tests show composite-index `SEARCH` operations and reject full scans of base identifier/pending resolution tables.
- [x] Term rescue performs no more than eight unique outgoing reads per context call and retains deterministic ranking.
- [x] Repeated dogfood context/inspect/trace timings show the expected order-of-magnitude reduction; timings remain report-only.
- [x] Focused red/green and worker-scope verification pass; hand off per commit mode.

### Task 3: Let a live sidecar broker activate a newly prepared model

**Files:**
- Modify: `/home/murphy/source/julie-semantic-sidecar/src/broker/engine.rs:10-220`
- Create: `/home/murphy/source/julie-semantic-sidecar/tests/broker_model_activation_tests.rs`

**Interfaces:**
- Consumes: `BrokerEngine::load_with`, retained engine loader, `LoadedEngine::{Ready,Unready}`, and protocol-v1 `health`.
- Produces: A `health` request rechecks an unready model and atomically swaps in a ready engine when preparation has completed; absent models remain a cheap stated refusal.

**Contract inputs:** Exact degraded reason `model_not_prepared`; no new environment knobs, endpoint fields, protocol methods, or broker respawn behavior.

**File ownership:** Sidecar: `src/broker/engine.rs`, create `tests/broker_model_activation_tests.rs`.

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Before rendering health, detect an unready `model_not_prepared` engine and invoke the retained loader once under the broker engine's existing exclusive mutable state. Keep the unready engine on another `ModelNotPrepared` result; install the loaded engine on success; make any other load failure visible without killing the broker.

**Approach:** Reuse the existing loader and current CPU-only policy after an unready startup has released the accelerator lease. Do not acquire a new lease or spawn a process from health. Tests use a fake loader sequence to prove absent→absent, absent→ready, concurrent serialized health, ready embed after transition, and no repeated load once ready.

**Acceptance criteria:**
- [x] An unprepared broker remains alive and reports protocol-conformant `model_not_prepared` health.
- [x] After the model becomes available, one health request flips the same broker process to ready and embedding succeeds.
- [x] Ready brokers do not reload, and unready checks do not hold or reacquire the accelerator lease.
- [x] Protocol v1 envelopes and stdout purity remain unchanged.
- [x] Sidecar focused tests, formatter, clippy, and worker-scope verification pass; hand off per commit mode.

### Task 4: Recover Miller semantic sessions after prepare and fix operator guidance

**Files:**
- Modify: `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs:10-560`
- Modify: `src/Miller.Indexing/Semantic/SemanticEmbeddingSessionBroker.cs:1-180`
- Modify: `src/Miller.Indexing/Semantic/SharedSemanticBrokerConnectionFactory.cs:131-202`
- Modify: `src/Miller.Indexing/Semantic/SemanticSearchArm.cs:284-383`
- Modify: `src/Miller.Indexing/VectorSidecar.cs:515-602`
- Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs:565-640,1240-1280`
- Modify: `src/Miller.Server/Cli/SemanticPrepareCli.cs:20-175`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs:300-350`
- Modify: `src/Miller.Server/Tools/WorkspaceHealthFacts.cs:339-388`
- Modify: `src/Miller.Server/Tools/WorkspaceRender.cs:542-551`
- Modify: `tests/Miller.Tests/Indexing/SemanticEmbeddingSessionTests.cs`
- Modify: `tests/Miller.Tests/Indexing/SemanticQueryDiagnosticsTests.cs`
- Modify: `tests/Miller.Tests/Indexing/SharedSemanticBrokerConnectionFactoryTests.cs`
- Modify: `tests/Miller.Tests/Indexing/VectorSidecarTests.cs`
- Modify: `tests/Miller.Tests/Indexing/VectorSidecarClassificationTests.cs`
- Modify: `tests/Miller.Tests/Indexing/VectorSidecarOpenTests.cs`
- Modify: `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`
- Modify: `tests/Miller.Tests/Server/SemanticPrepareCliTests.cs`
- Modify: `tests/Miller.Tests/Server/WorkspaceVectorFactsRenderTests.cs`

**Interfaces:**
- Consumes: Protocol health, `SharedSemanticBrokerConnectionFactory.ObserveExistingAsync`, converge ticks, and `SemanticPrepareCli` process result.
- Produces: `SemanticSessionState.ModelNotPrepared`, an explicit bounded readiness probe, post-prepare activation reporting, and actionable health guidance.

**Contract inputs:** A valid `model_not_prepared` health refusal is not a transport failure; successful prepare remains exit 0 even when no live broker exists; activation probing never starts a broker.

**File ownership:** Miller: semantic session/broker/factory, semantic search fallback, vector sidecar pause classification, vector convergence, semantic prepare/dispatch, workspace health/status rendering, and the listed tests.

**Serialization required:** No.

**Dependency reason:** The sidecar recovery contract is fixed by this plan, so fake-broker tests can proceed independently; real integration waits for Task 3.

**What to build:** Add a parked `ModelNotPrepared` session state that blocks embedding without charging/opening the transport circuit. Add an explicit readiness probe used by the existing converge tick and by a best-effort post-prepare activator. On prepare success, passively connect to an existing broker, send one bounded health probe, and render `activated`, `no_live_broker`, or `still_not_ready` while retaining exit 0. Map the parked state through convergence pause metadata and semantic-search fallback, and render its reason plus `miller semantic prepare` hint in compact workspace status and health guidance.

**Approach:** Split call admission so `EnsureStartedAsync` may probe a parked session while embed calls fail fast. Extend the nested `VectorConvergeService.EmbeddingClient` with `EnsureReadyAsync` through both `For(SemanticEmbeddingSession)` and `For(SemanticEmbeddingSessionBroker)` adapters; only converge invokes it automatically. Stamp `ModelNotPrepared` as a distinct non-ready convergence pause, map embed refusal to `SemanticFallbackKind.ModelNotPrepared`, and preserve the reason in `WorkspaceRender.VectorsLabel`. Use the existing cold-load `InitTimeout` for readiness probes, not the short request timeout. Inject the prepare activator for fast tests; production uses passive broker observation and cannot become an owner candidate. Keep transport errors on the existing fatal/backoff path.

**Acceptance criteria:**
- [x] `model_not_prepared` produces `SemanticSessionState.ModelNotPrepared`, not `CircuitOpen`, and does not increment the fatal counter.
- [x] Query embeddings fail fast while parked; converge and explicit prepare probes can transition the same session to `Ready`.
- [x] Convergence records a model-not-prepared pause and semantic search returns `ModelNotPrepared`, never generic `EmbedError` or a false-ready state.
- [x] Successful prepare probes a live broker once, does not spawn one, preserves exit 0, and reports activation outcome in compact and JSON output.
- [x] Compact `workspace status` includes the model-not-prepared reason and `miller semantic prepare` hint; `workspace health` recommends the same action and no longer recommends merely keeping a leader alive.
- [x] Readiness probes use the cold-load initialization timeout through both session and broker embedding adapters.
- [x] Transport-failure circuit, reconnect, `MILLER_SEMANTIC=off`, and no-restart-loop tests remain green.
- [x] Focused red/green and worker-scope verification pass; hand off per commit mode.

### Task 5: Replace shell capacity probes and prove the source fix on Windows

**Files:**
- Modify: `/home/murphy/source/julie-extractors/crates/julie-extract-cli/Cargo.toml`
- Modify: `/home/murphy/source/julie-extractors/Cargo.lock`
- Modify: `/home/murphy/source/julie-extractors/crates/julie-extract-cli/src/store/maintenance.rs:755-820`
- Modify: `/home/murphy/source/julie-extractors/crates/julie-extract-cli/src/store/import.rs:890-935`
- Modify: `/home/murphy/source/julie-extractors/.github/workflows/ci.yml`
- Modify: `tests/Miller.Tests/Indexing/LiveJulieStoreClientScaleTests.cs:14-90`
- Modify: `tests/Miller.Tests/Server/StoreWorkspaceIndexProviderScaleTests.cs:203-345`

**Interfaces:**
- Consumes: `fs4::available_space(Path) -> io::Result<u64>`, `CapacityProvider`, existing store failure JSON, and Miller Scale support.
- Produces: Native cross-platform capacity acquisition with original arithmetic/error classification and a source-built Windows PR proof that does not depend on Miller's older published pin.

**Contract inputs:** `fs4 1.1.0`; quota-aware available bytes; Microsoft `GetDiskFreeSpaceExW` semantics documented at `https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw`; no unsafe code in `julie-extractors`.

**File ownership:** Extractor capacity files/dependency/Windows CI plus Miller live-store Scale tests.

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Replace both Unix `df` and Windows PowerShell implementations of `filesystem_free_bytes` with `fs4::available_space`, leaving the existing ancestor selection, headroom, WAL projection, insufficient-capacity code, and `CapacityProvider` interface intact. Improve Miller Scale assertions to print producer failure class/message/JSON. Add an extractor PR job that builds the current source on Windows and runs the focused real-filesystem capacity/store probe.

**Approach:** Add `fs4 = "1.1.0"` to the CLI crate and update the lockfile. Unit-test real temporary-directory probing and keep injected arithmetic boundary tests. Do not enable Miller's PR `windows-scale-smoke` while `scripts/julie-pins.json` still points at 2.31.4; that job restores the published pin and would remain red. Keep it scheduled/manual until a separately approved 2.31.5 publication and pin bump, while extractor CI proves the source fix now.

**Acceptance criteria:**
- [x] No `df`, `powershell.exe`, `Get-Volume`, or command-line parsing remains in filesystem capacity acquisition.
- [x] Exact-fit, insufficient-space, nonexistent-store ancestor, and real-filesystem tests retain their current codes and arithmetic.
- [ ] Source-built Windows capacity/store probing passes with a plain local path; Miller Scale failures print producer class and message.
- [x] Extractor pull requests run the source-built Windows proof; Miller `windows-scale-smoke` remains schedule/manual while pinned to 2.31.4.
- [ ] Extractor formatter, clippy, default/contract tests, and `cargo deny check --all-features` pass; Miller worker scope passes.
- [x] Focused red/green and worker-scope verification pass; hand off per commit mode.

### Task 6: Prove the combined relief candidate and close only completed TODO items

**Files:**
- Modify: `TODO.md`
- Create: `docs/findings/2026-08-11-user-relief-bugfix-verification.md`
- Modify: `docs/README.md`
- Approval-gated after producer publication: `scripts/julie-pins.json`, `.github/workflows/ci.yml`
- Do not commit restored binaries under `.tools/`

**Interfaces:**
- Consumes: Verified source changes from Tasks 1-5 and Miller from-source restore scripts.
- Produces: One evidence packet, updated documentation map, and a release-candidate recommendation for sidecar 0.1.1, extractor 2.31.5, and Miller 1.18.2.

**Contract inputs:** `MILLER_JULIE_SOURCE=/home/murphy/source/julie-extractors scripts/restore-julie-extract.sh --from-source`; `MILLER_SEMANTIC_SIDECAR_SOURCE=/home/murphy/source/julie-semantic-sidecar scripts/restore-semantic-sidecar.sh --from-source`; no publication without approval.

**File ownership:** Miller TODO, verification finding, and documentation map; generated tool restores remain ignored/uncommitted.

**Serialization required:** Yes.

**Dependency reason:** Requires verified outputs from Tasks 1-5 and owns final cross-repository evidence.

**What to build:** Restore both fixed producer binaries from source into Miller, run all repository branch gates, then repeat the original JSON convergence, minimal context, inspect/trace, semantic prepare→health→embed, and store bootstrap repros. Record hard assertions and report-only timings. Remove the two Active TODO entries only after their acceptance criteria pass; do not alter excluded product backlog entries. After the user separately approves extractor publication and the Miller pin bump, pin 2.31.5 and only then enable Miller's `windows-scale-smoke` on pull requests.

**Approach:** Record repository path/branch/commit/dirty state before and after integration. Use the same context parameters and machine as the baseline. Run Miller fast and Scale suites separately, then Release build. On Windows, use the extractor PR source-build result or an equivalent real Windows runner; Linux cannot stand in for the failed PowerShell path. Produce release notes/version/pin changes only after the user separately approves publication. The Miller PR gate change is coupled to that approved pin so it never restores known-broken 2.31.4.

**Acceptance criteria:**
- [ ] All hard gates in the Verification Strategy pass with ledger entries tied to exact commits.
- [ ] Original JSON convergence repro returns `resolution_converging` in valid JSON.
- [ ] Original context/reference queries return identical evidence with report-only timings materially below baseline.
- [ ] A model prepared after broker startup becomes ready and embeds without restarting Miller or the broker.
- [ ] Real source-built Windows capacity/store probing succeeds; after approved 2.31.5 publication and pinning, the Miller PR Windows Scale gate is enabled and green.
- [ ] Only completed Active TODO entries are removed; excluded backlog remains unchanged.
- [ ] All three repository worktrees are audited and no changes are stranded.
- [ ] No push, tag, package publication, pin bump, or release occurs without explicit approval.
