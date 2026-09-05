# WAL recurrence hardening

Approved scope: the user approved closing the three gaps in the post-restart
finding and previously authorized investigation and repairs without repeated
permission stops. This document records the implementation choices within that scope.

## Architecture quality

Keep cleanup in `StoreWalCheckpoint` and scheduling in existing callers. Add one
read-only observation and one maintenance report, not another service or timer.
`StoreWorkspaceCoordinator` and `IndexerService` publish that report through the
existing `IIndexerPhaseSink`. Keep SQLite I/O in Indexing, not Core. No schemas,
new MCP tools, reader termination, producer pin changes, or live WAL deletion.
Risk is low for the additive report and existing-call-site repairs; no ownership
boundary changes. Producer lifecycle qualification is a separate bounded audit.

Prefer discovery of nonempty WAL files plus persisted debt to marker-only retries.
Reject a new fleet sweeper and a fake hard cap via `journal_size_limit`. No active
process means no autonomous timer; next refresh or producer command must recover.

## Acceptance criteria and single-agent implementation sequence

1. `src/Miller.Indexing/Store/StoreWalCheckpoint.cs` and its tests.
   - [x] Preserve the original debt timestamp across repeated marks.
   - [x] Observe current-generation and coordinator WAL bytes without creating files.
   - [x] Discover nonempty markerless WALs, attempt cleanup, preserve debt on Busy/Skipped.
   - [x] Report bytes before/after, debt age, outcome and elapsed time.
   - [x] Warning threshold is 256 MiB remaining in either WAL or debt at least five
     minutes old; unavailable measurements must not masquerade as zero.
   - [x] Exercise multiple committed batches under a held reader, then recovery using
     a new invocation with no in-memory state. Assert data intact and WAL truncated.
2. `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs`,
   `src/Miller.Server/Hosting/IndexerService.cs`,
   `src/Miller.Server/Hosting/IndexerPhaseRecord.cs` and focused server tests.
   - [x] Both no-change refresh and idle maintenance discover markerless WAL debt.
   - [x] Publish real checkpoint status instead of discarding it; warn with byte/age
     evidence for overdue or oversized remaining debt. Keep logging fail-open.
   - [x] Retain the existing 30-second resident retry throttle, including successful
     empty checks, so idle ticks do not add per-tick filesystem work.
   - [x] Cross-workspace coordinator recreation/no-resident path proves recovery.
3. Qualify standalone producer lifecycle. Record precise paths and focused tests;
   repair a demonstrated producer omission in its owning repo if required. Do not
   silently merge or overwrite the outstanding J1 branch.

Steps 1 and 2 are tightly sequential because reporting and scheduling consume the
same new checkpoint report. A read-only producer audit runs independently.

## Verification

TDD at the smallest named test group. Baseline production code already passed the
gates recorded in the previous finding; do not rerun unchanged full suites.
Use `dotnet test --filter FullyQualifiedName~StoreWalCheckpointTests` first,
then `StoreWorkspaceCoordinatorTests` and `IndexerPhaseRecordTests`.
At the branch gate run Release build, fast suite and `scripts/test.sh scale` once.
Run affected tests on the Windows NTFS guest because this changes file lifecycles.
Record results and remaining limitations. No release or push is authorized.
Security scope: none declared. No external reviewer selected.

## Execution refinements

Health visibility uses an optional WAL observation in StoreWorkspaceFacts, populated
by WorkspaceFactsAssembler and DashboardIndexFactsReader. WorkspaceHealthFacts adds
a typed warning without I/O in the renderer or health aggregation. Existing MCP,
CLI registered-workspace health and dashboard warning rendering consume it.

The exclusive-coordinator-lock regression demonstrated that the previous 300-second
SQLite timeout can wait behind a lock. Family checkpoints now wait at most one
second per database for locks. This does not bound checkpoint disk I/O.

The resident test lives in IndexerServiceWalTests, in the existing serialized store
environment collection, because the fast suite defaults MILLER_INDEX_STORE=off.
It uses the existing service clock to test the retry interval without sleeping.
