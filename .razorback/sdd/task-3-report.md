# Task 3 report — Theme tokens via light-dark() + contrast

**Status:** DONE

## What changed
- `src/Miller.Dashboard/wwwroot/dashboard.css` — collapsed the three-way theme definition
  (`:root` light block + `@media (prefers-color-scheme: dark)` block + `html[data-theme="dark"]`
  block, ~90 lines, every theme value written twice) into a single `:root` block where each
  themed token is defined ONCE as `light-dark(<light>, <dark>)`. The two old dark blocks are
  replaced by two tiny rules that only flip `color-scheme`:
  - `html[data-theme="dark"] { color-scheme: dark; }`
  - `html[data-theme="light"] { color-scheme: light; }`
  `:root` keeps `color-scheme: light dark`, so no-JS users still get OS-preference dark.
- `src/Miller.Dashboard/wwwroot/js/theme-init.js` — **not modified.** It only stamps
  `data-theme` on `<html>` (the exact contract `light-dark()` + the toggle rely on), and its
  one comment ("applies data-theme before first paint") is still accurate — it never described
  the old block layout. No behavior or comment change was required.

No token was renamed; no non-token rule was touched. The `#theme-toggle` / `#theme-toggle-label`
contract is untouched (those IDs live in razor/markup, not this file).

## `light-dark()` note
`light-dark()` takes two **color** values, so the two box-shadow tokens wrap only their color
part, keeping the offset geometry outside the function:
- `--lift: 3px 3px 0 0 light-dark(rgba(26,22,17,0.05), rgba(0,0,0,0.35));`
- `--lift-hover: 5px 5px 0 0 light-dark(rgba(26,22,17,0.08), rgba(0,0,0,0.45));`

## Token inventory (old light / old dark → new single definition)
All 20 themed tokens now defined once each via `light-dark()`. Values are byte-identical to the
old ones **except `--muted`** (the A7 fix):

| token | light | dark |
|---|---|---|
| --paper | #f1ece1 | #121110 |
| --grid | rgba(26,23,18,0.05) | rgba(255,255,255,0.028) |
| --surface | #fbf9f3 | #1a1916 |
| --surface-inset | #f5f1e7 | #211f1b |
| --ink | #1a1611 | #ece6d8 |
| --ink-soft | #4b463d | #c0baaa |
| **--muted** | **#8c8576 → #6f6959** | **#8f897a → #a49e8d** |
| --rule | #ddd6c6 | #2c2925 |
| --rule-strong | #c3bba7 | #423e38 |
| --accent | #0c7d72 | #2fb3a3 |
| --accent-ink | #075d54 | #46c7b6 |
| --accent-soft | #dcebe6 | #16302d |
| --ok / --ok-soft | #3f7d39 / #dde9d3 | #6aa85f / #1c2a18 |
| --warn / --warn-soft | #9a6207 / #f0e4c8 | #c98f2e / #2e2410 |
| --err / --err-soft | #b23a25 / #f1d9d1 | #d36a52 / #2e1813 |
| --lift / --lift-hover | rgba(26,22,17,…) | rgba(0,0,0,…) |

## Contrast ratios (WCAG relative-luminance formula, computed via python — not by eye)
AA target for small text = **4.5:1**. `--muted` drives 10–12px labels/paths/timestamps/table
headers, which sit on `--surface`, and also on `--surface-inset` (table `th`, `.ws-index-head`,
`.panel-heading p`) — so I verified against BOTH surfaces plus `--paper`.

### `--muted` — the fixed token
| theme | value | vs --surface | vs --surface-inset | vs --paper |
|---|---|---|---|---|
| light BEFORE | #8c8576 | **3.48 ✗** | (fail) | (fail) |
| light AFTER | **#6f6959** | **5.19 ✓** | 4.85 ✓ | 4.64 ✓ |
| dark BEFORE | #8f897a | 5.05 ✓ | 4.72 ✓ | — |
| dark AFTER | **#a49e8d** | **6.58 ✓** | 6.15 ✓ | 7.05 ✓ |

Light muted was the A7 failure (3.48:1). New light value clears 4.5:1 on every background it
actually renders on. Dark muted already passed (5.05) but I bumped it to #a49e8d so its worst
case (on surface-inset) rose from 4.72 → 6.15, giving a comfortable margin and keeping the two
themes visually balanced. New light muted (5.19) stays clearly lighter than `--ink-soft` (8.9),
so the muted/soft hierarchy is preserved.

### Other ≤12px tokens checked (per brief) — all already pass, left unchanged
| token | context | light ratio | dark ratio |
|---|---|---|---|
| --ink-soft | copy-source/breakdown 12–13px, on surface | 8.89 ✓ | 9.08 ✓ |
| --ink-soft | on surface-inset | 8.30 ✓ | 8.50 ✓ |
| --accent-ink | code / language-pill / activity-ws 11–12px, on accent-soft | 6.32 ✓ | 6.76 ✓ |
| --accent-ink | back-link 11px, on surface | 7.38 ✓ | 8.46 ✓ |

Only `--muted` was below AA; no other small-text token needed a change.

## Verification results (each check + what it proves)
- **(a) old dark blocks gone / one definition per token:** `grep -c 'prefers-color-scheme: dark'`
  = 0; the only `html[data-theme` lines are the two `color-scheme`-flip rules (:71–72) + one
  comment. Each of the 20 themed token names appears exactly once (`grep -c '  --token:'` = 1).
  → *Proves duplication is eliminated; a theme change is now made in one place (A5 fixed).*
- **(b) every `light-dark(` has both values:** every token-definition line matching
  `^\s*--x:.*light-dark\(` contains a comma pair; 20 token definitions + 2 comment mentions.
  → *Proves no half-written `light-dark()` that would drop a theme's value.*
- **(c) no dangling references:** `grep -o 'var(--[a-z-]*'` (referenced) minus `--x:` (defined)
  = empty. → *Proves every `var(--x)` in the file still resolves; no token was renamed away.*
- **(d) build:** `dotnet build Miller.slnx -c Release` → **Build succeeded, 0 Warning(s), 0
  Error(s)** (CSS is copied/embedded by the build). → *Proves the change ships.*
- **worker-ceiling — `scripts/test.sh` (fast suite):** 3081/3082 passed. The one failure was
  `RepositoryIndexLoaderBridgeTests.Load_RootMillerJsonDotnetWebProvider_PopulatesBridgeGraph`
  — an `ObjectDisposedException` on a SQLite handle in that test's own constructor, entirely
  unrelated to CSS. Re-ran that class in isolation: **17/17 passed.** So it is a pre-existing
  parallel-isolation flake, not a regression from this task. → *Proves the CSS change broke
  nothing elsewhere (CSS has no unit tests).*

## Judgment calls
1. **Bumped dark `--muted` too** (5.05 → 6.58 on surface) though it already met AA — for margin,
   theme balance, and because on `--surface-inset` the old value was only 4.72. Token-value
   change only; in scope ("fix any that fail… change token VALUES/definitions only").
2. **Verified against `--surface-inset` and `--paper`, not just `--surface`.** The brief targets
   `--surface`, but muted small text renders on inset backgrounds (table headers, index head) —
   both new values clear 4.5:1 on all three, so no surface has failing muted text.
3. **Left `theme-init.js` untouched.** Its behavior is exactly what the new CSS needs and its
   comment is accurate; editing it would be churn.

## For the lead to eyeball in Task 7 (live, both themes)
- Toggle overrides OS preference in BOTH directions (dark→light and light→dark) — this is the
  main thing `color-scheme`-flip + `light-dark()` changes structurally.
- No-JS / first-paint: OS-dark users with no stored theme should still see dark (via
  `color-scheme: light dark`).
- Muted text (table headers, workspace paths, timestamps, eyebrows) legible in both themes;
  confirm the muted vs ink-soft hierarchy still reads as two distinct weights.
- Box-shadow lifts (`.panel`, `.dashboard-hero`) render correctly in both themes (light-dark()
  now supplies the shadow color).

## Miller / tool calls used
- No `mcp__miller__search` calls needed: the `#theme-toggle` / `#theme-toggle-label` contract is
  markup-side and out of my file ownership; I confirmed via the brief and `theme-init.js` that
  only `data-theme` stamping matters, which is unchanged. CSS is not indexed as symbols, so I
  used `Read` on `dashboard.css` and `theme-init.js` directly (per Miller directives).

## Worktree state
- Path: `/Users/murphy/source/miller/.worktrees/dashboard-polish`
- Branch: `feat/dashboard-polish`
- Base commit: `6207978`
- Dirty state (before lead stages): modified `src/Miller.Dashboard/wwwroot/dashboard.css`,
  this report, and the pre-existing untracked `docs/plans/2026-07-08-dashboard-polish.md`.
  Did NOT `git add`/`commit` (parallel-lead-commit mode). `theme-init.js` unchanged.
