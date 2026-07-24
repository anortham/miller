# Sealed agent-efficiency protocol

The sealed run is user-controlled and spend-once. It uses only neutral `baseline` and `candidate` roles.
The implementation agent must not inspect its prompts, acceptance checks, adapter mapping, task rows, answers,
evidence, events, or trajectories.

## Corpus gate

- Use exactly 30 sealed tasks.
- Use exactly five tasks from each workflow class: `exact_lookup`, `concept_search`, `docs_config`,
  `context_assembly`, `references_trace`, and `impact_tests`.
- Cover at least five repository/language families.
- Derive `evidence_critical` only from workflow class: true for `exact_lookup`, `references_trace`, and
  `impact_tests`; false otherwise.
- Validate the complete parent manifest before selection and require coverage of all 13 closed takeover
  capabilities. A decision is always `corpus_role=decision` plus `decision_scope=full`; it accepts no
  `--task-family` selector.
- Freeze data-only fact predicates, accepted evidence anchors, forbidden claims, task hashes, and snapshot
  hashes before either adapter runs.
- Keep every repository snapshot dedicated to the benchmark and outside active developer checkouts. Its
  committed source is immutable. Only ordinary top-level `.miller` and `.julie` prepared artifact directories
  are permitted; reject symlinks, nested Git metadata, other dirty paths, and other artifact directories.

## Identity gate

Before the first Codex call, run `--preflight-only` and verify all 30 tasks and all snapshots are covered. Freeze:

- Codex CLI `0.145.0`, model `gpt-5.6-sol`, reasoning `medium`, and the answer-schema hash;
- `tiktoken==0.13.0` with `o200k_base`;
- both neutral role adapter commands, opaque adapter names, binary hashes, versions, commits, and the
  controller-derived isolated child
  environment keys;
- per-role, per-snapshot workspace/index/vector/model identities;
- per-role, per-snapshot MCP instruction and tool-schema hashes;
- exact parent/snapshot manifest byte hashes, selected-task-ID and selection hashes, repository
  commits/content hashes, schema hashes, and random seed.

Do not proceed when any snapshot is dirty or mismatched, an index or vector/model identity is missing or stale,
an adapter cannot initialize and list its tools in any snapshot, a pinned dependency differs, or a duplicate,
missing, or unknown runtime role is present. The legacy `products.miller/julie` adapter is calibration-subset
only and is forbidden for this run.

## Execution gate

1. Place the runtime identity, sealed manifest, snapshot manifest, raw output, private scorer rows, void ledger,
   retained hashes, and child process artifacts under one declared operator-owned `--private-root` outside the
   Miller checkout and every snapshot repository. The controller must reject any escaped path before reading
   the runtime identity or launching an adapter.
2. Run once with `--arm both`. Do not run either role separately for the decision.
3. Let the controller choose the balanced seeded role order. Do not change a prompt, budget, schema, model, or
   process environment between roles.
4. Let initial agreement stop after repetition 1. Let initial disagreement run repetitions 2 and 3 for both
   arms automatically.
5. Let a controller, proxy, or Codex infrastructure fault void the whole pair. Preserve the void ledger and rerun
   both arms automatically. Product errors, invalid answers, budget failures, and disallowed tools are outcomes,
   not voids.
6. Resume only through the controller. It verifies complete per-run and export hashes and refuses partial,
   corrupt, or identity-mismatched state.
7. Do not inspect results between roles, repetitions, or tasks and do not tune either adapter after the sealed
   spend begins.
8. Run the generated `agent-score-command.txt` exactly. It invokes the neutral scorer with
   `--baseline`/`--candidate`, then digest-verifies the private export and writes the allowlisted
   `safe-aggregate.json`.

## Privacy and return gate

Keep private:

- task IDs paired with prompts or acceptance checks;
- expected facts, evidence anchors, forbidden claims, and role order;
- source roots, repository secrets, command manifests, stderr, final answers, cited evidence, Codex JSONL, MCP
  calls/results, per-task scorer rows, adapter mapping, private filenames, evidence manifest, and the void
  ledger.

The operator may return only the generated `safe-aggregate.json` and one
`takeover-product-verdict-v1` attestation validated against
`product-verdict-attestation.schema.json`. The attestation's `safe_aggregate_sha256` is the SHA-256 of the exact
safe-aggregate file bytes. The operator derives `product_verdict` by applying the preflight-frozen private role
mapping to the sealed decision without revealing that mapping. The safe file contains only the frozen aggregate
allowlist, public capability IDs and counts, neutral role metrics, five-task-floor subgroups, exact identity
digests, ordinal private-evidence hashes, zero unresolved voids, and the decision verdict.

After privately applying the frozen role mapping, create the attestation:

```bash
"${AGENT_EFFICIENCY_PYTHON:-.venv-agent-efficiency/bin/python}" \
  scripts/bench-agent-efficiency.py attest-product \
  --safe-aggregate "$AGENT_EFFICIENCY_EXPORT/safe-aggregate.json" \
  --product-verdict pass \
  --output "$AGENT_EFFICIENCY_EXPORT/product-verdict-attestation.json"
```

Use `--product-verdict fail` when the sealed decision applied through the private mapping fails Miller. The
command records the operator's assertion that mapping, preflight, rerun, artifact, and void gates are satisfied;
it validates the full decisional aggregate, computes the exact file hash, validates the strict schema, and emits
no mapping field.

Do not return the private scorer aggregate, scorer JSONL, identity/evidence manifests, private filenames, or any
raw row. Do not return the neutral-role mapping artifact. Correlating the two permitted verdicts may make
Miller's neutral role inferable after the run; that bounded post-run disclosure is accepted, but no mapping or
row evidence may cross the boundary. Do not ask the implementation agent to diagnose a sealed task. If the run
cannot finish cleanly under frozen identities, preserve the private state, report only the aggregate blocker,
and do not spend replacement sealed tasks without a new user decision.

## Decision order

Apply correctness first: the candidate must have at least the baseline's stabilized completion count and zero
evidence-critical baseline-pass/candidate-fail tasks. Only then apply the token-or-call efficiency route and the
p75 wall-time guard. A correctness loss cannot be offset by speed, tokens, or a weighted score.
