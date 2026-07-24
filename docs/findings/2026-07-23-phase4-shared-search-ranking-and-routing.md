# Phase 4 shared search ranking and routing — 2026-07-23

## Decision

Phase 4 is complete for implementation and visible calibration. Miller now has one deterministic ranking
pipeline over both lexical backends, explicit AND-to-OR relaxation, mixed file/symbol auto routing, and a
per-call retrieval policy. The final sealed Miller-versus-Julie decision remains intentionally unspent until
the later full replay required by the takeover plan.

## Shipped behavior

- `SymbolReranker` keeps raw BM25 separate from exactness, phrase proximity, source role, path role,
  dominant-language affinity, and container evidence.
- Multi-term symbol queries run `AND` first. They use `OR` only when strict results cannot fill the requested
  page, keep strict rows first, and expose `relaxed` in compact and JSON output.
- Auto mode recognizes mixed path-plus-symbol queries and returns typed `symbol` and `file` rows. Explicit
  symbol, file, source, content, marker, and region routes remain unchanged.
- Multiple strong child matches may surface their otherwise-unmatched parent. The parent retains raw lexical
  score zero and receives a separately observable container contribution. This lets conceptual choice-point
  queries find factories and owning types without indexing arbitrary bodies into symbol BM25.
- Automatic semantic fusion abstains when deterministic container evidence produced the lexical winner.
  Explicit forced hybrid still fuses, so evaluation controls remain honest.
- MCP `retrieval=auto|lexical|hybrid|semantic` and CLI
  `--arm auto|lexical|hybrid|semantic` select the per-call symbol policy. Lexical bypasses semantic probes and
  canaries. Forced semantic failures return typed unavailable diagnostics; file, mixed, and text routes return
  typed unsupported diagnostics instead of silently changing meaning.
- `MILLER_SEMANTIC=off` remains authoritative. The default automatic policy and optional RRF arm remain
  available when semantic serving is enabled.

## Backend and contract evidence

- The on-disk FTS5 and in-memory indexes already implemented both `SearchMode.And` and `SearchMode.Or`; no
  backend rewrite was needed. A new end-to-end parity test proves the same relaxed, reranked symbol IDs and
  raw scores across both backends.
- Normal JSON continues to expose raw lexical `score`. Complete feature contributions remain available through
  the pure `SymbolReranker` evaluation result without inflating agent output.
- Golden compact and JSON output was updated only for the intentional reranking and `relaxed` additions.
- The final focused Phase 4 gate passed 516 tests across reranking, relaxation, route planning/execution, forced
  retrieval, container abstention, FTS parity, CLI parsing, determinism, and golden cases.

## Final verification

- Fast suite: 4,683 passed, 2 expected skips, 0 failed.
- Scale suite: 87 passed, 0 failed.
- Release build: 0 warnings, 0 errors.
- Native AOT publish: `osx-arm64` succeeded.
- Plugin contracts: 48 passed.
- Agent-efficiency Python harness: 99 passed in its pinned virtual environment.
- Retrieval evaluator agent scoring: 19 passed.

## Visible calibration

Five public takeover search prompts were replayed directly through the current Miller binary against the live
Goldfish and Eros indexes with a six-result page. This is implementation calibration, not sealed decision
evidence.

| Prompt class | Expected symbol | Result |
|---|---|---|
| exact homonym lookup | `normalizePathKeyForSafetyCheck` definition | rank 1 |
| exact factory lookup | `SemanticMillerCandidateFactory` | rank 1 |
| conceptual semantic choice point | `SemanticMillerCandidateFactory` | improved from absent in top 6 to rank 1 |
| conceptual workspace recovery | `recoverWorkspace` | rank 1 |
| exact true-no-hit lookup | `normalizePathKeyForUnsafeMutation` | explicit no-exact result; near rows typed `exact_match=false` |

For the four positive queries, recall@6, nDCG@6, MRR, and top-1 are all `1.0`. The negative query stops the
workflow after one call instead of nudging the agent to inspect a fuzzy neighbor. The key choice-point repair was
not a model swap: exact parent IDs already present in the extractor artifact let Miller aggregate strong child
evidence into the owning factory.

## Claude review

A focused Claude review raised four findings. One was accepted and fixed: a configured forced-hybrid arm that
declined to serve could previously fall through to lexical output. `RunRequiredHybrid` now observes whether the
arm was queried and returned fused rows; a declined or empty arm returns `semantic_unavailable`, with a focused
test proving the contract.

Three findings were rejected after code validation:

- Automatic semantic fusion abstains only when container evidence wins rank one; a lower container row must not
  suppress an otherwise-valid hybrid result.
- AND matches are a subset of the subsequent OR fallback. The bounded outside-scope hint is consulted only when
  no rows survive, so the last OR pass does not hide an actionable strict result.
- Phrase proximity deliberately rewards query-order adjacency. Order-independent token overlap is already
  represented by lexical score and exactness; making proximity unordered would duplicate those signals.

## Remaining decision boundary

The paid paired-agent affected replay and the operator-owned sealed corpus are not Phase 4 tuning inputs. The
full takeover decision still requires the later frozen replay after context, impact, remaining surfaces,
all-language coverage, and RC3 validation complete.
