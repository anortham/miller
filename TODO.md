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
6. Semantic activation requires session restart after `prepare` (Active item) — **done**
   (the park/re-probe state machine, the compact `workspace status` reason + prepare hint, and the health
   recommended-action all shipped in bedbdbfe; this pass added the plain-English activation line the verb
   prints after the probe, the honest `semantic_disabled` outcome for `MILLER_SEMANTIC=off`, and an
   old-sidecar bound test. The broker half — re-stat the model cache on `health` while unready — rides the
   next `julie-semantic-sidecar` pin bump.)
7. JSON diagnostics during family-store resolution convergence (Active item) — **done**
   (the renderer fix shipped in 877fa992; query-time resolution then deleted the `resolution_converging`
   layer and its guard tests, leaving `trace`/`impact` with no converging-JSON coverage — closed with
   `TraceImpactLevelGuardTests` and the standalone-envelope contract section)
8. Cross-tool discoverability empty states (backlog entry below) — **done**
   (`CrossToolHandoff` is the one decision table; `ToolDiagnosticAction.CompactOnly` carries every handoff
   on the ADR-0001 nudge channel, so JSON `diagnostic.next_actions` stays byte-identical)
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

- Telemetry audit 2026-08-27 (`docs/findings/2026-08-27-telemetry-audit.md`), in priority order:
  1. `edit replace_text` unhandled `InvalidOperationException` — RESOLVED 2026-08-27. The local
     `~/.miller/telemetry.db` `error_message`/`error_detail` columns (which the JSONL export omits)
     held the answer all along: all 107 rows are ONE cause — `FreshnessService.LoadPinnedStoreIndex`
     throwing "generation changed before its lazy repository was loaded" when `EditTool.Edit` forces
     `IndexHolder.Current` mid-swap. Already fixed in 1.24.1 (550dc679: retry the lazy load, map to
     `index_reloading`/Unavailable, stop caching the failure); zero occurrences on 1.24.1+ (last row
     is a 1.24.0 process). The WRN backstop log line stays as defense in depth for a NEXT novel escape.
  2. `context` tool latency — p50 ~7 s / p95 20–37 s. Fixes A1 AND B1 shipped 2026-08-27
     (`docs/plans/2026-08-27-context-latency-diagnosis.md`). A1: the cold fact-cache load no longer
     blocks anchor_resolution (4,813 ms → 6 ms; background load + `term_rescue: skipped_cold_facts`).
     B1: the Blazor evidence read no longer scans the 908k-row symbols table per revision advance
     (supplemental 1,192 → 135 ms; post-save graph_reach 1,285 → 223 ms; post-save call 3.1 → 2.0 s;
     cold first call 7.6 → 4.8 s). OPTIONAL remainder: a fresh process's first call still joins the
     background fact load inside graph_reach (~2.9 s, that call only) — re-read field telemetry
     after release before spending more here.
  3. A source file deleted mid-scan fails the whole store delta (`RequireCommitted`, os error 2,
     Tycho 8/26 ×3) — FIXED in julie-extract v2.37.2 (vanished files commit as deletions; only
     NotFound; decision record `docs/decisions/2026-08-27-vanished-file-delta-semantics.md` in
     that repo). Miller consumed it 2026-08-27: pin bump to 2.37.2 (pins.json, contract constant,
     THIRD-PARTY-NOTICES, version-asserting tests).
- On windows memory usage seems high, investigate.
- Docs will need updating for CT and should list supported langs/frameworks and state that support for more is ongoing

## Campaign 2026-08-26 — CT dogfood findings (status)

All findings below were addressed on branch `worktree-ct-dogfood-campaign`
(worktree `.claude/worktrees/ct-dogfood-campaign`, 17 commits over 2a5a80ec, fast+Scale green) —
**awaiting merge/push approval**. Plan: `docs/plans/2026-08-26-ct-dogfood-campaign.md`; full report:
`.memories/autonomous-run-2026-08-26-ct-dogfood-campaign.md` (on the branch).

1. Build-output relocation — **done** (workspace-local `.miller/ct/build/<proj12>`, Windows MAX_PATH fallback;
   infra-shaped failure classification shipped under finding 7's `group=error_class`)
2. Vitest visibility — **done**, with a corrected diagnosis: vitest RAN and passed 30s after daemon start
   (ct.db evidence); "557 untouched" was the whole not-yet-run set. Shipped per-project status rows,
   project-named `covers_all`, `reason=no_selection` drain logging, daemon stdout breadcrumb → real logs are
   `.miller/logs/miller-<date>.log` (`role:ct`)
3. Disable retires red cases — **done** (SQL predicate; re-enable restores)
4. `run wait=true` joins the active run — **done** (`run already active` reason)
5. Stale spike — **done** (watermark seeding never worked — 0 rows on two live DBs; fixed + poller cursor invariant)
6. Red ledger continuity — **done** (reds keep state on every staling path; no red loop)
7. `failures` size/filter/grouping — **done** (12 KiB MCP paging, 400-byte summaries, `project=`, `group=error_class` + `infra_shaped`)
8. csproj search — **Miller half done**; julie-extractors `feat/msbuild-xml-extensions` @ ee0da1a7 committed
   (NOT pushed/released). Needs: julie-extract release + pin bump (user approval)
9. `inspect <file>::<symbol>` — **done** (resolver parse, shared by trace/impact/edit/CLI)
10. Status run-block omission — **done** (honest compact line)

Open user decisions: merge/push the branch; julie-extract release + pin bump; general answer for hostile
repo build hooks (`npm ci` per build) — see the run report. (The round-2 campaign below IS the general
answer candidate: truncation-as-Unknown + the idle drain make CT converge on a hostile repo with zero
project-side settings.)

## Campaign 2026-08-26 evening — CT dogfood round 2 (status)

Round-2 findings from Tycho on miller 1.24.0+fa38f826 (the user's raw notes live in the main checkout's
uncommitted TODO edit). All addressed on branch `worktree-ct-dogfood-round2`
(worktree `.claude/worktrees/ct-dogfood-round2`) — **awaiting merge/push approval**.
Plan: `docs/plans/2026-08-26-ct-dogfood-round2.md`.

1. Red cases retired without a rerun — **done** (run start captures `pre_run_state` (ct.db schema v6) and
   keeps a red row's committed key; requested-but-unreported cases restore red with one owed stamp at run
   commit; bounded `role:ct` `run_unreported_cases` line)
2. Self-inflicted churn loop — **done, two halves.** Diagnosis correction: julie already no-ops
   byte-identical rewrites (`version_id` is content-keyed; the store delta reader's hash gate is now
   pinned by test). The Miller-side stall was (a) truncated impact answering Unavailable — cursor pinned,
   interval growing, auto-runs paused; now it delivers the delta as Changed and the selector fails it
   closed to Unknown, staleness lands, the cursor advances; (b) no idle drain — `CtIdleDrainPolicy` now
   schedules ONE owed-backlog drain when the workspace settles (healthy poll at live revision, quiet ≥
   debounce, empty queue, auto-runs not paused, 5-min per-context cooldown), executed as an explicit
   test-ID list, never whole-suite
3. `edit` hard-fails while CT builds churn — **done** (both lazy index factories retry onto the current
   generation, bounded by the sidecar reopen constant; `IndexHolder` discards a faulted lazy state instead
   of replaying the cached exception; exhausted retries classify `unavailable`/`index_reloading` with a
   plain retry message, not `internal_failure`)
4. `failures` hides the real OneTimeSetUp error — **done** (write-time two-part summary: first line + first
   error-shaped line, 400 UTF-8 bytes; TRX capture widened from StackTrace/sibling Messages; run-level
   RunInfo errors folded into each failed case; contract doc updated)
5. Auto-run pause invisible — **done** (`AutoRunsPaused`/`PauseReason` on the status record;
   `daemon.auto_runs_paused`/`daemon.pause_reason` in `tests status` JSON; compact `auto-runs paused:`
   line; one `role:ct` line on pause enter and clear)
6. `impact git=true` ignores staged changes — **done** (empty unstaged diff probes the staged diff once;
   when staged changes exist the diagnostic says so with a JSON-visible `staged=true` action; CLI note
   spells `--staged`)
7. CT bin depth breaks cap-8 walk-up helpers — **done** (build root flattened to `.miller/ct-<proj12>`;
   deepest assembly dir exactly 5 levels below the root, pinned by a separator-count test; Windows tail
   budget recomputed (root budget 117 → 123); peer scans filter to the `ct-` prefix; coordinator
   maintenance sweeps the legacy `.miller/ct/build` tree under the janitor's lease rules)
8. Impacted under-selection (1 case vs round 1's 6) — **watch item, open** (correct case was picked; no
   code change)
9. Watcher latency ~4 min under churn — **explained + addressed by finding 2's fixes** (the delay was the
   index-convergence window plus sticky-unavailable backoff, not the debounce; truncation-as-Unknown
   removes the stall arm)

Also closed while gating this campaign: the Active "intermittent single-test failure" — it is
`MillerLoggingSetupTests.SinkThatCannotOpenItsFileReportsThroughSerilogSelfLog`, which blocked only the
UTC day's log family while Serilog rolls by LOCAL date, so it failed exactly in the evening window where
the two dates diverge (both 2026-08-26 sightings were evening runs; proven TZ=UTC green / CDT red). The
test now blocks both families.

## CT dogfood findings 2026-08-26 — Tycho workspace (5 projects: 3 nunit, 2 vitest)

The core loop works: plant a bug in `RecurrenceService` → the watcher auto-ran an impacted-scope
foreground run (exactly the 6 cases `impact` predicted) → `failures` showed the one red case with the
exact assertion → revert → auto-rerun → green. Enable/disable UX is clean. The findings below are
everything that made the session harder than that loop.

1. **Out-of-tree execution silently breaks repo-root-relative tests — and the fix must be
   zero-config on the project side.** The dotnet provider builds with `--artifacts-path
   <BuildOutputRoot>`, so `TestContext.CurrentContext.TestDirectory` is outside the repo. 87 of the
   140 baseline failures were `DirectoryNotFoundException` from tests that walk up to find
   `Tycho.slnx` or a sibling source dir; all pass under plain `dotnet test`. User direction
   (2026-08-26): a project must NOT need Miller-specific settings to go green under CT — "most users
   are not going to want to fiddle with that". So `MILLER_CT_WORKSPACE_ROOT`-aware test helpers are
   NOT an acceptable answer, and Tycho commit 5dba115 (csproj conditions on that variable) is the
   kind of per-project fiddling to eliminate, not the model. Candidate zero-config fix: put the CT
   build-output root INSIDE the workspace (e.g. `.miller/ct/artifacts/<generation>/`) so walk-up
   repo-root discovery keeps working; the watcher/indexer must ignore `.miller/**` so this adds no
   churn. Residual even then: repo-defined build hooks that are hostile to repeated out-of-band
   builds (Tycho's `npm ci` on every test build) need a general answer, not an env-var contract each
   repo must opt into. Independent of the fix, `tests failures` should classify infra-shaped errors
   (DirectoryNotFoundException / missing-browser / native-lib-init) and say "fails only under CT,
   passes under plain `dotnet test`" when that is knowable.
2. **vitest projects never ran and nothing says why.** Both vitest projects enabled cleanly
   (`unsupported_reason=null`), the ClientApp suite passes locally in 5.7s (478 tests), yet after
   enable + start + a full `run` only `ct-provider:dotnet` executed (`known=992` of `selected_count=1549`).
   Verdict stays `partial` forever with no per-project row explaining which project is missing a run or
   why. Worse, the workspace-scope run reported `covers_all=true` while 557 vitest cases were never
   touched. Daemon logs (`.miller/ct/daemon.{out,err}.log`) are 0 bytes — no diagnosability at all.
3. **Disabling a project does not retire its red cases.** After `tests disable
   project=src/Tycho.UiTests/...` the project left the roster but its 45 Playwright reds still count in
   `failures` (total stayed 140) and in the stale ledger, so the verdict can never clear.
4. **`run wait=true` during an active run returns instantly with `verdict=unknown unacked`.** The
   command file shows the request WAS acknowledged ~25s later. Expected: join the in-flight run or
   wait for the ack; at minimum say "a run is already active".
5. **Stale count spikes to the full selected set on every revision bump, then recomputes down.** A
   plain manual `dotnet test` (bin/obj churn only, zero symbol changes) drove rev 2892→3260 and stale
   105→245; a one-line source edit reset stale to all 1549 before trimming to ~145 within a minute.
   The number is meaningless while it swings; show the post-trim stale count, and don't mark cases
   stale when no indexed content changed.
6. **The red ledger loses failures between revision churn and rerun.** `failures` showed "20 of 140",
   then "(1)" right after an edit-driven revision bump (only the freshly-red case), then 140 again once
   the backfill re-ran the old cases. An agent reading the "(1)" snapshot would conclude 139 problems
   were fixed. Red cases should stay listed until they pass.
7. **`failures format=json limit=200` blows the MCP token cap** (80k chars on one line, saved to a
   spill file). No size guard on the JSON path, and no server-side grouping/filtering — a
   `group=error-class` or `project=` filter would answer "what is actually broken" without paging 140
   rows.
8. **`search` cannot find text in `.csproj` files in any mode.** `MILLER_CT_WORKSPACE_ROOT` sits in two
   csproj files (comment + attribute condition); modes `source`, `content`, and `all-text` all return
   no hits. Had to fall back to git grep — the exact reflex Miller tells agents to drop. MSBuild XML
   should be in at least one text corpus.
9. **`inspect target="<file>::<symbol>"` fails with a misleading diagnostic** (`file_not_indexed`,
   suggesting a file search). The `scope` parameter works fine. Either support the `::` form or make
   the diagnostic say "use scope=".
10. **`status` sometimes omits the run block while `activity: executing`** (observed once, 17:30:39Z:
    header said executing, no provider/selection/progress lines). Cosmetic but confuses polling.

## Closed

- Semantic activation after `miller semantic prepare` no longer needs a session restart on Miller's side
  (found 2026-08-02 fresh-machine dogfood; evidence `.memories/2026-08-02/224155_d317.md`). Of the two latches,
  Miller's is gone: `SemanticEmbeddingSession` PARKS on `model_not_prepared` instead of latching, so
  `EnsureStartedAsync` re-probes and `TryEnterCall` fails the embed fast without counting a fatal. Every converge
  drain opens with that probe (`VectorConvergeService.DrainAsync`), and the 5-minute held-cursor retry is what
  keeps the ticks coming on a quiet workspace, so a broker that flips ready resumes embedding with no restart.
  Compact `workspace status` renders the reason plus the `miller semantic prepare` hint, and `workspace health`
  recommends `prepare` instead of the wrong "keep a resident leader running". `semantic prepare` sends ONE health
  probe to a live broker after the download (connect-only, never a spawn) and now prints the outcome in plain
  English beside the machine token, including the honest `semantic_disabled` state under `MILLER_SEMANTIC=off`.
  Against an old sidecar that never flips ready the cost is one connect plus one `health` per tick, no respawn,
  no circuit trip, and no log line.
  Residual: the sidecar half — the broker re-stats the model cache on `health` while unready, then loads and
  flips ready — lives in julie-extractors and rides the next `julie-semantic-sidecar` pin bump. Until then
  `still_not_ready` is the honest answer and a restart is the user's recovery.

- JSON diagnostics during convergence no longer reach a `--json` consumer as `invalid_json_output`
  (found 2026-08-11 dogfood; evidence `.memories/2026-08-11/125539_bf6d.md`). `AttachJson` renders the
  standalone `{schema_version, tool, diagnostic}` document when there is no payload to attach to, so every
  caller with that shape is covered at the renderer rather than per tool. The `resolution_converging` layer
  the dogfood hit was deleted by query-time resolution, which took `ResolutionLayerGuardTests` with it and
  left the successor `reference_layer_converging` diagnostic unguarded for `trace` and `impact` in both
  formats; `TraceImpactLevelGuardTests` closes that gap on the MCP and CLI paths and pins the standalone
  envelope. `docs/contracts/cli-eros-v1.md` now documents both diagnostic shapes and how to tell them apart.

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

- Cross-tool discoverability empty states — **DONE.** Every empty read that had a better answer in a
  DIFFERENT tool now names it, from one decision table
  ([`CrossToolHandoff`](src/Miller.Server/Tools/CrossToolHandoff.cs)) rather than per-tool string soup.
  Shipped handoffs: `search mode=content` → `mode=source` and the reverse; `search
  mode=external|web|all-text` → `content operation=list`; `search regions=…` → whole source bodies;
  `search mode=markers` → the marker words as literal source text; ANY filtered `search` miss with
  out-of-scope hits → the same call with `file_pattern`/`language` dropped, which beats every mode
  handoff because retrying a mode searches the same narrow scope again; `trace mode=refs` with no
  references → `regions=string_literal` for the DI/reflection/config uses the graph cannot link;
  `impact` on a change with no seed symbols → `patterns operation=summary path=…`, because a changed
  file with no indexed symbols is docs, markup, or config; `inspect` on an unresolvable name → a search
  across symbols, paths, and text (it offered nothing at all before); `inspect` on an indexed file with
  no symbols → that file's structure facts, or the same call minus its own `kind` filter; `patterns`
  free-text miss → the raw source text for those words. Every handoff is
  `ToolDiagnosticAction.CompactOnly`, so JSON `diagnostic.next_actions` is unchanged; each is pinned by
  an exact-line test in `CrossToolHandoffTests`. Also fixed the missing line break that ran the patterns
  filtered-out sentence into its own `Next:` block.
  Residual, needs telemetry evidence before acting: the CLI `search` verb attaches no empty diagnostic at
  all (pre-existing — the MCP route does), so none of these lines reach `miller search`; and `trace
  mode=path` still offers only graph recovery, where an `impact` handoff might read better.

- MCP SDK / stateless MCP: with new stateless MCP support available now, evaluate and plan the upgrade to the new MCP SDK. Goal: drop long-lived reader process assumptions where they hurt, improve multi-client behavior under Hermes gateway + CLI, and reduce cold/warm path surprises. Capture current stdio multi-process shape (gateway child + per-session reader) before changing it.

- Workspace blacklist / `.julieignore` sufficiency (station incident 2026-08-10):
  Hermes CLI opened `workspace_id=hermes-agent` against `~/.hermes/hermes-agent` and built a full index there (~7.8k files, ~543k symbols, ~4.3G under `.miller/`). Query path then sat at ~3.5s search / ~6.5s inspect; one `ensure_fresh` cold open took ~254s. Decide whether `.julieignore` (or a Miller-global ignore/deny list) is enough to keep install/home paths like `~/.hermes/hermes-agent` from being indexed, or whether path-class policy is required (e.g. deny `~/.hermes/**`, allow `~/source/**` by default). Note: `.julieignore` only helps once a root is chosen — it does not stop a bad root from being registered.

- Explicit workspace registration gate: consider requiring a user/agent interactive confirmation (or an explicit `workspace open --register` / allowlist step) before Miller builds or attaches a new workspace index. Intent: stop unintended indexes, enforce intentional working dirs, and make “current workspace” a conscious choice rather than a side effect of the first `search`/`inspect` with a path/`workspace_id`. Pair with clearer agent guidance when a root looks like an install tree, home config tree, or `/tmp`.

## Conditional Backlog

- Eros-first complexity workflows: keep `complexity export --jsonl` as the Miller fact feed. Do not add a Miller MCP/interactive complexity tool unless Eros dashboard usage proves a repeated agent workflow that cannot be served by the export.
- Dead-code workflow: Miller removed `references candidates` on 2026-08-18. Keep `references export` as the usage-fact feed. Historical `dead_code_*` metric-history rows stay readable via `--metric`.
- Eros CLI/export contracts: add or harden Miller CLI/export surfaces only when a concrete Eros workflow needs stable code facts or operations that the documented contracts do not cover. Current public surfaces are documented in `docs/contracts/cli-eros-v1.md`.
- Miller-native query/ranking surfaces: design only after a concrete agent or Eros workflow needs them. Likely future slices are structural-fact search/filtering, complexity report/ranking with Miller-owned thresholds, and body-hash duplicate/clone discovery.
