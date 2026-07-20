# Task D1 — Edit failure instrumentation, guidance, Unicode whitespace

**Status:** COMPLETE
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`
**Branch:** `worktree-semantic-p2` @ `da63f84` (ahead 2 of origin)
**Commit SHA:** none - parallel-lead-commit
**Dirty state at handoff:** worktree carries concurrent edits from parallel workers (C1/E1/B1). My four owned
files are listed under *Files changed*; I touched nothing else.

---

## 1. Implementation summary

Closed design §7 items 1–3.

**§7.1 — failure-reason coverage.** Audited every exit from the `replace_text` pipeline (table below) and made
the audit executable as a 13-row theory plus two focused tests for the content.db-backed exits. The audit's
finding is that the 2026-07-12 telemetry-diagnosis-hardening Task 1 had already closed every *reachable*
replace_text failure path — the enumerating test went green against unmodified `EditService`/`EditTool`. The one
genuine residue was the defensive `default:` arm of the target-resolution switch
(`EditService.cs:193`), which fell through to `Error(...)` with the implicit `FailureUnknown` bucket and a
message with no recovery action; it now stamps `target_not_found` and names the next call.

**§7.1 — version stamping.** Already satisfied structurally: `TelemetryLedger` binds
`MillerVersion.Current` into the `miller_version` column of every `tool_telemetry` insert
(`TelemetryLedger.cs:109`, schema `:33`, migration `:153`). No production change needed; added
`Edit_FailureTelemetryRow_CarriesMillerVersion` so the edit cohort's version stamp is pinned by an edit-owned
test rather than only by `TelemetryLedgerTests`.

**§7.2 — recovery-action messages.** `TextReplaceMatcher.Failure` now appends a mode-aware recovery action to
every match failure (new `RecoveryAction`), using the previously-unused `attemptedMode` parameter. Actions
differ per requested mode so the message names the *next rung*, not a generic retry. Also retuned the
`match_mode` MCP parameter description — the old text listed `auto | exact | normalized | fuzzy` with no signal
that `auto` already ladders, which is the plausible steer behind the 149/167 explicit-`exact` calls in the
telemetry cited by §7.

**§7.3 — Unicode whitespace.** `Normalized` (and the fuzzy normalizer that shares it) now treats U+00A0,
U+2000–U+200A, U+202F, U+205F, U+3000 and form feed as whitespace, both for leading/trailing trimming and — see
judgment call 2 — folded to a plain space in the interior of a line.

---

## 2. Failure-path audit (replace_text)

`before` = bucket stamped on unmodified HEAD; `after` = bucket stamped now. Test column names the covering case.

| # | Failure path | Site | Before | After | Covered by |
|---|---|---|---|---|---|
| 1 | unknown operation | `EditService.cs:128` | `invalid_request` | unchanged | theory `unknown_operation` |
| 2 | unknown occurrence | `EditService.cs:132` | `invalid_request` | unchanged | theory `unknown_occurrence` |
| 3 | unknown match_mode | `EditService.cs:136` | `invalid_request` | unchanged | theory `unknown_match_mode` |
| 4 | file target unresolved | `EditService.cs:156` → `NotFound` | `target_not_found` | unchanged | theory `file_target_not_found` |
| 5 | indexed file missing on disk | `EditService.cs:203` | `target_not_found` | unchanged | theory `file_missing_on_disk` |
| 6 | `new_text` null | `EditService.cs:213` | `invalid_request` | unchanged | theory `missing_new_text` |
| 7 | empty `old_text` | `TextReplaceMatcher.cs:19` → `MissingArgument` | `invalid_request` | unchanged | theory `empty_old_text` |
| 8 | no match on disk | `EditService.cs:242` via `EditPlanFailureMessage` | `no_match` | unchanged | theory `no_match_on_disk` |
| 9 | fuzzy snippet > 160 chars | `TextReplaceMatcher.cs:93` | `no_match` | unchanged | theory `fuzzy_snippet_too_long` |
| 10 | indexed selector, no candidate | `EditService.cs:333` | `no_match` | unchanged | theory `indexed_selector_no_candidate` |
| 11 | plan-time stale (indexed source still has text) | `EditService.cs:1104` | `stale_target` | unchanged | theory `stale_disk_text` |
| 12 | indexed candidates fail disk verification | `EditService.cs:381` | `stale_target` | unchanged | `Edit_IndexedCandidateFailsDiskVerification_…` |
| 13 | ambiguous indexed candidates | `EditService.cs:392` | `ambiguous_match` | unchanged | `Edit_AmbiguousIndexedCandidates_…` |
| 14 | splice span no longer fits content | `EditService.cs:259` | `stale_target` | unchanged | (pre-existing `Execute_…` coverage) |
| 15 | apply-time stale, recovery exhausted | `StaleBlocked`, `:873` | `stale_target` | unchanged | pre-existing `Edit_StaleTarget_…` |
| 16 | applier failure | `EditService.cs:300` | `apply_failed` | unchanged | theory `apply_failed` |
| 17 | exception escaping the pipeline | `EditTool.cs:150` | `unhandled_<Type>` | unchanged | theory `unhandled_exception` |
| 18 | **unrecognized target resolution (defensive)** | `EditService.cs:193` | **`unknown`, no recovery action** | **`target_not_found`, names inspect/search + `scope=`** | not test-reachable — see concerns |

Non-`replace_text` exits (symbol ops, rename) were re-checked while auditing and all stamp: `Candidates` →
`ambiguous_match` (`:982`, `:990`), `NotFound` → `target_not_found` (`:998`), rename missing/invalid new name →
`invalid_request` (`:537`, `:572`), rename no occurrences → `no_match` (`:566`).

**Net:** 17 of 18 paths already stamped a documented bucket before this task; row 18 was the residue. The
durable deliverable is the enumeration itself — a new failure exit that forgets to stamp now fails
`Edit_EveryReplaceTextFailurePath_StampsFailureReason` the moment a row is added for it.

---

## 3. Verification

| Gate | Invariant proved | Command | Result |
|---|---|---|---|
| Red phase | The new tests fail without the implementation (they test real behavior, not tautologies) | impl files reverted to `git show HEAD:…`, then `dotnet test --filter FullyQualifiedName~EditToolTests` | **19 failed / 85 passed** — all 19 mine (7 indent + 7 interior + 1 trailer Unicode, 3 recovery-action, 1 fuzzy-too-long). The 13-row failure theory and the version test passed red-phase, which *is* the audit finding (row-by-row above). |
| Gate 1 (assigned) | Edit failure/telemetry/whitespace behavior correct; ADR-0001 guidance budgets still hold after the `match_mode` description change | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~EditTool\|FullyQualifiedName~AgentInstructions"` | **Passed — 158/158, 0 failed** |
| Gate 2 (ceiling) | No regression anywhere in the pure/contract suite; fast suite stays fast | `scripts/test.sh` (from worktree root) | **Passed — 3733 passed, 0 failed, 1 skipped; wall time 28s (ceiling 30s)** |
| Build | 0 warnings / 0 errors under `TreatWarningsAsErrors` | Release build inside `scripts/test.sh` | clean |

Timestamp: **2026-07-20T14:30:28Z**. No scale suite run (out of scope).

Two transient conditions worth recording, neither attributable to this change:
- An earlier `scripts/test.sh` run reported **94s** and tripped the 30s tripwire. Attributed to build contention
  from four parallel agents, not a slow test: my 31 new tests total **~730ms** measured individually, and the
  suite re-run warm was 23–28s. Verified by running the fast suite with `EditToolTests` excluded (3628 tests,
  22s) versus included (3733 tests, 24s).
- One run showed `IndexerServiceScanTests.StartAsync_AsLeader_RecordsLeaderIdentity_AndRemovesItOnStop` failing.
  It passes 3/3 in isolation and passed in the surrounding full runs; it exercises the leader-lock/identity
  file, which the parallel agents contend for. Not in my file set.

---

## 4. Files changed

| File | Change |
|---|---|
| `src/Miller.Core/Editing/TextReplaceMatcher.cs` | `RecoveryAction` + mode-aware failure messages; `IsNormalizedWhitespace`/`IsUnicodeSpaceSubstitute`/`ContainsUnicodeSpaceSubstitute` replacing `IsIndentWhitespace`/`IsTrailingWhitespace`; `NormalizeLine` folds interior Unicode spaces |
| `src/Miller.Server/Tools/EditService.cs` | defensive target-resolution arm stamps `target_not_found` and names the recovery action (+3/−1 lines) |
| `src/Miller.Server/Tools/EditTool.cs` | `match_mode` parameter description no longer reads as a menu that invites `exact` |
| `tests/Miller.Tests/Server/EditToolTests.cs` | +315 lines: 13-row failure-path theory, 2 indexed-candidate exit tests, version-stamp test, 4 recovery-action tests, 15 Unicode-whitespace tests |

No other file touched. `MILLER_AGENT_INSTRUCTIONS.md` NOT modified — the discovery core has no edit-mode text to
correct, so spending its ≤1,900-char budget was unjustified (ADR-0001).

---

## 5. Miller calls used (orientation, per the Miller-first requirement)

| Call | Confirmed |
|---|---|
| `context query="EditTool replace_text failure paths, edit telemetry record, failure bucket stamping, normalized whitespace matching"` | seeds: `EditTool` @ `EditTool.cs:23`, `ReplaceTextPlanResult` @ `EditService.cs:1173`, `Edit_ReplaceText_NoMatch_StampsNoMatchBucket` @ `EditToolTests.cs:1683`, `DocumentedFailureBuckets` @ `:1505` |
| `inspect target="src/Miller.Server/Tools/EditTool.cs"` | `FailureReasonMetadataKey = "edit_failure_reason"` @ `:31`; `UnhandledFailureReasonPrefix = "unhandled_"` @ `:34` |
| `inspect target="EditTool.Edit" depth=full` | full body: telemetry stamping block, the `result.FailureReason ?? FailureUnknown` fallback, the catch-arm type-name-only bucket |
| `inspect target="src/Miller.Server/Tools/EditService.cs"` | the six `Failure*` constants @ `:40`–`:47`, `FailureReasonFor` @ `:1164`, all 47 methods incl. `PlanReplaceText` @ `:307` |
| `inspect target="EditService.Error" depth=full` | default parameter `failureReason = FailureUnknown` — proved row 18 stamps `unknown`, not nothing |
| `search query="TextMatchMode"` | located the match ladder: `TextReplaceMatcher.PlanExact/PlanNormalized/PlanFuzzy` @ `TextReplaceMatcher.cs:44/69/91`; enum @ `EditRecords.cs:47` |
| `search query="MillerVersion.Current" mode=source` | `TelemetryLedger.cs:109` binds it per insert — proved §7.1 version stamping already exists |
| `inspect target="src/Miller.Server/Telemetry/TelemetryScope.cs"` | `SetMetadata` overloads, `MetadataJson`, `SetEmptyReason`, `Outcome` — the telemetry record surface EditTool stamps |

`search query="NormalizeWhitespace"` returned no hits, which is why the ladder was located via `TextMatchMode`
rather than by guessing a helper name.

---

## 6. API-shape evidence (nothing guessed)

- `edit_failure_reason` — metadata key, `EditTool.FailureReasonMetadataKey` (`EditTool.cs:31`, via `inspect`).
- `unhandled_` prefix — `EditTool.UnhandledFailureReasonPrefix` (`:34`).
- Bucket vocabulary `no_match | ambiguous_match | stale_target | invalid_request | target_not_found |
  apply_failed | unknown` — `EditService.cs:40–47` (via `inspect`), cross-checked against the test-side
  `DocumentedFailureBuckets` (`EditToolTests.cs:1505`).
- `MillerVersion.Current` — `Miller.Server.MillerVersion` (via `search mode=source`); confirmed bound at
  `TelemetryLedger.cs:109`.
- Telemetry table `tool_telemetry`, column `miller_version` — read from `TelemetryLedger.cs:21–33`. My first
  draft assumed `tool_calls` and failed loudly against the real schema; corrected before the green run.
- `TextMatchMode.{Auto,Exact,Normalized,Fuzzy}` — `EditRecords.cs:47`.
- `EditErrorKind.{TextNotFound,MissingArgument,…}` — `EditError.cs:4–24`.
- `TextReplaceMatcher.MaxFuzzySnippetChars = 160` — `TextReplaceMatcher.cs:11`.
- Test helpers `BuildTool`/`Build`/`CreateSingleFileFixture`/`BuildContentDb`/`NumberedLines`/`OpenLedger`/
  `StampedFailureBucket` — read from `EditToolTests.cs:124–190` and `:1511` before use.

---

## 7. Self-review

- **Blast radius.** `TextReplaceMatcher` is `Miller.Core` (zero-I/O seam) — preserved; the change is pure-function
  only. `impact`-relevant consumers are `EditService.PlanReplaceText` / `PlanReplaceTextFromIndexedCandidates`,
  both re-run green. No new dependency, no new type, no seam moved.
- **Message-contract fallout checked.** Grepped every assertion on matcher message text before editing it: only
  `EditToolTests.cs:954` (`"old_text not found in current file"`, which is `EditService`'s replacement message,
  untouched) and two `Assert.Contains` in `TextReplaceMatcherTests` (`"too long"`, `"fuzzy"`) — all still pass
  because the action is appended, never substituted. `TextReplaceMatcherTests.cs` is outside my ownership and
  was not modified.
- **`exact` mode is behaviourally untouched.** The Unicode work lives entirely in `NormalizeLine`/`CreateLine`,
  which `PlanExact` does not call. An `exact` edit byte-compares as before.
- **ASCII content is byte-identical.** `NormalizeLine` early-returns the old `line[start..end]` unless a Unicode
  space substitute is actually present, so the common path allocates and behaves exactly as before.
- **Telemetry stays enum/counter-only.** No message, path, or user text added to any metadata write; the new
  recovery-action strings go to the tool *output*, never to the ledger. `StampedFailureBucket`'s forbidden-text
  assertions run on every one of the 15 new stamping cases and would catch a leak.
- **ADR-0001 budgets.** Only a parameter description changed (71 → 133 chars, ceiling 250); tool description,
  ServerInstructions core, and the nine `[Description]`s are untouched. `AgentInstructionsTests` green.
- **Comment discipline.** Two `why` comments added (the §7.2 rationale on `RecoveryAction`, the §7.3 rationale on
  `IsUnicodeSpaceSubstitute`) plus the audit-intent block above the theory. No narration comments; tests carry none.

---

## 8. Judgment calls

1. **`src/Miller.Server/Tools/EditService.cs:193` — chose `target_not_found` over leaving `unknown`** because
   the arm is reached only when target resolution yields something outside the closed
   Symbol/Candidates/NotFound/File set, and every observable consequence for the agent is identical to a
   not-found target. `unknown` is documented to mean "a known code path reached `Error()` without a more
   specific bucket", which makes it useless for the §7 cohort gate precisely where a bucket matters.
2. **`src/Miller.Core/Editing/TextReplaceMatcher.cs:315` — chose to fold interior Unicode spaces to `' '`, not
   only to widen trimming**, because trimming alone would have left the dominant real failure uncovered: an
   agent pastes `return 42;` from rendered docs and gets a bare no-match on a line it can see is identical.
   Folding is strictly a superset of the trimming fix, is a no-op for ASCII content (guarded by
   `ContainsUnicodeSpaceSubstitute`), and does not collapse whitespace *runs*, so `normalized`'s existing
   interior-sensitivity (two spaces ≠ one space, tab ≠ space) is preserved. The alternative — trim-only — is
   defensible against a literal reading of §7.3 but leaves the item's stated motivation unaddressed. Both the
   indent and interior behaviors are pinned by separate 7-case theories so a future reversal is explicit.
3. **`src/Miller.Server/Tools/EditTool.cs:76` — retuned the `match_mode` parameter description rather than
   editing `MILLER_AGENT_INSTRUCTIONS.md`.** §7.2 asks to "find and fix what steers agents to `exact`". The
   discovery core says nothing about edit modes, so growing it would spend the scarcest budget (≤1,900 chars,
   silently truncated at ~2KB by Claude Code) on the least likely steer. The parameter description was a menu
   listing `exact` first among explicit values with no hint that `auto` already ladders — the cheapest
   plausible cause of 149/167 explicit-`exact` calls, and free under the 250-char parameter budget.
4. **Kept the fuzzy "shorten it" advice for explicit `match_mode=fuzzy` only.** Under `auto`, a >160-char
   `old_text` is not really a fuzzy-length problem (exact and normalized are unbounded and already failed), so
   that case gets the ladder-exhausted message instead.

---

## 9. Concerns / handoff notes

- **Row 18 is not test-reachable.** `TargetResolution` is a closed hierarchy, so the `default:` arm cannot be
  driven from a test without adding a fake variant. I improved it defensively and left it uncovered rather than
  widening a production seam purely for coverage. Flagging so the lead can accept or reject that trade.
- **Design §7's premise partly overtaken by events.** The design says "41/52 historical errors carry none". That
  cohort predates the 2026-07-12 hardening; as of HEAD, 17 of 18 replace_text paths already stamped. The §7 gate
  ("replace_text error rate <10% on the instrumented, version-stamped cohort") is therefore measurable **now** —
  the blocker was never missing instrumentation but the absence of a version-stamped cohort to slice by, which
  the ledger column supplies. Recommend the lead treat the gate as ready to measure rather than ready to build.
- **Interior folding has a narrow false-positive surface**: a source line whose *string literal* differs from
  another only by NBSP-vs-space could now be matched under `normalized`. Bounded by (a) `normalized` being an
  opt-in whitespace-insensitive contract, (b) `exact` being unaffected, (c) the edit tool previewing a diff
  before any write. Called out for visibility, not as a known defect.
- **Shared-worktree contention is real.** Three separate build breaks from parallel workers' in-flight files
  (`NearDuplicateAnalyzerTests.cs`, `SearchTool.cs`, `MetricsTool.cs`, `WorkspaceFactsAssemblerTests.cs`) blocked
  verification for ~4 minutes of polling. Final gates ran against a green tree; the lead should re-run
  `scripts/test.sh` after integrating all lanes since my green run predates any later lane landing.
- **No commit made** (parallel-lead-commit). No push, per the 2026-07-20 no-push directive.
