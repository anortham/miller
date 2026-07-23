# Takeover Evaluator Fixtures

These are synthetic, visible inputs for exercising `retrieval-eval decision-score`. They contain no sealed
tasks, repository paths, prompts, source symbols, or product mapping.

- `tasks.jsonl` defines one relevance-eligible takeover-v1 task with two graded opaque anchors.
- `baseline-results.jsonl` and `candidate-results-pass.jsonl` produce equal relevance and a passing action gate.
- `candidate-results-relevance-fail.jsonl` keeps the action gate passing while failing the relevance gate.

The combined aggregate is private scorer output. Run it through the controller's `finalize-safe` command with
a frozen identity and evidence manifest before moving any decision artifact across the sealed boundary.
