# TODO

## Campaign 2026-08-25 — do all of it, in this order

Status legend: `queued` / `in progress` / `blocked on <what>` / `done <commit>`.

1. CT docs: supported languages/frameworks, ongoing-support note — **done** (docs/continuous-testing.md
   is the authoritative matrix; README/known-limits/tests contract point at it; site #testing gained the
   QML row and the jest vercel/ms fact)
2. julie-extract 2.37.0 pin bump + consumption (verify `ExtractSourceLimits` against published
   `languages.discovery_limits`, map the new `unsupported` update disposition into the refusal ledger,
   surface `quantum_overruns` in the queue reader, wire `store maintain` into a Miller lifecycle path) —
   **done** (merged e4a3ec16..: pin 2.37.0 + guard green; Scale-tagged limits-parity test; `unsupported`
   terminal disposition parsed (was a hard delta-abort), journal retires on ANY terminal row (a failed
   terminal row can only replay its failure), refusal ledger covers oversized manifest files;
   `quantum_overruns` in store.queue; `workspace prune` runs `store maintain gc --apply` per family.
   Open policy question moved to backlog: an oversized manifest file now keeps serving stale symbols
   until the next full rebuild — julie refuses the update and Miller stops resubmitting.)
3. CT xUnit v2 detection (backlog entry below) — **done**
4. Dashboard Tests section (backlog entry below) — **done**
5. Dashboard cleanup pass (backlog entry below) — **done**
6. Semantic activation requires session restart after `prepare` (Active item) — queued
7. JSON diagnostics during family-store resolution convergence (Active item) — queued
8. Cross-tool discoverability empty states (backlog entry below) — queued
9. Windows memory investigation (Active item; needs the win-test guest) — queued
10. MCP SDK / stateless MCP evaluation → plan doc only — **done**
    (docs/plans/2026-08-25-mcp-sdk-stateless-evaluation.md: upgrade for maintenance not speed; hard
    blocker — SDK 2.x deprecates Roots (`MCP9005` = build error under warnings-as-errors) and the
    spec forbids the `RequestRootsAsync` call `WorkspaceBindingService` makes; spike = read-only
    streamable-HTTP endpoint behind `MILLER_HTTP_MCP` in the dashboard process)
11. Workspace blacklist + explicit registration gate → design docs for a user decision, no
    implementation without approval — **decision paper done**
    (docs/plans/2026-08-25-workspace-safety-design.md; recommends path-class deny list at every bind
    entry point + pending state for flagged roots only; §7 holds the seven decision questions —
    awaiting user answers)

Semantic-noise experiment — **done**: 9 paired samples, mean on−off delta −0.2 tasks (SD 1.9), off and
on each ahead in 4 reps. Verdict: noise; default-on semantic costs nothing measurable on this workload
at tight budgets. Value measurement needs a concept-search task set (open idea, not scheduled).
Findings doc + site updated; per-rep aggregates in
docs/findings/agent-efficiency/2026-08-25-semantic-noise/.

## Active

- On windows memory usage seems high, investigate.
- Docs will need updating for CT and should list supported langs/frameworks and state that support for more is ongoing
- Semantic activation after `miller semantic prepare` requires a session restart (found 2026-08-02 fresh-machine
  dogfood; evidence `.memories/2026-08-02/224155_d317.md`). Two latches: the broker stats the model cache only at
  spawn, and Miller's embedding session opens its circuit permanently on `model_not_prepared`. Fix:
  1. julie-extractors: broker re-stats the cache on `health` while unready, loads and flips ready.
  2. Miller: park (don't latch) on `model_not_prepared`; re-probe health on the converge tick. Never respawn the
     sidecar — the no-restart-loop invariant stays.
  3. `semantic prepare`: after download, send one health probe to a live broker and print the outcome; surface the
     reason + prepare hint in compact `workspace status`; fix the misleading health recommended-action.
     Miller side ships first and is safe with old sidecars; the broker fix rides the next sidecar pin bump.

- JSON diagnostics during family-store resolution convergence: `inspect`, `trace`, and `impact` with
  `format=json` pass an empty result into `ToolDiagnosticRenderer.AttachJson` and return
  `invalid_json_output`; compact correctly returns `resolution_converging`. Add JSON variants to
  `ResolutionLayerGuardTests` and render a standalone diagnostic when the attached output is empty
  (found 2026-08-11 dogfood; evidence `.memories/2026-08-11/125539_bf6d.md`).

## Closed

- julie-extract 2.32.0 scoped resolution no longer expands a one-file change into the pathological closure found
  during dogfood. Clean replay measured an 18.309s scoped median versus 31.971s forced full with zero canonical
  diffs; the real-corpus crossover improved from about 20 minutes to 165.512s, with producer regression coverage.

- Long julie-extract 2.32.0 `store import --from-artifact` requests now heartbeat the writer lease and reconcile
  stale dead claims. The real 1.03 GB Julie recovery committed both request IDs, reached exact manifest generation
  2 in 364.057s, and completed a subsequent refresh in 242.611s.

- A valid family-store pointer whose store root disappeared no longer blocks its own repair at the extractor
  eligibility gate. Miller uses the preserved legacy binary version only for downgrade safety in that exact case
  and routes the operation through `RootRebind`; malformed pointers and corrupt existing stores still refuse.

- Missing family-store bootstrap after RootRebind no longer rejects a current tier-gated legacy resolution as
  `resolution_input_incomplete`. The producer had required `complete` even when tier gating was the only reason a
  current full pass reported `partial`; julie-extract now accepts it only when current and identifier-total. The
  21-test producer adapter target and Miller public bootstrap regression prove exact rows and a readable store.

## Product Backlog

- CT xUnit v2 detection (field report 2026-08-25, EpicTrackerboard) — **done**. Discovery classifies the
  runner generation from the csproj package ids: an `xunit.v3` reference keeps `framework: "xunit"`, a
  v2-only reference (`xunit`, `xunit.core`, `xunit.assert`, `xunit.abstractions`, `xunit.extensibility.*`)
  reports `framework: "xunit-v2"` plus `unsupported_reason: "xUnit v2 detected; CT needs the v3
  self-executing assembly"`. The shared packages (`xunit.runner.visualstudio`, `xunit.analyzers`) decide
  nothing and a project carrying both generations reads as v3. `tests status` lists a v2 project with its
  reason and drops the enable ladder when nothing found is runnable; a v2-ONLY `tests enable` (and
  `--project` on one) is refused at exit 3 writing nothing, the same rule as the no-toolchain refusal; a
  MIXED repo enables the supported projects and reports the rest under `unsupported_projects`. The provider
  also probes before spawning: a build that produced the dll but no executable beside it fails with the same
  plain reason instead of the raw OS process error. Docs: `docs/continuous-testing.md` known limit rewritten,
  `docs/contracts/tests-cli-v1.md` documents `xunit-v2`, `unsupported_reason`, and `unsupported_*`. (Same
  report confirmed the watch loop end-to-end: unprompted pickup of a broken assertion, exact failing test
  named, auto-green on revert — no action needed there.) Open follow-up, user decision required: CT could
  RUN xUnit v2 through the generic `dotnet test <dll>` path the fallback provider already uses, instead of
  refusing. Real risk: a second execution path per framework, different filter shapes, new Scale evidence
  needed — do not build without a real repo that needs it.

- Dashboard CT visibility (dogfood 2026-08-25) — **DONE**: the workspace detail view has a Tests section
  between Workspace health and Pattern inventory. `DashboardTestsPanel` (in `DashboardData.cs`) is a pure
  projection of `TestsCore.Status` + `TestsCore.Failures` — the same core that backs `miller tests status
  --json` — so the dashboard owns no CT logic; `WorkspaceTestsPanel.razor` renders it in the existing
  static-SSR + htmx pattern, and `GET /fragments/tests` serves the poll (ETag/304 come free from the
  `/fragments` middleware). Shown: verdict chip, stale/tracked case counts, daemon state + activity, project
  count, the daemon build mismatch and wedged-loop notices, the selected freshness key, last run, the run in
  flight, the project list with framework and `unsupported_reason` (the new `xunit-v2` classification lands
  here), and the last five red cases in the one-line `test id` + `failure summary` shape. Decisions worth
  keeping: (a) the section polls ONLY while `Enabled && !KillSwitchOff && no error` — a never-decided
  workspace would otherwise re-run the core's filesystem project scan every 5s to report the same standing
  answer; (b) `ReadTests` resolves one registry row and reads only the panel, so a poll never pays for a whole
  snapshot; (c) the panel is read from `selectedWorkspace` alone, not from index facts, because `ct.db` is
  self-contained and stays readable when the index is not; (d) `TestsCore.CompactFreshness` became public
  rather than being re-implemented dashboard-side. Status reads stay create-nothing (guarded by
  `DashboardTestsPanelTests.ReadTests_OnANeverDecidedWorkspaceDiscoversProjectsAndCreatesNothing`), and the
  read hydrates no index — the core opens the artifact only for the live freshness cursor, which is what
  ADR-0002 permits. 25 tests in `tests/Miller.Tests/Server/DashboardTestsPanelTests.cs` (fast suite).

- Dashboard cleanup pass — **done** (2026-08-26). Audit + decision:
  [`docs/plans/2026-08-26-dashboard-interaction-cleanup.md`](docs/plans/2026-08-26-dashboard-interaction-cleanup.md).
  The premise "we've swapped to blazor" was wrong — `DashboardHostPipeline` calls only `AddRazorComponents()`,
  so Razor Components is a static-SSR template engine here with no circuit and no interactivity. The audit
  scored the two client stacks: **htmx carried 8 in-page interactions across 6 panels** (activity 5s, refresh
  status 2s, telemetry 30s + manual, workspaces 30s, tests 5s, plus the refresh and open-folder POSTs), 31
  attribute uses, and 8 `dashboard-site.js` event hooks that implement the fragment ETag/304 protocol and the
  `X-Miller-Dashboard` CSRF header. **Alpine carried 2** (the workspace filter, two table sorts) through 16
  directives, with no reactive template and its state already living in plain-JS module stores. So htmx won and
  Alpine was removed: both controllers moved into `dashboard-site.js` as delegated `[data-sort-col]` /
  `#workspace-filter` handlers, and `wwwroot/lib/alpine/cspalpine.min.js` + `wwwroot/js/alpine-components.js`
  were deleted with their script tags, pipeline routes, and `release.yml` asset checks. idiomorph stays — it is
  the htmx `morph` extension every polling panel names, not a third stack. Dashboard `wwwroot` 268 KB → 196 KB
  in all four platform archives. Verified by 274 fast-suite dashboard tests plus a 27-check Playwright run
  against the live dashboard (filter, empty note, `/` shortcut, both tables' sort direction and `aria-sort`
  placement, sort surviving a morph poll, theme toggle, copy button, remove-confirm cancel, zero console
  errors). Load-bearing rule recorded in `CLAUDE.md`.

- Oversized-manifest staleness policy (found in the 2.37.0 consumption slice): a manifest file that grows
  past julie's 1 MiB limit can no longer have its rows retired by `store update` (2.37.0 refuses it), so
  its stale symbols serve until the next full rebuild. Miller's refusal ledger stops the resubmit loop but
  nothing surfaces the staleness. Options when it matters: surface a per-file `stale_oversized` marker in
  status/health, or teach a force scan to retire such rows. Deleting rows for a file that still exists was
  judged worse than serving them; decide only with a real-world case in hand.

- Cross-tool discoverability: keep improving high-traffic empty states so `search`, `trace`, `impact`, and `inspect` hand agents to `content`, `patterns`, source-region search, or complexity when those are the better next tool.

- MCP SDK / stateless MCP: with new stateless MCP support available now, evaluate and plan the upgrade to the new MCP SDK. Goal: drop long-lived reader process assumptions where they hurt, improve multi-client behavior under Hermes gateway + CLI, and reduce cold/warm path surprises. Capture current stdio multi-process shape (gateway child + per-session reader) before changing it.

- Workspace blacklist / `.julieignore` sufficiency (station incident 2026-08-10):
  Hermes CLI opened `workspace_id=hermes-agent` against `~/.hermes/hermes-agent` and built a full index there (~7.8k files, ~543k symbols, ~4.3G under `.miller/`). Query path then sat at ~3.5s search / ~6.5s inspect; one `ensure_fresh` cold open took ~254s. Decide whether `.julieignore` (or a Miller-global ignore/deny list) is enough to keep install/home paths like `~/.hermes/hermes-agent` from being indexed, or whether path-class policy is required (e.g. deny `~/.hermes/**`, allow `~/source/**` by default). Note: `.julieignore` only helps once a root is chosen — it does not stop a bad root from being registered.

- Explicit workspace registration gate: consider requiring a user/agent interactive confirmation (or an explicit `workspace open --register` / allowlist step) before Miller builds or attaches a new workspace index. Intent: stop unintended indexes, enforce intentional working dirs, and make “current workspace” a conscious choice rather than a side effect of the first `search`/`inspect` with a path/`workspace_id`. Pair with clearer agent guidance when a root looks like an install tree, home config tree, or `/tmp`.

## Conditional Backlog

- Eros-first complexity workflows: keep `complexity export --jsonl` as the Miller fact feed. Do not add a Miller MCP/interactive complexity tool unless Eros dashboard usage proves a repeated agent workflow that cannot be served by the export.
- Dead-code workflow: Miller removed `references candidates` on 2026-08-18. Keep `references export` as the usage-fact feed. Historical `dead_code_*` metric-history rows stay readable via `--metric`.
- Eros CLI/export contracts: add or harden Miller CLI/export surfaces only when a concrete Eros workflow needs stable code facts or operations that the documented contracts do not cover. Current public surfaces are documented in `docs/contracts/cli-eros-v1.md`.
- Miller-native query/ranking surfaces: design only after a concrete agent or Eros workflow needs them. Likely future slices are structural-fact search/filtering, complexity report/ranking with Miller-owned thresholds, and body-hash duplicate/clone discovery.
