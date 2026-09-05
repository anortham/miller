# Native-agent outcome evaluation and evidence-based positioning implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Determine where Miller improves completed coding tasks, and at what total cost, against the same agent with its native tools.

**Architecture:** Add a separately versioned `agent-outcomes-v1` evaluation beside the frozen takeover evaluation. Reuse recording, snapshot identity, and process-supervision machinery where its contracts fit. Keep task grading independent of Miller symbol IDs, tool selection, and agent self-reports.

**Tech Stack:** Python standard library/unittest, existing benchmark dependencies, Codex CLI JSONL, Git snapshots, repository-native test runners, existing Miller MCP/CLI.

**Architecture Quality:** Medium risk. The new contract changes what the experiment measures, not production tools. A runner adapter owns host-specific commands/events; neutral verification and paired scoring own no host implementation details. No new MCP tool, model server, second retrieval scorer, or changes to the historical C# takeover scorer are required.

**Status:** Execution-ready plan. Creating the harness and dry-run evidence is authorized when this plan is assigned. Paid model runs, model downloads, and publication require the user's separate authorization. A measured result is not claimed by writing this plan.

**Baseline inspected:** Miller `90220d7978fb4b59c593f8fd85b8bb8035700c32`; installed `codex-cli 0.153.3`, help checked 2026-09-04. Recheck both at execution start.

## Global Constraints

- Read [the program](2026-09-04-architecture-review-program.md) before dispatch.
- Preserve `takeover-evaluation-v1`, existing manifests, archived exports, prompt hashes, and `AgentEfficiencyScorer` byte-for-byte except an explicitly isolated shared-helper repair with old tests proving parity.
- The current `scripts/benchlib/agent_runner.py::_prompt_prefix` forbids native tools. Its `_command` fixes the old model and a read-only sandbox. Do not relabel that runner's results as native-agent results.
- Baseline and treatment use the same model, reasoning setting, host binary, task prompt, initial source, permissions, network policy, and test environment. Only Miller availability/configuration differs.
- Native tools remain available in every new arm. Never require the treatment to use Miller on every step; record actual adoption.
- No grading rule requires a Miller-issued ID, confidence number, tool name, or successful call to a particular tool. A correct answer obtained through native tools is correct.
- Published claims must identify the model, host, repositories, task count, repetitions, budgets, and limitations. A small corpus does not establish all-language performance.
- Use isolated task copies. Never run mutation tasks, test commands, or reset operations in the developer's live repository. Validate exact disposable paths before cleanup.
- Keep grading code, expected patches, and held-out assertions outside all agent-readable directories, not merely outside its working directory. A sandbox must actually enforce this boundary.
- Do not weaken test assertions, change labels after seeing treatment results, cherry-pick successful repetitions, or silently discard timeouts.
- `MILLER_SEMANTIC=off` and `MILLER_CT=off` retain their permanent zero-work semantics.
- Default experiment mode is validation/dry-run. No credential-consuming command runs without a frozen campaign and an explicitly approved run budget.
- Follow repo comment discipline: no narrative comments in test code. Do not create shell variables named HOME or CODEX_HOME or modify the user's auth/config files.

## Evidence motivating the work

Read `docs/findings/2026-08-25-miller-vs-bare-agent-v1.22.1-calibration.md` through its final nine-repetition section. The frozen Run A result is 11/15 versus 5/15; five tasks require product-issued identities that the bare adapter cannot produce. The Run A neutral subset is 7/10 versus 5/10. Both Run A efficiency gates fail. The repeated semantic comparison detects no average lift on that task mix; it is not an equivalence proof.

The README still foregrounds the older July budget result, which August did not reproduce. Fix that claim using existing evidence independently of the new campaign.

## Grounded entry points

| Existing file/symbol | Reuse or constraint |
|---|---|
| `scripts/benchlib/agent_runner.py::CodexAgentRunner` | Reference command construction, process classification, structured events; keep its old prompt/flags stable. |
| `scripts/benchlib/agent_runner.py::AgentArm`, `AgentSnapshot`, `AgentRun` | Inspect before deciding whether shared identity/process helpers can be reused without weakening old invariants. |
| `scripts/benchlib/recording_mcp_proxy.py::RecordingProxy` | Records Miller calls; it cannot count native shell/file work or total model cost. |
| `scripts/bench-agent-efficiency.py::balanced_arm_orders`, `execute_paired_tasks` | Existing paired ordering and failure accounting; do not reuse disagreement-triggered reruns as the new repetition design. |
| `scripts/benchlib/agent_contract.py::BenchmarkTask` | Frozen old contract; new neutral tasks get a separate type/module. |
| `scripts/tests/test_agent_runner.py` | Existing fake process fixtures and command assertions. |
| `scripts/benchmarks/agent-efficiency/README.md` | Existing environment/dependency setup and reproducibility conventions. |
| `eval/retrieval-eval/AgentEfficiencyScorer.cs` | Historical scorer remains unchanged. |
| `README.md`, `docs/site/benchmark.html`, `docs/site/method.html` | Public claim consumers. |

Current native command features were checked against installed help and [official non-interactive documentation](https://learn.chatgpt.com/docs/non-interactive-mode). `exec --json`, `--output-schema`, `--ephemeral`, `--model`, `--cd`, and `--sandbox read-only|workspace-write` exist. Recheck help before building commands; do not substitute obsolete automation flags. Unknown CLI versions/event shapes must produce an explicit unsupported-adapter result, not a silently incomplete measurement.

## Frozen proposed contracts

These are NEW evaluation artifacts, not existing production APIs.

1. `scripts/benchmarks/agent-outcomes/task.schema.json`: task fields `contract_id`, `task_id`, `repo_id`, `source_commit`, `snapshot_sha256`, `language`, `workflow`, `prompt`, `verifier_id`, `allowed_write_paths`, `max_wall_seconds`, and `max_model_tokens`. The contract ID is `agent-outcomes-v1`. Source commit and hashes must be real before a campaign can freeze.
2. `scripts/benchmarks/agent-outcomes/campaign.schema.json`: immutable task-set digest, exact host/model/reasoning identity, arms, independently seeded repetition count, randomized order seed, platform/toolchain image digest, network policy, resource limits, approved total run count, and approved money ceiling when priced usage exists.
3. Run record fields: identity, arm, repetition, order, outcome `correct|incorrect|timeout|product_error|infrastructure_void|unsupported`, verifier evidence digest, wall time, native tool counts, Miller calls, total model input/cached/output tokens, raw-event digest, and nullable price-derived cost. Missing usage is null and reported, never zero.
4. Runtime setup fields: download bytes/time, extraction time, sidecar convergence time, model load time, peak process memory, shared-broker mode, steady-state readiness, and measurement scope. Keep setup outside query-only timing but inside cold/end-to-end totals.

### Shared runtime-evidence join with S1

The final report root has `schema: "agent-outcomes-v1"`, `campaign_sha256`, and `arms`. Each arm has `arm_id`, `runtime_identity`, `runtime_qualification_sha256`, and its `metrics`. Native and lexical arms use null for both runtime fields.

The shared `metrics` object must contain `correctness`, `sufficient_evidence`, `calls`, `tokens`, `wall_time_seconds`, `retries`, `irrelevant_output`, `retrieval_diagnostics`, and `fallback`. S1 rejects a missing key or a non-object/empty metrics value before producing a joined record. Values remain the M5 schema's responsibility: null denotes explicitly unavailable measurement where M5 permits it, never zero or a passing result. S1 attaches those measurements without deriving efficacy from their presence. Additional metrics are allowed. Freeze and validate detailed metric types in M5 before any campaign; do not weaken these required join keys.

For a semantic arm, copy the complete `runtime_identity` object from S1 unchanged. Its exact keys are `sidecar_commit`, `binary_sha256`, `runtime_payload_sha256`, `model_id`, `model_sha256`, `model_manifest_sha256`, `miller_fixture_commit`, `resolved_backend`, `process_mode`, `served_dimensions`, `conformance_harness_sha256`, `throughput_harness_sha256`, and `concurrency_harness_sha256`. `model_sha256` hashes the prepared weight bytes; `model_manifest_sha256` hashes the canonical model manifest record. `runtime_payload_sha256` hashes the canonical complete runtime file/hash manifest, including native modules; hashing only the launcher cannot identify the runtime. `process_mode` is `stdio` or `broker`; evidence from independent stdio processes does not certify a shared broker workload.

`runtime_qualification_sha256` hashes the original S1 qualification file bytes. S1 can complete before any campaign and carries no campaign digest in its immutable runtime identity. A later S1 join verifies this file digest and exact identity against the matching M5 arm, then writes a separate joined record with `campaign_sha256` and the M5 report digest. Do not rewrite the hashed qualification file or make S1 runtime qualification depend on M5 completion. Missing runtime fields make a semantic arm unqualified; never fill them from the currently installed binary after the run.

No paid runner is enabled until schema validation and `freeze` create a manifest with all required identities. Reject duplicate JSON keys and unknown schema versions.

## Verification Strategy

**Project source of truth:** `CLAUDE.md`, existing benchmark README, and Python tests under `scripts/tests`.

**Worker red/green scope:** `python3 -B -m unittest discover -s scripts/tests -p 'test_agent_outcomes_*.py'`. These new tests use fake agents and tiny temporary Git fixtures; no model calls, network, or real extract process.

**Worker ceiling:** The new Python test pattern plus existing `test_agent_runner.py`, `test_recording_mcp_proxy.py`, and directly affected benchmark test modules.

**Worker gate invariant:** Configuration equality across arms, independent task grading, complete attempt accounting, stable old behavior, and no paid execution during validation.

**Lead affected-change scope:** Run all affected Python benchmark tests once after a coherent batch. Run `node --test tests/plugin/*.test.cjs` if public guidance or plugin tests require it; do not alter production guidance in this plan.

**Branch gate:** Python script test discovery with `python3 -B -m unittest discover -s scripts/tests -p 'test_*.py'`, `git diff --check`, link/claim audit. No .NET suite is needed for Python/docs-only changes. If a C# production file becomes necessary, stop that scope expansion and revise the plan before editing it.

**Security scope:** No production dependency changes planned. Verify that public report exports exclude credentials, private paths, source text, and held-out labels; negative fixtures are mandatory. Existing repository security release gates still apply to a later release.

**Replay/metric evidence:** Schema validity, deterministic verifier outcomes, isolation, and accounting are hard gates. Treatment superiority is an experimental result, never a gate to make the harness pass. Hardware-specific wall/RSS comparisons are reported with confidence intervals and scope.

**Escalation triggers:** Host event changes, inaccessible model, absent toolchain, incomplete token accounting, or grader visibility invalidate the affected comparison. Paid runs stop at the approved budget; dry-run work continues independently.

**Assigned verification failure:** Fix failures within the assigned ownership; report a concrete mismatch to the lead when fixing it would change the frozen measurement contract. Never lower the gate.

**Verification ledger:** Record command, commit, campaign digest, scope, UTC timestamp, result, failed/void counts, and artifact paths. Reuse green unchanged-scope evidence.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| 1: Neutral schema and verifier | A | New `scripts/benchlib/agent_outcomes_contract.py`, `scripts/benchmarks/agent-outcomes/{task,campaign}.schema.json`, `scripts/tests/test_agent_outcomes_contract.py` | No | None - safe parallel batch. |
| 2: Correct current public claims | A | `README.md`, `docs/site/benchmark.html`, `docs/site/method.html` | No | None - safe parallel batch. |
| 3: Native execution adapter | B | New `scripts/benchlib/agent_outcomes_runner.py`, `scripts/tests/test_agent_outcomes_runner.py`, `scripts/tests/fixtures/agent-outcomes/` | Yes | Task 1 defines inputs and outputs. |
| 4: Corpus and campaign freeze | B | New `scripts/benchmarks/agent-outcomes/{README.md,repositories.json,tasks.jsonl,verifiers/}`, `scripts/tests/test_agent_outcomes_corpus.py` | Yes | Task 1 defines task/verification identities. |
| 5: Paired scoring and controller | C | New `scripts/bench-agent-outcomes.py`, `scripts/benchlib/agent_outcomes_scoring.py`, `scripts/tests/test_agent_outcomes_scoring.py`, `scripts/tests/test_agent_outcomes_controller.py` | Yes | Tasks 3 and 4 produce runner and frozen corpus. |
| 6: Qualification and measured report | D | New `docs/findings/2026-09-04-agent-outcomes-harness-qualification.md`, campaign-specific findings/exports under `docs/findings/agent-outcomes/`, `docs/README.md` | Yes | Task 5 and explicit paid-run authorization for measured model results. |

Commit mode: `parallel-lead-commit`. Workers hand verified owned files to the lead; they do not stage unrelated changes. Tasks 3 and 4 may run together after Task 1. Do not run multiple campaigns concurrently on the same machine unless concurrency is the predeclared treatment.

## Task 1: Define neutral task grading

**Files/ownership:** Task 1 row above. **Interfaces:** New `validate_task(mapping)`, `validate_campaign(mapping)`, and `verify_result(task, result, artifact_root)` functions. **Contract inputs:** Proposed schema above; verifiers receive only the candidate result/source copy and frozen labels. **Serialization required:** No. **Dependency reason:** None - safe parallel batch.

1. Write a failing test that accepts a correct path/signature answer with no Miller ID, rejects the same answer with a wrong source span, and rejects a task whose only acceptance condition is a product ID. Use this proposed API shape:

   ```python
   def test_correct_location_needs_no_product_symbol_id(self):
       task = self.location_task(path="src/service.py", name="save", line=12)
       result = {"path": "src/service.py", "name": "save", "line": 12}
       self.assertTrue(verify_result(task, result, self.root).correct)
       self.assertFalse(verify_result(task, {**result, "line": 99}, self.root).correct)
   ```

   Implement `location_task` inside the new test fixture with explicit frozen labels. Do not borrow live Miller IDs to fill them.
2. Run the worker command, confirm failure comes from the missing neutral validator/verifier, and record it.
3. Implement location grading by normalized repository-relative path, definition signature/name, and permitted source-span alternatives. Reject traversal, absolute paths, symlink escapes, duplicate keys, missing evidence, and unknown fields. Mutation grading executes the frozen verifier in a separate validated copy, not code from the agent's answer string.
4. Define wrong-action checks independently of the answer's prose: unexpected changed paths, deleted acceptance tests, widened public behavior, and incomplete requested references are incorrect. A test command that never ran cannot count as a pass.
5. Run the worker command green. Add null/missing-cost and malformed-event schema fixtures now, before runner work.

**Acceptance criteria:**
- [ ] Correct native answers pass with zero product metadata.
- [ ] Invalid locations and forbidden mutations fail even if the agent claims success.
- [ ] Contract examples validate and malformed identities fail explicitly.
- [ ] The old task/scorer contract remains unchanged.

## Task 2: Align current claims with existing evidence

**Files/ownership:** Task 2 row above. **Interfaces:** Public prose and benchmark tables only. **Contract inputs:** July and complete August calibration findings. **Serialization required:** No. **Dependency reason:** None - safe parallel batch.

1. Record every numeric benchmark claim in the three target files and its exact supporting run/table. This is a documentation evidence check, not TDD.
2. Replace the universal claim that more budget makes the bare agent worse. The August rerun reports the bare arm flat at 5/15. Keep July as explicitly dated historical evidence.
3. Present the full-set and neutral-subset results together, state the product-ID constraint, and disclose the failed efficiency gates. Do not combine a correctness count from one run with a wrong-action rate from another.
4. Use this factual model for revised copy, adjusting only formatting:

   ```text
   In the August 25 visible calibration, Miller solved 11 of 15 tasks at the
   frozen budget versus 5 for the bare MCP adapter. Five tasks required
   product-issued identities unavailable to the adapter. On the ten neutral
   tasks, Run A scored 7 versus 5. This was not a native-agent comparison,
   and Miller did not pass the experiment's efficiency gate.
   ```
5. Verify every number against the finding, confirm all local links, and preview the HTML if layout changed materially. Do not change the release version from memory or from this plan.

**Acceptance criteria:**
- [ ] Claims identify the run and baseline restrictions.
- [ ] Historical results remain accessible without being presented as new measurements.
- [ ] No new efficacy or semantic equivalence claim is introduced.

## Task 3: Implement a genuine native-tool adapter

**Files/ownership:** Task 3 row above. **Interfaces:** New `NativeAgentRunner.run(task, arm, snapshot, output_dir)` returns the new run record. **Contract inputs:** Task 1 plus installed CLI help captured in the campaign manifest. **Serialization required:** Yes. **Dependency reason:** Task 1.

1. Use a fake executable in `scripts/tests/fixtures/agent-outcomes/` that records argv and emits deterministic JSONL for native command, edit, answer, usage, and failure events. Test before implementing the runner:

   ```python
   def test_native_baseline_has_no_miller_server(self):
       command, prompt = self.build_run(arm="native", workflow="repair")
       self.assertNotIn("Use only the benchmark MCP", prompt)
       self.assertNotIn("mcp_servers.benchmark.command", " ".join(command))
       self.assertEqual(self.option(command, "--sandbox"), "workspace-write")
   ```

2. Construct commands using argument arrays, never interpolated shell strings. The baseline runs with native tools and no Miller. Treatment adds Miller while retaining native tools. Exact model and reasoning come from required campaign fields; do not inherit a moving default.
3. Use `read-only` for answer-only tasks and `workspace-write` inside an isolated task copy for mutation tasks. Freeze the same constraints in paired arms. Run the agent CLI and its native tools inside a rootless Podman container on the Linux evaluation host; do not assume the CLI's write sandbox hides readable grading files. Podman and bubblewrap are installed on the planning host; this plan selects Podman only. The container image digest and mounts are frozen campaign inputs, not whichever image happens to be cached.

   Required directory topology:

   ```text
   host experiment root/
     task-input/       mounted as /workspace; per-run writable source copy
     agent-output/     mounted as /run-results; writable raw event artifacts
     private-grader/   NEVER mounted; labels, reference patches, hidden tests
     auth-input/       narrowly scoped approved credentials; never in public exports
   container image    native toolchains + CLI, pinned image digest
   ```

   Mount no host home, repository parent, container socket, or grader directory. Do not use privileged, host-PID, or host-network mode. The host supervisor grades the exported candidate in a second isolated container after the agent exits; it does not inject labels into the running agent. Model API networking and any repository dependency networking must be explicitly recorded and equal across arms. Dependency preparation is separate from measured execution; absent network enforcement must be disclosed, not described as network isolation.

   Before any paid call, start the exact runner image/mount configuration with a direct OS process, create an unpredictable sentinel in `private-grader`, and assert that opening both its absolute host path and `/private-grader/<sentinel>` fails. Also assert `/workspace` is writable only for mutation tasks, no container-management socket exists, and no host-parent mount is present by inspecting the container mount configuration. This is an actual OS process check, not an assertion made by a fake model. Preserve stdout, exit status, and the inspected mount list. On a host without this isolation, dry parsing tests may pass but live campaign execution must refuse.
4. Parse all native-agent usage and tool events separately from the Miller proxy. Unknown event versions preserve raw evidence and mark the run unsupported. At process timeout record the attempt and stop the owned process tree; do not erase its logs or rerun it only because it failed.
5. Capture the actual native tool availability, prompt, argv, environment allowlist names, binaries, and host-version hashes. Exclude secret values. Reuse existing auth handling only after checking it does not expose credentials in task directories or exported records.
6. Implement startup/resource accounting outside the model loop. For zero-semantic/zero-CT arms, assert no corresponding process/model/artifact access using fake process/file observers, not a model's claim.
7. Run new runner tests plus existing runner/proxy tests once. Existing prompt hashes and archived behavior must remain unchanged.

**Acceptance criteria:**
- [ ] Baseline can read, search, edit when permitted, and run tests with native tools.
- [ ] Miller is the only intended paired configuration difference.
- [ ] Total model usage is distinct from tool-output tokens; missing usage is visible.
- [ ] Fake runner qualification consumes no credentials and makes no network calls.

## Task 4: Build and freeze a representative corpus

**Files/ownership:** Task 4 row above. **Interfaces:** 36 fully labeled task records and six pinned repository records. **Contract inputs:** Task 1 schema. **Serialization required:** Yes. **Dependency reason:** Task 1.

1. Use six independent upstream repositories, starting with `pallets/flask`, `expressjs/express`, `go-chi/chi`, `BurntSushi/ripgrep`, `dotnet/command-line-api`, and `ruby/rake`. These are selection targets, not already pinned inputs. Resolve a real immutable commit, license, source hash, dependency lock state, and native test command for each before accepting its record. If one cannot run on the experiment machine, record exclusion before seeing treatment output and replace it with an independent repository in the same language using the same criteria.
2. Author six tasks per repository: exact location; conceptual behavior explanation; reference/dependency question with homonyms; narrowly specified safe edit; reproducible defect repair; change-to-test selection. Use real source and runnable verifiers. At least 12 tasks must require an edit/test outcome, not prose alone. CT selection tasks compare predicted affected cases with a frozen known change and complete runner inventory, never interpret unknown impact as proof of no tests.
3. Every mutation task includes an initial failing verifier, a reference patch that passes, and a deliberately wrong plausible patch that fails. Run all three locally without a model. Keep verifier inputs outside the model-readable snapshot.
4. Hold out two whole repositories for final evaluation. Freeze task labels before implementation-tuning runs on the remaining four. Do not inspect held-out model results until the final campaign. Task authoring necessarily sees source; report that limitation.
5. Include concept tasks with paraphrases, unrelated distractors, and explicit acceptable empty/refusal results. The semantic comparison must not consist mostly of exact names.
6. Validate with:

   ```bash
   python3 -B -m unittest discover -s scripts/tests -p 'test_agent_outcomes_corpus.py'
   ```

   The test must enumerate every checked-in record and reject missing commits, hashes, verifiers, or unsupported workflow labels. An empty corpus must fail. Store upstream source externally; commit manifests/verifiers and redistribution-permitted small fixtures only.

**Acceptance criteria:**
- [ ] All 36 tasks have real source identities and exercised positive/negative verifiers.
- [ ] Six languages and six independent repos are represented; no 40-language claim follows.
- [ ] Frozen development/holdout split and exclusion decisions are recorded.
- [ ] Held-out verification artifacts are inaccessible to the agent.

## Task 5: Add paired execution, honest costs, and reproducible scoring

**Files/ownership:** Task 5 row above. **Interfaces:** New CLI subcommands `validate`, `freeze`, `run`, and `score` in `scripts/bench-agent-outcomes.py`; these commands do not exist yet. **Contract inputs:** Tasks 1, 3, 4. **Serialization required:** Yes. **Dependency reason:** Runner and corpus required.

1. Test cost accounting before implementing it. Count costs of unsuccessful attempts too:

   ```python
   def test_cost_per_success_includes_failed_attempts(self):
       rows = [{"correct": True, "cost": 2.0}, {"correct": False, "cost": 8.0}]
       self.assertEqual(cost_per_success(rows), 10.0)
       self.assertIsNone(cost_per_success([{"correct": False, "cost": 8.0}]))
   ```

   Proposed implementation rule: sum measured attempt costs divided by verified successes; return null for zero successes or incomplete cost data. Report missing-data coverage separately.
2. Randomize paired arm order within each task/repetition. Use at least five predeclared repetitions per pair for the pilot; the final count is frozen before runs using pilot variance and an approved budget. No disagreement-triggered selective reruns. Report every repetition and every void reason.
3. Primary comparison: `native` versus `native+miller-lexical`, with semantics and CT off. Secondary comparison: lexical versus `native+miller-semantic` on the identical conceptual subset, explicit model/backend. CT comparison is a separate changed-test workflow with explicit enable/start and inventory warmup; do not attribute CT setup to semantic retrieval.
4. Use equal wall/model-token ceilings per task, not equal MCP-call counts. Native shell commands and MCP calls are not equivalent units. Record tool counts as explanatory metrics.
5. Report success rate, wrong-action rate, timeout/product-error rate, total wall time, total model usage/cost, and cost per verified success. Report paired confidence intervals clustered by repository/task, with repeated runs nested within tasks. Do not treat 5 repetitions of one task as 5 independent tasks.
6. Report cold first-use and amortized setup at 1, 10, and 100 tasks, using measured setup once per applicable environment. Shared broker memory is counted once, not once per client. Include product errors, timeouts, unsuccessful tasks, and billed infrastructure-void attempts in the arm's spent-cost numerator; infrastructure voids are excluded from the correctness denominator but their spend is separately visible. If ANY included attempt or setup component lacks required cost data, the full dollar-cost result is null with measured-attempt/component coverage. Do not silently sum the known subset and label it complete. Token and wall comparisons may still be reported with their own independent coverage.

   Add this additional regression before implementing the aggregator:

   ```python
   def test_missing_failed_attempt_cost_makes_total_unknown(self):
       rows = [{"correct": True, "cost": 2.0}, {"correct": False, "cost": None}]
       self.assertIsNone(cost_per_success(rows))
   ```
7. Freeze and test this CLI contract:

   ```bash
   python3 scripts/bench-agent-outcomes.py validate --tasks scripts/benchmarks/agent-outcomes/tasks.jsonl
   python3 scripts/bench-agent-outcomes.py freeze --config campaign.json --output campaign.frozen.json
   python3 scripts/bench-agent-outcomes.py run --campaign campaign.frozen.json --dry-run --output run-dry
   python3 scripts/bench-agent-outcomes.py score --run run-dry --output report-dry.json
   ```

   These are proposed commands to implement and test. `run` without `--dry-run` additionally requires an approval record whose campaign digest/run ceiling match. Refuse a changed campaign, missing usage-cap enforcement, or exhausted budget. A host that reports usage only at completion must count overshoot and stop further attempts; do not claim a hard token ceiling it cannot enforce.

**Acceptance criteria:**
- [ ] Fake paired runs produce deterministic scores and preserve failures/voids.
- [ ] Cost includes failed work and setup; zero-success cases cannot look cheap.
- [ ] No detected difference is reported as inconclusive, not proof of equivalence.
- [ ] The historical scorer and original benchmark outputs remain untouched.

## Task 6: Qualify the harness, then run only approved campaigns

**Files/ownership:** Task 6 row above. **Interfaces:** Reviewable evidence package consumed by the program. **Contract inputs:** Frozen campaign and, for semantic arms, [sidecar qualification](../../../julie-semantic-sidecar/docs/plans/2026-09-04-runtime-qualification-and-cost-evidence.md). **Serialization required:** Yes. **Dependency reason:** Task 5 and explicit authority for paid work.

1. Produce a dry qualification finding with fake-run argv/events, all verifier positive/negative results, isolation proof, manifest hashes, and the proposed campaign's exact maximum cost/run count. A missing model or budget does not prevent these deliverables.
2. Present the concrete frozen campaign for paid-run authorization. If not authorized, leave status `harness-qualified; campaign-not-run`, with an exact resume command. Never call it an efficacy result or a completed empirical campaign.
3. After authorization, run the pilot, freeze final repetition count and budget, then run the held-out campaign. Any expanded spending needs authorization before the extra run.
4. Cross-check model artifact identity and backend against the sidecar report. Do not join CPU cost with accelerated task results or compare different sidecar/model hashes as one treatment.
5. Write the result even if Miller loses. State where it helped, where it hurt, operational cost, and uncertainty. Keep implementation completion distinct from the empirical conclusion. Update `docs/README.md` to the new finding; update public claims only from reviewed completed results.

**Acceptance criteria:**
- [ ] Harness qualification is reproducible without paid calls.
- [ ] Every measured result is bound to a frozen campaign and complete attempt ledger.
- [ ] Unsupported hardware/hosts and unapproved runs are explicit, not counted as passes.
- [ ] Final recommendations follow results; a negative result does not trigger label changes.

## Handoff and completion ledger

| Task | Commit | Command/evidence | Scope | Timestamp | Result |
|---|---|---|---|---|---|
| Not executed | Not applicable | Planning only | planning | 2026-09-04 | No experiment run |

Execution is complete for implementation when Tasks 1-5 and Task 6 dry qualification pass. The empirical campaign is a separate explicitly reported approval-dependent deliverable. Preserve any existing task worktree and all user changes; do not commit/push/release merely because a benchmark finished.
