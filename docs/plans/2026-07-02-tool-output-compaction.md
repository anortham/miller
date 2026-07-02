# Tool Output Compaction & Informativeness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make Miller's MCP compact tool output cheaper in tokens and informative enough that an agent never needs a follow-up query, per the 2026-07-02 pre-release output audit (8 findings).

**Architecture:** All changes are render-layer or query-shaping changes inside existing tool classes (`Miller.Server/Tools/*`) plus one record extension (`WorkspaceListEntry`). No new MCP tools; two new optional parameters on the existing `workspace` tool. JSON outputs only change additively (new fields), never breaking existing keys.

**Tech Stack:** .NET 10, xUnit fast suite, existing pure tool cores.

**Architecture Quality:** No Architecture Impact — render/formatting and result-shaping changes within existing seams. `Miller.Core` stays I/O-free; nothing moves across the Core/Server boundary.

## Global Constraints

- Warnings are errors (`Directory.Build.props`); build must stay 0/0.
- Fast suite only for red/green (`scripts/test.sh`, <30s tripwire). No new test may spawn `julie-extract` — none of these tasks need it.
- Compact output is the default agent surface: every change must reduce or hold token count while adding information, never the reverse.
- JSON format changes must be **additive only** (new fields OK; renaming/removing existing fields is forbidden — Eros/CLI consumers).
- `AgentInstructionsTests` guards tool descriptions; if a tool description changes, update `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` accordingly.
- Do not push, tag, or release. Commit per task on the feature branch.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section) + `scripts/test.sh`.

**Worker red/green scope:** targeted fast-suite filter for the touched test class, e.g. `dotnet test tests/Miller.Tests --filter "FullyQualifiedName~TraceToolTests" -c Release` (or the class named in the task). csproj default filter already excludes Scale.

**Worker ceiling:** the full fast suite via `scripts/test.sh`. Workers do not run the scale suite.

**Worker gate invariant:** each task's new/changed unit tests prove the exact rendered-output contract stated in the task (string-shape assertions on compact output; key presence on JSON).

**Lead affected-change scope:** `scripts/test.sh` (full fast suite) after each merged task batch.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh all` (fast + scale; scale skips cleanly if `.tools/julie-extract` missing) before declaring done.

**Replay/metric evidence:** none — no hard metric gates; token-size comparisons are report-only (before/after sample renders in the run report).

**Escalation triggers:** any test outside the touched tool's test class failing; any JSON contract test failing (indicates a non-additive change — stop and report).

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** record invariant, command, scope label, commit SHA, result, timestamp per task in the run report.

## Model Routing

**Project source of truth:** none (`RAZORBACK.md` absent; no harness routing policy) → `inherit` throughout.

**Strategy tier:** lead (this session) — inherit.
**Implementation tier:** dispatched workers — inherit.
**Mechanical tier:** n/a (every task owns tests).
**Gate-interpretation reviewer:** lead inline review — inherit.
**Escalation tier:** lead — inherit.
**Worker eligibility:** all tasks are bounded, single-file-cluster changes with named tests — implementation tier.
**Escalation triggers:** JSON contract breakage, cross-tool test failures.
**Mechanical exclusion:** n/a.
**Unsupported harness behavior:** model per-agent selection unused; all agents inherit session model.

## Execution Notes

- Work in a dedicated worktree: `git worktree add .worktrees/tool-output-compaction -b tool-output-compaction` from repo root. All task work stays there.
- Tasks 1–8 are independent of each other (different render sites). Task 6 and Task 7 both touch `SearchTool.cs`-adjacent files but disjoint regions (`MarkerSearch.cs` vs `RunTextContent`); run sequentially anyway to avoid merge noise — the tasks are small.
- Existing render tests live in `tests/Miller.Tests/Server/` (`TraceToolTests`, `WorkspaceToolTests`, `ContextToolTests`, `InspectToolTests`, `SearchToolTests`, `ContentToolTests`, `WorkspaceRenderTests` if present — discover with Miller `inspect` on the test dir before editing).

---

### Task 1: `trace refs` — resolve `containing=` to a symbol name

**Files:**
- Modify: `src/Miller.Server/Tools/TraceTool.cs` (`ReferenceLine` :600, `RunRefs` :492, `WriteReference` ~:2101)
- Test: `tests/Miller.Tests/Server/TraceToolTests.cs`

**Interfaces:**
- Consumes: `ISymbolLookupIndex.FindBySymbolId(string)` (already used by `RunRefs` at :526); `SymbolRef.ContainingSymbolId` (`src/Miller.Indexing/SymbolDetail.cs:38`).
- Produces: compact ref line `path:line  kind  in=Name` (resolved) — no raw hash in compact output; JSON gains additive `containing_symbol_name` (string|null) next to existing `containing_symbol_id`.

**What to build:** Compact `trace mode=refs` currently renders `containing=<32-hex symbol id>` — pure token waste that forces a follow-up inspect. Resolve the id to the enclosing symbol's name.

**Approach:** Change `ReferenceLine(SymbolRef)` to `ReferenceLine(ISymbolLookupIndex index, SymbolRef reference)`; resolve `ContainingSymbolId` via `index.FindBySymbolId`. Render `in=<Name>` when resolved; when unresolvable, render nothing (drop the hash — it is unusable). Follow `InspectTool.DistinctCallers` (`InspectTool.cs:595`) as the resolution pattern. In `WriteReference` (JSON), add `containing_symbol_name` (nullable) after `containing_symbol_id`; JSON keeps the id (it is chainable there). `RunRefs` already holds `index` — thread it to both call sites. Resolution is per-line; refs are capped by `limit` (default 20), so ≤20 lookups — no batching needed (but `SymbolLookupBatch.FindBySymbolIds` exists if you prefer one batch call).

**Acceptance criteria:**
- [x] Compact refs output contains `in=<symbol name>` and never a 32-hex id.
- [x] JSON refs output has both `containing_symbol_id` and new `containing_symbol_name`.
- [x] Unresolvable containing id renders no `in=` segment (compact) and `null` name (JSON).
- [x] Worker-scope verification passes, committed.

### Task 2: `workspace list` — recency ordering, default cap, filter

**Files:**
- Modify: `src/Miller.Server/Tools/WorkspaceRender.cs` (`WorkspaceListEntry` :56, `ListCompact(entries)` :958, `ListJson(entries)` :999)
- Modify: `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs` (`ToListEntries` :112)
- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs` (`Workspace` method :133 — list operation dispatch)
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` (`WorkspaceList` ~:931 — CLI parity flags)
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` (workspace tool doc, if it enumerates list behavior)
- Test: `tests/Miller.Tests/Server/WorkspaceToolTests.cs` (+ CLI test class covering `workspace list` — discover exact class first)

**Interfaces:**
- Consumes: `WorkspaceRegistryRow.LastSeenAt` (`src/Miller.Indexing/WorkspaceRegistryRow.cs:21`).
- Produces: `WorkspaceListEntry` gains `DateTimeOffset LastSeenAt`; `workspace` MCP tool gains optional `filter` (string, default null) and `limit` (int, default 20, list operation only); compact tail line `… N more — raise limit or pass filter=<substring>`; JSON entries gain additive `last_seen_at` (ISO-8601).

**What to build:** `workspace list` dumps every registered workspace (139 rows ≈ 3.5k tokens locally). Order by relevance and cap compact output.

**Approach:** Ordering: current workspace first, then `LastSeenAt` descending. Compact: render at most `limit` entries (default 20; `limit<=0` means unlimited), then the omitted-count tail. Filter: case-insensitive substring match against `DisplayId` and `Root`, applied before the cap; when a filter is given and matches nothing, say so with the total registered count. JSON: unlimited by default (existing consumers), but respects explicit `limit`/`filter`; always add `last_seen_at`. Error-state entries (like `state: error`) that fall outside the cap must still be discoverable: append a one-line summary `errors: N workspace(s) in error state — filter or raise limit to see them` when any error entry was omitted. CLI `workspace list` gets `--filter <s>` / `--limit <n>` mapped to the same core. New MCP params go on the existing `Workspace` method with `[Description]` attributes mirroring this contract.

**Acceptance criteria:**
- [x] Compact list with >20 registered workspaces renders 20 entries + accurate omitted tail; current workspace always first.
- [x] `filter` narrows by display id or root substring (case-insensitive); no-match renders a helpful line, not an empty string.
- [x] JSON unchanged shape + additive `last_seen_at`; unlimited without explicit limit.
- [x] Omitted error-state entries produce the error summary line.
- [x] CLI `workspace list --filter/--limit` works (test via CLI dispatch unit path, not subprocess).
- [x] Worker-scope verification passes, committed.

### Task 3: `context` — rank neighbours by relevance, not symbol id

**Files:**
- Modify: `src/Miller.Server/Tools/ContextTool.cs` (`BuildCandidates` :305 step 3; new private scoring helper)
- Test: `tests/Miller.Tests/Server/ContextToolTests.cs`

**Interfaces:**
- Consumes: `graph.Reach(...)` result order `(hop asc, id asc)`; seed list with ranks from step 1; `ExtractIdentifierTokens` (:496).
- Produces: candidate list where hop>0 neighbours are ordered `(hop asc, relevance desc, id asc)`; render and packer code unchanged (they preserve caller order).

**What to build:** Neighbour order is currently `(hop, symbol-id)` — id order is arbitrary, so the 12 rendered neighbours are effectively random and the audit showed unrelated symbols crowding out relevant ones. Score neighbours by affinity to the query and seeds.

**Approach:** In `BuildCandidates` step 3, before appending reached nodes, compute a relevance score per reached symbol: (a) +2 per query/seed identifier token (from `ExtractIdentifierTokens(query)` plus seed symbol names, case-insensitive) that appears in the neighbour's `Name`; (b) +1 if the neighbour's `FilePath` equals any seed's `FilePath`; (c) +1 if the neighbour's `FilePath` directory equals a seed's directory. Sort reached by `(Hop asc, score desc, Id asc)` — stable and deterministic. Keep `candidatesExamined` semantics unchanged. Do NOT change seed selection or the packer. Test with a synthetic index: a seed whose same-file neighbour and name-overlapping neighbour must outrank an unrelated neighbour that has a smaller symbol id (the case that fails today).

**Acceptance criteria:**
- [x] Deterministic test proves same-file + name-overlap neighbours beat lower-id unrelated neighbours at equal hop.
- [x] Hop ordering still dominates (hop-1 before hop-2 regardless of score).
- [x] Existing ContextTool tests pass unmodified (or with order-only assertion updates justified in the commit message).
- [x] Worker-scope verification passes, committed.

### Task 4: `inspect depth=overview` — strip doc-comment lines from body preview

**Files:**
- Modify: `src/Miller.Server/Tools/InspectTool.cs` (`BodyPreview` :576)
- Test: `tests/Miller.Tests/Server/InspectToolTests.cs`

**Interfaces:**
- Consumes: `ExtractReader.BodyReadResult.Text`; caps `OverviewBodyPreviewMaxLines=16` / `OverviewBodyPreviewMaxChars=700` (:118-119).
- Produces: overview body preview with doc-comment lines removed before the line/char caps apply; `depth=full` body untouched.

**What to build:** The overview body preview of a container symbol spends its 16-line budget re-printing member doc comments that duplicate the already-rendered `doc:` section. Filter doc-comment lines out of the preview so the budget shows actual code.

**Approach:** In `BodyPreview`, after normalizing newlines and splitting, drop lines whose trimmed form is a doc-comment line: starts with `///` or `//!` (C#/Rust), or is inside a `/** … */` block (track a small in-block flag; a line starting `/**` opens, `*/` closes, drop inclusive), or starts with `"""` blocks is NOT in scope (Python docstrings are string literals — leave them). Keep ordinary `//` and `#` comments — they are code commentary, not doc duplication. If any lines were dropped, append nothing extra — the existing truncation note suffices; but count dropped lines toward `Truncated` only if the raw text also exceeded caps (dropping alone doesn't mean truncation). Then apply the existing line/char caps to the filtered lines.

**Acceptance criteria:**
- [x] Overview preview of a body containing `///` member docs shows code lines only, within existing caps.
- [x] `/** */` block lines are dropped; plain `//` comments are kept.
- [x] `depth=full` body output is byte-identical to before.
- [x] Worker-scope verification passes, committed.

### Task 5: `inspect depth=full` — dedup callees, group references by file

**Files:**
- Modify: `src/Miller.Server/Tools/InspectTool.cs` (`RenderSymbolCompact` :375 — references block :415-422, callees block :433-440)
- Test: `tests/Miller.Tests/Server/InspectToolTests.cs`

**Interfaces:**
- Consumes: `ExtractReader.ReadReferences` / `ReadCallees` results (unchanged).
- Produces: compact `## references` grouped as `path:l1,l2,l3` (one line per file); compact `## callees` deduped by name as `Name ×N  path:line` (first location, N omitted when 1). JSON untouched.

**What to build:** At `depth=full`, `## references` prints one `path:line` per row (11 lines for CompareVersions) and `## callees` repeats the same identifier (`TryParseTriple ×2`, `nameof ×2`, `ArgumentException ×2`). Group and dedup in compact rendering only.

**Approach:** References: group the (already path/line-ordered) refs by `FilePath`, render `path:l1,l2,…` per file; the omitted-count line (`AppendOmittedLine`) now counts refs, applied against `relationLimit` on the underlying ref count as today (group after `Take(relationLimit)` so the limit semantics don't change). Callees: dedup by `Name` preserving first occurrence, annotate `×N` when N>1; apply `Take(relationLimit)` AFTER dedup so full depth shows up to 50 distinct callees (more information for the same budget). Do not filter out any names (no keyword blocklist — `nameof`/`ArgumentException` still carry information; the dedup removes the repetition cost). Overview depth uses the same rendering with its own limit (3) — verify both depths in tests.

**Acceptance criteria:**
- [x] References render one line per file with comma-joined lines; totals/omitted counts remain accurate.
- [x] Callees are unique by name with `×N` counts; dedup happens before the relation limit.
- [x] JSON output unchanged.
- [x] Worker-scope verification passes, committed.

### Task 6: `search mode=markers` — collapse multi-marker lines

**Files:**
- Modify: `src/Miller.Server/Tools/MarkerSearch.cs` (`MarkerSearchHit` :189, `FindMarkers` :43, `RenderCompact` :128, `RenderJson` :158)
- Test: `tests/Miller.Tests/Server/` marker tests (discover exact class — search tests for `FindMarkers`)

**Interfaces:**
- Consumes: `RegionSearchHit.RegionId` as the collapse key.
- Produces: `MarkerSearchHit` becomes `(IReadOnlyList<string> Markers, RegionSearchHit Region)`; compact renders `path:line  TODO,FIXME,HACK  kind  Containing`; JSON keeps existing `marker` field (first marker, contract-compatible) and adds additive `markers` array.

**What to build:** A region whose text matches 3 requested markers renders 3 identical blocks (audit: `SearchTool.cs:45` ×3). Collapse to one block per region listing all matched markers.

**Approach:** In `FindMarkers`, key the dictionary by `RegionId` alone; accumulate the matched markers per region (ordered by `DefaultMarkers` index then name, matching today's tiebreak). `Take(limit)` then counts regions, not (marker,region) pairs — that is the correct unit. Sort by `(Path, Line, first-marker rank)` as today. `RenderCompact` joins markers with `,`. `RenderJson`: keep `"marker"` = first marker (additive-only rule) and add `"markers"` array. Update the internal callers (`SearchTool` markers mode, `CliDispatch.Todos` ~:155) for the record shape change.

**Acceptance criteria:**
- [x] A line matching TODO+FIXME+HACK renders exactly one block with `TODO,FIXME,HACK`.
- [x] `limit` counts distinct regions.
- [x] JSON has both `marker` (unchanged meaning: first) and `markers` (all, ordered).
- [x] CLI `todos` path compiles and its tests pass.
- [x] Worker-scope verification passes, committed.

### Task 7: text-content search — dedup identical (source, line) hits

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs` (both `RunTextContent` overloads :845 and :909 — add one shared dedup helper)
- Test: `tests/Miller.Tests/Server/SearchToolTests.cs`

**Interfaces:**
- Consumes: `TextContentSearchHit` (has `SourceId`, `DisplayPath`, line number, snippet — verify exact member names with Miller before coding).
- Produces: hit lists with at most one hit per `(SourceId, line)`; totals/`renderedCount`/`sourceBytes` computed from the deduped list.

**What to build:** `mode=source` returned the same `TraceTool.cs:2111` hit twice with identical snippets (overlapping chunks both match the line). Dedup after filtering, before paging.

**Approach:** Add `private static List<TextContentSearchHit> DedupByLine(List<TextContentSearchHit> hits)` keeping first occurrence per `(SourceId, LineNumber)` key (use the actual member names). Apply inside both `FetchWithEscalation` callbacks after the filter loop (so escalation counts see deduped totals) — i.e., dedup `hits` before returning `(fetched.Count, hits.Count)`. Preserve order. Also check `SearchRouteExecutor.RunTextContent` (`src/Miller.Server/Tools/SearchRouteExecutor.cs:67`) — if it fetches independently rather than delegating to these overloads, apply the same dedup there.

**Acceptance criteria:**
- [x] A fabricated index returning duplicate (source, line) hits renders the hit once; `total` reflects the deduped count.
- [x] Distinct lines from the same source are NOT collapsed.
- [x] `sourceBytes` accounting unchanged for the deduped page.
- [x] Worker-scope verification passes, committed.

### Task 8: single-say recovery text — content empty search + onboarding unresolved rows

**Files:**
- Modify: `src/Miller.Server/Tools/ContentTool.cs` (`RenderNoResultsCompact` :586)
- Modify: `src/Miller.Server/Tools/WorkspaceRender.cs` (`OnboardingCompact` :1033 hot-targets loop :1071-1082)
- Test: `tests/Miller.Tests/Server/ContentToolTests.cs`, workspace render/onboarding tests (discover exact class)

**Interfaces:**
- Consumes: `SearchNoResultsNextActions` (unchanged); `RecoveredTargetHash` rows where the target is unresolved (label renders as `unresolved repeated target`, confidence `unresolved_hash` — verify via `TargetLabel`).
- Produces: empty content-search compact = `No results for content search.` + `Tried content_kind=<kind>.` + `Next:` actions only (drop the duplicated "Try content_kind=… / use workspace_id=all… / use search mode=source…" prose sentence — the Next block already encodes those); onboarding hot-targets renders resolved targets individually and collapses ALL unresolved rows into one line `- unresolved repeated targets: N (M calls total)`.

**What to build:** Two double-say trims. The content empty-search message states the same recovery advice as prose and as structured next actions; the onboarding hot-target list spends rows on unresolved hashes that convey nothing individually.

**Approach:** ContentTool: delete the three-clause prose sentence in `RenderNoResultsCompact`, keep `Tried content_kind=<kind>.` (it states what was attempted — not duplicated in Next), keep `AppendContentNextActions`. JSON (`RenderNoResultsJson`) already only carries next_actions — leave it. WorkspaceRender: in the hot-targets loop, partition unresolved rows (identify via the same predicate `TargetLabel` uses to emit the unresolved label — inspect `TargetLabel`/`RecoveredTargetHash` first); render resolved rows as today (still capped at 5 total rendered rows), then if any unresolved exist append the single aggregate line with count and summed calls. Onboarding JSON unchanged.

**Acceptance criteria:**
- [x] Empty content search compact contains the advice exactly once (Next block), plus the `Tried content_kind` fact line.
- [x] Onboarding compact renders ≤1 line for unresolved targets regardless of row count; resolved targets unaffected.
- [x] JSON outputs unchanged for both surfaces.
- [x] Worker-scope verification passes, committed.

---

## Run Report (completed 2026-07-02)

Execution: subagent-driven-development, 8 tasks in 3 waves (T1/T2/T3/T4/T7 parallel → T5 → T6/T8 parallel), one fix round (T7 doc-comment placement). All tasks Lead-inline-reviewed and approved. Verification ledger: `.razorback/sdd/progress.md`.

**Branch gate (HEAD c7d67bf):** `dotnet build Miller.slnx -c Release` → 0 warnings / 0 errors. `scripts/test.sh all` → fast 2726/2726 (14s), scale 45/45 (real julie-extract, not skipped).

**Live before/after (report-only, this workspace):**
- `trace refs FreshnessService`: `containing=418b740cbe36…` (unusable hash, ~10 tokens/line) → `in=AddMillerServices` (actionable, shorter).
- `workspace list`: 140 rows ≈ 3.5k tokens → `# workspaces (20 of 140)` current-first/recency-ordered + `… 120 more` tail + error-state summary line.
- `inspect CompareVersions depth=full`: 11 single-ref lines → 5 grouped `path:l1,l2` lines; callees deduped with `×N`.
- `search mode=markers`: `SearchTool.cs:45` rendered once as `TODO,FIXME,HACK` (was 3 identical blocks).
- context neighbours, overview body preview, source-search dedup, content/onboarding single-say: proven by unit tests (deterministic fixtures).

**Notable judgment calls:** JSON stayed additive everywhere (`containing_symbol_name`, `last_seen_at`, `markers`); markers JSON keeps `marker`=first for contract compatibility. `WorkspaceListEntry.LastSeenAt` has a default value so pre-existing construction sites compile.

**Flag for future work:** `MILLER_AGENT_INSTRUCTIONS.md` is at 11,982 of its 12,000-char AgentInstructionsTests budget (18 chars headroom) — the next doc addition must trim elsewhere.
