# Task 10 — Responsive pass, hero-metrics grid, final visual sweep

**Status:** COMPLETE
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/dashboard-ux-fixes`
**Branch:** `worktree-dashboard-ux-fixes` (from `a3a96d3`)
**Verification:** `dotnet build Miller.slnx -c Release` → 0 warnings / 0 errors; `scripts/test.sh` → 3585/3585 passed, 23s (under the 30s ceiling)

---

## Ledger

| Item | Outcome |
|---|---|
| `.hero-metrics` auto-fit | DONE — plus two follow-on defects the spec'd value alone did not close (below) |
| `.detail-actions` / `.language-strip` / `.api-actions` wrap | NO CHANGE NEEDED — all three already carry `flex-wrap: wrap`; verified at 390/768 |
| `.stats-grid` / `.savings-summary` / `.fact-list` collapse | NO CHANGE NEEDED — existing `@media (max-width: 760px)` already collapses them to 1 column; no clip measured at any width |
| ws-index mobile grid audit ≤900px | NO CHANGE NEEDED — audit clean at 390/640/768/900 |
| Right-edge overflow / clipped numerals | DONE — found and fixed a real silent-clip bug (onboarding + health panels) |
| Deferred 1: merge `.ws-sort`/`.telemetry-sort` → `.col-sort` | DONE (cheap: class-name swaps only) |
| Deferred 2: stale-workspaces header row | DONE |
| Deferred 3: `/` filter matches remove-form text | DONE |

---

## Headline finding: the brief's premise was based on a broken screenshot harness

The task brief lists "every ≤640px clip" as established fact. **Those clips do not exist.** They are an
artifact of the prescribed harness.

`--headless=new --screenshot --window-size=390,3000` does **not** produce a 390px viewport. Headless Chrome
clamps the window to a **~500px minimum width**, lays the page out at 500px, then crops the screenshot to the
requested 390px. Every element in the right 110px looks "clipped". Measured proof:

```
$ chrome --headless=new --window-size=390,2000 --dump-dom   (probe: documentElement.scrollWidth vs innerWidth)
SCROLLWIDTH=500 INNERWIDTH=500 OVERFLOW=no      <-- asked for 390, got 500
```

I replaced it with a **CDP harness** (`Emulation.setDeviceMetricsOverride`), which honours a true 390px
viewport. Node 26 ships a global `WebSocket`, so this needed zero dependencies. Under a real 390px viewport:

```
INDEX     @390: PAGE scrollWidth=375 innerWidth=390 pageOverflow=no   CLIPPED-COUNT=0
WORKSPACE @390: PAGE scrollWidth=375 innerWidth=390 pageOverflow=no   (real clips found — see below)
```

**Neither page has ever had horizontal page overflow at 390px**, before or after this task. The wrap rules the
brief asked me to add were already present. I did not add redundant CSS for a bug that does not exist.

Second harness trap, worth recording: the CDP browser ran with a persistent profile and **served a cached
stylesheet**, so a first post-fix audit reported the bug as unfixed. `Network.setCacheDisabled` was required
before any measurement could be trusted. A CSS change also needs a dashboard rebuild+restart — the running
server serves `bin/**/wwwroot`, not the source tree.

---

## What was actually broken (all measured, not eyeballed)

### 1. `.hero-metrics` — 4 metrics in a hardcoded 3-column grid (the assigned bug, confirmed)

`/` renders 4 metrics (`WorkspacesShell.razor:28-46`), the workspace page 3 (`WorkspaceShell.razor:29-43`),
into `grid-template-columns: repeat(3, minmax(0,1fr))` — so `/` orphaned SYMBOLS onto a second row at every
width. Confirmed in the 1440 baseline.

### 2. The spec'd fix alone does not close the bug — `minmax(96px, 1fr)` still orphans at 1081–1128px

`.dashboard-hero`'s side column was `minmax(360px, 0.62fr)`. 4 × 96px = 384px > 360px, so `auto-fit` picks
**3** tracks in the band just above the 1080px stacking breakpoint and the 4th metric wraps — the original bug,
relocated. Measured:

```
minmax(96px,1fr) alone:
vw=1081 heroSideW=360 metrics=4 cols=3 RENDERED_ROWS=2  <-- ORPHAN!
vw=1100 heroSideW=367 metrics=4 cols=3 RENDERED_ROWS=2  <-- ORPHAN!
vw=1128 heroSideW=378 metrics=4 cols=3 RENDERED_ROWS=2  <-- ORPHAN!
vw=1200 heroSideW=405 metrics=4 cols=4 RENDERED_ROWS=1  ok
```

Fixed by raising the side minimum to `396px` (`dashboard.css:150-155`). `384px` was tested and still failed —
`box-sizing: border-box` means the strip's 2px border eats into the track, so the true requirement is
4 × 96 + 2 = 386px. The comment ties the two numbers together so a future edit cannot silently reintroduce it.

### 3. My own regression: a truncated number renders as a *wrong* number

With 4 tracks the columns narrow, and `.hero-metrics strong` has `text-overflow: ellipsis`. At 1440 the
symbols count rendered **"3,056…"** — which reads as *three thousand* rather than three million. Strictly
worse than the orphan I had just fixed. Caught by measuring `scrollWidth > clientWidth`:

```
vw=1440 stripW=497
  symbols  text="3,056,261" needs=130px has=96px  <-- TRUNCATED
```

Root constraint: `.shell` caps at `1500px`, so the two-column hero's side column **never exceeds ~544px** at
any viewport — while 4 metrics × a 9-digit count at 24px needs ~632px. **A 4-across 24px strip cannot fit the
two-column hero on any screen.** The strip is only roomy once the hero stacks (≤1080px → 655–787px).

The two acceptance criteria ("one row at 1440" and "no clipped numerals") therefore collide under the spec'd
rule. Resolved by scaling the readout to its track instead of ellipsising it — a container query scoped to
exactly the 4-metric strip in the 2-column hero (`dashboard.css:344-359`). Both criteria now hold:

```
FINAL  /  (4 metrics)                          FINAL  /workspace (3 metrics)
vw= 768 stripW= 655 ROWS=1 font=24px      t=0  vw= 768 stripW= 655 ROWS=1 font=24px      t=0
vw=1081 stripW= 396 ROWS=1 font=13px      t=0  vw=1081 stripW= 396 ROWS=1 font=24px      t=0
vw=1440 stripW= 497 ROWS=1 font=17.58px   t=0  vw=1440 stripW= 497 ROWS=1 font=24px      t=0
vw=1920 stripW= 544 ROWS=1 font=19.74px   t=0  vw=1920 stripW= 544 ROWS=1 font=24px      t=0
                                    (t = truncated count; pageOverflow=no at every width)
```

The 3-metric workspace strip and the stacked hero keep the full 24px — only the case that cannot fit is scaled.

### 4. Real silent clipping — onboarding + health panels (the "clipped right-aligned numerals")

Found by classifying every element that spills its nearest clipping ancestor, separating *scrollable*
(reachable) from *hidden* (silently lost):

```
WORKSPACE @390: CLIPPED-COUNT=35
  CLIPPED +45px  span  inside section.panel.onboarding-panel (overflow-x:hidden)  txt="2 calls"
  CLIPPED +22px  span.workspace-state.warn  inside section.panel.health-panel     txt="usable_with_warnings"
WORKSPACE @768: CLIPPED-COUNT=32   (same cause, 2-column breakdown grid)
```

Visual confirmation: the hot-targets rows rendered **"2 c"** instead of "2 calls".

Root cause: a grid item's automatic minimum is its content's min-content width. The unbreakable path token in
the detail line (`src/Miller.Dashboard/DashboardData.cs:476`) sets a ~377px floor on the `li`; `.panel`'s
`overflow: hidden` (`dashboard.css:361`) then eats the overflow — **no scrollbar, no ellipsis, no page
overflow**, which is exactly why it never showed up as a scroll bug. Fixed with `min-width: 0` on the list
items plus `overflow-wrap: anywhere` on the detail line (`dashboard.css:1065-1076`). Post-fix: **CLIPPED-COUNT=0
on both pages at 390/640/768/900/1080/1440.**

Not a bug, deliberately left alone: the telemetry `<table>` overflows its container at 768px, but `.table-wrap`
has `overflow-x: auto` (`dashboard.css:1104`) — it scrolls inside its own container, which is the correct
pattern. My first probe flagged it; the clip/scroll classification cleared it.

---

## Deferred items

**1. Merge `.ws-sort` + `.telemetry-sort` → `.col-sort` — DONE.** Ripple was class-name-only (2 CSS blocks, 2
razor files, 2 test assertions), so it met the "cheap" bar. Net −44 lines. The two rule sets existed because
the caret keys off `[role="columnheader"]` (ws-list spans) vs `th` (telemetry's real table, implicit role);
the merged rule matches both via a selector list. The JS was already class-agnostic (queries `[data-sort-col]`,
`alpine-components.js:147,233`), so nothing broke. Verified live in both header forms:

```
WS-LIST (span[role=columnheader])        TELEMETRY (th)
  idle  aria-sort=none       caret="↕"     idle  aria-sort=none       caret="↕"
  desc  aria-sort=descending caret="▼"     desc  aria-sort=descending caret="▼"
  asc   aria-sort=ascending  caret="▲"     asc   aria-sort=ascending  caret="▲"
```

I also added a `col-sort` assertion to the telemetry test, which previously pinned no class name at all.

**2. Stale-workspaces header row — DONE.** The stale table is `role="table"` with rows but no columnheader row
— a real a11y gap. Added a label-only header (`WorkspaceIndex.razor:141-153`); no sort buttons, because the
client-side sort store drives the live table only. It reuses `.ws-index-head`, so it inherits the existing
`display: none` below 900px.

This broke `WorkspaceIndex_EveryRowHasSameCellCountAsHeaderColumns`, which counted columnheaders **globally**
(`Assert.Equal(8, columnHeaders)`) — a second header row legitimately doubles that. I made the guard
structure-aware (derives columns-per-table from the header-row count) rather than weakening it; its invariant
(rows match their header's column count) is preserved. Flagging it because it is a test I was not explicitly
assigned — it is the direct consequence of an assigned change.

**3. `/` filter matching remove-form text — DONE.** Confirmed real, not theoretical. `applyFilter` matched
`row.textContent`, which includes `WorkspaceRemoveConfirm`'s copy ("Remove…", "Cancel", "…rebuildable via
`workspace open`"). Measured against the live 78-row registry:

```
BEFORE                                        AFTER
"cancel"         -> visible=78  <-- FALSE     "cancel"         -> visible=0   ok
"rebuildable"    -> visible=78  <-- FALSE     "rebuildable"    -> visible=0   ok
"registration"   -> visible=78  <-- FALSE     "registration"   -> visible=0   ok
"confirm remove" -> visible=78  <-- FALSE     "confirm remove" -> visible=0   ok
"hermes"         -> visible=3   (real)        "hermes"         -> visible=3   (preserved)
```

Note `.miller` appears in that copy, so **filtering for "miller" matched every row** — a very plausible query.
Fixed by scoping the match source to the data cells (`alpine-components.js:6-14,88`), excluding the actions
cell. Slightly more than one line (a named helper + one changed line) because inlining the selector into the
hot loop would have been worse; still within the item's intent.

---

## Files changed

| File | Change |
|---|---|
| `src/Miller.Dashboard/wwwroot/dashboard.css` | hero side min 360→396px; `.hero-metrics` auto-fit; fluid readout container query; breakdown clip fix; `.ws-sort`+`.telemetry-sort` → `.col-sort` |
| `src/Miller.Dashboard/Components/WorkspaceIndex.razor` | `.col-sort` swap (5); stale header row |
| `src/Miller.Dashboard/Components/TelemetryPanel.razor` | `.col-sort` swap (7) |
| `src/Miller.Dashboard/wwwroot/js/alpine-components.js` | filter match source scoped to data cells |
| `tests/.../DashboardRegistryReadTests.cs` | `col-sort` assertions; new stale-header test; cell-count guard made structure-aware |
| `tests/.../DashboardActivityFeedTests.cs` | `col-sort` assertion (telemetry pinned no class before) |
| `docs/plans/2026-07-16-dashboard-ux-fixes.md` | Task 10 checkboxes |

**Razor edits beyond class swaps:** one — the stale header row (deferred item 2), which is markup by nature.
No layout required a razor change; everything else was CSS-only.

---

## Miller calls / API-shape evidence

| Call | Proved |
|---|---|
| `search(query='hero-metrics', mode='source')` | The rule at `dashboard.css:302` + both consumers (`WorkspaceShell.razor:29`, `WorkspacesShell.razor:28`) → the 4-vs-3 metric mismatch |
| `search(query='ws-sort', mode='source')` | All 5 emitters in `WorkspaceIndex.razor` + test assertions at `DashboardRegistryReadTests.cs:1342,1373` |
| `search(query='telemetry-sort', mode='source')` | 7 emitters in `TelemetryPanel.razor`; no test pinned the class |
| `grep` (source-scoped, `bin/` excluded) | Full rename ripple before touching anything — Miller's index included stale `bin/**` copies, so I scoped to `src`/`tests` to avoid a false ripple estimate |

Every selector I relied on was confirmed against live rendered DOM via CDP, not inferred.

---

## TDD

- Sort-class merge: tests updated to `col-sort` first → **red** (2 failed) → rename → **green** (4 passed).
- Stale header: test written first → **red** ("ws-index-head" not found) → markup → **green**.
- CSS-only changes: measured before/after via the CDP probes above (screenshot evidence in place of red/green).
- Tests carry zero comments, per the house rule.

---

## Screenshot evidence

`.razorback/sdd/` is git-ignored for new files (`.git/info/exclude:7` + `.razorback/sdd/.gitignore` = `*`), so
per the brief the PNGs stay **uncommitted local artifacts**; this report is force-added like the prior task
reports. All 12 captured at a true viewport via CDP, post-fix, `dashboard-ux-fixes` workspace (915 files /
40,916 symbols — a data-rich page; the first workspace I drew was empty and proved nothing).

| Path | Proves |
|---|---|
| `.razorback/sdd/t10-index-1440-light.png` / `-dark.png` | 4 metrics on ONE row; "3,056,267" in full, no ellipsis; both themes intact |
| `.razorback/sdd/t10-ws-1440-light.png` / `-dark.png` | 3 metrics one row at full 24px; unaffected by the fluid rule |
| `.razorback/sdd/t10-index-768-light.png` / `-dark.png` | Stacked hero, 4 metrics one row at 24px |
| `.razorback/sdd/t10-ws-768-light.png` / `-dark.png` | Breakdown clip fix at the 2-column breakdown grid |
| `.razorback/sdd/t10-index-390-light.png` / `-dark.png` | No right-edge clipping, no horizontal scroll; metrics stacked at full 24px |
| `.razorback/sdd/t10-ws-390-light.png` / `-dark.png` | Hot-targets rows show "2 calls" in full (was "2 c") |

Harness (rebuildable): `/tmp/t10-cdp.mjs` (screenshot + overflow probe), `/tmp/t10-crop.mjs` (legible region
crops), `/tmp/t10-probe.mjs` (arbitrary DOM probe), `/tmp/t10-audit.js` (clip-vs-scroll classifier).

---

## Self-review / judgment calls

1. **Deviated from the literal spec on `.hero-metrics`.** `repeat(auto-fit, minmax(96px, 1fr))` is implemented
   verbatim, but shipping *only* that would have left an orphan at 1081–1128px and introduced a truncated
   number at 1440 — both measured. The two extra changes (side min 396px; fluid readout) exist solely to make
   the spec'd rule actually satisfy the spec'd acceptance criteria.
2. **Resolved a genuine conflict between acceptance criteria** ("one row at 1440" vs "no clipped numerals") in
   favour of satisfying both via a fluid readout, rather than picking one. A 2×2 layout would have satisfied
   "no clipping" but violated the explicit one-row criterion.
3. **Did not add CSS the brief asked for** where measurement showed it was already there (wrap rules) or
   unnecessary (band collapse). Adding it would have been dead code justified by a harness artifact.
4. **Modern CSS bar:** `:has()` and container queries are new here, but the sheet already ships `:has()`
   (`dashboard.css:615`) and `light-dark()` (Chrome 123+), so both sit inside the existing baseline. No
   external assets; local-first intact.
5. Kept both theme paths (`html[data-theme]` + `light-dark()`) untouched; verified in both themes at all three
   widths.

## Concerns / follow-ups (none blocking)

- **13px readout at 1081–1200px.** The fluid floor is small for a "hero" number. It is correct-but-cramped, and
  strictly better than a wrong number. The real cause is that the 2-column hero gives the strip only 396–544px
  while the copy column keeps ~60% of a mostly-empty row. **Rebalancing the hero split (or stacking earlier)
  would let the readout stay large** — a design decision I did not take unilaterally, as it reshapes the hero
  well beyond a responsive pass.
- **The fluid formula is tuned for ~9 digits.** A 10-digit count (10M+ symbols) would still ellipsize slightly
  at the narrowest 2-column hero. Not reachable with this registry's data; the durable fix is the hero
  rebalance above, or abbreviating large counts (`3.1M`), which is a data/markup change and out of CSS scope.
- **`IndexerServiceScanTests.StartAsync_WhenEnabledLeaderAndSidecarBuildFails_StillMarksRegistryScanned` is
  flaky under load.** It failed once while my dashboard + Chrome were competing for CPU, then passed 3/3 in
  isolation and in two clean full-suite runs. Unrelated to this task (indexer path, disjoint files) —
  flagging as a pre-existing hazard, not a regression.
- The `.razorback/sdd` PNGs are local-only; if the lead needs them in-repo they must be force-added.
