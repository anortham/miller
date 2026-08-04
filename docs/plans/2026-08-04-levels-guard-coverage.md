# Progressive-Levels Guard Coverage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make every Miller read surface honest against a symbols-level artifact, so no tool ever returns a confident-sounding empty answer for a layer that was never extracted — and fix the telemetry misclassification and staging-reaper gap found alongside it.

**Architecture:** v1.16.0 bolted level-awareness onto individual MCP wrapper methods using two inconsistent detection styles: reading the artifact file (`ExtractIndexLevelReader.Read(dbPath)`) and type-checking the loaded index (`context.Index is MillerRepositoryIndex`). The second style silently fails on cross-workspace reads, and the CLI verbs call the inner tool cores directly and skip both. The fix is to make artifact level a first-class property of every read context, derived from the artifact path so it never depends on which index implementation got selected, and to move the degraded decision into the shared cores that MCP, CLI, report, export, and history all consume.

**Tech Stack:** .NET 10, C#, xUnit, SQLite (Microsoft.Data.Sqlite), ModelContextProtocol server SDK.

**Architecture Quality:** Approved shape — artifact level is *context metadata read from the artifact path*, never inferred from the loaded index type, and the degraded diagnostic is produced by the shared tool core rather than by an MCP wrapper. Main architecture risk: `WorkspaceIndexProvider` returns two different context shapes (`ResolveRegistered` → `CachedIndex.Index`, `ResolveRegisteredSymbolRead` → lightweight FTS `ISymbolLookupIndex`); Task 1 must add the level to *both* without changing which index implementation serves a read. If code reality contradicts this shape, report a plan mismatch rather than redesigning locally.

## Global Constraints

- **The MCP surface stays at nine tools.** No new MCP tool may be added (CLAUDE.md MCP-stinginess rule). All fixes are diagnostics, classification, or CLI parity on existing surfaces.
- **`MILLER_INDEX_LEVELS=full` remains a permanent zero-behavior-change escape hatch.** Against a full-level artifact every guard added here must be inert and output must stay byte-identical to pre-change Miller. Every task needs a full-level negative test proving the diagnostic does *not* attach.
- **Build must be 0 warnings / 0 errors.** `TreatWarningsAsErrors` is on in `Directory.Build.props`; analyzer warnings are build errors.
- **`Miller.Core` has ZERO I/O dependencies.** Do not put artifact-reading code there. Level reading belongs in `Miller.Indexing` / `Miller.Server`.
- **Test split is load-bearing.** Any test that spawns `julie-extract` MUST be `[Trait("Category","Scale")]` at class level and MUST obtain the binary via `ScaleTestSupport.RequireJulieServer()`. Fast suite target <10s; a wall-clock tripwire fails past 30s.
- **The canonical symbols-level metadata value is `symbols`** (`IndexLevels.SymbolsMetadataValue`); absent/unknown reads report `full` (`IndexLevels.FullMetadataValue`) and must keep failing closed to full.
- **Existing diagnostic vocabulary is reused, not extended ad hoc.** `reference_layer_converging` is the established code; new codes in this plan are limited to `facts_layer_converging` and `regions_layer_converging`, which already appear as `MarkDegraded` reasons in `PatternsTool` / `SearchTool`.
- **CLAUDE.md/AGENTS.md are a byte-for-byte mirror.** If either is edited, run `scripts/sync-agents.sh` and confirm `cmp -s CLAUDE.md AGENTS.md`.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` ("Testing — read this before running tests" and "Build" sections).

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~<TestClassName>"` for the class the task adds or changes.

**Worker ceiling:** `scripts/test.sh` (the fast suite, `Category!=Scale`). Workers do NOT run the Scale suite or the Release build gate; those are lead-owned.

**Worker gate invariant:** For each task, the assigned worker gate must prove BOTH directions — the diagnostic attaches against a symbols-level artifact, AND it does not attach against a full-level artifact (the `MILLER_INDEX_LEVELS=full` byte-identical guarantee).

**Lead affected-change scope:** `scripts/test.sh` after each coherent batch, plus `dotnet build Miller.slnx -c Release` (0 warnings required).

**Branch gate:** `scripts/test.sh all` before handoff/PR. Requires `.tools/julie-extract` restored; Scale tests skip (not fail) if it is missing — a skip is NOT a pass for Task 1's scale coverage.

**Replay/metric evidence:** Task 3 changes recorded metric history. The hard gate is "no facts-derived metric row is appended under a symbols-level artifact"; the shape of existing recorded history is report-only.

**Escalation triggers:** Any change to `WorkspaceIndexProvider` context shapes, `FullRebuildPromotion`, or `JulieExtractRunner` argv requires the Scale suite (`scripts/test.sh scale`) before the branch gate, because those paths are only exercised against the real extractor.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. If the same HEAD already has a passing ledger entry for the required scope, reuse it instead of rerunning.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Level-aware read context + symbols-level fixture | None - serial (foundation) | Modify `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`, `src/Miller.Server/Tools/IndexLevelGuard.cs`; Create `tests/Miller.Tests/Support/SymbolsLevelArtifact.cs`; Create `tests/Miller.Tests/Indexing/IndexLevelContextTests.cs` | Yes | Tasks 2, 4, 5 consume the context-carried level and the shared fixture helper; they cannot be written against a signal that does not exist yet. |
| Task 2: markers guarded on MCP and CLI | Lane L1 | Modify `src/Miller.Server/Tools/SearchTool.cs`, `src/Miller.Server/Cli/CliDispatch.cs`; Create `tests/Miller.Tests/Server/MarkerLevelGuardTests.cs` | Yes | Depends on Task 1's context level, and shares `CliDispatch.cs` with Tasks 4 and 5. |
| Task 3: report and metric history stop recording false zeros | Batch B | Modify `src/Miller.Server/Tools/ReportTool.cs`, `src/Miller.Indexing/MetricSnapshotAggregates.cs`; Create `tests/Miller.Tests/Indexing/MetricSnapshotLevelTests.cs` | No | None - safe parallel batch. Reads the artifact path directly; shares no file with Lane L1 or Batch A. |
| Task 4: inspect and context reference guards | Lane L1 | Modify `src/Miller.Server/Tools/InspectTool.cs`, `src/Miller.Server/Tools/ContextTool.cs`, `src/Miller.Server/Cli/CliDispatch.cs`; Create `tests/Miller.Tests/Server/InspectContextLevelGuardTests.cs` | Yes | Depends on Task 1; shares `CliDispatch.cs` with Tasks 2 and 5 and `InspectTool.cs` with Task 8. |
| Task 5: patterns and region-search CLI parity | Lane L1 | Modify `src/Miller.Server/Tools/PatternsTool.cs`, `src/Miller.Server/Cli/CliDispatch.cs`; Create `tests/Miller.Tests/Server/PatternsRegionCliGuardTests.cs` | Yes | Shares `CliDispatch.cs` with Tasks 2 and 4. |
| Task 6: staging reaper independent of a successful build | Batch A | Modify `src/Miller.Indexing/SidecarStagingReaper.cs`, `src/Miller.Server/Tools/WorkspaceTool.cs`; Modify `tests/Miller.Tests/Indexing/SidecarStagingReaperTests.cs` | No | None - safe parallel batch. No dependency on level context; shares no file with other tasks. |
| Task 7: validation errors stop reporting as internal_failure | Batch A | Modify `src/Miller.Server/Tools/ToolDiagnostic.cs`, `src/Miller.Server/Tools/ToolDiagnosticRenderer.cs`; Modify `tests/Miller.Tests/Server/ToolDiagnosticTests.cs` | No | None - safe parallel batch. No dependency on level context; shares no file with other tasks. |
| Task 8: nonexistent path distinguished from symbol-empty file | Lane L1 | Modify `src/Miller.Server/Tools/InspectTool.cs`, `src/Miller.Server/Resolution/SmartTargetResolver.cs`; Create `tests/Miller.Tests/Server/InspectMissingFileTests.cs` | Yes | Shares `InspectTool.cs` with Task 4. |

**Dispatch order:** Batch A (Tasks 6, 7) may start immediately, in parallel with Task 1. Task 1 must complete before Lane L1 begins. Batch B (Task 3) may start once Task 1 lands. Lane L1 runs strictly serially: Task 2 → Task 4 → Task 5 → Task 8.

**Commit mode:** `parallel-lead-commit` for Batch A and Batch B; `serial-worker-commit` for Lane L1.

---

### Task 1: Level-aware read context + symbols-level fixture

**Files:**
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs` (`ResolveRegistered` ~:254, `ResolveRegisteredSymbolRead` :297, current-workspace path ~:224)
- Modify: `src/Miller.Server/Tools/IndexLevelGuard.cs:21` (`ReferenceLayerConverging`)
- Create: `tests/Miller.Tests/Support/SymbolsLevelArtifact.cs`
- Test: `tests/Miller.Tests/Indexing/IndexLevelContextTests.cs`

**Interfaces:**
- Consumes: `IndexLevels.SymbolsMetadataValue`, `IndexLevels.FullMetadataValue`, `ExtractIndexLevelReader.Read(string? dbPath)` from `src/Miller.Indexing/IndexLevels.cs`.
- Produces: (1) an `IndexLevel` string property carried on every workspace read context returned by `WorkspaceIndexProvider` — both the full `MillerRepositoryIndex` context and the lightweight symbol-read context — populated from `ExtractIndexLevelReader.Read(context.IndexDbPath)`; (2) `IndexLevelGuard.ReferenceLayerConverging` overload taking the context-carried level string rather than a `MillerRepositoryIndex`; (3) test helper `SymbolsLevelArtifact.Create(string dir)` returning a path to a synthetic schema-5 SQLite artifact with `artifact_metadata.index_level='symbols'`, populated `symbols`/`files`/`relationships`, and EMPTY `identifiers`/`identifier_resolutions`/`source_regions`/`structural_facts`, plus `SymbolsLevelArtifact.CreateFull(string dir)` for the full-level negative case.

**Contract inputs:** Artifact table names verified against a real symbols-level artifact: `symbols`, `files`, `relationships`, `identifiers`, `identifier_resolutions`, `source_regions`, `structural_facts`, `reference_sites`, `complexity_metrics`, `type_facts`, `artifact_metadata`. At symbols level `identifiers`, `identifier_resolutions`, `source_regions` and `structural_facts` are 0 while `reference_sites` (20,181), `complexity_metrics` (3,501) and `type_facts` (2,275) ARE populated — the fixture must reproduce that shape, not zero everything.

**File ownership:** Modify `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`, `src/Miller.Server/Tools/IndexLevelGuard.cs`; Create `tests/Miller.Tests/Support/SymbolsLevelArtifact.cs`; Create `tests/Miller.Tests/Indexing/IndexLevelContextTests.cs`

**Serialization required:** Yes

**Dependency reason:** Tasks 2, 4, 5 consume the context-carried level and the shared fixture helper; they cannot be written against a signal that does not exist yet.

**What to build:** Every workspace read context must carry the artifact's index level, read from the artifact path. Today `IndexLevelGuard.ReferenceLayerConverging(index)` reads `index.IndexLevel` off a `MillerRepositoryIndex`; on a cross-workspace read `WorkspaceIndexProvider.ResolveRegisteredSymbolRead` (:297) hands back a lightweight FTS `ISymbolLookupIndex` instead, so the `is MillerRepositoryIndex` test in `InspectTool.cs:107` fails and the guard silently never fires. Deriving the level from `context.IndexDbPath` removes the dependency on which index implementation was selected.

**Approach:** Add the level to the context records rather than to the index objects — availability of a *layer* is a property of the artifact, not of the search implementation. Keep `ExtractIndexLevelReader`'s existing tolerance (absent key/table/file ⇒ `full`) so a broken artifact degrades to "no levels behavior". Keep the old `ReferenceLayerConverging(MillerRepositoryIndex)` overload delegating to the new string overload so Tasks 2/4/5 can migrate call sites without a flag day. Read the level once per context construction, not per guard call — these are per-request hot paths. The fixture helper goes in `tests/Miller.Tests/Support/` (namespace `Miller.Tests.Support`, alongside `StubJulieExtract`/`FakeSemanticSidecar`); it must NOT spawn `julie-extract` (it writes SQLite directly), so it stays in the fast suite with no Scale trait.

**Acceptance criteria:**
- [x] Every read context returned by `WorkspaceIndexProvider` exposes the artifact's index level, populated from the artifact path — *5 of the 7 context types, at 10 construction sites. `WorkspaceContentSearchContext` and `WorkspaceTextContentSearchContext` are deliberately excluded and the reason is recorded at `WorkspaceIndexProvider.cs:351`: they serve `content.db`, which the levels split does not touch, so no content read can report an unextracted layer as an empty repository. First delivery covered only 2 of 7, which would have forced Tasks 2 and 5 into inline artifact reads while Task 4 used the carried level — the same two-detection-style split this plan removes. Corrected in fix round 1.*
- [x] `IndexLevelGuard` exposes an overload that decides from the carried level string, with the `MillerRepositoryIndex` overload delegating to it
- [x] A cross-workspace read against a symbols-level artifact reports converging, proven by a test that goes through `ResolveRegisteredSymbolRead`
- [x] `SymbolsLevelArtifact.Create` / `.CreateFull` produce artifacts matching the verified table shape above
- [x] A full-level artifact reports NOT converging through both context paths
- [x] Worker-scope verification passes and the change is committed per commit mode

---

### Task 2: markers guarded on MCP and CLI

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs:362` (markers branch), `src/Miller.Server/Tools/SearchTool.cs:973` (`EmptyReasonFor`)
- Modify: `src/Miller.Server/Cli/CliDispatch.cs:414` (`search --mode markers`), `src/Miller.Server/Cli/CliDispatch.cs:195` (`todos` verb)
- Test: `tests/Miller.Tests/Server/MarkerLevelGuardTests.cs`

**Interfaces:**
- Consumes: Task 1's context-carried index level and `SymbolsLevelArtifact` fixture helper.
- Produces: a `facts_layer_converging` diagnostic on both the MCP `search mode=markers` route and the CLI `search --mode markers` / `todos` verbs whenever the served artifact is symbols level.

**Contract inputs:** Marker facts are read from `structural_facts` rows with `pattern_id = 'code.marker.v1'` via `src/Miller.Indexing/MarkerFactReader.cs:22-36` — NOT from `source_regions`. `structural_facts` is empty at symbols level, so markers can never match. The existing zero-hit path returns `no_todo_markers` from `SearchTool.EmptyReasonFor` (:973).

**File ownership:** Modify `src/Miller.Server/Tools/SearchTool.cs`, `src/Miller.Server/Cli/CliDispatch.cs`; Create `tests/Miller.Tests/Server/MarkerLevelGuardTests.cs`

**Serialization required:** Yes

**Dependency reason:** Depends on Task 1's context level, and shares `CliDispatch.cs` with Tasks 4 and 5.

**What to build:** This is the highest-impact fix in the plan. At symbols level `miller todos` and MCP `search mode=markers` both answer "No TODO/FIXME/HACK/XXX markers." with `diagnostic_code=no_todo_markers` and `diagnostic_class=expected_empty` — a confident, definitive negative handed to an agent about a layer that was never extracted. Replace that with the converging diagnostic so the answer reads as "not yet known" rather than "none exist".

**Approach:** The level check must win over the zero-hit reason: when the artifact is symbols level, emit `facts_layer_converging` instead of `no_todo_markers`, mirroring how `SearchTool.cs:585` already gives the converging diagnostic priority on the regions route. Stamp the demand counter with `IndexLevelGuard.MarkDegraded(telemetry, "facts_layer_converging")` so the levels program keeps its measurement of how often agents hit converging layers. Put the decision where BOTH surfaces reach it — the CLI markers branch at `CliDispatch.cs:414` and the `todos` verb at :195 currently return the `MarkerSearch` output directly, so either route it through a shared guarded helper or apply the same check at both call sites. Reuse `IndexLevelGuard.Converging(...)` with wording naming the facts layer; do not invent a new diagnostic class.

**Acceptance criteria:**
- [ ] MCP `search mode=markers` against a symbols-level artifact returns `facts_layer_converging`, not `no_todo_markers`
- [ ] CLI `search --mode markers` and `todos` both emit the same diagnostic against a symbols-level artifact
- [ ] Telemetry records `degraded=true` / `degraded_reason=facts_layer_converging` for those calls
- [ ] Against a full-level artifact, all three surfaces still return `no_todo_markers` on a genuine zero-hit and output is unchanged
- [ ] Worker-scope verification passes and the change is committed per commit mode

---

### Task 3: report and metric history stop recording false zeros

**Files:**
- Modify: `src/Miller.Server/Tools/ReportTool.cs:120` (`ReadMarkerSection`), and the marker/facts availability rendering around `ReportTool.cs:140`
- Modify: `src/Miller.Indexing/MetricSnapshotAggregates.cs:229` (`AddMarkerCounts`), snapshot append path ~`MetricSnapshotAggregates.cs:82`
- Test: `tests/Miller.Tests/Indexing/MetricSnapshotLevelTests.cs`

**Interfaces:**
- Consumes: `ExtractIndexLevelReader.Read(dbPath)` and `IndexLevels.SymbolsMetadataValue` directly from the artifact path (this task does NOT depend on Task 1's context, so it can run in parallel).
- Produces: a report that marks facts-derived sections as unavailable-pending-upgrade rather than zero, and a metric-history writer that omits facts-derived counters entirely when the artifact is symbols level.

**Contract inputs:** `structural_facts` is empty at symbols level, so every facts-derived count is 0. `complexity_metrics` (3,501) IS populated at symbols level and must keep being reported — do NOT suppress complexity, clones, churn, or risk. Only facts-derived counters (markers and pattern counts) are affected.

**File ownership:** Modify `src/Miller.Server/Tools/ReportTool.cs`, `src/Miller.Indexing/MetricSnapshotAggregates.cs`; Create `tests/Miller.Tests/Indexing/MetricSnapshotLevelTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch. Reads the artifact path directly; shares no file with Lane L1 or Batch A.

**What to build:** `miller report` currently marks the marker layer as available and prints zero counts against a symbols-level artifact. Worse, `MetricSnapshotAggregates.AddMarkerCounts` records `marker_total=0` into the append-only `history.db` under the symbols artifact's identity — so the trend permanently contains a fabricated zero, and the later full-level upgrade shows a false zero-to-real spike in the dashboard sparklines. Unlike every other bug in this plan, this one *outlives* the upgrade: bad rows stay in history forever.

**Approach:** In the history writer, SKIP appending facts-derived metric rows when the artifact level is symbols — omit the row rather than writing 0, because a gap in a sparkline is honest and a zero is a lie. In `ReportTool`, render facts-derived sections as unavailable-pending-upgrade with the same next-step actions the converging diagnostic uses. Be surgical about which counters are facts-derived: complexity/clones/churn/risk read tables that ARE populated at symbols level and must be unaffected. Do not attempt to retro-clean existing `history.db` rows in this task — that is a separate migration decision; note it in the PR instead.

**Acceptance criteria:**
- [ ] No facts-derived metric row is appended to `history.db` when the artifact is symbols level (hard gate)
- [ ] Complexity/clones/churn/risk metrics are still recorded normally at symbols level
- [ ] `miller report` shows facts-derived sections as unavailable-pending-upgrade, not as zero counts
- [ ] Against a full-level artifact, report output and recorded history are byte-identical to pre-change
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode

---

### Task 4: inspect and context reference guards

**Files:**
- Modify: `src/Miller.Server/Tools/InspectTool.cs:104-112` (migrate guard to the context-carried level)
- Modify: `src/Miller.Server/Tools/ContextTool.cs:151` (reference enrichment)
- Modify: `src/Miller.Server/Cli/CliDispatch.cs:1993` (`inspect --depth overview|full`), `src/Miller.Server/Cli/CliDispatch.cs:2060` (`context --reference-mode usage`)
- Test: `tests/Miller.Tests/Server/InspectContextLevelGuardTests.cs`

**Interfaces:**
- Consumes: Task 1's context-carried index level, the new `IndexLevelGuard` string overload, and `SymbolsLevelArtifact`.
- Produces: `reference_layer_converging` on MCP `inspect` depth=overview|full (current AND cross-workspace), MCP `context` reference enrichment, and the equivalent CLI verbs.

**Contract inputs:** `InspectTool.cs:104-112`'s condition is `parsedDepth is not Summary && diagnostic is null && context.Index is MillerRepositoryIndex && ReferenceLayerConverging(...)`. There is no earlier return and no `count` condition — the `is MillerRepositoryIndex` runtime type check is the sole reason cross-workspace inspect misses the guard. `depth=summary` is complete at symbols level and must stay unguarded.

**File ownership:** Modify `src/Miller.Server/Tools/InspectTool.cs`, `src/Miller.Server/Tools/ContextTool.cs`, `src/Miller.Server/Cli/CliDispatch.cs`; Create `tests/Miller.Tests/Server/InspectContextLevelGuardTests.cs`

**Serialization required:** Yes

**Dependency reason:** Depends on Task 1; shares `CliDispatch.cs` with Tasks 2 and 5 and `InspectTool.cs` with Task 8.

**What to build:** Two related reference-layer omissions. First, `inspect depth=overview|full` renders a definition with silently-absent refs/callers/callees sections on any cross-workspace read — an agent reads that as "nothing calls this symbol". Second, `context` reads reference evidence with no level check at all on either surface; note that even `reference_mode=off` reads outgoing evidence, so `context` can quietly lose usage enrichment at symbols level.

**Approach:** Replace the `is MillerRepositoryIndex` type test with the context-carried level from Task 1 — that single change fixes cross-workspace inspect. Keep `depth=summary` exempt (it is complete at symbols level; guarding it would be noise). For `ContextTool`, attach the converging diagnostic whenever reference evidence contributed to — or was silently omitted from — the bundle, including the `reference_mode=off` outgoing-evidence path. The CLI verbs call the tool cores directly, so route them through the same guarded path rather than duplicating a third detection style.

**Acceptance criteria:**
- [ ] MCP `inspect depth=overview` and `depth=full` emit `reference_layer_converging` on BOTH current-workspace and cross-workspace reads against a symbols-level artifact
- [ ] `inspect depth=summary` is unaffected at any level
- [ ] MCP `context` emits the diagnostic at symbols level, including `reference_mode=off`
- [ ] CLI `inspect --depth overview|full` and `context --reference-mode usage` emit it too
- [ ] Against a full-level artifact none of these attach and output is byte-identical
- [ ] Worker-scope verification passes and the change is committed per commit mode

---

### Task 5: patterns and region-search CLI parity

**Files:**
- Modify: `src/Miller.Server/Tools/PatternsTool.cs:113-135` (move the guard from the MCP wrapper into the shared core)
- Modify: `src/Miller.Server/Cli/CliDispatch.cs:1184` (`patterns list|summary|search`), `src/Miller.Server/Cli/CliDispatch.cs:109` (`patterns export`), `src/Miller.Server/Cli/CliDispatch.cs:432` (`SearchRouteKind.Regions` branch)
- Test: `tests/Miller.Tests/Server/PatternsRegionCliGuardTests.cs`

**Interfaces:**
- Consumes: Task 1's context-carried level and `SymbolsLevelArtifact`.
- Produces: `facts_layer_converging` on the CLI patterns verbs (including `patterns export`) and `regions_layer_converging` on CLI region search, matching what MCP already emits.

**Contract inputs:** The guard currently lives inside `PatternsTool.Patterns` (the `[McpServerTool]` method) at `PatternsTool.cs:113` and `:130`; the CLI calls `PatternsTool.Run` at `CliDispatch.cs:1184` and skips it. The CLI region path opens the empty region sidecar directly at `CliDispatch.cs:432`. Correction to an earlier hypothesis: `RunNormalSymbolRoute` and `RunNormalContentRoute` are NOT affected — symbol and content routes stay usable at symbols level, and only the `SearchRouteKind.Regions` branch is missing the guard.

**File ownership:** Modify `src/Miller.Server/Tools/PatternsTool.cs`, `src/Miller.Server/Cli/CliDispatch.cs`; Create `tests/Miller.Tests/Server/PatternsRegionCliGuardTests.cs`

**Serialization required:** Yes

**Dependency reason:** Shares `CliDispatch.cs` with Tasks 2 and 4.

**What to build:** Close the CLI/MCP divergence on the two surfaces where MCP is already correct. This matters beyond ergonomics because the CLI is the documented Eros-facing contract surface (`docs/contracts/cli-eros-v1.md`) — a fleet consumer reading `patterns export` at symbols level currently gets a clean, empty, authoritative-looking feed.

**Approach:** Move the level decision out of the MCP wrapper and into `PatternsTool.Run` (or a shared helper both entry points call) so there is one implementation and the CLI cannot drift again. `patterns export` is a JSONL feed — emit the degradation as a machine-readable field or a documented stderr warning rather than injecting a prose line into the JSONL stream, and record whichever choice is made in `docs/contracts/cli-eros-v1.md`. Note that `patterns export` bypasses the wrapper independently at `CliDispatch.cs:109`, so it needs its own call site fixed even after `Run` is guarded if it does not route through `Run`.

**Acceptance criteria:**
- [ ] CLI `patterns list`, `summary`, and `search` emit the converging diagnostic at symbols level
- [ ] CLI `patterns export` signals degradation without corrupting the JSONL stream, and the contract doc records how
- [ ] CLI `search --regions` emits the converging diagnostic at symbols level
- [ ] The guard has ONE implementation shared by MCP and CLI (no duplicated level check)
- [ ] Against a full-level artifact all CLI output is byte-identical to pre-change
- [ ] Worker-scope verification passes and the change is committed per commit mode

---

### Task 6: staging reaper independent of a successful build

**Files:**
- Modify: `src/Miller.Indexing/SidecarStagingReaper.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs` (add a reap call to a lifecycle path that runs without a sidecar build)
- Test: `tests/Miller.Tests/Indexing/SidecarStagingReaperTests.cs`

**Interfaces:**
- Consumes: existing `SidecarStagingReaper.ReapStale(directory, prefix, staleAge, exceptPath)` and `SidecarStagingReaper.DefaultStaleAge` (15 minutes).
- Produces: a reap that also runs on a workspace lifecycle path (status/health/open) so orphans are reclaimed without requiring a sidecar build to start.

**Contract inputs:** The only production callers today are `SearchIndexWriter.Write` (~:116) and `ContentCorpusWriter` (~:26) — both at the START of a new sidecar build. Staging names carry a GUID so a crashed build's file is never overwritten. `ReapStale` deletes only files not written for at least 15 minutes and shields `exceptPath`; a live build keeps its staging file's write time fresh, so age alone cannot race a sibling build.

**File ownership:** Modify `src/Miller.Indexing/SidecarStagingReaper.cs`, `src/Miller.Server/Tools/WorkspaceTool.cs`; Modify `tests/Miller.Tests/Indexing/SidecarStagingReaperTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch. No dependency on level context; shares no file with other tasks.

**What to build:** Because reaping only happens when a sidecar build starts, a workspace whose scans keep failing never reclaims its own orphans. Real instance: 1.18 GB of `.search-build-*.db` in `~/.hermes/hermes-agent/.miller`, whose registry row is stuck in a scan error (`julie-extract reported a usage/argv error (exit 2)`). Note the precise scope — the earlier "builds keep failing" framing was too broad: any later build that *reaches* `SearchIndexWriter.Write` reaps older files first, even if that build then fails. The genuine leak is a workspace where nothing reaches the sidecar writer at all.

**Approach:** Add a reap on a lifecycle path that runs even when scanning is broken — `workspace status`/`health`/`open` are the natural hooks since they already touch the workspace directory. Keep it strictly best-effort and non-throwing: this must never turn a status call into an error. Preserve the 15-minute stale age and the `exceptPath` shield; do not lower the age to make a test easier — inject the age instead. Do not add a background timer; a lifecycle hook is sufficient and keeps the CLI's no-background-service rule intact.

**Acceptance criteria:**
- [ ] Orphaned `.search-build-*.db` and `.content-build-*.db` older than the stale age are reaped by a workspace lifecycle call with no sidecar build
- [ ] A workspace stuck in a scan-error state still reaps
- [ ] Files newer than the stale age, and any live build's staging file, are never deleted
- [ ] A reap failure (permissions, held handle) never fails the lifecycle call
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode

---

### Task 7: validation errors stop reporting as internal_failure

**Files:**
- Modify: `src/Miller.Server/Tools/ToolDiagnostic.cs:82,124` (`FromException` classification)
- Modify: `src/Miller.Server/Tools/ToolDiagnosticRenderer.cs:59` (`SetErrorCategory` overwrite)
- Test: `tests/Miller.Tests/Server/ToolDiagnosticTests.cs`

**Interfaces:**
- Consumes: `TelemetryScope.ClassifyError` (`src/Miller.Server/Telemetry/TelemetryScope.cs:269`), which ALREADY maps the selector exception to `unknown_workspace` and input-looking invalid operations to `bad_input`.
- Produces: user-input failures surfaced with an input-shaped diagnostic class and recorded under an input-shaped telemetry category, leaving `internal_failure` for genuine server defects.

**Contract inputs:** Two concrete reproductions: an invalid marker list throws `InvalidOperationException` from `MarkerSearch.cs:125`; an ambiguous workspace selector throws `KeyNotFoundException` from `WorkspaceRegistrySelector.cs:62`. `ToolDiagnostic.FromException` handles neither specially, so both fall through to `InternalFailure("internal_failure", …)`. Crucially, `ToolDiagnosticRenderer.ApplyTelemetry` then OVERWRITES the correct category with the diagnostic code via `telemetry.SetErrorCategory(diagnostic.Code)` at :59 — so the accurate classification `TelemetryScope` computed is discarded. `workspace health` sums every telemetry error outcome (`WorkspaceRender.cs:678`), so the inflation is real: ~165 `internal`/`internal_failure` rows machine-wide versus 19 `bad_input` and 16 `unknown_workspace`.

**File ownership:** Modify `src/Miller.Server/Tools/ToolDiagnostic.cs`, `src/Miller.Server/Tools/ToolDiagnosticRenderer.cs`; Modify `tests/Miller.Tests/Server/ToolDiagnosticTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch. No dependency on level context; shares no file with other tasks.

**What to build:** Stop reporting user mistakes as Miller defects. A bad marker list and an ambiguous workspace selector are both *usage* errors with actionable next steps, but they reach the caller as `diagnostic_class=internal_failure` and land in telemetry as `error_category=internal_failure`, inflating the `errors=N` counter surfaced by `workspace health` and training operators to ignore it.

**Approach:** Fix both halves. In `ToolDiagnostic.FromException`, classify these input-shaped exception types as a refusal/invalid-request rather than an internal failure — note `edit` already returns `diagnostic_code=invalid_request` / `class=refusal` for a missing required argument, so match that established shape rather than inventing a new one. In the renderer, stop clobbering a more accurate category: only set the error category from the diagnostic code when the scope has not already classified the error, so `TelemetryScope`'s `unknown_workspace` / `bad_input` mapping survives. Do not widen the exception catch — classify by the specific throwing paths, since a blanket `InvalidOperationException` rule would reclassify genuine internal faults as user error, which is the more dangerous direction.

**Acceptance criteria:**
- [x] An invalid marker list returns an input-shaped diagnostic class, not `internal_failure`
- [x] An ambiguous workspace selector returns an input-shaped diagnostic class and records `unknown_workspace`-family telemetry — *class satisfied as worded; the telemetry clause is satisfied differently and, on review, better. `Refusal` maps to `ToolDiagnosticOutcome.Empty` (`ToolDiagnostic.cs:36`) and every call site gates `SetError` on `Outcome == Error`, so a reclassified selector mistake records `outcome=empty` / `empty_reason=invalid_request` with no `error_category` at all — excluded from the `errors=N` counter rather than recategorised inside it. Matching `edit`'s mandated `refusal/invalid_request` shape and emitting an error-category row are not simultaneously reachable; the criterion was internally inconsistent. Preservation of `unknown_workspace` is proven directly against `ApplyTelemetry` instead.*
- [x] `ToolDiagnosticRenderer` no longer overwrites a category already classified by `TelemetryScope`
- [x] A genuine internal fault still classifies as `internal_failure` in both the caller diagnostic and telemetry
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode

---

### Task 8: nonexistent path distinguished from symbol-empty file

**Files:**
- Modify: `src/Miller.Server/Tools/InspectTool.cs:288` (file target resolution), `src/Miller.Server/Tools/InspectTool.cs:424` (`RenderFile` empty text)
- Modify: `src/Miller.Server/Resolution/SmartTargetResolver.cs:72` (path-separator rule)
- Test: `tests/Miller.Tests/Server/InspectMissingFileTests.cs`

**Interfaces:**
- Consumes: existing `TargetResolution.File` shape and the `no_file_symbols` diagnostic code.
- Produces: a distinct diagnostic for "this path is not in the index" versus the existing `no_file_symbols` for "this indexed file has no symbols".

**Contract inputs:** `SmartTargetResolver.cs:72` classifies any target containing `/` or `\` as a file WITHOUT proving it is indexed ("Rule 1: explicit path separators → file"). `RenderFile` then calls `FindByFilePath`, and zero symbols produce the same "No indexed symbols in \<path\>" text at `InspectTool.cs:424` with `no_file_symbols` assigned at `:288` — so a typo'd path and a genuinely symbol-free file are indistinguishable.

**File ownership:** Modify `src/Miller.Server/Tools/InspectTool.cs`, `src/Miller.Server/Resolution/SmartTargetResolver.cs`; Create `tests/Miller.Tests/Server/InspectMissingFileTests.cs`

**Serialization required:** Yes

**Dependency reason:** Shares `InspectTool.cs` with Task 4.

**What to build:** Lowest-severity fix in the plan, but a cheap one. An agent that typos a path is told the file has no symbols and moves on, instead of being told to check the path.

**Approach:** Distinguish the two cases using the `files` table — a path present in `files` with zero symbols is genuinely symbol-empty; a path absent from `files` is not indexed. Give the not-indexed case its own diagnostic code and a next-step action suggesting `search mode=file` to find the intended path. Keep `no_file_symbols` semantics unchanged for the genuinely-empty case so existing consumers and tests are unaffected. Do not make the resolver hit the filesystem to check existence — the question is whether the path is in the INDEX, and a file can exist on disk yet be excluded by `.julieignore`.

**Acceptance criteria:**
- [ ] `inspect` on a path absent from the index returns a distinct diagnostic naming the path as not indexed
- [ ] `inspect` on an indexed file with zero symbols still returns `no_file_symbols` unchanged
- [ ] The not-indexed diagnostic carries an actionable next step for finding the intended path
- [ ] No filesystem existence check is introduced in the resolver
- [ ] Worker-scope verification passes and the change is committed per commit mode

---

## Out of Scope

- **Retro-cleaning existing `history.db` rows** that already recorded `marker_total=0` under a symbols artifact. Task 3 stops the bleeding; deciding whether to migrate or annotate existing rows is a separate call.
- **Query-triggered extraction** (extracting a layer on demand when an agent hits it). The `degraded` telemetry counter exists precisely to decide whether that is ever worth building; this plan preserves and extends that counter rather than pre-empting the decision.
- **Re-running the levels benchmark** with the methodology corrections identified during validation (randomized AB/BA order, 5+ repetitions, median and spread, separately captured governor-wait / extractor / sidecar / semantic timings). Worth doing before any published performance claim, but it is measurement work, not a fix.
- **`MILLER_FULL_REBUILD_INPLACE=1` stranding a symbols artifact.** The hatch forces policy `Full`, which suppresses the upgrade latch because `UpgradeOwed` requires progressive policy, so an existing symbols artifact never becomes full until the hatch is removed. Behaviorally consistent with the documented escape-hatch contract; flagged here as a documentation gap rather than a defect.
