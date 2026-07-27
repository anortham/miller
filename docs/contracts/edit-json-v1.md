# Miller edit JSON v1 contract

Status: active. This document specifies the additive `rename_symbol` evidence fields; existing edit outcome,
diagnostic, diff, match-evidence, and apply fields remain unchanged.

## Request

`rename_symbol` accepts `rename_mode`:

- `exact` (default): rename only target-proven reference spans and the exact definition token.
- `include_fallback`: also include separately labeled name-based same-name sites not proven to the exact target.

## Rename evidence

Successful preview and applied JSON include:

```json
{
  "rename_evidence": {
    "mode": "exact",
    "target_symbol_id": "exact-symbol-id",
    "exact_sites": [],
    "exact_sites_total_count": 0,
    "exact_sites_returned_count": 0,
    "exact_sites_omitted_count": 0,
    "fallback_sites": [],
    "fallback_sites_total_count": 0,
    "fallback_sites_returned_count": 0,
    "fallback_sites_omitted_count": 0,
    "coverage": [],
    "coverage_total_count": 0,
    "coverage_omitted_count": 0,
    "fallback_candidates": 0,
    "fallback_status": "NoCandidates",
    "inferred_exact_count": 0
  }
}
```

Applied JSON also includes `post_apply_hint`, a copyable `impact` command using the renamed symbol's exact ID
followed by the test reminder. Preview JSON omits this field.

Each site contains `file`, `line`, `source`, and `resolution_status`. `exact_sites` starts with the definition site,
then target-proven reference sites. `fallback_sites` contains explicitly selected name-based evidence and may
include unresolved sites or sites belonging to another same-name symbol. Each tier returns at most eight sites;
the total, returned, and omitted counts state the complete population.

Coverage rows contain `language`, `kind`, `resolution_status`, `count`, `inferred_count`, and `min_confidence`
— every row, including the definition row (`inferred_count` 0, `min_confidence` 1.0) and the name-based fallback
row, which is not a binding at all and so reports fully inferred at `min_confidence` 0.0.
The definition has its own exact coverage row. Exact reference rows are grouped by extracted language and source
kind; explicit fallback is grouped as `language=unknown`, `kind=name_based`, `resolution_status=fallback`.
Coverage returns at most eight rows and reports its exact total and omitted count.

`inferred_count` and the top-level `inferred_exact_count` report how many exact sites the extractor bound through
a heuristic resolution tier — julie's tier 3 (`tier3_receiver` 0.65, `tier3_static_type` 0.70), which corroborates
a receiver no recorded type fact backs, and tier 4 (`tier4_global` 0.55), which binds on global name uniqueness
alone. They are real references and are renamed, but a rename writes, so they must not render as
indistinguishable from a scope-proved binding; `min_confidence` states the weakest binding in the row. Compact
output carries the same facts as a parenthetical on the coverage line plus a review note. Non-heuristic bindings
(a direct extractor target, a relationship edge, tiers 1 and 2) report `inferred_count` 0.

`fallback_candidates` reports unresolved candidates observed even when exact mode refuses them.
`fallback_status` reports the evidence reader's fallback state.

## Safety

Exact mode refuses the operation when any required exact site is not identifier-derived, does not span exactly
the old identifier's UTF-8 byte length, lacks a usable byte span, references a file that cannot be loaded, or the
definition token cannot be proven. The caller must choose `include_fallback` to accept homonym risk.

`allow_stale=true` applies only to `replace_text`, whose match is derived from current disk text. Symbol-span,
insert, documentation, and rename operations always require fresh indexed spans.

`query`, `anchor`, and `line` narrow `replace_text` to bounded candidate windows. `occurrence=all` cannot be
combined with those selectors because that would make `all` mean only the hidden candidate window; omit selectors
for a whole-file replacement. When the content index is unavailable, `line` and `anchor` are enforced against
current disk text, while `query` refuses rather than widening silently to the whole file. Within a selector
window, multiple plausible or overlapping locations refuse as `ambiguous_match` instead of letting
`occurrence=first` choose a neighboring target.

Every MCP edit response is valid compact text or JSON within 12 KiB. Rename diffs, summaries, site rows, and
coverage rows are independently bounded; omission counts remain exact. If an unexpected path still exceeds the
envelope, the MCP shell returns a small valid JSON or compact summary that preserves apply/outcome facts and
states that detailed evidence was omitted.

Apply rolls back every already-written file when a later write fails. If a rollback itself fails, JSON reports
`applied=false`, `partially_applied=true`, `failure_reason=partial_apply`, `files_left_modified_count`, and
`files_left_modified`; `result_count` is the number of files still modified. At most twenty paths are returned and
`files_left_modified_omitted_count` is exact. Compact output follows the same bound.
Miller attempts write-through convergence for those paths so the index does not knowingly retain pre-edit text.
A successful compact apply ends with an `impact` command using the exact symbol ID and a reminder to run the
selected tests.

`match_count` reports the full candidate population while `selected_match_count` reports the non-overlapping spans
selected for preview or apply. Normalized and fuzzy `occurrence=all` skip overlapping candidates rather than emit
corrupting splices; compact output states the selected and skipped counts when they differ. Fuzzy
`occurrence=first|last` chooses the lowest edit-distance tier before position. Fuzzy `occurrence=all` selects
lowest-distance non-overlapping candidates from the full threshold population — every site within the threshold,
not only the closest, so a distant-but-admissible site is rewritten alongside an exact one. Fuzzy results
therefore carry `fuzzy_sites`, one `{line, distance}` per selected site ascending by line, so the spread is
visible in the preview before an apply; compact output renders the same as a `fuzzy sites L<line>~<distance>`
match note. Non-fuzzy matches omit the field. When the changed region is too
large for bounded LCS alignment, the diff still returns a bounded prefix proof plus exact old/new omitted-line
counts.
