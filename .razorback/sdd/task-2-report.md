# Task 2 — Copy & data presentation (pluralization, unresolved hashes, pattern list)

**Status:** COMPLETE
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/dashboard-ux-fixes`
**Branch:** `worktree-dashboard-ux-fixes` @ base commit `780b51d` (working tree dirty — Task 3 in flight in parallel)
**Commit SHA:** none — parallel-lead-commit (no `git add` / `git commit` run)

> Note: this path previously held a stale report from the earlier `2026-07-08-dashboard-polish` plan
> (worktree `.worktrees/dashboard-polish`, base `6207978`). Overwritten per the task brief, which names this
> path for the current plan's Task 2.

## What I implemented

### 1. Pluralization (`DashboardFormat.cs:14-19`)
Added an optional third parameter: `FormatCount(long value, string singular, string? plural = null)`.
Two-arg behaviour is byte-identical (`plural ?? singular + "s"`), so all 22 existing call sites keep their
current output. `WorkspaceOnboardingPanel.razor:36` now passes `"common miss", "common misses"` — fixing
"10 common misss".

### 2. Unresolved hot targets collapse (`WorkspaceOnboardingPanel.razor:68-93, 148-149`)
`Onboarding.HotTargets` is partitioned by a new `IsNamed` predicate (`!string.IsNullOrWhiteSpace(Name)`).
Named targets render exactly as before. Unresolved targets collapse into ONE trailing row:
`N unresolved targets` + summed calls, detail `hashes not present in the current index`. When every target
is unresolved, the summary row is the only row (the `No hot targets.` empty state still only fires on a
genuinely empty list).

### 2b. Hot-target metric counts only named targets (fix round — `WorkspaceOnboardingPanel.razor:32, 148-150`)
Lead-accepted fix round for my own concern #1. The metric band's "resolved from hashes" figure was
`Onboarding.HotTargets.Count` — every target, resolved or not. Now `FormatCount(NamedHotTargetCount, "hot target")`,
where `NamedHotTargetCount => Onboarding is null ? 0 : Onboarding.HotTargets.Count(IsNamed)` reuses the same
`IsNamed` predicate the list partition uses, so the metric and the list can never disagree. Label unchanged.
All-unresolved renders "0 hot targets" — the honest number; the count still surfaces in the summary row.

### 3. Pattern inventory sub-line (`PatternInventoryPanel.razor:37, 47-69`)
Replaced the three-clause `;`-separated sub-line with `FamilyDetails(family)`, joined by ` · `:
- `languages: …` — always
- `N patterns` — only when `PatternCount > 1` (via `FormatCount`, so the "1 pattern" noise is gone)
- `captures: …` — only when `HasInformativeCaptures`: non-empty AND not a single capture equal to the
  family's trailing segment (`json.property` + `["property"]` → omitted; `dotnet.route` + `["route","verb"]` → shown).

## Verification ledger

| Invariant | Scope | Command | Result | Timestamp |
|---|---|---|---|---|
| Red before green (compile red) | focused | `dotnet test … --filter "(Category!=Scale)&(FullyQualifiedName~DashboardActivityFeedTests\|FullyQualifiedName~DashboardFormat)"` | FAIL — CS1501 no 3-arg `FormatCount` (×2) | 2026-07-16 |
| Red before green (behavioural) | focused | same | FAIL — 4 failed / 70 passed: no "2 common misses", no "3 unresolved targets", no "2 unresolved targets", "captures:" still present | 2026-07-16 |
| Green after implementation | focused | same | **PASS — 74/74**, 188 ms | 2026-07-16 |
| Fast suite | worker scope | `scripts/test.sh` | 2 failed / 3535 passed — both failures in `DashboardNotFoundTests` (Task 3's untracked, mid-flight file) | 2026-07-16 |
| Fast suite excl. Task 3 in-flight | worker scope | `dotnet test … --filter "(Category!=Scale)&(FullyQualifiedName!~DashboardNotFoundTests)"` | **PASS — 3533/3533**, 21 s | 2026-07-16 |
| Zero-warning Release build | worker ceiling | `dotnet build Miller.slnx -c Release` | **PASS — 0 Warning(s), 0 Error(s)** | 2026-07-16 |
| Duplicate-assignment re-verify | focused | same focused filter | **PASS — 74/74**, 269 ms (work intact at `780b51d`, unaffected by Task 3's shared-tree edits) | 2026-07-16 |
| Fix round — red first | focused | same focused filter | FAIL — 2 failed / 72 passed: no `<strong>1 hot target</strong>`, no `<strong>0 hot targets</strong>` | 2026-07-16 |
| Fix round — green | focused | same focused filter | **PASS — 74/74**, 243 ms | 2026-07-16 |
| Fix round — zero-warning build | worker ceiling | `dotnet build src/Miller.Dashboard/Miller.Dashboard.csproj -c Release` | **PASS — 0 Warning(s), 0 Error(s)** | 2026-07-16 |

The only fast-suite failures (`DashboardNotFoundTests.WorkspacesShell_RendersVersionFooterAndNewTabJsonLinks`,
`…WorkspaceShell_RendersVersionFooterAndNewTabJsonLinks`) are in an **untracked file created by Task 3**
(`git status` confirms `?? tests/Miller.Tests/Server/DashboardNotFoundTests.cs`), asserting a version footer
Task 3 has not finished wiring. Excluding that class, my scope is fully green. Not my files, not my regression.

## Files changed (all within my ownership)

- `src/Miller.Dashboard/DashboardFormat.cs` (+8/−4)
- `src/Miller.Dashboard/Components/WorkspaceOnboardingPanel.razor` (+19/−2)
- `src/Miller.Dashboard/Components/PatternInventoryPanel.razor` (+29/−7)
- `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs` (+119) — 5 new render tests
- `tests/Miller.Tests/Server/DashboardFormatTests.cs` (+27) — 3 new `FormatCount` tests (file already existed;
  it had zero `FormatCount` coverage, so I extended it rather than creating a new file per the task note)

## Miller calls used

| Call | What it confirmed |
|---|---|
| `inspect(target='FormatCount', depth='full')` | Definition at `DashboardFormat.cs:14`; body `value.ToString("N0", …) + " " + (value == 1 ? singular : singular + "s")`; 22 dependents |
| `trace(target='FormatCount', mode='refs', limit=50)` | All 22 call sites enumerated (ContextSavings ×3, WorkspaceDetail ×7, WorkspaceHealth ×2, WorkspaceLocalMetrics ×3, WorkspaceOnboarding ×5, WorkspaceTrends ×2). **Every one passes exactly 2 args** — no caller already passes a plural, so the optional param is safe |
| `inspect(target='DashboardOnboardingTarget', depth='full')` | Positional record: `(string Confidence, string? Name, string? Kind, string? Path, int? Line, long Calls)` |
| `inspect(target='DashboardPatternFamily', depth='full')` | Positional record: `(string Family, int PatternCount, long FactCount, IReadOnlyList<string> Languages, IReadOnlyList<string> Captures)` |
| `inspect(target='DashboardWorkspaceOnboardingPanel', depth='full')` | `(string? WorkspaceId, string State, long TotalCalls, IReadOnlyList<string> StartHere, IReadOnlyList<DashboardOnboardingTarget> HotTargets, IReadOnlyList<DashboardOnboardingMiss> CommonMisses, IReadOnlyList<string> Notes, string? Error = null)` — used to construct test fixtures |
| `inspect(target='DashboardPatternInventoryPanel', depth='full')` | `(string? WorkspaceId, string State, IReadOnlyList<DashboardPatternFamily> Families, string? Error = null)` |
| `inspect(target='DashboardOnboardingMiss', depth='full')` | `(string Tool, string? Op, string Reason, long Calls)` |

Supporting greps (non-shape): `grep -rn "unresolved_hash" src/ tests/` confirmed `"unresolved_hash"` is the
`Confidence` value produced by `WorkspaceTargetHashResolver.cs:60` / `DashboardData.cs:1201` for name-less
targets — i.e. `Name == null` and `Confidence == "unresolved_hash"` travel together, so partitioning on `Name`
(per the approved plan) matches the data.

## API-shape evidence

No guessed shapes. Every record constructed in tests came from an `inspect … depth='full'` body listed above.
`ImplicitUsings=enable` in `Directory.Build.props` supplies `System.Linq` to the generated Razor class
(`Components/_Imports.razor` does not import it explicitly) — confirmed by the clean Release build.

## Judgment calls

- `PatternInventoryPanel.razor:47` — chose a `FamilyDetails(family)` helper in the component's `@code` block over
  inline Razor ternaries, because the conditional ` · ` joining is unreadable inline and it mirrors the existing
  `TargetDetails` helper in `WorkspaceOnboardingPanel.razor:151`. This is NOT a new helper "outside DashboardFormat"
  in the architectural sense — it is component-private presentation logic in the shape the sibling panel already uses.
- `PatternInventoryPanel.razor:60` — redundancy check applies only when `Captures.Count == 1`. A multi-capture set is
  always informative even if one member repeats the tail (`["property","value"]` on `json.property` still tells you
  about `value`). Chose this over filtering individual redundant members, which would render a misleadingly partial
  capture list.
- `PatternInventoryPanel.razor:66` — `OrdinalIgnoreCase` over `Ordinal` for the tail comparison; extractor family ids
  and captures are lowercase today, so the choice is invisible now but avoids a redundant row if casing ever drifts.
- `PatternInventoryPanel.razor:66` — a family with no `.` (e.g. `route` + `["route"]`) treats the whole family as the
  tail, so it is correctly judged redundant. Falls out of `LastIndexOf('.') + 1 == 0`.
- `WorkspaceOnboardingPanel.razor:71-72` — partition on `Name` (the plan's stated rule) rather than on
  `Confidence == "unresolved_hash"`. The two are equivalent in the data (evidence above), but `Name` is what the row
  actually needs to render, so it cannot go stale if the extractor adds another unresolved confidence band.
- `WorkspaceOnboardingPanel.razor:71` — used `var` rather than the explicit `List<DashboardOnboardingTarget>` type: a
  line starting with `List<` inside a Razor code block trips the markup/tag parser (`RZ1010` class of error).
- `WorkspaceOnboardingPanel.razor:151` — left `TargetName`'s `Confidence` fallback in place though the named
  partition makes it unreachable. It carries the null-safety the compiler needs for `string? Name`; removing it would
  require a `!` suppression, which is worse.
- Tests: extended the pre-existing `DashboardFormatTests.cs` instead of creating it. The brief said "create ONLY if
  none exist" — the file existed (from the earlier dashboard-polish plan) but had zero `FormatCount` coverage (only
  `RelativeTime`/`AbsoluteShort`/`FormatBytes`), which is why the `grep -rn "FormatCount" tests/` found nothing.

## Self-review findings

- Two-arg regression coverage is explicit: `FormatCount_WithoutPlural_AppendsS` pins `"file"`/`"symbol"` across
  0/1/2/1234, and `FormatCount_NullPlural_MatchesTwoArgumentBehaviour` pins the overloads to each other.
- `WorkspaceOnboardingPanel_AllHotTargetsUnresolved_RendersSummaryRowOnly` includes a whitespace-only `Name` (`"   "`)
  to prove the partition uses `IsNullOrWhiteSpace`, not `IsNullOrEmpty`.
- `Assert.DoesNotContain("unresolved_hash", html)` proves the raw confidence token no longer leaks to the UI
  (previously `TargetName` fell back to it, rendering `<span>unresolved_hash</span>` per row).
- `Assert.Equal(1, html.Split("unresolved target").Length - 1)` proves the "at most one row" criterion, not merely the
  row's presence.
- Summed-calls arithmetic is asserted with distinct numbers (named target 9 calls vs unresolved sum 4+3+1=8) so a
  wrong-partition bug cannot pass by coincidence.
- `PatternInventoryPanel_ShowsPatternCountAndCapturesWhenInformative` was green before the change — kept deliberately
  as a regression guard proving the informative case still renders after the rewrite.
- Zero comments in the new tests; no narration comments in the changed production code (the two doc comments added
  state non-obvious *why*: the plural contract and the capture-redundancy rule).

## Concerns

1. ~~**Out-of-scope inaccuracy left in place** — the "resolved from hashes" metric counted unresolved targets too.~~
   **RESOLVED in the fix round** (see §2b): the lead accepted it as in-scope, and the metric now counts only named
   targets via `NamedHotTargetCount`, sharing the `IsNamed` predicate with the list partition.
2. ~~**Shared test file with Task 3**~~ — **did not materialize.** Lead confirmed Task 3 never touched
   `DashboardActivityFeedTests.cs`; my 5 render tests are the only additions. No conflict expected at commit.
3. **Fast suite is red in the shared working tree** purely from Task 3's in-flight `DashboardNotFoundTests`. Per the
   lead's instruction I skipped the full fast suite this round and ran the focused filter only; the lead runs the
   batch-level suite after Task 3 lands. My last full run (pre-fix-round) was 3533/3533 with that class excluded.

## Fix round summary (post lead review)

Lead review approved the implementation (byte-compatible `FormatCount`, spec-matching collapse and sub-line logic)
and requested one fix: the hot-target metric. Delivered TDD — extended two existing render tests with metric
assertions (`<strong>1 hot target</strong>` when 1 of 4 targets is named; `<strong>0 hot targets</strong>` when all
are unresolved), watched both fail (2 failed / 72 passed), then added `NamedHotTargetCount` and went green
(74/74). `Assert.DoesNotContain("4 hot targets", html)` pins the regression. No new files; no `git add`/`commit`
(parallel-lead-commit unchanged).
