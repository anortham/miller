# CT Dogfood Campaign (2026-08-26 Tycho findings) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Close the ten CT dogfood findings from TODO.md (2026-08-26, Tycho workspace) plus the two non-CT findings (csproj search, inspect `::`).

**Architecture:** Fixes land in the existing CT store/daemon/tool seams; no new subsystem. The one structural change is the CT build-output root moving from `<os-temp>/miller-ct/build/...` to `<workspace>/.miller/ct/build/...` so repo-root-relative tests pass with zero project configuration (user direction recorded in TODO.md finding 1). One task edits the sibling repo `julie-extractors` (same owner) for the csproj manifest gap; its release/pin bump is explicitly out of this campaign.

**Tech Stack:** .NET 10, SQLite (ct.db sidecar), xUnit v3; Rust for the one julie-extractors task.

**Architecture Quality:** No new modules. Risky decision: inverting the `ValidateBuildOutputRoot` invariant (build output must now live INSIDE `<root>/.miller/`) with a length-budget fallback to the legacy temp root for deep Windows paths. If code reality contradicts any shape stated in a task, report a plan mismatch; do not redesign locally.

## Ground evidence (read before arguing with a finding)

The live Tycho ct.db was inspected read-only (scratchpad note `tycho-evidence.md`; key facts inline below). Three findings shifted:

- Finding 2 ("vitest never ran"): vitest DID run and pass ~30s after daemon start; all 64 discovered vitest cases (file-level granularity, by provider design) are green with watermarks. The user's "557 never touched" = the not-yet-run set at that snapshot (448 Client.Tests + 45 UiTests + 64 vitest). The real defects are: no per-project rows in `tests status`, `covers_all` is project-scoped but reads as workspace-scoped, a zero-selection drain skip logs NOTHING, and the 0-byte `daemon.{out,err}.log` files hide that real diagnostics live in `.miller/logs/miller-<date>.log` (`role:ct`).
- Finding 3: confirmed root cause — the enabled-project SQL predicate only applies to `ct-project-status` rows, so provider cases of a disabled project keep counting (three SQL sites).
- Finding 5: the fresh-watermark mechanism has NEVER seeded (0 rows in both Tycho's and Miller's own ct.db) — the seed predicate requires `last_run_revision >= from`, which stops being true the moment the cursor outruns the last run. Separately, the revision cursor (`MAX(store_log.sequence)`) moves on non-content events (other views' imports, background `version_level_completed` rows), so build churn bumps it with zero indexed changes.

## Global Constraints

- Build: `dotnet build Miller.slnx -c Release`, 0 warnings / 0 errors (warnings are errors).
- JSON contracts are additive-only: nothing existing in `docs/contracts/tests-cli-v1.md` shapes may be renamed or removed. Advice lines are compact-only (ADR-0001); JSON carries facts.
- Tests that spawn julie-extract or a CT provider MUST be `[Trait("Category","Scale")]` and use `ScaleTestSupport`/`CtProviderTestSupport` (convention guards enforce this).
- No test comments; no narration comments (user CLAUDE.md).
- Status reads stay create-nothing: never create `ct.db`, `.miller/ct/`, or start the daemon.
- Docs and `CLAUDE.md` updates are batched in Task 13, not per task. `CLAUDE.md` edits require re-running `scripts/sync-agents.sh` (AGENTS.md is generated).
- Do NOT touch `TODO.md` in this worktree (the main checkout holds uncommitted user edits; the lead reconciles it at campaign end).
- julie-extract stays pinned at 2.37.0; Task 12's julie-extractors change does NOT get released or pin-bumped in this campaign.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section) + `scripts/test.sh`.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<TestClassName>"` for the classes the task touches. Task 12 (Rust): `cargo test -p julie-extractors <focused filter>` in `/home/murphy/source/julie-extractors`.

**Worker ceiling:** focused `--filter` runs only. Workers never run the fast suite, Scale suite, or whole `dotnet test`.

**Worker gate invariant:** each task's acceptance criteria name the behavior its focused tests must prove.

**Lead affected-change scope:** `scripts/test.sh` (fast suite) once per completed batch, run by the lead.

**Branch gate:** `scripts/test.sh all` once, before merge — this campaign touches CT provider and indexing paths, so Scale is mandatory. Never rerun a green suite on an unchanged tree.

**Security scope:** none declared.

**Replay/metric evidence:** none — all gates are hard test gates.

**Escalation triggers:** any change under `src/Miller.Testing/Providers/**` or `src/Miller.Indexing/**` ⟹ Scale suite at the branch gate (already mandated above).

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless their task explicitly says to update that gate (Tasks 3 and 10 update named pinning tests).

**Verification ledger:** the lead records command, scope, commit SHA, result per batch in the execution log.

## Parallel Execution Contract

Commit mode: **parallel-lead-commit** for every task (workers hand verified diffs to the lead; the lead reviews inline, stages, commits). Task 12 is the exception: it works in a separate repo on a new branch and commits there (**serial-worker-commit**).

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Disabled projects stop counting | Batch A | `src/Miller.Testing/Store/ContinuousTestStore.cs` (SQL predicates only) + `tests/Miller.Tests/Testing/Store/**` (new file) | No | None - safe parallel batch. |
| Task 5: Honest run line while discovering | Batch A | `src/Miller.Server/Tools/TestsCore.cs` (RenderStatusCompact only) + its render tests | No | None - safe parallel batch. |
| Task 9: Resolver `::` parse | Batch A | `src/Miller.Server/Resolution/SmartTargetResolver.cs`, `src/Miller.Server/Tools/InspectTool.cs` (FileSearchQuery), `tests/.../SmartTargetResolverTests.cs`, `tests/.../InspectMissingFileTests.cs` | No | None - safe parallel batch. |
| Task 11: MSBuild XML as config content | Batch A | `src/Miller.Indexing/ContentFileClassifier.cs`, `tests/Miller.Tests/Indexing/ContentFileClassifierTests.cs`, `tests/Miller.Tests/Indexing/ContentCorpusWriterTests.cs` | No | None - safe parallel batch. |
| Task 2: Watermark seed + no-op advances | Batch B | `src/Miller.Testing/Store/ContinuousTestStore.Coverage.cs`, `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs` + their tests | Yes (after Batch A) | Task 1 edits `ContinuousTestStore.cs`; keep store edits sequential across batches to avoid overlapping-diff churn. |
| Task 4: Drain visibility + log breadcrumb | Batch B | `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs` (zero-selection branch), `src/Miller.Testing/Daemon/CtDaemonLauncher.cs` or host startup, + their tests | No | None - safe parallel batch. |
| Task 6: run submit knows the active run | Batch B | `src/Miller.Server/Tools/TestsCore.cs` (Run path), `tests/.../TestsRunDaemonAckTests.cs` | Yes (after Task 5) | Same file as Task 5 (`TestsCore.cs`); lane is serial. |
| Task 3: Reds survive the automatic advance | Batch C | `src/Miller.Testing/Store/ContinuousTestStore.cs` (MarkContinuousTestsStale), `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs` (DemotePriorGreen only if needed) + tests | Yes (after Task 2) | Interacts with the watermark/backfill semantics Task 2 fixes; sequence after it. |
| Task 7: failures bounds, filter, grouping | Batch C | `src/Miller.Server/Tools/TestsCore.cs` (Failures + renderers), `src/Miller.Server/Tools/TestsTool.cs`, `src/Miller.Server/Tools/ToolContinuation.cs`, `src/Miller.Testing/Store/ContinuousTestStore.ProjectReads.cs`, `src/Miller.Server/Cli/CliDispatch.cs` (failures verb args) + tests | Yes (after Task 6) | Same file as Tasks 5/6 (`TestsCore.cs`); lane is serial. |
| Task 8: Per-project status rows | Batch D | `src/Miller.Server/Tools/TestsCore.cs` (Status + renderers) + tests | Yes (after Task 7) | Same file lane (`TestsCore.cs`, `ProjectReads.cs`). |
| Task 10: CT build output inside the workspace | Batch D | `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs`, `ContinuousTestDaemonQueue.cs` (ValidateBuildOutputRoot), `CtBuildCacheJanitor.cs`, `src/Miller.Testing/Providers/Shared/CtTempPaths.cs` (only if needed), named pinning tests | Yes (after Tasks 3, 4) | Shares `ContinuousTestDaemonQueue.cs` with Task 4 and store semantics with Task 3. |
| Task 12: julie-extractors xml spec | Batch A (independent repo) | `/home/murphy/source/julie-extractors/crates/julie-extractors/src/language_spec/specs.rs` + its tests, on a NEW branch in that repo | No | None - safe parallel batch (separate repository). |
| Task 13: Docs, contracts, CLAUDE.md | Batch E (last, solo) | `docs/contracts/tests-cli-v1.md`, `docs/continuous-testing.md`, `docs/install.md` (log pointer), `CLAUDE.md` + `AGENTS.md` via sync script | Yes | Documents every landed behavior; must run after all code tasks. |

---

### Task 1: Disabled projects stop counting (finding 3)

**Files:**
- Modify: `src/Miller.Testing/Store/ContinuousTestStore.cs` — the three enabled-predicate sites: `AggregateContinuousTestStatusesNoCursorSql` (~line 68), `AggregateContinuousTestStatusesSelectedSql` (~line 95), `ListContinuousTestStatuses` (~line 347)
- Test: new `tests/Miller.Tests/Testing/Store/DisabledProjectStatusFilterTests.cs`

**Interfaces:**
- Consumes: existing `ct_test_projects.enabled`, `LEFT JOIN` already present in all three queries.
- Produces: `ListContinuousTestStatuses` and both aggregate SQL constants exclude rows whose project row exists with `enabled = 0`, for ALL case sources.

**Contract inputs:** current predicate is `(tc.source <> 'ct-project-status' OR p.enabled IS NULL OR p.enabled = 1)`; replace with `(p.enabled IS NULL OR p.enabled = 1)`. `p.enabled IS NULL` must stay (cases with no project row are never dropped).

**File ownership:** `src/Miller.Testing/Store/ContinuousTestStore.cs` (SQL predicates only) + `tests/Miller.Tests/Testing/Store/**` (new file)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Disabling a project must remove its cases from `failures`, `stale_count`, `selected_count`, and the verdict, without deleting rows — re-enable restores them. Today only the synthetic `ct-project-status` row honors `enabled`.

**Approach:** Narrow SQL change in all three sites; no C# changes needed (`TestsCore.Failures` and the daemon verdict both read through these queries).

**Acceptance criteria:**
- [x] A red case whose project row is `enabled = 0` is absent from `ListContinuousTestStatuses` and both aggregates.
- [x] Re-enabling the project restores the same rows (no data loss).
- [x] A case with no matching project row still counts.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 2: Watermark seeding + content-no-op advances (finding 5)

**Files:**
- Modify: `src/Miller.Testing/Store/ContinuousTestStore.Coverage.cs` — `AdvanceContinuousTestFreshWatermark` seed predicate (~lines 915-920)
- Modify: `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs` — `PollAsync` (~lines 178-310)
- Test: extend the store coverage tests + poller tests in `tests/Miller.Tests/Testing/**` (locate by class name)

**Interfaces:**
- Consumes: `ApplyRevisionAdvance(ws, project, from, freshness, staleIds, outcome)`; `ContinuousTestDurableFreshness.IsFreshAt`.
- Produces: (a) a green row committed at ANY revision `<= from` on the same `index_identity` seeds a watermark on advance; red/skipped still never advance. (b) A revision bump whose resolved delta names zero changed indexed files advances watermarks for every project (KnownEmpty-style) instead of leaving rows to read stale.

**Contract inputs:** live evidence — `ct_case_fresh_watermarks` is empty on two real workspaces; the seed predicate `last_run_revision >= $from` is the reason. `docs/contracts/tests-cli-v1.md` line ~115 documents watermark-aware staleness; the shipped behavior must start matching that sentence.

**File ownership:** `ContinuousTestStore.Coverage.cs`, `ContinuousTestRevisionPoller.cs` + their tests

**Serialization required:** Yes (after Batch A)

**Dependency reason:** Task 1 edits `ContinuousTestStore.cs`; keep store edits sequential across batches.

**What to build:** Make the anti-spike watermark design actually operate, and stop content-free revision bumps (other views' imports, background level completions, build churn) from inflating `stale_count`.

**Approach:** Widen the seed predicate to `(s.index_identity = $identity AND s.status-is-green AND committed-revision <= $from) OR existing-watermark >= $from` — the exact SQL shape is the implementer's, but the rule is: same identity + green ⟹ seedable. For the poller: inspect the paths that move the saved cursor without calling `ApplyRevisionAdvance` (the `unavailable_delta` returns at ~lines 254-282); the invariant to establish is **the cursor never advances past a revision whose staleness consequences were not applied**. When the delta is readable and empty, apply an advance that carries watermarks forward for all projects. Do NOT touch `StoreLogCursor.MaxSequenceSql` — it is shared with sidecar stamps.

**Acceptance criteria:**
- [x] Green rows committed at an older revision on the same identity stay fresh (not stale) across a revision advance.
- [x] A revision bump with an empty delta leaves `stale_count` unchanged.
- [x] Red and skipped rows still never ride a watermark.
- [x] Identity change still fail-safes everything stale.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 3: Reds survive the automatic advance (finding 6)

**Files:**
- Modify: `src/Miller.Testing/Store/ContinuousTestStore.cs` — `MarkContinuousTestsStale` upsert (~lines 419-521)
- Modify (only if the selection math needs it): `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`
- Test: extend store tests + `tests/Miller.Tests/Testing/**` queue tests; the pinned contract test for the explicit-run `keepRed` behavior must stay green

**Interfaces:**
- Consumes: Task 2's watermark semantics.
- Produces: `MarkContinuousTestsStale` preserves `state = 'red'` (extending the existing running-run CASE) while still stamping `stale_since_revision` and deleting watermark rows. `tests failures` therefore keeps listing reds across automatic advances.

**Contract inputs:** `docs/contracts/tests-cli-v1.md` ~line 512: "Reds are added to what EXECUTES, never to what is marked stale." The campaign extends that sentence from explicit runs to every staling path. CLAUDE.md red-loop rule: an auto-run must NOT re-run every red on every debounce — only impacted reds re-run automatically.

**File ownership:** `ContinuousTestStore.cs` (MarkContinuousTestsStale), `ContinuousTestDaemonHost.cs` (DemotePriorGreen only if needed) + tests

**Serialization required:** Yes (after Task 2)

**Dependency reason:** Interacts with watermark/backfill semantics Task 2 fixes.

**What to build:** A red case stays `red` in `ct_test_states` until a run proves it green; staleness (needs-rerun) is tracked by the stale stamp/watermark loss, not by overwriting the state string. The `failures` total stops collapsing to "(1)" after an edit.

**Approach:** Extend the `state = CASE ... END` to keep `'red'` as well as running-state rows. Then verify what drives execution selection: if the stale execution set is derived from `state = 'stale'` anywhere (rather than freshness), include red-with-stale-stamp rows in the impacted/backfill selection so impacted reds still re-run. Prove the no-red-loop rule: a NON-impacted red must not enter the automatic execution set. `DemotePriorGreen` (identity change) flows through the same function — reds stay red there too, while still reading stale for execution; state the reasoning in the test names.

**Acceptance criteria:**
- [x] `failures` total is stable across an automatic revision advance that impacts red cases.
- [x] An impacted red still executes on the next drain; a non-impacted red does not.
- [x] The explicit-run `keepRed` pinned tests stay green unmodified.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 4: Drain visibility + daemon log breadcrumb (finding 2, diagnosability)

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs` — the zero-selection skip branch (~lines 370-374)
- Modify: the daemon startup path (`ContinuousTestDaemonHost.RunAsync` entry or the `ct-daemon` CLI verb) to print one stdout line
- Test: queue drain tests + a unit test on the breadcrumb line

**Interfaces:**
- Consumes: `CtDaemonLog.Write` (shared `.miller/logs` pair), the existing `ct drain skip ... reason=all_fresh_at_revision` log-line shape.
- Produces: (a) `ct drain skip workspace=<id> project=<path> reason=no_selection` on the empty-selection branch; (b) one stdout line at daemon start naming version, pid, and the diagnostics path `<root>/.miller/logs/miller-<yyyyMMdd>.log`, so `daemon.out.log` is never a mysterious 0 bytes.

**Contract inputs:** `daemon.{out,err}.log` capture raw stdout/stderr only (CtDaemonLauncher doc ~lines 366-369); real diagnostics go through `CtDaemonLog`. The breadcrumb must be a single line and must not violate the "record about a root the process is leaving" write rules — stdout is not a control-plane file, so it is safe.

**File ownership:** `ContinuousTestDaemonQueue.cs` (zero-selection branch), `CtDaemonLauncher.cs` or host startup, + tests

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The two one-liners that would have made the Tycho session self-diagnosing.

**Acceptance criteria:**
- [x] A drained project with zero selected cases produces a log line naming the project and reason.
- [x] Daemon stdout carries exactly one startup breadcrumb naming the shared log path.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 5: Honest run line while discovering (finding 10)

**Files:**
- Modify: `src/Miller.Server/Tools/TestsCore.cs` — `RenderStatusCompact` (~lines 1068-1110)
- Test: the existing status-render test class for compact output

**Interfaces:**
- Consumes: `TestsStatusResult.DaemonActivity`, `DaemonRun`.
- Produces: when `DaemonActivity == Executing` and `DaemonRun == null`, compact output prints `run: none selected yet (project discovery or between projects)`. JSON unchanged (`"run": null` stays).

**Contract inputs:** ADR-0001 — advice/explanation lines are compact-only. CLAUDE.md records executing-with-no-run as an accepted daemon gap; this task renders it honestly instead of omitting the block.

**File ownership:** `TestsCore.cs` (RenderStatusCompact only) + its render tests

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** One compact line closing the "header says executing, nothing else" confusion.

**Acceptance criteria:**
- [x] Compact status with `Executing` + null run prints the explanatory line; JSON is byte-identical to before.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 6: `run` submit knows the active run (finding 4)

**Files:**
- Modify: `src/Miller.Server/Tools/TestsCore.cs` — `Run` (~lines 761-817) and `WaitForDaemonToSettle` (~lines 1626-1701) if needed
- Test: `tests/Miller.Tests/Server/TestsRunDaemonAckTests.cs`

**Interfaces:**
- Consumes: `ContinuousTestDaemonHost.ReadStatus(root)` / `ReadLiveStatus` (already used by `Status` and the wait loop); `CtDaemonStatusRecord.Activity` / `Run`.
- Produces: on an unacked submit (ack missing after the 5s window) with daemon status `Executing`/`Queued`: `reason` becomes `"run already active"` (plus `run_id`/`project_path` when known) instead of bare "not acknowledged"; when `wait=true`, the call falls into `WaitForDaemonToSettle` and joins the in-flight work instead of returning exit 3 instantly.

**Contract inputs:** the daemon reads command files only between drains, so a mid-drain submit acks late by design; the command file still gets processed (the user observed the ack ~25s later). `docs/contracts/tests-cli-v1.md` ~lines 482-487 documents the current exit-3 contract — the new behavior is additive (a new reason string; wait now actually waits). Do not change `CtCommandChannel.DefaultAckTimeout`.

**File ownership:** `TestsCore.cs` (Run path), `TestsRunDaemonAckTests.cs`

**Serialization required:** Yes (after Task 5)

**Dependency reason:** Same file as Task 5 (`TestsCore.cs`); lane is serial.

**What to build:** A user who types `run wait=true` during an active run gets an honest join-and-wait, not `verdict=unknown unacked`.

**Approach:** Read daemon status when the ack misses. Busy (`Executing`/`Queued`) + `wait=false` → exit 0-or-3 per existing contract but with the honest reason. Busy + `wait=true` → enter the settle wait (the daemon will pick the command up next loop; the settle loop already tolerates runs it learns from snapshots). Not busy → keep today's unacked failure.

**Acceptance criteria:**
- [x] Unacked submit against a busy daemon reports `run already active`.
- [x] `wait=true` against a busy daemon waits for settle and returns the resulting verdict.
- [x] Unacked submit against a dead/idle daemon keeps today's behavior.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 7: `failures` output bounds, project filter, grouping (finding 7 + finding 1's classification ask)

**Files:**
- Modify: `src/Miller.Server/Tools/ToolContinuation.cs` — add `TestsMcpMaxBytes` to `ToolOutputBudget`
- Modify: `src/Miller.Server/Tools/TestsTool.cs` — bound MCP output; new `group` arg; wire existing `project` arg into failures
- Modify: `src/Miller.Server/Tools/TestsCore.cs` — `Failures`, `RenderFailuresJson`, `RenderFailuresCompact`
- Modify: `src/Miller.Testing/Store/ContinuousTestStore.ProjectReads.cs` — expose the per-project statuses read
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` — `tests failures` verb gains `--project`, `--group`
- Test: TestsCore/TestsTool failures tests + a byte-budget test

**Interfaces:**
- Consumes: `ToolOutputBudget` precedent (12 KiB caps, `TruncateUtf8`, render-prefix helpers); `ListContinuousTestStatusesForProject`; `TestsCoreRequest.ProjectPath` (already parsed).
- Produces: (a) MCP `tests` output is capped like every other tool (page or refuse per the Impact/Search precedent — implementer picks the closer fit and states why); (b) per-row `failure_summary` truncated to a bounded length in BOTH formats; (c) `project=<path>` filters failures; (d) `group=error_class` returns per-class counts + one sample row per class, derived by splitting `failure_summary` at the first `": "` when the prefix is a dotted type-name shape, `unclassified` otherwise; (e) an `infra_shaped: true` field on classes in a fixed set (`DirectoryNotFoundException`, `FileNotFoundException`, `DllNotFoundException`, browser/native-init markers) with a compact-only hint that these often differ between CT and a plain provider run.

**Contract inputs:** all JSON additions are additive. ADR-0001: the "verify under a plain run" advice is compact-only. `failure_text_hash` exists for exact-dupe grouping but there is no stored error-class column — derivation is string-based by design in this slice (a stored column is a future julie/provider slice, not this one).

**File ownership:** as listed above

**Serialization required:** Yes (after Task 6)

**Dependency reason:** Same `TestsCore.cs` lane.

**What to build:** `failures` answers "what is actually broken" within the MCP budget: bounded rows, a project filter, and error-class grouping that separates 87 infra-shaped `DirectoryNotFoundException` rows from real assertion failures.

**Acceptance criteria:**
- [x] `failures format=json limit=200` output stays under the tests byte cap on a 140-red fixture (page/refusal proves it).
- [x] `project=` returns only that project's reds.
- [x] `group=error_class` groups the Tycho-shaped fixture correctly and flags `DirectoryNotFoundException` as infra-shaped.
- [x] Existing failures JSON fields unchanged.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 8: Per-project status rows + covers_all scope honesty (finding 2, visibility)

**Files:**
- Modify: `src/Miller.Server/Tools/TestsCore.cs` — `TestsStatusProject` (~lines 80-94), `ToStatusProject`/`Status` population (~lines 308-359), compact render (~lines 1100-1106), JSON render, and the `covers_all` compact label (~line 1168)
- Test: TestsCore status tests

**Interfaces:**
- Consumes: `ListTestCasesForProject`, the per-project statuses read Task 7 exposed, `ContinuousTestStatusProjection.Project` over per-project rows.
- Produces: each project row gains `case_count`, `stale_count`, `red_count`, `verdict`, `last_run_at` (additive JSON; compact one-liner per project). The compact `covers_all=` line names the project it applies to; the JSON field name `covers_every_known_case` is unchanged.

**Contract inputs:** `selected_count` is workspace-scoped while `known`/`covers_all` are project-scoped — the mismatch that produced the false "covers_all=true while 557 untouched" reading. Status must stay create-nothing and cheap (N small ct.db queries for N projects is acceptable; no index hydration). `DashboardTestsPanel` is a projection — leave it untouched; additive fields must not break `DashboardTestsPanelTests`.

**File ownership:** `TestsCore.cs` (Status + renderers) + tests

**Serialization required:** Yes (after Task 7)

**Dependency reason:** Same `TestsCore.cs`/`ProjectReads.cs` lane.

**What to build:** `tests status` answers "which project is missing a run and why" at a glance — the question the whole Tycho session could not answer.

**Acceptance criteria:**
- [x] Status lists every enabled project with verdict, case/stale/red counts, and last run.
- [x] A never-run project reads `verdict=unknown, last_run=never` — visibly distinct from a green one.
- [x] Compact `covers_all` names its project; JSON shape unchanged.
- [x] `DashboardTestsPanelTests` stay green unmodified.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 9: Resolver `::` parse (finding 9)

**Files:**
- Modify: `src/Miller.Server/Resolution/SmartTargetResolver.cs` — `Resolve` (~lines 63-108), before the path rule
- Modify: `src/Miller.Server/Tools/InspectTool.cs` — `FileSearchQuery` (~line 482) strips a trailing `::segment`
- Test: `tests/Miller.Tests/Server/SmartTargetResolverTests.cs`, `tests/Miller.Tests/Server/InspectMissingFileTests.cs`

**Interfaces:**
- Consumes: `ResolveByName(name, scope)`, `HasKnownExtension`, existing `TryNormalizeColonQualifiedMember` fallback pattern.
- Produces: a target containing `::` whose left side (split at the LAST `::`) contains a path separator or a known file extension resolves as `ResolveByName(right, scope: left)`, falling through to today's behavior on NotFound. `inspect`, `trace`, `impact`, `edit`, and the CLI all gain the form for free (shared resolver).

**Contract inputs:** measured collision check — no indexed symbol name on this machine contains both `::` and a path separator (Rust paths and CSS pseudo-elements are slash-free), and the existing `::` id-shape tests are all slash-free, so a path-conditioned rule touches none of them.

**File ownership:** as listed above

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** `inspect target="src/Foo/Bar.cs::Method"` works instead of returning a misleading `file_not_indexed` with a useless retry hint.

**Acceptance criteria:**
- [x] `<file>::<symbol>` resolves to the symbol scoped to the file; `<file>::<a>::<b>` resolves via the qualified-member machinery (documented deviation: head-keyed parse instead of last-`::` split).
- [x] All existing `::` id-shape and CSS/Rust-name tests stay green.
- [x] The `file_not_indexed` fallback hint no longer embeds `::` in a file-search query.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 10: CT build output inside the workspace (finding 1)

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs` — `MaterializeProjectWorkItems` build-root computation (~lines 249-320)
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs` — `ValidateBuildOutputRoot` (~lines 1209-1224)
- Modify: `src/Miller.Testing/Daemon/CtBuildCacheJanitor.cs` — workspace/machine budget wiring
- Test: `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs` (the three pinned tests), `tests/Miller.Tests/Testing/Providers/Dotnet/DotnetTestProviderTests.cs` generation invariants (should stay green), a new WatchPathFilter case for `.miller/ct/build/**`

**Interfaces:**
- Consumes: `.miller/**` exclusion already enforced by `WatchPathFilter.SkipSegments`, `ScanIgnorePolicy.InvariantPatterns`, and `VendorScan.BaselinePatterns` (verified — no watcher/indexer work needed).
- Produces: default build root `<workspaceRoot>/.miller/ct/build/<proj12>` (the 12-hex project segment stays; the workspace segment is dropped — the root is per-workspace already). Generation scheme (`g<hex>`, `.allocated` markers, cache root) unchanged. `ValidateBuildOutputRoot` now requires the root INSIDE `<root>/.miller/`. Length fallback: when the composed workspace-local path would exceed the existing Windows tail budget (recompute the `BuildRootTailBudget` math from the workspace root's length), fall back to the legacy `<os-temp>/miller-ct/build/<ws12>/<proj12>` root for that project and log the choice — repo-root-relative tests are already broken for such a project either way, and MAX_PATH breakage is worse.
- The janitor: `EnforceWorkspace` runs against whichever root a project uses; `EnforceMachine` keeps guarding the temp root (fallback users + legacy dirs, which its 7-day inactivity LRU ages out naturally — no migration code).

**Contract inputs:** user direction (TODO.md finding 1): a project must NOT need Miller-specific settings to go green under CT; `MILLER_CT_WORKSPACE_ROOT`-aware helpers are not an acceptable answer. The `--artifacts-path` argument is the GENERATION root (one level below the build root) — that tested invariant stays. Windows MAX_PATH rationale is documented at `ContinuousTestProjectInventory.cs:287-313`; the fallback preserves it. The per-project serialization gate (`ProjectGate`) and generation-handoff are keyed on the build-root string — a root change changes those identities, which is safe because the daemon computes both from the same materialization.

**File ownership:** as listed above

**Serialization required:** Yes (after Tasks 3, 4)

**Dependency reason:** Shares `ContinuousTestDaemonQueue.cs` with Task 4; store/queue semantics settle first.

**What to build:** Tests that walk up from `TestContext.TestDirectory` to find the repo root pass under CT with zero project-side configuration, because the test binary now runs from inside the workspace tree.

**Acceptance criteria:**
- [x] Default materialized build root is `<workspace>/.miller/ct/build/<proj12>`; validation rejects anything in the workspace outside `.miller/` and accepts the temp-root fallback.
- [x] The Windows tail-budget math is recomputed from the workspace root; an over-budget workspace falls back to the temp root with the reason carried on the work item (no caller logs it yet — deferred).
- [x] `WatchPathFilter` skips `.miller/ct/build/**` (pinned by a test).
- [x] The three renamed/inverted pinning tests state the new invariant; dotnet provider generation-shape tests pass unmodified.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 11: MSBuild XML as config content — Miller side (finding 8)

**Files:**
- Modify: `src/Miller.Indexing/ContentFileClassifier.cs` — `ConfigExtensions` (~lines 17-21), `IsConfigLike` language list (~lines 66-69)
- Test: `tests/Miller.Tests/Indexing/ContentFileClassifierTests.cs`, `tests/Miller.Tests/Indexing/ContentCorpusWriterTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `.csproj .props .targets .vbproj .fsproj .slnx .nuspec .resx` classify as `workspace_config`; language `xml` counts as config-like.

**Contract inputs:** this is inert until julie-extract manifests those files (Task 12 + a future pin bump) — files absent from the artifact never reach the corpus. Landing it now means the pin bump alone completes the feature. No size/binary guard changes (1 MiB cap and UTF-8 decode already apply).

**File ownership:** as listed above

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**Acceptance criteria:**
- [x] Classifier tests pin the new extensions and the `xml` language as config.
- [x] `IsDocsLike_SourceFiles_False` and existing pins stay green.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 12: julie-extractors xml spec extensions (finding 8, upstream)

**Files (repo `/home/murphy/source/julie-extractors`, NEW branch `feat/msbuild-xml-extensions`):**
- Modify: `crates/julie-extractors/src/language_spec/specs.rs` — the `xml` spec extension list (~lines 309-311)
- Test: that repo's language-spec/detection tests

**Interfaces:**
- Consumes: `detect_language_from_extension`.
- Produces: `xml` spec claims `csproj, props, targets, vbproj, fsproj, slnx, nuspec, resx` (`.sln` stays out — not XML). MSBuild files enter the manifest as `xml` on the next release.

**Contract inputs:** verify that repo's branch/dirty state before starting; follow its own AGENTS/CLAUDE instructions; run its focused `cargo test`. Commit on the new branch; do NOT release, tag, or bump Miller's pin — that is a later, user-approved step.

**File ownership:** the julie-extractors files above, on a new branch in that repo

**Serialization required:** No

**Dependency reason:** None - safe parallel batch (separate repository).

**Acceptance criteria:**
- [x] Extension detection tests cover the new extensions; focused cargo tests pass.
- [x] Committed on the new branch with that repo's state reported (path, branch, commit, dirty state); nothing released or pinned.

### Task 13: Docs, contracts, CLAUDE.md (last)

**Files:**
- Modify: `docs/contracts/tests-cli-v1.md` — disable-retires-cases semantics; failures `project`/`group`/truncation/byte-cap; per-project status fields; the `run already active` reason + wait-join; the red-preservation rule extended to every staling path; the watermark sentence now true
- Modify: `docs/continuous-testing.md` — build-output location (workspace-local default, temp fallback), where the daemon logs (`.miller/logs/miller-<date>.log`, `role:ct`; `daemon.{out,err}.log` is raw stdio only), per-project status reading
- Modify: `docs/install.md` — only if it references CT log locations
- Modify: `CLAUDE.md` — the CT sidecar bullets that changed (build root, red preservation, watermark seed, disable semantics); then `scripts/sync-agents.sh` and `cmp -s CLAUDE.md AGENTS.md`

**Interfaces:** Consumes every landed behavior; produces the documentation matching it.

**Contract inputs:** additive contract language only; historical design notes stay historical.

**File ownership:** docs + CLAUDE.md/AGENTS.md

**Serialization required:** Yes

**Dependency reason:** Documents all prior tasks; runs last, solo.

**Acceptance criteria:**
- [ ] Every behavior change above is documented in the contract or operating doc that owns it.
- [ ] `cmp -s CLAUDE.md AGENTS.md` passes.
- [ ] Worker-scope verification passes (`AgentInstructionsTests` and doc-guard tests) and the diff is handed to the lead.

---

## Out of scope (recorded, not built)

- Repo-defined expensive build hooks (Tycho's `npm ci` on every test build): needs a general policy decision; the workspace-local build root removes the `MILLER_CT_WORKSPACE_ROOT` path contract but not hook cost. Backlog + user decision.
- Releasing julie-extract with Task 12's change and bumping Miller's pin (user approval boundary).
- Per-test (not per-file) vitest case granularity — provider design question, needs its own slice.
- A stored error-class column in ct.db (string derivation ships first; a column is a follow-up if grouping proves out).
- Daemon-side mid-drain command acking (the client-side join in Task 6 closes the user-facing gap).
