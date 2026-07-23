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
    "fallback_sites": [],
    "coverage": [],
    "fallback_candidates": 0,
    "fallback_status": "NoCandidates"
  }
}
```

Each site contains `file`, `line`, `source`, and `resolution_status`. `exact_sites` starts with the definition site,
then target-proven reference sites. `fallback_sites` contains explicitly selected name-based evidence and may
include unresolved sites or sites belonging to another same-name symbol.

Coverage rows contain `language`, `kind`, `resolution_status`, and `count`. The definition has its own exact
coverage row. Exact reference rows are grouped by extracted language and source kind; explicit fallback is grouped
as `language=unknown`, `kind=name_based`, `resolution_status=fallback`.

`fallback_candidates` reports unresolved candidates observed even when exact mode refuses them.
`fallback_status` reports the evidence reader's fallback state.

## Safety

Exact mode refuses the operation when any required exact site lacks a usable byte span, the definition token
cannot be proven, or exact evidence references a file that cannot be loaded. The caller must choose
`include_fallback` to accept homonym risk.

Apply remains atomic across files. A successful compact apply ends with an `impact` command using the exact symbol
ID and a reminder to run the selected tests.
