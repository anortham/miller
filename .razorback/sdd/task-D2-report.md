# Task D2 — Fuzzy policy replay evaluation + plan-time stale-target convergence wait

**Status:** COMPLETE
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`
**Branch:** `worktree-semantic-p2` @ `3743bb8` (ahead 8 of origin; `.razorback/` is excluded from git via
`.git/info/exclude`, so this report is not part of any commit)
**Commit SHA:** none — parallel-lead-commit (no `git add`, no `git commit` run)
**Dirty state at handoff:** the worktree carries concurrent edits from impl-b2 and impl-e2. My four owned files
are listed under *Files changed*; I touched nothing else.

---

## 1. Implementation summary

Closed design §7 items 4–5.

**§7.5 — plan-time stale-target bounded wait.** New `EditService.WaitForCandidateConvergence`. When an indexed
edit candidate is found but `old_text` does not verify against current disk text (`FailureStaleTarget`, the
`PlanReplaceTextFromIndexedCandidates` exit), the plan path now asks the write-through to converge that one file
and re-discovers + re-verifies candidates until the plan succeeds or the shared `RecoveryOptions` budget (2.5s /
150ms in production) expires. `StaleRecoveryAttempt.None` returns immediately with the pre-existing refusal, so
a write-through with no recovery path is byte-identically unchanged.

It **polls the success condition rather than the freshness gate** — see judgment call 2. A wait stamps
`wait_reason = "edit_stale_converge"`, threaded out on `EditResult.StaleWaitPerformed` and stamped by `EditTool`
(judgment call 3).

**§7.4 — fuzzy policy.** Measured first, changed second. The replay found that fuzzy policy is **not** where the
edit error rate lives: every error in the version-stamped cohort requested `match_mode=exact`, which never
enters the fuzzy rung, so **any fuzzy policy change would have altered zero historical outcomes**. One narrow,
cost-neutral change passed the strict-improvement gate and shipped; the more substantial one was rejected on the
numbers. Full methodology, numbers, and the rejection reasoning:
[`docs/findings/2026-07-20-edit-fuzzy-policy-replay.md`](../../docs/findings/2026-07-20-edit-fuzzy-policy-replay.md).

- **SHIPPED — cap measures comparable text.** `PlanFuzzy` capped raw `oldText.Length` at 160 while the distance
  scan compares the *normalized* text (indentation and line endings stripped first). The cap now measures
  `targetText.Length`. Recall 4/6 → 6/6, precision breaks unchanged.
- **REJECTED — proportional distance ceiling.** `MaxFuzzyDistance` stays 1/2/3.

---

## 2. Replay methodology + numbers

### Why the intended corpus does not exist

Design §7.4 assumes historical edit failures can be replayed from `telemetry.db`. **They cannot.** The ledger is
enum/counter-only: on all 61 `tool='edit'` error rows, `error_message` and `error_detail` are NULL, `target_hash`
is a hash, and `metadata_json` carries only booleans and enums (`has_query`, `has_anchor`, `has_line`,
`match_mode`, `edit_failure_reason`, `server_version`, `index_state`, `wait_reason`). No `old_text` exists
anywhere. This is the privacy contract working correctly and I did not propose relaxing it.

So replay used two sources: **telemetry** for rung reachability (real, N=253), and a **synthesized 10-case
corpus** for precision/recall of the policy itself. The corpus is an executable test (`RunReplay()`), not prose.

### Telemetry (read-only snapshot of `~/.miller/telemetry.db`, copied to a scratch dir; the live DB was not opened for write)

| Cohort | Calls | Errors | Rate |
|---|---|---|---|
| All `replace_text` | 212 | 60 | 28.3% |
| Version-stamped `replace_text` (the §7 gate cohort) | 52 | 11 | **21.2%** |

Stamped-cohort error buckets: `stale_target` 6, `no_match` 4, `target_not_found` 1.

Rung reachability by requested mode (all history): `exact` 125 ok / 47 err (never reaches fuzzy), `normalized`
10 / 3 (never), `auto` 12 / 9 (reaches last), `fuzzy` 5 / 1 (reaches directly).

Two design premises did not survive the data:
- **"Zero fuzzy successes" is false.** Explicit `match_mode=fuzzy` has 5 successes and 1 failure.
- **All 11 stamped-cohort errors requested `exact`**, so zero of them reached the fuzzy rung. Across all
  history, at most 10 of 60 `replace_text` errors were even under a fuzzy-reaching mode.

For §7.5 the same data is strongly positive: all 7 historical `stale_target` rows carry `wait_reason=none` —
none waited — and 5 of the 6 `replace_text` ones are `apply=0` with an indexed selector, i.e. exactly the exit
this task fixes.

### Synthesized corpus (N=10: 6 recall, 4 precision)

Recall = exact and normalized both fail and a fuzzy match **at a specific line** is correct. Precision = a fuzzy
match would splice the wrong span. Gate: strictly more recall, no new precision break.

| Policy | Recall hits | Precision breaks |
|---|---|---|
| Before | 4 / 6 | 1 / 4 |
| After (shipped cap change) | **6 / 6** | **1 / 4** — the same one |

Strict improvement met. The delta is test-pinned rather than asserted:
`FuzzyPolicyReplay_CapChangeAffectsOnlyTheIndentedRecallCases` proves exactly two corpus cases have raw length
>160 with comparable length inside it and that both are recall cases, so every other case is policy-invariant
and the "before" column is derived.

**Cost neutrality is why this is safe.** The cap bounds an O(n·m) Levenshtein scan. Normalization never
lengthens text, so old (raw ≤160 ⟹ normalized ≤160) and new (normalized ≤160) bound the scan identically. Raw
whitespace is now unbounded but `NormalizedTargetLines` is O(raw) linear, and ≤160 normalized chars also caps
the candidate window's line count. A whitespace-only snippet still fails at `targetLines.Count == 0`.

### The precision break the measurement surfaced

`constant_table_wrong_key` — `old_text = 'case 7: return "one";'` against a table containing
`case 1: return "one";` — **matches**, at distance 1 on a 21-char line (ceiling 2). The single differing
character *is* the discriminator; fuzzy edits the wrong arm. It exists identically before and after this task.
I recorded it in the corpus as `KnownCeilingGap = true` and asserted it rather than deleting the case, so it
stays visible. Deleting it would have made the shipped policy look clean and hidden a real defect.

### Why the proportional ceiling was rejected

1. **Zero measured upside** — no stamped-cohort error reached the rung.
2. **Precision is already overdrawn** — the ceiling produces a wrong-span match at its *current* setting; a
   policy already too loose at a measured point is not a candidate for loosening. A wrong-span match writes
   plausible code to the wrong place, and an agent calling with `apply=true` never sees the preview diff.
3. **It changes what counts as a match**, not just what is scanned — so unlike the cap change it has no
   cost-neutrality or precision-neutrality argument, and would need the real corpus §1 established is
   unavailable.

---

## 3. Verification

| Scope | Invariant proved | Command | Result |
|---|---|---|---|
| Red phase | The new tests fail without the implementation (real behavior, not tautologies) | 3 impl files reverted to `git show HEAD:…`, then the gate-1 filter | **6 failed / 122 passed** — exactly my 6 behavior tests. The 2 corpus-property tests passed red, which is correct: they pin policy-invariant facts by design. |
| worker-red-green | Plan-time wait converges, times out cleanly, skips without recovery, stamps `wait_reason` with no raw edit text; fuzzy cap admits comparable-length snippets and still refuses oversize ones; no regression in the matcher's existing contract | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~EditTool\|FullyQualifiedName~TextReplaceMatcher"` | **Passed — 128/128, 0 failed** (2s warm) |
| worker-ceiling | No regression anywhere in the fast/pure suite | `scripts/test.sh` | **3823 passed / 0 failed / 1 skipped** on the first clean run. Two later re-runs showed **1 failure, not mine** — see below. |
| worker-ceiling | 0 warnings / 0 errors under `TreatWarningsAsErrors` | `dotnet build Miller.slnx -c Release` | **Build succeeded. 0 Warning(s), 0 Error(s)** |

No scale suite run (out of scope). No `julie-extract`-spawning test added, so no `Category=Scale` trait was
needed.

**The one non-green result is not attributable to D2.**
`MetricsToolTests.RunClones_CandidateScanTruncated_SuppressesTheMetricAndSaysSo` failed on the last two full
runs. `MetricsToolTests.cs` and `MetricsTool.cs` are impl-e2's owned files and are dirty in the shared worktree;
per the brief I did not touch them. My owned scope is green at 128/128 in the same tree, and the first
`scripts/test.sh` run — before that test's in-flight edit landed — was 3823/0.

**The `scripts/test.sh` 30s wall-clock tripwire fired (83s / 96s / 122s across runs).** I checked whether this is
mine: it is not, in any material amount. My 10 new/adjacent tests run in **2s total** measured directly against
the Release assembly (`--filter "…PlanTimeStaleCandidate|…FuzzyPolicyReplay|…Plan_Fuzzy_Cap|…Plan_Fuzzy_StillRefuses"`).
The suite has grown from D1's 3733 tests to 3824 across four lanes, and four agents are contending for CPU in
one worktree — D1 recorded the same tripwire at 94s for the same reason. The lead should re-measure on a quiet
tree before treating the tripwire as a real regression.

**Shared-worktree note:** four separate foreign build breaks blocked verification during this task
(`MetricsTool.cs`/`NearDuplicateScan`, `ReportToolTests.cs`/xUnit2031, `VectorSidecar.cs` ×2 from impl-b2 and
impl-e2). I waited and retried rather than touching their files, per the brief. Every gate above ran against a
tree that built cleanly.

---

## 4. Judgment calls

1. **The wait lives in `EditService.cs`, not `EditTool.cs`.** The brief anticipated this and named the behavior
   rather than the file. `EditTool` is a thin MCP/DI/telemetry shell that calls `EditService.Execute`; the
   plan-time stale exit is inside `PlanReplaceText`, four frames down. Putting a wait in `EditTool` would have
   meant re-running the whole pipeline blind. `EditTool` still changed — it stamps the wait-reason enum.
2. **The wait polls candidate re-verification, not the freshness gate.** The apply path polls
   `FreshnessGate.Check` (symbols.db hash vs disk). I did not mirror that literally: plan-time success depends on
   **content.db**, which converges from `revision_file_changes` and can lag symbols.db by a tick, so a fresh gate
   would not prove a verifying candidate exists. The loop re-runs `FindCandidates` + re-plans — the actual
   success condition. This is a deliberate divergence from "mirroring the apply path" in mechanism while
   matching it in shape (same budget type, same `StaleRecoveryAttempt` contract, same transient-exception
   handling, same clean refusal on timeout).
3. **Wait-reason telemetry is threaded through `EditResult`, not stamped ambiently inside `EditService`.** My
   first cut called `TelemetryContext.Current?.SetWaitReason(...)` directly from `EditService` — the idiom
   `WorkspaceIndexProvider` uses. I reverted it: `EditService`'s class doc states it is "kept off the MCP/DI/
   telemetry surface", and silently amending a declared seam is not mine to do. The flag rides
   `EditResult.StaleWaitPerformed` (an `init` property, so no positional-record breaking change; applied with
   `with` at three call sites, leaving `Preview`/`Applied`/`Error` untouched) and `EditTool` stamps it, which is
   exactly D1's `edit_failure_reason` pattern.
4. **Scoped the wait to one exit.** Two other paths were considered and deliberately left alone, documented in
   the findings doc §5: `EditPlanFailureMessage`'s stale case (the index is **behind disk** and disk is
   authoritative — converging cannot make `old_text` reappear, so a wait could only downgrade the message from
   `stale_target` to `no_match`), and `IndexedEditCandidateState.NoMatch` (not a stale bucket; already falls back
   to whole-file disk matching, which usually succeeds).
5. **Kept the pre-existing precision break instead of tuning it out.** See §2. Fixing it would have meant
   changing match behavior on one synthesized case with no observed real instance — outside the evidence bar
   this task set for itself. Logged as findings §6 item 2.
6. **The apply-path wait remains unstamped.** `TryRecoverFreshness` spends up to 2.5s without recording
   `wait_reason`. Threading the flag through the symbol-op recovery re-entry (`ExecuteSingleFile(…,
   allowRecovery: false)`) is a separate change to pre-existing behavior, so I left it and logged it as findings
   §6 item 1.
7. **Test fixtures converge both artifacts.** My first stale-wait test faked convergence by rebuilding
   content.db alone and failed — the content corpus refuses to index a file whose disk bytes do not match the
   indexed hash, so the rebuilt chunks were inactive. The fake now stamps the symbols.db hash *and* rebuilds
   content.db, matching what a real single-file converge leaves behind. Diagnosed with a throwaway probe test,
   which was removed.

---

## 5. Files changed

| File | Change |
|---|---|
| `src/Miller.Core/Editing/TextReplaceMatcher.cs` | fuzzy cap moved after normalization and measured against `targetText.Length`; message reports "comparable chars" (+10/−7) |
| `src/Miller.Server/Tools/EditService.cs` | `WaitForCandidateConvergence`; `StaleConvergeWaitReason`; `EditResult.StaleWaitPerformed` + `ReplaceTextPlanResult.StaleWaitPerformed`; flag threaded through `PlanAndFinishSingleFile`/`FinishSingleFile` (+92/−7) |
| `src/Miller.Server/Tools/EditTool.cs` | stamps `wait_reason` from `result.StaleWaitPerformed` (+2) |
| `tests/Miller.Tests/Server/EditToolTests.cs` | +8 tests: 5 plan-time-wait, 3 fuzzy-policy/replay; the 10-case replay corpus and `RunReplay()` harness |
| `docs/findings/2026-07-20-edit-fuzzy-policy-replay.md` | new — methodology, telemetry baseline, before/after numbers, rejection reasoning, 5 open items |

No other file touched. No MCP tool added, no MCP parameter added, no `ServerInstructions` change — the tool
description and all nine `[Description]`s are untouched, so ADR-0001 budgets are unaffected.

---

## 6. Miller calls used

| Call | Confirmed |
|---|---|
| `context query="edit plan-time stale_target bounded wait, apply-path 2.5s stale wait, index convergence retry in EditService"` | seeds `EditService` @ `EditService.cs:38`, `Edit_StaleTarget_StampsStaleTargetBucket` @ `EditToolTests.cs:1598`, `Execute_StaleTarget_RequestedRecovery_TimesOut_AndBlocks` @ `:1088` — the existing stale-test pattern I mirrored |
| `inspect target="src/Miller.Core/Editing/TextReplaceMatcher.cs"` | full symbol list before reading: `MaxFuzzySnippetChars` @ `:11`, `PlanFuzzy` @ `:91`, `MaxFuzzyDistance` @ `:354`, `BoundedLevenshteinDistance` @ `:363` |
| `inspect target="src/Miller.Server/Tools/EditService.cs"` | all 47 methods + the 6 failure constants @ `:40`–`:47`; located `PlanReplaceTextFromIndexedCandidates` @ `:364`, `TryRecoverFreshness` @ `:900`, `ExpectedWorkspaceRevision` @ `:495` without reading the 1200-line file whole |
| `inspect target="src/Miller.Indexing/IndexedEditCandidateReader.cs"` | `IndexedEditCandidateState.{Current,NoMatch,Unavailable}` @ `:208`–`:212`, `MaxCandidates = 8` @ `:11`, `FindCandidates` signature @ `:13` |
| `search query="wait_reason" mode=source` | `TelemetryScope.SetWaitReason` @ `:160` (first-wins), the `index_load`/`workspace_refresh` precedents, and `TelemetryCallToolFilter` @ `:151` writing the `"none"` default — this is what made `edit_stale_converge` the right shape |

Per the Miller-first directive I listed every file's symbols via `inspect` before reading any of it, and read only
the regions the inspect output pointed at. `impact`/`trace` were not needed: the two changed methods are
`private` (`PlanFuzzy` is reached only through `TextReplaceMatcher.Plan`), and the only public-surface change is
an additive `init` property on `EditResult`, whose consumers I checked directly.

---

## 7. API-shape evidence (nothing guessed)

- `TextReplaceMatcher.MaxFuzzySnippetChars = 160` — `TextReplaceMatcher.cs:11` (via `inspect`).
- `MaxFuzzyDistance` ladder 1/2/3 at ≤12 / ≤48 / else — `TextReplaceMatcher.cs:354–361` (read, not assumed; the
  brief said "distance ceiling 3", which is only the top rung).
- `EditService.RecoveryOptions(Timeout, PollInterval)`, `Default = 2500ms/150ms` — `EditService.cs:66–71`.
- `IEditWriteThrough.TryRecoverStaleFile(string) => StaleRecoveryAttempt.None` is a **default interface method** —
  `IEditWriteThrough.cs:50`. This is why the `None` early-return preserves existing behavior for recorders.
- `StaleRecoveryAttempt.{None,Requested,Converged}` — used per `TryRecoverFreshness` (`EditService.cs:900–939`).
- `IndexedEditCandidateResult.{Current,NoMatch,Unavailable}` factory methods — `IndexedEditCandidateReader.cs:221–227`.
- `TelemetryScope.SetWaitReason` is first-wins and writes the `wait_reason` metadata key — `TelemetryScope.cs:159–168`.
- `TextReplaceMatch.StartLine` (3rd positional field) — `EditRecords.cs:77–83`, read before the corpus asserts
  on matched lines.
- `ContentCorpusWriter.Write(contentDbPath, symbolsDbPath, workspaceRoot, workspaceId, revision)` reads file
  bytes **from disk** under the workspace root — `ContentCorpusWriter.cs:268–287`. Read after the converge fake
  failed, and it is what proved the missing piece was the symbols.db hash, not the corpus rebuild.
- Telemetry schema: table `tool_telemetry`, columns `error_message`/`error_detail`/`target_hash`/`metadata_json`
  — read from the live DB's `.schema`, not from the writer, so the §1 claim rests on the actual persisted shape.
- Test helpers `Build`/`BuildTool`/`CreateSingleFileFixture`/`BuildContentDb`/`NumberedLines`/`OpenLedger`/
  `ConvergeIndexedHash`/`RecoveringWriteThrough` — read from `EditToolTests.cs:124–190` before use.

---

## 8. Self-review

- **Behavior with recovery unavailable is byte-identical.** `StaleRecoveryAttempt.None` returns before any
  polling, so every existing write-through (including `RecordingWriteThrough` and production followers with no
  leader) produces the same result as before. Pinned by
  `Execute_ReplaceText_PlanTimeStaleCandidate_NoRecoveryAvailable_FailsWithoutWaiting`, which also asserts the
  call does not consume a 60s budget.
- **The wait cannot loop unbounded.** `Converged` (synchronous) does exactly one re-check; `Requested` polls
  until `elapsed >= _recovery.Timeout`. Both exits return `null`, which yields the caller's original refusal.
- **Worst-case latency is bounded at ~5s, not 2.5s**, in one narrow case: plan-time wait succeeds via content.db
  but symbols.db is still behind, so the apply gate waits again on its own budget. It cannot stack in the common
  cases (a successful plan-time converge leaves the gate fresh; a failed one returns before the gate). Flagged
  in concerns rather than fixed, because sharing one budget across both would change apply-path semantics.
- **No TOCTOU introduced.** Disk text is read once before the wait and re-planned against unchanged; the index
  converges toward disk, not the reverse. The apply path's own TOCTOU re-check in `EditApplier` is untouched.
- **Telemetry stays enum/counter-only.** `edit_stale_converge` is a compile-time constant. The wait-reason test
  asserts `target-value` and `beta-anchor` appear nowhere in `MetadataJson`.
- **`exact` and `normalized` are behaviourally untouched.** The fuzzy change is inside `PlanFuzzy` only.
- **Existing fuzzy contract preserved.** `TextReplaceMatcherTests.Plan_Fuzzy_RefusesLongSnippets` uses 161
  non-whitespace chars, whose comparable length is also 161 — still refused, still says "too long". D1's
  `fuzzy_snippet_too_long` theory row (200 `z`s) likewise still refuses. Both green; neither file was modified.
- **Comment discipline.** Four `why` comments added (the cap's subject, the wait's mechanism divergence, the
  converge-fake's two-artifact requirement, the wait-budget note in the telemetry test). Tests carry no
  arrange/act/assert narration.
- **Fast suite discipline.** All new tests inject sub-100ms budgets except the one that must exercise the real
  2.5s default, which converges inline so it costs one re-check rather than the budget.

---

## 9. Concerns / handoff notes

- **The §7 gate is measurable and not met: 21.2% vs <10%** on the version-stamped `replace_text` cohort (52
  calls). The evidence says the remaining distance is guidance and stale-target handling, not matcher tuning —
  D2 addressed 55% of the stamped error volume (`stale_target`), and D1 addressed the `exact` steer. Worth
  re-measuring the cohort after both have been in use for a while; N=52 is small.
- **Design §7.4's premise was wrong in two ways** ("zero fuzzy successes"; replayable historical failures).
  I implemented against the data rather than the premise and shipped only the change that passed the gate. If
  the lead intended a larger fuzzy change, the blocker is evidence, not effort — and the finding is that the
  evidence does not exist and cannot be manufactured without weakening the telemetry privacy contract.
- **A real precision defect is now documented but unfixed** (findings §6 item 2). It is pre-existing, asserted
  in the corpus so it cannot regress silently, and I judged it outside D2's evidence bar. Lead should accept or
  schedule it.
- **The 5s worst-case double-wait** described in §8. Bounded and self-healing; flagged for visibility.
- **`matched_mode` is absent from telemetry** (findings §6 item 4). Without it, `auto`'s internal ladder is
  unobservable and the *next* fuzzy evaluation will face the same synthesized-corpus problem. One enum field
  would fix it permanently — cheapest high-leverage follow-up in this lane.
- **`MetricsToolTests.RunClones_CandidateScanTruncated_SuppressesTheMetricAndSaysSo` is currently red** in the
  shared worktree. It is impl-e2's file, mid-edit, and outside my ownership — flagging it so the lead does not
  attribute it to this lane or lose track of it.
- **No commit made** (parallel-lead-commit). No push, per the 2026-07-20 no-push directive. My gate runs predate
  any later lane landing, so the lead should re-run `scripts/test.sh` after integrating all lanes — on a quiet
  tree, so the wall-clock tripwire reading is meaningful.
