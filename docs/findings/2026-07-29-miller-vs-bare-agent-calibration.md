# Miller vs bare agent — the first baseline that matters

Date: 2026-07-29. This is the decisive experiment named in the 2026-07-28 strategy
(`docs/findings/2026-07-28-sealed-gate-disposition.md`): every prior benchmark compared Miller to Julie;
this one measures what Miller adds over an agent with only generic file tools. Four paired visible
calibration runs, all completed and scored.

## Method

- Harness: the frozen takeover-evaluation-v1 visible-calibration machinery
  (`scripts/bench-agent-efficiency.py`, Codex `gpt-5.6-sol` @ medium, `codex-cli 0.145.0`, seed 731,
  15 dev tasks over the 5 pinned snapshot repos of `dev-snapshots.json`).
- Miller arm: the **published v1.14.0 release archive** (`1.14.0+2b69c7b334f2`, sha-verified) — the binary a
  real user installs — with default configuration (semantic on) under an isolated bench HOME.
- Bare arm: a minimal stdio MCP adapter exposing `grep`, `read_file`, `list_files` rooted at the snapshot
  (`~/bench/adapters/bare_mcp_adapter.py`, commit recorded in each run's identity manifest). Identical
  proxy, budgets, frozen prompt, and scoring as the Miller arm; the adapter cannot read `.miller/`.
- Budgets: frozen (8 calls / 12k tool-output tokens / 120s) AND raised (16 / 24k / 240s) — the raised pair
  removes the "you starved the baseline" objection.
- Runs: A = bare vs Miller-on; B = Miller `MILLER_SEMANTIC=off` vs Miller-on. Evidence exports for all four
  runs are committed under
  [`agent-efficiency/2026-07-29-bare-agent/`](agent-efficiency/2026-07-29-bare-agent/); run identities
  `c880c196…` (A-frozen), `a06988d7…` (A-raised), `f87380da…` (B-frozen), `3ae25ec3…` (B-raised).

## Headline: Miller more than doubles agent task correctness, and budget is not the reason

| Run A (bare vs Miller) | bare 8-call | Miller 8-call | bare 16-call | Miller 16-call |
|---|---:|---:|---:|---:|
| correct tasks /15 | 5 | **11** | 4 | **10** |
| wrong-action rate | 26.7% | **0%** | 40.0% | 20.0% |
| conditional recall@6 | 28.2% | **67.9%** | 23.1% | 55.1% |
| MRR | 0.31 | **0.72** | 0.23 | 0.62 |
| hard errors /15 | 4 | 1 | 4 | 0 |
| budget-exceeded failures | 12 | 2 | 7 | 0 |

- **Strict dominance.** In every run, `baseline_only = 0`: no task the bare agent solved that Miller
  missed, while Miller solved 6 tasks the bare agent could not.
- **Doubling the bare agent's budget made it worse, not better** (5 → 4 correct; wrong-action rate
  26.7% → 40%). The deficit is capability, not starvation. More calls gave the bare agent more rope.
- The one gate Miller fails is **efficiency**: on both-correct tasks it spends more calls/tokens/time than
  the bare agent (median 3 vs 2 calls, 3.4k vs 2.6k tokens, p75 59s vs 34s at frozen budget). The bare
  agent is cheap largely because it fails cheaply: 12 of its 15 frozen-budget failures were
  budget-exhaustion.

## Decomposition: where the advantage comes from

Five of the 15 tasks require product-issued exact symbol identity (`dev-001/002/009/010/015` — refs,
call-path, rename-safety task shapes). The bare agent solved **zero** of these in any run — a structural
ceiling of 10/15, confirmed empirically. On the product-neutral 10:

| product-neutral subset /10 | bare | Miller-off | Miller-on |
|---|---:|---:|---:|
| frozen budget | 5 | 6 | 7 |
| raised budget | 4 | 5 | 7 |

- Roughly **half of Miller's win is the exact-identity capability class** (refs/paths/rename proof) that
  grep structurally cannot provide; the other half is better retrieval and disciplined action on the
  shared task shapes.
- **Unconditional anchor recall** (evidence found regardless of answer correctness) is closer between
  arms: bare 0.59–0.66 vs Miller 0.73–0.81. The bare agent often *finds* relevant evidence; it then
  exceeds budget, mis-acts, or attributes wrongly (0% wrong actions for Miller at frozen budget vs 26.7%
  bare). Miller's edge is as much *identity precision and act-on-evidence discipline* as raw retrieval.

## Run B: what the semantic arm adds over lexical Miller

| Run B (off vs on) | off 8-call | on 8-call | off 16-call | on 16-call |
|---|---:|---:|---:|---:|
| correct tasks /15 | 11 | **12** | 10 | **12** |
| wrong-action rate | 6.7% | 13.3% | 13.3% | **6.7%** |
| conditional recall@6 | 56.4% | 64.1% | 52.6% | 65.4% |

- The **lexical core carries most of the product** (11/15 vs bare 5/15). Semantic retrieval adds +1..2
  correct tasks and +8..13 points recall, with `baseline_only = 0` (semantics never lost a task lexical
  Miller solved). Wrong-action deltas are within one-task noise in both directions.

## Product findings hit during preparation (fix candidates)

1. **Static workspaces never converge chunk vectors.** Every server start stamps a full rebuild
   (`VectorConvergeService.StampTarget`), the symbol lane shadow-rebuilds — re-embedding every card on
   every restart — and the chunk lane holds for that wake (`VectorConvergePlanner.ChunkHold`); an idle
   workspace never wakes again, so the hold is permanent until a file changes. Bench workaround: an
   append-and-revert file nudge. A post-shadow-rebuild wake (or excluding the initial build from the
   full-rebuild signal) would fix both the hold and the restart re-embed cost.
2. **`.julieignore` seeding dirties externally-verified checkouts.** Miller ≥1.14 seeds a top-level
   `.julieignore` into any workspace lacking one; the benchmark's prepared-snapshot verifier had to learn
   to tolerate it (`benchlib/agent_contract.py`). Anything else that hashes or cleanliness-checks a
   workspace after Miller touches it will hit the same surprise.
3. Harness: proxy budgets and run-result schema bounds were frozen-budget-coupled; now parameterized
   (`--max-calls/--max-output-tokens/--timeout-seconds`, schema sanity ceilings at 64).

## Decision-rule application

The strategy brief's rule: a decisive delta makes this the marketing story. The delta is decisive —
**2.2× correct tasks, 0% vs 27% wrong actions, strict dominance, and immunity to the budget objection**.
The honest framing for the write-up: Miller's value is exact identity plus act-on-evidence discipline
under bounded context, not "grep can't find things." The efficiency gate result belongs in the write-up
too: Miller costs more per task than a failing-fast baseline; it buys correctness, not cheapness.

Caveats: single model (`gpt-5.6-sol`), 15 visible tasks the author wrote, single repetition plus
disagreement reruns, five snapshot repos by the same author. The next external step (distribution month,
strategy Step 3) should publish method + exports so others can rerun it.
