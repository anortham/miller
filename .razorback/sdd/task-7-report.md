# Task 7 report — Lead live verification (report-only)

## Status: DONE (lead-executed)

Executed by the lead directly against the branch Release build
(`src/Miller.Dashboard/bin/Release/net10.0/Miller.Dashboard`, built at 51822cb / Tasks 1–6 merged).
Scratch instances ran on ports 4991/4992 so the user's running dashboard (pid 78858, main checkout)
was never killed or reused — a deliberate deviation from the brief's "kill and relaunch via the
workspace tool" note, chosen because the session's Miller MCP server is the user-level install (its
launcher would serve the INSTALLED dashboard, not this branch build) and killing the user's live
instance was avoidable state mutation. The branch build was exercised directly instead.

## The six checks (observed values)

1. **Routes from the branch build** — `/` 200, `/workspace` 200, `/snapshot.json` 200,
   `/fragments/workspaces` 200, `/telemetry.json` 200 (port 4991).
2. **Theme** — served `dashboard.css` carries 24 `light-dark()` token definitions and exactly the two
   flip rules `html[data-theme="dark"] { color-scheme: dark; }` / `html[data-theme="light"] { color-scheme: light; }`.
   Contrast ratios were computed in Task 3 (light 5.19:1, dark 6.57:1, independently recomputed by the
   lead). **Visual eyeball of the toggle in both directions still needs human eyes** — flagged below.
3. **List** — landing page markup carries `data-poll-trigger="every 30s"` + `hx-get="/fragments/workspaces"`
   and 4 `data-sort-col` sort buttons; the poll target `/fragments/workspaces` returns 200 with the same
   section markup. Live observation of a 30s swap preserving filter/sort state was **not** browser-verified
   (no browser automation in this session) — the state-store logic is unit-covered by markup contract tests
   and the mechanism mirrors the pre-existing `openIssueDetails` swap-survival pattern.
4. **Detail** — `/workspace` HTML contains the `Refreshing…` htmx indicator label, 6 `id-chip` spans,
   20 `data-copy-target` copy buttons (riding the pre-existing delegated handler at dashboard-site.js:169),
   and sparkline min/max/latest scale labels. Toast-on-click and clipboard writes are **browser behaviors** —
   flagged below.
5. **Corruption drill** — copied `~/.miller/telemetry.db` to the session scratchpad, truncated to 2,048
   bytes, launched a second instance with `MILLER_TELEMETRY_DB=<scratch copy>` on port 4992: `/` 200,
   `/workspace` 200, `/telemetry.json` 200; telemetry panel rendered the degraded empty shape
   ("No telemetry recorded yet.") instead of a 500 — exactly Task 1's approved degrade contract for
   telemetry (the explicit error NOTICE is the registry-corruption surface, unit-covered by
   `ReadIndex_CorruptRegistryDbReturnsEmptyIndexWithError` + the Task 4 render test). Scratch copy deleted;
   both scratch instances killed.
6. **Timestamps** — every `<time class="rel-ts">` in the served HTML carries humanized text ("34m ago")
   with the ISO value only in `datetime`/`data-ts`; zero time elements had raw `+00:00` bodies, so there is
   no ISO flash to suppress — the server renders the final text (JS only keeps it fresh).

## Real-file invariant

`~/.miller/telemetry.db` row count 19,151 before and after; `~/.miller/workspaces.db` 56 before and
after. The drill ran exclusively against the scratch copy.

## Flagged for human eyes (browser-only, not blockers)

- Theme toggle visual check in both directions (tokens + flip rules verified served; ratios verified by math).
- One live 30s auto-refresh swap with filter text typed (logic unit-verified; store pattern proven elsewhere).
- Open-folder toast appearance and chip clipboard copy (markup + delegated handler verified; `navigator.clipboard` needs a browser).
- The Task 4 worker's note that `aria-sort` on a plain `<button>` is ignored by some AT (spec-required shape; revisit only if a11y feedback arrives).
