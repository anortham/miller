# Production-volume Linux performance baseline (Task 1B-B)

**Date:** 2026-08-14
**Status:** Linux baseline frozen; no performance recovery or Windows completion is claimed.

This finding freezes the exact Linux evidence needed before Task 5B. The raw snapshot and JSONL
files remain external under `/home/murphy/.miller/perf-recovery-task1b-b-baseline-c9c57645` and are
not committed.

## Identity and isolation

- Baseline root: `/home/murphy/.miller/perf-recovery-task1b-b-baseline-c9c57645`.
- Miller: `1.19.1+c3b8d7cf0dec`. Julie: `julie-extract 2.33.2`.
- The clean exact rows required two corrections: `c6ce1468` fixed the setup timeout in the
  successor 90-test harness, and `00b8b6e4` supplied the blank resolve root. The earlier shimmed
  one-file row is superseded by the exact unshimmed row below.
- The final one-file and full-resolve children targeted only their temporary `store-family`
  directories. The copied input, live family, source `.miller/store.json`, source `README.md`, and
  `CURRENT` were unchanged before and after those runs.
- The initial snapshot seed and the later producer seed are different. The immutable snapshot
  began with 21 durable files and digest
  `85d0a693dfab325350fdb89cdc99075215c609f2e48af6d855ba4e57456d238c`; startup and sidecar
  convergence later produced a 22-file seed with digest
  `279afa64523b5d9ad4e792cee0fe9d0fb682402d1893bca3f63316d799100e02`. The rows therefore do not
  claim one byte-identical store across the whole baseline. Mutating producer rows still cloned
  isolated inputs of their own.

## Snapshot

`snapshot-result.json` SHA-256:
`4a29b8b515c7e04c28a1e0126f1cc771ccb6a5d8d49e2e343c8aeb07269b7849`.

| Fact | Captured value |
|---|---|
| Source durable family | 21 files / 4,379,587,064 bytes |
| Source digest | `85d0a693dfab325350fdb89cdc99075215c609f2e48af6d855ba4e57456d238c` |
| Destination family | 19 files / 4,377,686,024 bytes |
| Destination digest | `8ae097d03d0db24cbebbdaf95607f6d8798768b877e3d94d9e93eca04bd30d05` |
| Generation | `gen-001` on source and destination |
| SQLite validation | 16 databases checked: 14 non-empty integrity checks passed; 2 empty partials skipped |
| Destination WAL/SHM | Absent at snapshot validation; the report records `wal_shm=false` |
| Claim gate | Passed with only dead/expired owner `cli-352212` (PID 352212 not alive) |

Later producer activity recreated six transient WAL/SHM files in the disposable copied store. They
are runtime sidecars, not a change to the immutable snapshot facts, and were left untouched.

## Measured rows

All row counts below were checked with bounded `jq`; hashes are SHA-256 of the external JSONL files.
Warmup values are shown separately from measured attempts.

| Evidence | Rows / SHA-256 | Result |
|---|---|---|
| `baseline-startup.jsonl` | 8 / `455ddfc1260ca1f3c696053ae0e91ad73a3ebadf8e1ffff12b7e144155d846b2` | Leader measured wall `28,353 / 27,754 / 27,757 ms` against the `2,000 ms` budget; hard gate false. `startup_total` was `27,584.709 / 26,967.3091 / 26,940.3336 ms` with `DidWork=false`. Warm reader measured `852 / 860 / 849 ms`; hard gate true. |
| `baseline-workspace-open.jsonl` | 4 / `bea6457cbf968b1a7c89740e522e1534122b9df79b79bb0d37d6daf874bdf98e` | Measured wall `27,489 / 27,499 / 27,553 ms` against `5,000 ms`; hard gate false. The sequence advanced `17,864 -> 18,070 -> 18,276 -> 18,482` while `CURRENT=gen-001`; no phase records were emitted. |
| `baseline-producer-retry.jsonl` | 4 / `5b9a675e51946bab692bd1732859863fecdc3fb748df5219969992d418f52123` | Warmup `26,646 ms`; measured `116 / 116 / 117 ms`, all hard-gate true, with identical output digest. Producer/version/phase metadata were absent. |
| `baseline-tools.jsonl` | 32 / `c237568f689926dc424a89b23bfe8e3c0d62e65f7bbd83bb06a3fcd67cb5f86f` | All 32 exited `0` without timeout, but every wall gate was false. Max PSS was `390,426,624` bytes, below the `629,145,600`-byte limit. |
| `baseline-resolve-one-file.jsonl` | 1 / `0bbad3f7370abe87351bb0c46804b1343348245bed4774b9d8d2aaf2a7761e0d` | Wall `60,004 ms`, CPU `58,356 ms`, PSS `29,622,272` bytes, exit `-15`, timed out, hard gate false against `5,000 ms`. The expected scoped mode was not proven: the producer emitted no JSON before termination, so actual mode and scope are unavailable. |
| `baseline-resolve-full.jsonl` | 1 / `bec0b3df62add19d77fff5e335993d09b50ff3dfe67c63e923628d47d96c57c4` | Wall `179,176 ms`, CPU `163,626 ms`, PSS `192,703,488` bytes, exit `0`, no timeout, hard gate false against `60,000 ms`. Full scope was exact: 1,510 files / 566,803 rows; phase timings were scope `71 ms`, diff `1,354 ms`, resolution `171,696 ms`; exact parity was true with `JULIE_STORE_RESOLUTION_DELTA=off`. |

The packet brief's stated startup hash contains `…a3ebf8e1…`; bounded `sha256sum` verification
produced `…a3ebadf8e1…` above. The computed hash is the artifact hash used here.

### Tool detail

Measured wall medians and ranges were:

| Workload | Median / range (ms) |
|---|---:|
| Inspect | 6,736 / 6,683–6,783 |
| Context depth 0 | 7,284 / 7,283–7,334 |
| Context depth 1 | 7,235 / 7,232–7,336 |
| Context depth 1, semantic | 7,332 / 7,329–7,334 |
| Context depth 1, batch off | 7,286 / 7,285–7,335 |
| Context depth 1, batch on | 7,484 / 7,341–7,586 |
| Impact | 6,890 / 6,882–6,932 |
| Trace | 6,779 / 6,734–6,782 |

Context outputs were identical for the selected depth-0, depth-1, semantic, and batch rows, and
same-depth batch parity passed. The selected query returned zero identifier rows and four non-symbol
rows. This proves that this baseline query did not expose added relationship rows; it does not prove
that depth semantics are universally equivalent.

## Conclusions and ownership boundaries

### Proved by this baseline

- Warm reader startup, byte-identical producer retry, and all recorded memory limits passed their
  applicable gates. General SQLite or memory pressure is therefore not the primary explanation for
  the observed failures.
- Repeated leader/lifecycle admission is the dominant observed cost across startup, workspace-open,
  and tool process invocations. `DidWork=false` beside sequence churn requires Task 5B phase
  attribution before a behavior repair is accepted.
- Full resolution is a separate dominant cost: `171,696 ms` of `179,176 ms` was resolution, while
  scope and diff were `71 ms` and `1,354 ms`. Task 6 owns incremental routing/equivalence before
  optimization; retained-history cost belongs to Task 7A and post-rotation read-plan cost to Task 7B.
- The one-file timeout does not prove that scoped mode was selected or rejected; scope evidence was
  unavailable because the producer emitted no JSON before termination.
- Context batch parity is clean but has no speedup on this query (`7,484 ms` batch-on median versus
  `7,286 ms` batch-off median). The batch switch remains default-off pending integration evidence.

### Incident and open gates

- A lead Miller context call spawned a live leader/resolve. Only those new PIDs were stopped; no
  generation was promoted. The orphan request was first backed up and requeued, then fully removed
  only after proving no receipt, result, or effect. Three scratch files were moved to retained
  evidence rather than deleted. Live `CURRENT` and the recovery source pointer stayed unchanged;
  bounded `quick_check` remained OK and no writer lease remained. This demonstrates that a
  read-oriented agent call can trigger expensive leader work; it is not baseline row data.
- A default semantic direct CLI also failed when a long `MILLER_HOME` produced a Unix-domain socket
  path over 108 characters; a semantic-off short-home retry succeeded. This remains an open
  Task 8 Linux/Windows-safe-path issue and was not fixed here.
- Windows producer, replay, memory, semantic-broker, and timing gates remain open. This finding
  does not claim Windows completion.

## Verification

The external evidence was checked without rerunning suites or replays:

- `sha256sum` verified every listed snapshot/JSONL hash; the startup discrepancy above is the only
  mismatch against the packet brief.
- Bounded `wc`/`jq` checks verified 8, 4, 4, 32, 1, and 1 JSONL rows respectively, plus the
  snapshot source/destination byte counts, digests, owner decision, database count, and snapshot
  WAL/SHM state.
- `git diff --check` and local docs link/path checks passed after the documentation edits.
- The exact-tree 90-test Python harness result and prior focused .NET evidence are cited from the
  preceding packet reports; they were not rerun for this docs-only packet.
