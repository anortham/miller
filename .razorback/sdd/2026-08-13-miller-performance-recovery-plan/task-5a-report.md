# Task 5A report: startup convergence phase records

## Repository state

- Miller worktree: `/home/murphy/source/miller/.worktrees/performance-recovery`
- Miller branch: `feature/performance-recovery`
- Miller base: `9ffd76bda532117a35fd4af21d2c38321b72efc1`
- Miller implementation/checkpoint commit: `7216d5cfdfda1de99cb63eda3e08df2e72bbcde5` (`perf: record startup convergence phases`)
- Julie characterization worktree: `/home/murphy/source/julie-extractors/.worktrees/miller-performance-recovery-producer`
- Julie branch/base: `feature/miller-performance-recovery-producer` at `ebe09b4cc30e8e515ebf2f5c4a47abedfc46dfd5`
- Julie was clean and unchanged; producer characterization was already green, so no Julie production or test change was needed.
- Miller was clean at the implementation commit. This report is the only post-commit report artifact to be added.

## RED/GREEN evidence

Focused command used throughout:

```text
dotnet test --filter "FullyQualifiedName~StoreWorkspaceCoordinatorTests|FullyQualifiedName~IndexerServiceScanTests|FullyQualifiedName~StoreSidecarConvergerTests"
```

- RED: the first run after adding the focused tests reached the test-project build and failed only because the new internal `IIndexerPhaseSink`/`IndexerPhaseRecord` seam did not yet exist.
- GREEN: after the sink, record, scope, production wiring, and test seam were implemented, the focused filter passed `92` tests, `0` failed, `0` skipped.
- GREEN after diff reduction: the same focused filter passed `92` tests, `0` failed, `0` skipped.
- `git diff --check` passed before checkpoint and commit.

## Phase-record seam and verdict

`IndexerPhaseRecord` is internal and contains phase, elapsed duration, outcome, nullable store sequence, and `didWork`. `LoggingIndexerPhaseSink` emits those fields through the existing structured logger; `NullIndexerPhaseSink` preserves existing callers that do not inject a sink. `IndexerPhaseScope` records failure by default and safely contains sink failures so instrumentation cannot alter indexing behavior.

Records cover:

- coordinator: `import`, `resolve`, `bind`, `coordinator_total`;
- sidecars: `content`, `search`, `metrics`, `vector`, `sidecar_total`;
- leader startup: `startup_total` around `RunStartupDeltaScan`.

`didWork` is derived from existing facts: import manifest disposition and before-state, successful terminal resolve result, pointer write versus valid pointer reuse, boolean sidecar ensure results, metric-history write result, vector target revision stamp, and `ExtractReport.CreatedRevision`. No paths, workspace/family/view IDs, source, queries, or payloads are logged.

The implementation is instrumentation-only. Existing admission, phase order, arguments, retry/backoff, locking, rebuild choice, `ConvergeStore` call, and exception propagation remain unchanged. No product or public API surface was added.

## Exact files changed

- `src/Miller.Server/Hosting/IndexerPhaseRecord.cs` (new internal record/sink/scope)
- `src/Miller.Server/Hosting/IndexerService.cs`
- `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`
- `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs`
- `tests/Miller.Tests/Server/IndexerServiceScanTests.cs`
- `tests/Miller.Tests/Server/StoreSidecarConvergerTests.cs`
- `tests/Miller.Tests/Server/StoreWorkspaceCoordinatorTests.cs`
- `.memories/2026-08-14/055648_b32e.md` (Goldfish checkpoint)
- `.razorback/sdd/2026-08-13-miller-performance-recovery-plan/task-5a-report.md` (this report)

## Replay and remaining risk

No replay workload was run or changed in this packet. The existing safe replay path is a direct one-shot CLI subprocess path, not the Generic Host/leader startup path; Task 5A owns observation-only records and explicitly excludes replay scripts/workloads. The focused tests exercise the injected production seams without mutating a live workspace or family store.

Bootstrap and cross-workspace coordinator call sites outside the Task 5A owned files retain the default null sink. The leader `IndexerService` production path carries the structured logging sink; extending logging into those other owners would require a separate bounded change outside this packet.
