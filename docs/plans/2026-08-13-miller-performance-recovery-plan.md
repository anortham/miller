# Miller Performance Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Restore Miller startup, indexing, and relationship-tool performance to the published Linux and Windows budgets without weakening freshness, relationship features, or output correctness.

**Architecture:** Treat the slowdown as a coupled producer pipeline plus two isolated read-path costs: a dead claim can block reuse, force repeated leader work, and feed an overgrown resolution overlay; context adds pre-budget N+1 evidence reads; impact reaches a bounded graph but can use a poor SQLite access path. Instrument each phase first, repair it at the existing ownership boundary, retain full resolution and current tool output as correctness oracles, and make every optimization independently reversible.

**Tech Stack:** .NET 10, C#, Rust, SQLite/FTS5, xUnit, Cargo integration tests, Python 3 performance harnesses, Linux, and Windows PowerShell validation.

**Architecture Quality:** High-risk cross-repository recovery. SQL lifecycle and resolution remain in `julie-extract`; Miller receives one narrow batch-read seam and measured query-shape changes behind existing tool interfaces. No generic relationship service, unbounded cache, new MCP tool, or public contract is introduced.

## Global Constraints

- Preserve `StoreContractVersion = 1`, existing MCP/CLI schemas, lexical-only byte identity, and `reference_mode=off` byte identity.
- Keep semantic retrieval and family-store mode default-on; their existing off switches remain rollback paths, not the recovery strategy.
- Warm `inspect` must complete within 500 ms on the development machine and 2 s on constrained Windows.
- Warm `context`, `impact`, and `trace` must complete within 2 s on the development machine and 5 s on constrained Windows.
- Cold family-store reads must complete within 5 s on the development machine and 10 s on constrained Windows.
- Idle proportional set size must stay at or below 350 MB; peak proportional set size must stay at or below 600 MB.
- One-file resolution must complete within 5 s on the development machine and 10 s on constrained Windows.
- Full real-Miller resolution must complete within 60 s on the development machine and 120 s on constrained Windows.
- A byte-identical producer retry must complete within 2 s on the development machine and 5 s on constrained Windows.
- A replay workload must invoke the production path named by its ID: MCP startup rows start the stdio host, producer resolve rows submit a producer resolve request, and CLI status/leader reads are diagnostic controls only.
- Workload timeouts are observation failsafes, not performance budgets. A workload timeout must exceed its published hard budget and the slowest observed baseline long enough to capture phase evidence.
- Preserve deterministic ordering, coverage, truncation fields, selected counts, and provenance for untruncated relationship-tool outputs.
- Any extraction-backed behavior must pass `SELECT language, kind, COUNT(*) FROM symbols GROUP BY 1,2` on a real supported-language extract, plus the equivalent grouping on the specific new extraction table when one is introduced.
- Every full-resolution optimization must prove the same exact digest as the existing full resolver before becoming the default.
- Never classify exit code 135 as OOM without captured process or operating-system evidence; only exit 137 retains the current OOM retry policy.
- Never mutate the live coordinator or store during development. Stop writers and create a verified whole-family snapshot containing `CURRENT`, the coordinator, every generation and resolution base referenced by current/coordinator state, and required sidecars. For a source database with a nonempty WAL, capture content and metadata facts, stream-copy the database/WAL/SHM triplet into a private temporary shadow, recapture and compare every source fact, then use SQLite backup from that stable shadow; never open, chmod, raw-promote, or checkpoint the source in place. The destination snapshot must be WAL/SHM-free and validated before atomic promotion. Reflink is optional only for WAL-free files on Linux and never assumed on Windows.
- Do not run concurrent Rust builds, .NET builds, or performance workloads on the acceptance machine.
- Do not rerun a passing expensive scope on an unchanged commit; reuse its verification-ledger entry.
- Do not add a new MCP tool, public CLI verb, Store Contract version, dependency, release, tag, push, or pin bump without applicable explicit approval.
- Preserve Windows compatibility; no recovery may depend on Unix signals, `/proc`, shell-only paths, or rename semantics without a Windows equivalent.
- Reconcile the existing `feature/store-incremental-resolution-consumer` worktree and its three base-rotation tests; do not abandon or duplicate that work.

---

## Problem Statement and Evidence

The project is not uniformly slow. Warm `inspect` measured about 172-176 ms and `trace` about 68-102 ms, while a fixed `context` request rose from about 1.38 s at `reference_depth=0` to about 11.93 s at `reference_depth=1` with byte-identical 2,665-byte output. A live leader startup required about 28.5 s, but a warm reader bootstrapped in about 333 ms wall time. This isolates reader startup and ordinary reads from the regression, but it does not prove sidecar rebuilding: the producer import, resolve, bind, content/search convergence, metric snapshot, and vector signaling phases must be timed individually before Task 5 changes code.

Producer evidence is stronger. The 2.443 GB family store contained about 1.12 GB of live resolution tables and indexes, 154 resolution deltas, about 1.78 million identifier-delta rows, about 127,840 pending-delta rows, and about 352 MB of `exact_gap_json`. It reported no freelist pages, so the growth is retained live history, not ordinary SQLite fragmentation. Recent exact resolution sessions took about 164-172 s; coordinator history showed 100 resolves averaging about 255 s with a 1,252 s maximum.

The incident also contained a stranded claimed import owned by a dead PID, an expired writer lease, an unbound generation, eight exit-135 failures, and 86 historical partial-resolution bootstrap failures. Current `julie-extractors/main` already has generic dead-lease takeover logic, so producer work begins with a characterization gate. If current source repairs the exact import state, the missing work is adoption and pinning rather than duplicate recovery code.

Query plans show delta arms using indexes while bounded reverse relationship reads can scan roughly 1.7 million active-base rows. There is no `sqlite_stat1` or `sqlite_stat4` evidence. Adding `ANALYZE`, `PRAGMA optimize`, or an index without before/after plans is prohibited.

This plan supersedes only residual work from the August 11 family-store recovery plan. Lazy family reads, targeted family reference SQL, bounded term rescue, writer heartbeat, extraction job caps, and the existing resolution crossover remain the baseline.

## Architecture Quality

**Affected modules:** `julie-extract-artifact` coordinator/store maintenance, `julie-extract-cli` import/resolver flows, Miller store coordination and startup convergence, family-store resolution reads, `ReferenceEvidenceReader`, `ContextTool`, `SqliteSymbolGraphIndex`, and `ImpactTool`.

**Caller-facing interface:** Existing MCP and CLI payloads and Store Contract v1 remain unchanged. Internal additions are limited to a batched `ReferenceEvidenceReader` operation, query-plan/count instrumentation through existing graph interfaces, and producer result facts that fit the current report envelope.

**Depth/locality check:** Claims, fencing, resolution, rebase, garbage collection, and their SQL stay in the producer or `Miller.Indexing`. Context token decisions stay in `ContextTool`; graph limits stay at `ISymbolGraphReachability`/`SqliteSymbolGraphIndex`. No host or UI layer gains SQLite policy.

**Test surface:** Store behavior is proved through coordinator/import/resolve contracts; reference batching through `ReferenceEvidenceReader` and `ContextTool`; traversal through `ImpactTool` and the SQLite graph implementation; startup through `IndexerService`; performance through one isolated cross-platform replay.

**Seams/adapters:** Reuse `IJulieStoreClient`, `IWorkspaceReadSession`, `ReferenceEvidenceBundle`, `ISymbolGraphReachability`, Store Contract v1 reports, and current phase telemetry. Add one batch evidence method. Retain a singular delegate adapter for pure context fixtures; do not add another graph service or budget abstraction unless measured evidence proves the existing interface cannot enforce its current bound.

**Rejected shortcuts:** Raising timeouts; disabling features; treating exit 135 as OOM; loading the graph or resolution base into memory; adding `IRelationshipEnrichmentService`; global caches; blind planner tuning; final limiting before risk ranking; Windows-specific behavior forks; a new MCP tool.

**Architecture risk:** high.

## Recovery Order and Rollback

1. Establish an isolated replay and ledger before changing behavior.
2. Correct the workload contract and capture a pre-change copied-store baseline.
3. Repair or prove import and resolve claim recovery before optimizing resolution.
4. Prove which no-change producer or leader phase remains expensive, then fix only that phase.
5. Characterize the already-shipped incremental resolver and stop if its one-file parity and timing gates pass.
6. Bound store growth and prove safe base rotation and garbage collection before changing read SQL.
7. Capture post-rotation read plans, then make only the measured query/index/statistics change.
8. Batch context evidence reads and make it default only after lexical copied-store replay passes.
9. Run Linux and Windows gates, then adopt the verified producer through the normal approval-gated pin workflow.

Each slice builds and tests independently. Producer optimizations retain the full resolver as fallback. Miller query optimizations retain existing read paths behind internal rollback switches until replay parity is recorded.

## Execution Worktrees and Rollback Switches

- Start Miller execution from current `main` in one worktree on branch `feature/performance-recovery`; the present `docs/perf-recovery-plan` branch supplies this plan only.
- Start Julie execution from current `main` in one worktree on branch `feature/miller-performance-recovery-producer`; inventory and three-way-diff every existing Julie performance worktree before moving changes.
- Treat Miller `main` as authoritative when reconciling `feature/store-incremental-resolution-consumer`. Transplant only coverage missing from `main`, preserving later cursor/base-rotation tests.
- `JULIE_STORE_RESOLUTION_DELTA=off` is the existing producer fallback to the full resolver.
- Add temporary internal switch `MILLER_CONTEXT_REFERENCE_BATCH=off`. Its default remains off until focused parity and lexical copied-store replay pass, changes to on in the integrating slice, and is tested in both modes. Task 3's first implementation enabled the batch path when unset; the correction slice must restore default-off before replay. Do not add an impact query switch unless Task 7B proves a new query, index, or statistics maintenance step is required.
- Remove neither legacy path in this recovery release. A later cleanup requires its own evidence that the fallback has not been used and its own approval.

## Verification Strategy

**Project source of truth:** Miller `AGENTS.md`, `PERF.md`, `Directory.Build.props`, `tests/Miller.Tests/Miller.Tests.csproj`, and `scripts/test.sh`/`scripts/test.ps1`; Julie Extractors `AGENTS.md`, Cargo manifests, store contract tests, and `cargo xtask performance`.

**Worker red/green scope:** Run only the exact xUnit filter supplied in each task, the named Cargo integration target with its required feature and exact filter when available, or `python scripts/tests/test_perf_recovery.py`.

**Worker ceiling:** One focused test class or Cargo integration target plus the directly changed project build. Workers do not own full suites, Windows acceptance, or real-store replay unless assigned that exact scope.

**Worker gate invariant:** Each task names the behavior its focused command proves: fencing, digest parity, atomic rotation, output parity, bounded query work, truthful truncation, or no-change startup admission.

**Lead affected-change scope:** After a producer batch, run changed crate tests and `cargo clippy --workspace --all-targets --all-features -- -D warnings`. After a Miller batch, run named affected classes and `dotnet build Miller.slnx -c Release` with the matching restored producer.

**Branch gate:** Miller: `scripts/test.sh all` on Linux and `scripts/test.ps1 all` on Windows. Julie: documented format, clippy, workspace, and store-performance gates. Run the fixed replay three times on quiet Linux and constrained Windows; every median must meet its hard budget.

**Security scope:** `security-secrets`: `gitleaks detect --source . --no-banner`; `security-deps`: `dotnet list package --vulnerable --include-transitive` in Miller and `cargo audit` in Julie. Missing tooling is a branch-gate failure, not a silent skip.

**Replay/metric evidence:** Hard gates are wall time, semantic output invariants, exact resolver digest parity, automatic stale-claim recovery, old-owner fencing, bounded SQL/read counts, truthful truncation, Linux PSS at or below 350 MB idle/600 MB peak, and Windows `PROCESS_MEMORY_COUNTERS_EX.PrivateUsage` at or below the independently enforced 350 MB/600 MB ceilings. PSS and PrivateUsage are not compared to each other. Windows working set is recorded report-only. CPU, I/O, phase durations, row counts, database size, query plans, and cache counts are report-only unless a task sets an explicit threshold.

**Escalation triggers:** Run Scale for indexing, startup, store-read, graph-read, or pin changes. Run real language extraction for resolver-input changes. Run Windows filesystem/process tests for claims, promotion, rotation, supervision, or paths. Run semantic broker soak for startup or sidecar-admission changes.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope, commit SHA, result, and timestamp. Replay entries also record OS, CPU, RAM, filesystem, producer SHA/version, Miller SHA, store/view/generation, warm state, each timing, median, PSS, I/O, output digest, query counts, and report-only metrics. Reuse passing evidence for the same commit and environment.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Lock replay safety and ledger | None - serial | Miller: create `scripts/perf-recovery.py`, `scripts/tests/test_perf_recovery.py`, `scripts/benchmarks/perf-recovery-workloads.json`; modify `PERF.md` | Yes | All tasks consume its workloads and evidence schema. |
| Task 1B: Correct workload fidelity and freeze the baseline | None - serial | Miller replay harness, workload manifest, replay tests, copied-store snapshot helper/evidence, plus only the context batch default and its focused test | Yes | Reopens the replay contract before Tasks 5-8 because Task 1 proved safety but several rows exercised diagnostic CLI paths rather than the named production paths. |
| Task 2: Recover and fence claims | Batch A | Julie: `store/coordinator.rs`, `store_coordinator_contract.rs`; Miller: `JulieExtractRunner.cs`, `JulieExtractExceptions.cs`, runner/scan-failure tests | No | None - safe parallel batch. |
| Task 2B: Validate resolve-claim coverage and copied-field healing | None - serial | Julie coordinator/CLI contract tests only for a proven coverage gap; copied-store recovery and Windows-liveness evidence | Yes | Requires Task 1B's verified snapshot; validates the already-shipped resolve recovery against the copied incident state and Windows liveness. |
| Task 3: Batch context evidence | Batch A | Miller: `ReferenceEvidenceReader.cs`, `ReferenceEvidenceReader.FamilyStore.cs`, `ContextTool.cs`, reader and context tests | No | None - safe parallel batch. |
| Task 4: Characterize impact relationship reads and add telemetry | Batch A | Miller: `SqliteSymbolGraphIndex.cs`, `FamilyStoreReadSession.cs`, `ImpactTool.cs`, impact and SQLite graph/read-session tests | No | None - safe parallel batch. |
| Task 5: Measure and repair the expensive no-change phase | None - serial | Julie manifest/from-artifact/executor paths only for failed reuse; Miller `StoreWorkspaceCoordinator.cs`, `IndexerService.cs`, `IndexerSidecarConverger.cs`, coordinator/indexer/sidecar tests | Yes | Requires Tasks 1B and 2B; instrumentation may land earlier, but no behavior repair is accepted before the faithful baseline. |
| Task 6: Prove the shipped incremental resolver or stop | None - serial | Julie: scope/equivalence/report/performance tests and evidence; modify `resolution.rs:1969-2105,3081-3155,3384-3445` or `store/resolve.rs:114-292,1200-1230` only for a characterized failure | Yes | Requires trustworthy claims and Task 5 no-change evidence; may exit green with no production edit. |
| Task 7A: Rebase, collect, and rotate | None - serial | Julie maintenance/rebase and tests; Miller family read-session and base-rotation tests | Yes | Requires Task 6, a pre-maintenance snapshot, and a three-way reconciliation of the existing consumer branch against current `main`. |
| Task 7B: Optimize measured bounded reads | None - serial | Julie resolution-base schema/statistics only if proved; Miller `FamilyStoreReadSession`, family evidence reader, graph/resolution query-plan tests | Yes | Runs only after 7A so lifecycle compaction and read-path tuning have separate evidence and rollback commits. |
| Task 8: Close Linux and Windows gates | None - serial | Miller `PERF.md`, `docs/README.md`, `docs/findings/2026-08-13-performance-recovery-verification.md`, parity scripts only if defective; approval-gated pin inputs | Yes | Integrates all prior slices. |

### Task 1: Lock Replay Safety and Ledger

**Files:**
- Create: `scripts/perf-recovery.py`
- Create: `scripts/tests/test_perf_recovery.py`
- Create: `scripts/benchmarks/perf-recovery-workloads.json`
- Modify: `PERF.md`

**Interfaces:**
- Consumes: Miller CLI JSON, `MILLER_HOME`, current budgets, and a caller-supplied store copy.
- Produces: CLI options `--workloads`, `--out`, `--miller`, `--workspace`, `--store-copy`, `--live-store`, `--only`, and `--runs`, with one JSONL record per attempt.

**Contract inputs:** The safety harness initially locks IDs `startup.reader.warm`, `startup.leader.no_change`, `workspace.open.no_change`, `producer.retry.identical`, `producer.resolve.one_file`, `producer.resolve.full`, `tool.inspect.warm`, context depth/batch controls, `tool.impact.bounded`, and `tool.trace.warm`; Task 1B makes their execution paths authoritative before they are used for decisions. `workspace.open.no_change` closes PERF-010 and uses the cold family-read 5 s/10 s budget. Require the original `--live-store`, prove the staged workspace's active family or legacy artifact is the exact `--store-copy`, isolate `MILLER_HOME`, and use shell-free arguments. `--only` selects a nonempty unique subset in manifest order and `--runs` overrides measured attempts without changing warmups. `MILLER_HOME` does not isolate the machine-global Windows semantic broker: serialize semantic workloads, record broker identity/health, and run lexical-only controls with `MILLER_SEMANTIC=off` rather than assuming home isolation.

**File ownership:** Miller: create `scripts/perf-recovery.py`, `scripts/tests/test_perf_recovery.py`, `scripts/benchmarks/perf-recovery-workloads.json`; modify `PERF.md`

**Serialization required:** Yes

**Dependency reason:** All tasks consume its workloads and evidence schema.

**Step 1: Write failing safety and parity tests**

```python
def test_refuses_live_store_path(self):
    request = ReplayRequest(store_copy=self.live_store, live_store=self.live_store)
    with self.assertRaisesRegex(ValueError, "store-copy must not be the live store"):
        validate_request(request)

def test_same_depth_batch_pair_records_output_parity(self):
    result = compare_pair(self.batch_off, self.batch_on)
    self.assertTrue(result.output_digest_match)
    self.assertEqual(self.batch_on.wall_ms - self.batch_off.wall_ms, result.delta_wall_ms)
```

**Step 2: Verify red**

Run: `python scripts/tests/test_perf_recovery.py`

Expected: FAIL because the harness types do not exist.

**Step 3: Implement the record and isolated process runner**

```python
@dataclass(frozen=True)
class ReplayRecord:
    workload_id: str
    platform: str
    commit: str
    producer_version: str
    wall_ms: int
    cpu_ms: int
    peak_rss_bytes: int
    peak_pss_bytes: int | None
    output_sha256: str
    exit_code: int
    timed_out: bool
    hard_gate_passed: bool
```

On Windows, collect `PrivateUsage` for the hard memory gate through the native process-memory API. Other unavailable report-only metrics are null, never zero. Record workspace, view, generation, I/O, environment, warm state, producer import/resolve/bind timings, content/search/metric/vector phase timings, and broker identity in the complete type.

**Step 4: Add the fixed manifest and open PERF rows**

```json
{
  "id": "tool.context.references.depth1.batch_on",
  "execution_kind": "miller_cli",
  "command": ["context", "--json", "--reference-depth", "1"],
  "warmups": 1,
  "runs": 3,
  "hard_budget_ms": {"development": 2000, "windows": 5000},
  "parity_with": "tool.context.references.depth1.batch_off",
  "environment": {"MILLER_SEMANTIC": "off", "MILLER_CONTEXT_REFERENCE_BATCH": "on"}
}
```

Add rows for stranded import, resolution/store growth, context N+1, impact graph/query work, and leader no-change convergence. Preserve historical entries.

**Step 5: Verify green**

Run: `python scripts/tests/test_perf_recovery.py`

Expected: PASS for live-store rejection, isolation, timeout recording, parity, and nullable platform metrics.

**Step 6: Apply commit mode**

- `serial-worker-commit`: commit owned files after verification and record the SHA.

### Task 1B: Correct Workload Fidelity and Freeze the Baseline

**Files:**
- Modify: `scripts/perf-recovery.py`
- Modify: `scripts/tests/test_perf_recovery.py`
- Modify: `scripts/benchmarks/perf-recovery-workloads.json`
- Create: `scripts/perf-store-snapshot.py`
- Modify only to restore the planned rollback default: `src/Miller.Server/Tools/ContextTool.cs`
- Test: `tests/Miller.Tests/Server/ContextToolTests.cs`

**Interfaces:**
- Consumes: Task 1's safe process runner, Miller's `serve` stdio host, `julie-extract store import`, `julie-extract store resolve`, Store Contract v1 reports, and Task 5's observation-only phase records.
- Produces: Execution kinds `miller_cli`, `mcp_bootstrap`, and `julie_store`; a verified whole-family snapshot; lexical context controls; one immutable pre-change JSONL baseline.

**Contract inputs:** `workspace leader --json` and `workspace status --json` are diagnostic CLI reads and do not construct the Generic Host. `julie-extract store resolve` has `--store`, `--view`, optional request identity, timeout, and JSON flags; it derives incremental scope from store state and has no invented file-scope flag. `JULIE_STORE_RESOLUTION_DELTA=off` is the full-resolver oracle. Observation-only Task 5 instrumentation may land before this task, but no Task 5 behavior repair may precede the baseline.

**File ownership:** Miller replay harness, workload manifest, replay tests, copied-store snapshot helper/evidence, plus only the context batch default and its focused test

**Serialization required:** Yes

**Dependency reason:** Reopens the replay contract before Tasks 5-8 because Task 1 proved safety but several rows exercised diagnostic CLI paths rather than the named production paths.

**Step 1: Make workload execution paths testable**

Add manifest validation that rejects a startup workload unless `execution_kind=mcp_bootstrap`, rejects a producer resolve workload unless `execution_kind=julie_store`, and rejects any workload whose timeout is not greater than its hard budget. The MCP runner starts `miller serve`, performs initialize/initialized over stdio, waits for the phase record that closes the named startup path, records it, and terminates the host through the normal protocol/process boundary. A warm-reader row keeps a leader host alive and measures a second host against the same copied store. A Windows record with a configured memory gate and null `PrivateUsage` is blocked/failed, never silently passed.

**Step 2: Create and validate a whole-family snapshot**

`perf-store-snapshot.py` requires explicit source and destination roots, refuses aliases, refuses live or unknown owners, and permits definitively dead/stale claims so the incident remains reproducible. For every SQLite database it captures source content and metadata facts, stream-copies the database/WAL/SHM triplet into a private temporary shadow, verifies every source fact stayed stable, and uses SQLite backup from the shadow so committed WAL rows are incorporated without opening or mutating the source; raw copy is allowed only for WAL-free non-database files. It copies `CURRENT`, `coord.db`, every generation and resolution base referenced by current/coordinator state, sidecars, and required store-owned files without shell-specific commands, then verifies destination databases pass `quick_check`, no destination WAL/SHM remains, and the completed report/digest before atomic promotion. On Windows it uses ordinary local filesystem destinations; antivirus state is recorded, never changed automatically.

**Step 3: Replace diagnostic workloads with faithful workloads**

- `startup.leader.no_change`: first isolated MCP host through completed startup-delta phase.
- `startup.reader.warm`: second MCP host while the first owns leadership.
- `producer.retry.identical`: repeat the exact producer import request/idempotency contract against a disposable snapshot.
- `producer.resolve.one_file`: on its own snapshot, change one fixture file, import without resolve, then call `julie-extract store resolve`; assert the report chose incremental scope.
- `producer.resolve.full`: on its own snapshot, create an unresolved full generation, then call the same resolve command with `JULIE_STORE_RESOLUTION_DELTA=off`; set the observation timeout above the 1,252 s historical maximum while retaining 60 s/120 s as the hard budget.
- `workspace.open.no_change`: pre-register and converge the staged workspace before measurement so the row measures open/read, not an accidental extract.

Each mutating workload receives a fresh snapshot. No measured retry reuses state mutated by a prior workload.

**Step 4: Correct context controls and rollback semantics**

Run the depth-0 and depth-1 N+1 pair with `MILLER_SEMANTIC=off`; remove byte-identity between different depths. Compare stable pivots, tier-0/tier-1 selections, ordering, and truncation semantics, and record added depth-1 bytes. Add a separate semantic-on depth-1 row. For the batching optimization, compare batch-off versus batch-on at the same depth and require byte-identical output. Restore `MILLER_CONTEXT_REFERENCE_BATCH` to default-off until that copied-store lexical gate passes.

**Step 5: Capture the immutable baseline**

Run three quiet Linux attempts for every non-destructive row and one complete observed attempt for the historical long full-resolve row. Record source/destination snapshot hashes, WAL state, Miller SHA, producer SHA/version, view/generation, filesystem, each phase, resolver scope, and hard-budget result. A timeout records failure and phase evidence; it never erases the row.

**Step 6: Verify and apply commit mode**

Run: `python scripts/tests/test_perf_recovery.py`

Run: `dotnet test --filter "FullyQualifiedName~ContextToolTests"`

Expected: path-kind validation, snapshot refusal/integrity, MCP framing, producer invocation, timeout separation, semantic invariants, batch off/on identity, and missing-Windows-memory failure pass. Commit the corrected harness, default-off rollback, and baseline metadata/evidence; do not commit copied store data.

- `serial-worker-commit`: checkpoint, commit owned files, and record the SHA.

### Task 2: Recover and Fence Stranded Producer Claims

**Files:**
- Modify: `crates/julie-extract-artifact/src/store/coordinator.rs:791-965,1058-1135,1321-1365,1821-1885`
- Modify: `crates/julie-extract-artifact/tests/store_coordinator_contract.rs:700-820,1005-1305,1707-1775`
- Modify: `src/Miller.Indexing/JulieExtractRunner.cs:390-455`
- Modify: `src/Miller.Indexing/JulieExtractExceptions.cs:1-70`
- Modify: `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs`
- Modify: `tests/Miller.Tests/Freshness/ScanFailurePolicyTests.cs`

**Interfaces:**
- Consumes: Writer lease, owner PID/start identity, heartbeats, request state, `claim_resolve`, and `claim_request`.
- Produces: One fenced takeover rule for import and resolve; Miller-side subprocess diagnostics that preserve exit 135 without OOM inference.

**Contract inputs:** Recreate dead owner, expired lease, claimed import, stopped heartbeat, and unbound generation. Current source may pass unchanged; green means adoption/pin gap, not permission to duplicate recovery.

**File ownership:** Julie: `store/coordinator.rs`, `store_coordinator_contract.rs`; Miller: `JulieExtractRunner.cs`, `JulieExtractExceptions.cs`, focused runner and scan-failure tests

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**Step 1: Write the incident characterization**

```rust
#[test]
fn dead_import_owner_with_expired_writer_lease_is_requeued_and_fenced() {
    let temp = TempDir::new();
    let layout = layout(temp.path());
    let mut owner = StoreCoordinator::open_with_liveness(&layout, FixedLiveness(true)).unwrap();
    owner.enqueue(CoordinatorRequest::new("request-a", "idem-a", RequestKind::Import, "{}", "requester", 10_000, 1)).unwrap();
    owner.try_acquire_or_takeover(LeaseHolder::new("holder-a", "2.33.2", 41), 10).unwrap();
    claim_request_for_test(layout.coordinator_db(), "request-a", "holder-a", 10);
    let mut replacement = StoreCoordinator::open_with_liveness(&layout, FixedLiveness(false)).unwrap();
    assert!(replacement.try_acquire_or_takeover(LeaseHolder::new("holder-b", "2.33.2", 42), 11).unwrap().acquired());
    let recovered = replacement.request("request-a").unwrap();
    assert_eq!(recovered.state.as_str(), "queued");
    assert_eq!(recovered.claim_owner, None);
}
```

Extract the existing contract's repeated claim setup into `claim_request_for_test`; use production coordinator APIs for takeover and assertions.

**Step 2: Run the characterization**

Run: `cargo test -p julie-extract-artifact --test store_coordinator_contract dead_import_owner_with_expired_writer_lease_is_requeued_and_fenced`

Expected: PASS proves an adoption gap; FAIL proves import recovery needs repair. Record the branch.

**Step 3: If red, centralize recovery in the generic claim transaction**

```rust
fn blocking_claim_is_recoverable(claim: &ClaimedRequest, lease: &WriterLease, now: Timestamp, processes: &dyn ProcessLiveness) -> bool {
    lease.is_expired(now)
        && (claim.heartbeat_is_stale(now, CLAIM_STALE_AFTER)
            || !processes.is_same_process(claim.owner_pid, claim.owner_started_at))
}
```

Fence, requeue, and claim atomically. The prior lease token must fail before publish.

**Step 4: Preserve termination facts in Miller's subprocess boundary**

```rust
public sealed record JulieProcessTermination(
    int? ExitCode,
    int? Signal,
    string? PlatformStatus,
    string? StderrTail);
```

Populate this where Miller owns the child process and stderr. Keep exit 135 as `unknown_failure` unless captured OS evidence proves a category; preserve the existing exit-137-only job clamp.

**Step 5: Verify green**

Run: `cargo test -p julie-extract-artifact --test store_coordinator_contract`

Run: `dotnet test --filter "FullyQualifiedName~JulieExtractRunnerTests|FullyQualifiedName~ScanFailurePolicyTests"`

Expected: Current owners are retained, dead owners recover, and fenced owners cannot publish.

**Step 6: Apply commit mode**

- `parallel-lead-commit`: do not commit; hand verified diff or green characterization evidence to the lead.

### Task 2B: Validate Resolve-Claim Coverage and Copied-Field Healing

**Files:**
- Modify only for a proven coverage gap: `crates/julie-extract-artifact/tests/store_coordinator_contract.rs`
- Modify only for a proven coverage gap: the existing CLI contract containing `claimed_resolve_holds_no_writer_lease_and_a_short_update_completes`
- Modify only if existing behavior is red: `crates/julie-extract-artifact/src/store/coordinator.rs`
- Create: copied-store recovery evidence under Miller `artifacts/perf/` during execution; do not commit the copied database

**Interfaces:**
- Consumes: `claim_resolve`, resolve-request heartbeat/owner identity, process liveness, the unique claimed-resolve constraint, and Task 1B's immutable snapshot.
- Produces: An inventory and replay of existing dead/stale resolve-claim proof, a Windows liveness matrix, and a production-API heal of the copied incident state.

**Contract inputs:** The observed August 14 blocker was a claimed import with an expired writer lease; do not relabel it as a resolve. Current producer tests already cover stale/dead resolve takeover/reaping and prove that a claimed resolve has no writer lease while a short update completes. Inventory those exact tests before adding anything. Inventory and three-way-diff `fix/store-import-idempotent-retry` at `bc47d4f2`, `fix/store-resolution-scope` at `fb31da08`, `perf/store-resolution-query-amplification` at `ab3aa957`, and `fix/store-writer-heartbeat` at `0500ab1e` against the producer execution branch before modifying shared paths.

**File ownership:** Julie coordinator/CLI contract tests only for a proven coverage gap; copied-store recovery and Windows-liveness evidence

**Serialization required:** Yes

**Dependency reason:** Requires Task 1B's verified snapshot; validates the already-shipped resolve recovery against the copied incident state and Windows liveness.

**Step 1: Inventory and replay resolve-claim characterization**

Run and map the existing coordinator dead/stale resolve tests and the CLI `claimed_resolve_holds_no_writer_lease_and_a_short_update_completes` contract to these invariants: owner B recovers after the stale/dead threshold, `uidx_coord_one_claimed_resolve` no longer blocks progress, resolve holds no writer lease, and owner A cannot heartbeat or publish. Add a test or production repair only when an invariant is absent or red; green existing coverage is the preferred result.

**Step 2: Characterize Windows process liveness**

Exercise the Windows `tasklist` adapter for same live process, dead PID, reused PID/start-identity mismatch, and probe failure/`Unknown`. A known-dead owner must recover without waiting on a Unix signal; `Unknown` must follow the explicit conservative timeout policy and remain observable rather than being treated as alive forever.

**Step 3: Heal only the copied field state**

Start the current producer against Task 1B's snapshot and submit the blocked operation through the normal coordinator API. Record the original request state, recovered state, new fencing token, unique-index outcome, view resolution state, and elapsed time. No SQLite hand edit is accepted as recovery evidence, and the live family remains untouched.

**Step 4: Verify and apply commit mode**

Run: `cargo test -p julie-extract-artifact --test store_coordinator_contract -- --test-threads=1`

Run the Windows coordinator contract target serially on Windows in Task 8.

Expected: existing import and resolve dead-owner shapes recover without duplicate logic, unknown liveness is bounded and visible, old owners are fenced, and the copied incident state converges through production APIs.

- `serial-worker-commit`: checkpoint and commit the focused contract/repair, or a test/evidence-only characterization when production already passes.

### Task 3: Batch and Budget Context Relationship Evidence

**Files:**
- Modify: `src/Miller.Indexing/ReferenceEvidenceReader.cs:8-12,20-52,153-330,391-520`
- Modify: `src/Miller.Indexing/ReferenceEvidenceReader.FamilyStore.cs:20-120,224-370`
- Modify: `src/Miller.Server/Tools/ContextTool.cs:235-245,454,1009-1120,2281-2435`
- Modify: `tests/Miller.Tests/Indexing/ReferenceEvidenceReaderTests.cs:403-715`
- Modify: `tests/Miller.Tests/Server/ContextToolTests.cs:671-710,980-1605,2055-2135`

**Interfaces:**
- Consumes: `IWorkspaceReadSession`, the hot path's singular `ReferenceEvidenceReader.Read` and `ReadOutgoing` operations, `ReferenceEvidenceBundle`, exact/fallback bounds, `ReferenceRowsPerSymbol = 12`, current allocation tiers, and current singular test delegates.
- Produces: `ReferenceEvidenceReader.ReadMany(IWorkspaceReadSession, IReadOnlyList<string>, ReferenceEvidenceQuery) -> IReadOnlyDictionary<string, ReferenceEvidenceBundle>` that batches the same inbound and outgoing rows, plus non-sensitive count telemetry.

**Contract inputs:** `reference_depth=1` is the CLI default, so this repairs a default path. Deduplicate IDs in first-seen order, use one snapshot and bounded ID chunks, and preserve tiers: 0 pivot/implementation, 1 content, 2 identifiers, 3 non-pivot symbols. Derive the minimum identifier cost from the existing `TokenEstimator`; only perform zero evidence reads when no tier-2 item can fit under that estimator.

**File ownership:** Miller: `ReferenceEvidenceReader.cs`, `ReferenceEvidenceReader.FamilyStore.cs`, `ContextTool.cs`, reader and context tests

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**Step 1: Write reader parity tests**

```csharp
[Fact]
public void ReadMany_MatchesSingularSnapshotsAndPreservesFirstSeenOrder()
{
    using var session = fixture.OpenReadSession();
    var actual = ReferenceEvidenceReader.ReadMany(session, new[] { fixture.LeftId, fixture.RightId, fixture.LeftId }, fixture.Query);
    Assert.Equal(new[] { fixture.LeftId, fixture.RightId }, actual.Keys);
    Assert.Equal(fixture.BundleFromSingularReadAndReadOutgoing(session, fixture.LeftId), actual[fixture.LeftId]);
}
```

Add exact, fallback, truncation, duplicate, and rotated-base cases.

**Step 2: Verify red**

Run: `dotnet test --filter "FullyQualifiedName~ReferenceEvidenceReaderTests"`

Expected: FAIL because `ReadMany` is absent.

**Step 3: Implement bounded one-snapshot batching**

```csharp
public static IReadOnlyDictionary<string, ReferenceEvidenceBundle> ReadMany(IWorkspaceReadSession session, IReadOnlyList<string> symbolIds, ReferenceEvidenceQuery query)
{
    var orderedIds = symbolIds.Distinct(StringComparer.Ordinal).ToArray();
    return session.Read(reader => ReadMany(reader, session, orderedIds, query));
}
```

Use a bounded temporary/VALUES relation per chunk and retain every per-symbol order and limit. Do not loop through singular SQL.

**Step 4: Write no-work, call-count, and output-parity tests**

```csharp
[Fact]
public void RunReferenceAware_WhenNoIdentifierCanFit_PerformsNoEvidenceRead()
{
    var calls = 0;
    var result = fixture.Run(referenceDepth: 1, tokenBudget: fixture.BaseItemsOnlyBudget, readMany: _ => { calls++; return fixture.Evidence; });
    Assert.Equal(0, calls);
    Assert.Equal(fixture.ExpectedBaseOnlyJson, result);
}
```

Add a fitting case that expects one bounded batch and exact current JSON.

**Step 5: Make enrichment budget-aware**

```csharp
var fixedItems = BuildFixedTierItems(candidates, contentChunks, request);
var fixedTierTokenCost = EstimateRenderedTokenCost(fixedItems, request.Format);
if (referenceDepth == 0 || tokenBudget - fixedTierTokenCost < MinimumIdentifierTokenCost)
    return AppendNonPivotSymbols(fixedItems, candidates);
var evidenceById = readReferenceEvidence(candidateIds);
return AppendReferenceAndNonPivotItems(fixedItems, candidates, evidenceById);
```

The fixed set contains only tiers 0 and 1, so tier-2 identifiers retain priority over tier-3 non-pivot symbols. Use the existing `TokenEstimator` for fixed and minimum costs; do not add another estimator. Preserve ordering, `candidates_examined`, cancellation, and schemas. Run tests once with `MILLER_CONTEXT_REFERENCE_BATCH=off` for legacy parity and once with it on.

**Step 6: Verify green**

Run: `dotnet test --filter "FullyQualifiedName~ReferenceEvidenceReaderTests"`

Run: `dotnet test --filter "FullyQualifiedName~ContextToolTests"`

Expected: Byte-identical fixed output; zero reads when nothing fits; bounded batches otherwise.

**Step 7: Apply commit mode**

- `parallel-lead-commit`: do not commit; hand the verified diff to the lead.

### Task 4: Characterize Impact Relationship Reads and Add Telemetry

**Files:**
- Modify: `src/Miller.Indexing/SqliteSymbolGraphIndex.cs:111-340`
- Modify: `src/Miller.Indexing/Reads/FamilyStoreReadSession.cs:209-285`
- Modify: `src/Miller.Server/Tools/ImpactTool.cs:486-575,898-1015`
- Modify: `tests/Miller.Tests/Server/ImpactToolTests.cs:376-610,1112-1145,1267-1315,1832-1875`
- Modify: `tests/Miller.Tests/Indexing/SqliteSymbolGraphIndexTests.cs`
- Modify: `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs`

**Interfaces:**
- Consumes: Existing `ISymbolGraphReachability.ReachWithEvidence`, `GraphReachResult`, `RankingCandidateLimit`, risk ranking, heuristic-test candidates, final result limit, coverage, and truncation fields.
- Produces: A recorded before-plan/row-count verdict for every family-resolution arm plus telemetry for traversal-window, reached-graph, heuristic-candidate, displacement, and selected counts. If the existing candidate-first query still scans the active base at realistic volume, Task 7B receives the exact plan and owns the schema/overlay repair.

**Contract inputs:** Current traversal is already bounded by a 500-2,000 candidate window and the measured limit-20 workload reached only 52 candidates. `FamilyStoreReadSession.ReadResolutionEdges` already builds a bounded candidate CTE, executes eight resolution arms, records `GraphStatementObservation`, and can capture `EXPLAIN QUERY PLAN`. Preserve that window and final-after-risk-ranking behavior. The remaining measured target is the base reverse plan that can scan roughly 1.7 million rows despite candidate-first SQL, not an unbounded closure or a missing CTE.

**File ownership:** Miller: `SqliteSymbolGraphIndex.cs`, `FamilyStoreReadSession.cs`, `ImpactTool.cs`, impact and SQLite graph/read-session tests

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**Step 1: Write query-plan characterization and result-parity tests**

```csharp
[Fact]
public void ReadNeighborEvidence_SmallCandidateSetRecordsEveryResolutionArmPlan()
{
    var observations = fixture.ReadReverseNeighbourEvidence(new[] { fixture.TargetId });
    Assert.Contains(observations, item => item.Phase == GraphStatementPhase.IdentifierBaseReverse);
    Assert.All(observations, item => Assert.True(item.CandidateCount <= 1));
}

[Fact]
public void Run_SqliteGraph_PreservesRankedWindowAndTruthfulCounts()
{
    var result = fixture.Run(limit: 20, depth: 3);
    Assert.Equal(fixture.ExpectedItems, result.Items);
    Assert.Equal(fixture.ExpectedCounts, result.Counts);
}
```

Extend the current `Run_AppliesLimitAfterRiskRanking`, SQLite ranked-window, displacement, heuristic, and truncation cases rather than replacing their semantics.

**Step 2: Run the affected tests and verify red**

Run: `dotnet test --filter "FullyQualifiedName~ImpactToolTests"`

Run: `dotnet test --filter "FullyQualifiedName~SqliteSymbolGraphIndexTests|FullyQualifiedName~FamilyStoreReadSessionTests"`

Expected: Existing result parity remains green. If the observation test is already green, record that the candidate-first/count seam shipped and do not rewrite it. A new tool-telemetry assertion must fail before production telemetry is added.

**Step 3: Preserve the existing bounded read shape and route the plan verdict**

```csharp
var graph = reachability.ReachWithEvidence(seeds, depth, traversalCandidateLimit, Direction.Reverse);
var statementPlans = graphTelemetry.StatementObservations;
var baseReverse = statementPlans.Single(item => item.Phase == GraphStatementPhase.IdentifierBaseReverse);
phaseVerdict.Record(familySession.LastGraphResolutionQueryPlan, baseReverse.Rows, baseReverse.CandidateCount, baseReverse.Elapsed);
```

Use the existing `GraphStatementObservation` and query-plan capture instead of adding a second SQL or graph abstraction. Never apply one shared SQL `LIMIT` across all IDs because it can starve later seeds and change ranking. If realistic-volume evidence still shows a base scan, append the exact plan/rows/timing to Task 7B's brief; do not add `ANALYZE`, planner pragmas, indexes, graph APIs, or new limits in this task.

**Step 4: Add non-sensitive count telemetry through the current tool scope**

```csharp
telemetry.SetMetadata("traversal_candidate_limit", traversalCandidateLimit);
telemetry.SetMetadata("graph_reached_count", graphReach.Nodes.Count);
telemetry.SetMetadata("heuristic_test_candidate_count", heuristicTestCandidateCount);
telemetry.SetMetadata("selected_count", selected.Count);
```

Add displacement and truncation flags without symbol IDs, paths, or query text. Rename the local `candidateLimit` to `traversalCandidateLimit` to keep it distinct from the final result limit; do not change its calculation.

**Step 5: Verify parity, plan, and timing**

Run: `dotnet test --filter "FullyQualifiedName~ImpactToolTests"`

Run: `dotnet test --filter "FullyQualifiedName~SqliteSymbolGraphIndexTests|FullyQualifiedName~FamilyStoreReadSessionTests"`

Run the isolated `tool.impact.bounded` workload three times before and after the query change.

Expected: Every caller-facing item/count/truncation assertion is unchanged and the new non-sensitive telemetry is present. Record the complete real-volume plan and development median. If the base scan remains or the median exceeds 2 s, Task 7B owns the measured producer-schema/overlay repair; Task 4 still completes when its characterization and telemetry are correct.

**Step 6: Apply commit mode**

- `parallel-lead-commit`: do not commit; hand the verified diff and query-plan verdict to the lead.

### Task 5: Measure and Repair the Expensive No-Change Phase

**Files:**
- Modify only for a failed reuse characterization: `crates/julie-extract-artifact/src/store/manifest.rs:140-230`, `crates/julie-extract-cli/src/store/from_artifact.rs:192-285`, `crates/julie-extract-cli/src/store/executor.rs:923-1010`
- Test: existing manifest/from-artifact/executor contract tests
- Modify: `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs:295-390`
- Modify: `src/Miller.Server/Hosting/IndexerService.cs:374-470,859-930,1349-1370`
- Modify: `src/Miller.Server/Hosting/IndexerSidecarConverger.cs:111-175`
- Modify: `tests/Miller.Tests/Server/StoreWorkspaceCoordinatorTests.cs`
- Modify: `tests/Miller.Tests/Server/IndexerServiceScanTests.cs`
- Modify: `tests/Miller.Tests/Server/StoreSidecarConvergerTests.cs`

**Interfaces:**
- Consumes: `ManifestPublishDisposition.Reused`, terminal origin/hash/generation checks, `StoreWorkspaceCoordinator.Submit`, synchronous `RunStartupDeltaScan`, `IndexerSidecarConverger.ConvergeStore`, and each existing sidecar ensure operation.
- Produces: Stable structured phase records for import, resolve, bind, content, search, metric snapshot, vector signaling, and total leader startup; a production change only in a phase proven both expensive and redundant.

**Contract inputs:** A dead claimed import and unbound generation can prevent reuse and cause later leader work, so characterize after Task 2. `ConvergeStore` already calls ensure functions whose matching stamps may return no work; calling it is not proof of rebuilding. A changed generation, manifest, revision, derivation identity, or sidecar stamp retains normal convergence.

**File ownership:** Julie manifest/from-artifact/executor paths only for a failed reuse test; Miller `StoreWorkspaceCoordinator.cs`, `IndexerService.cs`, `IndexerSidecarConverger.cs`, coordinator/indexer/sidecar tests

**Serialization required:** Yes

**Dependency reason:** Observation-only instrumentation may land after Task 2; a behavior repair requires Tasks 1B and 2B plus the faithful copied-store baseline.

**Step 1: Characterize byte-identical producer reuse**

```rust
#[test]
fn reusable_terminal_import_returns_reused_exact_manifest() {
    let created = manifests.publish("view-a", None, [entry.clone()], "request-create").unwrap();
    bind_exact_manifest(&connection, "view-a", created.generation);
    let reused = manifests.publish("view-a", Some(created.generation), [entry], "request-reuse").unwrap();
    assert_eq!(reused.disposition, ManifestPublishDisposition::Reused);
    assert_eq!(reused.generation, created.generation);
    assert_eq!(reused.effect_sequence, None);
}
```

Add changed hash, origin, generation, and terminal-state negatives. Run: `cargo test -p julie-extract-artifact --features test-store-resolution --test store_manifest_contract -- --test-threads=1` and the existing CLI from-artifact contract under `--features test-store-resolution-contract`. If green, make no producer edit.

Characterization result at producer commit `ebe09b4c` on 2026-08-14: manifest contract 27 passed, CLI resolution contract 24 passed, and resolution adapters 24 passed. Identical reuse and negative invalidation cases are green, so Task 5 has no producer behavior edit unless the faithful Task 1B baseline contradicts those contracts.

**Step 2: Write phase-recording tests before asserting a cause**

```csharp
[Fact]
public void ConvergeStore_RecordsEachExistingPhaseAndWhetherItDidWork()
{
    fixture.Converger.ConvergeStore(fixture.StoreRoot, fixture.Session);
    Assert.Equal(new[] { "content", "search", "metrics", "vector" }, fixture.Phases.Select(x => x.Name));
    Assert.All(fixture.Phases, phase => Assert.True(phase.ElapsedMilliseconds >= 0));
}
```

Add coordinator and indexer tests that record import, resolve, bind, and total startup. Records contain durations, outcome, store sequence, and did-work booleans, not paths or IDs.

**Step 3: Verify red and add instrumentation only**

Run: `dotnet test --filter "FullyQualifiedName~StoreWorkspaceCoordinatorTests|FullyQualifiedName~IndexerServiceScanTests|FullyQualifiedName~StoreSidecarConvergerTests"`

Expected: FAIL because phase records do not exist. Add timing around the current operations without changing admission, ordering, or full-rebuild arguments, then rerun to PASS.

```csharp
var started = Stopwatch.GetTimestamp();
var before = readCurrentStamp();
converge();
var after = readCurrentStamp();
_phaseSink.Record(new IndexerPhaseRecord(name, Stopwatch.GetElapsedTime(started), before != after));
```

Derive `didWork` from each sidecar's existing stamp/result rather than assuming a void call rebuilt anything.

**Step 4: Replay and apply the decision table**

Run: `python scripts/perf-recovery.py --workloads scripts/benchmarks/perf-recovery-workloads.json --workspace <staged-workspace> --store-copy <staged-copy> --live-store <original-live-store> --only producer.retry.identical,startup.leader.no_change,workspace.open.no_change --runs 3 --out artifacts/perf/no-change-phases.jsonl`

```text
claimed import dominates -> Task 2/reuse-report repair
claimed resolve dominates -> Task 2B resolve-claim repair
import dominates and disposition is Reused -> reconcile fix/store-import-idempotent-retry and repair Julie L3/materialization, not Miller sidecar admission
unbound generation dominates -> complete Task 2B copied-store heal before any resolver change
bind dominates with unchanged identity -> fix binding refresh in StoreWorkspaceCoordinator.Submit
content/search dominates with matching stamp -> fix that ensure implementation and its contract test
metrics dominates -> fix MetricSnapshotAggregates.RecordConverge admission
vector signaling dominates -> fix VectorConvergeService.StampTarget admission
no phase exceeds budget -> no behavior change in this task
```

Do not skip all of `ConvergeStore` as a shortcut; change only the failing phase and retain current-stamp checks.

**Step 5: Verify the selected repair or the no-change verdict**

Run: `dotnet test --filter "FullyQualifiedName~StoreWorkspaceCoordinatorTests|FullyQualifiedName~IndexerServiceScanTests|FullyQualifiedName~StoreSidecarConvergerTests"`

Run: `python scripts/perf-recovery.py --workloads scripts/benchmarks/perf-recovery-workloads.json --workspace <staged-workspace> --store-copy <staged-copy> --live-store <original-live-store> --only producer.retry.identical,startup.leader.no_change,workspace.open.no_change --runs 3 --out artifacts/perf/no-change-phases.jsonl`

Expected: Byte-identical retry at most 2 s, registered no-change open at most 5 s, the real MCP leader startup meets 2 s/5 s, phase records sum consistently to leader startup, every unused resolve or sidecar rebuild is a hard failure, and changed identities still perform required work. A telemetry-only green result is a valid task completion when the faithful baseline has no expensive redundant phase.

**Step 6: Apply commit mode**

- `serial-worker-commit`: commit instrumentation plus only the measured repair, or instrumentation/evidence alone when no redundant phase exists; record the verdict and SHA.

### Task 6: Prove the Shipped Incremental Resolver or Stop

**Files:**
- Modify: `crates/julie-extract-cli/tests/store_resolution_scope_equivalence.rs:351-520`
- Modify: `crates/julie-extract-cli/tests/store_delta_scope_contract.rs:289-535`
- Modify: `crates/julie-extract-cli/tests/resolution_report_scope.rs:108-205`
- Modify: `crates/julie-extract-cli/tests/store_resolution_performance.rs:1500-1700`
- Modify only for a proved correctness failure: `crates/julie-extract-cli/src/resolution.rs:1969-2105,3081-3155,3384-3445`
- Modify only for a proved routing failure: `crates/julie-extract-cli/src/store/resolve.rs:114-292,1200-1230`
- Create: Julie's dated incremental-resolution recovery evidence document under its existing evidence convention

**Interfaces:**
- Consumes: Already-default-on `JULIE_STORE_RESOLUTION_DELTA`, `resolve_workspace_with_crossover(tx, scope, crossover)`, `resolution_delta_enabled`, the 0.7 crossover, full-resolution oracle, and exact-gap output.
- Produces: A characterization verdict: `already-correct`, `correct-but-over-budget`, or `incorrect`, with exact digests, actual scope, timing, and the narrow production owner if a failure exists.

**Contract inputs:** Incremental store resolution shipped in 2.32.0 and is enabled by default in 2.33.2. Do not reopen or redesign it merely because the live accumulated store is slow. Keep the full resolver and `JULIE_STORE_RESOLUTION_DELTA=off` as oracle and fallback.

**File ownership:** Julie: scope/equivalence/report/performance tests and evidence; modify `resolution.rs:1969-2105,3081-3155,3384-3445` or `store/resolve.rs:114-292,1200-1230` only for a characterized failure

**Serialization required:** Yes

**Dependency reason:** Requires trustworthy claims and Task 5 no-change evidence; may exit green with no production edit.

**Step 1: Add a real one-file on/off oracle comparison**

```rust
#[test]
fn one_file_default_incremental_matches_full_escape_hatch() {
    let scoped = run_timed_resolve(&store_root, &scoped_view_id, "scoped-one-file", ReplayMode::Scoped);
    let full = run_timed_resolve(&store_root, &full_view_id, "full-one-file", ReplayMode::ForcedFull);
    assert_eq!(semantic_digest(&scoped.report), semantic_digest(&full.report));
    assert_eq!(scoped.report["resolution"]["resolution_mode"], "scoped");
}
```

Use the current performance fixture's `run_timed_resolve` and mode enum. Extend it for additions, deletions, renames, ambiguity, test/source partitions, and a real supported-language corpus.

**Step 2: Run the characterization with required features**

Run: `cargo test -p julie-extract-cli --features test-store-resolution-contract --test store_resolution_scope_equivalence -- --test-threads=1`

Run: `cargo test -p julie-extract-cli --features test-store-resolution-contract --test store_delta_scope_contract -- --test-threads=1`

Run: `cargo test -p julie-extract-cli --features test-store-resolution-contract --test store_resolution_performance one_file_default_incremental_matches_full_escape_hatch -- --exact --nocapture --test-threads=1`

Expected: If digests, scope, and 5 s development timing pass, record `already-correct` and make no production edit. A failure must name whether routing, scope construction, resolution, or accumulated-overlay maintenance owns it.

**Step 3: Fix only a proved failure**

```rust
let delta_enabled = resolution_delta_enabled()?;
let resolution = resolve_workspace_with_crossover(&transaction, delta_scope, DELTA_SCOPE_CROSSOVER)?;
let telemetry = ResolutionExecutionTelemetry::from_durable_payload(&resolution.durable_payload)?;
```

Use the current function signatures discovered in the execution checkout. A routing failure is confined to `store/resolve.rs:114-292,1200-1230`; a digest/scope failure is confined to `resolution.rs`. Overlay-history cost belongs to Task 7A and post-rotation read amplification belongs to Task 7B, not this task.

**Step 4: Prove reports, parity, and language coverage**

Run: `cargo test -p julie-extract-cli --features test-store-resolution-contract --test resolution_report_scope -- --test-threads=1`

Run the language-parity query on the real resolution input table and record exact digests for delta-on and delta-off runs.

Expected: Every language is present, scopes are truthful, and exact/exact-gap digests match.

**Step 5: Run the producer timing gate**

Run: `cargo xtask performance store-resolution --runs 3 --out-dir target/performance/store-incremental-resolution-recovery`

Expected: One-file median at most 5 s and full real-Miller median at most 60 s on development Linux. If one-file passes but accumulated-store full resolution fails because retained history dominates, Task 7A owns it; if post-rotation read plans dominate, Task 7B owns it.

**Step 6: Apply commit mode**

- `serial-worker-commit`: commit tests/evidence and any narrowly proved fix after verification; record the verdict and SHA.

### Task 7A: Rebase, Collect, and Rotate

**Files:**
- Modify: `crates/julie-extract-artifact/src/store/maintenance.rs:291-420`
- Modify: `crates/julie-extract-cli/src/store/resolve.rs:751-860,971-1045`
- Modify: `store_maintenance_contract.rs`, `store_maintenance_property.rs`
- Reconcile from `feature/store-incremental-resolution-consumer`: `FamilyStoreReadSession.cs`, `FamilyStoreReadSessionTests.cs`, `StoreWorkspaceIndexProviderScaleTests.cs`
- Modify: `StoreResolutionReaderTests.cs:18-155`

**Interfaces:**
- Consumes: Rebase planner, strict `>25%` replacement/tombstone and `>64 MiB` gap thresholds, active pins, lease heartbeat, generation manifests, and `ReadResolutionEdges`.
- Produces: Atomic rebase, pin-aware collection, bounded retained history, rotated-base reopen, and an independently revertible lifecycle commit.

**Contract inputs:** Publish new base/generation before collection. Never delete data reachable from a live pin. Current Miller `main` already contains `StoreSequenceAdvanceReopensTheRotatedResolutionBasePath` and later cursor coverage. Three-way-diff commits `9bf6bc26` and `382f654c` against current `main`, transplant only missing assertions such as rebase sequence/base rotation and partial-legacy rebind, and never replace newer tests with the old branch versions.

**File ownership:** Julie maintenance/rebase and tests; Miller family read-session and base-rotation tests

**Serialization required:** Yes

**Dependency reason:** Requires Tasks 3 and 6 and a three-way reconciliation of the existing consumer branch against current `main`.

**Step 1: Replay existing consumer tests**

```csharp
[Fact]
public void StoreSequenceAdvanceReopensTheRotatedResolutionBasePath()
{
    using var session = fixture.OpenSession();
    fixture.RotateBaseAndAdvanceStoreSequence();
    Assert.Equal(fixture.NewBaseEdges, session.ReadResolutionEdges(fixture.Query));
}
```

Run all three named branch tests, inventory the same names on current `main`, and record a three-way diff before reconciliation.

**Step 2: Write producer threshold, crash, and pin tests**

```rust
#[test]
fn rebase_publishes_new_base_before_collecting_unpinned_history() {
    let pinned = fixture.pin_current_view();
    let rebased = fixture.resolve_past_gap_threshold();
    assert!(fixture.base_exists(pinned.base_id()));
    drop(pinned);
    fixture.collect();
    assert!(!fixture.base_exists(fixture.old_base_id()));
    assert!(rebased.current_view_is_exact());
}
```

Cover exact and over-threshold boundaries, heartbeat loss, crash before/after publish, and Windows held handles.

**Step 3: Verify red**

Run: `cargo test -p julie-extract-artifact --features test-store-resolution --test store_maintenance_contract -- --test-threads=1`

Run: `cargo test -p julie-extract-artifact --features test-store-resolution --test store_maintenance_property -- --test-threads=1`

Expected: New retained-history or crash-ordering coverage fails until lifecycle completion.

**Step 4: Implement publish-before-collect**

```rust
let candidate = prepare_rebased_base(store, view, lease)?;
lease.ensure_current()?;
let published = publish_rebased_generation(store, candidate, lease)?;
store.advance_sequence(published.sequence)?;
collect_unreachable_resolution_history(store, active_pins(store)?, published)?;
```

Check the lease at every durable boundary. Preserve recoverable old/prepared artifacts on Windows retry exhaustion.

**Step 5: Verify green**

Run: `cargo test -p julie-extract-artifact --features test-store-resolution --test store_maintenance_contract -- --test-threads=1`

Run: `cargo test -p julie-extract-artifact --features test-store-resolution --test store_maintenance_property -- --test-threads=1`

Run: `dotnet test --filter "FullyQualifiedName~FamilyStoreReadSessionTests|FullyQualifiedName~StoreResolutionReaderTests"`

Expected: Pins survive, obsolete history collects, readers reopen, partial legacy state heals, and the copied store reaches explicit retained-history ceilings. On the quiet copied store, active overlay generations are at most one, unreachable unpinned delta rows are zero, retained overlay rows fall by at least 80%, and logical resolution-table/index bytes fall by at least 25% from Task 1B's baseline; no reachable row may be lost. If the current on-disk lifecycle cannot meet a numeric ceiling without a separately proved compaction step, the task remains open rather than converting the ceiling to report-only.

After the serial worker commit is integrated, the lead runs `dotnet test --filter "FullyQualifiedName~StoreWorkspaceIndexProviderScaleTests"` as the affected Scale gate.

Before this task starts, preserve a second verified pre-maintenance snapshot. Collection is never tested first on the only baseline copy.

**Step 6: Apply commit mode**

- `serial-worker-commit`: commit reconciled files and record original and resulting SHAs.

### Task 7B: Optimize Measured Bounded Reads

**Files:**
- Modify only after plan evidence: `src/Miller.Indexing/FamilyStoreReadSession.cs`
- Modify only after plan evidence: `src/Miller.Indexing/ReferenceEvidenceReader.FamilyStore.cs:224-370`
- Modify only after plan evidence: `src/Miller.Indexing/SqliteSymbolGraphIndex.cs`
- Modify: `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs`
- Modify: the existing graph query-plan tests covering `LastGraphResolutionQueryPlan`
- Modify: `tests/Miller.Tests/Indexing/StoreResolutionReaderTests.cs:18-155`
- Modify only if a producer index or statistics lifecycle wins measurement: the current resolution-base schema/maintenance owner and its focused producer contracts

**Interfaces:**
- Consumes: Task 7A's rotated copied store, `ReadResolutionEdges`, all forward/reverse exact/fallback arms, active-base schema, delta overlays, and SQLite `EXPLAIN QUERY PLAN`.
- Produces: One independently revertible query-shape, producer-index, or measured statistics-maintenance repair with before/after plans and actual-row/timing evidence.

**Contract inputs:** Capture plans after Task 7A because rotation can remove the apparent win. The historical reverse-base scan of about 1.7 million rows is evidence to reproduce, not proof that a Miller SQL rewrite is the owner. `ANALYZE` and an index are allowed only as measured candidates after the eight-arm baseline exists; blind planner tuning remains prohibited.

**File ownership:** Julie resolution-base schema/statistics only if proved; Miller `FamilyStoreReadSession`, family evidence reader, graph/resolution query-plan tests

**Serialization required:** Yes

**Dependency reason:** Runs only after 7A so lifecycle compaction and read-path tuning have separate evidence and rollback commits.

**Step 1: Capture the eight-arm post-rotation baseline**

Record forward/reverse × exact/fallback × small-ID/100-ID plans, actual candidate and returned rows, statement counts, cache state, and three-run medians. Include one reverse-base workload whose candidate count reproduces the scan risk; the prior depth-1/limit-20 impact row is telemetry, not sufficient query-plan proof.

**Step 2: Test candidates one at a time**

Evaluate only the owner supported by the baseline: a Miller query-shape change, a producer target-side index that matches `target_version_id` plus `target_symbol_id`, or `ANALYZE`/statistics maintenance on the copied artifact. For each candidate, require an exact before/after plan, unchanged rows/order/provenance, and a lower quiet three-run median. Do not combine candidates in one measurement.

```csharp
var plan = fixture.ExplainFamilyReverseQuery(query);
Assert.DoesNotContain("SCAN resolution_base.identifier_resolutions", plan, StringComparison.Ordinal);
Assert.Contains("SEARCH", plan, StringComparison.Ordinal);
```

**Step 3: Verify green**

Run: `dotnet test --filter "FullyQualifiedName~FamilyStoreReadSessionTests|FullyQualifiedName~ReferenceEvidenceReaderTests|FullyQualifiedName~StoreResolutionReaderTests|FullyQualifiedName~SqliteSymbolGraphIndexTests"`

Run the focused producer schema/maintenance contract only when that owner changes.

Expected: all eight arms preserve exact results, bounded reads avoid the reproduced full-base scan, and the copied-store median improves. If no candidate meets all three conditions, make no production query/schema/statistics change and retain the evidence as the task result.

**Step 4: Apply commit mode**

- `serial-worker-commit`: checkpoint and commit the single measured owner change or evidence-only verdict separately from Task 7A.

### Task 8: Close Linux and Windows Recovery Gates

**Files:**
- Modify: `PERF.md`
- Modify: `docs/README.md`
- Create: `docs/findings/2026-08-13-performance-recovery-verification.md`
- Modify only if parity exposes a defect: `scripts/perf-recovery.py`, `scripts/semantic-broker-soak.sh`, `scripts/semantic-broker-soak.ps1`
- Modify only after explicit approval: `scripts/julie-pins.json` and version-aligned release inputs

**Interfaces:**
- Consumes: Prior commits, fixed workloads, source restoration, Linux/Windows wrappers, and semantic soak.
- Produces: Dated evidence with every hard budget, parity result, resource ceiling, query count/plan, platform result, and remaining blocker.

**Contract inputs:** Quiet machines, exact producer SHA, three runs. Pinning, pushing, tagging, publishing, and releasing remain approval-gated.

**File ownership:** Miller `PERF.md`, `docs/README.md`, `docs/findings/2026-08-13-performance-recovery-verification.md`, parity scripts only if defective; approval-gated pin inputs

**Serialization required:** Yes

**Dependency reason:** Integrates all prior slices.

**Step 1: Write strict report tests**

```python
def test_verification_report_rejects_missing_hard_gate(self):
    records = load_records(self.fixture_path)
    with self.assertRaisesRegex(ValueError, "missing hard gate: tool.context.references.depth1"):
        build_verification_report(records[:-1])

def test_windows_record_rejects_missing_private_usage(self):
    record = windows_record(private_usage_bytes=None, hard_memory_bytes=600 * 1024 * 1024)
    with self.assertRaisesRegex(ValueError, "missing Windows PrivateUsage"):
        require_hard_gates([record], "windows")
```

Reject missing IDs, fewer than three runs, parity failure, missing platform identity, and over-budget median.

**Step 2: Verify red, implement validation, verify green**

Run: `python scripts/tests/test_perf_recovery.py`

```python
def require_hard_gates(records: Sequence[ReplayRecord], platform: str) -> None:
    for workload_id in REQUIRED_WORKLOAD_IDS:
        measured = [record for record in records if record.workload_id == workload_id and record.platform == platform]
        if len(measured) < 3:
            raise ValueError(f"missing hard gate: {workload_id}")
        if median(record.wall_ms for record in measured) > budget_for(workload_id, platform):
            raise ValueError(f"budget exceeded: {workload_id}")
```

Expected: PASS only when every gate is present and valid.

**Step 3: Run Linux branch gates**

Run Julie format, clippy, workspace, contract, and performance gates.

Run the serialized `store_coordinator_contract`, `store_resolution_contract`, and maintenance contracts with their required Cargo features; do not assume the workspace default enables them.

With `MILLER_JULIE_SOURCE` set to the exact tested Julie worktree, run: `scripts/restore-julie-extract.sh --from-source`.

Run: `dotnet build Miller.slnx -c Release`

Run: `scripts/test.sh all`

Run: `scripts/semantic-broker-soak.sh`

Run the full replay three times. Expected: tests pass, 0 warnings/errors, resolver and same-depth batch exact parity, context cross-depth semantic invariants, and budgets pass.

**Step 4: Run Windows branch gates**

With `$env:MILLER_JULIE_SOURCE` set to the exact tested Julie worktree, run: `scripts/restore-julie-extract.ps1 -FromSource`.

Run: `dotnet build Miller.slnx -c Release`

Run: `scripts/test.ps1 all`

Run the Windows `store_coordinator_contract` and `store_resolution_contract` targets serially with their required Cargo features. The current Windows allowlist and `test.ps1 all` do not substitute for these producer contracts.

Run focused Windows contracts for `JulieExtractRunnerTests`/`WindowsKillOnCloseJob` descendant-tree containment, `PathCanonicalizerTests` drive/UNC/verbatim long paths, held-handle promotion/rotation retry, coordinator `tasklist` liveness, replay MCP stdio framing, and semantic broker identity. A missing platform prerequisite blocks the named gate; it is not recorded as passing.

Run `scripts/semantic-broker-soak.ps1` only on a dedicated Windows runner with no other Miller broker/session. The machine-global pipe is not isolated by `MILLER_HOME`; on a shared host, record this gate as blocked rather than attributing another session's broker to the test.

Create the Windows scratch snapshot with Task 1B's cross-platform helper, record filesystem/antivirus state, and do not require or modify a Defender exclusion. Run the full replay three times and assert `PROCESS_MEMORY_COUNTERS_EX.PrivateUsage` at or below its independent 350 MB idle/600 MB peak gate. Expected: tests pass, Windows locking behavior is correct, resolver digest parity and context semantic invariants hold, memory passes, and constrained timing budgets pass.

**Step 5: Reconcile ledger and docs**

```markdown
| Workload | Linux median | Linux gate | Windows median | Windows gate | Parity |
|---|---:|---|---:|---|---|
| `tool.context.references.depth1.lexical` | measured | <= 2,000 ms | measured | <= 5,000 ms | stable pivots/order/truncation |
| `tool.context.references.depth1.batch_on` | measured | <= 2,000 ms | measured | <= 5,000 ms | byte-identical to batch-off at the same depth |
```

Close only passing PERF rows; failures remain open with metric, owner, and diagnostic. Link the evidence from `PERF.md` and `docs/README.md`.

**Step 6: Apply commit mode and stop at approvals**

- `serial-worker-commit`: commit evidence/docs after all gates and record the SHA.
- Do not pin, push, tag, publish, deploy, or release without explicit approval for the verified clean state.

## External Review Reconciliation

Grok 4.6 reviewed the draft read-only on 2026-08-13 and reviewed the in-progress plan plus Task 1-4 reports again on 2026-08-14. No external-model policy was declared in `AGENTS.md` or `CLAUDE.md`, so the required policy note is: `no external-model policy declared — plan sent to xAI`.

- Accepted: incremental resolution is shipped and must be characterized before any production edit; Task 6 is now a prove-or-stop gate.
- Accepted: leader delay does not prove sidecar rebuilding; Task 5 records all coupled phases before changing one.
- Accepted: impact already has a bounded 500-2,000 traversal window; Task 4 preserves it and targets the measured SQLite read shape.
- Accepted: current Miller `main` is authoritative over the old consumer commits; Task 7A transplants only missing coverage after a three-way diff.
- Accepted: Python/Cargo commands, Windows private-memory evidence, the machine-global broker constraint, worktree ownership, and overlapping `resolve.rs` ranges were corrected.
- Accepted from the second review: Task 1's path safety is useful but `workspace status/leader` do not measure Generic Host startup; resolve workloads need producer invocations and observation timeouts above the baseline; the family copy needs a stable whole-store procedure; context needs lexical controls and same-depth batch parity; Task 7 must split lifecycle surgery from query tuning; Windows must run the feature-gated producer contracts explicitly.
- Accepted from the second review: Tasks 3 and 4 are implemented but remain production-volume unproven until Task 1B replay. The context batch switch returns to default-off until copied-store lexical parity and timing pass.
- Corrected after code-level audit: the observed August 14 stranded request was an import, and current Julie contracts already cover dead/stale resolve takeover plus the no-writer-lease invariant. Task 2B inventories that proof, adds only a demonstrated coverage gap, and validates copied-field healing and Windows liveness.
- Rejected: `julie-extract store resolve` has no explicit one-file CLI flag. The faithful workload creates a one-file pending generation, then lets the production resolver derive its scope.
- Rejected: automatically adding a Windows Defender exclusion. The snapshot/replay records antivirus state and uses ordinary local copying; it does not weaken host security or require an administrative mutation.
- Rejected: adding an impact reachability overload or reducing the 500-candidate floor now. No evidence requires a new graph interface or semantic change, and query-plan parity is the narrower fix.

## Completion Criteria

- The stranded-import state recovers automatically or is proven fixed in the adopted producer; a fenced old owner cannot publish.
- Exit 135 has diagnostics and remains unclassified without proof.
- Byte-identical retries and no-change leader startup do no unnecessary resolve or full sidecar rebuild.
- Incremental/full resolution exact digests match across supported languages and timing budgets pass.
- Rebase thresholds, pins, publish ordering, collection, base rotation, and partial legacy recovery are tested.
- Obsolete resolution history is bounded and bounded reverse reads avoid complete active-base scans.
- Context batch-off/on output is byte-identical at the same depth; cross-depth pivots/order/truncation semantics are stable; no-fit requests do zero evidence reads; fitting requests batch and pass 2 s/5 s.
- Impact ranks before selection, bounds graph/SQL work, and reports truncation/displacement truthfully.
- Linux and Windows build, test, Scale, semantic, resource, timing, and parity gates pass on the same intentional state.
- `PERF.md`, the dated evidence, and `docs/README.md` contain measured results.

## Out of Scope

- New MCP tools, fleet semantics, guidance/confidence views, embeddings-as-a-service, or extraction ownership changes.
- Replacing SQLite, rewriting the resolver wholesale, changing Store Contract version, or removing relationship features.
- Publishing, pinning, or releasing without separate explicit approval and release-state verification.
