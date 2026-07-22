# Visible agent-efficiency evidence

- `baseline/` is the pre-repair paired run at Miller commit `072f1f1779e056a63e8ce9e0ffcd9294baa31d07`.
- `candidate/` is the frozen post-repair paired run at Miller commit `1f4724f11ca97aa46388702fd4782c23738d7682`.
- Each run contains the privacy-safe controller exports, frozen identities, scorer input rows, exact scorer command, and aggregate.
- Raw Codex transcripts remain outside the repository; `miller-loss-classification.json` retains the bounded trajectory evidence used by the finding.
- `semantic-model-diagnostic.json` records the identical-corpus BGE-small/CodeRankEmbed isolation.
- `context-budget-replay.json` records direct post-repair rendering checks.
- `reviewed-candidate.json` records the byte-equivalent post-review candidate that the sealed operator must build and identify.
