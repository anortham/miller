# Semantic production-readiness repair implementation plan

**Design:** `docs/plans/2026-07-21-semantic-production-readiness-repair-design.md`

**Goal:** Repair the semantic serving experiment and lifecycle, then produce a measured go/no-go decision from a clean corpus.

**Branch:** `worktree-fusion-v2-eval`

**Commit mode:** Parallel tasks use `parallel-lead-commit`; workers do not commit. Serial evaluation may use `serial-worker-commit`. The lead stages exact reviewed files and commits each task separately.

## Architecture Quality

- **Affected modules:** search orchestration, content candidate lookup, vector artifact/session lifecycle, watcher filtering, workspace/CLI presentation, telemetry export/gating, release packaging, evaluation evidence.
- **Caller interfaces:** existing MCP/CLI `search`, `workspace`, `content read`, and `telemetry export`; no new MCP tool.
- **Test surface:** test through the public search/workspace/CLI/telemetry interfaces, with narrow indexing tests only for the new content lookup and session broker.
- **Complexity seams:** one optional semantic content lookup, one process-local semantic session broker, and one request-scoped serving assignment. Do not add another search facade or general orchestration layer.
- **Risk:** high. The slice affects causal measurement, returned search results, subprocess lifecycle, and release artifacts.
- **Deferred:** machine-wide session sharing; extractor-level worktree exclusion; semantic context/inspect/impact/trace/dead-code/dashboard features.

## Global invariants

1. `MILLER_SEMANTIC=off` performs zero vector/model work and preserves lexical output byte-for-byte.
2. Canary control and shadow output are lexical byte-for-byte across primary and rescue paths.
3. Treatment can introduce semantic-only symbol/content candidates; content lexical-zero rescue is supported.
4. No vector result serves unless its artifact identity and cursor are fresh both before and after embedding.
5. One Miller server process launches at most one resident semantic sidecar shared by queries and convergence.
6. Foreign-workspace read tools never generate embeddings.
7. Telemetry stores no raw queries, snippets, names, paths, or result content.
8. No new MCP tool and no parser/extractor behavior is added.

## Task 1: Make canary assignment govern the full search and add true content retrieval

**Owns:**

- `src/Miller.Server/Tools/SearchTool.cs`
- `src/Miller.Indexing/ITextContentSearchIndex.cs` only if the optional interface belongs beside it
- a new narrow semantic content lookup interface under `src/Miller.Indexing/`
- `src/Miller.Indexing/FtsTextContentSearchIndex.cs`
- focused search/content indexing tests

**Red tests:**

1. An eligible control query with a weak primary result never invokes semantic rescue and matches semantic-off bytes.
2. An eligible shadow query may execute shadow measurement but returns lexical bytes even when semantic ranks a different result.
3. `MILLER_SEMANTIC=shadow` never constructs a serving treatment arm.
4. Content lexical-zero plus a valid semantic chunk returns a materialized hit in treatment.
5. A semantic-only content hit still obeys content-kind and `excludeTests` filters.
6. An index without the optional materializer and every semantic fallback remain lexical byte-identical.

**Implementation:**

- Represent request serving policy once and carry it through `RunSymbolsWithCanary`, the rescue ladder, and content canary execution.
- Permit semantic rescue only for treatment or the existing explicitly non-canary production arm.
- Keep shadow measurement separate from serving output.
- Materialize semantic chunk IDs through the FTS-owned metadata map, union lexical and semantic membership, then use deterministic fusion/tie-breaking.
- Do not widen `ITextContentSearchIndex` for adapters that cannot materialize chunk IDs; prefer a separate optional capability.

**Worker verification:** focused `CanarySearchTests`, `CanaryContentSearchTests`, `SearchToolRescueTests`, and `FtsTextContentSearchIndexTests`.

## Task 2: Enforce vector freshness and share one process-local sidecar session

**Owns:**

- `src/Miller.Indexing/VectorSidecar.cs`
- `src/Miller.Indexing/Semantic/SemanticSearchArm.cs`
- a new session broker under `src/Miller.Indexing/Semantic/` or `src/Miller.Server/Hosting/`
- `src/Miller.Server/Hosting/VectorConvergeService.cs`
- `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
- focused vector/session/registration tests

**Red tests:**

1. Ready generation with a different live artifact ID returns `VectorsStale` before embedding.
2. Cursor lag outside the accepted freshness rule returns `VectorsStale` before embedding.
3. Artifact promotion or cursor change during embedding returns `VectorsStale` before KNN can serve.
4. Missing, stale, building, incompatible, disk-blocked, timeout, and circuit-open states remain distinguishable.
5. Concurrent query and convergence demand creates one session/child and shares restart/circuit state.
6. Semantic off never calls the broker factory.
7. Rebinding workspace A to B recreates root-bound generation cleanup state.

**Implementation:**

- Add a typed vector open/classification result rather than using `TryOpen` null as every failure.
- Pass the live workspace artifact/cursor expectation into query execution and revalidate after embedding.
- Introduce a singleton lazy broker used by `SemanticSearchArm` and `VectorConvergeService`; preserve query priority and bounded cancellation.
- Reset root-bound cleanup objects when the bound workspace identity changes.

**Worker verification:** focused `VectorSidecar*`, `SemanticSearchArm*`, `SemanticEmbeddingSession*`, `VectorConvergeServiceTests`, and `HostStartupRegistrationTests`.

## Task 3: Exclude nested worktrees from full scan and incremental watching

**Owns:**

- `.julieignore`
- `src/Miller.Indexing/WatchPathFilter.cs`
- `tests/Miller.Tests/Indexing/WatchPathFilterTests.cs`
- one existing scale test file if required to prove extractor scope

**Red tests:**

1. `.claude/worktrees/example/src/A.cs` is rejected by the watcher.
2. Similar non-worktree `.claude` paths remain eligible.
3. Both slash styles and mixed case behave according to existing platform normalization rules.
4. A real full extract of this repository contains no `.claude/worktrees/**` rows.

**Implementation:**

- Add the precise nested-worktree path to `.julieignore`.
- Add segment-pair filtering for `.claude/worktrees` without ignoring all `.claude` content.
- Do not change `julie-extractors` in this task.

**Worker verification:** focused watcher tests plus the narrowest scale extract assertion available.

## Task 4: Make telemetry attribution and promotion gates causal

**Depends on:** Tasks 1 and 2 contracts.

**Owns:**

- `src/Miller.Server/Telemetry/CanaryGateReport.cs`
- `src/Miller.Server/Telemetry/CanaryExport.cs`
- `src/Miller.Server/Telemetry/CanaryLedgerReader.cs` when required by the export shape
- `src/Miller.Server/Telemetry/CanaryTelemetry.cs` when required by typed attribution
- `src/Miller.Server/Tools/ContentTool.cs`
- telemetry contract/runbook docs and focused tests

**Red tests:**

1. `gate_passes` is false or indeterminate when identifier shadow is underpowered or regresses.
2. Export never pools different exact Miller versions, encoders, revisions, schemas, quantization, dimensions, or fusion profiles.
3. Export does not select an arbitrary first identity.
4. Content read hashes the resolved served path and records the resolved workspace, including cross-workspace reads.
5. A served semantic-only content hit can receive follow-up attribution.
6. Typed vector fallbacks reach ledger/export unchanged.

**Implementation:**

- Include the identifier-shadow verdict in the overall promotion verdict.
- Key analysis units by exact version plus complete semantic identity; emit explicit null/unknown strata instead of mixing.
- Stamp the content-read scope after resolution, using the same canonical path hash as search telemetry.
- Keep privacy properties unchanged.
- Correct the runbook so shadow is non-serving and the pinned default encoder is named once and accurately.

**Worker verification:** focused canary gate/export/ledger/content telemetry tests.

## Task 5: Align workspace facts, health, refresh, and normal CLI search

**Depends on:** Tasks 1 and 2.

**Owns:**

- `src/Miller.Server/Tools/WorkspaceTool.cs`
- `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`
- `src/Miller.Server/Tools/WorkspaceHealthFacts.cs`
- `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`
- `src/Miller.Server/Cli/CliDispatch.cs`
- focused workspace and CLI tests

**Red tests:**

1. Current-workspace status/health JSON and compact output show ready, stale, unavailable, pending, and semantic-off states truthfully.
2. Stale or failed vectors affect health warning/action; semantic off does not make lexical workspace health fail.
3. Current-workspace CLI refresh advances vectors when allowed or returns the resident-leader requirement; foreign refresh never generates.
4. Normal eligible CLI search matches MCP production-arm output for symbol and content queries.
5. Forced CLI arms remain explicit evaluation-only behavior and retain loud validation for unsupported modes.
6. Eligible normal CLI searches write privacy-preserving canary telemetry; lexical-off output stays byte-identical.

**Implementation:**

- Reuse `VectorSidecar` facts already used for registered workspaces, including pending files.
- Add vector health rules without making optional/off semantic state unhealthy.
- Route current refresh through the shared vector convergence boundary or report the actual leader requirement.
- Compose normal CLI semantic/canary behavior from the same policy and arm implementations as the server; do not fork ranking logic.

**Worker verification:** focused `WorkspaceToolTests`, `WorkspaceRenderTests`, workspace-health tests, `CliDispatchTests`, and CLI semantic/canary tests.

## Task 6: Smoke the exact packaged semantic payload

**Depends on:** Task 2 session contract.

**Owns:**

- `.github/workflows/release.yml`
- a new cross-platform smoke script and its tests, if a script keeps YAML small
- release-process/package documentation if the contract changes

**Red contract test:** packaged smoke fails when sqlite-vec is missing, sidecar identity is wrong, embedding dimension mismatches, or KNN cannot return the inserted vector.

**Implementation:**

- Run the smoke against the staged archive contents for every RID before archive upload.
- Load the staged sqlite-vec extension, launch the staged semantic sidecar with Miller's active pin, embed one fixed input, insert/query one vector, and verify identity/dimension/result.
- Keep download/network work outside the smoke; the package must already be self-contained.

**Worker verification:** script contract tests plus local-host smoke for the current RID. Other RIDs are verified by workflow structure and the next package-only run; do not dispatch a workflow or publish.

## Task 7: Rebuild cleanly and make the semantic go/no-go decision

**Depends on:** Tasks 1–6 and an assertion-green branch gate.

**Owns:**

- evaluation inputs/outputs under the existing `eval/` contracts
- `docs/findings/2026-07-21-semantic-production-readiness-evaluation.md`
- no production code unless a reproducible evaluation defect is first documented and routed back to its owning task

**Procedure:**

1. Force a fresh `symbols.db` rebuild and converge `search.db`, `content.db`, and `vectors.db` with the pinned BGE encoder.
2. Query the artifacts to prove zero nested-worktree files, symbols, content chunks, symbol vectors, and chunk vectors.
3. Re-run the frozen retrieval evaluation for lexical, semantic, and production hybrid arms without changing judgments or thresholds.
4. Run the production-arm replay at the real serving candidate depth, not the earlier offline-only dump shape.
5. Exercise explicit lexical-zero symbol and content queries and record whether semantic results receive accepted follow-up evidence.
6. Export a fixed canary window and run exact-version/identity gates, including identifier shadow and warm latency.
7. Record quality, empty-rate conversion, accepted follow-up, p50/p95 latency, sidecar RSS, vector disk size, and rebuild duration.
8. State one verdict: promote to a larger canary, keep evaluation-only, or stop semantic expansion and remain lexical-only.

**Decision rule:** semantic only advances when it clears every acceptance criterion in the approved design. Underpowered is not a pass. If there is no useful zero-result conversion or adjusted quality lift on the clean corpus, recommend stopping expansion.

## Execution order

- **Batch A, parallel:** Tasks 1, 2, 3.
- **Lead integration gate:** inline diff review, focused tests, exact-file commits, Release build.
- **Batch B, parallel:** Tasks 4, 5, 6.
- **Lead integration gate:** inline diff review, focused tests, exact-file commits, Release build.
- **Serial:** Task 7.

Workers receive the full task text and use Miller for orientation before file reads. They write tests before production changes, do not edit this plan, do not touch files owned by another active task, and report worktree path, branch, HEAD, and dirty state.

## Verification plan

### Worker red-green

Each task runs its named focused tests. A worker may build its project once to support `--no-build` focused loops. Workers do not run the full suite concurrently.

### Affected-change gates

After each batch, the lead runs:

```bash
dotnet build Miller.slnx -c Release
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-build --filter "<affected semantic groups>"
```

### Branch gate

Because indexing, sidecar execution, and packaging change, the final code gate is:

```bash
scripts/test.sh all
dotnet build Miller.slnx -c Release
dotnet test eval/retrieval-eval/tests -c Release
```

The branch is not complete unless all assertions pass, the fast-suite wall-clock tripwire passes on a quiet run, the scale suite passes, and Release builds with zero warnings/errors. The observed assertion-green but wall-clock-red baseline is recorded as environmental debt, not waived for completion.

### Final review

After the branch gate, review the complete diff against this plan, the design, ADR-0003, semantic telemetry contracts, CLI Eros contract, and release workflow. Any real finding is fixed and the affected gates are rerun before the evaluation verdict.
