# Semantic production-readiness evaluation — 2026-07-21

**Verdict: keep semantic retrieval evaluation-only. Do not promote to a larger canary or default-on.**

The clean dev replay shows real retrieval value and two real lexical-zero rescues. It does not clear the
approved production gates: the exact measured-build canary cohort is underpowered, warm latency is indeterminate,
identifier shadow is underpowered, the sealed set is unavailable, the markdown worst-language score is zero,
and every negative query returns a result. The design requires every criterion to pass and explicitly says
underpowered is not a pass.

## Evaluated build and corpora

- Miller build: `1.13.0+a5474758072b`, commit
  `a5474758072b4a5d497c179cd605556b318b6e4e`.
- Julie corpus: commit `9d1d22c5dcca8509e412db96b6dbb5ff19d4311a`.
- Encoder: pinned `bge-small-en-v1.5-f32`, fingerprint
  `sha256:3e8b7e8a0890dc84f702db1d13c47e312501905ee9d1aafb772bdc803616d7f4`.
- Vector lane: `vec0-int8-384-cosine-v1`; corpus generation `cards-v1-chunks-v1`; fusion profile
  `fusion-v1`.
- The Miller quality corpus was a clean `git archive` of the evaluated commit with `eval/`, `.razorback/`, and
  `.claude/` excluded. The archive contained 1,863 entries and zero excluded-root entries. The Julie corpus was
  an archive of its pinned commit. Both temporary corpora were outside the indexed task root.
- The frozen visible dev set validated 82 queries and 38 distinct document references with zero missing
  references. Judgments and thresholds were unchanged.
- No sealed set is present in the repository: `eval/retrieval-eval/sets/` contains the visible dev set and
  `SEALED-SET-PROTOCOL.md` only. No sealed score was run or inferred.

After the replay, the branch advanced to `0ac49c39ccd95d050fa7f0802f51103dc80f273c` with a filter-scoping
repair and scale-fixture freshness tests. The frozen replay contains no filtered query, so those changes do not
alter these unfiltered production-route numbers. This report names `a547475` as the measured binary instead of
pretending the later test/fix commit was executed by the benchmark; filtered behavior is covered by its focused
tests, not by this score.

The lead's focused final-fix gate at `a547475` passed 151/151 tests. This evaluation rebuilt
`Miller.slnx -c Release` with zero warnings and zero errors before creating either corpus.

## Clean artifact proof and cost

All rebuilds used the real pinned `julie-extract` and converged search, content, and BGE vector artifacts before
queries ran.

| Corpus | full rebuild total | vector converge | symbol cards | chunks | host peak RSS | sidecar peak RSS | `vectors.db` |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| clean Miller quality corpus | 23.777 s | 34 s | 10,095 | 793 | 336,704 KiB | 485,744 KiB | 9,371,648 B |
| clean Julie quality corpus | 16.113 s | 27 s | 8,915 | 506 | 289,280 KiB | 490,848 KiB | 8,134,656 B |
| task worktree lifecycle proof | 22.950 s | 53 s | 10,338 | 861 | 751,120 KiB | 488,432 KiB | 9,965,568 B |

The task-root artifact converged both cursors at revision 2 with `build_state=ready` and the exact measured writer
version. Direct SQL checks found zero nested-worktree rows across every required surface:

| Surface | nested-worktree rows | total rows |
| --- | ---: | ---: |
| files | 0 | 1,105 |
| symbols | 0 | 54,399 |
| content sources | 0 | 1,105 |
| content chunks | 0 | 2,536 |
| symbol vectors | 0 | 10,338 |
| chunk vectors | 0 | 861 |

The clean Miller benchmark artifact independently reported zero nested-worktree rows across 1,027 files,
44,919 symbols, 2,430 content chunks, 10,095 symbol vectors, and 793 chunk vectors.

## Frozen retrieval replay

The live-arm runner invoked the measured `miller search --json` binary once per query at serving depth 10. The
production arm used normal routing with no forced arm, randomized canary assignment disabled, and the binary's
pinned default encoder. Metrics use the harness's cluster-unit primary policy.

| Arm | recall@10 | nDCG@10 | language macro recall | language macro nDCG | identifier recall | identifier nDCG | cluster hits | negative FPR |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lexical | 0.5122 | 0.4748 | 0.3725 | 0.3453 | 1.0000 | 0.9989 | 10/14 | 1.0000 |
| semantic | 0.6580 | 0.5824 | 0.4785 | 0.4235 | 1.0000 | 0.9763 | 14/14 | 1.0000 |
| production | 0.6267 | 0.5834 | 0.4558 | 0.4243 | 1.0000 | 0.9989 | 14/14 | 1.0000 |

Production improved cluster recall by 0.1146 and nDCG by 0.1086 over lexical while preserving the identifier
set exactly at the reported precision. It also recovered all four missed intent clusters. The worst language
was markdown for every arm at 0 recall and 0 nDCG. All six negative queries returned at least one result in
every arm. There were no missing or unknown result rows.

The frozen set's result-level empty rate was 0/82 for every arm, so it cannot demonstrate an aggregate empty-
rate reduction. Two explicit, separately recorded probes do demonstrate the new capability:

- Symbol: `atomize orthographic compounds lexemic morphemes` returned 0 lexical rows and 10 production rows.
  `filter_compound_tokens` ranked first, and a real `inspect` follow-up resolved it successfully.
- Content: `abolish the perpetually resident overseer` returned 0 lexical rows and 5 production rows. The first
  result was the Julie daemon-adapter teardown plan, and a real file `inspect` follow-up resolved it.

Those are functional acceptance events, not causal canary successes. Natural v2 canary assignment placed the
mixed, docs-like, and prose probes in control on this workspace/day, so they served zero rows and could not
receive semantic-result attribution. The probes kept their natural assignments: no workspace clone, date
override, repetition, or result-based unit selection was used to force treatment.

## Latency and canary gate

One-shot CLI wall time includes process startup and any per-process semantic session startup. Nearest-rank
percentiles across all 82 queries were:

| Arm | p50 | p95 |
| --- | ---: | ---: |
| lexical | 168.498 ms | 202.944 ms |
| semantic | 775.920 ms | 840.550 ms |
| production | 796.711 ms | 1,097.725 ms |

These cold one-shot numbers are operational evidence, not the design's authoritative warm-server latency gate.
The fixed UTC export window `2026-07-21` through `2026-07-22` was byte-identical across two exports (SHA-256
`bf3302eaeb55d4f67a7eef6f7fd414db5d2e40baf96589ec5d23b1d173b03036`). Schema v2 suppressed three sub-five-
call units and emitted zero eligible or shadow units.

The authoritative local gate partitioned the exact measured semantic identity and returned `gate_passes=false`:

- success rate: `underpowered`, 0/30 included control units and 0/30 treatment units;
- warm latency: `indeterminate`, 0/100 warm treatment rows and 3/100 control rows;
- identifier shadow: `underpowered`, 0/30 units.

## Decision

The evidence is promising enough to continue evaluation: production has adjusted dev-set quality lift, keeps
identifier quality, and converts both explicit lexical-zero cases into useful results. It is not evidence for
promotion. The following required bars are still uncleared:

1. Run the user-owned sealed set through the unchanged production arm and pass its frozen bars.
2. Accumulate at least 30 included v2 assignment units per arm under one exact semantic identity and require the
   success-rate confidence interval lower bound to be greater than zero.
3. Accumulate at least 100 warm treatment and 100 control rows and stay within the 1.20 p95 latency ratio.
4. Accumulate at least 30 identifier-shadow units and pass both frozen non-inferiority margins.
5. Obtain aggregate evidence across several operators, repositories, and language families.
6. Resolve or explicitly gate the zero markdown score and 100% negative false-positive rate without changing
   the frozen judgments after seeing results.

Until all six clear, keep `MILLER_SEMANTIC=off` as the normal safe path and semantic serving evaluation-only.
The current data does not justify stopping the semantic program outright, but it does rule out promotion now.

## Evaluation incident

One partial Julie production replay was discarded after the host volume fell from 1.5 GiB free to 103 MiB
while another checkout's derived `.miller` artifact grew to 5.2 GiB. Query 22 failed with SQLite error 10.
After removing only this task's explicitly disposable derived artifact, all four Julie databases passed
`PRAGMA quick_check`; the production arm was rerun from row 1 and completed 41/41. No row from the failed
partial run appears in the scored outputs.

Machine-readable reports, result rows, timing samples, rebuild logs, exclusion proofs, zero-result probes, and
the deterministic canary export live under
[`eval/retrieval-eval/out/production-readiness-2026-07-21/`](../../eval/retrieval-eval/out/production-readiness-2026-07-21/).
