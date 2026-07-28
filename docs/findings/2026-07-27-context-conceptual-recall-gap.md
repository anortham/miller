# Context: conceptual queries can miss the answering symbol entirely (recall, not ranking)

**Date:** 2026-07-27
**Status:** **implemented on branch `codex/context-conceptual-recall`** (plan
[`docs/plans/2026-07-27-context-conceptual-recall-plan.md`](../plans/2026-07-27-context-conceptual-recall-plan.md)).
Live re-verified before work at commit `b759d2a4` / index rev 116; post-fix dogfood recorded below.
**Related prior fix:** `b2e72137` made disposition honest on value-declaration pivots (still in place).

## What was observed (baseline)

Dogfooding the pre-fix build, `context` answered a conceptual question with four pivots, none of which
were the answer, and (before `b2e72137`) reported `evidence=sufficient`.

Query:

```
how does a derived sidecar prove which extract generation it was built from
```

The answer is `SymbolsArtifactIdentity` (`src/Miller.Indexing/SymbolsArtifactIdentity.cs`) and its
`MatchesArtifact` / `Unprovable` members, plus the readers that gate on them.

Baseline pivots (pre-fix):

| reason | symbol | file |
| --- | --- | --- |
| `query_rank_1` | `SIDECAR_EXTRACT` | `scripts/restore-semantic-sidecar.sh:379` |
| `query_term_prove` | `MatchesArtifact_UnreadableArtifact_…` (test) | tests… |
| `query_term_prove` | `prove_cpu_backend` | `eval/sidecar-conformance/generate.py:121` |
| `query_term_extract` | `VEC_EXTRACT` | `scripts/restore-semantic-sidecar.sh:384` |

## Diagnosis (still accurate)

`SymbolsArtifactIdentity` is not in the **lexical symbol** candidate set (name + signature only). Ranking
alone cannot invent it. Fixes must change the candidate set (content rescue, semantic seeds, test-subject
promotion) and/or ranking tiers.

## What shipped

| Slice | Behavior |
| --- | --- |
| Ranking | Name weight ≥ path (12 vs 8); term rescue strength cap 18; term arm inherits parent NL auto-hide-tests |
| next_actions | Value-only pivots lead with `search mode=source`; inspect prefers implementation kinds |
| Source rescue | NL queries admit ≤3 `source_rescue_N` pivots at strength 35 from content/source corpus |
| Semantic seeds | Strength 26 when served; still non-authoritative |
| Test-subject promotion | Exactly one exact non-test outgoing → `query_term_<term>_subject` |
| Disposition | Discovery implementation body → `partial` / `discovery_implementation_present` (not masked by value siblings) |

## Post-fix dogfood (`MILLER_SEMANTIC=off`, workspace miller root)

Built miller from the feature branch against the live main workspace index:

| role | reason | symbol |
| --- | --- | --- |
| pivot | `source_rescue_1` | `OpenRequired` (`SymbolSearchSidecar.cs`) |
| pivot | `query_rank_1` | `SIDECAR_EXTRACT` |
| pivot | `source_rescue_2` | `SymbolSearchSidecar` |
| pivot | `query_term_prove` | `prove_cpu_backend` |

- Disposition: `partial` / `discovery_implementation_present` (after disposition fix; was wrongly
  `pivot_value_declaration_only` when source-rescue methods sat next to shell constants).
- `next_actions`: inspect on implementation pivots + `search mode=source` recovery.
- **Residual:** exact answer symbols (`SymbolsArtifactIdentity` / `MatchesArtifact` / `Unprovable`) are
  not always top pivots; content rescue maps chunks to **containing** symbols in
  `SymbolSearchSidecar`, which document the generation contract and reference the answer type. That is a
  large improvement over baseline (scripts/eval only) but not perfect name-level recall for this query.
- Test-subject promotion may not fire when parent NL auto-hide drops the test before a promotion-only
  re-scan finds it, or when exact outgoing density is low for that hit.

## Explicit non-goals (unchanged)

No doc text in symbol ranking; no required semantic; no global bans of tests/scripts/constants; no
raising the four-pivot cap as a substitute.

## Validation log (baseline + post-fix)

See plan verification ledger on the feature branch. Baseline SQL one-hop still holds: resolved target
from the long test name is only `Unprovable`.
