# Miller–Julie Takeover Evaluation Contract v1

Status: frozen Phase 0 contract. Implementation must conform before a takeover baseline or decision run is valid.

This contract defines how Miller and Julie are compared without product-specific scoring. It covers task labels, structured answers, paired execution, pure scoring, subset replay, sealed privacy, and the only run shape allowed to produce a decision verdict. It does not add an MCP tool or a second runner/scorer.

The implementation owners remain:

- `scripts/benchlib/agent_contract.py` and the agent-efficiency JSON schemas for task, answer, and semantic verification policy;
- `scripts/benchlib/agent_runner.py` for isolated agent execution and normalized run outcomes;
- `scripts/benchlib/recording_mcp_proxy.py` for byte-transparent MCP transport recording and budgets, never scoring;
- `scripts/bench-agent-efficiency.py` for pairing, selection identity, reruns, voids, resume, and export policy;
- `scripts/benchlib/reporting.py` for private-versus-safe artifact projection;
- `eval/retrieval-eval` for pure relevance, correctness, and efficiency scoring.

`TaskCompletionScorer` remains a separate accepted protocol. No takeover rule may silently change its input, output, or verdict semantics.

## Contract Identity And Versioning

Every takeover manifest, run artifact, scorer input, aggregate, and identity manifest carries:

```json
{
  "contract_id": "takeover-evaluation-v1",
  "schema_version": 1
}
```

The identifier and version have semantic force:

- Unknown `contract_id` or `schema_version` values fail before any agent or product launch.
- JSON schemas remain strict: unknown, duplicate, or missing required fields fail validation.
- A change to a field's meaning, an enum, canonicalization, metric formula, gate, identity hash, privacy class, or required field requires a new schema version. A breaking contract change requires `takeover-evaluation-v2`.
- Existing visible exports may enter through an explicit legacy compatibility adapter. They remain calibration evidence and cannot receive takeover, sealed, or retirement verdicts.
- Missing v1 fields are never inferred from prompts, filenames, product labels, old workflow classes, or default values.

## Corpus Lanes And Decision Scope

Two independent fields define a run:

| Field | Values | Meaning |
|---|---|---|
| `corpus_role` | `calibration`, `decision` | Whether labels are public calibration data or externally owned sealed decision data. |
| `decision_scope` | `subset`, `full` | Whether the run covers a capability-derived affected subset or the complete frozen manifest. |

### Calibration lane

- Calibration tasks, labels, snapshots, and raw results may be committed and inspected.
- Both `subset` and `full` scopes are allowed.
- Calibration results tune implementation and expose failures, but never authorize Julie retirement.
- Visible labels must not be copied into or treated as the sealed decision labels.

### External sealed decision lane

- `corpus_role=decision` is operator-controlled and external to every product and implementation repository.
- Only `decision_scope=full` is valid. A selector, missing task, or missing capability fails before preflight or product launch.
- Prompts, labels, acceptance checks, task IDs paired with labels, answers, evidence rows, events, trajectories, arm mapping, and raw scorer rows remain private.
- The lane is spend-once for a frozen decision identity. Resume may reuse only hash-verified artifacts from that same identity.
- The implementation session must not inspect, transform, diagnose, or repair sealed rows. The operator returns only the safe aggregate defined below.

## Task Classification

### Closed capability catalog

Each task has a non-empty, unique `capabilities` array containing only these 13 capability IDs:

1. `discovery`
2. `exact_symbol_lookup`
3. `homonym_disambiguation`
4. `context_orientation`
5. `callers`
6. `callees`
7. `call_path`
8. `impact_tests`
9. `edit`
10. `rename`
11. `logs`
12. `patterns`
13. `workspace_recovery`

A full calibration or decision manifest covers every capability at least once. Capabilities are orthogonal coverage and subset keys; they are not inferred from prompt text or `workflow_class`.

### `workflow_class` compatibility

`workflow_class` remains required as the coarse statistical rollup and keeps exactly the existing six values:

- `exact_lookup`
- `concept_search`
- `docs_config`
- `context_assembly`
- `references_trace`
- `impact_tests`

One task has exactly one workflow class and one or more capabilities. No fixed one-to-one mapping is implied. Existing balance and criticality reporting continues by `workflow_class`; takeover coverage and replay selection use `capabilities`.

The existing `evidence_critical` rule remains compatible: it is `true` exactly for `exact_lookup`, `references_trace`, and `impact_tests`, and `false` otherwise. The verifier rejects a conflicting value.

## Ground-Truth Contract

Task labels are data-only. They cannot contain commands, callbacks, regular expressions, executable graders, absolute paths, or product-specific scoring rules.

Each v1 task carries at least:

- task, repository, frozen snapshot, language, workflow, and capability identity;
- `expected_outcome`;
- evidence anchors with relevance grades;
- required fact, path, symbol, and forbidden-claim predicates where applicable;
- typed `acceptable_actions` and `forbidden_actions`;
- `uncertainty_expectation`;
- canonical `reference_sites` when reference identity is part of correctness.

The natural-language task and the product-neutral structured-answer contract cross into the agent prompt. Label IDs, expected outcomes, predicates, relevance grades, task-specific acceptable or forbidden actions, reference sites, uncertainty rules, and scores remain verifier-side.

### Expected outcomes

`expected_outcome` is exactly one of:

- `success` — the task requires a supported answer or action;
- `empty` — the correct result is that no qualifying result exists;
- `refusal` — the correct behavior is to stop because the requested exact action is unsafe or unsupported.

`hard_error` and `wrong_answer` are observed failures and can never be expected successes.

### Evidence anchors and ordered evidence

Each ground-truth evidence anchor has a stable `anchor_id`, repository-relative path, optional symbol identity, optional line/span, and integer `relevance_grade` in `[1,3]`.

The structured answer contains an ordered evidence array. The verifier maps each submitted row to at most one canonical anchor by exact path plus the label's required symbol/span constraints. It records the matched anchor ID in submitted order. The agent never sees anchor IDs.

An unmatched row has grade `0`. Repeated matches to the same anchor gain `0` after the first occurrence. Product-specific raw tool-result order is not scored; both roles are scored from the common structured answer.

The shared agent prompt states that `evidence.path` is repository-relative, `evidence.symbol` is a human-readable symbol name rather than a symbol ID, and `evidence.line` must fall inside the cited fact. This public shape rule is part of the prompt identity; it does not reveal private anchors.

### Canonical reference sites

Reference-sensitive labels use `reference_sites`. Each site contains:

| Field | Rule |
|---|---|
| `site_id` | Stable within the frozen manifest; label-side only. |
| `path` | Repository-relative path. Absolute paths are invalid. |
| `line_start`, `line_end` | One-based inclusive lines with `line_start <= line_end`. |
| `column_start`, `column_end` | Optional zero-based UTF-16 span with an end-exclusive `column_end`; both or neither are present. |
| `reference_kind` | Canonical extractor/Miller kind, not a source-specific alias. |
| `containing_symbol_id` | Canonical symbol ID when known, otherwise null. |
| `source_symbol_id` | Canonical source symbol ID when the relationship has one, otherwise null. |
| `target_symbol_id` | Canonical target symbol ID for exact evidence; null only when unresolvedness is the labeled fact. |
| `resolution` | `exact`, `fallback`, or `unresolved`. |

An exact-site predicate matches the full canonical identity required by the label. Same-name symbols, a different site, a different canonical kind, fallback evidence, or an unresolved row cannot satisfy an exact site.

### Typed actions

Final answer actions use this closed `kind` catalog:

- `inspect_symbol`
- `inspect_file`
- `assemble_context`
- `trace_callers`
- `trace_callees`
- `trace_call_path`
- `cite_reference_site`
- `select_tests`
- `propose_edit`
- `propose_rename`
- `read_log`
- `query_pattern`
- `recover_workspace`
- `report_empty`
- `refuse_unsafe`

An action contains `kind` and a typed target composed only from repository-relative path, canonical symbol identity, canonical reference-site fields, test path, pattern ID, or workspace selector as applicable. Empty target fields are rejected. Edit and rename actions are proposals only; the evaluation runner remains read-only.

The shared agent prompt states that actions are the minimum typed evidence needed to ground the answer, not a transcript of every tool call. This exposes the scoring rule equally to both roles without revealing task-specific accepted targets.

Each acceptable action label has a stable label-side `action_id`, exact `kind` and target, a non-empty `requirement_group`, and optional evidence-anchor/site requirements. Each requirement group must be satisfied by at least one submitted action. Multiple labels in one group express valid alternatives.

Each forbidden action label has a stable label-side ID, exact `kind` and forbidden target, and a reason. Any submitted action matching a forbidden action is a wrong action. A submitted final action that matches no acceptable action is also a wrong action. Matching uses typed fields, never prose or substring search.

Canonical matching treats a grounded repository path attached to an exact symbol-ID action as corroborating metadata rather than a different action. The path must match an evidence anchor tied to the acceptable action; a conflicting path remains unrecognized. Current-workspace actions likewise accept only aliases that resolve to the prepared snapshot: the task repository ID, `.`, `current`, canonical root, stable workspace ID, or the product display-ID form derived from that identity.

`wrong_action_count` is the number of distinct submitted actions that are forbidden or unrecognized, de-duplicated by canonical action identity. A task is action-correct only when all required groups are satisfied and `wrong_action_count=0`.

### Uncertainty

`uncertainty_expectation` is exactly one of:

- `must_resolve` — the answer must resolve the labeled identity and may not substitute fallback ambiguity;
- `must_disclose` — the answer may proceed only while explicitly returning the typed ambiguity/fallback disposition required by the label;
- `must_refuse` — the answer must use `refuse_unsafe` and take no conflicting action.

Failure to satisfy the uncertainty rule is a wrong answer. A refusal passes only when `expected_outcome=refusal`, `uncertainty_expectation=must_refuse`, and the typed refusal matches. An empty result passes only when `expected_outcome=empty` and `report_empty` matches. Empty and refusal are never aliases for insufficient evidence on a success task.

## Canonical Observed Outcomes

The verifier/runner emits exactly one normalized `observed_outcome` per role, task, and repetition:

- `success` — `expected_outcome=success` and every fact, evidence, action, forbidden, and uncertainty check passes;
- `empty` — `expected_outcome=empty` and the structured empty action passes all applicable checks;
- `refusal` — `expected_outcome=refusal` and the structured refusal passes all applicable checks;
- `hard_error` — product, tool, timeout, budget, structured-output, or process failure prevents a valid answer;
- `wrong_answer` — a completed answer or policy action violates expected outcome, fact, evidence, action, forbidden, or uncertainty rules.

The first three outcomes are correct only when they equal the task's expected outcome and all semantic checks pass. `hard_error` and `wrong_answer` are always incorrect.

Diagnostic reasons remain separate from the canonical outcome. The allowed reasons remain `incorrect`, `insufficient_evidence`, `budget_exceeded`, `disallowed_tool`, `product_error`, and `invalid_answer`.

The required normalization is:

- a correct `not_found` becomes `empty`; a correct `blocked` becomes `refusal`;
- an unexpected `not_found`, `blocked`, false claim, wrong target, missing required action, or forbidden action becomes `wrong_answer`;
- `disallowed_tool` becomes a scored `wrong_answer` with diagnostic reason `disallowed_tool`;
- timeout, budget exhaustion, product/tool failure, or invalid structured answer becomes `hard_error` with its specific diagnostic reason;
- no product outcome is reconstructed from free-form text or a product label.

## Harness Voids Versus Scored Outcomes

A harness void means the paired comparison itself is invalid. It is not a sixth product outcome and produces no scorer row for either role.

Void the pair only for a failure outside the compared products that prevents equal execution or trustworthy capture, including:

- controller, common Codex harness, or recording-proxy protocol failure;
- corrupt, missing, or hash-mismatched shared task/snapshot/runtime artifacts;
- failure to establish identical frozen budgets, model/reasoning, sandbox, prompt schema, or paired selection;
- capture loss that makes calls, tokens, output, duration, or answer identity untrustworthy;
- resume identity mismatch.

Product timeout, process exit, tool failure, invalid answer, budget exhaustion, and disallowed-tool use are scored outcomes. They never void the other role. In particular, detecting a disallowed tool must change classification away from `harness_failure` before paired void logic runs.

Every void records a reason in a private void ledger and spends neither role's task result. Retry uses the same frozen pair identity and arm order. A final or sealed gate requires zero unresolved voids.

## Neutral Baseline And Candidate Roles

The evaluator and scorer know only `baseline` and `candidate` roles. Product display names, executable commands, environment, binary versions, and commits are adapter metadata frozen in runtime identity; they cannot select verifier or scorer logic.

Both roles use identical:

- tasks, snapshots, prompt and answer schemas;
- model, reasoning effort, context, call/token/time budgets, and repetition rules;
- isolated agent home, read-only sandbox, allowed-tool policy, and recording proxy;
- semantic verifier, outcome normalizer, relevance scorer, action scorer, and aggregate formulas.

Both roles run the identical selected task set in one paired run. Decision runs require both roles. Swapping product adapters changes attribution metadata only; normalized events must score identically.

The existing Miller/Julie-named CLI may remain only as a thin legacy adapter to baseline/candidate inputs. New takeover artifacts and reports use neutral role names.

## Capability-Derived Subsets And Identity

The public repeated selector may retain the CLI spelling `--task-family`, but each value is one capability ID from the closed catalog. The runner must not accept callbacks, task IDs, prompt matching, arbitrary predicates, or remediation phase numbers as selectors.

Selection occurs after strict full-manifest validation and before runtime preflight identity construction:

1. Validate the complete parent manifest, including full capability coverage.
2. Validate and ordinal-sort the unique requested capability IDs.
3. Select the union of tasks whose `capabilities` intersects the requested set.
4. Preserve parent-manifest order for paired execution and ordinal-sort task IDs only for identity hashing.
5. Reject an empty selection or unknown capability before any agent/product launch.

The private selection identity contains:

- `contract_id`, `schema_version`, `corpus_role`, and `decision_scope`;
- `parent_manifest_sha256` over the exact parent-manifest bytes;
- `snapshot_manifest_sha256` over the exact snapshot-manifest bytes;
- sorted `selected_capability_ids`;
- selected task count;
- ordinal-sorted selected task IDs;
- `selected_task_ids_sha256`;
- `selection_sha256`.

`selected_task_ids_sha256` is lowercase SHA-256 over UTF-8 task IDs joined by one LF with a final LF. `selection_sha256` is lowercase SHA-256 over compact UTF-8 JSON with keys sorted ordinally and arrays in the canonical orders above, containing the contract/version, lane/scope, parent and snapshot digests, selected capabilities, task count, and selected-task-ID digest.

Resume requires exact selection and runtime identity equality. A changed parent, snapshot, capability set, task membership, role adapter, budget, model, command, environment, instructions, or tool-schema hash creates a different run identity.

Subset aggregates carry `decision_scope=subset` and `decision_verdict=not_decisional`. They may diagnose later phases but cannot pass or fail retirement. Safe subset output may expose public selected capability IDs, count, and selection hashes, but never task IDs or rows.

## Repetition And Stabilization

Every selected task/role starts with repetition `1`.

- If baseline and candidate correctness agree on repetition 1, both roles have only repetition 1.
- If correctness disagrees, both roles must have exactly repetitions `1`, `2`, and `3`.
- Stabilized correctness is the strict majority of the role's correctness values.
- For three repetitions, a canonical outcome occurring at least twice is the stabilized outcome. If incorrect outcomes are all different, use deterministic precedence `hard_error` before `wrong_answer`; a correct majority necessarily shares the task's expected outcome.

Missing, extra, or duplicate repetitions invalidate the run before scoring.

## Ordered Evidence Relevance

Relevance is scored independently from action correctness and efficiency. It uses the ordered evidence in the common structured answer, never product-specific raw tool output.

For one repetition, let `R` be the set of relevant anchor IDs, `g(a)` the anchor's grade in `[1,3]`, and `e_1..e_N` the complete submitted evidence order, with unmatched/duplicate rows assigned grade `0`. Recall and nDCG use only positions 1 through 6.

- `recall@6 = |{e_i in R}| / |R|`.
- `DCG@6 = sum(i=1..6, (2^g(e_i) - 1) / log2(i + 1))`.
- `IDCG@6` uses the six highest ground-truth grades in descending order with the same gain and discount.
- `nDCG@6 = DCG@6 / IDCG@6`.
- `MRR = 1 / min{i | e_i in R, 1 <= i <= N}`; it is `0` when no relevant anchor is matched.
- `top_1 = 1` when `e_1 in R`, otherwise `0`.

Relevance is defined only for tasks with `expected_outcome=success` and at least one graded relevant anchor. Zero relevant anchors on a relevance-eligible task are invalid labels. Empty and refusal tasks are outcome-scored, not assigned fabricated relevance zeros.

For a role/task with three repetitions, each metric is the median of the three repetition scores; incorrect repetitions contribute `0`. Role aggregates are macro means over the identical full set of relevance-eligible selected tasks. No query, task, repository, language, workflow, or capability receives extra weight from having more evidence rows.

The relevance gate passes exactly when candidate recall@6, nDCG@6, MRR, and top-1 are each greater than or equal to the corresponding baseline metric. The report preserves each metric separately; they are never combined into correctness or one composite score.

## Action Correctness And Efficiency

### Correctness

Per-task correctness means the canonical observed outcome matches `expected_outcome`, every semantic/evidence/action/uncertainty check passes, and `wrong_action_count=0`.

For stabilized task counts:

- `baseline_correct = both_correct + baseline_only`;
- `candidate_correct = both_correct + candidate_only`;
- `wrong_action_rate = stabilized_wrong_action_tasks / selected_task_count`.

The correctness gate passes exactly when:

1. `candidate_correct >= baseline_correct`;
2. there are zero `evidence_critical` baseline-pass/candidate-fail tasks;
3. `candidate_wrong_action_rate <= baseline_wrong_action_rate`.

Correct empty and refusal outcomes count as correct while remaining distinct outcome cells. Hard errors cannot count as empty/refusal, and token/call/wall gains cannot compensate for a correctness failure.

### Efficiency population and per-task values

Efficiency uses only tasks whose stabilized result is correct for both roles. If that population is empty, efficiency is `not_measurable` and its gate fails.

For each role/task, take the median over that role's correct repetitions for:

- `tool_output_tokens`;
- `tool_calls`;
- `duration_ms`.

Then compute the arm median of the per-task token values and call values. Compute p75 wall time by sorting per-task median durations ascending and selecting nearest rank `ceil(0.75 * N)`, one-based.

`duration_ms` is measured from timed agent execution start through receipt of the submitted structured final answer. It is the contract's wall time to the accepted final action; no earlier event is called “time to first action” without a future versioned timing contract.

### Efficiency gate

Let `B_t`, `C_t` be baseline/candidate median tool-output tokens; `B_c`, `C_c` median tool calls; and `B_w`, `C_w` nearest-rank p75 duration.

- Token route: `B_t > 0` and `C_t <= 0.80 * B_t`.
- Call route: `C_c <= B_c - 1` and `C_t <= B_t`.
- Wall guard: `C_w <= 1.20 * B_w`.
- Efficiency passes exactly when the wall guard passes and either the token route or call route passes.

The report may additionally aggregate output bytes, model tokens, duplicate calls, uncited tokens, and product errors, but none changes the v1 efficiency verdict.

## Reports And Final Gates

All reports keep relevance, correctness, outcomes, and efficiency in separate blocks. Aggregate outcome counts include all five canonical outcomes by neutral role. Failure-reason counts remain diagnostic and cannot replace outcome counts.

The pure agent-action scorer emits `action_verdict=pass|fail` from its correctness and efficiency blocks, but always emits `decision_verdict=not_decisional`. It does not receive relevance results, full capability/selection identity, corpus role, privacy validation, or unresolved-void state. Only the later combined full-decision aggregate may emit `decision_verdict=pass|fail` after validating every final-gate input below.

Workflow, capability, repository, and language subgroups are aggregate-only, ordinally ordered, and suppressed when they contain fewer than five selected tasks. Suppression never removes a task from global gates.

### Subset report

A subset report includes identity, selected public capabilities, task count, aggregate outcomes, separate relevance/correctness/efficiency diagnostics, and `decision_verdict=not_decisional`. It cannot claim takeover readiness or retirement.

### Full calibration gate

A visible full baseline is valid only when:

- the frozen parent and snapshot manifests validate and cover all 13 capabilities;
- both neutral role adapters run under one frozen paired identity;
- all contract, schema, verifier, runner, proxy, CLI, and pure-scorer tests pass;
- relevance/correctness/efficiency blocks are complete and zero harness voids remain unresolved.

It is implementation evidence, not a retirement verdict.

### Full sealed decision gate

Only `corpus_role=decision` plus `decision_scope=full` may emit `decision_verdict=pass|fail`. The gate refuses partial capability coverage, selectors, missing tasks, identity mismatch, label mutation, unresolved voids, or a non-external private-artifact root.

`decision_verdict=pass` requires all of:

1. exact frozen full manifest/snapshot/runtime/selection identities and all 13 capabilities;
2. both neutral roles completed under identical paired controls with zero unresolved harness voids;
3. correctness gate pass;
4. relevance gate pass;
5. efficiency gate pass;
6. safe-export privacy validation pass.

Any failed prerequisite produces `decision_verdict=fail` or a classified invalid-run refusal; no incomplete run becomes a pass. The evaluator verdict is one input to the broader platform, contract, review, and release gates in the remediation plan. It does not by itself publish, release, or delete Julie.

## Privacy And Export

### Private artifacts

Decision manifests, snapshots when private, task IDs paired with labels, prompts, predicates, anchor/reference/action labels, answers, ordered evidence, actions, events, trajectories, per-task repetitions, scorer rows, arm mapping, void ledger details, and raw logs stay under an operator-owned external root. They are never committed, copied into a repository, or returned to the implementation session.

### Safe aggregate

The only decision artifact returned across the sealed boundary may contain:

- contract/schema version, `corpus_role=decision`, and `decision_scope=full`;
- parent/snapshot/runtime/selection digests;
- public selected capability IDs and selected task count;
- neutral role aggregate outcome counts and failure counts;
- global relevance, correctness, wrong-action, and efficiency metrics and gates;
- privacy-safe subgroups meeting the five-task floor;
- unresolved-void count, which must be zero for a decision;
- hashes of retained private evidence artifacts;
- `decision_verdict`.

It must not contain task IDs, prompts, acceptance labels, repository roots, file paths, symbol/reference/action identities, answers, evidence rows, events, trajectories, per-task values, arm order, product mapping, or private filenames.

Safe-export validation runs before the artifact crosses the boundary. Hashes prove private evidence retention without revealing contents. Aggregate values must not be rounded or suppressed in a way that can turn a failed gate into a pass.

## Phase 0 Requirement Map

| Phase 0 requirement | Frozen v1 owner |
|---|---|
| Complete takeover coverage | Closed 13-ID `capabilities` catalog plus full-manifest coverage validation. |
| Symbols, files, reference sites, actions, false positives, uncertainty | Ground-truth anchors, canonical `reference_sites`, typed acceptable/forbidden actions, existing predicates/forbidden claims, and `uncertainty_expectation`. |
| Separate relevance and action evaluation | Ordered-evidence relevance block and independent correctness/efficiency blocks. |
| Five canonical outcomes | `observed_outcome` normalization from verifier through run artifact and scorer. |
| Product-neutral comparison | Baseline/candidate roles with product labels confined to frozen adapter metadata. |
| Affected replay | Capability-derived immutable subset identity and non-decisional subset report. |
| Final full replay | Full calibration gate and full-only external sealed decision gate. |
| Calibration/sealed separation | Mechanical `corpus_role`, external-root enforcement, spend-once identity, and safe export. |
| Disallowed tools versus voids | Disallowed tools are scored wrong answers; only common harness/capture faults void a pair. |
| Exact metrics and gates | Frozen relevance, stabilization, correctness, wrong-action, efficiency, and final-gate formulas above. |

## Compatibility And Change Control

- The six workflow classes and existing evidence-critical derivation remain stable.
- Existing fact, path, symbol, evidence-anchor, and forbidden-claim checks remain and compose with v1 action, uncertainty, and reference-site checks.
- Existing balanced order, one-versus-three repetition rule, immutable artifacts, resume hashes, proxy transparency, budgets, isolation, and aggregate subgroup floor remain load-bearing.
- Legacy Miller/Julie names may exist only at an input adapter; all v1 scorer policy and output use baseline/candidate roles.
- `MILLER_SEMANTIC=off`, extractor ownership, Miller's nine-tool limit, and all-language gates are outside evaluator scoring and remain unchanged.
- No implementation may weaken a gate, expose sealed data, infer missing labels, add product-specific scoring, or reinterpret a legacy artifact as v1 without a new contract version and explicit migration evidence.

See the [takeover remediation plan](../plans/2026-07-22-miller-julie-takeover-remediation-plan.md) for implementation order and the non-evaluator gates that follow Phase 0.
