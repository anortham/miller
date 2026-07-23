# Miller/Julie takeover-v1 visible calibration — 2026-07-23

## Decision

The takeover evaluator is ready for affected-subset replay and later sealed use. Miller is not ready to replace Julie.

The final visible calibration gave Julie three correct tasks and Miller two. Julie led relevance, wrong-action rate, and the only measurable efficiency comparison. Miller's exact-symbol workflow beat Julie, but Miller lost the config/document and captured-output workflows and repeatedly exhausted budgets through unbounded tool responses.

This is calibration evidence, not a retirement verdict. The sealed lane remains unspent until Phase 10 because spending the operator-owned tasks before remediation would violate the spend-once design.

## Frozen identity

- Contract: `takeover-evaluation-v1`
- Run identity: `5de9199cbdfdc7db3333e68d3767eca283630ca8a3794e59d3f8e12b57d71370`
- Prompt contract: `abb1cb10275caea25f89e00821dcca953ab25026da0a2a6850e43bd0fb8f6b39`
- Corpus: 15 visible tasks, all 13 takeover capabilities, five clean source snapshots
- Execution: 30 initial arms plus 12 symmetric disagreement arms, zero unresolved voids
- Agent: Codex `gpt-5.6-sol`, medium reasoning, seed `731`
- Julie: `7.16.0`, commit `27d39714339778b18f412c6a5f1110de1257dcd3`
- Miller: `1.13.0+6593fe6e36f5`, commit `6593fe6e36f5025464bfd39642520a564ef8da4a`
- Miller code artifact SHA-256: `f6beb8eb883135b44b9a6b0d5f5f4f12d7e6eb7081957c924a065964936d78f2`
- Aggregate SHA-256: `0319531aef481cb692124d16022a594cfdaa015e45f049932b8d17da1871e74f`
- Safe aggregate SHA-256: `68a3aa8eae424d5fd25454cbe41b320cb0e7c0f17228f9f30336a81e12e777b9`
- Evidence manifest SHA-256: `c42725fe224e4560995405085fec46df3e7a90b7f88980ee9ee8a1d8dec5266a`

The committed exports are under
[`agent-efficiency/2026-07-23-takeover-v1/`](agent-efficiency/2026-07-23-takeover-v1/).

## Gate result

| Measure | Julie baseline | Miller candidate | Result |
| --- | ---: | ---: | --- |
| correct tasks | 3 | 2 | correctness fail |
| correct empty results | 1 | 1 | tie |
| hard-error tasks | 8 | 8 | tie at an unacceptable level |
| wrong-answer tasks | 4 | 5 | Julie |
| wrong-action task rate | 20.0% | 26.7% | Julie |
| recall@6 | 15.38% | 7.69% | relevance fail |
| nDCG@6 | 15.38% | 7.69% | relevance fail |
| MRR | 15.38% | 7.69% | relevance fail |
| top-1 | 15.38% | 7.69% | relevance fail |

Completion cells were:

- both correct: `1`
- Julie only: `2`
- Miller only: `1`
- neither correct: `11`

The sole both-correct task was the expected-empty lookup `dev-014`. Julie used one call, 1,117 tool-output tokens, and 14.3 seconds. Miller used three calls, 1,749 tokens, and 30.9 seconds. The call, token, and wall-time routes therefore all failed.

## Exact product differences

### Miller advantage

`dev-002` asked for an exact factory and all accepted candidate identifiers. Miller succeeded in all three repetitions using exact symbol identity. Julie failed all three because it produced file inspection rather than the required exact-symbol action.

This validates Miller's symbol-ID-first direction. Exact identity should remain the basis for Phase 1 reference evidence and later rename safety.

### Julie advantages

`dev-006` asked for package-script/config facts. Julie succeeded in all three repetitions. Miller found the JSON symbol but continued to represent the result as `inspect_symbol` instead of the documented file/config action.

`dev-013` asked for captured command-output evidence. Julie succeeded in all three repetitions. Miller produced two budget failures and one wrong answer after routing through broad source/file/content search and a test symbol rather than bounded captured-output retrieval.

On `dev-014`, both products correctly reported no matching symbol, but Julie needed one call while Miller needed three.

## Miller failure causes

### Unbounded output

The largest Miller responses were sufficient to consume or force exhaustion of the entire 12,000-token arm budget:

| Task | Response | Characters |
| --- | --- | ---: |
| `dev-004` | `workspace health` | 110,109 |
| `dev-008` | `inspect summary` | 68,434 |
| `dev-013` | broad source search | 57,753 |
| `dev-013` | file search | 37,668 |
| `dev-005` | `patterns search` | 27,000 |
| `dev-008` | context bundle | 22,731 |
| `dev-013` | content search | 19,030 |
| `dev-007` | `inspect summary` | 18,853 |
| `dev-003` | context bundle | 17,745 |
| `dev-007` | context bundle | 15,913 |

`dev-003`, `dev-011`, and `dev-015` also exhausted the eight-call budget. `dev-007` crossed the token ceiling in six calls. Limits on result rows are not deterministic output budgets.

### Relationship workflows

`dev-009` found the target and invoked caller tracing, but the final answer still lacked both required canonical reference-site actions and added unrelated symbol inspections.

`dev-010` found the caller but did not produce the required callee, call-path, and canonical reference-site evidence.

These are direct manifestations of the audited reference defect: Miller can expose nearby names and graph rows without reliably carrying exact resolved target identity through the agent workflow.

### Guidance and routing

`dev-001` found the correct rename target but omitted the Windows helper name and included extra inspections.

`dev-006` routed a config/document fact through symbol inspection.

`dev-012` selected both correct tests with the correct typed actions, but its answer did not cite the required evidence or report the exact `DRIFT DETECTED` fact.

`dev-013` routed captured output through broad workspace search rather than bounded content/log retrieval.

## Evaluator stabilization record

Earlier runs remain immutable and are not product evidence:

| Candidate | Disposition |
| --- | --- |
| `800f7cc` | Codex schema/API void; no product evidence |
| `67d9b59` | scorer/export invariant failure |
| `1106d42` | hidden typed-target contract produced invalid answers |
| `c582752` | prompt-bound run exposed one hidden nested reference rule |
| `5bad5d9` | fully executable, but evidence-name and minimal-action rules were not model-facing |
| `5a9fd24` | focused replay exposed full-object action matching that rejected allowed grounded path metadata |
| `8290d7e` | canonical matching fixed; full run exposed an undocumented action-kind ontology |
| `6593fe6` | final aligned prompt, schema, verifier, matching, and product identities |

The evaluator was hardened without changing task facts, accepted product answers, relevance anchors, budgets, or scoring thresholds. Fixes exposed common rules equally to both roles or canonicalized semantically identical typed identities.

## Phase consequence

Phase 0 is complete for visible calibration, subset replay, safe aggregation, and sealed-lane contract enforcement. The literal operator-owned sealed run remains intentionally unspent.

Proceed in dependency order:

1. exact symbol-ID reference evidence;
2. typed diagnostics and deterministic output budgets;
3. reference-consumer and rename migration;
4. search, context, impact, and remaining-surface improvements;
5. all-language extraction coverage and RC3 validation;
6. fresh Claude review for each affected tool as its implementation pass completes;
7. sealed paired decision, all nine repeated tool reviews, and the broad final Claude review.

The current evidence does not justify replacing BGE-small with CodeRankEmbed. The dominant losses are output size, routing, evidence composition, and exact relationship identity. Model selection remains a later controlled comparison after those workflows are corrected.
