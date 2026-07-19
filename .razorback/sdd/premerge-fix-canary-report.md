# Pre-merge fix report — canary telemetry contract (Findings A + B)

- Worktree: `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`
- Branch: `worktree-semantic-integration`, HEAD `3886ec5`
- Dirty state at finish: `docs/contracts/canary-telemetry-v1.md` modified by me; `eval/*` files modified by
  parallel agents (untouched by me).
- Commit SHA: none - parallel-lead-commit
- Files changed: `docs/contracts/canary-telemetry-v1.md` only.

## Finding A — gate now computable from contract-defined data

Added to `## The Success Event`, after the existing gate paragraph (which is unchanged):

1. **`### Where each clause is computed (load-bearing)`** — names two surfaces: local raw
   `tool_telemetry` rows (authoritative) and the aggregate export (approximation). Then defines:
   - *Success-rate clause (local)*: eligible rows in the exact `miller_version` cohort, grouped by
     assignment unit, per-unit rate = attributed successes ÷ eligible calls, arm-mean difference.
   - *Warm-latency clause (local)*: treatment population = eligible `treatment` rows with
     `canary_embed_warmth=warm`; **control population = all eligible control rows**, justified in one
     sentence — control rows never embed, so they always record `embed_warmth=none` and have no warm/cold
     split; the whole control arm is the steady-state lexical baseline. Statistic is nearest-rank p95 over
     integer `duration_ms`, pass rule `p95(treatment warm) ≤ 1.20 × p95(control)`, **indeterminate** (not
     a pass) below the minimum row counts.
   - *Warm-latency clause (export approximation)*: bucketed p95 = first ladder bucket whose cumulative
     count reaches `ceil(0.95 × calls)`; comparison is median-across-units of the arm's p95 rung, flagged
     when treatment's rung is strictly higher; explicitly labeled a coarse screen ("a flag means go compute
     the local clause"), since one rung spans up to 2.5×.
   - *Identifier non-inferiority*: pointer to the frozen parameters.
2. **`### Frozen analysis parameters`** — a table freezing every knob (see next section).
3. **Export field `total_latency_bucket_counts`** — bucketed `duration_ms` over the unit's eligible calls,
   same `latency_bucket` ladder, written for both arms, counts sum to `calls`. Export invariant text
   changed from "no `duration_ms`" to "**no raw `duration_ms` — bucketed total-latency counters only**",
   with the counters-and-enums rationale. Example JSON updated (`9+27+3+2 = 41 = calls`). Added a paragraph
   after the JSON stating the three bucket maps are separate marginals and no joint
   (warmth × total-latency) distribution is expressible — which is precisely why the export check is an
   approximation.
4. `canary_contract_version` stays **1**. Because "frozen means frozen" otherwise forbids added keys, I
   added a short *pre-ship amendment* exception paragraph to the header: until P2b writes the first row,
   no data exists that a v2 could protect; the exception is named as spent once the first row is written,
   and both prior amendments are listed. Without this the document contradicted itself.

Consequential ripple edits for consistency: the `duration_ms` row in the reused-columns table, the
`latency_bucket` preamble ("applied to both per-row semantic timing fields and — in the aggregate export
only — to `duration_ms`"), and its rationale note ("`duration_ms` remains raw **on the local row** … it is
bucketed on the way out of the machine").

### Frozen values: design-sourced vs judgment call

| Parameter | Value | Provenance |
|---|---|---|
| Unit of analysis | `(workspace_id, utc_date, query_class)` | Contract § Assignment (already frozen) |
| Estimator = difference in arm means of per-unit rates | — | Design §9.1 (per-unit / cluster-randomized) |
| Success-rate pass rule = CI lower bound > 0 | — | Design §9.1, §11 |
| Warm-latency threshold | ≤ 1.20 × control p95 | Design §9.1/§11 ("no >20% p95 warm-latency regression") |
| Min eligible calls per unit | **5** | Reused the contract's own export suppression floor (not a new number) |
| **Min included units per arm** | **30** | **JUDGMENT CALL** — design silent; below this the gate is reported underpowered, never a pass |
| **CI method** | **Welch two-sample 95% t-interval, two-sided** | **JUDGMENT CALL** — design says only "confidence interval excludes zero" |
| **p95 estimator** | **nearest-rank, `ceil(0.95 × n)`, no interpolation** | **JUDGMENT CALL** (task-directed); rationale: `duration_ms` is integer |
| **Min rows for latency clause** | **100 warm treatment and 100 control** | **JUDGMENT CALL** — at n=100 the nearest-rank p95 rests on 5 observations |
| **Identifier non-inferiority population** | shadow rows with `shadow_status=ok`, units `(workspace_id, utc_date, identifier)`, same 5-call floor | **JUDGMENT CALL** — population was previously unstated |
| **Identifier non-inferiority margins** | `top1_changed` per-unit rate 95% CI upper bound ≤ 0.05; mean `overlap_at_10` 95% CI lower bound ≥ 8.0; min 30 shadow units | **JUDGMENT CALL** — design and the P0 eval-protocol task set no numbers (checked design §8/§9/§11 and `docs/plans/2026-07-19-p0-governance-and-gates-plan.md` Tasks 5–8) |

I checked design §9 and §11 verbatim plus the P0 plan and the model-benchmark findings doc: the only
numbers already decided are the 20% latency threshold and "CI excludes zero". Everything else above marked
JUDGMENT CALL is mine and needs the user's eye.

**Contradiction avoided:** the Shadow Population section previously said the non-inferiority thresholds are
set by the eval-protocol task "not here". I did not delete that. Instead it now names **two distinct bars**
reported separately: the sealed-set retrieval bar (eval protocol's, unchanged) and the field-telemetry
margins over shadow rows (frozen here, because a gate clause computed from fields this contract defines has
to be computable from this contract).

## Finding B — qualified-name attribution

Evidence first (see MCP/tooling calls below): **the artifact has no qualified/fully-qualified name
column.** `symbols` carries `symbol_id, file_id, path, language, name, kind, signature, …,
parent_symbol_id, …` — no `qualified_name`/`fq_name`. So the contract specifies the hash over
`<ParentName>.<Name>` derived from the real columns `symbols.parent_symbol_id` → the parent row's
`symbols.name`, joined to the result's own `symbols.name`. That is exactly the shape
`SmartTargetResolver.ResolveQualifiedMember` (`src/Miller.Server/Resolution/SmartTargetResolver.cs:238-256`)
already accepts on `inspect target=`: it splits on the last dot and matches the immediate parent's name.

Amendments:

- New third array `canary_result_qualified_hashes`, written only when the qualified spelling differs from
  the bare name (i.e. the symbol has a parent); top-level symbols contribute no entry.
- **One shared truncation flag** (`canary_result_hash_truncated`), as instructed — justified in the text:
  the ≤10 cap is applied to the served-result list *before* hashing, so all three arrays truncate at the
  same result boundary. The qualified array may be *shorter* — that is absence, not truncation, and the
  doc says so.
- Matching rule clause 4 now unions all three arrays, and states a hash in two arrays is still one match.
- New **Conformance cases** bullet list (bare / qualified / path / top-level-no-parent / deeper spelling /
  double-counting) worked against a concrete example symbol.
- Privacy note rewritten to cover all three arrays: same `SetTarget` mechanism, same local-only exposure,
  excluded from every export.
- Field Reference row added for the new key; `canary_result_hash_truncated`'s "Written when" updated.
- **Explicit v1 limitation stated**: only the one-level `Parent.Member` form is hashed. A deeper spelling
  (`Ns.Type.Member`) does not attribute in v1 and counts as no conversion — declared as the conservative
  direction rather than left as a silent gap.

## Verification performed (documented consistency pass; no test suite exists for contracts)

1. Re-read the amended document end to end.
2. **Every gate clause names its data source and estimator** — success rate (local rows + Welch t-interval),
   warm latency (local raw `duration_ms` + nearest-rank p95, plus the labeled export approximation),
   identifier non-inferiority (shadow rows + population + margins). No clause now depends on a field the
   contract does not define.
3. **Export example matches the export field list** — `total_latency_bucket_counts` present in both the
   invariant text and the example; its counts sum to `calls` (9+27+3+2 = 41), matching the pre-existing
   `embed_latency_bucket_counts` sum (12+21+6+2 = 41).
4. **No contradicting reference remains** — grepped `duration_ms`, `total_latency_bucket_counts`,
   `qualified_hashes`, `hash_truncated`, `nearest-rank`, `no >20%` across the file and reconciled each hit:
   reused-columns table, `latency_bucket` preamble and rationale, export invariant, Field Reference,
   Shadow Population, Stability Rules.
5. **Frozen-vs-amendment contradiction resolved** — the "frozen means frozen / a genuinely required
   addition is a v2" rule now has an explicit, bounded, self-expiring pre-ship exception; without it the
   two new keys contradicted the Stability Rules.
6. Constraint honored: only `docs/contracts/canary-telemetry-v1.md` was modified; no `git add`/`commit` run.
   Prior amendments (version string `1.14.0+abc1234`, exact-set cohort matching) left intact.

## Tool calls used for evidence

- Miller MCP `inspect target=WorkspaceTargetHashResolver depth=overview` — confirmed the existing local
  hash-reversal mechanism the contract cites.
- Miller MCP `search query="qualified target resolution parent dotted name inspect" mode=source` — no hits
  (this is itself the signal: no qualified-name concept in source text), so I fell back to:
- `sqlite3 .miller/symbols.db ".schema symbols"` — authoritative proof that no qualified-name column exists.
- `grep` for `LastIndexOf('.')` → `SmartTargetResolver.ResolveQualifiedMember` and `EditService`, plus a
  `Read` of `SmartTargetResolver.cs:225-269` — confirmed the one-level `Parent.Member` resolution shape.
- `Read`/`grep` of `TelemetryScope.SetTarget` — confirmed it hashes the exact passed string (the root cause
  of Finding B).

## Concerns for the user

1. **Five frozen numbers are mine, not the design's**: min 30 units/arm, Welch 95% t-interval, min 100 rows
   per latency population, identifier margins (0.05 top-1 change ceiling, 8.0 overlap floor), min 30 shadow
   units. All are conventional and defensible, but they are a *gate* — please sanity-check them.
2. **Overlap floor of 8.0 is the softest number.** It presumes the hybrid arm should preserve 8 of 10
   lexical top-10 results on identifier queries. If the fusion profile is intentionally more aggressive on
   the mixed class, this floor may be tighter than intended for identifiers.
3. **The pre-ship amendment exception is new policy text**, not just a fix. It is deliberately
   self-expiring, but it does weaken "frozen means frozen" prose. If you would rather this contract had
   simply become v2, that decision belongs to you and is a small edit.
4. **The export latency screen is genuinely weak** and I said so in the document rather than dressing it up:
   bucket rungs cannot confirm a 20% regression. Multi-operator acceptance of the latency clause therefore
   rests on operators running the local computation and reporting its result, not on the export alone. If
   P5 expects the export to settle the latency gate by itself, that expectation needs revisiting now.
