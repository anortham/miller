# Semantic-noise experiment — eight B-frozen repetitions

Eight reruns of the 2026-08-25 B-frozen configuration (miller-1.22.1-semantic-off baseline vs
miller-1.22.1-semantic-on candidate, frozen budgets 8/12k/120s, seed 731, identity B), run overnight
2026-08-25→26 with zero voids. Rep 1 is the original B-frozen run in
`../2026-08-25-bare-agent-v1.22.1/B-frozen/`. This directory keeps each rep's scored
`aggregate.json` and `safe-aggregate.json`; the full raw exports are reproducible from
`scripts/bench-agent-efficiency.py` with the recorded identities. The analysis and verdict (mean
on−off delta −0.2 tasks, SD 1.9 — noise) are in
`../../2026-08-25-miller-vs-bare-agent-v1.22.1-calibration.md`.
