# Phase 1 exact reference evidence — 2026-07-23

## Result

Miller now has one bounded inbound-reference seam keyed by resolved symbol ID. Exact extractor evidence and
name fallback are separate result arms; ambiguous fallback candidates are counted but never attributed to a
definition.

The agent-facing tools still use their legacy projections until the Phase 3 consumer migration. Phase 1 changes
the shared evidence and graph foundations without claiming that trace, inspect, context, impact, or rename is
already remediation-complete.

## Contract

`ReferenceEvidenceReader.Read` accepts:

- a pinned extract artifact;
- one resolved target symbol ID;
- explicit exact and fallback limits.

It returns:

- exact and fallback rows separately;
- canonical kind plus the raw extractor kind;
- containing symbol and complete recorded site coordinates;
- source, resolution status, confidence, and resolution tier;
- observed, deduplicated, returned, truncated, ambiguity, and fallback-safety facts.

Exact source precedence is direct identifier target, identifier-resolution overlay, relationship, then resolved
pending relationship. `call` and `calls` canonicalize to the same kind before site deduplication. Conflicting
direct and overlay targets prefer the direct target.

Fallback is returned only when the target name has one definition. It is separately bounded and capped at
confidence `0.5`. Multiple definitions produce `SuppressedAmbiguousName`; the candidate count remains visible
and `FallbackTruncated` remains false because suppression is not truncation.

## Acceptance evidence

| Fixture | Expected | Result |
| --- | --- | --- |
| two `Run` definitions with distinct resolved identifiers | disjoint exact sets | pass |
| direct, overlay, relationship, and pending rows at one site | one canonical site | pass |
| `JulieExtractRunner.Run` shape | ten observed source rows, five exact sites | pass |
| `ContextTool.Run` shape | zero exact sites, 632 unsafe fallback candidates, zero attributed | pass |
| unique unresolved name with limit two | three available, two returned, low confidence, truncated | pass |
| conflicting direct and overlay targets | direct target only | pass |
| two line-only sites on one line | distinct callers and start columns preserved | pass |
| pending resolution only | caller scope, tier, and confidence preserved | pass |
| identifier-resolution overlay | source and tier preserved | pass |
| same-name identifier resolved elsewhere | excluded from target fallback | pass |
| exact identifier target absent from artifact | omitted by both graph implementations | pass |
| missing resolution overlay table | classified incompatible extract | pass |

Graph loading now prefers direct and overlay target IDs. It no longer emits unresolved homonym fan-out and keeps
unique-name fallback for unresolved rows. The materialized server graph and on-demand CLI graph now use the
same exact-first rules in both directions.

## Current extractor coverage

The live Miller artifact was measured after implementation. `exact` means either `identifiers.target_symbol_id`
or the `identifier_resolutions` overlay supplied a resolved target.

| Language | Kind | Total | Exact | Exact % |
| --- | --- | ---: | ---: | ---: |
| bash | call | 526 | 24 | 4.56 |
| bash | member_access | 15 | 0 | 0.00 |
| bash | variable_ref | 631 | 0 | 0.00 |
| csharp | call | 71,917 | 14,175 | 19.71 |
| csharp | member_access | 37,765 | 0 | 0.00 |
| csharp | type_usage | 23,707 | 8,478 | 35.76 |
| csharp | variable_ref | 128,038 | 3 | <0.01 |
| css | call | 434 | 0 | 0.00 |
| css | member_access | 435 | 0 | 0.00 |
| css | variable_ref | 312 | 0 | 0.00 |
| html | member_access | 70 | 0 | 0.00 |
| javascript | call | 1,037 | 132 | 12.73 |
| javascript | member_access | 499 | 0 | 0.00 |
| javascript | variable_ref | 1,751 | 0 | 0.00 |
| powershell | call | 180 | 7 | 3.89 |
| powershell | member_access | 64 | 0 | 0.00 |
| powershell | variable_ref | 273 | 0 | 0.00 |
| python | call | 6,079 | 1,076 | 17.70 |
| python | member_access | 1,714 | 0 | 0.00 |
| python | type_usage | 664 | 136 | 20.48 |
| python | variable_ref | 10,840 | 0 | 0.00 |
| razor | call | 290 | 33 | 11.38 |
| razor | member_access | 543 | 0 | 0.00 |
| razor | type_usage | 176 | 0 | 0.00 |
| razor | variable_ref | 690 | 0 | 0.00 |

The seam reports this evidence honestly, but current extraction is not language-complete. Exact calls are
partial in six observed languages, most member and variable references are unresolved, and the artifact covers
only the languages present in this workspace. Phase 8 remains the blocking all-supported-language,
cross-platform extractor promotion gate; Phase 1 does not claim that gate is complete.

## Claude review disposition

| Finding | Disposition | Resolution |
| --- | --- | --- |
| CLI graph still used name fan-out while the server graph was exact-first | accepted | on-demand forward and reverse queries now consume direct targets, overlays, relationships, and pending resolutions before unique-name fallback |
| graph quality depends on per-language resolution coverage | accepted as a product gate | live coverage is recorded above; the all-language extractor gate remains Phase 8 |
| `maxNameResolutionTargets` became inert | accepted | removed from both graph implementations and the repository loader |
| line-only dedup could merge multiple sites on one line | accepted | the fallback key now includes containing symbol and normalized start column |
| pending-only, tier propagation, and resolved-away fallback needed direct tests | accepted | focused fixtures now cover each case |
| follow-up: on-demand forward exact edge could escape the artifact symbol set | accepted | the query now retains unresolved fallback rows but requires exact targets to join `symbols`; a red-green parity regression covers the missing-target case |
| follow-up: stale “target_symbol_id always null” claims remained | accepted | production contracts and the legacy rename message now describe the actual v4 evidence and remaining name-based consumer behavior |
| closure review: both follow-up fixes were correct; one changed line was misindented | accepted | indentation corrected; no substantive Phase 1 finding remained |

## Verification

- focused reference, materialized graph, on-demand graph, and repository graph tests: 42 passed.
- bridge-loader fixtures: 18 passed.
- fast suite: 4,578 passed, two expected skips, under the 30-second ceiling.
- scale suite: 87 passed against the real `julie-extract`.
- Release build: zero warnings and zero errors.
- `git diff --check`: clean.

## Architecture

[`ADR-0004`](../adr/ADR-0004-exact-reference-evidence.md) records the deep-module decision. SQLite source union,
precedence, kind normalization, fallback safety, and deduplication stay in `Miller.Indexing`; `Miller.Core`
contains only pure result and policy types.
