# P1 Task 4 — Docs map + cross-contract consistency pass

**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`
**Branch:** `worktree-semantic-p1` (from `2c81b71`)
**Status:** complete; verification green.

## 1. Docs map

`docs/README.md` "Current docs" gains three one-line hooks, placed directly after the existing
`contracts/canary-telemetry-v1.md` line so the semantic contracts sit together:

- `contracts/semantic-sidecar-protocol-v1.md` — frozen wire protocol, knob table, `prepare`, backend
  selection, conformance bars.
- `contracts/vectors-v1.md` — frozen `vectors.db` artifact: identity, invalidation matrix, cursors,
  storage schema, shadow lifecycle, status vocabulary.
- `../eval/sidecar-conformance/` — 39-text corpus + CPU goldens, gated by `generate.py --verify`.

All three link targets verified to exist.

## 2. Consistency table

Documents: **A** = `docs/contracts/semantic-sidecar-protocol-v1.md`, **B** = `docs/contracts/vectors-v1.md`,
**C** = `docs/contracts/canary-telemetry-v1.md`, **D** = `docs/plans/2026-07-19-miller-semantic-integration-design.md`,
**E** = `eval/sidecar-conformance/README.md`. Every literal grepped across all five.

| # | Literal | A | B | C | D | E | Verdict |
|---|---|---|---|---|---|---|---|
| 1 | `Qwen3-Embedding-0.6B` | 3 | 2 | – | 1 | 1 | consistent |
| 2 | pins id `qwen3-0.6b-f16` | 2 | 1 | – | – | 2 | consistent (also golden `model` field) |
| 3 | pins id `bge-small-en-v1.5-f32` | 5 | 3 | – | – | – | consistent |
| 4 | short name `bge-small-f32` | 1 | 1 | – | – | 1 | reconciled — see F1 |
| 5 | qwen3 sha256 `421a27e5…4340` | 1 | 1 | – | – | 1 | identical |
| 6 | bge sha256 `bf40c42a…9b65` | 1 | 1 | – | – | – | identical |
| 7 | sha256 source = `bench-pins.json` | ✓ | ✓ | – | – | ✓ | single source, no re-derivation anywhere |
| 8 | native dims 1024 / 384 | ✓ | ✓ | – | ✓ | ✓ | consistent |
| 9 | lane dims 512 / 384 | ✓ | ✓ | – | ✓ | ✓ | consistent |
| 10 | lane `vec0-int8-512-cosine-v1` | 0 | 4 | 0 | 0 | 1 | consistent (A defers lane to B by design) |
| 11 | lane `vec0-int8-384-cosine-v1` | 0 | 3 | 0 | 0 | 0 (in golden rows) | consistent |
| 12 | `vec0-int8-256-cosine-v1` | 0 | 0 | 2 | 0 | 0 | **example, not a pin** — see F2 |
| 13 | norm tolerance `1e-3` | 1 | 1 | – | – | 3 | identical value; scope wording — see F3 |
| 14 | cosine bar `0.999` | 1 | 1 | – | – | 4 | identical |
| 15 | dims bar "exactly equal to the lane" | ✓ | ✓ | – | – | ✓ | identical |
| 16 | schema `julie.embedding.sidecar` | 5 | – | – | 1 | – | consistent |
| 17 | `invalid_request` | 7 | – | – | 1 | – | consistent (D already amended) |
| 18 | `invalid_json` | 5 | – | – | 1 | – | consistent |
| 19 | `unknown_method` | 6 | – | – | 1 | – | consistent |
| 20 | `internal_error` | 7 | – | – | 1 | – | consistent |
| 21 | old codes `parse_error`/`invalid_params`/`serialize_error` | 3 each (D1 only) | 0 | 0 | 0 | 0 | reconciled — survive only inside A's Deviations D1 |
| 22 | `embed_error` | 3 (D1 only) | 0 | 1 | 0 | 0 | **different namespace** — see F4 |
| 23 | `corpus_generation` = `cards-v1-chunks-v1` | – | 2 | 1 (example) | – | – | identical value |
| 24 | quantization order slice → renormalize → quantize | ✓ (knob table + note) | ✓ (2×) | – | ✓ (§ "MRL slice-then-renormalize order") | ✓ (§ Lane derivation order) | consistent, same order in all four |
| 25 | qwen3 `query_instruction` | 1 | 1 | – | – | – | byte-identical (incl. literal `\n` + trailing space) |
| 26 | bge `query_instruction` | 1 | 1 | – | – | – | byte-identical (incl. trailing space) |
| 27 | `document_instruction` = `""` both models | ✓ | ✓ | – | – | ✓ | consistent |
| 28 | EOS append `<\|endoftext\|>` (qwen3 only) | 2 | 1 | – | 1 | 1 | consistent; provenance = `bench.py:28`/`:151`/`:156`, not pins |
| 29 | pooling `last` / `cls` | ✓ | ✓ | – | ✓ | ✓ | consistent |
| 30 | L2-always normalization | ✓ | ✓ | – | ✓ | ✓ | consistent; provenance = `--embd-normalize 2`, `bench.py:65` |
| 31 | llama.cpp build `b10068` | 1 | 0 | – | – | 2 | consistent (B has no llama.cpp claim to disagree with) |
| 32 | `encoder_fingerprint` | 1 | 10 | 3 | 3 | – | semantics consistent; format — see F5 |
| 33 | `MILLER_SEMANTIC` three-state `off\|shadow\|on` | 1 (`off` only) | 4 | 5 | 2 | – | consistent; A only asserts the `off` zero-work guarantee |
| 34 | sqlite-vec pin `0.1.9` | – | ✓ | – | – | – | single-doc, matches `scripts/spike-pins.json` + P0 findings |

**34 literals × 5 documents.** Mismatches found: 0 value mismatches. 5 items needed a
recorded reading or a reconciling cross-reference (F1–F5); 1 cross-link gap fixed.

## 3. Findings and reconciliation

**F1 — `bge-small-f32` short name (RECONCILED, in my ownership).** The golden filename
`golden-bge-small-f32.jsonl` and the golden rows' `model` field carry the P1 plan's short name, while both
contracts freeze the pins key `bge-small-en-v1.5-f32` (A § Deviations D7; B's blockquote at line 110). The
values do not conflict — they are a fixture-local label vs. the model identity — but nothing said so in the
fixture README. **Fixed:** added a cross-reference blockquote to `eval/sidecar-conformance/README.md`
stating the short name is fixture-local and pointing at `bench-pins.json` for identity.

**F2 — canary `vec0-int8-256-cosine-v1` (EXAMPLE — I agree with Task 2; canary left untouched).** Both
occurrences read unambiguously as illustrations, not pins:
- `canary-telemetry-v1.md:473` — value column literally reads *"Opaque lane id, e.g.
  `vec0-int8-256-cosine-v1`"*. The `e.g.` is in the frozen text, and the column type is "Opaque lane id",
  i.e. the field is deliberately not enumerated.
- `canary-telemetry-v1.md:589` — inside a JSON output example whose sibling values are equally synthetic
  (`encoder_fingerprint: "3f9a1c22b0e4d781"`, `miller_versions: ["1.14.0+abc1234"]`).

No edit to the canary contract; nothing to report as a defect. (Optional lead call: refreshing the example
to the pinned 512 lane would remove a future reader's double-take, but the contract is frozen and the text
is already correct as written.)

**F3 — norm bar scope: wire floats vs int8 storage codes (CONSISTENT).** A § Conformance carries the lead's
added qualification ("Emitted vector" = wire float; int8 codes bounded by dual cosine + code-range instead).
E states the same rule at greater length (§ Tolerance policy, "The norm bar governs float vectors, not int8
storage codes", plus the ~1.5e-3 quantization-physics explanation and the two int8 rows in its table). B does
not restate the qualification: its § Conformance item 4 delegates by reference — *"emitted vectors satisfy
the frozen tolerance policy in the [sidecar protocol contract]"* — and B independently pins the same
division of labor at line 145 ("The wire therefore never carries quantized vectors"). Consistent by
delegation; no third statement of the bar to drift. No edit needed.

**F4 — `embed_error` collision (NOT a mismatch; namespace note for the lead).** `embed_error` appears in
`canary-telemetry-v1.md:184` as a `fallback_reason` enum value ("Sidecar returned an application-level embed
error") and in A only inside § Deviations D1 as a *rejected* wire error code. These are two different
namespaces — a Miller-side telemetry classification vs. the sidecar wire vocabulary — and both are correct
in place. Worth knowing that the reason a canary row says `embed_error` is that the sidecar sent
`internal_error`; A's § Errors makes that mapping derivable. No edit; recorded so a future reader does not
"fix" one to match the other.

**F5 — LEAD-OWNED, reported not fixed: `encoder_fingerprint` format across contract and telemetry.**
- `vectors-v1.md` freezes the stored value as `sha256:<64 hex>` (algorithm-tagged, 64 hex chars).
- `canary-telemetry-v1.md:472` types `canary_encoder_fingerprint` as *"Opaque lowercase hex, ≤32 chars"*,
  and its example at line 588 is 16 hex chars with no `sha256:` prefix.

The telemetry field is evidently a truncated, untagged form of the artifact value (sensible: telemetry
cardinality/size), but **neither document says so**, and `sha256:<64 hex>` is neither ≤32 chars nor
lowercase-hex-only once the tag is included. A P2b implementer stamping `vectors_meta.encoder_fingerprint`
straight into telemetry would violate the canary contract's stated field constraint. Both files are
lead-owned (canary contract; the design doc is where the truncation rule would most naturally be stated), so
this is reported rather than fixed. Suggested minimal resolution: one clause in the canary contract's
`canary_encoder_fingerprint` row naming it the first N hex chars of `vectors_meta.encoder_fingerprint` with
the `sha256:` tag stripped. Impact if left as-is: an ambiguity at P2b telemetry-stamping time, not a
contract-value conflict.

**Cross-link gap (FIXED, in my ownership).** A links to B (×3) and to the fixture dir; B links to A (×3) and
to the fixture dir; E linked to **neither contract**. Added a cross-reference paragraph to E naming
A § Conformance (group C) and B § Conformance (item 4).

## 4. Verification

| Invariant | Command | Scope | Result |
|---|---|---|---|
| Consistency table has zero unreconciled mismatches in owned files | grep sweep, 34 literals × 5 docs (table above) | Task 4 | PASS — 0 value mismatches; F1 + cross-link gap fixed in owned files; F5 reported to lead |
| Fixture gate still green after the README cross-ref edit | `python3 eval/sidecar-conformance/generate.py --verify` | Task 3 fixtures | PASS — `CONFORMANCE PASS: 78 vectors across 2 models (dims exact, \|norm-1\| <= 0.001, cosine >= 0.999) in 16.7s`; CPU backend proof held (`layers assigned to ['CPU']`) |
| Docs-map link targets exist | `ls` on all three new targets | Task 4 | PASS |

No `src/` or `tests/` files touched — no build/test run needed at worker scope (docs + fixture README only).

## 5. Files changed

- `docs/README.md` — three contract/fixture hooks in the active section.
- `eval/sidecar-conformance/README.md` — cross-reference paragraph to both contracts; short-name blockquote
  (F1). No content/tolerance/corpus changes.

Not touched (out of ownership): `docs/contracts/canary-telemetry-v1.md`,
`docs/plans/2026-07-19-miller-semantic-integration-design.md`,
`docs/contracts/semantic-sidecar-protocol-v1.md`, `docs/contracts/vectors-v1.md` — the latter two needed no
cross-reference lines, they already link each other and the fixtures.

## 6. Concerns

1. **F5 is the only open item** and it is lead-owned. Low severity, but it lands in P2b's lap if unresolved.
2. The canary contract's frozen examples (256 lane, 16-char fingerprint) predate the pins. Correct as
   examples (F2), but they are the two places a P2 reader is most likely to misread as normative.
