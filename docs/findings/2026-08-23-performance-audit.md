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
