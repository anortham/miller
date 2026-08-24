# Miller v1.21.1 Performance Audit

## Scope

- Audit the continuous-testing release for latency, CPU, memory, polling, selection, and provider-execution regressions.
- Audit representative non-CT CLI paths for collateral regressions and general hot spots.
- Do not change production code without a reproducible baseline and a confirmed cause.

## Fixed environment

- Candidate: Miller `1.21.1+28f680ac27df`, Release configuration, .NET SDK 10.0.110.
- Source tree: `/home/murphy/source/miller/.worktrees/perf-ct-audit-2026-08-23`.
- Data workspace: `/home/murphy/source/miller`, family-store generation selected at audit start.
- Concurrency: one command at a time on the local Linux workstation.
- Timing rule: discard one warm-up, then collect ten runs and report p95 plus the full range.

## Workloads

### CT status

- Metric: one-shot CLI wall time and peak resident memory.
- Command: `miller tests status --json --workspace /home/murphy/source/miller`.
- Invariant: read-only; starts no daemon and performs no refresh.

### Common read paths

- Metric: one-shot CLI wall time.
- Symbol search: `miller search WorkspaceIndexProvider --mode symbol --arm lexical --limit 5 --json --workspace /home/murphy/source/miller`.
- Inspect: `miller inspect WorkspaceIndexProvider --depth overview --json --workspace /home/murphy/source/miller`.
- Context: `miller context "continuous test revision poller" --token-budget 4000 --json --workspace /home/murphy/source/miller`.
- Impact: `miller impact ContinuousTestRevisionPoller --json --workspace /home/murphy/source/miller`.

### CT active and idle daemon

- Metric: initial run wall time split into selection/build/provider phases from CT activity; steady-state CPU-seconds and resident memory over a 60-second no-change window.
- Data: this audit worktree with the real `tests/Miller.Tests/Miller.Tests.csproj`, excluding `Category=Scale` through the persisted CT project contract.
- Invariant: restore the daemon to its pre-audit stopped state when measurement finishes.

### Scaling and operation counts

- Metric: SQLite statements/rows and algorithmic growth at existing synthetic CT sizes.
- Method: identify existing deterministic test seams after the wall-time baselines, then compare operation counts at `n` and `2n` without wall-clock assertions.

## Baseline results

Ten warm one-shot runs against the same pinned generation:

| Workload | p95 wall | Range | Mean |
| --- | ---: | ---: | ---: |
| CT status | 210.4 ms | 199.1-210.4 ms | 203.9 ms |
| Lexical symbol search | 282.7 ms | 257.2-282.7 ms | 265.3 ms |
| Inspect overview | 538.9 ms | 518.9-538.9 ms | 526.4 ms |
| Context | 1.457 s | 1.416-1.457 s | 1.431 s |
| Impact | 3.487 s | 3.197-3.487 s | 3.376 s |

CT status peak resident memory ranged from 70,668 KB to 71,572 KB over ten warm runs.

### CT active observation

One foreground cycle over three persisted projects and revision 38,607 took 112.78 seconds wall time,
used 134.93 user CPU-seconds plus 233.43 system CPU-seconds, and peaked at 693,496 KB resident memory.
The two small projects passed in 12.78 and 20.92 seconds. Miller.Tests ended as a provider-level failure
after 111.20 seconds, with no failing test case recorded. The same released tree's normal fast suite had
passed 8,301 tests in 45 seconds during worktree setup.

The relevant workspace's retained CT build cache occupies 1.7 GB. Its two Miller.Tests generations are
302 MB and 1.4 GB; the larger generation duplicates large runtime/output trees under project-specific
directories. All workspaces under `/tmp/miller-ct/build` occupied about 8.6 GB at observation time. The
measured cycle reused an existing generation, so retained size is a disk-pressure finding rather than an
explanation for all 112.78 seconds.

The first cycle overlapped read-only audit workers and is not a valid performance comparison. A quiet retry
selected only the still-stale Miller.Tests project. It took 75.78 seconds wall time, used 139.21 user CPU-seconds
plus 235.41 system CPU-seconds, peaked at 585,236 KB resident memory, and failed after 74.50 seconds without
recording one case result.

### Task 3 Release foreground proof

At 2026-08-23 22:14:29–22:16:14.827 CDT, the task worktree at HEAD `13bfcbdc532754781c4af96bb54cb38e596653d1`
built with `dotnet build Miller.slnx -c Release`: 0 warnings and 0 errors. Before the run,
`miller tests status --json --workspace /home/murphy/source/miller` reported `daemon.state=stopped`,
`running=false`, and three enabled xUnit projects. The exact required foreground command was run with the
Release binary under `/usr/bin/time -v`:

```text
/usr/bin/time -v -o /tmp/miller-ct-task3-run.time \
  /home/murphy/source/miller/.worktrees/perf-ct-audit-2026-08-23/src/Miller.Server/bin/Release/net10.0/miller \
  tests run --json --workspace /home/murphy/source/miller
```

The process exited 0 after 1:47.77 wall time (117.13 user CPU-seconds, 229.97 system CPU-seconds,
525,516 KB maximum RSS), and stdout was valid 252-byte JSON with `execution=foreground_one_shot`; however,
the reported verdict was `partial`, so this is diagnosis and not a passing whole-suite baseline. Three
persisted provider runs all completed `passed` at revision 38,946: FusionArm 11 results, RetrievalEval 95,
and Miller.Tests 8,260 (8,251 passed plus 9 skipped). The three JUnit artifacts were retained and parsed:
11 cases/0 failures/0 errors/0 disabled (2,421 bytes), 95/0/0/0 (21,786 bytes), and 8,310/0/0/9
(1,842,373 bytes). The artifact payloads reported `mapped_selected=11`, `95`, and `8,260`, with
`selected_residue=0` and `new_artifact_cases=0` for each. The read-only `ct.db` was 56,369,152 bytes.

The partial verdict is explained by two historical project-discovery pseudo-cases that remain stale and
were not reported by the run: `project-discovery::/home/murphy/source/miller/src/Miller.Testing/Miller.Testing.csproj`
(`ct-discovery-failure:dad7afa7e0ecbe8301dabf9b`) and
`project-discovery::/home/murphy/source/miller/tests/Miller.SharedBrokerTestHost/Miller.SharedBrokerTestHost.csproj`
(`ct-discovery-failure:76f293dbfbc14b7ee6e1978e`). Both projects are disabled in `ct_test_projects`; each
has one historical failed result but zero results in the three new run IDs. Their prior failure records are
an exit-134 `--endpoint` argument exception and a missing `Miller.Testing` process path. Post-run status was
still `daemon=stopped`, `verdict=partial`, `stale=2`. Miller's selector only admits provider-managed cases
whose source starts with `ct-provider:`; these two `ct-project-status` lifecycle rows belong to disabled
projects and were not runnable work. The same Release binary was therefore started for a qualified no-runnable-
work idle sample, while retaining `stale=2` as a hard status fact.

`miller tests serve --json --workspace /home/murphy/source/miller` returned `status=started`, `pid=1038423`,
and `publication.readiness=ready`. Start and end status reads both reported the same Release version,
`activity=idle`, `run=null`, and `loop_stalled=false`. The process stayed alive and sleeping (`/proc/1038423/stat`
state `S`) at five samples over 60.036670614 seconds, from 2026-08-23 22:20:54.099907685 CDT through
22:21:54.129702080 CDT. CPU ticks moved from user/system `486/53` to `1103/144`: +617 user, +91 system,
708 total ticks, or 7.08 CPU-seconds (11.80% of one core; report-only). RSS samples ranged from 120,192 to
131,716 KB (11,524 KB span; report-only). A final `tests stop --json` returned `status=stopped`; the PID was
absent from `/proc`, and the final status read reported `daemon.state=stopped`, `running=false`, `run=null`.
No provider process was present in the final process scan. Exact poller-versus-projection phase timing was not
observable; the sample is a pre-aggregate, no-runnable-work idle baseline, not proof that stale_count is zero.

### Release A/B

The same commands and current family-store generation were read by the released `v1.20.1` binary. The CT schema
was not read by the old binary because v1.21 migrated it in place.

| Workload | v1.20.1 p95 | v1.21.1 p95 | Result |
| --- | ---: | ---: | --- |
| Lexical symbol search | 266.66 ms | 269.76 ms | Mean differed by 0.78 ms in 20 interleaved runs; no regression |
| Inspect overview | 3.428 s | 0.539 s | 84% faster |
| Context | 4.895 s | 1.457 s | 70% faster |
| Impact | 6.352 s | 3.487 s | 45% faster |

The CT release did not introduce a measurable regression in these representative non-CT read paths.

## Confirmed CT regression

The quiet run's log reports that standard output exceeded the capture cap. The xUnit provider uses the full JSON
reporter, captures at most 8M characters, then rejects a truncated stream. The completed run left a valid JUnit
artifact containing 8,310 test cases, but the provider discarded it before the coordinator's existing artifact
import fallback could run.

A diagnostic redirect was stopped after its environment diverged from CT, so its timing is not a baseline. Its
partial output already measured 48,502,555 bytes and 75,369 JSON event lines; the corresponding JUnit artifact
was 2,040,925 bytes. Raising the 8M cap would increase memory and move, not remove, the failure threshold.

The preferred repair is local to xUnit whole-suite execution: use the official `verbose` reporter plus
`-noAutoReporters`, retain the existing JUnit artifact, validate it with `JunitTestResultParser`, and return the
existing artifact-only `ProviderRunResult`. Verbose progress preserves the output-silence stall guard while the
bounded capture is no longer parsed. `ContinuousTestCoordinator.TryImportProviderResultArtifact` maps the
artifact back to stored cases, including theory rows. Provider imports preflight attribution and fail before
mutation if none of a non-empty selected inventory maps; partial residue is diagnosed and remains stale. New
artifact rows remain importable because whole-suite runs can discover tests added since the prior inventory.
Selected/chunked xUnit runs keep JSON; their 120-unit/6-KiB chunk cap bounds output below this failure mode.

## Other hot spots

### CT enabled paths

- The daemon ticks every 250 ms per context. Each tick reopens a family read session and rebuilds the temporary
  compatibility projection over 1,827 visible manifest entries and 595,077 symbols: about four opens per second
  per context, before useful work exists.
- Each tick materializes all 8,368 CT status rows through fresh non-pooled SQLite reads: about 33,472 rows per
  second for the primary context. This has no CPU, allocation, or operation-count guard.
- Changed-revision selection loads all test cases and then all statuses per enabled project. Current selection is
  therefore total-case and project-fan-out work rather than changed-file work.
- Completing a run performs approximately `2 + 4R` SQLite operations. The largest recorded run had 8,138 results,
  implying about 32,554 operations. The per-result recent-history query uses a temporary sort.
- `ContinuousTestDaemonQueue._retryAttempts` has no eviction across revisions. Per-key retry count is bounded;
  long-lived key cardinality is not.
- CT run/history rows have no retention path. Current scale is still small: 43 MiB, 24 runs after audit,
  13,717 results before the failed retries.

These are confirmed scaling shapes, not yet proven dominant wall-time costs. They need deterministic counters or
an idle-daemon CPU/RSS measurement after the whole-suite regression is repaired.

### General read paths

- Impact remains the slowest representative one-shot command at 3.487 seconds p95, but it is 45% faster than
  v1.20.1. A file impact can issue roughly 11,278 indexed identifier-detail point queries and lazily load 429
  version slices; this is repeated indexed work, not an unindexed table scan.
- Context graph expansion is material: a separate probe fell from 3.011 seconds p95 to 1.720 seconds with
  `--max-hops 0`. Existing hop/reach caps bound it; no guard counts detail queries or lazy slices.

## Decision boundary

The user approved the xUnit result-transport and daemon aggregate-projection design. A one-call Claude doubt pass
found and closed two design gaps: silent output would defeat the ten-minute stall/liveness clock, and permissive
artifact import could create new cases while selected inventory rows stayed stale. General impact and context
work is not a release regression and remains a separate measured optimization effort.

## Task 5 final measurement and evidence

### Scope, source, and evidence route

The final measurement ran on 2026-08-23 in `/home/murphy/source/miller/.worktrees/perf-ct-audit-2026-08-23`,
branch `perf/ct-audit-2026-08-23`, HEAD `90207cb7412f1675e0c57ab6ec1a25db3a166356`. The tree was clean before
measurement; the only task edits afterward are this findings document, the plan's Task 5 acceptance/ledger
portion, and the ignored worker report. The live CT database remained `/home/murphy/source/miller/.miller/ct.db`.

Miller evidence calls used for code and contract claims were: `workspace onboarding` for the target worktree;
`search` (`file`, `content`, `source`, and `all-text`) for the prior audit, Task 5 plan, aggregate symbols,
allocation guard, and query-plan test; and `inspect` for the findings file, `AggregateContinuousTestStatuses`,
`ContinuousTestStatusAggregate`, `ContinuousTestDaemonHost.Evaluate`, and the aggregate tests. Large raw logs were
kept in `/tmp` and read only at bounded excerpts.

### Build and live-state evidence

At 22:50:29 CDT, the required command completed with hard-gate success:

```text
dotnet build Miller.slnx -c Release
Build succeeded. 0 Warning(s), 0 Error(s)
```

The first read at 22:50:34 found the daemon stopped and the expected three enabled xUnit projects, but the live
index cursor had advanced to revision 39051. Its revision-relative projection reported `stale_count=8368` and
there were zero fresh-watermark rows for that cursor, while direct SQLite state still had 8,357 green and 9 skipped
provider rows plus exactly two stale `ct-project-status` discovery rows. Starting the daemon at 22:52:30 (PID
1062919) left it `running/idle`, `run=null`, with no provider process and no provider runnable rows for about 60
seconds; the empty-delta watermark convergence did not occur. It was stopped at 22:53:34 with `pid_gone=true`.

Per the measurement handoff, the exact foreground command was then run once as environment-drift recovery, not as
a second provider benchmark:

```text
/usr/bin/time -v -o /tmp/miller-ct-task5-recovery.time \
  /home/murphy/source/miller/.worktrees/perf-ct-audit-2026-08-23/src/Miller.Server/bin/Release/net10.0/miller \
  tests run --json --workspace /home/murphy/source/miller
```

It ran from 22:53:55.475 to 22:55:42.128 CDT, exited 0, and returned valid foreground JSON. The recovery imported
11, 95, and 8,310 JUnit cases with `mapped_selected=11`, `95`, and `8,260`, `selected_residue=0`, and
`new_artifact_cases=0`; its provider timing was 1:46.64 wall, 116.58 user seconds, 230.38 system seconds, and
567,196 KB maximum RSS. Those provider numbers are recovery evidence only, not the before/after workload metric.

After recovery the status projection was back to `stale_count=2` at revision 39051. Direct state contained exactly
two stale lifecycle rows, no provider rows in `unknown`, `running`, or `stale`, and no budget holder or provider
process. One provider row was fresh red (`FtsSymbolSearchIndexTests.CountTokenOccurrences_RepeatedHighFanoutScoringDoesNotAllocate`); it is not automatic stale work and is retained as a comparison concern. The two stale lifecycle rows are the same disabled-project discovery failures recorded in Task 3.

### Comparable idle sample

The measured daemon was the same Release binary and the same target root. It started at 22:59:08.821 CDT as PID
1067947; warm-up reached `running`, sleeping state `S`, `activity=idle`, `run=null`, `stale_count=2`, and zero
provider-runnable rows before t0. `/proc/<pid>/stat` and `/proc/<pid>/status` were sampled at absolute 15-second
deadlines, matching the five-sample Task 3 cadence:

```text
/home/murphy/source/miller/.worktrees/perf-ct-audit-2026-08-23/src/Miller.Server/bin/Release/net10.0/miller \
  tests serve --json --workspace /home/murphy/source/miller
/home/murphy/source/miller/.worktrees/perf-ct-audit-2026-08-23/src/Miller.Server/bin/Release/net10.0/miller \
  tests status --json --workspace /home/murphy/source/miller
/home/murphy/source/miller/.worktrees/perf-ct-audit-2026-08-23/src/Miller.Server/bin/Release/net10.0/miller \
  tests stop --json --workspace /home/murphy/source/miller
```

For each sample, the raw process facts came from
`awk '{print $1" "$3" "$14" "$15" "$22}' /proc/<pid>/stat` and
`awk '/^VmRSS:/ {print $2; exit}' /proc/<pid>/status`; the read-only SQLite checks counted lifecycle stale
rows and provider states. The provider process scan matched only `dotnet test`, `cargo test`, `pytest`, or
`node test` process names.

| Sample | CDT timestamp | `/proc` state | start-time ticks | user/system ticks | total ticks | RSS KB | daemon activity | run | stale | provider runnable |
| --- | --- | --- | ---: | ---: | ---: | ---: | --- | --- | ---: | ---: |
| t0 | 22:59:09.311275564 | S | 27981504 | 18/2 | 20 | 71,316 | idle | null | 2 | 0 |
| t15 | 22:59:24.316498682 | S | 27981504 | 179/17 | 196 | 106,160 | idle | null | 2 | 0 |
| t30 | 22:59:39.317024156 | S | 27981504 | 265/29 | 294 | 106,748 | idle | null | 2 | 0 |
| t45 | 22:59:54.317132052 | S | 27981504 | 338/44 | 382 | 107,196 | idle | null | 2 | 0 |
| t60 | 23:00:09.316850723 | S | 27981504 | 410/57 | 467 | 107,192 | idle | null | 2 | 0 |

The measured interval was 60.005417358 seconds at `CLK_TCK=100`. Direct CPU tick identity is hard evidence: the
same PID start-time identity stayed constant and the process stayed sleeping with no run or provider execution.
CPU seconds and one-core percentage are derived from those ticks; wall time and RSS are report-only. The graceful
stop returned `{"status":"stopped","reason":"stopped"}` at 23:00:09.567 CDT, `/proc/1067947` was gone by
23:00:09.767, and the final status was stopped with `run=null` and `budget_holder=null`. No provider process was
present in the final scan.

### Before/after comparison

| Metric | Task 3 baseline | Task 5 after | Change |
| --- | ---: | ---: | ---: |
| Idle window | 60.036670614 s | 60.005417358 s | -0.031253256 s (-0.0521%, report-only) |
| CPU ticks (user + system) | 708 | 447 | -261 (-36.8644%, direct tick evidence) |
| CPU seconds at CLK_TCK=100 | 7.08 s | 4.47 s | -2.61 s (-36.8644%, derived) |
| One-core CPU share | 11.80% | 7.4493% | -4.3507 percentage points (-36.8703%, derived) |
| RSS range | 120,192–131,716 KB | 71,316–107,196 KB | report-only; not thresholded |

The after CPU reduction is consistent with replacing detailed status/watermark materialization in the idle
projection path. The accepted Task 4 focused evidence recorded the deterministic allocation guard as
`detailed_allocations=720480` versus `aggregate_allocations=38624` bytes (18.6537x lower; 94.6391% fewer bytes).
Its query-plan test requires existing `idx_ct_test_states_` indexes and rejects `USE TEMP B-TREE` for both selected
and no-cursor aggregate SQL. These are hard test/plan claims consumed from Task 4; this worker did not rerun the
branch-gate suites.

### Deferred hot spots and concerns

The poller session-reopen candidate remains explicitly deferred: each 250 ms CT tick still reopens a family read
session and rebuilds the compatibility projection. Changed-revision selection remains total-case/project-fan-out
work; run completion still has the recorded per-result operation/history shape; retry-key cardinality still lacks
eviction; and CT run/history retention remains unaddressed. None of these is silently declared fixed by the
aggregate projection change.

The live index cursor drift required the one recovery run, so the provider recovery result is not presented as an
after benchmark. The recovery also produced one fresh red provider case while Task 3 had all provider cases green
or skipped; that case is not automatic runnable work but means the provider result set is not byte-for-byte identical.
RSS is substantially lower but has a wider sampled span, so it remains report-only. Lead-owned fast/Scale suites,
`git diff --check`, final worktree reconciliation, and branch-gate completion remain pending.

### Verification ledger

| Invariant | Exact command/scope | Commit | Result | Evidence class | Timestamp |
| --- | --- | --- | --- | --- | --- |
| Release build has no warnings/errors | `dotnet build Miller.slnx -c Release` | `90207cb7` | PASS, 0 warnings, 0 errors | hard gate | 2026-08-23 22:50:29 CDT |
| Target has the expected project set and no daemon before measurement | `miller tests status --json --workspace /home/murphy/source/miller` plus read-only SQLite state queries | `90207cb7` | PASS after recovery: 3 enabled projects, 2 disabled lifecycle rows, `stale_count=2`, budget null | hard state | 2026-08-23 22:55:42 CDT |
| Exact provider recovery imports artifacts | `/usr/bin/time -v ... miller tests run --json --workspace /home/murphy/source/miller` | `90207cb7` | PASS exit 0; recovery-only, partial verdict due retained lifecycle/red facts | diagnostic/recovery | 2026-08-23 22:53:55–22:55:42 CDT |
| Idle daemon stays alive, idle, and provider-free | `miller tests serve/stop --json` plus five `/proc/<pid>` samples | `90207cb7` | PASS: PID 1067947, constant start-time ticks, S/idle/run null, stale 2, provider runnable 0; PID gone after stop | hard state; CPU derived | 2026-08-23 22:59:08–23:00:09 CDT |
| Idle CPU/RSS comparison | Same five-sample cadence as Task 3 | `13bfcbdc` → `90207cb7` | 708 → 447 ticks; 7.08 → 4.47 CPU-s; RSS ranges recorded | ticks direct; wall/RSS report-only | baseline 2026-08-23 22:20:54–22:21:54; after 22:59:09–23:00:09 CDT |
| Aggregate parity, allocation, and SQL-plan guard | Accepted Task 4 focused evidence (`ContinuousTestStoreTests`) | `90207cb7` | PASS evidence: 720,480 vs 38,624 bytes; indexes used; no temp sort | hard focused-test evidence | Task 4 handoff |
| Full fast/Scale/diff/worktree branch gates | `scripts/test.sh`; `scripts/test.sh scale`; `git diff --check`; related-worktree status audit | `90207cb7` | PASS: fast 8,320 passed/9 skipped; Scale 161 passed/16 skipped; diff clean | branch gate | 2026-08-23 23:05 CDT |
