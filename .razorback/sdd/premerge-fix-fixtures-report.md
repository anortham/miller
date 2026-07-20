# Pre-merge fix report — sidecar conformance fixtures

Worktree `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`, branch
`worktree-semantic-p1`. Files touched: `eval/sidecar-conformance/generate.py`,
`eval/sidecar-conformance/README.md`, both regenerated golden JSONL files. No other paths.

## Finding 1 (high) — verification normalized away failures and accepted NaN — FIXED

`generate.py` now gates the server's untransformed output in `validate_raw()` before anything reshapes it:

1. raw shape equals the model's declared `native_dims` from `bench-pins.json`;
2. every component finite — a NaN or ±Inf fails the run naming `model/text_id`;
3. raw L2 norm within the frozen `1e-3` of `1.0`, which is the actual `--embd-normalize 2` check, since the
   server is the thing that normalizes.

Only after those three does the generator re-normalize (float-precision hygiene). Because `--verify`
regenerates through the same `embed_lane()`, the checks cover both modes. The 250-wide batch-position probe
validates every stacked vector the same way. Goldens are written with `allow_nan=False` and parsed with
`parse_constant` rejecting non-standard literals, so `NaN`/`Infinity` cannot enter or leave a committed file.
`check_row` additionally re-checks committed vectors for finiteness and the committed `norm_native_raw`
against the norm bar.

New golden fields: `norm_native_raw`, `truncation_sensitive`.

## Finding 2 (high) — golden truncation contradicts the token-budget contract — STOPPED, gap reported

Tokenizer-based truncation was **not implementable as frozen**. The endpoints exist — probing the cached
`llama-server` b10068 live returned `{"tokens":[7592,2088,19817,4609,10719,15113]}` from `POST /tokenize`
and `{"content":" hello world"}` from `POST /detokenize` — so the blocker is contract semantics, not
capability. Three specific gaps in
`docs/contracts/semantic-sidecar-protocol-v1.md` § Truncation semantics (lines 351-361) and § Prompt
templates (line 379-384):

1. **No frozen value for `max_text_tokens`.** Line 327 defines it as a per-implementation field reported
   through `health`. v1 freezes no number, so two compliant sidecars may truncate the same text at different
   points and both be conformant. A golden cannot encode "the" contract truncation point.
2. **A token budget frozen to a character-based algorithm.** Line 356-358 says the combined string is fit to
   the budget and that this "mirrors the benchmark harness's `_fit` (`eval/model-bench/bench.py:144-156`)" —
   but `_fit` cuts at `context_length × 1.6` *characters* on a word boundary. Token-based and char-based cut
   at different places; the contract asserts both.
3. **Application order contradicts the cited reference.** Contract steps (lines 381-383) are prefix → append
   EOS → fit to budget. Fitting after the EOS append can cut the EOS marker that line 422 calls
   unconditional for Qwen3. `bench.py:150`/`:155` fit *before* appending EOS. The contract's own order and
   its cited implementation disagree.

Any golden I generated would have encoded a guess at all three. Per the instruction to stop rather than
invent semantics, I took the documented fallback: rows whose input the generator truncated now carry
`truncation_sensitive: true` and are exempt from the cosine-vs-golden check only. Every other check,
including the three new raw checks, still applies to them. `--verify` reports a sub-floor cosine on such a
row as an informational note, not a failure. Affected rows: `long-truncation-001`, `long-truncation-002` in
the bge goldens only (qwen3's 32K context truncates nothing). The other 37 rows per model stay under the
full strict gate. README § "Truncated rows are excluded from the cross-implementation cosine gate"
documents the gap and the three blocking points.

**Lead-owned follow-up:** freeze a `max_text_tokens` value per pinned model, pick token- or char-based
truncation (not both), and settle whether EOS is appended before or after the fit. Once those land the
goldens can be regenerated token-exactly through `/tokenize` and the exemption removed.

## Verification

Both goldens regenerated (78 vectors, 18.3s), then `--verify` green twice consecutively from the committed
goldens:

```
backend proof: layers assigned to ['CPU']
CONFORMANCE PASS: 78 vectors across 2 models (raw output finite and unit-norm, dims exact, |norm-1| <= 0.001, cosine >= 0.999) in 14.8s
```

CPU-backend proof still holds (`layers assigned to ['CPU']`, no offload device selected).

Negative test of the new gate, via two scratch copies of `generate.py` that inject a bad vector at one
corpus position (copies removed afterwards; working tree confirmed clean of them):

| Injection | Result | Exit |
|---|---|---|
| one vector replaced with all-`NaN` | `qwen3-0.6b-f16/ascii-ident-004: raw server output has 1024 non-finite component(s)` | 1 |
| one vector scaled by 3 | `qwen3-0.6b-f16/ascii-ident-004: raw server output L2 norm 3.000000 outside 1.0 +/- 0.001; the runtime did not honour --embd-normalize 2` | 1 |

Committed goldens confirmed free of `NaN`/`Infinity` literals; all 78 `norm_native_raw` values are exactly
`1.0`.

## Invariant

The gate cannot pass non-finite or denormalized raw output — validation happens on the server's
untransformed bytes, before any renormalization could repair them, in both generation and `--verify`. The
goldens do **not** yet encode contract-frozen truncation; the two rows where that matters are marked and
excluded from the cross-implementation cosine gate, with the contract gap recorded rather than papered over.

## Frozen numbers unchanged

Dims exact, `1e-3` norm tolerance, `0.999` cosine floor — all untouched.

---

# Addendum — truncation frozen, goldens regenerated token-exactly

Follow-up after the lead froze § Truncation semantics (`20fbb72`). All three reported gaps are closed, so
the fallback exemption is retired and the goldens now encode the contract's own cut point.

## What changed in `generate.py`

The bench harness's character budget is no longer used for truncation. `prepare()` now implements the
frozen algorithm against the running server's own tokenizer:

1. sanitize → prefix the role's instruction;
2. `eos_reserve` = token count of the model's EOS marker (`1` for qwen3's `<|endoftext|>`, `0` for bge);
3. tokenize the prefixed string, tail-truncate to the budget if it overruns;
4. round-trip stability rule — detokenize, retokenize, drop one more trailing token while the
   retokenization differs (`fit_to_budget()`);
5. append EOS, which is reserved and therefore always survives.

Applied to all 39 inputs in both lanes; it is a no-op below budget. Because preparation now needs the
tokenizer, `embed_lane()` builds the prepared texts *inside* the `LlamaServer` context instead of before it.

**One implementation decision worth recording.** The contract says the budget covers the entire model input
and that the fit tokenizes "exactly as embedding input is tokenized", but does not name special tokens.
bge-small's tokenizer wraps every input in `[CLS]` … `[SEP]` (verified live: `"hello world"` →
`[101, 7592, 2088, 102]` with `add_special=true`, `[7592, 2088]` without). I therefore measure that overhead
once per model (`special_token_overhead()`) and subtract it alongside `eos_reserve`. This is the only
reading that keeps the input inside the model's context — without it a prefixed text could tokenize to
exactly 512 content tokens and the server would reject the 514-token request. Recorded per row as
`generator.special_token_overhead` (`2` bge, `1` qwen3) and documented in the README. Flagging it as a
possible one-line clarification for the contract, not a blocker.

## Gate changes

- The truncation exemption is **removed**; `long-truncation-001/002` rejoin the strict cosine gate.
- The redundant `truncation_sensitive` field is dropped — with the cut point frozen it duplicated
  `input_truncated`, which `--verify` already compares.
- `prepared_chars` **added** to the compared-field set, so a wrong truncation point fails on its own
  evidence rather than depending on the cosine bar to notice.
- New `generator` fields: `max_text_tokens`, `special_token_overhead`.
- The raw-validation gate from the first pass is untouched.

## Results

Both goldens regenerated in one run (78 vectors, 15.6s). Truncation now happens at a much later, token-exact
point than the old character heuristic: `long-truncation-001/002` stabilize at **510 content tokens = 512
with specials**, exactly bge's frozen budget, at 2484 and 2512 chars (the retired char budget cut at 819).
Qwen3 truncates nothing — its 32768 budget leaves the ~8 KB longest text far inside. The stability loop
needed zero extra drops for both rows; the code handles the non-zero case regardless.

Independently reproduced outside the generator by driving `/tokenize` and `/detokenize` against a
standalone server: same 510-token stable cut, same `prepared_chars` of 2484 and 2512.

## Verification

`--verify` green twice consecutively, CPU proof intact both runs:

```
backend proof: layers assigned to ['CPU']
CONFORMANCE PASS: 78 vectors across 2 models (raw output finite and unit-norm, dims exact, |norm-1| <= 0.001, cosine >= 0.999) in 15.9s
```

Negative tests (scratch copies, removed after; tree confirmed clean):

| Injected defect | Failure | Exit |
|---|---|---|
| one server vector replaced with all-`NaN` | `qwen3-0.6b-f16/ascii-ident-004: raw server output has 1024 non-finite component(s)` | 1 |
| token budget cut 40 tokens early | `long-truncation-002: native cosine 0.998894 < 0.999`, `lane cosine 0.998595 < 0.999`, `prepared_chars 2325 != committed 2512` | 1 |

The second confirms the new invariant directly: shifting the cut by 40 tokens out of 510 fails both the
cosine bar and the `prepared_chars` comparison, so the goldens genuinely encode the frozen truncation rather
than merely tolerating some truncation.

## Invariant (updated)

The goldens encode contract-frozen, tokenizer-exact truncation — computed with the pinned tokenizer through
`/tokenize` + `/detokenize`, budget-reserved so the instruction prefix and EOS always survive — and the gate
cannot pass non-finite raw output, denormalized raw output, or a shifted truncation point. No row is exempt.

Frozen tolerance numbers still untouched: dims exact, `1e-3` norm, `0.999` cosine.
