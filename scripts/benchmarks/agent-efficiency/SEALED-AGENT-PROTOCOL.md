# Sealed agent-efficiency protocol

The sealed run is user-controlled and spend-once. The implementation agent must not inspect its prompts,
acceptance checks, arm mapping, task rows, answers, evidence, events, or trajectories.

## Corpus gate

- Use exactly 30 sealed tasks.
- Use exactly five tasks from each workflow class: `exact_lookup`, `concept_search`, `docs_config`,
  `context_assembly`, `references_trace`, and `impact_tests`.
- Cover at least five repository/language families.
- Derive `evidence_critical` only from workflow class: true for `exact_lookup`, `references_trace`, and
  `impact_tests`; false otherwise.
- Freeze data-only fact predicates, accepted evidence anchors, forbidden claims, task hashes, and snapshot
  hashes before either product runs.
- Keep every repository snapshot dedicated to the benchmark and outside active developer checkouts. Its
  committed source is immutable. Only ordinary top-level `.miller` and `.julie` prepared artifact directories
  are permitted; reject symlinks, nested Git metadata, other dirty paths, and other artifact directories.

## Identity gate

Before the first Codex call, run `--preflight-only` and verify all 30 tasks and all snapshots are covered. Freeze:

- Codex CLI `0.145.0`, model `gpt-5.6-sol`, reasoning `medium`, and the answer-schema hash;
- `tiktoken==0.13.0` with `o200k_base`;
- both product commands, binary hashes, versions, commits, and the controller-derived isolated child
  environment keys;
- per-product, per-snapshot workspace/index/vector/model identities;
- per-product, per-snapshot MCP instruction and tool-schema hashes;
- task/snapshot manifest hashes, repository commits/content hashes, schema hashes, and random seed.

Do not proceed when any snapshot is dirty or mismatched, an index or vector/model identity is missing or stale,
a product cannot initialize and list its tools in any snapshot, or a pinned dependency differs.

## Execution gate

1. Place the runtime identity, sealed manifest, snapshot manifest, raw output, and child process artifacts outside
   every source repository.
2. Run once with `--arm both`. Do not run either arm separately for the decision.
3. Let the controller choose the balanced seeded order. Do not change a prompt, budget, schema, model, or process
   environment between arms.
4. Let initial agreement stop after repetition 1. Let initial disagreement run repetitions 2 and 3 for both
   arms automatically.
5. Let a controller, proxy, or Codex infrastructure fault void the whole pair. Preserve the void ledger and rerun
   both arms automatically. Product errors, invalid answers, budget failures, and disallowed tools are outcomes,
   not voids.
6. Resume only through the controller. It verifies complete per-run and export hashes and refuses partial,
   corrupt, or identity-mismatched state.
7. Do not inspect results between arms, repetitions, or tasks and do not tune either product after the sealed
   spend begins.

## Privacy and return gate

Keep private:

- task ids paired with prompts or acceptance checks;
- expected facts, evidence anchors, forbidden claims, and arm order;
- source roots, repository secrets, command manifests, stderr, final answers, cited evidence, Codex JSONL, MCP
  calls/results, per-task scorer rows, and the void ledger.

The operator may return only:

- the aggregate `agent-score` JSON;
- the safe identity manifest;
- the evidence manifest proving the returned artifact hashes;
- a statement that preflight, automatic reruns, artifact verification, and zero unresolved harness voids passed.

Do not return scorer JSONL or any raw row. Do not ask the implementation agent to diagnose a sealed task. If the
run cannot finish cleanly under frozen identities, preserve the private state, report only the aggregate blocker,
and do not spend replacement sealed tasks without a new user decision.

## Decision order

Apply correctness first: Miller must have at least Julie's stabilized completion count and zero evidence-critical
Julie-pass/Miller-fail tasks. Only then apply the token-or-call efficiency route and the p75 wall-time guard. A
correctness loss cannot be offset by speed, tokens, or a weighted score.
