# CT Performance Hardening Design

## Goal

Repair the measured xUnit whole-suite result-transport regression, then remove the daemon's 250 ms
all-state materialization while preserving CT verdict, freshness, worktree, scheduling, and artifact
semantics.

## Evidence

- A quiet Miller.Tests CT run took 75.78 seconds, peaked at 585,236 KB resident memory, and failed
  after completing the test process because JSON stdout exceeded the 8M-character capture cap.
- A diagnostic partial stream reached 48,502,555 bytes and 75,369 lines. The same run's JUnit
  artifact was about 2 MB and contained 8,310 test cases.
- The idle daemon calls `Evaluate` every 250 ms. Each call materializes all 8,368 status rows and all
  matching fresh-watermark rows before projecting three aggregate facts: verdict, stale count, and
  selected count.
- The release A/B found no collateral non-CT regression. Inspect, context, and impact are materially
  faster than v1.20.1; lexical search is unchanged within noise.

Full evidence is in `docs/findings/2026-08-23-performance-audit.md`.

## Slice 1: xUnit whole-suite artifact results

Only an xUnit whole-suite invocation changes transport:

1. `DotnetTestProvider` launches it with xUnit v3's `verbose` reporter and `-noAutoReporters`.
   Verbose progress keeps the existing ten-minute output-silence stall guard and daemon child-liveness
   signal honest without emitting the JSON reporter's full structured lifecycle payload. The bounded
   capture may truncate verbose text as the suite grows, but artifact-only runs never parse that text.
2. The existing JUnit result artifact remains enabled and remains the durable evidence artifact.
3. The provider validates that the artifact exists, parses safely, contains reported cases when cases
   were requested, and has a verdict consistent with the process exit. A normal red test exit returns
   a failed artifact-only result; missing/malformed/empty artifacts and non-test runner exits remain
   provider failures.
4. The provider returns the existing artifact-only `ProviderRunResult`: no in-memory case-result list,
   the validated verdict, and `ResultArtifactPath` set.
5. `ContinuousTestCoordinator.TryImportProviderResultArtifact` performs the existing selector
   reconciliation and persistence, including theory-row handling. Provider-triggered imports use a
   strict mode: every reported artifact row must resolve to an already-selected inventory id before
   any store write. An unresolved row fails validation instead of creating a new `source=artifact`
   case. Selected ids absent from the artifact stay stale and their requested/reported residue is
   written through the existing coordinator lifecycle diagnostic.

Selected and chunked xUnit runs keep the JSON reporter. Their existing 120-unit/6-KiB argv caps also
bound JSON volume, and their immediate per-case results remain useful for partial selection.

Per-test coverage keeps the current JSON result path because coverage compaction consumes immediate
case results and display names. Artifact-only whole-suite transport is enabled only when coverage mode
is `None`; a future coverage-wide transport change needs its own measurement and design.

The shipped xUnit v3.2.2 help and official reporter documentation confirm `verbose` emits per-test
progress, `-noAutoReporters` prevents CI auto-detection from replacing the chosen reporter, and JUnit is a
supported result format: <https://xunit.net/docs/getting-started/v3/custom-runner-reporter>.
The provider already requires xUnit v3-only flags such as `-preEnumerateTheories` and `-trait-`; a runner
that rejects the verified reporter flags remains an actionable provider compatibility failure. It does
not fall back to full JSON and reintroduce the size regression.

### Rejected alternatives

- Raising the capture cap: needs more than 48 MB today, increases peak memory, and fails again as the
  suite grows.
- Redirecting JSON to disk: avoids the cap but preserves tens of megabytes of serialization, I/O, and
  parsing that the JUnit artifact already replaces.
- Changing every xUnit run to artifact-only: unnecessary for bounded selected runs and broadens the
  first repair.
- Disabling the output-silence guard for whole suites: removes the signal that distinguishes a slow
  suite from a wedged child. Verbose artifact-only output preserves that guard.

## Slice 2: aggregate daemon status projection

After Slice 1 produces a successful run, capture a 60-second idle daemon baseline: process CPU delta,
resident memory, projection call count, and rows materialized. Separately time the revision-poller
session reopen and `Evaluate` status projection so the larger family-index reopen cannot hide the
status change. Then replace the daemon-only full-row projection with one aggregate store query.

The store query accepts `(workspaceId, selected freshness key)` and returns only:

- total status rows;
- pending rows (`unknown` or `running`);
- rows stale at the selected key;
- red rows fresh at the selected key.

Freshness is computed against the same durable rule used by `ContinuousTestDurableFreshness`: a
committed green/red/skipped row at the selected identity+revision, or a green row whose watermark for
that identity reaches the selected revision. The query joins the existing indexed
`ct_case_fresh_watermarks` table and returns one aggregate row.

`ContinuousTestStatusProjection` gains an aggregate-input overload so verdict precedence stays in the
pure domain layer: unhealthy/empty/pending → unknown, then stale → partial, then fresh red → red,
otherwise green. `ContinuousTestDaemonHost.Evaluate` uses this aggregate path. User-requested status,
failures, queue selection, and all callers that need per-case rows keep `ListContinuousTestStatuses`.

The aggregate query must have a parity test against the existing row-by-row projection covering no
cursor, identity mismatch, old revision, pending states, fresh red, watermark green, skipped, empty,
and unhealthy-watch cases. Its query plan must use the existing workspace/freshness indexes and avoid
temporary sorting.

The aggregate query still scans matching status rows inside SQLite, but it returns one record and
materializes zero `ContinuousTestStatus` or watermark collections in the daemon. A deterministic
allocation/row-count probe measures this slice independently of total idle CPU. The per-tick
`MillerArtifactRevisionSource` family-session reopen is measured and recorded as a separate candidate;
it is not bundled into this change.

Cross-process change detection was considered and deferred. `PRAGMA data_version` is connection-local,
while `ContinuousTestStore` deliberately opens fresh non-pooled read connections; an in-memory write
counter would miss foreground writers. Adding a durable mutation counter or watcher is a schema/
lifecycle change larger than the aggregate projection and needs separate evidence.

## Architecture Quality

**Affected modules:** `Miller.Testing` xUnit provider, CT store, status projection, and daemon host.

**Caller-facing interface:** no CLI, MCP, database-schema, artifact, or plugin contract changes. One
internal aggregate record/store method is added; the existing provider/coordinator artifact seam is
reused.

**Depth/locality check:** transport behavior remains inside `DotnetTestProvider`; daemon aggregation
remains inside the store/projection/host boundary. Detailed status callers are unchanged.

**Test surface:** provider command/result tests, strict coordinator artifact-import integration, store
projection parity, query-plan evidence, daemon snapshot tests, and one real Scale whole-suite run.

**Seams/adapters:** the existing `ProviderRunResult.ResultArtifactPath` and
`TryImportProviderResultArtifact` seam earns reuse. The aggregate DTO exists only to keep verdict policy
out of SQL and large rowsets out of the daemon.

**Rejected shortcuts:** output-cap inflation, unbounded result capture, duplicated verdict policy in
the daemon, caching detailed status rows, and changing public status output.

**Architecture risk:** medium. The public surface is unchanged, but both fixes sit on correctness-
sensitive fail-safe paths; parity and real-provider verification are required.

## Measurement contract

- Preserve the pre-fix whole-suite numbers and exact command in the findings document.
- Slice 1 after-number: identical Miller.Tests CT whole-suite workload, with wall time, peak RSS, exit
  status, recorded result count, stale count, and artifact size.
- Slice 2 before/after: identical 60-second idle window on the same successful CT state, reporting CPU
  delta, RSS range, poller time, projection time, projection count, detailed rows materialized, and
  per-call allocated bytes.
- Keep a change only when the identical workload improves and all correctness gates pass.
- Prefer deterministic operation-count guards; do not add wall-clock assertions to the test suite.

## Acceptance criteria

- [ ] Miller.Tests whole-suite CT completes without truncated-output failure and persists all reported
      JUnit cases.
- [ ] Whole-suite xUnit retained stdout remains bounded independently of suite size while verbose
      progress continues to reset the stall/liveness clock.
- [ ] Every imported JUnit row maps to a selected inventory id; unresolved rows fail before store
      mutation, while selected-but-unreported ids stay stale with a lifecycle diagnostic.
- [ ] Red, empty-artifact, malformed-artifact, unsupported-runner, and coverage-mode edges remain
      honest and tested.
- [ ] Selected/chunked xUnit behavior and exact theory-row attribution remain unchanged.
- [ ] The daemon no longer materializes detailed status or watermark collections every 250 ms.
- [ ] Aggregate and row-by-row verdict projection agree across every freshness/state edge case.
- [ ] Idle before/after evidence and whole-suite before/after evidence are recorded.
- [ ] Focused tests, the fast suite, required Scale scope, Release build, and `git diff --check` pass.
