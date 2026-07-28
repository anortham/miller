# Context MCP/CLI Contract v1

Status: active
Surfaces: MCP `context`, CLI `miller context`

## Purpose

`context` returns a small task-ranked bundle for orienting in an unfamiliar code area. It combines at most four
pivots with bounded implementation bodies, shallow neighbour signatures, optional exact/fallback usage evidence,
and an explicit evidence disposition. A caller that already knows the target symbol should use `inspect`.

## Inputs

| Meaning | MCP parameter | CLI option |
|---|---|---|
| Task text | `query` | positional `<query>` |
| Output budget | `token_budget` | `--token-budget N` |
| Neighbour radius | `max_hops` | `--max-hops 0-2` |
| Entry symbol, ID, or indexed-file anchors | `entry_symbols` | repeat/comma-list `--entry-symbol NAME` |
| Edited-file anchors | `edited_files` | comma-list `--edited-files PATHS` |
| Failing-test anchor | `failing_test` | `--failing-test TEXT` |
| Stack anchor | `stack_trace` | `--stack-trace TEXT` |
| Reference enrichment | `reference_mode` | `--reference-mode off\|usage` |
| Reference depth | `reference_depth` | `--reference-depth 0-1` |
| Test filtering | `exclude_tests` | `--exclude-tests` |
| Output format | `format=compact\|json` | compact default or `--json` |
| Registered workspace | `workspace_id` | `--workspace-id SELECTOR` |
| Refresh selected workspace | `ensure_fresh` | CLI resolves its selected workspace before the read |

CLI also accepts `--workspace DIR` for a direct workspace path.

`max_hops` is clamped to zero through two and `reference_depth` to zero through one.
`reference_mode=off` returns symbol and implementation evidence; `reference_mode=usage` adds bounded
implementation, identifier, and containing-content evidence. `exclude_tests` applies only to usage enrichment.
It does not alter lexical or optional semantic pivot selection when reference enrichment is off.

`token_budget` bounds the complete response. MCP caps the effective budget at 2,400 estimated tokens, which is
stricter than Miller's universal 12 KiB MCP response ceiling. CLI does not apply the 2,400-token MCP cap.

## Output

JSON returns a `bundle` array. Normal mode uses `item_type=symbol`; usage mode may also return
`item_type=implementation`, `identifier`, or `content_chunk`. Items carry the available identity, location,
`role`, `reason`, and `confidence` fields for their evidence type.

`disposition.status` is:

- `sufficient` only when the rendered bundle includes an implementation body anchored by an exact entry symbol,
  entry file, edited file, failing-test symbol, stack frame/symbol, or full-query ranked pivot; usage mode is also
  sufficient when it includes an exact containing-symbol content chunk;
- `partial` when a pivot or relation is present without authoritative implementation evidence;
- `insufficient` when no pivot is rendered.

A `constant`, `variable`, `field`, or `property` pivot body is the value it was assigned, not an implementation,
so it never reaches `sufficient` however it ranked; an authoritatively anchored one reports `partial` with reason
`pivot_value_declaration_only`. Discovery-tier pivots remain `partial` even when they carry a real implementation
body: reasons include `source_rescue_N` (content/source corpus rescue mapped to a containing symbol),
`semantic_rank_N` (optional semantic seed), `query_term_<term>` (per-term symbol rescue), and
`query_term_<term>_subject` (exactly one resolved non-test subject promoted from a term-rescue test hit). When a
discovery pivot supplies an implementation body, disposition reason is `discovery_implementation_present` rather
than masking the bundle as value-declaration-only. Those paths are not authoritative task anchors.
`next_actions` appears only when the disposition is not `sufficient`. When every pivot is a value declaration,
`next_actions` leads with `search(query=…, mode=source)` instead of inspect-on-constants; when discovery or other
implementation pivots are present, inspect targets those kinds and may still append a source-search recovery.

Compact output conveys the same selected items and disposition. Every selected item is rendered; omitted
candidates are never included in the selected count. The four-pivot ranking cap is deliberate and does not emit a
continuation or dropped-pivot count; narrow or supply an explicit anchor when a candidate is absent.

## Anchor diagnostics and bounds

Unresolved, ambiguous, or capped anchors appear under JSON `anchor_diagnostics` and compact
`## anchor diagnostics`. Diagnostic values may be shortened for output safety without splitting a valid UTF-16
surrogate pair.

- Entry-symbol ambiguity admits at most 10 candidates. `reason=ambiguous_truncated` reports additional matches;
  otherwise the reason is `ambiguous`.
- Edited-file anchors that do not resolve to indexed symbols use `reason=not_indexed`.
- Failing-test and stack-trace symbol hints inspect at most 24 distinct identifier tokens and at most six matching
  pivot symbols per token. `reason=truncated` applies to a matched failing-test hint;
  `reason=symbols_truncated` applies to matched stack evidence. These reasons report that one or both symbol-hint
  caps fired; the response does not distinguish the token cap from the per-token match cap.
- Stack traces inspect at most 24 parsed file/line frames in textual order across recognized .NET and Python
  shapes. `reason=frames_truncated` reports additional frames when useful matches remain.
- Missing entry symbols use `not_found`; unmatched failing tests use `no_symbol_match`; unmatched stack traces use
  `no_frame_match`. If capped evidence produced no match, the reasons are `no_symbol_match_truncated` and
  `no_frame_match_truncated`; neither claims that the unexamined evidence has no match.

These are work bounds, not continuation points. Narrow the anchor or use `search` when a diagnostic reports
truncation.

## Budget behavior

The UTF-8 byte estimator and final renderer bound the complete compact or JSON response, including workspace
banners, diagnostics, disposition, and next actions.

- `token_budget <= 0` returns no bytes in either format.
- A positive budget that cannot hold the JSON envelope returns the canonical `{}` when it fits, otherwise no
  bytes.
- For positive budgets below 512 estimated tokens, selection uses the requested budget. Tiny compact responses
  retain the largest complete line prefix that fits, then fall back to `…` or no bytes.
- For budgets of at least 512 estimated tokens, item selection uses three quarters of the requested budget. This
  conservative reserve protects the complete response against the byte-based estimator while the final bound
  still enforces the caller's full budget.
- Bounded strings do not split valid UTF-16 surrogate pairs.

## Ranking and discovery seed strengths

Explicit anchors (entry/edited/failing/stack) stay in the 65–100 band. Full-query symbol hits use
`TaskQueryAffinity` (0–50) with name weight ≥ path weight. Bounded content/source rescue seeds use fixed
strength 35 (`source_rescue_N`). Optional semantic seeds use fixed strength 26 (`semantic_rank_N`). Term
rescue is capped at 18 (`query_term_<term>`). Term rescue inherits the parent query’s auto-hide-tests policy
so one-word terms cannot reintroduce tests on natural-language queries.

## Semantic and reference guarantees

- `MILLER_SEMANTIC=off` performs no semantic work and keeps lexical-only output byte-identical. Optional
  embeddings come from the pinned shared sidecar, not from Miller.
- Reference-aware context consumes the shared exact symbol-ID evidence contract. Exact and fallback evidence stay
  distinct and retain confidence and provenance.
- `confidence=name_based` is possible same-name evidence, not a resolved target-symbol edge.

See [Exact Reference Consumers v1](exact-reference-consumers-v1.md) for the shared usage-evidence rules.
See [ADR-0003](../adr/ADR-0003-semantic-retrieval-ownership.md) for the semantic ownership and permanent off-switch
boundary.
