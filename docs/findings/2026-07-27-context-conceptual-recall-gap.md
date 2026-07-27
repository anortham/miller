# Context: conceptual queries can miss the answering symbol entirely (recall, not ranking)

**Date:** 2026-07-27
**Status:** diagnosed and measured. No ranking change implemented — the proposal below is **not accepted**.
**Related fix:** `b2e72137` made the disposition honest on this exact bundle; it did not change ranking.

## What was observed

Dogfooding the live build, `context` answered a conceptual question with four pivots, none of which
were the answer, and (before `b2e72137`) reported `evidence=sufficient`.

Query:

```
how does a derived sidecar prove which extract generation it was built from
```

The answer is `SymbolsArtifactIdentity` (`src/Miller.Indexing/SymbolsArtifactIdentity.cs`) and its
`MatchesArtifact` / `Unprovable` members, plus the readers that gate on them.

Returned pivots:

| reason | symbol | file |
| --- | --- | --- |
| `query_rank_1` | `SIDECAR_EXTRACT` | `scripts/restore-semantic-sidecar.sh:379` |
| `query_term_prove` | `MatchesArtifact_UnreadableArtifact_RefusesBecauseItCannotProveTheGeneration` | `tests/Miller.Tests/Indexing/SymbolsArtifactIdentityTests.cs:9` |
| `query_term_prove` | `prove_cpu_backend` | `eval/sidecar-conformance/generate.py:121` |
| `query_term_extract` | `VEC_EXTRACT` | `scripts/restore-semantic-sidecar.sh:384` |

Two shell variable assignments, one Python eval helper, one C# test method. Zero non-test C#
implementation symbols, in a repo where the answer is a well-named C# class. Neighbours were more of
the same files. `SymbolsArtifactIdentity` appeared only as the *subject of the tests* that ranked.

## The diagnosis: this is a recall gap, not a ranking gap

The first framing of this finding called it bad ranking. That is wrong, and the distinction decides
which fixes can possibly work.

`SymbolsArtifactIdentity` is **not in the lexical candidate set at all**. Verified by running symbol
search directly against the live index, for the full query and for each query term independently:

| query | top symbol hits |
| --- | --- |
| full phrase | `SIDECAR_EXTRACT`, `sidecarExtract`, `FixedInput`, `ProseMarkers`, `Promote`, … |
| `sidecar` | `sidecar`, `Sidecar`, `Sidecar` |
| `prove` | `prove_cpu_backend`, `backend_proof`, `MatchesArtifact_UnreadableArtifact_…` |
| `extract` | `SIDECAR_EXTRACT`, `VEC_EXTRACT`, `ExtractOp` |
| `generation` | `UnknownGeneration`, `BindingGeneration`, `OnlyReadyGeneration` |
| `derived` | (three test methods) |
| `built` | `BuiltRevision`, `_ensureSearchBuilt`, `BuiltArtifactId` |

`SymbolsArtifactIdentity` appears in none of them. Symbol search covers `name + signature` only
(deliberate — see CLAUDE.md), and this symbol's tokens are `symbols` / `artifact` / `identity`, which
share nothing with `derived` / `sidecar` / `prove` / `extract` / `generation` / `built`. The language
that matches the question lives in its doc comments, which are correctly outside symbol ranking.

**Consequence: no re-scoring, re-weighting, or diversity change can fix this query.** A ranker cannot
promote a candidate that was never retrieved. Any fix must change what enters the candidate set.

## Real secondary defects found while diagnosing

These are independently wrong and would matter on other queries, but none of them recovers this one.

1. **Path affinity outranks name affinity.** `TaskQueryAffinity`
   (`src/Miller.Server/Tools/ContextTool.cs:1163-1193`) scores a kind match 15, a **name** match 10,
   a signature match 5, and a **path** match 12. A symbol earns more for living in
   `eval/sidecar-conformance/` than for having the word in its own name. That ordering looks
   inverted; `prove_cpu_backend` and `VEC_EXTRACT` both benefit from it.

2. **Per-term rescue competes on equal footing with the full query.** Both the full-query arm
   (`ContextTool.cs:830`) and the per-term rescue arm (`ContextTool.cs:854`) pass
   `TaskQueryAffinity` as `AnchorStrength`, and `ContextPivotRanker.Rank`
   (`src/Miller.Core/Graph/ContextPivotRanker.cs:58-63`) orders by `AnchorStrength` **before**
   `RetrievalRank`. Each per-term search also restarts its rank at 1. So a hit found only because of
   one common term can outrank the full query's intent, and `AnchorStrength` conflates ordinary
   lexical overlap with genuine anchors such as edited files and stack frames.

3. **Test filtering is asymmetric between the two arms.** Both pass `excludeTests: null`, which means
   "auto-hide for natural-language queries". The full phrase qualifies as natural language and hides
   tests; a one-word rescue query does not, so tests re-enter through the rescue arm only. That is
   why a test method is a pivot here at all.

Note that `exclude_tests` cannot be repurposed to suppress them: `contracts/context-json-v1.md`
scopes it to usage enrichment and states it does not alter lexical pivot selection.

## The one lexical bridge that exists

`MatchesArtifact_UnreadableArtifact_RefusesBecauseItCannotProveTheGeneration` matches on `prove` and
`generation`, and its **only** resolved one-hop target in the artifact is
`SymbolsArtifactIdentity.Unprovable`:

```sql
SELECT t.name, t.kind FROM identifiers i
JOIN symbols t ON t.symbol_id = i.target_symbol_id
WHERE i.containing_symbol_id = (SELECT symbol_id FROM symbols
  WHERE name = 'MatchesArtifact_UnreadableArtifact_RefusesBecauseItCannotProveTheGeneration');
-- Unprovable|method
```

Exactly one non-test subject, resolved by the extractor rather than guessed from the test's name.
This is the only route from the retrieved set to the correct answer.

## Proposal (NOT accepted — recorded for a later dedicated pass)

Direction suggested by a Codex second opinion on 2026-07-27, verified against the source above:

- **Test-subject promotion.** For a *term-rescue* test hit on a query with no test intent, follow
  one-hop resolved forward edges. If they identify exactly one non-test subject, promote that symbol
  (or its container when container identity is also strongly supported) into the pivot set in place
  of the raw test, inheriting the test's lexical evidence under a distinct reason such as
  `query_term_<term>_subject`. If more than one plausible subject exists, keep the test and do not
  guess. This would run in `BuildCandidates` before `ContextPivotRanker.Rank`, because graph
  expansion currently happens *after* the four pivots are finalized, so a reached implementation can
  only ever become a neighbour.
- **Give term rescue its own evidence tier**, below full-query retrieval, instead of sharing the
  `AnchorStrength` namespace.
- **Make term rescue inherit the original query's auto-test policy**, closing defect 3.
- **Reconsider the path-vs-name weighting**, closing defect 1.

### Cost this proposal actually carries

It is not a small change. It adds a pivot tier, a new reason string, and a graph lookup inside
candidate building, and it changes `contracts/context-json-v1.md`. The load-bearing cost is the
language-parity rule: one-hop resolution quality would have to be verified on a real extract for
every language julie-extractors supports, not just C#, before it could be trusted. A test-subject
promotion that silently works well only for C# would be exactly the kind of authoritative-looking
partial feature CLAUDE.md forbids.

### Explicitly rejected directions

- Folding doc comments, string literals, or broad source text into **symbol** ranking. Symbol search
  stays `name + signature`; the explicit `mode=source` / `mode=content` / `regions=` paths and the
  content corpus exist for that text.
- Requiring the semantic arm. ADR-0003 makes it optional and off-switchable, and `MILLER_SEMANTIC=off`
  must stay a zero-work guarantee with byte-identical lexical output. Whatever ships must work
  lexically.
- Globally excluding constants, tests, `scripts/`, Python, or `eval/` from pivots. `scripts/` can hold
  authoritative packaging behavior and `eval/` is authoritative for benchmark questions; many
  ecosystems have no `src/`, so a "prefer src" prior violates language parity.
- Suppressing the test hit. It is the only lexical bridge to the answer here; removing it makes this
  query strictly worse.
- Stopwording `prove` / `extract`, or relying on IDF alone. Neither adds a missing candidate.
- Raising the four-pivot cap. It spends more budget on the same bad candidates.

## Current mitigation

`b2e72137` stopped `context` from calling this bundle `evidence=sufficient`. A `constant`, `variable`,
`field`, or `property` pivot body is a declared value, not an implementation, so it can no longer
reach `sufficient`; the bundle now reports `partial` with reason `pivot_value_declaration_only` and
emits `next_actions`. That makes the recall gap visible to the agent instead of hiding it behind a
stop-looking signal. It does not close the gap.
