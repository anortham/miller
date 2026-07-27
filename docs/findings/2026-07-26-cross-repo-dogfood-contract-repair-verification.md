# Cross-Repo Dogfood Contract Repair Verification

## Scope

- Miller: `7a1512aa` on `codex/cross-repo-dogfood-repair`
- `julie-extractors`: `500416af` on `codex/cross-repo-dogfood-repair`
- Eros: downstream and intentionally excluded until Miller and its direct producers are final
- No push, tag, release, publication, or deployment was performed

## Finding-to-fix matrix

| Finding | Resolution | Evidence |
|---|---|---|
| Mutable schema-5 foreign keys lacked leading indexes, making one-file refresh exceed 600 seconds. | Added six producer indexes plus a generic catalog test covering every composite mutable foreign key. | Repaired refresh: 10,995 ms scan / 19,933 ms total; warm refresh: 6,744 ms / 15,339 ms. |
| Pending JSONL export read nullable `caller_scope_symbol_id` as `String`. | Read `Option<String>` and added a null contract fixture. | RED `InvalidColumnType`; GREEN `jsonl_contract` 10/10. |
| Skipped relationship rows still created orphan `reference_sites`. | Shared insertability predicates between reference-site and evidence writers. | RED two orphan sites; GREEN zero written/stored sites for skipped children. |
| Malformed marker owner text suppressed the whole marker fact. | Preserve the marker and treat the unclosed owner suffix as description text. | RED zero facts; GREEN one exact `code.marker.v1` fact. |
| Reference-site scope disagreement was reported as a writer defect. | Rejected. Scope is part of canonical producer identity; disagreement is invalid data and must roll back. | Full artifact has zero identifier, relationship, or pending scope disagreements. |
| Marker filtering happened after a hard 500-row prefix and history counts saturated at 500. | Filter before bounding and aggregate exact canonical marker counts directly from `structural_facts`. | Focused marker/history regressions pass. |
| Pattern paging hydrated and parsed the full population. | Scan identity columns for total/fingerprint, then hydrate only the requested page. | RED 99,724,784 allocated bytes; GREEN under the 8 MiB tripwire. |
| Content error handling could throw while rendering a budget failure. | Added a bounded minimal diagnostic fallback. | RED escaped `InvalidOperationException`; GREEN typed refusal at a 256-byte budget. |
| Compact diagnostic dedup could let result text suppress the real diagnostic. | Renderer now owns diagnostic fields; Content avoids pre-rendering them. | RED one misleading code line; GREEN both source text and final authoritative diagnostic. |
| Exact identifier search admitted zero-evidence noise. | Reject identifier candidates without strict evidence. | Commit `290d6cbd`; empty queries now return typed expected-empty results. |
| Large Scale fixtures still hand-built a legacy schema. | Centralized all fixture DDL in `JulieDbFixture.EnsureCurrentSchema`; large edges now emit current spanless reference sites. | RED three schema-gate failures; GREEN current-schema/rebuild tests 6/6 and full Scale 92/95 with three skips. |
| Source-text convention crashed on an unstaged rename. | Audit cached plus untracked nonignored existing files. | RED missing old path; GREEN focused convention/current-schema/rebuild tests 7/7. |

## Final producer artifact

`workspace full --json --wait` used the producer built from `500416af`:

- artifact: `artifact-1785116658093991000`
- scan: 23,382 ms; total: 31,752 ms
- schema/extract/JSONL: `5 / 4 / 4`
- symbols: 65,839
- reference sites: 422,081
- identifiers: 336,542
- relationships: 15,300
- pending relationships: 76,702
- capability gaps: 70 `open`, 16 `exception`, no unknown status
- missing reference-site foreign rows: `0 / 0 / 0`
- orphan reference sites: `0`
- scope disagreements: `0 / 0 / 0`
- Miller contains no actionable marker facts; invalid marker names: `0`

`capabilities --json` reports Miller `1.14.0+7a1512aa8cbc`, Julie `2.18.0`,
semantic policy `2`, Patterns schema `2`, and References export schema `2`.

## Branch gates

### `julie-extractors` at `500416af`

- `cargo fmt --all --check`: pass
- `cargo check --workspace --all-targets`: pass
- `cargo test --workspace --all-targets`: pass
- extractor library: 3,021 passed, 7 ignored
- artifact, CLI, operations, xtask, and release-contract suites: pass
- doctests: 1 passed

### Miller at `7a1512aa`

- `dotnet build Miller.slnx -c Release`: 0 warnings, 0 errors
- fast suite: 5,121 passed, 2 skipped, 0 failed; 23-second wall time
- Scale suite: 92 passed, 3 skipped, 0 failed; 43 seconds
- skipped Scale tests require optional semantic runtimes not configured in this environment

## Live nine-tool MCP matrix

All nine tools were listed and exercised through the rebuilt branch server. Success, empty,
invalid/refusal, paging, output-budget, dry-run edit, health, and cross-workspace paths completed.

| Call | Result bytes | Text bytes | Latency |
|---|---:|---:|---:|
| workspace onboarding | 154 | 99 | 7 ms |
| search | 635 | 574 | 2,002 ms |
| cross-workspace search | 491 | 443 | 826 ms |
| expected-empty search | 338 | 285 | 224 ms |
| inspect overview | 3,363 | 3,236 | 47 ms |
| inspect full/budget | 7,055 | 6,844 | 24 ms |
| context | 3,712 | 3,583 | 47 ms |
| trace | 501 | 451 | 11 ms |
| impact | 3,519 | 3,448 | 11 ms |
| edit dry-run | 809 | 751 | 8 ms |
| Patterns one-row page | 1,655 | 1,508 | 16 ms |
| content list | 185 | 144 | 4 ms |
| workspace health | 766 | 716 | 59 ms |

- maximum result: 7,055 bytes
- results above 12 KiB: 0
- converged tool errors: 0
- invalid inputs returned typed diagnostics in 0-2 ms
- the first cold matrix exposed a typed stale-sidecar refusal; the sidecar converged and warm replay passed

Exact matrix: `/private/tmp/miller-cross-repo-claude-review.XRi3eZ/mcp_matrix.current.json`.

## Semantic evaluation

Open evaluation used frozen Miller and Julie corpora and policy version 2.

| Metric | Production hybrid | Lexical |
|---|---:|---:|
| recall@10 | 0.703125 | 0.567708 |
| nDCG | 0.645212 | 0.519199 |
| MRR | 0.672771 | 0.537566 |
| top-1 | 0.618056 | 0.493056 |
| intent groups | 14/14 | 12/14 |
| identifier recall / nDCG | 1.0 / 1.0 | 1.0 / 1.0 |

The six open negative queries remain false positives in both the prior baseline and this run. Semantic
cosine ranges overlap valid conceptual queries, so an absolute threshold would remove required recall.
No guessed threshold was added. A proper abstention change requires a calibrated query-level confidence
model and new development material.

The user-owned sealed set was not available. The sealed gate remains explicitly blocked and no sealed
query text was inspected or tuned against.

## Independent review

- Claude Miller contract review: six findings; five confirmed/fixed, one strict closed-status compatibility suggestion rejected.
- Claude Julie producer review: five findings; four confirmed/fixed, one invalid scope-merge suggestion rejected after live corpus proof.
- Two narrower Claude semantic structured reviews exhausted their turn limits and produced no review result.
- Eros review and compatibility work are deferred until these upstream contracts are final.
