# Continuous testing moves into Miller — migration design

Date: 2026-08-18. Status: design approved in brainstorm; implementation plan pending.
Direction record: goldfish brief `ct-sidecar-direction` (2026-08-18).

## Goal

Port Eros's continuous-testing engine into Miller so "is the build green" is one cheap
call for an agent working in a workspace. Miller already owns the hard parts: test
selection is the `impact` engine, change detection is the freshness/revision machinery,
and process discipline (job caps, backoff journals, supervision) is established practice.

## Source material

`Eros.ContinuousTesting` in the eros repo (main @ 71d78cd): 37 files, ~16k lines of
source, ~20k lines of tests. Self-contained — it references only `Eros.Core` (models)
and `Eros.Store` (SQLite state). Contents:

- Providers: `DotnetTestProvider`, `RustTestProvider`, `JavaScriptTestProvider`,
  `PythonTestProvider`, plus cargo metadata/list/output parsing.
- Importers: JUnit result artifacts, coverage artifacts.
- Selection: `ContinuousTestImpactSelector` + selection contracts (evidence-weighted
  impact → test-case mapping).
- Daemon: runner, queue, revision poller, run-concurrency gate, durable freshness,
  CT generation paths/disk state.
- Analysis: classifier, confidence engine, pre-edit confidence, quality analyzer,
  readiness builder, status summary, coverage narrower.

The one real seam: the selector reads Eros's `WorkspaceStore`, which is Eros's
projection of Miller artifact data. In Miller that layer disappears — the selector
reads Miller's own fact tables (symbols, references, identifiers via
`RevisionFactCache`/`QueryTimeResolver`) and the impact engine directly.

## Architecture in Miller

### Project layout

- New project `src/Miller.Testing`, namespace `Miller.Testing`. The port lands here,
  renamed from `Eros.*` so nothing sticks out: `ContinuousTest*` type names stay
  (they are descriptive), `Eros.Core`/`Eros.Store` references are replaced with
  Miller equivalents.
- Pure selection/parsing logic that needs no I/O may live in or alongside
  `Miller.Core` conventions; the project seam rule holds — `Miller.Core` keeps zero
  I/O dependencies. Process-spawning and disk code stays in `Miller.Testing`.
- Tests fold into `tests/Miller.Tests`. Anything that spawns a real provider process
  (`dotnet test`, `cargo test`, node, pytest) or builds large fixtures is tagged
  `[Trait("Category","Scale")]`. Pure logic (parsers, selector, queue, gates,
  summaries) joins the fast suite. The fast suite must stay fast and pure.

### State

- New revision-keyed sidecar `<workspace>/.miller/ct.db`, owned by Miller, same
  pattern as `telemetry.db` and `history.db`. The Eros CT-generation store logic
  (`WorkspaceStore.ContinuousTesting` / `CtGenerations` partials) ports into a
  dedicated CT store class writing this file.
- Verdict semantics carry over unchanged: aggregate is `Green` only when complete
  results exist at the selected revision; known staleness → `Partial`; unknown
  execution/watch health → `Unknown`. Never report green without evidence.

### Daemon process model

- The daemon is a long-running CLI verb of the main binary: `miller tests serve`.
  Separate process, same executable, no new packaging. It polls the artifact
  revision (the ported revision poller) and runs selected tests through the
  providers.
- The MCP server process never runs tests in-process. It reads `ct.db` for status
  and manages the daemon process through explicit lifecycle operations.

### Lifecycle and safety (the Eros 2026-07-28 incident as product law)

- **Explicit start only.** The daemon starts from `miller tests serve` (human),
  the dashboard, or the MCP `tests` tool's explicit `start` operation (agent).
  No other path — not status reads, not server boot, not workspace open — may
  start it as a side effect.
- **No catch-up storms.** On start the daemon is status-only: it computes and
  reports staleness but executes nothing until a new change arrives or an explicit
  `run` is requested. Delta-unavailable or degraded index never falls back to a
  full-suite run.
- **Opt-in per workspace, default off.** Enabling CT is a recorded workspace
  setting; new workspaces are disabled.
- **Global budget.** At most one workspace's daemon executes tests at a time, with
  a bounded number of provider processes (default 1). A start that cannot hold the
  budget succeeds as paused and says so in status.
- **Degraded-index backoff.** While the index reports unhealthy/migration-required,
  the daemon stops enqueueing and backs off; it never spins on exit-code loops.
- **Kill switch.** `MILLER_CT=off` is a permanent zero-work guarantee, mirroring
  `MILLER_SEMANTIC=off`: no daemon, no ct.db writes, status reports disabled.

### Surfaces

- **CLI verbs** (land first): `miller tests status [--json]`, `miller tests serve`,
  `miller tests run [--wait]`, `miller tests enable|disable`, `miller tests stop`.
  JSON output joins the documented export contracts so an external console can
  consume CT evidence.
- **MCP tool `tests`** (approved 2026-08-18): one tool, `operation` parameter,
  matching the `workspace` tool pattern. Operations: `status` (the cheap call),
  `failures` (bounded detail on red), `start`, `stop`, `enable`, `disable`, `run`.
  Description ≤900 chars; embedded agent-instructions core stays ≤1,900 chars;
  `AgentInstructionsTests` gates updated for a tenth tool.
- **Dashboard** (later slice, not this migration): CT status card on the workspace
  detail view, start/stop buttons behind the existing antiforgery POST pattern.

## Not moving

- Fleet/multi-workspace CT, hub integration, central projections
  (`CentralStore.CtProjection`) — fleet views belong to the external console, fed
  by exported CT evidence, never by remote execution.
- Eros's hub autostart machinery — dead by design.

## Sequencing inside the migration

1. Port the pure core: contracts, parsers, selector (rewired to Miller facts),
   queue, gates, summaries — with their fast tests.
2. Port state to `ct.db` + durable freshness.
3. Port providers + daemon runner behind `miller tests serve`, with Scale tests.
4. CLI verbs + JSON contracts.
5. MCP `tests` tool + guidance budget updates.
6. Release notes, docs map, README surface.

## Acceptance criteria

- [ ] `Eros.ContinuousTesting` functionality runs in Miller with no `Eros.*`
      references; all four providers work.
- [ ] Selection reads Miller fact tables directly; no projection layer.
- [ ] `ct.db` sidecar; no CT writes anywhere else.
- [ ] Explicit-start-only enforced; a status call on a stopped daemon starts
      nothing (test-guarded).
- [ ] Start executes nothing until a new change or explicit `run` (test-guarded).
- [ ] Global budget and degraded-index backoff enforced (test-guarded).
- [ ] `MILLER_CT=off` is total: zero CT work, honest status.
- [ ] Fast/Scale split preserved: no provider-spawning test in the fast suite.
- [ ] MCP `tests` tool within guidance budgets; `AgentInstructionsTests` green.
- [ ] Green verdict requires complete results at the selected revision.
