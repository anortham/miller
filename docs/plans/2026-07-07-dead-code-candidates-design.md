# Dead-code candidates: `miller references candidates` (P3, evidence-gated)

**Status:** design for review · **Date:** 2026-07-07 · **Depends on:** julie-extract 2.9.0 pin
(schema v4, merged locally at `e1b7b9a`)

## Context

The 2026-07-06 standalone-bolstering consensus demoted dead-code candidates to P3, blocked on
extractor reference resolution. julie-extractors v2.9.0 shipped that resolution (schema v4:
`pending_resolutions` + `identifier_resolutions` overlay tables, FK-consistent
`identifiers.target_symbol_id`, tiered confidence). Miller's pin is bumped and gated
(`MillerExtractContract` expects schema 4).

**The load-bearing measurement** (live 2.9.0 scan of the Miller repo, 2026-07-07):

- 92,952 identifiers → 14,183 resolved (15.3%). Outcomes: `no_context` 25,759, `missing` 13,889,
  `ambiguous` 214. Tiers seen: `tier1_local` 0.95 (7,469), `tier4_global` 0.55 (6,555),
  `tier3_receiver` 0.65 (159).
- Per-language resolved %: C# 15.6, Python 14.1, JavaScript 10.1, bash 5.5, razor 2.1, css/html 0.
- `symbols.visibility`: 26,925 NULL, 8,209 public, 3,534 private, 2 protected on the same scan.

Consequence: **absence of a resolved inbound edge is weak evidence of death** (≈85% of usage
sites are unresolved), while **presence of one is strong evidence of life**. The candidate rule
below treats resolution accordingly: it can only save symbols from being flagged, never add
flags. This preserves the locked-in conservative stance ("collisions hide dead code rather than
flagging live code").

## Product shape

- **Surface:** `miller references candidates [--json]` — a new operation on the existing
  `references` CLI verb. **No new MCP tool** (standing rule). **Not** added to `miller report`
  or the dashboard yet: the consensus keeps dead-code out of the rollup until this prototype
  earns confidence on real dogfood.
- **Posture:** facts, not a verdict. Output is a candidate list plus named-rule suppression
  counts and per-language coverage disclaimers. No suppression persistence, no state.

## Candidate rule

A symbol S (from `symbols`) is a candidate iff ALL of:

1. **Definition kind.** `S.kind ∈ {function, method, class, struct, interface, enum, delegate,
   property, constant}` — the definition kinds worth reporting. Excluded: `variable`, `field`,
   `import`, `export`, `module`, `namespace`, `enum_member`, `constructor` (constructors follow
   their type; fields/variables are too noisy for a prototype).
2. **Name-based liveness fails.** No row in `identifiers` has `name = S.name` outside S's own
   definition (an identifier is "inside" when `containing_symbol_id = S.symbol_id` OR it lies
   within S's `[start_byte, end_byte]` span in S's file). Same-name matches anywhere else in the
   workspace — any file, any language — count as alive. Conservative by construction.
3. **No resolved inbound evidence.** Zero `identifier_resolutions` rows with
   `target_symbol_id = S.symbol_id` originating outside S (per the same inside-test on the
   resolved identifier), AND zero `relationships` rows with `to_symbol_id = S.symbol_id` from
   outside S. This is where v4 earns its keep: a resolved edge from an *aliased* usage (identifier
   name ≠ symbol name) rescues a symbol that name-matching alone would falsely flag.
4. **No named suppression applies** (see below).

### Suppression rules (each named, each counted in output)

| Rule id | Suppresses | Source |
|---|---|---|
| `public_api` | `visibility = 'public'` or language-equivalent exported forms | `symbols.visibility` |
| `visibility_unknown` | `visibility IS NULL` — the extractor did not record visibility for this symbol/language, so public exposure cannot be ruled out | `symbols.visibility` |
| `test_symbol` | `is_test = 1`, or any ancestor via `parent_symbol_id` has `is_test = 1` | `symbols.is_test` |
| `entry_point` | well-known entry names per language (e.g. `Main`/`main`, `Program`), plus symbols under a `Program.cs`-style startup file heuristic per language | `symbols.name`/`path` |
| `framework_bound` | symbol (or an ancestor) is `containing_symbol_id` of any `structural_facts` row — routes, handlers, bindings, DI registrations, etc. Broad on purpose | `structural_facts` |
| `annotated` | symbol carries any `symbol_annotations` row (attributes/decorators frequently mean reflection/framework discovery — `[Fact]`, `[JsonProperty]`, `@app.route`, …) | `symbol_annotations` |
| `generated_path` | path matches conservative generated-code globs (`*.g.cs`, `*.generated.*`, `obj/`, `bin/`, `node_modules/`, `*.designer.cs`, `wwwroot/lib/`) | `symbols.path` |
| `low_evidence_language` | the symbol's language has zero identifier rows in this artifact (nothing to test liveness against — e.g. css/html) | computed |

`visibility_unknown` is the honesty rule the live data forces: 26,925 of 38,670 symbols carry
NULL visibility. A prototype that flagged them would be guessing about public exposure. The
output's suppression counts make the cost visible per repo, which is exactly the evidence needed
to decide whether per-language visibility inference is worth extractor work later.

### Confidence labels (per candidate)

- `strong` — the candidate's language has resolution coverage ≥ 10% in *this artifact*
  (measured: resolved identifiers / identifiers for that language) AND the symbol's file had at
  least one resolution attempt. Both name evidence and resolution evidence agree it is unused.
- `moderate` — everything else that survives the rule (name evidence alone; resolver largely
  blind in this language).

Thresholds are computed from the artifact at query time — never hardcoded per language — so the
labels stay honest as the resolver improves.

## Output

**Compact:** header (`candidates: N of M symbols examined`), one line per candidate —
`name kind language path:line visibility confidence [name_matches=0 resolved_in=0 calls_in=0]` —
then a footer: suppression counts by rule id and a per-language coverage table
(`csharp: 15.6% resolved; razor: 2.1% — name-evidence only`). Bounded (default top 50 by
file path; `--limit`).

**`--json`:** new contract doc `docs/contracts/references-candidates-v1.md` (additive, versioned
per the contract discipline): `schema_version: 1`, `candidates[]` (symbol_id, name, kind,
language, path, start_line, visibility, confidence, evidence{name_matches, resolved_inbound,
calls_inbound}), `suppressions{rule_id: count}`, `language_coverage[]` (language, identifiers,
resolved_pct), `examined`, `artifact{artifact_id, revision, reference_resolution_version}`.
Advertised in `capabilities --json` under `optional_features.references_candidates`.

## Architecture

Follows the `ReferenceExportReader` seam exactly — no new architecture:

- `Miller.Core`: pure candidate/suppression/confidence logic over plain row records
  (`DeadCodeCandidates.Evaluate(...)`) — unit-testable in milliseconds, zero I/O.
- `Miller.Indexing`: `DeadCodeCandidateReader` — SQL that pages the needed rows (symbols of
  candidate kinds; identifier name-match counts via indexed lookups; resolved-inbound counts;
  structural-fact/annotation suppression sets) and feeds `Miller.Core`. One pass, no full-table
  materialization of identifiers.
- `Miller.Server`: `CliDispatch` wiring (`references candidates`), compact render, JSON
  serializer context entries, `CliCapabilities` advertisement.

## Testing

- Fast suite: contract-faithful v4 fixtures (real column shapes incl. `identifier_resolutions`
  with the `outcome='resolved' ⇔ target NOT NULL` CHECK — per the fixture-fidelity rule).
  Cover: candidate found; saved-by-name-match; saved-by-resolved-alias-edge (identifier name ≠
  symbol name); each suppression rule fires and is counted; confidence split; visibility NULL
  path; JSON shape.
- Scale suite: one test scanning a small multi-language fixture with the real binary, asserting
  the command runs end-to-end and per-language coverage renders.
- CLI contract tests extend `CliDispatchTests` (existing seams: `SetIdentifierTarget`,
  `MarkSymbolAsTest`).

## Evidence gate (definition of "earned confidence")

Before this feature is mentioned in `miller report`, the dashboard, README, or agent guidance:

1. Dogfood on the Miller repo and the julie-extractors repo (registered workspace).
2. Hand-verify every candidate on the Miller repo (expected: a short list; non-public C# symbols
   with zero name matches are rare in a heavily-tested codebase).
3. Record precision in a findings doc (`docs/findings/`). If the list is noisy, the rule
   tightens or the feature stays CLI-only and undocumented — it does not ship noisy.

## Out of scope (YAGNI)

- Collision refinement (flagging symbols whose every name-match resolves elsewhere) — needs
  trust in tier-4 (0.55) global resolutions; revisit with resolver improvements.
- Suppression persistence, ignore-lists, or per-repo config.
- Dashboard panel, `miller report` section, MCP tool — gated on the evidence above.
- Cross-workspace candidates.

## Acceptance criteria

- [ ] `miller references candidates` and `--json` run against a v4 artifact; compact and JSON
      shapes match this doc; `--limit` honored.
- [ ] Rule fidelity proven by fast tests: alive-by-name, alive-by-resolved-alias-edge,
      alive-by-calls-edge each prevent candidacy; all eight suppression rules fire, are counted,
      and are named in output.
- [ ] Confidence labels derive from artifact-measured per-language coverage (no hardcoded
      language lists).
- [ ] `references-candidates-v1.md` contract committed; `capabilities --json` advertises it;
      contract test added.
- [ ] Fast suite stays fast; the one real-binary test is `Category=Scale` via
      `ScaleTestSupport`.
- [ ] Dogfood evidence recorded in `docs/findings/` with hand-verified precision on the Miller
      repo (evidence gate for any further surfacing).
