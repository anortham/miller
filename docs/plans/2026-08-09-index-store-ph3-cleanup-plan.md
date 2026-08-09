# Index Store Ph3 Cleanup — Ph4/Ph5 Entry Plan

**Status:** A1-A7 complete 2026-08-09; the remaining work is Ph4 dashboard delivery and Ph5
physical-byte validation/default-on decision. This plan closes the execution drift identified by the
2026-08-09 Miller/Julie review before Ph4 work begins. Store mode remains explicit opt-in
(`MILLER_INDEX_STORE=1`); no new MCP tools, default-on change, release, tag, push, or publish is
part of this plan.

## Architecture quality

- Producer-owned physical accounting stays in `julie-extract-artifact`; it covers the store DB/WAL,
  resolution bases/deltas, and producer maintenance scratch. Miller consumes the public store
  process contract and never writes producer-owned databases. Miller-owned sidecars remain a
  separate Ph5 aggregate measurement rather than being silently omitted from the overall byte goal.
- Logical retention remains the candidate-selection input, but a completed GC records the
  composed physical bytes after checkpoint/vacuum and owns the persistent breach/escalation
  decision.
- Invalid store pointer metadata cannot authorize a legacy read. The pointer is removed only
  when it is structurally unusable, and bootstrap/refresh force a source rebuild before serving
  the legacy artifact. A structurally valid store that cannot be opened keeps its pointer and
  returns not-ready.
- Store level policy is evaluated against the pinned family-store read session when store mode
  is enabled. The legacy `symbols.db` is not an authority for store level completion.
- Language parity is verified by constructing the equivalence/crash/resolution fixtures from all
  38 `fixtures/extraction/<language>/basic` inputs, not by a hand-picked Rust fixture.

## Verification strategy

- Miller red/green scope: the smallest named xUnit test through
  `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter ...`;
  store subprocess tests remain `Category=Scale`.
- Julie red/green scope: the smallest named Cargo integration test with
  `RUSTUP_TOOLCHAIN=1.95.0-aarch64-apple-darwin`, using the producer's feature gates for store
  contract/crash tests.
- Final gates: Miller `scripts/test.sh`, focused store Scale tests, Julie maintenance and store
  contract tests, `dotnet build Miller.slnx -c Release`, and `git diff --check` in both worktrees.
- Evidence must include physical store bytes before/after GC, vacuum completion, consecutive
  retention-breach state, and the language set observed by each comparison.

## Parallel execution contract

| Task | Worktree | Ownership | Serialization |
|---|---|---|---|
| Contract amendments and reopened acceptance | Miller | v4 contract, Ph3 findings, this plan | First, so implementation targets are explicit |
| Physical GC/retention and capacity preflight | Julie | maintenance, store import/update/from-artifact, producer tests | Serial within Julie |
| Language-parity fixtures and comparison coverage | Julie | store equivalence/crash/mixed-version/resolution tests | After fixture helper, before final producer gate |
| Safe rollback and store level upgrade | Miller | rollback exporter/bootstrap/refresh/indexer tests | Serial within Miller |
| Cross-repo verification | Both | live binaries, focused suites, docs evidence | Last |

## Tasks

### 1. Mark the v4 contract amendments and reopen false acceptance claims

- Amend the frozen v4 header with a dated post-freeze register for stepped vacuum, physical
  retention/C7, capacity preflight, language-parity evidence, rollback safety, and store level
  deepening.
- Mark lock-order and freshness-token deviations as open until implementation evidence closes them;
  once closed, record the evidence in the v4.1 register and acceptance finding.
- Change the Ph3 acceptance finding from accepted to reopened/conditional and remove claims that
  are not backed by the current producer fixture or Miller rollback path.
- Correct stale producer-version references in the Ph3 wiring plan where they conflict with the
  pinned 2.31.1 evidence.

### 2. Make GC physically reclaim and report bytes

- Replace the single `PRAGMA incremental_vacuum(N)` execution with bounded page-stepping until
  the freelist is empty or the configured stage budget is exhausted, while preserving the
  checkpoint order and crash resumability.
- Capture producer-owned physical bytes after the final truncate checkpoint, including store DB/WAL,
  resolution bases/deltas, and scratch. The report names that ownership boundary so Ph5 can add
  Miller-owned sidecars to the end-to-end physical-byte measurement.
- Add a contract test that creates freelist pressure, applies GC, and proves file bytes shrink
  and the vacuum reaches the expected freelist state.

### 3. Enforce physical retention and capacity preflight

- Keep logical bytes for deterministic retention candidate selection, then remeasure physical
  bytes after GC and compare them with the target/ceiling contract.
- Persist the consecutive physical-target-breach count and emit an explicit compaction-escalation
  result after the tunable threshold; report the physical ceiling separately as the pressure guard.
- Run the documented capacity preflight on store import/update/from-artifact paths before
  allocating generation, WAL, spool, or scratch space. Refuse with the typed capacity result
  before mutating the store.
- Add focused capacity and escalation tests, retaining the existing generation-promotion
  preflight tests.

### 4. Make comparison evidence language-uniform

- Add one shared test fixture builder that copies the 38 basic language fixtures into isolated
  per-language paths.
- Use that matrix for store-vs-fresh equivalence, resolution/export comparison, crash recovery,
  and mixed-version comparison tests; assert the observed language set equals the producer's
  supported-language set.
- Keep the default suite feature-gated and preserve the existing Scale/contract split.

### 5. Make Miller rollback and store deepening truthful

- Extend `StoreRollbackExportResult` with a rebuild-required state. Invalid pointer shape/schema/
  root metadata removes the unusable pointer and forces a full source reconciliation before a
  legacy artifact can bind. A valid pointer whose store cannot open still propagates the failure
  and preserves the binding.
- Run the same rollback/export decision in cross-workspace refresh before any legacy scan; a
  malformed pointer cannot cause a refresh to serve the prior artifact.
- When store mode is enabled, read `IndexLevel` from `FamilyStoreReadSession` and schedule
  `ScanIntent.LevelUpgrade` from that snapshot. Add an end-to-end test for an L1 store that
  deepens to Full under progressive policy.

### 6. Verify and hand off

- Refresh both Miller indexes, run impact analysis on changed symbols, run focused red/green
  tests, then the required fast/build/Scale gates.
- Recheck every worktree's path, branch, commit, and dirty state. Leave changes uncommitted and
  do not touch the existing release-prep path.
- Report the remaining physical-byte aggregate and default-on decision as Ph5 gates; do not reopen
  the lock-order/freshness work after its implementation evidence is recorded.

## Completion evidence

- A1: bounded incremental vacuum, physical before/after fields, and a freelist/shrinkage contract
  test pass in `store_maintenance_contract`.
- A2: retention compares post-GC producer-owned physical bytes, persists the physical-target breach
  streak, and reports `compaction_required` after the configured threshold; Miller-owned sidecars
  remain in the Ph5 aggregate measurement.
- A3: import, update, and `--from-artifact` capacity preflight returns the typed refusal before
  store mutation; focused capacity tests pass.
- A4: equivalence, mixed-version, crash, and resolution comparison fixtures use all 38 producer
  languages and assert the exact supported set.
- A5: malformed store pointers force source reconciliation in bootstrap and cross-workspace
  refresh; valid-but-unopenable pointers remain bound and not-ready.
- A6: progressive level-up reads the family-store session and schedules Full from an L1 store;
  focused Miller Scale coverage passes.
- A7: the family-store freshness token carries store instance/view/generation, manifest generation,
  per-level stamps, resolution state, manifest hash, and store-log sequence; cache and sidecar stamps
  consume the same identity. The machine governor is released before sidecar convergence, and a
  family-scoped sidecar-converger lease serializes content/search/vector writes, shadow promotion,
  vector creation/recovery, and retained-generation GC. Focused A7 coverage, the fast suite, build,
  and full Scale suite pass. Ph5 physical-byte aggregation and default-on adoption remain open.
