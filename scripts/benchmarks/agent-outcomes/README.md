# Agent outcomes v1 corpus

This frozen corpus contains 36 tasks across six independent upstream repositories and six languages. Each repository contributes one location, concept, references, safe-edit, repair, and test-selection task. `command-line-api` and `rake` are whole-repository holdouts; their task labels and hidden verifier inputs must not enter an agent-readable task directory.

`repositories.json` records the immutable upstream commit, canonical source inventory hash, license, dependency-lock state, native qualification command, development/holdout split, and qualification evidence. Canonical hashes use `agent_outcomes_contract.source_inventory`: sorted JSON entries contain `path` and the SHA-256 of regular-file bytes, or `path`, `link_target`, and the SHA-256 of relative symlink-target bytes; `.git` is excluded.

Repair tasks start from disclosed benchmark seed overlays, not from defects claimed to exist in upstream history. Each repository record distinguishes the pristine upstream hash, seed patch hash/path, and resulting task snapshot hash. Seed, reference, wrong, and verifier artifacts remain under the host-only `verifiers/` tree and are never copied into an agent input. This synthetic-seed design tests repair behavior reproducibly but does not measure naturally occurring upstream bug discovery.

Every mutation task was replayed in disposable copies in three states: seeded/pristine baseline failed, the reference patch passed, and a plausible wrong patch failed. The verifier runs repository-native behavior through an explicit isolated-executor seam. `prepared-environments.json` defines dependency setup, environment variables, and any unmeasured pre-test restore. The measured `test_argv` never installs or resolves dependencies. Build output stays in disposable candidates or the declared prepared directory, never in a frozen source inventory.

Read-only grading is bounded and structured. Concept prompts describe behavior without naming the target symbol and name factual response facets without disclosing their values. The verifier checks typed boolean, string, and string-list facts plus source evidence. String lists are unique unordered sets, so private label order cannot reject a correct answer. Location and reference labels use native paths and symbols. Flask references exercise a legitimate empty result for an absent homonym, and the Rake reference task refuses to invent proprietary source. Incorrect homonyms, extra facts, omitted references, and a different case in the same test file are negative examples.

Each test-selection prompt contains a concrete frozen diff. `selection_replay.py` ran each repository's normal native suite before and after that diff, parsed machine-readable results where the runner provides them, and compared complete outcome maps. The runs executed 494 Flask, 1,260 Express, 290 chi, 1,228 ripgrep, up to 920 command-line-api, and 606 Rake cases. Their frozen inventories contain 494, 1,253, 290, 1,228, 904, and 601 unique native path/ID pairs. Express, xUnit, and test-unit repeat some identical display identities. Parameterized xUnit display names remain intact. The C# comparison repeats both states, unions cases omitted by unstable solution projects, and accepts only pass/pass to fail/fail transitions as affected.

The six repository test commands were qualified before task authoring. Flask, Express, chi, ripgrep, and rake passed their recorded commands. The selected command-line-api commit passed its focused SplitCommandLine suite under `DOTNET_ROLL_FORWARD=Major`; its full suite had one host signal-code assertion mismatch (expected 42, observed 130), so the manifest records the focused command and the limitation instead of claiming a clean full-suite result. Express and rake had no committed dependency lock at the pinned commits; qualification resolved dependencies in disposable copies.

Validate the checked-in corpus without upstream source or network access:

```bash
python3 -B -m unittest discover -s scripts/tests -p 'test_agent_outcomes_corpus.py'
```

Replay the exact mutation and selection evidence with:

```bash
python3 -B scripts/benchmarks/agent-outcomes/verifiers/replay.py \
  --sources-root /path/to/pinned-sources \
  --evidence-root /path/outside/the-agent-sandbox
```

Pass `--update-checked-evidence` only while authoring a new frozen corpus. The round-one review evidence is outside Git at `/home/murphy/source/miller-evidence/agent-outcomes-task4-round1`; `external-evidence.json` records the replay-file paths and digests. The future runner must prepare dependencies before measured verification and keep that directory, verifier labels, and held-out assertions outside the agent sandbox. Corpus authoring necessarily inspected both development and held-out source; no treatment-model results were inspected or used to tune labels.
