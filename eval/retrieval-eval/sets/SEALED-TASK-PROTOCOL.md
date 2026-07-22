# Sealed paired task-completion protocol

This event measures whether the frozen semantic candidate helps agents complete real work, not merely retrieve
graded files. The user owns and coordinates it. Implementation agents receive only the aggregate produced by
`retrieval-eval task-score`; they never receive sealed prompts, acceptance checks, trajectories, task ids,
per-task outcomes, or the blinded arm mapping.

## Freeze before sampling

Record the candidate commit, executable hash, semantic sidecar/model identity, fusion and policy versions,
baseline lexical settings, agent model, system/project instructions, tool surface, token/time/call budgets, and
the repository commits used for clean snapshots. Freeze any secondary retrieval query's `search_mode` before
either arm runs; both arms use the same value.

Create at least 30 tasks spanning at least five distinct `(repo, language-family)` combinations. Include at
least five identifier/path tasks for the safety subset. Prompts and executable acceptance checks are written
and frozen before arm assignment. Hold reserve tasks separately for predeclared infrastructure burns.

The local manifest contains only:

```json
{"task_id":"opaque-random-key","repo":"short-label","language":"language-family","query_profile":"mixed"}
```

`repo` is a non-sensitive label, never a file-system path. `query_profile` is one of `identifier`, `path`,
`short_token`, `prose`, `docs_like`, or `mixed`. Do not put prompts, checks, paths, expected output, or other
sealed content in the manifest.

## Blind and randomize

The user-controlled coordinator assigns neutral arm labels and keeps their baseline/candidate mapping secret
from runners and implementation agents. Randomize arm order independently per task using a recorded seed or
assignment file that remains sealed. Do not run every baseline first or allow a runner to infer the arm from
labels, environment descriptions, or filenames.

Each pair starts from the same clean repository snapshot. Reset generated files, indexes, caches, and task
side effects between arms according to the frozen setup. Use the identical agent model, instructions, tools,
acceptance checks, token budget, time budget, and call budget. Only the pre-frozen retrieval arm configuration
may differ.

The coordinator applies the acceptance check and records one row per arm:

```json
{"task_id":"opaque-random-key","completed":true,"duration_ms":1234,"tool_calls":8,"search_calls":3,"zero_result_search_calls":0}
```

The result row contains no prompt, check output, model response, source path, tool arguments, or trajectory.

## Burn rules

Predeclare infrastructure-burn conditions before the first run: unavailable service, corrupted snapshot,
runner crash before the frozen budget, or acceptance-check malfunction. Mark the entire pair burned before
unblinding; never keep the favorable arm or substitute a reserve task after seeing an outcome. Record only the
burn reason and opaque id in the coordinator's private ledger, then use an untouched reserve task.

If a task prompt, check, trajectory, per-task outcome, arm mapping, or raw input file reaches the repository,
an implementation-agent prompt, a shared transcript, or an aggregate report, retire that task or slice. A
failed product task is not an infrastructure burn. Never rerun failures with tuned settings against the same
sealed slice.

## Score locally

After all complete pairs are frozen, the coordinator unblinds only enough to assign the two aggregate input
files and runs:

```bash
dotnet run --project eval/retrieval-eval -- task-score \
  --tasks <private-manifest.jsonl> \
  --baseline <private-baseline-results.jsonl> \
  --candidate <private-candidate-results.jsonl> \
  --out <private-aggregate.json>
```

The three input files remain user-owned and outside the repository. The scorer rejects unknown fields and
mismatched task-id sets. A valid `pass`, `fail`, or `underpowered` aggregate exits `0`; validation failures
exit `2`.

## Return aggregates only

Return the schema 1 aggregate fields exactly:

- `inputs`: lowercase SHA-256 digests for the task, baseline, and candidate files, with no paths;
- `pair_count` and `completion` (`both_completed`, `candidate_only`, `baseline_only`, `neither_completed`);
- `primary_gate` and `identifier_path_safety`;
- aggregate-only baseline/candidate duration, tool-call, search-call, and zero-result diagnostics;
- `by_repo`, `by_language`, and `by_query_profile` groups emitted only at five or more pairs.

Do not return the manifest, arm files, task ids, input paths, prompts, checks, trajectories, per-task rows, burn
ledger, randomized order, or arm mapping. Implementation agents may diagnose a failed aggregate only with the
visible dev set and new unsealed material. They may not request or inspect sealed rows.

The primary gate passes only with at least 30 complete pairs and a two-sided 95% Wilson lower bound above 0.5
for `candidate_only / (candidate_only + baseline_only)`. Thirty or more pairs with no discordant outcomes is a
failure. Identifier/path safety is underpowered below five pairs and fails when `baseline_only` exceeds
`candidate_only`. Duration and call counts are diagnostics and cannot manufacture a pass.

## Spend once

Treat the returned aggregate as one acceptance event. Record its date, frozen candidate identity, pinned
repository commits, input digests, pair count, burn count, and aggregate verdicts. Any candidate change after
the event requires new visible development evidence and a fresh sealed slice; repeated scoring of the same
tasks would turn them into a dev set.
