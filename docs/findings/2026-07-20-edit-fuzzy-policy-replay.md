# Edit fuzzy-policy replay + plan-time stale-target wait (design §7.4–§7.5)

**Date:** 2026-07-20 · **Task:** P2 D2 · **Status:** measured; one policy change shipped, one rejected

Design §7.4 asked for a fuzzy policy proposal "gated on a replay corpus of historical failures", on the stated
premise that "the 160-char snippet cap and distance ceiling 3 yield zero fuzzy successes". §7.5 asked for a
plan-time bounded convergence wait mirroring the apply path. This note reports what the measurement actually
found, including two places where the design's premise did not survive contact with the data.

---

## 1. Corpus provenance — why telemetry cannot be replayed directly

The intended corpus was historical edit failures from `telemetry.db`. **It cannot supply one.** The ledger is
enum/counter-only by construction, so no historical call retains the text a replay would need:

| Column / field | Value on `tool='edit'` error rows |
|---|---|
| `error_message`, `error_detail` | NULL on all 61 rows (the edit path never writes them) |
| `target_hash` | present on all 61 — a hash, not a path |
| `metadata_json` keys | `format, apply, allow_stale, has_scope, match_mode, has_query, has_anchor, has_line, server_version, edit_failure_reason, index_state, wait_reason` |

`has_query` / `has_anchor` / `has_line` are booleans, not values. There is no `old_text`, no `new_text`, no file
content, and no matched-mode field anywhere in the ledger.

This is the privacy contract working as designed, and it should not be relaxed to enable tuning. The consequence
is that telemetry can answer **"which policy is worth changing"** (how often each rung is even reached, and with
what outcome) but not **"what would a different threshold have matched"**. The first question turned out to be
decisive, so a text corpus was never needed to reach a verdict.

Replay therefore uses two sources:
- **Telemetry (real, N=253 edit calls):** rung reachability and failure-bucket distribution — §2.
- **A synthesized corpus (N=10):** the precision/recall harness for the policy change itself — §4. It lives as
  an executable test (`EditToolTests.FuzzyPolicyReplay_*`, `RunReplay()`), not as numbers in prose, so a future
  policy change is re-measured rather than re-argued.

---

## 2. Telemetry baseline (`~/.miller/telemetry.db`, read-only snapshot 2026-07-20)

**All edit calls:** 253. **All `replace_text`:** 212 calls / 60 errors (28.3%).

**Version-stamped cohort** (rows carrying `server_version`, i.e. the D1 gate cohort) — `replace_text`:

| Metric | Value |
|---|---|
| Calls | 52 |
| Errors | 11 (**21.2%**) |
| `stale_target` | 6 (55% of errors) |
| `no_match` | 4 |
| `target_not_found` | 1 |

Against design §7's gate ("replace_text error rate <10% on the instrumented, version-stamped cohort"), the
cohort currently sits at **21.2%** — the gate is measurable now and not yet met. D1's handoff called this
correctly.

**Rung reachability — the decisive number.** `match_mode` decides whether the fuzzy rung is entered at all:
`exact` and `normalized` never reach it; `auto` reaches it only after exact and normalized both fail.

| Requested mode | ok | error | Reaches fuzzy rung? |
|---|---|---|---|
| `exact` | 125 | 47 | no |
| `normalized` | 10 | 3 | no |
| `auto` (incl. unset) | 12 | 9 | yes, last |
| `fuzzy` | 5 | 1 | yes, directly |

- **All 11 errors in the version-stamped cohort requested `match_mode=exact`.** Zero of them entered the fuzzy
  rung. **Any fuzzy policy change would have altered exactly zero historical stamped outcomes.**
- Across all history, at most **10 of 60** `replace_text` errors (17%) were even under a mode that reaches fuzzy.
- **The design's "zero fuzzy successes" premise is false as of this snapshot:** explicit `match_mode=fuzzy` has
  **5 successes and 1 failure**. The premise appears to predate those calls.

**Conclusion for §7.4:** fuzzy policy is not where the edit error rate lives. The dominant lever is what steers
agents to `exact` (D1 §7.2's territory), and after that, `stale_target`. Fuzzy policy work is therefore justified
only to the extent it is cheap and strictly safe — which sets the bar used in §4.

**Conclusion for §7.5:** `stale_target` is 55% of stamped errors, and **every one of the 7 historical
`stale_target` rows carries `wait_reason=none`** — not one of them waited. 5 of the 6 `replace_text` ones are
`apply=0` with an indexed selector (`has_anchor` ×4, `has_line` ×1), i.e. the plan-time indexed-candidate
verification exit. That is the exit §5 fixes. Two pairs are ~17ms and ~27s apart — the same agent retrying by
hand what the tool should have waited for.

---

## 3. Current fuzzy policy, as verified in code

| Element | Value | Site |
|---|---|---|
| Snippet cap | `MaxFuzzySnippetChars = 160` | `TextReplaceMatcher.cs:11` |
| Cap applied to | **raw `oldText.Length`** (pre-change) | `PlanFuzzy`, `TextReplaceMatcher.cs:93` |
| Distance ceiling | ≤12 chars → 1; ≤48 → 2; else 3 | `MaxFuzzyDistance`, `:354` |
| Distance measured on | normalized joined target text | `PlanFuzzy`, `:105` |
| Window shape | exactly `targetLines.Count` lines | `PlanFuzzy`, `:109` |

Two mismatches fall out of that table:

1. **The cap and the threshold measure different strings.** Matching normalizes first — `NormalizedTargetLines`
   strips indentation, trailing whitespace and line endings — but the cap counted the raw snippet. A 3-line
   block at 12-space indentation spends ~40 raw chars on whitespace that is discarded before any comparison
   happens, so snippets whose *comparable* content was well inside 160 were refused for being "too long".
2. **The ceiling is flat in absolute terms, so it is regressive in relative terms.** A 12-char snippet tolerates
   1 edit (8.3%); a 160-char snippet tolerates 3 (1.9%). Longer snippets — which have more opportunity to drift —
   get proportionally *less* tolerance.

A third limitation is structural, not a constant: fuzzy only considers windows with exactly the target's line
count, so it cannot absorb an added or deleted line. Out of scope here; recorded in §6.

---

## 4. Replay: two candidate policies, one shipped

Harness: `EditToolTests.RunReplay()` over a 10-case corpus — 6 **recall** cases (exact and normalized both fail;
a fuzzy match on a specific line is the correct outcome, and the harness checks the matched *line*, not merely
that something matched) and 4 **precision** cases (a fuzzy match would splice the wrong span — sibling switch
branches, a constant table with a wrong key, a changed argument). Precision cases are the brake: the failure mode
of a loose fuzzy policy is not a miss, it is a silent wrong-span write.

Ship bar, per the task gate: **strictly more recall hits AND zero new precision breaks.**

### Candidate P1 — cap measures comparable text (SHIPPED)

Replace `oldText.Length > 160` with `targetText.Length > 160`, where `targetText` is the normalized joined target
the distance scan actually compares. Same constant; corrected subject.

| Policy | Recall hits | Precision breaks |
|---|---|---|
| Before | 4 / 6 | 1 / 4 |
| After (P1) | **6 / 6** | **1 / 4** (the same one) |

Strict improvement: two more recall hits, no new precision break. The newly-matched cases
(`indented_single_line_over_raw_cap`, `indented_multiline_over_raw_cap`) are snippets copied out of an indented
C# method body whose raw length exceeds 160 purely through leading whitespace. Precision is untouched because
the distance threshold, the window shape, and the ceiling are all unchanged — P1 only decides *which snippets
are admitted to the scan*, never *what counts as a match*.

The before/after delta is not asserted in prose: `FuzzyPolicyReplay_CapChangeAffectsOnlyTheIndentedRecallCases`
pins that exactly two corpus cases have raw length >160 with comparable length inside it, and that both are
recall cases. Every other case behaves identically under both policies, so the "before" column is derived, not
claimed.

**The measurement also found a precision break the current ceiling already had, unrelated to this change.**
`constant_table_wrong_key` — `old_text = 'case 7: return "one";'` against a table containing
`case 1: return "one";` — **matches**, at distance 1 on a 21-char line (ceiling 2). The one differing character
*is* the discriminator, and fuzzy splices the wrong arm. It is recorded in the corpus as `KnownCeilingGap = true`
and asserted rather than deleted, so it stays visible. It pre-dates this task, is unchanged by P1, and is the
strongest single argument against P2 below.

**Cost is neutral by construction, which is why this is safe.** The cap exists to bound an O(n·m) Levenshtein
scan per window. Because normalization never lengthens text, the old rule (raw ≤160 ⟹ normalized ≤160) and the
new rule (normalized ≤160) bound the scan *identically*. The new rule admits snippets with unbounded raw
whitespace, but `NormalizedTargetLines` is O(raw) linear and total normalized content stays ≤160 chars, which
also caps the line count of any candidate window. A whitespace-only snippet still fails earlier at
`targetLines.Count == 0`.

### Candidate P2 — proportional distance ceiling (REJECTED)

Replace the flat 1/2/3 ladder with a length-proportional ceiling (~1 edit per 16 comparable chars, clamped).
Rejected on the evidence, before implementation, on three independent grounds:

1. **Zero measured upside.** Per §2, no error in the version-stamped cohort reached the fuzzy rung at all. There
   is no historical failure the change could have rescued, so its benefit is entirely speculative.
2. **Precision is the wrong thing to spend, and it is already overdrawn.** The corpus's surviving precision
   cases are near-miss siblings 3–8 edits apart — exactly the band a proportional ceiling opens up on longer
   snippets — and the ceiling *already* produces one wrong-span match (`constant_table_wrong_key`) at the
   current setting. A policy that is already too loose at one measured point is not a candidate for loosening.
   A wrong-span match writes plausible code to the wrong place; the preview diff is the only guard, and an agent
   applying with `apply=true` in one call never sees it.
3. **It changes what counts as a match, not just what is scanned.** Unlike P1 it has no cost-neutrality or
   precision-neutrality argument, so it would need a real corpus to justify — and §1 established that a real
   corpus is not retrievable.

**Current policy kept:** `MaxFuzzyDistance` is unchanged at 1/2/3. If fuzzy-rung traffic ever becomes material
(watch `match_mode` ∈ {`auto`,`fuzzy`} error counts in the stamped cohort), re-run `RunReplay()` against a
corpus grown from real cases and revisit. The evidence points at *tightening* short-snippet tolerance before
loosening anything — but that would change existing match behavior with no measured demand, so it is recorded in
§6 rather than shipped on a 10-case synthesized corpus.

---

## 5. Plan-time stale-target bounded wait (§7.5)

**Before:** the apply path waited (`EditService.TryRecoverFreshness`, 2.5s budget / 150ms polls) before refusing
a stale file. The plan path did not. An indexed edit candidate whose content.db chunk pre-dated the current disk
text failed verification and returned `stale_target` instantly — while the leader was, typically, about one
debounce tick from converging that same file.

**After:** `EditService.WaitForCandidateConvergence` mirrors the apply path's shape and shares its
`RecoveryOptions` budget. On the indexed-candidate verification failure it asks the write-through to converge
that one file, then re-discovers and re-verifies candidates until the plan succeeds or the budget expires.

Three deliberate differences from the apply-path wait, all with reasons:

- **It polls the success condition, not the freshness gate.** The apply path polls
  `FreshnessGate.Check` (symbols.db hash vs disk). Plan-time success depends on **content.db**, which converges
  from `revision_file_changes` and can lag symbols.db by a tick — a fresh gate would not prove a verifying
  candidate exists. The loop re-runs `FindCandidates` + re-plans, which is the actual thing being waited for.
- **`StaleRecoveryAttempt.None` returns immediately.** A write-through with no recovery path (reader with no
  leader; test recorders) behaves exactly as before — no budget spent, no behavior change.
- **A transient `SqliteException`/`FileNotFoundException`/`InvalidOperationException` during the wait reads as
  "not yet converged"**, matching `TryRecoverFreshness`'s handling of a mid-swap DB, so `Execute` keeps its
  promise never to throw for an expected condition.

The disk text is read once, before the wait, and is not re-read: the index is converging *toward* disk, so
re-planning against the already-read content is correct and avoids a TOCTOU window.

**Scoped deliberately to one exit.** Two other plan-time paths were considered and left alone:

| Path | Bucket | Why no wait |
|---|---|---|
| `EditPlanFailureMessage` — disk lacks `old_text`, indexed source still has it (`EditService.cs:1104`) | `stale_target` | The index is **behind disk** and disk is authoritative. Converging removes `old_text` from the index too; the plan still cannot succeed. Waiting would only downgrade the message from `stale_target` to `no_match`. |
| `IndexedEditCandidateState.NoMatch` | `no_match` | Not a stale bucket, and it already falls back to whole-file disk matching, which usually succeeds. Widening the wait here would spend budget on the common case. |

**Telemetry.** A wait stamps `wait_reason = "edit_stale_converge"` (a fixed enum; `SetWaitReason` is first-wins,
matching `index_load` / `workspace_refresh`). It is threaded out through `EditResult.StaleWaitPerformed` and
stamped by `EditTool`, because `EditService`'s class contract keeps it off the telemetry surface. This makes the
fix measurable: historical `stale_target` rows all read `wait_reason=none`, so any future row reading
`edit_stale_converge` is a wait that happened, and a `stale_target` row still reading `none` means recovery was
unavailable rather than slow.

The pre-existing apply-path wait (`TryRecoverFreshness`) remains **unstamped** — it is outside D2's change and
threading the flag through the symbol-op recovery re-entry is a separate change. Recorded in §6.

---

## 6. Open items (not done here)

1. **Apply-path wait is unstamped.** `TryRecoverFreshness` spends up to 2.5s without recording `wait_reason`, so
   apply-path waits are invisible in telemetry. Small, self-contained follow-up.
2. **The distance ceiling is too loose on short lines** (`constant_table_wrong_key`, §4): at ≤12 chars it allows
   1 edit and at ≤48 it allows 2, which is enough to match a sibling whose *only* difference is the discriminating
   character — a constant-table key, an enum arm, an index. A candidate rule is to refuse a fuzzy match whose
   edits fall entirely inside a numeric or single-token difference. Not shipped: it changes match behavior, and
   the evidence is one synthesized case with no observed real-world instance (fuzzy-rung traffic is ~5% of edit
   calls). Revisit together with item 3.
3. **Fuzzy cannot absorb a line count change.** `PlanFuzzy` only scans windows of exactly `targetLines.Count`
   lines, so an added or deleted line inside the snippet is unmatchable at any ceiling. This is a bigger
   limitation than either constant, and no constant tuning reaches it.
4. **No `matched_mode` in telemetry.** The ledger records the *requested* `match_mode`, never the rung that
   actually matched, so `auto`'s internal ladder is unobservable — we cannot tell how often `auto` succeeds via
   normalized vs fuzzy. One enum field would make the next fuzzy evaluation data-driven rather than synthesized.
5. **The §7 gate is not met:** 21.2% vs the <10% target on the stamped cohort. On this evidence the remaining
   distance is guidance (`exact` over-use) and stale-target handling, not matcher tuning.
