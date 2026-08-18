# References Candidates Contract v1

**RETIRED 2026-08-18.** User decision (query-time resolution design §5): this feature is
REMOVED, not maintained. Miller no longer ships `references candidates`, does not persist
suppressions, and does not expose an MCP tool for it. Recorded `history.db` rows for
`dead_code_candidate_count` and `dead_code_suppressed_total` stay readable via
`miller metrics history --metric`. This file is kept as the historical contract.

Status before retirement: experimental, evidence-gated. The evidence gate **PASSED 2026-07-07** (Miller repo: 392 → 5
candidates, zero confirmed-live after julie-extract 2.10.0 `variable_ref` emission and the eleven
suppression rules; see
[`findings/2026-07-07-dead-code-candidates-dogfood.md`](../findings/2026-07-07-dead-code-candidates-dogfood.md),
FINAL VERDICT). The surface was a **CLI-only prototype**.

`miller references candidates [--json] [--limit N] [--workspace-id SELECTOR] [--workspace DIR]` lists
deterministic dead-code *candidates* from one workspace's schema-v4 artifact: definition symbols that
survived exclusion, showed no inbound evidence of use, and were not caught by any named suppression rule.

**Posture: facts, not a verdict.** A candidate is a *fact to check*, never a deletion to make. Output pairs
the candidate list with named-rule suppression counts, literal-scan coverage, and per-language resolver
coverage so no consumer can mistake a list built on a *partial* resolver for a certainty grade. Miller owns
this deterministic candidate listing with named suppressions; ranking beyond the deterministic rule,
suppression **persistence**, history, and fleet/cross-workspace workflows stay out of Miller (Eros's, if it
ships).

## Invocation And Selectors

The command is an operation on the existing `references` CLI verb (no new MCP tool). It accepts the normal
read-command selectors:

- `--workspace-id SELECTOR` — display ID, unique prefix, full workspace ID, registered root path, `current`,
  or `primary`.
- `--workspace DIR` — path alias, normalized before selection.
- `--limit N` — bounds ONLY the candidate list (default 50). `examined`, `suppressions`, `literal_scan`, and
  `language_coverage` always report full totals, never a limited subset.
- `--json` — emit the JSON envelope below instead of compact text.

A selector flag supplied without a value is a usage error (exit `2`).

## Exit Codes

Same process-level contract as `symbols export --jsonl` / `references export --jsonl`:

- `0` — the payload is ingestable.
- `2` — usage or selector error.
- `3` — operational index failure: a missing index, or an incompatible artifact. A pre-v4 (schema 3)
  artifact and a v4 artifact missing the required resolution tables both exit `3` with the standard rebuild
  message (the Indexing reader validates the artifact and surfaces `IncompatibleExtractException`, mapped to
  exit `3` like other artifact commands).

## Candidate Rule (summary)

A symbol is examined, then flagged as a candidate, iff ALL hold. See
[`plans/2026-07-07-dead-code-candidates-design.md`](../plans/2026-07-07-dead-code-candidates-design.md) for
the full logic.

1. **Definition kind.** Kind is one of `function, method, class, struct, interface, enum, delegate,
   property, constant`. Any other kind is excluded entirely — not examined, not suppressed. **Syntax-invoked
   member shapes** are also excluded up front: names starting `~` (finalizers/destructors), containing
   `this[` (indexers), starting `operator` / `op_` (operator overloads), and `Finalize`. These are invoked
   by syntax, not by an identifier bearing their own name.
2. **No inbound evidence.** All four inbound counts are zero: `name_matches` (same-name identifier outside
   the symbol's own definition span), `resolved_inbound` (`identifier_resolutions` / `relationships` edges),
   `pending_resolved_inbound` (`pending_resolutions` inbound), and `calls_inbound`. A symbol with any inbound
   evidence is *alive-by-evidence* and is **silently dropped — that is not a suppression** (it is not counted
   in `suppressions`). The rule is one-directional by construction: resolution and literal evidence can only
   SAVE a symbol from being flagged, never add a flag. This preserves the conservative stance — collisions
   hide dead code rather than flag live code.
3. **No named suppression applies** (the eleven rules below).

## Suppression Rules

The reader applies the eleven rules in the exact TABLE ORDER below (`DeadCodeCandidates.SuppressionRuleIds`
in `src/Miller.Core/DeadCode/DeadCodeCandidates.cs:59` is the single source of the ids and their order).
**First match wins** — a suppressed symbol increments exactly one rule's count. Every rule id is always
present in the output, even at count 0.

| # | Rule id | Suppresses when | Source |
|---:|---|---|---|
| 1 | `public_api` | visibility is `public` or `exported` (case-insensitive) — publicly reachable, cannot be judged dead from this workspace. | `symbols.visibility` |
| 2 | `visibility_unknown` | visibility is NULL/blank — the extractor did not record visibility for this symbol/language, so public exposure cannot be ruled out. The honesty rule the live data forces. | `symbols.visibility` |
| 3 | `test_symbol` | `is_test` on the symbol or any ancestor via `parent_symbol_id`, OR the path contains a whole `test` / `tests` / `__tests__` segment (whole-segment match — `src/protest/` does not fire). | `symbols.is_test` + path |
| 4 | `entry_point` | name is `Main` / `main`, or the file is a `Program.cs`-style startup file. | `symbols.name` / `path` |
| 5 | `override_member` | the signature carries an `override`-family modifier (C#/Kotlin/Swift/Scala `override`, VB `Overrides`; whole-word match). Invoked through a base contract, so zero inbound name/graph evidence is expected. Java's `@Override` is an annotation, covered by `annotated`. | `symbols.signature` |
| 6 | `live_member_container` | a type-kind row (`class`/`struct`/`interface`/`enum`) whose own name shows no inbound evidence but at least one of its loaded candidate-kind members does — the classic static extension-class shape (`outcome.ToStorageString()`; the class name appears nowhere). The container is alive through its members. | computed over loaded rows |
| 7 | `framework_bound` | the symbol (or an ancestor) is the `containing_symbol_id` of any `structural_facts` row — routes, handlers, bindings, DI registrations. Broad on purpose. | `structural_facts` |
| 8 | `annotated` | the symbol carries any `symbol_annotations` row (attributes/decorators frequently mean reflection/framework discovery — `[Fact]`, `[JsonProperty]`, `@app.route`). | `symbol_annotations` |
| 9 | `generated_path` | the path matches conservative generated/vendored globs: `obj/`, `bin/`, `node_modules/`, `wwwroot/lib/` (as a leading or `/`-prefixed segment), or a filename ending `.g.cs` / `.designer.cs` or containing `.generated.`. | `symbols.path` |
| 10 | `low_evidence_language` | the symbol's language is present in coverage with **zero** identifier rows — nothing to test liveness against (e.g. css/html). A language ABSENT from coverage does NOT fire this rule; it can still be a candidate with the `name` evidence label. | computed |
| 11 | `string_literal_match` | the symbol's name appears in workspace string-literal text — reflection/config/DI-by-name usage (`GetMethod("Foo")`, route strings, serialized member names). Implemented by scanning `string_literal` `source_regions` spans and re-reading only those spans from source under the `files.content_hash` freshness guard, over the surviving-candidate set only. Files whose content hash is stale are counted and reported as unscanned (`files_skipped_stale`), not silently skipped. | `source_regions` + source re-read |

## Evidence Labels — Provenance, Not Certainty

Each candidate carries an `evidence_label` that states **which evidence was consulted, never how certain the
finding is.** There is no `strong`/`weak` grade — a certainty word over a partial resolver would read as
deletion-grade confidence the data cannot deliver.

- `name` — name-based liveness plus literal-scan found nothing, and the resolver was effectively blind for
  this language (below 10% measured resolution coverage in this artifact).
- `name+resolver` — the above AND the symbol's language has ≥ 10% measured resolution coverage in this
  artifact, so resolver silence carries some additional weight.

The 10% threshold is computed from the artifact at query time (resolved identifiers / identifiers per
language) — never hardcoded, never a static language list. It is single-sourced with the `resolved_pct`
rendered in `language_coverage` (`DeadCodeCandidates.ResolvedPercent`), so the label threshold and the
reported percentage cannot drift apart.

### Partial-resolver caveat (load-bearing)

On every current real scan `reference_resolution_status = partial`: measured resolution coverage is ~15% for
C#, lower for most languages. Therefore:

- **Absence of a resolved inbound edge is weak evidence of death** (~85% of usage sites are unresolved).
- **Presence of a resolved inbound edge is strong evidence of life** — which is why rule 2's inbound checks
  only ever SAVE a symbol.

Both the compact header and the JSON `artifact` object carry `reference_resolution_status` and
`reference_resolution_version` verbatim so no consumer can mistake a candidate list built on a partial
resolver for a verdict. The compact header states it in words:
`resolver: partial — candidates are facts to check, not deletions to make.`

### Write-only and comment-only symbols are facts to check, not verdicts

The surface does not classify a candidate as "safe to delete." A candidate may be write-only (e.g. bash
`IFS` in `while IFS= read` — set is consumed by the shell runtime, idiomatic rather than removable),
comment-only, or invoked by a runtime/binding the extractor cannot see. These are exactly the cases the
"fact to check, not a verdict" framing covers: the consumer confirms usage before acting. Miller does not
emit a deletion recommendation.

## Compact Output

```
candidates: <N> of <M> symbols examined · resolver: <status> — candidates are facts to check, not deletions to make.
<name> <kind> <language> <path>:<line> <visibility> evidence=<label> [name_matches=0 resolved_in=0 pending_in=0 calls_in=0]
...
showing top <K> of <N> by path            # only when the candidate list is limited
suppressed: public_api=… visibility_unknown=… test_symbol=… entry_point=… override_member=… live_member_container=… framework_bound=… annotated=… generated_path=… low_evidence_language=… string_literal_match=…
literal_scan: files_scanned=… files_skipped_stale=…
coverage: <lang>: <pct>% resolved; <lang>: <pct>% — name-evidence only; …
```

- Candidate lines are sorted by `(path, start_line)` with a stable `symbol_id` tiebreak, then the first
  `--limit` are shown. `visibility` renders `unknown` when NULL.
- The four per-candidate counts are definitionally all-zero (rule 2 requires it); they are printed for
  transparency, not as signal.
- `coverage` renders ` resolved` when `pct ≥ 10.0`, else ` — name-evidence only`, matching the evidence
  label threshold.

## `--json` Envelope

A single JSON object with `schema_version: 1`.

| Field | Type | Description |
|---|---|---|
| `schema_version` | number | Envelope schema version. Currently `1`. |
| `candidates` | array | The shown candidates (bounded by `--limit`), sorted `(path, start_line, symbol_id)`. |
| `candidates[].symbol_id` | string | Julie's stable symbol id. |
| `candidates[].name` | string | Symbol name. |
| `candidates[].kind` | string | Definition kind (one of the candidate kinds). |
| `candidates[].language` | string | Source language. |
| `candidates[].path` | string | Workspace-relative source path. |
| `candidates[].start_line` | number | 1-based definition start line. |
| `candidates[].visibility` | string or null | Symbol visibility; `null` when the extractor recorded none. |
| `candidates[].evidence_label` | string | `name` or `name+resolver` — provenance, not certainty. |
| `candidates[].evidence` | object | The four inbound-evidence counts (all `0` by construction). |
| `candidates[].evidence.name_matches` | number | Same-name identifier occurrences outside the definition. |
| `candidates[].evidence.resolved_inbound` | number | Resolved inbound edges (`identifier_resolutions` / `relationships`). |
| `candidates[].evidence.pending_resolved_inbound` | number | Inbound `pending_resolutions` edges. |
| `candidates[].evidence.calls_inbound` | number | Inbound call edges. |
| `suppressions` | object | `{ rule_id: count }` for ALL eleven rule ids, in table order, always present (even at `0`). Full totals, never limited. |
| `literal_scan` | object | Literal-scan coverage. |
| `literal_scan.files_scanned` | number | Literal-bearing files re-read for `string_literal_match`. |
| `literal_scan.files_skipped_stale` | number | Files skipped because their `content_hash` was stale (reported, not silently dropped). |
| `language_coverage` | array | Per-language resolver coverage, computed at query time. |
| `language_coverage[].language` | string | Source language. |
| `language_coverage[].identifiers` | number | Identifier rows for this language in the artifact. |
| `language_coverage[].resolved_pct` | number | Resolved identifiers / identifiers, 0–100, one decimal (`10.0`, not `0.1`). |
| `examined` | number | Symbols that survived exclusion (candidate kinds, non-syntax-invoked). Full total. |
| `artifact` | object | Artifact identity and resolver status. |
| `artifact.artifact_id` | string or null | Current artifact identity from `artifact_metadata`. |
| `artifact.revision` | number or null | Workspace revision for the artifact. |
| `artifact.reference_resolution_status` | string | `partial` on all current real scans. |
| `artifact.reference_resolution_version` | string or null | Resolver version recorded by the artifact. |

### Example

```json
{
  "schema_version": 1,
  "candidates": [
    {
      "symbol_id": "sym_abc123",
      "name": "SearchBackendMetadata",
      "kind": "method",
      "language": "csharp",
      "path": "src/Miller.Server/Tools/SearchTool.cs",
      "start_line": 436,
      "visibility": "private",
      "evidence_label": "name+resolver",
      "evidence": {
        "name_matches": 0,
        "resolved_inbound": 0,
        "pending_resolved_inbound": 0,
        "calls_inbound": 0
      }
    }
  ],
  "suppressions": {
    "public_api": 3357,
    "visibility_unknown": 709,
    "test_symbol": 0,
    "entry_point": 3,
    "override_member": 0,
    "live_member_container": 0,
    "framework_bound": 0,
    "annotated": 6,
    "generated_path": 0,
    "low_evidence_language": 0,
    "string_literal_match": 88
  },
  "literal_scan": { "files_scanned": 479, "files_skipped_stale": 0 },
  "language_coverage": [
    { "language": "csharp", "identifiers": 92952, "resolved_pct": 16.0 },
    { "language": "razor", "identifiers": 4100, "resolved_pct": 2.1 }
  ],
  "examined": 9186,
  "artifact": {
    "artifact_id": "artifact-2026-07-07",
    "revision": 42,
    "reference_resolution_status": "partial",
    "reference_resolution_version": "3"
  }
}
```

## Capabilities

`miller capabilities --json` advertises the surface via three keys (verify with `capabilities --json`):

- `optional_features.references_candidates: true` — boolean feature flag, same pattern as
  `reference_aware_context`.
- `json_commands` includes `references candidates --json`.
- `json_contracts` includes `references_candidates` at schema version `1`, pointing at this doc
  (`docs/contracts/references-candidates-v1.md`).

## Boundary

Miller owns the deterministic candidate listing and its named suppressions. It does **not** own: ranking
beyond the deterministic rule, suppression **persistence** / ignore-lists / per-repo config, candidate
history or trends, cross-workspace candidates, or any confidence/evidence ranking view — those require
semantics or fleet state and stay out of Miller. Surfacing candidates in `miller report`, the dashboard, or
an MCP tool is gated behind explicit user approval even though the CLI evidence gate has passed.
