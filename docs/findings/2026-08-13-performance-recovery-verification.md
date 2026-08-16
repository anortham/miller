# Performance-recovery Linux handoff and native Windows verification

**Date:** 2026-08-16
**Status:** Native Windows evidence is complete for this packet, including Julie's latest 30/30 resolution contract rerun; this is not a release or adoption claim.

This finding records the Linux handoff and the bounded native Windows acceptance run for Task 8. Raw
transcripts and large replay files remain external under:

`C:\Users\alann\.miller\perf-recovery-windows-acceptance\run-5d593419-65bb7862`

## Source identity

| Tree | Acceptance source | State |
|---|---|---|
| Miller | `feature/performance-recovery` at `5d593419` | Windows acceptance base; current Windows fixes are uncommitted |
| Julie | `feature/miller-performance-recovery-producer` at `65bb7862` | Producer base; current Windows fixes are uncommitted |

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

## Native Windows acceptance

The bounded Windows gates produced the following results:

| Gate | Result |
|---|---:|
| Miller Release build | 0 warnings/errors |
| Miller fast tests | 6,546 passed / 24 skipped |
| Miller Scale tests | 142 passed / 6 skipped |
| Focused .NET TRX | 80 passed / 6 skipped |
| Python replay suite | 150 passed / 3 skipped |
| Recording proxy contracts | 17/17 |
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

## Adoption boundary

The evidence and all current Windows fixes are uncommitted. No commit or push was made. Pinning,
adoption, pushing, tagging, publishing, and release remain approval-gated; this finding makes no
release claim.
