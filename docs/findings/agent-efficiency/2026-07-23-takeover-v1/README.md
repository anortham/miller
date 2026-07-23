# Takeover-v1 visible calibration artifacts

These exports belong to the final visible calibration documented in
[`../../2026-07-23-miller-julie-takeover-v1-visible-calibration.md`](../../2026-07-23-miller-julie-takeover-v1-visible-calibration.md).

- `identity-manifest.json` binds product, snapshot, model, schema, prompt, tokenizer, and environment identities.
- `agent-tasks.jsonl` contains privacy-safe task metadata without prompts or labels.
- `baseline-results.jsonl` and `candidate-results.jsonl` contain normalized scored rows.
- `aggregate.json` is the pure scorer output.
- `safe-aggregate.json` is the recursively allowlisted report.
- `evidence-manifest.json` binds the evidence files by digest.
- `void-status.json` records zero unresolved harness voids.
- `agent-score-command.txt` is the copyable scorer/finalizer command.

Raw prompts, answers, tool transcripts, and prepared snapshots are intentionally excluded.
