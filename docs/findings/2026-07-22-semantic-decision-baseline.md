# Semantic decision corrected baseline — 2026-07-22

**Verdict: the corrected visible replay materially strengthens the case for semantic retrieval, but it is not promotion evidence.** Production removed all three visible zero-result rows, improved cluster-unit recall and nDCG, recovered every intent cluster, raised markdown recall from 0.25 to 0.75, and preserved all 16 identifier ranked lists exactly. The negative false-positive diagnostic remains 1.0, cold one-shot latency remains about 4x lexical, and no sealed paired task or powered decision-canary result exists yet.

## Frozen inputs

- Candidate: Miller `1.13.0+26dc98d287d7`, commit `26dc98d287d7`.
- Miller corpus: clean archive of `97485d4f0ba8d3a03c8893fe39405a8e77a90b86`.
- Julie corpus: clean archive of `0744b93013ca3eea374c78064a4d0f054cedc99a`.
- Excluded from both archives: `eval/`, `.razorback/`, and `.claude/`.
- Frozen set: 82 rows, 38 distinct document references, zero missing references.
- Frozen routing: 78 `auto` rows and four `content` rows; every arm used the same per-row mode.
- Randomized canary: off for every replay arm.
- Encoder: pinned BGE fingerprint `sha256:3e8b7e8a0890dc84f702db1d13c47e312501905ee9d1aafb772bdc803616d7f4`, lane `vec0-int8-384-cosine-v1`, corpus `cards-v1-chunks-v1`, fusion `fusion-v1`.

The fresh Miller corpus converged 8,063 symbol cards and 724 chunks into a 7,634,944-byte `vectors.db`. The fresh Julie corpus converged 8,752 cards and 490 chunks into an 8,073,216-byte `vectors.db`. Both symbol and chunk cursors reached revision 1 with no errors.

## Corrected retrieval result

| Arm | recall@10 | nDCG@10 | macro recall | macro nDCG | markdown recall | markdown nDCG | intent clusters | zero rows | negative FPR |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lexical | 0.5365 | 0.5054 | 0.4583 | 0.4358 | 0.2500 | 0.2500 | 11/14 | 3/82 | 1.0000 |
| semantic | 0.6997 | 0.6409 | 0.7134 | 0.6288 | 0.7500 | 0.5967 | 14/14 | 0/82 | 1.0000 |
| production | 0.6892 | 0.6434 | 0.7058 | 0.6306 | 0.7500 | 0.5967 | 14/14 | 0/82 | 1.0000 |

Production versus lexical improved cluster-unit recall by `+0.1528` and nDCG by `+0.1379`. It recovered all three missed intent clusters and all three visible zero-result rows. All arms emitted exactly 82 result rows with zero missing or unknown ids.

Semantic contribution was observable rather than inferred from the headline score: production changed 58 ranked lists and 44 top results, added at least one judged-relevant document on three queries, lost no judged-relevant document that lexical had retrieved, and rescued three lexical-zero rows.

The four markdown rows now exercised explicit content search instead of JSON auto-symbol routing. Production retrieved three of four judged documents: `docs/release-process.md` and the Julie in-process-leader plan at rank 1, plus `ADR-0001-guidance-delivery-channels.md` at rank 5. The Julie daemon-teardown document remains the one judged markdown miss.

## Safety and diagnostics

- Production and lexical returned byte-for-byte identical ranked lists for all 16 identifier queries. Both scored identifier recall and nDCG `1.0000`.
- The forced semantic symbol arm retained identifier recall `1.0000` but its nDCG was `0.9764`; this does not affect production because identifier policy remains lexical.
- Every negative row still returned results in every arm. This is a report-only diagnostic because Miller has no abstention contract; it cannot be converted into a semantic promotion failure after the fact.
- The visible set is not a causal task-completion event. Its quality lift cannot substitute for the sealed paired task gate.

## Cold latency

One-shot CLI wall time includes process and semantic-session startup and is not the authoritative warm-server canary gate.

| Arm | p50 | p95 | max |
| --- | ---: | ---: | ---: |
| lexical | 160.991 ms | 195.558 ms | 215.975 ms |
| semantic | 736.845 ms | 786.228 ms | 805.562 ms |
| production | 742.583 ms | 782.215 ms | 797.714 ms |

The cold production p95 is `3.999x` lexical. Promotion still requires the raw local canary's warm p95 ratio at or below `1.20`; aggregate latency buckets are only a screen.

## Decision impact

This replay resolves the prior zero-markdown incident and proves the current production route extracts real cross-surface value from semantic data. Removing semantic immediately would discard measurable quality and zero-result improvements. Promotion remains blocked until every frozen gate passes: powered v3 canary success, authoritative warm latency, identifier shadow, sealed paired task lift, and cost/reliability limits. Day 30 remains the forced remove-or-promote decision; underpowered is not a pass.

Raw results, reports, latency rows, hashes, corpus facts, and summaries are under [`eval/retrieval-eval/out/semantic-decision-baseline-2026-07-22/`](../../eval/retrieval-eval/out/semantic-decision-baseline-2026-07-22/).
