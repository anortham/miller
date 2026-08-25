# Miller v1.22.1 vs bare agent — the July calibration, rerun

Date: 2026-08-25. This repeats the 2026-07-29 decisive baseline
([`2026-07-29-miller-vs-bare-agent-calibration.md`](2026-07-29-miller-vs-bare-agent-calibration.md)) on the
published v1.22.1 release archive, eight Miller minor versions later. Method held fixed: same 15 dev tasks,
same 5 pinned snapshot repos, same model, same seed, same two budgets, same scorer. Four paired visible
calibration runs, all completed and scored, **zero unresolved voids in every run**.

Short answer: Run A replicates July almost exactly. Run B does not — the semantic arm lost two tasks the
lexical arm solved at the frozen budget, which July's runs never showed. Section "Run B" reports that
honestly and states what it does and does not prove.

## Method

- Harness: the frozen takeover-evaluation-v1 visible-calibration machinery
  (`scripts/bench-agent-efficiency.py`, Codex `gpt-5.6-sol` @ medium, `codex-cli 0.145.0`, seed 731,
  15 dev tasks over the 5 pinned snapshot repos of `dev-snapshots.json`, content-hash verified against July).
- Miller arm: the **published v1.22.1 release archive** (`1.22.1+c920c7fdb167`, sha-verified) — the binary a
  real user installs — with default configuration (semantic on) under an isolated bench HOME.
- Bare arm: a minimal stdio MCP adapter exposing `grep`, `read_file`, `list_files` rooted at the snapshot.
  Identical proxy, budgets, frozen prompt, and scoring as the Miller arm; the adapter cannot read `.miller/`.
- Budgets: frozen (8 calls / 12,000 tool-output tokens / 120 s) AND raised (16 / 24,000 / 240 s).
- Runs: A = bare vs Miller-on; B = Miller `MILLER_SEMANTIC=off` vs Miller-on (identical `command_sha256`,
  different `environment_sha256`). Evidence exports for all four runs are committed under
  [`agent-efficiency/2026-08-25-bare-agent-v1.22.1/`](agent-efficiency/2026-08-25-bare-agent-v1.22.1/); run
  identities `e70596b9…` (A-frozen), `f0527c9a…` (A-raised), `6b90d8dd…` (B-frozen), `bf3e1918…` (B-raised).
  All four share one selection identity (`selection_sha256 ed6f8998…`).

### Four method deviations from July — disclose these wherever this method is described

1. **The bare adapter was rewritten.** July's `bare-agent-adapter 1.0.0` source was lost; these runs use
   `bare-agent-adapter 2.0.0`, rebuilt from the recorded spec. Bare-arm run identities therefore **cannot**
   match July's by construction, and Run A is not a like-for-like re-measurement of the bare arm. The
   evidence that 2.0.0 reproduces 1.0.0 behavior is circumstantial but strong: the bare arm's correct set is
   the same five tasks (`dev-005`, `dev-006`, `dev-012`, `dev-013`, `dev-014`) in July A-frozen and in both
   2026-08-25 A runs, and the `ordered_evidence_matches` arrays on those five tasks are byte-identical across
   all three runs.
2. **Harness hardening.** The harness gained `--max-void-attempts` (these runs used 8) and now seeds
   `models_cache.json` into each ephemeral codex home, because an ephemeral home made every spawn fetch the
   remote model list and lose the race against codex's 30 s MCP startup watchdog (commit `22e46127`).
3. **A proxy defect was fixed before these runs.** The recording proxy's controller pump mixed `select()` on
   the raw fd with a `BufferedReader`, so a coalesced burst left the second request stranded in userspace
   until the watchdog killed the proxy — voiding the pair. Fixed in commit `f02ac370`. July's runs predate
   the fix; their void count and flake environment are unknown, and July's exports do not record voids.
4. **Two snapshots gained a `.julieignore`.** `tree-sitter-razor` and `tree-sitter-c-sharp` name
   `src/parser.c` to work around a store-wedge defect (see product findings). This changes neither
   `content_sha256` nor what was indexed — a 32 MB generated `parser.c` was never in the manifest.

The scorer is unchanged. Read these definitions before quoting any number: **correct** = stabilized outcome
equals the task's `expected_outcome` AND zero wrong actions; **conditional** relevance metrics score a
repetition 0 unless that repetition was correct with zero wrong actions, take the per-task median, then the
mean over the 13 anchored tasks (`dev-014` expects `empty` and `dev-015` expects `refusal`; neither carries
anchors); **`failure_counts` are row counts** summed over repetitions, not task counts; **efficiency medians
cover the both-correct tasks only**, so the subset differs per run and the medians are not comparable
across runs.

## Headline: Run A replicates July

| Run A (bare vs Miller-on) | bare 8-call | Miller 8-call | bare 16-call | Miller 16-call |
|---|---:|---:|---:|---:|
| correct tasks /15 | 5 | **11** | 5 | **12** |
| wrong-action rate | 26.67% | 13.33% | 26.67% | **0%** |
| conditional recall@6 | 0.2821 | **0.5385** | 0.2821 | **0.6538** |
| conditional MRR | 0.3077 | **0.6923** | 0.3077 | **0.7308** |
| hard errors (tasks) | 4 | 1 | 2 | 0 |
| `budget_exceeded` rows | 6 | 3 | 4 | 0 |
| critical losses | — | 0 | — | 0 |

July's Run A read 5 vs 11 frozen and 4 vs 10 raised. Miller's frozen count is unchanged at 11; its raised
count rose from 10 to **12**. Miller's raised-budget wrong-action rate fell from 20.0% to **0%**.

- **Strict dominance did NOT hold this time.** July reported `baseline_only = 0` in every run. Here
  **A-frozen has `baseline_only = 1`**: `dev-005` (razorback, JSON, `docs_config`, capability `patterns`,
  2 anchors, `evidence_critical: false` — which is why `critical_loss_count` stays 0). The bare agent
  answered it in 3 calls on all three repetitions; Miller hit `budget_exceeded` on all three, at exactly
  **8 of 8 calls** while spending only 7,448 / 9,062 / 7,747 of the 12,000-token allowance. Miller failed
  the **call** budget, not the token budget — a search-strategy cost, not an output-volume cost. In July's
  A-frozen Miller solved `dev-005` at 8 calls and 8,097 tokens: the same cap, one call short of failing.
  `dev-005` is budget-bound for Miller at 8 calls and lands on either side of the line depending on the run;
  Miller solves it at the raised budget in both A-raised (2 of 3 repetitions) and B-raised (both arms).
  A-raised has `baseline_only = 0`, so Miller does dominate the bare agent strictly once the budget is
  raised.
- **"More budget made the bare agent worse" is not reproduced.** With the 2.0.0 adapter the bare arm is flat
  at 5/15 at both budgets, and its raised-budget wrong-action rate is 26.7% against July's 40.0%. This is an
  adapter change, not a Miller change. The budget objection is still removed — doubling the budget does not
  close a 7-task gap — but it is removed by flatness now, not by degradation.
- **Miller still loses the efficiency gate, on all three routes, in both A runs.** Frozen: 6 vs 1.5 median
  calls, 4,468 vs 1,062 median tool-output tokens, p75 43,147 ms vs 27,726 ms. Raised: 8 vs 3 calls,
  6,430 vs 811 tokens, p75 58,804 ms vs 28,537 ms. The bare agent is cheap largely because it fails cheaply.
  Every run in this campaign scores `decision_verdict: not_decisional` and `efficiency: fail`, the same as
  July.

## Decomposition: where the advantage comes from

Five of the 15 tasks require product-issued exact symbol identity (`dev-001/002/009/010/015` — refs,
call-path, rename-safety shapes). **The bare agent solved zero of these five in all four bare runs across
both months** — a structural ceiling of 10/15, confirmed twice. On the product-neutral 10:

| product-neutral subset /10 | bare | Miller-off | Miller-on |
|---|---:|---:|---:|
| frozen budget | 5 | 7 | 7 (A run) / 6 (B run) |
| raised budget | 5 | 6 | 7 (A run) / 7 (B run) |

Two cells are given per Miller-on row because the same configuration was measured twice. July's published
table gave one cell per budget; at the frozen budget both July runs agreed, but its raised-budget `7` came
from the B run while its A run scored 5. **Quote a run name with any single-number version of this table.**

- Of Miller-on's frozen-budget 11 correct tasks, **4 come from the exact-identity class the bare agent
  structurally cannot reach** and 7 from the shared shapes, where the bare agent gets 5. At raised budget it
  is 5 and 7 against 0 and 5. The exact-identity class is still the largest single source of the gap
  (4–5 tasks of a 6–7 task lead); the neutral-subset lead is +2 at both budgets.
- **Unconditional anchor recall** (evidence surfaced regardless of answer correctness, recomputed from
  `ordered_evidence_matches`): frozen bare **0.7244** vs Miller **0.7692**; raised bare **0.6474** vs Miller
  **0.7692**. The rewritten adapter *finds* materially more evidence than 1.0.0 did (July frozen bare
  0.6090), closing that gap from 17.3 points to 4.5 — **yet correct tasks stayed 5 vs 11.** This strengthens
  July's core interpretation rather than weakening it: the bare agent's deficit is not finding the evidence,
  it is exact identity plus acting correctly on what it found. (Method note: this reproduces July's stated
  "bare 0.59–0.66 vs Miller 0.73–0.81" inside its ranges but not to the digit. Treat July's endpoints as
  that doc's rounding and quote these values when precision matters.)

## Run B: the semantic arm inverted at the frozen budget

| Run B (off vs on) | off 8-call | on 8-call | off 16-call | on 16-call |
|---|---:|---:|---:|---:|
| correct tasks /15 | **12** | 10 | 11 | **12** |
| wrong-action rate | 0% | 0% | 13.33% | 13.33% |
| conditional recall@6 | **0.6410** | 0.5256 | 0.6026 | **0.6410** |
| conditional MRR | **0.7308** | 0.6154 | 0.6923 | **0.7692** |
| `baseline_only` | **2** | | 0 | |
| critical losses | | **1** | | 0 |

**July's claim "semantic retrieval adds +1..2 correct tasks and never lost a task lexical Miller solved" did
not replicate at the frozen budget.** B-frozen is the only Run B in either month with `baseline_only > 0`
and the only one with a nonzero `critical_loss_count`. It fails the correctness and relevance gates as well
as efficiency. That is real and is not smoothed away here. The raised budget still shows the July shape
(11 vs 12, `baseline_only = 0`).

The two lost tasks do not share a failure mechanism:

- **`dev-002`** (eros, C#, `exact_lookup`, capability `exact_symbol_lookup`, 1 anchor,
  `evidence_critical: true` — this task **is** the whole of `critical_loss_count: 1`). The semantic arm did
  **not** burn budget: 4, 4 and 1 calls against a cap of 8, at most 6,163 of 12,000 tokens. It answered
  wrongly with budget in hand twice, then answered correctly in a single call on the third repetition — the
  same one-call route the lexical arm took on its own third repetition. The failure mode is answer
  selection, not exhaustion. The semantic arm solved `dev-002` in every other run this month and in every
  July run, so a permanent semantic regression on it is not supported.
- **`dev-003`** (eros, C#, `concept_search`, capability `discovery`, 2 anchors,
  `evidence_critical: false`). Here the semantic arm **did** burn budget: repetition 1 crossed the frozen cap
  at **12,074 tool-output tokens** on 7 of 8 calls, repetition 2 spent 9,021 tokens and answered wrongly,
  repetition 3 succeeded at 6,878. Across three repetitions the semantic arm spent 27,973 tokens against the
  lexical arm's 18,723 — **1.49x the output volume for a worse result.** The lexical arm was not clean
  either: its own repetition 3 exhausted the call budget. `dev-003` sat in the `neither` quadrant of all four
  July runs and no Miller-on arm has solved it in any run in either month, so the anomaly here is the lexical
  arm winning it once (2 of 3), not the semantic arm missing it.

**What the inversion proves, and what it does not.** The same candidate configuration
(`miller-1.22.1-semantic-on`) was measured four times this month and scored **11, 12, 10, 12** — the same
10–12 band as July's 11, 10, 12, 12. The decisive control is that **A-frozen and B-frozen run the identical
configuration at the identical budget** and returned 11 and 10, with **five tasks disagreeing** (A-frozen
only `{002, 006, 011}`, B-frozen only `{001, 004}`). At the raised budget the same control gives 12 and 12
with two tasks disagreeing; July's frozen control gave 11 and 12 with three disagreeing. The observed
inversion is −2 tasks; the same-configuration control already moves 1 in count and 5 in task identity. The
inversion sits inside that spread. Also note `dev-005` and `dev-006` failed with `budget_exceeded` on
**both** B-frozen arms, so the frozen budget binds this pair generally, not only the retrieval arm.

Honest verdict: **the design cannot answer the question.** One repetition per configuration, with
disagreement reruns firing only on an initial disagreement, supports no significance claim in either
direction. Do not report "semantics hurts" and do not report "noise, ignore it". Report the inversion, report
the control beside it, and note that a multi-repetition run of the frozen-budget semantic-on arm is the
experiment that would settle it.

What survives from July's Run B reading: **the lexical core carries the product** (11–12 of 15 against the
bare agent's 5), and the semantic arm's delta at these budgets is inside single-run noise in both directions.

## Product findings hit during preparation

The campaign was a dogfood run as well as a benchmark. Everything below is fixed on `main` or recorded as
backlog; none of it changed the measured binary, which is the published v1.22.1 archive.

1. **Vector-converge defects — fixed.** A whole-repo delta stamped a vector full rebuild and re-embedded the
   whole corpus (`9e84edf2`); a shadow promote whose artifact would not reopen stranded the chunk cursor with
   no retry (`da3a4f0f`); the converge drain and its tail GC ran in reader processes, so a reader could
   delete a generation another process held open (`c0d6089e`, gated on `IndexerService.IsLeader`). This is
   the same area as July's finding 1 (static workspaces never converge chunk vectors).
2. **Family-store coordinator wedge — five fixes, M1–M5, on `main`.** A 32 MB generated `src/parser.c` wedged
   the coordinator queue: discovery treated any tree file absent from the manifest as an add, so a file
   julie-extract refuses was re-submitted every tick, forever. `938bdec3` mirrors julie's real limits
   (`ExtractSourceLimits`, `WatchPathFilter.IsDiscoverableSource`); `e20b991f` keeps the request journal on a
   retryable failure so retries reuse one request id instead of minting poison rows; `fd683464` adds
   `StoreRefusalLedger` negative memory keyed on content hash; `7d990d1e` surfaces the queue in
   `workspace status`/`health` with a `store_queue_wedged` freshness reason; `02dafed6` names the blocked
   queue instead of advising a `workspace refresh` that submits into it. The `.julieignore` snapshot
   workaround (method deviation 4) was the interim mitigation.
3. **Five julie-extract follow-ups recorded**, `julie-extractors/TODO.md` items 19–23: `store update`
   bypasses scan's discovery gates; a backlog quantum overrun overwrites the caller's committed state;
   unschedulable requests requeue forever with no attempt counter; nobody reaps dead-requester queue rows
   (`store maintain` skips `requests`); publish discovery limits in `languages --json`.
4. **The proxy pump bug was OURS, not codex and not the network** (`f02ac370`). Both benchmark batch aborts
   were blamed on flaky infrastructure until the pump was read: `select()` on the raw fd plus a
   `BufferedReader` loses a coalesced request. The lesson is worth carrying — a harness that voids pairs will
   attribute its own defect to the environment unless someone reads it.
5. **CT backlog from the same week's field report** (`12bacbe3`, Miller `TODO.md`): CT's self-executing
   assembly launch fails opaquely on xUnit v2 projects (classify the runner generation at enable/inventory
   time and say so plainly); the dashboard shows no live test status during a CT session (add a Tests section
   fed read-only from `tests status --json` facts, create-nothing).

## Caveats

July's caveats all still apply: single model (`gpt-5.6-sol` @ medium), 15 visible tasks the author wrote,
single repetition plus disagreement reruns, five snapshot repos by the same author. Two more apply to this
rerun:

- **Comparability of Run A.** The bare adapter is a rewrite (2.0.0). Year-over-year bare-arm numbers compare
  two implementations of one spec, not two runs of one implementation. The byte-identical evidence arrays on
  the five tasks the bare agent solves are the strongest available check, and they are a check, not a proof.
- **Run B is not powered to detect a semantic regression.** One repetition per configuration and a
  same-configuration run-to-run spread of 1 count / 5 tasks mean the B-frozen inversion can be neither
  claimed as a regression nor dismissed as noise. A multi-repetition frozen-budget run of the semantic-on arm
  is the missing experiment.
