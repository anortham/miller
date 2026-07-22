# Miller/Julie Agent-Efficiency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Build and calibrate the isolated agent-in-the-loop benchmark that produces a correctness-first, efficiency-aware Miller-versus-Julie decision no later than 2026-07-25.

**Architecture:** A Python controller launches one fresh Codex process per task/arm through a transparent stdio MCP recording proxy, verifies the structured answer against a frozen task manifest, and emits privacy-safe arm-result JSONL. An additive C# `agent-score` command validates rerun shape, stabilizes discordant pairs, and emits aggregate correctness and efficiency gates without changing the existing semantic `task-score` contract.

**Tech Stack:** Python 3.14 standard library/unittest, `tiktoken==0.13.0` with `o200k_base`, Codex CLI 0.145.0, MCP JSON-RPC over stdio, .NET 10, C#, `System.Text.Json`, xUnit.

**Architecture Quality:** Medium/high evidence-contract risk. Contracts are frozen before process work fans out; the proxy and runner are additive beside the existing 14-caller `McpProcess`; the evaluator gains separate agent models/scorer/report types; raw sealed trajectories remain outside the repository; and an unknown Day 2 product repair requires its own evidence-grounded plan rather than an invented edit.

## Global Constraints

- The benchmark measures whether an agent obtains correct and sufficient repository evidence with fewer calls, tokens, irrelevant results, and seconds.
- Correctness is the hard floor. Efficiency is scored only for stabilized tasks both products complete.
- Scope is read-only orientation, exact lookup, concept search, docs/config discovery, context assembly, references/trace, and impact/test selection. Editing and test execution are excluded.
- The visible set has exactly 12 tasks: two each for `exact_lookup`, `concept_search`, `docs_config`, `context_assembly`, `references_trace`, and `impact_tests`.
- The sealed set has exactly 30 tasks: five from each workflow class across at least five repositories and languages.
- Every `exact_lookup`, `references_trace`, and `impact_tests` row is `evidence_critical`; the flag is derived from workflow class before execution.
- Use dedicated clean committed repository snapshots. Never index, read through, or mutate the active Julie or `julie-semantic-sidecar` checkout.
- Prebuild product indexes before timed runs. Product startup, MCP initialization, semantic runtime initialization, and all agent work count toward wall time.
- Each task/arm starts a fresh ephemeral Codex and product process with the same task, snapshot, model, reasoning level, answer schema, and budgets.
- Freeze Codex as `codex-cli 0.145.0`, model `gpt-5.6-sol`, reasoning `medium`, unless the user changes the choice before the visible baseline. Record the exact resolved values in every run manifest.
- Start Codex with a private temporary child `CODEX_HOME` containing only the minimum authentication material needed for the run. Do not load machine-global `AGENTS.md`, skills, plugins, hooks, history, or configuration, and never copy authentication material into benchmark artifacts.
- Codex receives only one configured MCP server: the selected arm's recording proxy. The proxy forwards the product's native server instructions and tool schemas without rewriting content.
- Any shell command, direct filesystem read, web search, non-arm MCP call, or other repository-access tool makes the run `disallowed_tool`; it is not silently retried as a harness fault.
- Common budget: at most 8 MCP tool calls, 120 seconds end-to-end, and 12,000 cumulative tool-output tokens.
- Pin token measurement to `tiktoken==0.13.0` and `o200k_base`; record exact UTF-8 bytes too. Missing tokenizer support fails preflight; no bytes/4 fallback may decide the gate.
- Forward each tool response whole. If it crosses the token ceiling, record the whole response, mark `budget_exceeded`, and allow no later tool call.
- Randomize Miller/Julie order per task from a recorded seed and balance which arm runs first across the corpus.
- A product crash, malformed response, tool error, or product timeout belongs to the arm outcome. A controller/proxy/Codex infrastructure fault voids the pair and requires both arms to rerun with the void reason retained.
- A task passes only when every required fact is present, every required claim has accepted snapshot evidence, no predeclared material false claim appears, and all tool/time/token constraints hold.
- Initial one-arm completion triggers exactly two additional repetitions for both arms; majority-of-three is the stabilized completion result. Both-pass and both-fail initial pairs do not rerun.
- Miller passes correctness only when its stabilized completion count is at least Julie's and no evidence-critical task is a stabilized Julie-pass/Miller-fail.
- Miller wins efficiency when median paired tool-output tokens are at least 20% lower, or median paired tool calls are at least one lower with no higher median tokens. Either route also requires p75 wall time no more than 20% above Julie.
- Median uses the midpoint of the two central values for even populations. p75 uses nearest rank: `ceil(0.75 * n)` in ascending order.
- Report exact bytes, total model tokens, product errors, duplicate calls, final-answer size, and uncited-output ratio as diagnostics. No weighted composite may hide a correctness loss.
- Existing `TaskCompletionScorer`, `task-score` JSON/stdout/exit behavior, retrieval scoring, and Miller/Julie product binaries stay unchanged during infrastructure implementation.
- Do not add an MCP tool, product dependency to `retrieval-eval`, generic agent-runner interface, or framework/provider registry.
- Use TDD. Every task leaves its owned slice verified and hands changes to the lead under `parallel-lead-commit`; workers do not commit.
- The user owns the sealed prompts, acceptance checks, arm mapping, raw trajectories, per-task rows, and final aggregate execution outside the repository.
- Do not inspect sealed rows, tune against sealed output, push, publish, release, or change normal product defaults.

---

## File Structure

| File | Responsibility |
| --- | --- |
| `scripts/benchlib/agent_contract.py` | Frozen task/snapshot/answer/run models, manifest validation, deterministic answer verification, and pinned token counting. |
| `scripts/benchlib/recording_mcp_proxy.py` | Transparent bidirectional stdio JSON-RPC forwarding plus exact call/response/timing/budget events. |
| `scripts/benchlib/agent_runner.py` | Concrete Codex 0.145.0 command construction, isolated config, JSONL event parsing, timeout/process cleanup, and disallowed-tool detection. |
| `scripts/bench-agent-efficiency.py` | Top-level validate/run orchestration, arm-order randomization, discordant reruns, and privacy-safe scorer inputs. |
| `scripts/benchmarks/agent-efficiency/*` | Frozen JSON schemas, dependency pin, visible tasks/snapshots, answer schema, operator protocol, and usage. |
| `eval/retrieval-eval/AgentEfficiencyModel.cs` | Privacy-safe manifest/run input records and exact allowed enums. |
| `eval/retrieval-eval/AgentEfficiencyReport.cs` | Aggregate-only correctness, efficiency, diagnostics, and subgroup report models. |
| `eval/retrieval-eval/AgentEfficiencyScorer.cs` | Validation, discordant stabilization, paired metrics, and approved decision gates. |
| `eval/retrieval-eval/Program.cs` | Thin additive `agent-score` file adapter and existing command dispatch. |

## Architecture Quality

- **Affected modules:** Keep task execution and MCP recording under `scripts/agent-efficiency`; keep statistical scoring under `eval/retrieval-eval`; keep product code unchanged until evidence identifies a repair.
- **Caller-facing interface:** Expose one concrete Python benchmark command and one additive `retrieval-eval agent-score` command; do not introduce a generic agent-runner abstraction.
- **Depth and locality:** The recording proxy owns tool policy and measurement at the MCP boundary, while the evaluator owns correctness and decision rules; neither leaks benchmark concerns into Miller runtime code.
- **Test surface:** Exercise the proxy through its JSONL protocol, the runner through recorded fixtures and fake processes, and the evaluator through its CLI contract and sealed-free fixtures.
- **Earned seams:** Use a concrete subprocess seam for Codex and a recording MCP proxy because both isolate real external boundaries; avoid interfaces for single in-process implementations.
- **Rejected shortcuts:** Do not modify `McpProcess`, reuse the semantic-only `task-score` verdict, shell together unvalidated JSON, or add a speculative multi-agent/provider framework.
- **Architecture risk:** Medium-high because the benchmark controls a product decision; fail closed on isolation, limits, protocol violations, missing tasks, or ambiguous correctness.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` / `AGENTS.md` for fast/Scale/build gates, `eval/retrieval-eval/README.md` for evaluator boundaries, and the approved [agent-efficiency design](2026-07-22-miller-julie-agent-efficiency-decision-design.md).

**Worker red/green scope:** Python contract/proxy/runner tasks run their exact `python -m unittest` module through the benchmark virtual environment. The scoring task runs `dotnet test eval/retrieval-eval/tests/RetrievalEval.Tests.csproj --filter "FullyQualifiedName~AgentEfficiency|FullyQualifiedName~AgentScore"`. The integration task runs all new Python tests plus the full evaluator test project.

**Worker ceiling:** New Python unittest modules and the full `RetrievalEval.Tests.csproj`. Workers do not run Miller fast/Scale suites, live Codex calls, Julie, the semantic sidecar, or sealed tasks.

**Worker gate invariant:** Contracts reject malformed or privacy-unsafe data; the proxy preserves MCP payloads while measuring them; Codex isolation fails closed; scorer math matches hand-computed fixtures; and existing semantic task scoring remains byte-compatible.

**Lead affected-change scope:** Create a temporary virtual environment from `scripts/benchmarks/agent-efficiency/requirements.txt`, run all `scripts/tests/test_agent_*.py`, run the full evaluator test project, and verify `eval/retrieval-eval/RetrievalEval.csproj` still has no package or product-project dependency.

**Branch gate:** `dotnet build Miller.slnx -c Release`, `scripts/test.sh`, `scripts/test.sh scale`, `dotnet test eval/retrieval-eval/tests/RetrievalEval.Tests.csproj`, and all new Python unittest modules in the pinned virtual environment.

**Replay/metric evidence:** Hard gates are manifest/schema validation, exact allowed-tool enforcement, proxy transparency, full-response token enforcement, discordant rerun shape, correctness floor, both efficiency routes, wall-time guard, privacy-safe aggregate output, and zero harness faults in the visible run. Raw latency, total model tokens, bytes, evidence density, and model-only semantic results are report-only diagnostics.

**Escalation triggers:** Stop for a plan mismatch if Codex 0.145.0 cannot isolate user config as documented, its JSONL omits MCP item events needed for enforcement, either product requires writes during read-only queries, task verification needs arbitrary executable code, raw sealed data would enter the repo/transcript, or a product repair cannot be named from visible evidence.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the visible-baseline finding. Record Codex/product/model/tokenizer/snapshot identities and every hard-gate metric. Reuse a passing ledger entry only for the exact same HEAD and frozen identities.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
| --- | --- | --- | --- | --- |
| Task 1: Freeze benchmark contracts and visible corpus | None - serial | Create `scripts/benchlib/agent_contract.py`, `scripts/benchmarks/agent-efficiency/requirements.txt`, `task-manifest.schema.json`, `snapshot-manifest.schema.json`, `answer-schema.json`, `run-result.schema.json`, `dev-tasks.json`, `dev-snapshots.json`, `scripts/tests/test_agent_contract.py` | Yes | Contract-first risk slice; Tasks 2-4 consume these exact models and schemas. |
| Task 2: Transparent recording MCP proxy | Batch A | Create `scripts/benchlib/recording_mcp_proxy.py`, `scripts/tests/test_recording_mcp_proxy.py`, `scripts/tests/fixtures/agent-efficiency/fake_mcp_server.py` | No | None - safe parallel batch after Task 1. |
| Task 3: Additive agent-efficiency scorer | Batch A | Create `eval/retrieval-eval/AgentEfficiencyModel.cs`, `AgentEfficiencyReport.cs`, `AgentEfficiencyScorer.cs`, `eval/retrieval-eval/tests/AgentEfficiencyScorerTests.cs`, `eval/retrieval-eval/tests/AgentScoreEndToEndTests.cs`; modify `eval/retrieval-eval/Program.cs:9-39,140-141,190-233`, `eval/retrieval-eval/README.md:13-51` | No | None - safe parallel batch after Task 1. |
| Task 4: Concrete Codex agent runner | Batch A | Create `scripts/benchlib/agent_runner.py`, `scripts/tests/test_agent_runner.py`, `scripts/tests/fixtures/agent-efficiency/codex-success.jsonl`, `codex-disallowed-tool.jsonl`, `codex-failure.jsonl` | No | None - safe parallel batch after Task 1; consumes the frozen proxy CLI and event contract without editing Task 2 files. |
| Task 5: Paired orchestration and operator protocol | None - serial | Create `scripts/bench-agent-efficiency.py`, `scripts/tests/test_bench_agent_efficiency.py`, `scripts/benchmarks/agent-efficiency/README.md`, `scripts/benchmarks/agent-efficiency/SEALED-AGENT-PROTOCOL.md` | Yes | Integrates Tasks 1-4 and owns the only top-level run workflow. |
| Task 6: Visible baseline and decision fork | None - serial | Create `docs/findings/2026-07-22-miller-julie-agent-efficiency-visible-baseline.md`, `docs/findings/agent-efficiency/2026-07-22-visible/**`; modify `docs/README.md` | Yes | Lead-only live evidence after the full branch gate. Its result determines whether the separate one-repair plan is needed. |
| Task 7: Sealed aggregate and product verdict | None - serial | Create `docs/findings/2026-07-25-miller-julie-agent-efficiency-decision.md`, `docs/findings/agent-efficiency/2026-07-25-sealed/aggregate.json`, `evidence-manifest.json`; modify `docs/README.md` | Yes | Requires the user-owned sealed aggregate and, if Task 6 exposes a Miller loss, completion of one separate evidence-grounded repair plan first. |

Commit mode for every task is `parallel-lead-commit`: workers hand verified diffs to the lead without committing. The lead runs the full branch gate, reviews the integrated diff, then creates the intentional commit. No push is authorized.

### Task 1: Freeze benchmark contracts and visible corpus

**Files:**
- Create: `scripts/benchlib/agent_contract.py`
- Create: `scripts/benchmarks/agent-efficiency/requirements.txt`
- Create: `scripts/benchmarks/agent-efficiency/task-manifest.schema.json`
- Create: `scripts/benchmarks/agent-efficiency/snapshot-manifest.schema.json`
- Create: `scripts/benchmarks/agent-efficiency/answer-schema.json`
- Create: `scripts/benchmarks/agent-efficiency/run-result.schema.json`
- Create: `scripts/benchmarks/agent-efficiency/dev-tasks.json`
- Create: `scripts/benchmarks/agent-efficiency/dev-snapshots.json`
- Test: `scripts/tests/test_agent_contract.py`

**Interfaces:**
- Consumes: controller manifests with task prompt, `workflow_class`, derived `evidence_critical`, fact predicates, accepted evidence anchors, forbidden claims, and snapshot id.
- Produces: `BenchmarkTask`, `SnapshotIdentity`, `StructuredAnswer`, `VerificationResult`, `load_task_manifest(path)`, `load_snapshot_manifest(path)`, `verify_answer(task, answer, snapshot_root)`, and `count_tool_output_tokens(text)`.

**Contract inputs:** Fact predicates are data only: required `all_terms`, optional `any_terms`, `source=answer|evidence_claim`, and required evidence-anchor ids; separate `path_cited`/`symbol_cited` predicates pin location. Evidence anchors are repo-relative path plus optional symbol and inclusive line bounds. No regex, Python import/callback, shell fragment, or arbitrary expression may appear in a manifest. The committed snapshot manifest contains logical repo ids, commits, hashes, and languages but no absolute roots; runtime roots arrive through repeatable `--snapshot-root <repo>=<dir>`. `tiktoken==0.13.0` and `o200k_base` are the frozen metric implementation; source: `https://pypi.org/project/tiktoken/`.

**File ownership:** Create `scripts/benchlib/agent_contract.py`, `scripts/benchmarks/agent-efficiency/requirements.txt`, `task-manifest.schema.json`, `snapshot-manifest.schema.json`, `answer-schema.json`, `run-result.schema.json`, `dev-tasks.json`, `dev-snapshots.json`, `scripts/tests/test_agent_contract.py`

**Serialization required:** Yes.

**Dependency reason:** Contract-first risk slice; Tasks 2-4 consume these exact models and schemas.

**What to build:** Define and test the complete benchmark data boundary before process orchestration exists. Populate twelve visible tasks with two rows per approved workflow class across at least five clean committed repo/language snapshots; include the observed natural-language semantic-architecture miss and derive criticality from class.

**Approach:** Use strict JSON field allowlists, ordinal ids/enums, repo-relative evidence paths, SHA-256 snapshot identities, and deterministic validation messages. Verify accepted evidence against the frozen snapshot without importing product indexes. The structured-answer schema is exactly `status`, `answer`, and bounded `evidence[]` entries containing `path`, optional `symbol`/`line`, and `claim`.

**Acceptance criteria:**
- [x] All four schemas reject extra fields and encode the exact approved enums and numeric floors.
- [x] The visible manifest contains exactly 12 unique tasks, two per workflow class, across at least five repositories/languages.
- [x] `evidence_critical` is true exactly for `exact_lookup`, `references_trace`, and `impact_tests`.
- [x] Snapshot validation rejects dirty roots, wrong commits/content hashes, absolute evidence paths, missing anchors, nested worktree content, and product/benchmark artifacts.
- [x] Answer verification requires every fact/evidence predicate and rejects forbidden claims without executing manifest data.
- [x] Token tests pin known `o200k_base` counts and fail rather than estimate when the tokenizer/encoding is unavailable.
- [x] Worker-scope verification passes and the diff is handed to the lead without a worker commit.

### Task 2: Transparent recording MCP proxy

**Files:**
- Create: `scripts/benchlib/recording_mcp_proxy.py`
- Create: `scripts/tests/test_recording_mcp_proxy.py`
- Create: `scripts/tests/fixtures/agent-efficiency/fake_mcp_server.py`

**Interfaces:**
- Consumes: `recording_mcp_proxy.py --events <jsonl> --tokenizer o200k_base --max-calls 8 --max-output-tokens 12000 --cwd <snapshot> -- <product-command> [args...]`.
- Produces: unchanged bidirectional JSON-RPC lines on stdio plus append-only proxy events for initialize, tools/list, tool calls/results/errors, stderr, budget transitions, process exit, exact bytes/tokens, and monotonic durations.

**Contract inputs:** The proxy owns stdout exclusively for forwarded MCP traffic. Diagnostics and product stderr never enter stdout. JSON-RPC ids may be strings or numbers; server notifications and requests are forwarded, not dropped. The ninth tool call is rejected without reaching the product. A response crossing 12,000 cumulative tokens is forwarded whole before the budget closes.

**File ownership:** Create `scripts/benchlib/recording_mcp_proxy.py`, `scripts/tests/test_recording_mcp_proxy.py`, `scripts/tests/fixtures/agent-efficiency/fake_mcp_server.py`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch after Task 1.

**What to build:** A transparent stdio relay that measures the exact MCP exchange seen by Codex without using or changing `McpProcess`. It must clean up the downstream process on EOF, timeout, or signal and leave enough events to distinguish product behavior from harness failure.

**Approach:** Use a select/threaded pump appropriate to the platform, a request-id timing map, atomic JSONL event writes, and process-group cleanup. Parse copies for measurement while forwarding the original line bytes. Hash initialize instructions and tool schemas in the summarized event, but keep raw visible-run events in the run directory.

**Acceptance criteria:**
- [x] Initialize instructions, tool schemas, arguments, results, errors, notifications, and request ids arrive unchanged at the opposite endpoint.
- [x] stdout contains only forwarded JSON-RPC; stderr and measurement events are separated.
- [x] Exact byte/token totals and monotonic durations match hand-computed fake-server exchanges.
- [x] Call 9 never reaches the fake server; a token-crossing response arrives whole and closes later calls.
- [x] Product crash, malformed JSON, timeout, controller EOF, and interrupt terminate children without an orphan.
- [x] Existing `scripts/benchlib/mcp_client.py` and its 14 references remain unchanged.
- [x] Worker-scope verification passes and the diff is handed to the lead without a worker commit.

### Task 3: Additive agent-efficiency scorer

**Files:**
- Create: `eval/retrieval-eval/AgentEfficiencyModel.cs`
- Create: `eval/retrieval-eval/AgentEfficiencyReport.cs`
- Create: `eval/retrieval-eval/AgentEfficiencyScorer.cs`
- Create: `eval/retrieval-eval/tests/AgentEfficiencyScorerTests.cs`
- Create: `eval/retrieval-eval/tests/AgentScoreEndToEndTests.cs`
- Modify: `eval/retrieval-eval/Program.cs:9-39`
- Modify: `eval/retrieval-eval/Program.cs:140-141`
- Modify: `eval/retrieval-eval/Program.cs:190-233`
- Modify: `eval/retrieval-eval/README.md:13-51`

**Interfaces:**
- Consumes: `AgentTaskManifestRow(task_id, repo, language, workflow_class, evidence_critical)` and `AgentRunResult(task_id, repetition, completed, failure_reason, duration_ms, tool_calls, tool_output_bytes, tool_output_tokens, model_input_tokens, model_output_tokens, product_errors, duplicate_calls, uncited_tool_output_tokens)`.
- Produces: `AgentEfficiencyScorer.Score(tasks, millerRuns, julieRuns)` and `retrieval-eval agent-score --tasks <jsonl> --miller <jsonl> --julie <jsonl> --out <json>`.

**Contract inputs:** Repetition numbers are `1..3`. Every task/arm has repetition 1. Initial agreement permits only repetition 1; initial disagreement requires exactly repetitions 1, 2, and 3 for both arms. Failure reasons are `incorrect|insufficient_evidence|budget_exceeded|disallowed_tool|product_error|invalid_answer`; completed rows have no failure reason. All counts/durations are nonnegative and uncited tokens do not exceed output tokens.

**File ownership:** Create `eval/retrieval-eval/AgentEfficiencyModel.cs`, `AgentEfficiencyReport.cs`, `AgentEfficiencyScorer.cs`, `eval/retrieval-eval/tests/AgentEfficiencyScorerTests.cs`, `eval/retrieval-eval/tests/AgentScoreEndToEndTests.cs`; modify `eval/retrieval-eval/Program.cs:9-39,140-141,190-233`, `eval/retrieval-eval/README.md:13-51`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch after Task 1.

**What to build:** Add a pure scorer and thin CLI adapter for the approved replacement floor and efficiency alternatives. Preserve privacy by emitting only aggregate cells, gates, arm metrics, failure counts, and sufficiently populated workflow/repo/language groups.

**Approach:** Keep the new scorer independent; do not modify `TaskCompletionScorer`, `TaskModel`, or `TaskReport`. Stabilize discordant pairs before computing completion cells. For a stabilized both-pass task, take the median of that arm's passing repetitions, then calculate arm medians and nearest-rank p75 over paired tasks.

**Acceptance criteria:**
- [x] Validation rejects missing/extra/duplicate task ids, bad run shapes, invalid critical flags, unsupported enums, negative counts, and privacy-unsafe fields.
- [x] Hand-computed fixtures cover initial agreement, both discordant majorities, critical loss, completion tie, Miller completion loss/win, and no both-pass tasks.
- [x] The 20% token route, one-call route, token non-regression, and 20% p75 wall guard match exact boundary fixtures.
- [x] Aggregate JSON contains no task id, input path, prompt, answer, evidence, trajectory, arm-order seed, or per-task row.
- [x] `agent-score` uses exit `0` for every valid verdict, `1` for usage/IO, and `2` for validation failure.
- [x] Existing `task-score` stdout, JSON, tests, and exit behavior remain unchanged.
- [x] `RetrievalEval.csproj` retains only `System.Text.Json` from the framework and no Miller/product project reference.
- [x] Worker-scope verification passes and the diff is handed to the lead without a worker commit.

### Task 4: Concrete Codex agent runner

**Files:**
- Create: `scripts/benchlib/agent_runner.py`
- Create: `scripts/tests/test_agent_runner.py`
- Create: `scripts/tests/fixtures/agent-efficiency/codex-success.jsonl`
- Create: `scripts/tests/fixtures/agent-efficiency/codex-disallowed-tool.jsonl`
- Create: `scripts/tests/fixtures/agent-efficiency/codex-failure.jsonl`

**Interfaces:**
- Consumes: `CodexAgentRunner.run(task, arm, snapshot, output_dir) -> AgentRun`, Task 1 contracts, and Task 2's frozen proxy command.
- Produces: exact Codex command/config manifest, raw Codex JSONL, final structured answer, proxy-event path, run outcome, usage diagnostics, and explicit harness/product failure classification.

**Contract inputs:** Verified 2026-07-22 against Codex CLI 0.145.0 and official `codex exec` documentation at `https://learn.chatgpt.com/docs/non-interactive-mode`: `--json`, `--ephemeral`, `--ignore-user-config`, `--ignore-rules`, `--strict-config`, `--output-schema`, `--sandbox read-only`, `--cd`, and `--skip-git-repo-check`. MCP config uses `mcp_servers.benchmark.command`, `args`, `cwd`, `required=true`, startup/tool timeouts, and the proxy as the only server. A mode-0700 temporary child Codex home receives only authentication material from the real home, is excluded from artifacts, and is destroyed after the run; inability to authenticate from that isolated home is a preflight failure.

**File ownership:** Create `scripts/benchlib/agent_runner.py`, `scripts/tests/test_agent_runner.py`, `scripts/tests/fixtures/agent-efficiency/codex-success.jsonl`, `codex-disallowed-tool.jsonl`, `codex-failure.jsonl`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch after Task 1; consumes the frozen proxy CLI and event contract without editing Task 2 files.

**What to build:** Launch Codex non-interactively in an empty temporary working directory with only the selected proxy configured, capture its JSONL event stream, enforce 120 seconds, and validate its last message through the answer schema. Detect command execution, file access, web search, non-arm MCP, file changes, and other disallowed item types from the event stream.

**Approach:** Build argument arrays without shell interpolation. Pass MCP configuration as strict TOML `-c` values, set `approval_policy="never"`, and require the proxy server. Copy only file-based auth when required, preserve restrictive permissions, never parse or log its contents, and otherwise use the platform credential store from the isolated child home. Start a process group/job, stream stdout/stderr without deadlock, terminate the entire group on timeout, and distinguish a valid agent insufficiency response from CLI/transport failure.

**Acceptance criteria:**
- [x] Command fixtures pin every required isolation, model, reasoning, schema, sandbox, MCP, and ephemeral option with no global config/plugin server.
- [x] A prompt-input/preflight fixture proves no machine-global `AGENTS.md`, skill, plugin, hook, history, or MCP server reaches the agent; temporary auth material is permission-restricted and absent from every artifact.
- [x] JSONL parsing accepts documented `thread.*`, `turn.*`, `item.*`, and `error` events and preserves unknown events for diagnostics.
- [x] Any command/file/web/non-arm tool event produces `disallowed_tool`; no final answer can override it.
- [x] Valid final JSON is schema-checked and handed to Task 1 verification; missing/malformed output is `invalid_answer`.
- [x] Timeout, nonzero exit, MCP startup failure, truncated JSONL, and signal cleanup are classified deterministically with no orphan.
- [x] Model usage from `turn.completed` is diagnostic and never substituted for proxy-measured tool-output tokens.
- [x] Worker-scope verification passes and the diff is handed to the lead without a worker commit.

### Task 5: Paired orchestration and operator protocol

**Files:**
- Create: `scripts/bench-agent-efficiency.py`
- Create: `scripts/tests/test_bench_agent_efficiency.py`
- Create: `scripts/benchmarks/agent-efficiency/README.md`
- Create: `scripts/benchmarks/agent-efficiency/SEALED-AGENT-PROTOCOL.md`

**Interfaces:**
- Consumes: `python3 scripts/bench-agent-efficiency.py --manifest <tasks.json> --snapshots <snapshots.json> --snapshot-root <repo>=<dir> [--snapshot-root ...] --arm miller|julie|both --out <dir> --seed <int> --model gpt-5.6-sol --reasoning medium`.
- Produces: immutable per-run raw directories, void ledger, privacy-safe `agent-tasks.jsonl`, `miller-results.jsonl`, `julie-results.jsonl`, identity manifest, SHA-256 evidence manifest, and an exact copyable `agent-score` command.

**Contract inputs:** `--arm both` is required for a decision run. The same task prompt and budget enter both arms. Product commands, env allowlists, binary hashes/versions, workspace/index/vector/model identities, snapshot hashes, Codex identity, tokenizer identity, schema hashes, and random seed are frozen before the first task.

**File ownership:** Create `scripts/bench-agent-efficiency.py`, `scripts/tests/test_bench_agent_efficiency.py`, `scripts/benchmarks/agent-efficiency/README.md`, `scripts/benchmarks/agent-efficiency/SEALED-AGENT-PROTOCOL.md`

**Serialization required:** Yes.

**Dependency reason:** Integrates Tasks 1-4 and owns the only top-level run workflow.

**What to build:** Validate the whole run before spending an agent call, balance/randomize arm order, execute the first repetition, schedule only predeclared discordant reruns, and export the privacy-safe scorer inputs. The sealed protocol tells the user-controlled operator exactly what stays private and what aggregate may return.

**Approach:** Separate harness voids from product outcomes and rerun both arms only for voids. Never overwrite a completed run directory; resume only by recognizing complete hash-matching rows. Redact secrets and absolute source roots from aggregate/evidence manifests. Keep raw dev runs locally reviewable and raw sealed runs outside the repository.

**Acceptance criteria:**
- [x] Preflight fails before Codex use on dirty/wrong snapshots, stale product indexes, hash/version mismatch, missing model/vector readiness, unavailable tokenizer, unsupported Codex version/model, or uninitializable product server.
- [x] Arm order is deterministic for a seed, balanced across tasks, and recorded without changing prompts.
- [x] Initial agreement emits one repetition per arm; initial disagreement emits exactly three per arm; harness voids rerun the whole pair and remain in the ledger.
- [x] Resume never duplicates or overwrites a complete hash-matching task/arm/repetition.
- [x] Scorer inputs contain only privacy-safe fields accepted by Task 3, and every artifact digest verifies.
- [x] The operator protocol requires 30 sealed tasks, five per class, five repo/language families, frozen identities, private raw data, automatic reruns, aggregate-only return, and spend-once handling.
- [x] Full Python integration tests and the full evaluator test project pass; the diff is handed to the lead without a worker commit.

### Task 6: Visible baseline and decision fork

**Files:**
- Create: `docs/findings/2026-07-22-miller-julie-agent-efficiency-visible-baseline.md`
- Create: `docs/findings/agent-efficiency/2026-07-22-visible/**`
- Modify: `docs/README.md`

**Interfaces:**
- Consumes: branch-gated benchmark, 12 visible tasks, clean frozen product/snapshot identities, and user-approved defaults.
- Produces: visible paired aggregate, raw reproducible dev artifacts, per-class failure classification, semantic diagnostic disposition, and an explicit `freeze` or `one-repair-plan-required` decision.

**Contract inputs:** Product-realistic diagnostics are Miller lexical-only, Miller production BGE-small, and Julie production CodeRankEmbed. If model isolation is needed, use identical texts/boundaries/normalization/query set/candidate count with BGE-small and Julie's Python CodeRankEmbed provider; do not use the blocked llama.cpp CodeRankEmbed conversion as a quality result.

**File ownership:** Create `docs/findings/2026-07-22-miller-julie-agent-efficiency-visible-baseline.md`, `docs/findings/agent-efficiency/2026-07-22-visible/**`; modify `docs/README.md`

**Serialization required:** Yes.

**Dependency reason:** Lead-only live evidence after the full branch gate. Its result determines whether the separate one-repair plan is needed.

**What to build:** Run the complete visible benchmark and classify every Miller loss as retrieval, routing, ambiguity, context assembly, output size, guidance, latency/runtime, product failure, or harness failure. Use direct MCP/model diagnostics only where they distinguish plausible causes.

**Approach:** Rank repair candidates first by Miller correctness losses, then expected token/call savings. If Miller already clears visible correctness and efficiency, freeze it without a repair. Otherwise invoke `razorback:systematic-debugging`, then `razorback:writing-plans`, for exactly one focused repair with named live files/tests; execute it, rerun all 12 visible pairs, and freeze. Do not guess that repair in this plan or alter the immutable semantic canary.

**Acceptance criteria:**
- [ ] All 12 pairs complete with zero unresolved harness voids and all frozen identity hashes recorded.
- [ ] The finding reports correctness cells/floor, both efficiency routes, p75 wall guard, task classes, failure reasons, and report-only diagnostics.
- [ ] Every Miller loss has one evidence-backed class and direct trajectory/tool evidence.
- [ ] Every semantic/concept failure includes both product-realistic replay and an identical-corpus BGE-small/CodeRankEmbed result. Inability to run the model-only comparison is a real blocker and prevents candidate freeze.
- [ ] The output is either `freeze` or `one-repair-plan-required`; no second repair or sealed tuning is permitted.
- [ ] The branch gate and verification ledger pass before the candidate is frozen and committed by the lead.

### Task 7: Sealed aggregate and product verdict

**Files:**
- Create: `docs/findings/2026-07-25-miller-julie-agent-efficiency-decision.md`
- Create: `docs/findings/agent-efficiency/2026-07-25-sealed/aggregate.json`
- Create: `docs/findings/agent-efficiency/2026-07-25-sealed/evidence-manifest.json`
- Modify: `docs/README.md`

**Interfaces:**
- Consumes: user-returned aggregate/evidence manifest from the frozen 30-task sealed run, without task ids, prompts, answers, paths, per-task rows, trajectories, or arm mapping secrets.
- Produces: one dated `Miller primary`, `Julie primary`, or `shared architecture limit requires separate design` verdict.

**Contract inputs:** The user-controlled operator runs the frozen benchmark and `agent-score`, verifies SHA-256 identities, then returns only the aggregate and safe identity manifest. The implementation agent does not request or inspect sealed raw data. The verdict applies the approved correctness floor before efficiency.

**File ownership:** Create `docs/findings/2026-07-25-miller-julie-agent-efficiency-decision.md`, `docs/findings/agent-efficiency/2026-07-25-sealed/aggregate.json`, `evidence-manifest.json`; modify `docs/README.md`

**Serialization required:** Yes.

**Dependency reason:** Requires the user-owned sealed aggregate and, if Task 6 exposes a Miller loss, completion of one separate evidence-grounded repair plan first.

**What to build:** Validate the safe aggregate and identities, apply the predeclared gates without reinterpretation, and record the immediate product direction. The longer semantic canary remains background evidence only.

**Approach:** Miller becomes primary only when correctness and one efficiency route pass. Any correctness or efficiency failure keeps Julie primary immediately. Consider a new project only when aggregate class evidence plus visible diagnostics prove both products share the same architectural limitation.

**Acceptance criteria:**
- [ ] The aggregate represents exactly 30 complete task pairs and only predeclared discordant reruns.
- [ ] Input hashes match the frozen candidate, products, model, tokenizer, schemas, and snapshots.
- [ ] No raw sealed or per-task material enters the repo, transcript, or finding.
- [ ] The finding states every correctness/efficiency gate and the resulting product verdict without a composite score.
- [ ] The verdict is dated no later than 2026-07-25; the canary cannot delay it.
- [ ] The final docs map and evidence manifest verify; the lead commits locally after the applicable branch gate. No push, release, or publication occurs.
