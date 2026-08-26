# TODO

## Campaign 2026-08-25 — do all of it, in this order

Status legend: `queued` / `in progress` / `blocked on <what>` / `done <commit>`.

1. CT docs: supported languages/frameworks, ongoing-support note — **done** (docs/continuous-testing.md
   is the authoritative matrix; README/known-limits/tests contract point at it; site #testing gained the
   QML row and the jest vercel/ms fact)
2. julie-extract 2.37.0 pin bump + consumption (verify `ExtractSourceLimits` against published
   `languages.discovery_limits`, map the new `unsupported` update disposition into the refusal ledger,
   surface `quantum_overruns` in the queue reader, wire `store maintain` into a Miller lifecycle path) —
   **blocked on the v2.37.0 release publish + the semantic-noise batch finishing (machine must be quiet)**
3. CT xUnit v2 detection (backlog entry below) — queued
4. Dashboard Tests section (backlog entry below) — queued
5. Dashboard cleanup pass (backlog entry below) — queued
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

Semantic-noise experiment (8 reps, off-vs-on at the frozen budget) runs until ~02:50 UTC; its analysis
and the findings/site update land when it completes.

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

- CT xUnit v2 detection (field report 2026-08-25, EpicTrackerboard): CT runs the built self-executing test
  assembly (`DotnetTestProvider.TestAssemblyPath` + `ExecutableExtension`), which only xUnit v3 /
  Microsoft.Testing.Platform produces. An xUnit v2 project builds only a dll plus `testhost.exe`, so CT fails
  late with `ct-discovery-failure: An error occurred trying to start process '...\<Project>.Tests.exe'` — the
  user went hunting for a broken build. Fix in two places: (1) classify the runner generation at
  enable/inventory time (xunit v3 vs v2 package reference in the csproj) and say plainly "xUnit v2 detected;
  CT needs the v3 self-executing assembly" in `tests enable`/`status` output — same honesty rule as the
  refused no-toolchain enable; (2) when discovery fails because the exe is missing but the dll exists next to
  it, name the real cause in the failure message instead of the raw process error. Docs note: `dotnet new
  xunit` still scaffolds v2 on SDK 10.0.400, so standard scaffolding hits this. (Same report confirmed the
  watch loop end-to-end: unprompted pickup of a broken assertion, exact failing test named, auto-green on
  revert — no action needed there.)

- Dashboard CT visibility (dogfood 2026-08-25): watching the dashboard during a live CT session gives no view
  of test status. Add a Tests section to the workspace detail view fed read-only from the CT sidecar facts the
  contract already exposes (`tests status --json` core: enabled state, project inventory, verdict, stale/selected
  counts, daemon liveness/version, last run + failures with test name and exception type — the one-line failure
  shape the field report praised). `ct.db` is self-contained and cheap to read, so this fits the ADR-0002
  dashboard rule (aggregate facts only, no index hydration); status reads must stay create-nothing. Lesser
  fallback if the section stalls: show tool RESPONSES (not just calls) for `tests` in Live Activity — noted as
  strictly less useful than a dedicated section.

- Dashboard cleanup pass: the dashboard is Razor Components running the static-SSR + htmx + Alpine hybrid —
  `DashboardHead.razor` still loads and configures htmx (`selfRequestsOnly`), `DashboardScripts.razor` loads
  idiomorph + Alpine + `alpine-components.js`, so none of it is provably dead today. Audit which interactions
  still ride htmx/Alpine vs Razor, converge on one interaction model, then delete the losing stack's assets
  (`wwwroot/lib/htmx`, `lib/idiomorph`, `lib/alpine`, `js/alpine-components.js`) and their CSP/config residue.
  Release packaging note: dashboard wwwroot assets ship in every archive, so removing dead ones shrinks all
  four platform packages.

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
