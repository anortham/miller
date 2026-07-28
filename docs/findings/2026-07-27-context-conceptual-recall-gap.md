# Context: conceptual queries can miss the answering symbol entirely (recall, not ranking)

**Date:** 2026-07-27
**Status:** diagnosed, measured, and **live re-verified 2026-07-27** (commit `b759d2a4`, index rev 116).
No ranking or candidate-set change implemented yet. Full implementation plan:
[`docs/plans/2026-07-27-context-conceptual-recall-plan.md`](../plans/2026-07-27-context-conceptual-recall-plan.md) (**pending approval**).
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

Returned pivots (**reproduced live 2026-07-27**, byte-identical reasons/files/lines):

| reason | symbol | file |
| --- | --- | --- |
| `query_rank_1` | `SIDECAR_EXTRACT` | `scripts/restore-semantic-sidecar.sh:379` |
| `query_term_prove` | `MatchesArtifact_UnreadableArtifact_RefusesBecauseItCannotProveTheGeneration` | `tests/Miller.Tests/Indexing/SymbolsArtifactIdentityTests.cs:9` |
| `query_term_prove` | `prove_cpu_backend` | `eval/sidecar-conformance/generate.py:121` |
| `query_term_extract` | `VEC_EXTRACT` | `scripts/restore-semantic-sidecar.sh:384` |

Disposition live: `partial` / `pivot_value_declaration_only` (the post-`b2e72137` honest path).
`next_actions` only suggests `inspect` on those four pivots — not `search mode=source` and not the
answering symbol.

Two shell variable assignments, one Python eval helper, one C# test method. Zero non-test C#
implementation pivots, in a repo where the answer is a well-named C# class. Neighbours were more of
the same files (`file_neighbour` only in the live bundle). `SymbolsArtifactIdentity` appeared only
as the *subject of the tests* that ranked.

## The diagnosis: this is a recall gap, not a ranking gap

The first framing of this finding called it bad ranking. That is wrong, and the distinction decides
which fixes can possibly work.

`SymbolsArtifactIdentity` is **not in the lexical symbol candidate set at all**. Re-verified with
`search mode=symbol retrieval=lexical` against the live index, for the full query and for each query
term independently:

| query | top symbol hits (lexical) |
| --- | --- |
| full phrase | `SIDECAR_EXTRACT`, `sidecarExtract`, `FixedInput`, `ProseMarkers`, `Promote`, … |
| `sidecar` | `sidecar`, `Sidecar`, `Sidecar` |
| `prove` | `prove_cpu_backend`, `backend_proof`, `MatchesArtifact_UnreadableArtifact_…` |
| `extract` | `SIDECAR_EXTRACT`, `VEC_EXTRACT`, `ExtractOp` |
| `generation` | `UnknownGeneration`, `BindingGeneration`, `OnlyReadyGeneration` |
| `derived` | (test methods only) |
| `built` | `BuiltRevision`, `_ensureSearchBuilt`, `BuiltArtifactId` |

`SymbolsArtifactIdentity` appears in none of them. Symbol search covers `name + signature` only
(deliberate — `SearchableDocument` / Decision D3; see CLAUDE.md), and this symbol's tokens are
`symbols` / `artifact` / `identity`, which share nothing with `derived` / `sidecar` / `prove` /
`extract` / `generation` / `built`. The language that matches the question lives in its doc comments
and remarks (`MatchesArtifact` documents "derived sidecar", "generation", "prove"), which are
correctly outside symbol ranking.

**Consequence: no re-scoring, re-weighting, or diversity change can fix this query on the lexical
symbol path.** A ranker cannot promote a candidate that was never retrieved. Any lexical fix must
change what enters the candidate set.

### Related channels (re-checked live)

These do **not** change the lexical-symbol diagnosis, but they matter for choosing a fix:

1. **`search mode=source retrieval=lexical`** hits `SymbolSearchSidecar` source/docs that name the
   extract-generation / derived-sidecar problem and reference `SymbolsArtifactIdentity.Matches`. The
   prose already exists in the content corpus; `context` simply never consults it when building pivots.
2. **Hybrid symbol search** (`retrieval=auto`, semantic on) *can* surface `MatchesArtifact` /
   `ReadSymbolsIdentity` via `semantic_rank_*` with zero lexical score. That is search fusion, not
   context pivot selection.
3. **Context semantic seeds** exist (`LoadSemanticSeeds` → `semantic_rank_N` with `AnchorStrength = 0`).
   For this query, admission should allow expansion (top/runner-up lexical scores ≈16.4 / 14.5,
   ratio &lt; 1.25 → `RerankAndExpand`). Even so, seeds lose pivot slots because
   `ContextPivotRanker` orders by `AnchorStrength` first and every lexical pivot here has
   `TaskQueryAffinity` ≫ 0 (e.g. `SIDECAR_EXTRACT` ≈30, term-rescue hits ≈22–30). Semantic seeds
   stay at 0 and never displace the four junk pivots. Requiring semantic is still forbidden for
   `MILLER_SEMANTIC=off`; the point is that **today's context ranking also blunts semantic when on**.

## Real secondary defects found while diagnosing

These are independently wrong and would matter on other queries, but none of them recovers this one
on the lexical symbol path alone.

1. **Path affinity outranks name affinity.** `TaskQueryAffinity`
   (`src/Miller.Server/Tools/ContextTool.cs:1163-1193`) scores a kind match 15, a **name** match 10,
   a signature match 5, and a **path** match 12. Arms are exclusive per term (name wins over path when
   both match on that term), but a path-only term scores 12 while a name-only term scores 10 — so a
   symbol can earn more for living under a path token than for carrying the word in its own name.
   Path-heavy hits under `eval/sidecar-conformance/` benefit.

2. **Per-term rescue competes on equal footing with the full query.** Both the full-query arm
   (`ContextTool.cs:830`) and the per-term rescue arm (`ContextTool.cs:854`) pass
   `TaskQueryAffinity` as `AnchorStrength`, and `ContextPivotRanker.Rank`
   (`src/Miller.Core/Graph/ContextPivotRanker.cs:58-63`) orders by `AnchorStrength` **before**
   `RetrievalRank`. Each per-term search also restarts its rank at 1. So a hit found only because of
   one common term can outrank the full query's intent, and `AnchorStrength` conflates ordinary
   lexical overlap with genuine anchors such as edited files and stack frames.

3. **Test filtering is asymmetric between the two arms.** Both pass `excludeTests: null`, which means
   "auto-hide for natural-language queries" (`SearchTool.ResolveExcludeTests`: phrase ⇔ ≥2 words).
   The full phrase qualifies as natural language and hides tests; a one-word rescue query does not,
   so tests re-enter through the rescue arm only. That is why a test method is a pivot here at all.

Note that the MCP/CLI `exclude_tests` parameter cannot be repurposed to suppress them:
[`docs/contracts/context-json-v1.md`](../contracts/context-json-v1.md) scopes it to usage enrichment
and states it does not alter lexical pivot selection when reference enrichment is off.

## The one lexical bridge that exists

`MatchesArtifact_UnreadableArtifact_RefusesBecauseItCannotProveTheGeneration` matches on `prove` and
`generation`. Re-queried against live `.miller/symbols.db` (schema uses `symbol_id` / `path`, not
`id` / `file_path`):

```sql
SELECT t.name, t.kind, t.path
FROM identifiers i
JOIN symbols t ON t.symbol_id = i.target_symbol_id
WHERE i.containing_symbol_id = (
  SELECT symbol_id FROM symbols
  WHERE name = 'MatchesArtifact_UnreadableArtifact_RefusesBecauseItCannotProveTheGeneration'
  LIMIT 1
);
-- Unprovable|method|src/Miller.Indexing/SymbolsArtifactIdentity.cs
```

Resolved targets: **exactly one** — `Unprovable`. Unresolved identifier rows also mention
`MatchesArtifact` (call) and `SymbolsArtifactIdentity` (variable_ref) with null
`target_symbol_id`; promotion must not invent those. Exactly one non-test **resolved** subject,
resolved by the extractor rather than guessed from the test's name. This is the only route from the
**already-retrieved** set to the correct answer. It is not the only possible lexical route into that
answer from the *corpus* (source/doc text already contains the query's language).

## Proposal (NOT accepted — recorded for a later dedicated pass)

Direction suggested by a Codex second opinion on 2026-07-27, verified against the source above:

- **Test-subject promotion.** For a *term-rescue* test hit on a query with no test intent, follow
  one-hop **resolved** forward edges. If they identify exactly one non-test subject, promote that
  symbol (or its container when container identity is also strongly supported) into the pivot set in
  place of the raw test, inheriting the test's lexical evidence under a distinct reason such as
  `query_term_<term>_subject`. If more than one plausible subject exists, keep the test and do not
  guess. This would run in `BuildCandidates` before `ContextPivotRanker.Rank`, because graph
  expansion currently happens *after* the four pivots are finalized (`Rank` then `graph.Reach`), so a
  reached implementation can only ever become a neighbour.
- **Give term rescue its own evidence tier**, below full-query retrieval, instead of sharing the
  `AnchorStrength` namespace.
- **Make term rescue inherit the original query's auto-test policy**, closing defect 3.
- **Reconsider the path-vs-name weighting**, closing defect 1.

### Scope of test-subject promotion (important)

This recovers **this dogfood query** when a long test name already carries the conceptual terms. It
does **not** fix the general class of conceptual `context` misses where no test-bridge is retrieved.
Promotion of `Unprovable` is also a thin pivot relative to the class-level answer
(`SymbolsArtifactIdentity` + `MatchesArtifact` remarks) unless container promotion is included.
Disposition today treats only full-query `query_rank_*` (and explicit anchors) as authoritative for
`sufficient`; a `query_term_*_subject` reason would still be discovery-tier / `partial` unless the
disposition contract is deliberately extended.

### Cost this proposal actually carries

It is not a small change. It adds a pivot tier, a new reason string, and a graph lookup inside
candidate building, and it changes `docs/contracts/context-json-v1.md`. The load-bearing cost is the
language-parity rule: one-hop resolution quality would have to be verified on a real extract for
every language julie-extractors supports, not just C#, before it could be trusted. A test-subject
promotion that silently works well only for C# would be exactly the kind of authoritative-looking
partial feature CLAUDE.md forbids.

### Better general direction than test-subject promotion alone

For the **class** of conceptual NL queries (not only this bridge), the stronger candidate-set fixes
are:

1. **Bounded context-side source / doc-comment rescue** for natural-language queries — map
   content/source hits to containing symbols and admit a small number as discovery-tier pivots
   (or neighbours), without folding prose into **symbol** ranking. Search already has
   `RunAutoSourceRescue` for weak symbol auto results; `context` has no equivalent arm today. Live
   `mode=source` already reaches the right area for this query. This uses Miller-owned corpus text
   and does not depend on cross-language identifier resolution quality.
2. **Make optional semantic seeds able to compete when served** — they already load, but
   `AnchorStrength = 0` means they cannot displace junk lexical affinity. A dedicated discovery tier
   (below real anchors, above or beside pure path/name term rescue) would let served semantic hits
   matter without making semantic required. `MILLER_SEMANTIC=off` must still be zero-work with
   byte-identical lexical output.

Secondary ranking fixes (term-rescue tier, test-policy inheritance, path-vs-name) remain worth doing
on their own; they improve pivot quality on queries that *do* retrieve something usable, but they
cannot invent a missing candidate.

### Explicitly rejected directions

- Folding doc comments, string literals, or broad source text into **symbol** ranking. Symbol search
  stays `name + signature`; the explicit `mode=source` / `mode=content` / `regions=` paths and the
  content corpus exist for that text. (A *context* rescue that *consumes* those paths is different
  from polluting the symbol index.)
- Requiring the semantic arm. ADR-0003 makes it optional and off-switchable, and `MILLER_SEMANTIC=off`
  must stay a zero-work guarantee with byte-identical lexical output. Whatever ships must work
  lexically when semantic is off.
- Globally excluding constants, tests, `scripts/`, Python, or `eval/` from pivots. `scripts/` can hold
  authoritative packaging behavior and `eval/` is authoritative for benchmark questions; many
  ecosystems have no `src/`, so a "prefer src" prior violates language parity. (Constants remain
  eligible query pivots via `IsQueryPivot`; disposition, not exclusion, handles false confidence.)
- Suppressing the test hit without a replacement subject. It is the only bridge from the *retrieved*
  set to the answer here; removing it makes this query strictly worse.
- Stopwording `prove` / `extract`, or relying on IDF alone. Neither adds a missing candidate.
- Raising the four-pivot cap. It spends more budget on the same bad candidates.

## Current mitigation

`b2e72137` stopped `context` from calling this bundle `evidence=sufficient`. A `constant`, `variable`,
`field`, or `property` pivot body is a declared value, not an implementation, so it can no longer
reach `sufficient`; the bundle now reports `partial` with reason `pivot_value_declaration_only` and
emits `next_actions` (`CarriesImplementation` + contract text; covered by
`Context_TopRankedValueDeclarationBody_LeavesBundleShortOfSufficient`). Live re-check confirms
`partial` / `pivot_value_declaration_only` on this query. That makes the recall gap visible to the
agent instead of hiding it behind a stop-looking signal. It does not close the gap.

**Mitigation limit:** `next_actions` currently only suggests `inspect` on the selected pivots. For
this bundle that re-points the agent at shell constants / eval helpers / a test name, not at
`search mode=source` or the answering symbol. Visibility without a better next action is only a
partial mitigation.

## Validation log (live re-check, 2026-07-27)

| Check | Result |
| --- | --- |
| Workspace | `miller-b275269b2d7c`, rev 116, fresh; `vectors: ready` |
| `context` pivots | Exact match to original table |
| Disposition | `partial` / `pivot_value_declaration_only` |
| Lexical symbol full phrase | No `SymbolsArtifactIdentity`; tops with `SIDECAR_EXTRACT` … |
| Per-term lexical | Matches original table shape; answer never appears |
| One-hop SQL (resolved) | Only `Unprovable` method (schema: `symbol_id`/`path`) |
| Unresolved identifiers | `MatchesArtifact` call + `SymbolsArtifactIdentity` var_ref null targets |
| `mode=source` lexical | Hits `SymbolSearchSidecar` docs naming derived-sidecar generation |
| Hybrid symbol search | Can surface `MatchesArtifact` via semantic ranks |
| Context semantic seeds | Blunted by `AnchorStrength=0` under positive lexical affinity |
| Commit `b2e72137` | Present; touches `ContextTool` disposition + contract + test |
| Secondary defects 1–3 | Confirmed in current source |
| Contract path | `docs/contracts/context-json-v1.md` (not bare `contracts/`) |

**Overall:** original diagnosis holds. Best class-level fix is context-side source/doc rescue (+ optional
semantic seed tiering); test-subject promotion is a valid narrow lexical patch for this bridge only.
