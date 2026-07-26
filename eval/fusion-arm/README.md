# fusion-arm

The offline **fused retrieval arm** for Miller's semantic-retrieval evaluation
([design §6/§8](../../docs/plans/2026-07-19-miller-semantic-integration-design.md)). It reads a lexical run and
a semantic run for each query, routes and fuses them, and writes a
[retrieval-eval](../retrieval-eval/README.md) results JSONL that any scoring pass consumes unchanged.

It **is** production fusion — it links `Miller.Core` and calls the same `SemanticQueryPolicy.Route` and
`RrfFusion.Fuse` the live server uses. Nothing here reimplements routing or RRF; the arm only marshals the
per-query JSON onto those types and collapses the fused symbol order back to the `doc_id` vocabulary. That is
what makes an arm score meaningful: a fusion difference in the report is a weight/routing difference, not a
harness difference.

It is deliberately **outside `Miller.slnx`** and references `src/Miller.Core` only. Product builds do not see it.

## Usage

```bash
dotnet run --project eval/fusion-arm -- fuse \
  --queries eval/retrieval-eval/sets/dev/queries.jsonl \
  --lexical  /path/to/lexical-runs \
  --semantic /path/to/semantic-runs \
  --k-const 60 \
  --conceptual-ratio 1.0 \
  --out /path/to/fusion-results.jsonl \
  [--forced-hybrid]
```

Exit codes: `0` ok, `1` usage/IO error.

Tests: `dotnet test eval/fusion-arm/tests/FusionArm.Tests.csproj`.

## Inputs

`--queries` is a retrieval-eval query set (schema: [retrieval-eval README](../retrieval-eval/README.md)); the arm
reads `query_id` and `query`.

`--lexical` and `--semantic` are directories of **per-query files named `<query_id>.json`**, each a JSON array of
rows:

| field | lexical | semantic | notes |
| --- | --- | --- | --- |
| `symbol_id` | ✓ | ✓ | the fusion identity key; RRF dedupes on it |
| `doc_id` | ✓ | ✓ | retrieval-eval doc vocabulary; travels with the symbol |
| `score` | ✓ | ✓ | lexical `score` also feeds routing evidence and RRF tie-breaks |
| `rank` | — | ✓ | semantic 1-based rank; lexical rank is array order |

The lexical arm's own confidence — the `LexicalEvidence` routing reads — is built from the lexical file itself:
`HitCount` = row count, `TopScore` = first row's score, `RunnerUpScore` = second row's score (or `0`).

## Behavior

For each query:

1. Load its lexical file. **Missing lexical file → emit no results row**, count it, and continue (that query
   scores zero downstream, which is retrieval-eval's `missing_results` contract).
2. Route with `SemanticQueryPolicy.Route(query)` and decide candidate admission from the lexical evidence.
   - **Lexical-only route** → emit the lexical `doc_id` order untouched (the semantic file is not read).
   - **Hybrid route** → load the semantic file (**missing → emit no row + count**) and fuse with
     `RrfFusion.Fuse`. Weights are `new FusionWeights(1.0, conceptualRatio)` for the Conceptual class and the
     frozen `RrfFusion.WeightsFor(class)` constants for SymbolLookup/Mixed.
   - Zero hits expand; one hit expands while keeping that hit first; decisive multi-hit evidence reranks the
     lexical population only; other multi-hit evidence reranks and expands.
3. `--forced-hybrid` bypasses routing entirely and fuses **every** query under Conceptual weights
   `(1.0, conceptualRatio)` — the identifier diagnostic arm that measures fusion's non-inferiority on lexical
   queries.

`--k-const` is the RRF rank constant (production default `60`). Because RRF reads only ranks, its scores are
scale-invariant, so the Conceptual weight `(1.0, r)` is comparable across ratios.

## Output

One retrieval-eval results row per emitted query:

```json
{"query_id": "m-rank-1", "policy_version": 2, "ranked": ["src/Miller.Core/Search/Bm25.cs", "src/Miller.Indexing/SymbolSearchSidecar.cs"]}
```

`doc_id` collapse happens **after** fusion: the fused symbol order is walked in order, each symbol's `doc_id` is
kept on first appearance (so a doc inherits its best fused rank), and the list is truncated to the top **10**
docs. Output is deterministic and byte-identical across runs over identical inputs.

## Parity

The 5-query live parity smoke — adapter at fusion-v1 vs `miller search --arm hybrid --json` over real vectors — is
deferred to Task 4, which produces the live lexical/semantic runs this arm consumes.
