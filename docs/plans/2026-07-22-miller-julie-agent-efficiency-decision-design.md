# Miller/Julie Agent-Efficiency Decision

**Date:** 2026-07-22
**Status:** Accepted direction (user approved 2026-07-22); implementation plan required before code
**Decision deadline:** No later than 2026-07-25
**Architecture risk:** Medium/high — agent nondeterminism, sealed evaluation, and configuration isolation can
produce a confident but false product decision if they are handled casually.

**Implementation plan:**
[Miller/Julie agent-efficiency implementation](2026-07-22-miller-julie-agent-efficiency-implementation-plan.md)

## 1. Decision

Run a bounded, agent-in-the-loop benchmark to decide whether Miller is ready to replace Julie for read-only
code discovery. The benchmark measures the outcome the user actually needs: an agent receives correct and
sufficient repository evidence with the fewest calls, tokens, irrelevant results, and seconds.

The benchmark has two product-realistic arms:

- **Miller:** the frozen Miller binary, its pinned extractor and semantic sidecar, and Miller's native MCP
  instructions and tools.
- **Julie:** the frozen Julie binary, its current Python semantic provider and CodeRankEmbed configuration,
  and Julie's native MCP instructions and tools.

Both arms use the same Codex version, model, reasoning level, task prompt, repository snapshot, task budget,
and final-answer schema. The agent may choose each product's natural workflow; the benchmark does not force
identical tool calls.

Correctness is the hard floor. Efficiency can decide the winner only on tasks both products complete. The
result replaces the month-long wait as the immediate Miller-versus-Julie product-direction decision. The
long-running semantic canary may continue as background evidence for Miller's semantic operating cost and
reliability, but it does not delay the 2026-07-25 decision.

Related evidence and protocols:

- [Existing scripted Julie/Miller search and inspect benchmark](../findings/2026-06-27-julie-miller-search-inspect-benchmark.md)
- [Existing broader foundation effectiveness matrix](../findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md)
- [Semantic maturity decision program](2026-07-22-semantic-maturity-decision-design.md)
- [Existing sealed paired task-completion protocol](../../eval/retrieval-eval/sets/SEALED-TASK-PROTOCOL.md)

## 2. Scope and boundaries

### In scope

- Read-only orientation, exact lookup, concept search, docs/config discovery, context assembly, references,
  trace, impact, and test-selection tasks.
- One concrete Codex CLI runner with a fresh ephemeral agent for every task and product arm.
- Product-native MCP tools and server instructions, isolated from machine-global plugins and MCP servers.
- Exact trajectory capture for tool calls, tool responses, timing, failures, and final structured answers.
- Twelve visible development tasks and thirty sealed decision tasks across at least five repositories and
  languages.
- Deterministic task verification, predeclared discordant-pair reruns, paired completion scoring, and an
  efficiency score over tasks both arms complete.
- Controlled semantic diagnostics that distinguish embedding-model quality from chunking, routing, fusion,
  output, and agent-guidance quality.
- At most one focused Miller repair based only on the visible development set before the sealed run.

### Out of scope

- Editing, test execution, continuous testing, or other write workflows in the first benchmark.
- A new MCP tool or any expansion of Miller's public MCP surface.
- Fleet ranking, guidance/confidence views, suppression persistence, or other Eros-owned behavior.
- Changes to Julie, `julie-semantic-sidecar`, or their active working sessions.
- A general multi-agent or multi-harness benchmark framework.
- Tuning against sealed prompts, expected facts, trajectories, or task-level outcomes.
- Publishing, releasing, pushing, or changing either product's normal defaults.
- Treating a retrieval-only script, model-only bakeoff, or canary metric as a substitute for agent completion.

## 3. Architecture quality gate

### Affected modules and caller-facing interfaces

| Area | Responsibility | Caller-facing interface |
| --- | --- | --- |
| `scripts/bench-agent-efficiency.py` | Validate a run, randomize paired arm order, start fresh agents, enforce budgets, and write immutable run artifacts. | `python3 scripts/bench-agent-efficiency.py --manifest <path> --arm miller|julie|both --out <dir>` |
| `scripts/benchlib/agent_runner.py` | Concrete Codex CLI invocation and JSONL event parsing. It is not a generic runner abstraction. | Internal benchmark module. |
| `scripts/benchlib/recording_mcp_proxy.py` | Forward one product's MCP protocol without rewriting its instructions, schemas, arguments, or results; record exact calls, responses, and timings. | Internal stdio MCP command generated into the arm-specific Codex configuration. |
| `scripts/benchmarks/agent-efficiency/` | Visible task manifest, answer schema, fake fixtures, and run documentation. | Versioned JSON/JSONL inputs and schemas. |
| `eval/retrieval-eval` | Validate normalized paired results and calculate completion and efficiency gates. | Additive `agent-score` command; existing `task-score` behavior remains unchanged. |
| `docs/findings/agent-efficiency/` | Frozen manifests, aggregate reports, reproduction metadata, and the final product verdict. | Human-readable finding plus machine-readable aggregate JSON. |

The exact command spelling may be refined in the implementation plan, but the benchmark remains one top-level
runner and one additive offline scoring entry point.

### Depth and locality

- Python owns process orchestration because the existing benchmark and MCP process helpers already live under
  `scripts/benchlib`. Product code remains a black box during benchmark calibration.
- The recording proxy is the only measurement boundary. It forwards Miller or Julie's native initialize
  result and tool traffic while recording the bytes actually delivered to the agent.
- C# owns pure aggregate scoring beside the existing retrieval and paired task-completion scorers. It does not
  start agents or product processes.
- The existing `TaskCompletionScorer` validates and reports semantic-candidate superiority. Its Wilson gate
  intentionally fails ties and therefore does not express this benchmark's approved replacement floor. Do not
  change that contract. The additive agent score may reuse its models or extracted pure pairing logic, but it
  owns the non-regression and efficiency verdicts separately.
- The stable extension seam is the normalized JSONL trajectory/result contract. Do not create an
  `IAgentRunner`, provider registry, or harness plugin system until a second real runner is required.

### Test surface

- Unit fixtures for Codex JSONL parsing, malformed events, process exit, timeout, and final-answer validation.
- A fake stdio MCP server proving the proxy preserves initialize instructions, tool schemas, arguments, result
  content, errors, and ordering while recording exact byte counts and monotonic timings.
- Budget tests for eight calls, 120 seconds, and 12,000 cumulative tool-output tokens, including a single
  response that crosses the token ceiling.
- Manifest tests for duplicate ids, missing snapshot identities, invalid task classes, leaked expected facts,
  and unbalanced development or sealed workflow classes.
- Pure scorer tests for ties, Miller wins/losses, evidence-critical losses, discordant majority results,
  both-pass selection, token/call alternatives, and the p75 wall-time guard.
- One live development smoke per arm before the 72-hour run. No sealed task is used for harness debugging.

### Rejected shortcuts

- **Make the scripted MCP benchmark primary:** rejected because it measures predetermined calls rather than
  whether an agent can discover and assemble sufficient evidence.
- **Give both arms the same forced tool sequence:** rejected because native routing, guidance, compactness, and
  recovery behavior are part of product quality.
- **Let the agent use shell search as a fallback:** rejected because it would measure Codex plus `rg`, not the
  product. Any non-arm code-access tool call invalidates the run.
- **Compare BGE-small and CodeRankEmbed through different corpora or fusion policies:** rejected because it
  confounds the model with the rest of each product.
- **Change the frozen semantic `task-score` gate:** rejected because that scorer protects a separate accepted
  protocol.
- **Build a generic runner framework now:** rejected because it adds interfaces without a second caller.
- **Wait for the August canary deadline:** rejected because the canary answers a longer-horizon operational
  question and cannot replace the immediate product comparison.

## 4. Task corpus

### Workflow classes

The visible set contains two tasks from each class; the sealed set contains five from each class.

| Class | Agent outcome being measured | Typical required evidence |
| --- | --- | --- |
| `exact_lookup` | Locate a named file, symbol, signature, or definition. | Correct path, symbol, and signature/line anchor. |
| `concept_search` | Find an implementation from natural-language intent without knowing its identifier. | Correct implementation path and the fact that proves the match. |
| `docs_config` | Find behavior described in documentation, configuration, or literals. | Correct text source and relevant bounded passage. |
| `context_assembly` | Explain an unfamiliar area using the minimum sufficient entry points. | Required components, roles, and relationships. |
| `references_trace` | Identify callers, references, or a dependency/route path. | Required endpoints and relationship evidence. |
| `impact_tests` | Identify likely blast radius and tests for a proposed read-only change scenario. | Required impacted symbols/files and justified test targets. |

Tasks come from real prior agent work, telemetry-derived misses, and the existing Miller/Julie foundation
manifest. The visible set must include the observed failure where a natural-language semantic-architecture
request produces generic architecture-quality material instead of the repository's semantic design records.
Task wording must describe the desired outcome, not name a product tool or search mode.

### Snapshot rules

- Use at least five repositories and languages represented in the existing foundation benchmark.
- Record canonical repository id, commit SHA, language, extraction/index identity, and snapshot content hash.
- Build dedicated clean benchmark copies from committed revisions. Never point either product at the user's
  active Julie, Miller main, or sidecar workspaces.
- Exclude nested worktrees, `.miller`, `.julie`, build output, benchmark output, and product-specific indexes
  from source corpora consistently.
- Prebuild and verify both product indexes before timed tasks. Index construction is reported separately and
  does not consume a task budget.
- Freeze the latest clean committed Julie revision available at benchmark preparation. Uncommitted work in the
  active Julie session is neither read nor copied.
- Pin the sidecar revision delivered by its active session only if it is available and passes the visible
  preflight before the Day 3 freeze. Otherwise use Miller's already pinned sidecar. Sidecar work cannot delay
  the decision.

### Manifest split

The committed visible manifest may contain prompts and acceptance data. The sealed manifest remains outside
the repository under the evaluation operator's control and follows the existing spend-once principles:

- implementation agents cannot inspect sealed prompts, expected facts, evidence allowlists, task ids,
  trajectories, randomized order, per-task outcomes, or arm mapping;
- the sealed controller may inspect them to run deterministic verification and automatic reruns;
- the implementation side receives only aggregate verdicts and predeclared subgroup diagnostics;
- no product change is allowed after sealed execution begins.

Each controller manifest row records at least:

- `task_id`, repository/snapshot identity, language, and workflow class;
- task prompt and whether the task is `evidence_critical`;
- required fact predicates and accepted evidence anchors;
- forbidden material claims when a stable deterministic check is possible;
- the common budgets and final-answer schema version.

All `exact_lookup`, `references_trace`, and `impact_tests` sealed rows are `evidence_critical`. The flag is
derived from workflow class rather than chosen after results are visible.

## 5. Agent and product isolation

### Agent process

Every task/arm run starts a new ephemeral Codex process with:

- the same pinned Codex CLI version, model, reasoning level, base prompt, timeout, and answer schema;
- user configuration ignored while authentication remains available;
- a generated configuration containing only the selected product's recording proxy;
- a read-only sandbox, no web-research tool, and an otherwise empty working directory; Codex's API transport
  remains available;
- no conversation, tool results, or model state from a previous task or the paired arm.

The agent prompt explicitly permits only the selected code-intelligence MCP server for repository evidence.
Codex's event stream is checked for shell commands, filesystem reads, web calls, or another MCP server. Such a
call is a product-arm failure, not a reason to silently fall back or rerun.

### Product process

The proxy starts the frozen product command with the benchmark snapshot as its explicit workspace. It forwards
the native server instructions and tool catalog so the arm measures the product experience agents receive in
practice. The proxy must not add arm-specific hints, rewrite errors, truncate normal responses, or translate
one product's concepts into the other's.

Each task gets a fresh product server. Indexes and model files are prepared before timing, but process startup,
MCP initialization, semantic runtime initialization, and all agent work count toward end-to-end wall time. A
separate scripted warm-read diagnostic may explain a loss; it cannot replace the end-to-end result.

The order of Miller and Julie is randomized per task from a recorded seed. The controller balances which arm
runs first across the corpus to reduce filesystem-cache and thermal bias.

## 6. Budgets and trajectory contract

### Common task budget

- Maximum MCP tool calls: `8`.
- Maximum end-to-end wall time: `120` seconds.
- Maximum cumulative tool-output tokens: `12,000` using a pinned tokenizer/version recorded in the run
  manifest.

The proxy sends a tool response whole. If that response crosses the token ceiling, the run is marked
`budget_exceeded` after recording the complete response and no further call is allowed. Output is never
silently truncated to manufacture a pass.

A product crash, malformed response, tool error, or product timeout is part of the arm outcome. A controller,
proxy, or Codex infrastructure failure voids the pair; both arms rerun after the harness fault is fixed and the
void reason is preserved.

### Recorded run identity

Every raw run records:

- schema version, run id, task id, blinded arm id, repetition, randomized order, and timestamps;
- Codex version/model/reasoning identity and generated-config hash;
- product version, binary SHA-256, commit SHA, MCP instruction/tool-schema hash, and relevant environment
  allowlist;
- repository snapshot identity and index/vector/model generation identities;
- every MCP call argument, response, error, exact UTF-8 byte count, token count, and monotonic duration;
- total calls, tool-output bytes/tokens, product errors, duplicate calls, end-to-end wall time, exit state, and
  final structured answer;
- total model token usage when Codex reports it, as a diagnostic rather than the primary efficiency metric.

Raw sealed trajectories remain private to the controller. Aggregate artifacts contain no task text, expected
facts, repository secrets, or per-task rows.

### Final-answer schema

The agent returns a small structured object containing:

- `status`: `answered` or `insufficient_evidence`;
- `answer`: the direct response to the task;
- `evidence`: bounded entries with path, optional symbol/line, and the claim each entry supports.

The schema makes verification deterministic without telling the agent which facts or files are expected.

## 7. Correctness scoring

### Per-run verification

A run completes only when:

1. every required fact predicate is satisfied;
2. the cited paths/symbols/lines exist in the frozen snapshot and match accepted evidence anchors;
3. each required claim has sufficient cited evidence;
4. no predeclared material false claim is present; and
5. the run stayed within the tool, token, time, and allowed-tool constraints.

Verification is deterministic for normal results. A human adjudicator sees only disputes that the verifier
marks ambiguous; the adjudicator applies the frozen acceptance checks and cannot change task requirements or
arm budgets after seeing an answer.

### Discordant stabilization

If exactly one arm completes a task on the initial run, the sealed controller runs two additional repetitions
for both arms under the same frozen conditions. Majority-of-three becomes the stabilized completion outcome.
This rerun rule is automatic and predeclared; it is not available for both-pass or both-fail pairs.

For a stabilized both-pass task, per-arm efficiency uses the median of that arm's passing repetitions. A task
that remains a one-arm completion is excluded from efficiency comparison because correctness already decides
it.

### Correctness floor

Miller passes the replacement floor only when:

- its stabilized sealed completion count is at least Julie's; and
- there is no `evidence_critical` task where Julie stabilizes to pass and Miller stabilizes to fail.

Any material false claim makes that run incomplete even if it contains all expected facts. Failing this floor
means Miller is not ready to replace Julie regardless of speed or compactness.

The scorer also emits existing paired cells (`both_completed`, `miller_only`, `julie_only`, and
`neither_completed`) and per-class aggregates. They explain the result but do not weaken the approved floor.

## 8. Efficiency scoring

Efficiency is evaluated only over stabilized tasks both products complete. Metrics remain paired by task; a
large win on an easy task cannot compensate for a correctness loss elsewhere.

Priority order:

1. cumulative tool-output tokens delivered to the agent;
2. MCP tool-call count;
3. end-to-end wall time.

Miller wins the efficiency gate through either predeclared route:

- median paired tool-output tokens are at least 20% lower than Julie's; or
- median paired tool-call count is at least one call lower and Miller's median tool-output tokens are no
  higher than Julie's.

Under either route, Miller's p75 end-to-end wall time may not exceed Julie's by more than 20%. If there are no
both-pass tasks, efficiency is not measurable and Miller does not pass.

Report-only diagnostics include exact bytes, total model tokens, per-class medians, product/tool errors,
duplicate calls, final-answer size, uncited tool-output ratio, and evidence density based on accepted evidence
anchors. No weighted composite can offset a correctness loss.

## 9. Semantic-engine diagnostics

The product benchmark decides which complete system helps an agent more. It cannot by itself determine whether
a difference came from BGE-small, CodeRankEmbed, corpus construction, query routing, fusion, or guidance.

Every visible semantic/concept failure therefore gets two diagnostic views:

### Product-realistic replay

- Miller lexical-only.
- Miller production routing with the pinned BGE-small sidecar.
- Julie production routing with its Python CodeRankEmbed provider.

This identifies the behavior agents actually saw, including routing and fusion. These rows remain diagnostic
and cannot alter the primary arm result.

### Identical-corpus model bakeoff

BGE-small and CodeRankEmbed receive the same frozen texts, text boundaries, normalization, query set, vector
normalization, candidate count, and nearest-neighbor scoring. No lexical fusion or product-specific reranking
is applied in the model-only comparison. Record retrieval quality, cold/warm embedding latency, indexing
throughput, resident memory, failures, and actual hardware backend.

Interpretation:

- CodeRankEmbed wins the identical-corpus test while Julie wins product tasks: model choice is a credible part
  of Miller's gap.
- The models are comparable but Julie wins product tasks: investigate chunking, routing, fusion, compact
  output, and agent guidance before changing models.
- BGE-small wins or ties model-only while Miller loses product tasks: replacing the model is unlikely to fix
  the product problem.
- Miller wins product tasks: the Rust sidecar still needs its separate MPS, DirectML or equivalent on Windows,
  CUDA/ROCm on Linux, Intel GPU, packaging, multi-session, memory, and reliability promotion gates before
  replacing Julie's Python provider.

Rust versus Python is therefore a packaging/runtime decision, not a quality conclusion. Neither implementation
is promoted from language choice or one product-realistic score alone. Rust is not an architectural
requirement: the consumer-neutral sidecar protocol may admit a native .NET backend later if it proves the same
quality, deterministic packaging, and hardware-acceleration gates. Do not rewrite the sidecar for language
symmetry alone.

## 10. Seventy-two-hour execution

### Day 1 — harness and visible baseline

- Freeze repository snapshots, product binaries, model identities, Codex identity, prompts, schemas, budgets,
  and the randomization seed.
- Implement and test the runner, recording proxy, normalized result contract, and additive scorer.
- Build the twelve visible tasks and run one live smoke per arm.
- Run the full visible paired baseline and publish failure classes, not product changes.

### Day 2 — one focused repair

- Classify every visible Miller loss as retrieval, routing, ambiguity, context assembly, output size, guidance,
  latency/runtime, or infrastructure.
- Run scripted and semantic diagnostics only where they distinguish plausible causes.
- Select the single highest-impact Miller failure class by lost task count, then expected token/call savings.
- Make one focused Miller repair with fast tests, relevant scale tests, and a visible-set rerun.
- Build that repair as a separate benchmark candidate. Do not replace or mutate the immutable binary enrolled
  in the background semantic canary.
- Do not modify Julie or the sidecar workspace. If the sidecar session delivers a candidate, consume only a
  clean pinned build through the preflight rule.

### Day 3 — freeze, sealed run, verdict

- Freeze the final Miller candidate and rerun all harness preflights.
- Execute the thirty sealed pairs in randomized order.
- Let the controller perform only the predeclared discordant repetitions and deterministic adjudication.
- Produce aggregate correctness, efficiency, class diagnostics, identities, and a content-addressed evidence
  manifest with recorded SHA-256 digests.
- Record the product-direction verdict no later than 2026-07-25. No canary wait extends this deadline.

## 11. Decision outcomes

### Miller passes correctness and efficiency

- Make Miller the primary local code-intelligence product.
- Keep Julie in maintenance mode and plan its retirement separately; do not delete it during this benchmark.
- Continue the semantic canary only as background evidence for keeping, changing, or removing Miller's optional
  semantic runtime.
- Turn remaining visible diagnostics into prioritized Miller improvements without reopening the product choice
  after every small fluctuation.

### Miller fails correctness or efficiency

- Julie remains the primary product immediately.
- Stop broad Miller feature expansion. Preserve Miller as a deterministic fallback and contract consumer while
  the losing behavior has no credible repair.
- Use only visible and aggregate diagnostics to decide whether one bounded Miller repair is warranted later;
  do not inspect or tune against spent sealed tasks.

### Both products expose the same material limitation

- Do not start a new project merely because both scores are disappointing.
- First prove that the shared failures come from an architectural constraint neither product can repair
  locally, rather than task wording, extraction coverage, model choice, routing, or guidance.
- Only then write a separate design that combines the proven lessons from Julie, Miller, and Eros.

## 12. Evidence and completion criteria

The design is implemented when all of the following exist and pass:

- the visible 12-task manifest and tests;
- the isolated Codex runner and transparent recording proxy;
- normalized raw-result and aggregate schemas;
- the additive agent-efficiency scorer without changes to existing `task-score` output;
- fake-process unit coverage and one live smoke per product;
- frozen build/snapshot/config/model identity manifests;
- a user-controlled sealed 30-task manifest that the implementation agent cannot inspect;
- the Day 1 visible baseline, Day 2 focused repair evidence, and Day 3 sealed aggregate;
- an explicit Miller-primary, Julie-primary, or shared-architecture-limit verdict dated no later than
  2026-07-25.

The benchmark is incomplete if it reports only retrieval recall, direct tool timing, model quality, or canary
telemetry without the paired agent completion and efficiency verdict.
