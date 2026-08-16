# Performance-recovery Linux handoff and native Windows verification

**Date:** 2026-08-16
**Status:** Exact-current source, build, fast, Scale, and performance traceability is complete for this packet,
including Julie's latest 30/30 resolution contract rerun. Miller `21b73bcf` and producer `152f51e4` are local
and not pushed; this is not a release or adoption claim.

This finding records the Linux handoff and the bounded native Windows acceptance run for Task 8. Raw
transcripts and large replay files remain external under:

`C:\Users\alann\.miller\perf-recovery-windows-acceptance\run-5d593419-65bb7862`

## Source identity

| Tree | Acceptance source | State |
|---|---|---|
| Miller | `feature/performance-recovery` base `5d593419`; exact-current `21b73bcf` | Exact-current correction and evidence are local and not pushed |
| Julie | `feature/miller-performance-recovery-producer` base `65bb7862`; exact-current `152f51e4` | Exact corrected producer tree is local and not pushed |

The prior Linux handoff tested Miller `686dd6e4` against Julie `65bb7862`. Julie's Linux producer gate
passed format, strict all-target/all-feature Clippy, the 120-test xtask suite, 4,318 default tests,
412 feature-gated contracts, and three official performance runs: full median `29,522 ms`, scoped
median `11,002 ms`, with G1-G6 and semantic/applied/row diffs green. The installed Linux
`julie-extract 2.33.2` SHA-256 was
`46562b537878f36a13adab629413a99daf490fa4b1b58a7b708b36a6eb373c7d`.

The Linux Miller handoff recorded a Release build with zero warnings/errors, 6,566 fast tests with
four skips, 138 Scale tests with ten skips, and a 30-minute semantic soak with 17/17 probes,
14,648 queries/batches, one shared Vulkan broker, zero reconnects, and both expected kill/recovery
probes green. The final exact Linux full-resolution replay was `54,814 ms` wall,
`44,673 ms` resolution, and `26,830,848` bytes peak PSS.

The Linux and Windows rows intentionally do not claim identical source commits. The Linux handoff used
Miller `686dd6e4` with Julie `65bb7862`; exact-current verification used Miller `21b73bcf` and producer
`152f51e4`, both local and not pushed. Exact-current Linux source, Release build, fast, Scale, and
performance traceability is closed. The 30-minute Linux semantic soak is carried from `686dd6e4` because
`686dd6e4..21b73bcf` changes no semantic production code (only soak scripts), and those scripts passed the
exact-current native Windows `1,800 s` gate. These are the same intentional production recovery state;
post-Linux differences are platform and harness hardening, not an identical-SHA claim.

## Native Windows acceptance

The bounded Windows gates produced the following results:

| Gate | Result |
|---|---:|
| Miller Release build | 0 warnings/errors |
| Miller fast tests (exact-current) | 6,567 passed / 4 skipped |
| Miller Scale tests | 138 passed / 10 skipped against the exact corrected producer before the CLI-only bridge change; extraction path unchanged, so no rerun was needed |
| Focused .NET TRX | 80 passed / 6 skipped |
| Python replay suite | 150 passed / 3 skipped |
| Recording proxy contracts | 17/17 |
| Exact-current combined gate | 168 total / 2 skipped |
| Producer exact-tree gates | format; strict Clippy; manifest 27/27; import 4/4; resolution 30/30 |
| Julie coordinator contracts | 65 passed |
| Julie maintenance contracts | 22 + 15 + 2 + 1 + 3 passed |
| Julie exact process tests | 2 passed |
| Julie resolution final gate | 30/30 passed, 0 failed in 109.83 s (`25-julie-final-resolution-contract.txt`) |

The full semantic soak ran for `1,800 s`: 26/26 normal probes completed with zero failures or hangs;
broker and owner recovery were `1.249 s` and `1.54 s`; and one-session and many-session GPU deltas
were both `87 MiB`. The prepared preflight verified both required models before probing while
retaining the shared user-global model cache, dual-model endpoint separation, and accelerator
acceptance rules.

The strict replay report `windows-performance-summary-final2.json` records `passed=true`,
`42` measured records across `14` workloads, and zero measured hard-gate failures or nonzero
exits. The largest measured peak `PrivateUsage` was `60,510,208` bytes and the largest idle
`PrivateUsage` was `35,983,360` bytes.

| Workload | Median wall (ms) | Max peak `PrivateUsage` (bytes) |
|---|---:|---:|
| `startup.reader.warm` | 704 | 25,837,568 |
| `startup.leader.no_change` | 1,229 | 38,879,232 |
| `workspace.open.no_change` | 1,086 | 25,661,440 |
| `producer.retry.identical` | 235 | 2,109,440 |
| `producer.resolve.one_file` | 247 | 17,383,424 |
| `producer.resolve.full` | 226 | 20,303,872 |
| `tool.inspect.warm` | 474 | 12,316,672 |
| `tool.context.references.depth0` | 1,940 | 59,097,088 |
| `tool.context.references.depth1` | 1,907 | 60,510,208 |
| `tool.context.references.depth1.batch_off` | 1,919 | 56,221,696 |
| `tool.context.references.depth1.batch_on` | 1,919 | 59,965,440 |
| `tool.context.references.depth1.semantic` | 1,947 | 60,166,144 |
| `tool.impact.bounded` | 1,430 | 26,714,112 |
| `tool.trace.warm` | 539 | 13,877,248 |

The disposable replay snapshot passed `quick_check`; its SHA-256 is
`99aae58e5c0147badeba6b2b192c594a56d9ebfaabf431914a17c4e2fe46217a`, and no WAL/SHM files were
present at snapshot validation. The protected live family was used only for the safety guard and a
post-auto-restart read-only maintenance inspection; that inspection was structurally valid.

## PERF-009 bridge budget closure

The baseline CLI runs were `6.62/6.64/6.65 s` at `408,092/410,220/408,356 KB` RSS; `dotnet-trace`
identified full `RepositoryIndexLoader`/`SymbolGraphReader` work as dominant. The direct lean loader
completed in `1.386–1.393 s` at about `176 MB`. First MCP bridge runs completed in `1.787/1.779/1.758 s`
at about `187 MB` PSS with an identical output hash. After Miller `21b73bcf`, final CLI runs completed in
`1.45/1.44/1.47 s` at `179,860/180,108/180,036 KB` RSS with identical output SHA-256
`1de73d186bf36926b9e0102364e6e61094a6edfc232802554f165ad11d707a21`. PERF-009 is accepted; no sidecar
is needed by this measured gate.

## Windows defects corrected during acceptance

- C# fixture paths and bounded cleanup/read retries were made Windows-safe in the Miller contract and
  Scale fixtures.
- The recording MCP proxy now uses a fail-closed Windows Job Object with `KILL_ON_JOB_CLOSE`,
  suspended-process setup, output draining, and controller cancellation; the final proxy gate was 17/17.
- Both semantic soak scripts explicitly prepare and verify the pinned default and fallback models before
  probes; the shared user-global model cache remains intentional.
- The replay harness now handles extended Windows paths and `change_root` path canonicalization.
- Julie's Windows family-view root identity was corrected; the latest resolution contract passed 30/30
  with zero failures in 109.83 s.

## PERF-008 integrated scan-cap evidence

The later Linux evidence packet used the exact Miller tree at `0a23584b` and its bundled
`julie-extract 2.33.2` binary (SHA-256
`257ea63c5fd86cec59ad7a1b739105b737ac84490c0f964fef43013e57e7162c`). Raw evidence is preserved at
`/home/murphy/.miller/perf-recovery-perf008-0a23584b-2KGQea`; its `SHA256SUMS.txt` hash is
`8ff2451e6d372bf5e02ce058cddbafaafefe1fe69b54935252dc206ec64aaa7c`, and
`protected_family_selected=no`.

The host had 24 processors, so the default policy resolved to `--jobs 4`. Process polling captured the actual
Miller child argv with `--jobs 4` for the integrated paths and no `--jobs 0`/auto fallback. The fresh integrated
`workspace open` exited `0` in `154.97 s`, with `117.10/26.73 s` user/system CPU, `881,572 KB` maximum RSS,
and `144,620 ms` scan duration. The direct exact Julie scan exited `0` in `243.39 s`, with `257.02/1.00 s`
user/system CPU, `1,663,236 KB` maximum RSS, and Julie-reported timing of `243,142 ms` total, `6,175 ms`
extraction spool, `236,833 ms` artifact write, `228,240 ms` resolution, and `2,177 ms` index build.
No-change Miller `workspace full` exited `0` in `1.05 s` at `73,940 KB`; no-change `workspace refresh` exited
`0` in `0.93 s` at `74,452 KB`.

Cold full indexing remains expensive, but the measured extraction stayed near one CPU and did not silently become
an all-core workload. PERF-008 therefore closes the saturation/job-cap evidence; it does not establish a new
cold-build latency budget. The earlier source-identity statements for Miller `21b73bcf` and producer `152f51e4`
remain local and unpushed; this later evidence packet is likewise local and unpushed.

## Remaining recovery gates

- PERF-009's real bridge trace budget gate is closed by the measured CLI/lean-loader/MCP evidence above;
  no sidecar is needed.
- Exact-current Linux source, Release build, fast, Scale, and performance traceability is closed. The
  semantic soak is carried from `686dd6e4` under the no-semantic-production-code condition above, with the
  soak scripts passing the exact-current native Windows `1,800 s` gate.

## Adoption boundary

Miller `21b73bcf` and producer `152f51e4` are local commits and are not pushed. After these local
commits and docs, the remaining actions are renewed push approval, then separately approval-gated
producer adoption, pinning, tagging, publishing, and release. This finding makes no release claim.
