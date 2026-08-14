# Task 5A report: startup convergence phase records and correction

## Repository state

- Miller worktree: `/home/murphy/source/miller/.worktrees/performance-recovery`
- Miller branch: `feature/performance-recovery`
- Miller base: `9ffd76bda532117a35fd4af21d2c38321b72efc1`
- Task 5A implementation/checkpoint commit: `7216d5cfdfda1de99cb63eda3e08df2e72bbcde5` (`perf: record startup convergence phases`)
- Task 5A first report commit: `1700d6e468ecbb34128c091268549330a25e51ec`
- Task 5A correction commit: `991492f3` (`perf: correct task 5a phase instrumentation`)
- Task 5A final truth correction commit: `94430afd` (`perf: correct task 5a work truth`)
- Final report commit: this report is committed separately after the correction commit.
- Julie characterization worktree: `/home/murphy/source/julie-extractors/.worktrees/miller-performance-recovery-producer`
- Julie branch/base: `feature/miller-performance-recovery-producer` at `ebe09b4cc30e8e515ebf2f5c4a47abedfc46dfd5`
- Julie was clean and unchanged; producer characterization was already green, so no Julie production or test change was needed.
- Miller was clean at `94430afd` before this report edit; the report commit is the only remaining report change.
- Correction checkpoints: `.memories/2026-08-14/061628_75bd.md` (`checkpoint_75bd107b`) and `.memories/2026-08-14/062201_021c.md` (`checkpoint_021ccb87`).

## RED/GREEN evidence

Focused commands used for the corrections:

```text
dotnet test --filter "FullyQualifiedName~StoreWorkspaceCoordinatorTests|FullyQualifiedName~StoreSidecarConvergerTests|FullyQualifiedName~IndexerServiceScanTests|FullyQualifiedName~IndexBootstrapServiceTests|FullyQualifiedName~CrossWorkspaceRefreshServiceTests"
dotnet test --filter "FullyQualifiedName~StoreWorkspaceCoordinatorTests|FullyQualifiedName~IndexerSidecarConvergerTests"
```

- RED (initial Task 5A): the first run reached the test-project build and failed because the new internal `IIndexerPhaseSink`/`IndexerPhaseRecord` seam did not yet exist.
- GREEN (initial Task 5A): after the sink, record, scope, production wiring, and test seam were implemented, the three-core-class filter passed `92` tests, `0` failed, `0` skipped; the reduced diff rerun was also `92/92`.
- RED (correction): the first correction run passed `219` tests and failed `1` stale assertion that still expected content/search to carry a store sequence before the target read.
- GREEN (final correction): after changing that assertion to the intended nullable early sequence and reducing the coordinator patch, the five-class filter passed `220` tests, `0` failed, `0` skipped.
- RED (final truth correction): the first test edit triggered xUnit analyzer errors for blocking wake waits and cancellation; after making the wake test async, the behavioral run passed `36` tests and failed `2` assertions (full-to-L1 import work and same-revision full-rebuild vector work).
- GREEN (final truth correction): the exact two-class filter passed `38` tests, `0` failed, `0` skipped after the production predicates were corrected.
- `git diff --check` passed before each correction checkpoint and all implementation/report commits.

## Phase-record seam and verdict

`IndexerPhaseRecord` is internal and contains phase, elapsed duration, outcome, nullable store sequence, and `didWork`. `LoggingIndexerPhaseSink` emits those fields through the existing structured logger; `NullIndexerPhaseSink` preserves existing callers that do not inject a sink. `IndexerPhaseScope` records failure by default and safely contains sink failures so instrumentation cannot alter indexing behavior.

Records cover:

- coordinator: `import`, `resolve`, `bind`, `coordinator_total`;
- sidecars: `content`, `search`, `metrics`, `vector`, `sidecar_total`;
- leader startup: `startup_total` around `RunStartupDeltaScan`.

`didWork` is derived from existing facts: import manifest disposition and before-state, successful terminal resolve result, pointer write versus valid pointer reuse, boolean sidecar ensure results, metric-history write result, vector target revision stamp, and `ExtractReport.CreatedRevision`. A reused import reports work for a requested full level only when the existing level is not already full; a full-to-L1 reuse is correctly no work. Legacy vector work is true only when semantic convergence is enabled and the target advances or `fullRebuild` is requested, so a same-revision full rebuild still records and wakes vector work. Store convergence continues to pass `fullRebuild: false`. No paths, workspace/family/view IDs, source, queries, or payloads are logged.

The implementation is instrumentation-only. Existing admission, arguments, retry/backoff, locking, rebuild choice, `ConvergeStore` call, and exception propagation remain unchanged. The correction restores the pre-instrumentation sidecar order: content/search run before the store sequence is required, and metrics/vector still read the sequence at the original point. Early content/search records can therefore have a null sequence. Sink failures are contained, while coordinator request failures and startup failures still propagate or remain contained as before and now record failed outcomes. `startup_total` remains scoped to `RunStartupDeltaScan`; MCP process wall time is intentionally out of scope here. No new MCP telemetry schema or tool surface was added.

The bootstrap coordinator now receives the existing logger-backed phase sink. The cross-workspace service creates the same structured sink from its existing service logger (resolved by the host) and passes it to both the coordinator and its already-instrumented sidecar converger; no duplicate sidecar sink was added. Direct test/internal construction continues to use the null-safe default unless a sink is injected.

## Exact files changed

- `src/Miller.Server/Hosting/IndexerPhaseRecord.cs` (new internal record/sink/scope)
- `src/Miller.Server/Hosting/IndexerService.cs`
- `src/Miller.Server/Hosting/IndexBootstrapService.cs`
- `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`
- `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`
- `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs`
- `tests/Miller.Tests/Server/IndexerServiceScanTests.cs`
- `tests/Miller.Tests/Server/IndexerSidecarConvergerTests.cs`
- `tests/Miller.Tests/Server/StoreSidecarConvergerTests.cs`
- `tests/Miller.Tests/Server/StoreWorkspaceCoordinatorTests.cs`
- `.memories/2026-08-14/055648_b32e.md` (initial Goldfish checkpoint)
- `.memories/2026-08-14/061628_75bd.md` (correction Goldfish checkpoint)
- `.memories/2026-08-14/062201_021c.md` (final truth correction checkpoint)
- `.razorback/sdd/2026-08-13-miller-performance-recovery-plan/task-5a-report.md` (this report)

## Replay and remaining risk

No replay workload was run or changed in this packet. The existing safe replay path is a direct one-shot CLI subprocess path, not the Generic Host/leader startup path; Task 5A owns observation-only records and explicitly excludes replay scripts/workloads. The focused tests exercise the injected production seams without mutating a live workspace or family store.

Remaining risk is limited to unrun replay/scale workload evidence and the direct CLI's null logger fallback when no host logger is supplied; MCP bootstrap and host-resolved workspace-open paths use the structured logger-backed sink. Julie producer characterization stayed green and required no production or test change.
