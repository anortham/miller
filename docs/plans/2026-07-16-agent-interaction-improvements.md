# Agent→Miller Interaction Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Close the agent-adoption and output-actionability gaps identified in the 2026-07-16 six-topic audit: surface empty-result diagnosis to agents, remove remaining content-tool contract friction, trim output bloat, reword adoption-hostile guidance, add injection-only session hooks with a real delivery gate, expand instruction-tier harness reach, and re-measure.

**Architecture:** All changes stay inside the existing 9-tool MCP surface and existing channels (compact renderers, tool descriptions, embedded instruction core, plugin layer, CLI). New adoption surfaces are plugin hooks (prompt-injection only, fail-open) and a CLI verb — no new MCP tools. Diagnosis data already computed for telemetry is re-routed into **compact output only**; JSON shapes are untouched (see Global Constraints).

**Tech Stack:** .NET 10 / C# (Miller.Server tools + Miller.Tests), Node.js CJS (plugin hooks + `tests/plugin/*.test.cjs`), Markdown (guidance/docs).

**Architecture Quality:** No new modules. The one interface addition is a typed `SearchNextAction` record inside `SearchTool` mirroring the existing `TraceNextAction` (`src/Miller.Server/Tools/TraceTool.cs:144`) pattern — per-tool decision, shared `NextStepHint`-style single-line rendering stays untouched. Hooks are static prompt emitters with zero Miller-binary coupling (no download, no index access). Main architecture risk: compact-output changes leaking into JSON contracts — mitigated by pin tests added in the same tasks.

## Evidence Base (audit findings this plan addresses)

Telemetry window 2026-06-16→07-16, 30,067 calls, 10 workspaces (`~/.miller/telemetry.db`):

- Tool mix: inspect 54% + search 27% = ~80%; edit 0.7% with 26% error rate; content 13.8% errors (contract friction); trace 29% / patterns 25% / impact 12% empty.
- Search empty rate flat-to-worse (weekly 27→16→43→33→32%); symbol/auto <1% empty; text modes stuck (source 45.6%, file 43.9%, content 35.6%).
- `empty_diagnosis` (shipped 1.9.0): 87.5% `true_no_hit`, 125 `query_shape`, 53 `mode_mismatch`; 68 file-mode path-like query-shape failures. The diagnosis is computed at `src/Miller.Server/Tools/SearchTool.cs:489` and `src/Miller.Server/Tools/ContentTool.cs:471` but goes to telemetry only — agents get the static `SymbolNoResultsHint` (`SearchTool.cs:60`).
- **Baseline caveat (2026-07-16 re-verification):** several audit error samples predate current behavior. Already shipped today: content read resolves exact/unique `display_path` with ambiguity handling (`ContentTool.cs:285` → `ResolveSourceId`); `content_kind` accepts case-insensitive aliases `source|docs|doc|config|external|file` (`ContentTool.cs:484-500`); file mode matches directory fragments and suffix paths with `\` normalization (`src/Miller.Indexing/FilePathSymbolLookup.cs`). Tasks T1.3/T2.1/T2.2 are scoped to the *residual* gaps and pin the shipped behavior as regression tests.
- Cross-project evidence (ponytail, context-mode): both skip MCP ServerInstructions and deliver guidance via SessionStart hook `additionalContext` (~4.6–5.2KB, no 2KB cap); SessionStart context never reaches subagents (SubagentStart re-injection required); measured vocabulary effects — "blocked"→"redirected" flipped Opus capitulation 6/6→0/6, bare-NOT negations regressed smaller models; unenforced budgets rot (context-mode descriptions 2–3× over their own cap), so every new guidance surface gets a gating test.
- context-mode is Elastic-2.0 licensed: ideas only, **no code copying**. Re-implement everything from behavior descriptions.

## Global Constraints

- MCP surface stays exactly 9 tools (`AgentInstructionsTests` pins the list). No new MCP tools; new surfaces are CLI verbs, plugin hooks, or docs.
- Guidance budgets are hard gates and must NOT be raised: server instructions ≤1,900 chars; tool descriptions ≤900 (trace ≤1,500, search ≤1,100); combined ≤9,000; params ≤250 (`tests/Miller.Tests/Server/AgentInstructionsTests.cs:25-46`).
- **JSON output shapes are frozen for this plan.** Empty search JSON is the literal `[]` (`SearchTool.cs:740,:750`) and stays `[]`; suggestion-bearing empties keep today's `RenderEmptyJson` shape. All new diagnostics/hints/suggestions in this plan are **compact-only**. Tasks touching empty paths add pin tests for the top-level JSON type. (User note 2026-07-16: Eros is owned in-house, so breaking the contract IS allowed when paired with a coordinated Eros update — the freeze here is a scoping choice to keep this plan single-repo, not an external prohibition. JSON enrichment of empty results is a viable follow-up plan: change the shape, update Eros consumers, rev `docs/contracts/cli-eros-v1.md` together.)
- **Empty-output budget:** a compact empty result renders ≤6 lines and ≤400 chars (excluding the workspace banner), with exactly one primary recovery action (two only for genuine ambiguity). New guidance **replaces** existing hint text; it never stacks on top.
- `Miller.Core` stays zero-I/O. Build must be 0 warnings / 0 errors (`TreatWarningsAsErrors`).
- Test split is load-bearing: fast suite via `scripts/test.sh`; any test spawning `julie-extract` gets `[Trait("Category","Scale")]` via `ScaleTestSupport.RequireJulieServer()`.
- Hooks must be prompt-injection only: never `deny`, never `ask`, never `modify`; fail-open on every error path; ≤1s stdin timeout; no network, no Miller binary launch, no filesystem writes outside the plugin's own state. Miller-owned opt-out: `MILLER_SESSION_HOOKS=0` makes every hook script exit 0 silently.
- Every new guidance surface (routing block, rules file) ships with a budget test and a drift/canary test in the same task — no untested guidance text.
- Redirect vocabulary rule (all new/edited guidance): affirm the capability, name the alternative, no bare-NOT negations without a named alternative ("NOT for: X (use Y)" stays; "do NOT re-verify" goes).
- No push, tag, release, or version bump without explicit user approval. Plugin manifest versions stay aligned (`tests/plugin/plugin-manifest.test.cjs`). **A pushed release-prep commit is a live marketplace release** — Phase 5 delivery (T5.5) is a single approval-gated session.
- External-surface claims (harness hook payload shapes, rules-file formats) must be verified against live hosts or current official docs during the owning task (razorback:grounding-in-current-docs) and the verified facts recorded in the task's findings — conflicting secondhand claims exist (see T5.2 note).

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section), `scripts/test.sh`, `scripts/test-plugin.sh`.

**Worker red/green scope:** focused test class run, e.g. `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~SearchToolTests&Category!=Scale"` (the explicit `Category!=Scale` is required because a command-line `--filter` overrides the csproj default). Plugin tasks: `node --test tests/plugin/<file>.test.cjs` or `scripts/test-plugin.sh`.

**Worker ceiling:** `scripts/test.sh` (fast suite, <30s budget tripwire). Workers do not run the scale suite.

**Worker gate invariant:** each task's acceptance criteria name the behavior its focused tests must prove (new empty-output text, clamps, budgets, JSON-shape pins).

**Lead affected-change scope:** `scripts/test.sh` after each merged batch; add `scripts/test-plugin.sh` whenever `bin/`, `hooks/`, or any plugin manifest changed.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh all` before handoff/PR.

**Replay/metric evidence:** Phase 7 telemetry re-measurement is report-only (findings doc), not a merge gate; the hard gates are the test suites above.

**Escalation triggers:** touching the indexing/extract path or sidecar build → run `scripts/test.sh scale`. Touching plugin launcher/hooks → `scripts/test-plugin.sh` mandatory. T5.5 (delivery) → full release checklist per CLAUDE.md release rules.

**Assigned verification failure:** workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate (T4.1/T4.2 explicitly update golden-clause pins in `AgentInstructionsTests`).

**Verification ledger:** record invariant, command, scope label, commit SHA, result, timestamp per task. Reuse passing evidence for the same HEAD instead of rerunning expensive gates.

## Parallel Execution Contract

Lanes: **A** = `SearchTool.cs` (+its tests), **B** = `ContentTool.cs` (+its tests), **C** = `ImpactTool.cs`, **D** = `EditService.cs`, **E** = docs only, **F** = guidance core + `AgentInstructionsTests`, **G** = plugin/Node (`hooks/`, manifests, `tests/plugin/`), **H** = CLI + embedded resources.

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| T1.1 Search empty diagnosis + next action (compact) | Batch 1 (lane A) | Modify `src/Miller.Server/Tools/SearchTool.cs`; Test `tests/Miller.Tests/Server/SearchToolTests.cs` | No | None - safe parallel batch. |
| T1.2 Did-you-mean for text-mode misses | Batch 2 (lane A) | Modify `src/Miller.Server/Tools/SearchTool.cs`; Test `tests/Miller.Tests/Server/SearchToolTests.cs` | Yes | Same files as T1.1; runs after T1.1 lands. |
| T1.3 File-mode residual path forms | Batch 3 (lane A) | Modify `src/Miller.Server/Tools/SearchTool.cs` and/or `src/Miller.Indexing/FilePathSymbolLookup.cs`; Test `tests/Miller.Tests/Server/SearchToolTests.cs` | Yes | Same files as T1.1/T1.2; runs after T1.2 lands. |
| T1.4 Content-search empty diagnosis (compact) | Batch 1 (lane B) | Modify `src/Miller.Server/Tools/ContentTool.cs`; Test `tests/Miller.Tests/Server/ContentToolTests.cs` | No | None - safe parallel batch. |
| T2.1 Content-read suffix resolution + near-path suggestions | Batch 2 (lane B) | Modify `src/Miller.Server/Tools/ContentTool.cs` (+`ResolveSourceId` store path if needed); Test `tests/Miller.Tests/Server/ContentToolTests.cs` | Yes | Same files as T1.4; runs after T1.4 lands. |
| T2.2 Read-window clamp + kind-alias regression pins | Batch 3 (lane B) | Modify `src/Miller.Server/Tools/ContentTool.cs`, `src/Miller.Indexing/ContentCorpusExternalStore.cs`; Test `tests/Miller.Tests/Server/ContentToolTests.cs` | Yes | Same files as T2.1; runs after T2.1 lands. |
| T3.1 Impact compact row cap | Batch 1 (lane C) | Modify `src/Miller.Server/Tools/ImpactTool.cs`; Test `tests/Miller.Tests/Server/ImpactToolTests.cs` | No | None - safe parallel batch. |
| T3.2 Content-search source_id dedup | Batch 4 (lane B) | Modify `src/Miller.Server/Tools/ContentTool.cs`; Test `tests/Miller.Tests/Server/ContentToolTests.cs` | Yes | Same files as T2.2; runs after T2.2 lands. |
| T3.3 Edit match-proof compression | Batch 1 (lane D) | Modify `src/Miller.Server/Tools/EditService.cs` (`AppendEvidence` :823); Test `tests/Miller.Tests/Server/EditToolTests.cs` | No | None - safe parallel batch. |
| T4.1 Instruction-core vocabulary rework | Batch 1 (lane F) | Modify `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`, `tests/Miller.Tests/Server/AgentInstructionsTests.cs` | No | None - safe parallel batch. |
| T3.4 Shared SignatureMaxLength const | Batch 5a (cross-lane) | Modify `SearchTool.cs`, `InspectTool.cs`, `ContextTool.cs`; Create shared const home; Test: existing suites + const guard | Yes | Touches lanes A + two otherwise-untouched files; runs after Batches 1–4 land. |
| T4.2 Description bare-NOT sweep | Batch 5b (cross-lane) | Modify `[Description]` strings across the nine `src/Miller.Server/Tools/*Tool.cs`; Test `AgentInstructionsTests.cs` | Yes | Same files as T3.4 (SearchTool/InspectTool/ContextTool); runs after T3.4 lands. |
| T4.3 Nudge-graph extension | Batch 6 (lane A′) | Modify `src/Miller.Server/Tools/InspectTool.cs`; Test `tests/Miller.Tests/Server/InspectToolTests.cs` | Yes | Depends on T4.2 wording being final so hints match descriptions. |
| T3.5 ADR-0001 figure refresh | Batch 6 (lane E) | Modify `docs/adr/ADR-0001-guidance-delivery-channels.md` | Yes | Measures description totals; must run after T4.2's final description edits. |
| T5.1 Routing block + gate tests | Batch 2 (lane G) | Create `hooks/miller-routing-block.md`, `tests/plugin/hooks-routing-block.test.cjs` | Yes | Canary cross-checks the instruction core; needs T4.1's final wording. |
| T5.2 Session hook script + hooks JSON | Batch 3 (lane G) | Create `hooks/miller-session-hook.cjs`, `hooks/claude-codex-hooks.json`; Test `tests/plugin/hooks-session-hook.test.cjs` | Yes | Consumes T5.1's routing block file. |
| T5.3 Manifest wiring + Windows variants | Batch 4 (lane G) | Modify `.claude-plugin/plugin.json`, `.codex-plugin/plugin.json`, `tests/plugin/plugin-manifest.test.cjs`, `README.md` | Yes | Consumes T5.2's hooks JSON. |
| T5.4 SubagentStart re-injection | Batch 5 (lane G) | Modify `hooks/claude-codex-hooks.json`, `hooks/miller-session-hook.cjs`; Test `tests/plugin/hooks-session-hook.test.cjs` | Yes | Extends T5.2/T5.3 artifacts. |
| T5.5 Hook delivery gate: release + install + trust + smoke | Batch 7 (lane G, **approval-gated**) | Modify version-bearing manifests per release process; Create `docs/findings/<date>-hook-delivery-verification.md` | Yes | Requires T5.1–T5.4 merged, all gates green, and explicit user approval to version-bump/push/publish. |
| T6.1 `miller rules` CLI verb (embedded block) | Batch 6 (lane H) | Modify `src/Miller.Server/Cli/CliDispatch.cs`, `src/Miller.Server/Miller.Server.csproj` (EmbeddedResource); Create render helper + `docs/contracts/rules-v1.md`; Test `tests/Miller.Tests/Cli/*` | Yes | Consumes final T4.1 wording + T5.1 routing block as embedded source text. |
| T6.2 Instruction-tier docs | Batch 8 (lane E) | Modify `README.md`, `docs/README.md` | Yes | Documents T6.1 output; runs after T6.1. |
| T7.1 Edit-failure telemetry review | Batch 9 (analysis, date-gated) | Create `docs/findings/<date>-edit-failure-telemetry.md` | Yes | Needs ≥2 weeks of 1.9.0 `edit_failure_reason` data (from ~2026-07-28); independent of T7.2. |
| T7.2 Post-ship adoption re-measure | Batch 10 (analysis, release-gated) | Create `docs/findings/<date>-agent-interaction-remeasure.md` | Yes | Clock starts at T5.5 delivery verification, not merge; needs ≥2 weeks of post-release telemetry. |

---

# Phase 1 — Empty-result actionability (search + content)

The highest-leverage fix: the diagnosis Miller already computes must reach the agent, with a concrete next call. **Compact-only; JSON stays byte-identical.**

### Task T1.1: Surface `empty_diagnosis` in search compact empty output with one typed next action

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs` (`ApplyEmptyTelemetry` :489, `EmptyDiagnosisFor` :508-552, `SymbolNoResultsHint` :60, `RenderEmptySymbolMissCompact` :1688, and the text/content/source empty render paths)
- Test: `tests/Miller.Tests/Server/SearchToolTests.cs`

**Interfaces:**
- Consumes: existing `query_shape` classifier (:483) and `EmptyDiagnosisFor` route logic; `TraceNextAction` pattern (`TraceTool.cs:144`, `AppendNextActions` :1779-1806) as the structural template — do not share the type, mirror it.
- Produces: `SearchNextAction` private record + a diagnosis-driven empty renderer. Compact empty output = one diagnosis-specific hint sentence + one primary `Next:` call (two only when the diagnosis is genuinely ambiguous between two modes). This **replaces** the static `SymbolNoResultsHint` text on text/content/source/file routes; symbol-route `Try:` suggestions (`EmptySuggestionLimit` :62) are kept and count toward the line budget.
- JSON: unchanged. Empty JSON remains literal `[]` / existing `RenderEmptyJson` shape; add pin tests asserting the top-level JSON type for empty results on every route.

**Contract inputs:** diagnosis values `true_no_hit | query_shape | mode_mismatch`; query shapes `identifier_like | natural_language | path_like | docs_like | source_like | short`. Empty-output budget and redirect vocabulary from Global Constraints. **Reachable-case enumeration:** derive the diagnosis×route matrix from `EmptyDiagnosisFor`'s actual branches (not a Cartesian product); test exactly the reachable pairs and document them in the test file.

**File ownership:** Modify `src/Miller.Server/Tools/SearchTool.cs`; Test `tests/Miller.Tests/Server/SearchToolTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Replace the one-size static empty hint with diagnosis-driven output. `mode_mismatch` names the specific right mode (the classifier already knows it — e.g. docs-like query in `mode=source` → "this reads like docs/config prose — retry with mode=content" + the copyable call). `query_shape` explains what failed about the shape and shows one corrected example call. `true_no_hit` states plainly that the workspace text has no lexical match and (for natural-language shapes) says to retry with words that appear literally in code or docs. Wording is affirmative-redirect.

**Acceptance criteria:**
- [x] Every *reachable* diagnosis×route pair renders its specific hint + exactly one primary `Next:` call (two only for documented ambiguous pairs); total empty output ≤6 lines / ≤400 chars excluding banner.
- [x] Empty JSON responses are byte-identical to current behavior; pin tests assert top-level `[]` / existing envelope per route.
- [x] No bare-NOT phrasing in any new string.
- [x] Focused SearchToolTests pass; change handed to lead per commit mode.

### Task T1.2: Did-you-mean suggestions for identifier-like text-mode misses

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs`
- Test: `tests/Miller.Tests/Server/SearchToolTests.cs`

**Interfaces:**
- Consumes: T1.1's empty render path; `SymbolSuggestionEngine.Suggest` (already used by symbol-route empties, :745-747).
- Produces: on `mode=source|content` compact empty results where `query_shape=identifier_like`, up to 3 `Try:` near-match symbol suggestions (name + path:line) within the T1.1 line budget (suggestions displace the corrected-example line, never exceed the budget). Compact-only.

**Contract inputs:** telemetry shows 402 identifier-like true-no-hits in source/content last month — likely misremembered names; reuse `SymbolSuggestionEngine` exactly as the symbol route does (no new ranking).

**File ownership:** Modify `src/Miller.Server/Tools/SearchTool.cs`; Test `tests/Miller.Tests/Server/SearchToolTests.cs`

**Serialization required:** Yes

**Dependency reason:** Same files as T1.1; runs after T1.1 lands.

**What to build:** When a text-mode search for an identifier-shaped query returns nothing, run the existing suggestion engine against the identifier and offer near matches. Converts hallucinated-name dead ends into one-call recoveries.

**Acceptance criteria:**
- [x] Identifier-like empty source/content searches with near symbol matches render ≤3 suggestions inside the ≤6-line budget; no suggestions block when nothing is near.
- [x] Suggestion lookup is invoked **only** on the empty text-mode path — unit test asserts the engine is not called for non-empty results or non-identifier shapes.
- [x] Empty JSON responses unchanged (pinned).
- [x] Focused SearchToolTests pass; handed to lead per commit mode.

### Task T1.3: File-mode residual path-form fixes (probe-first)

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs` and/or `src/Miller.Indexing/FilePathSymbolLookup.cs` (only where probes show gaps)
- Test: `tests/Miller.Tests/Server/SearchToolTests.cs`

**Interfaces:**
- Consumes: `FilePathSymbolLookup.FindByFilePathFragment` — **already shipped:** directory fragments, suffix paths, basename matching, `\`→`/` normalization. The plan's original examples (`src/Miller.Server/Tools`, `Tools/SearchTool.cs`) already work.
- Produces: (a) regression tests pinning the shipped forms above; (b) fixes for the residual failing forms only. Known candidates to probe: leading `./`, absolute paths that include the workspace root, trailing separators, queries with embedded spaces. 68 file-mode path-like empties/month say *something* still misses — the task starts by probing which forms.

**Contract inputs:** probe against a real index (dogfood workspace) before writing code; record the failing-form inventory in the task findings. `Miller.Indexing` changes stay pure lookup logic.

**File ownership:** Modify `src/Miller.Server/Tools/SearchTool.cs` and/or `src/Miller.Indexing/FilePathSymbolLookup.cs`; Test `tests/Miller.Tests/Server/SearchToolTests.cs`

**Serialization required:** Yes

**Dependency reason:** Same files as T1.1/T1.2; runs after T1.2 lands.

**Acceptance criteria:**
- [x] Shipped path forms pinned as regression tests (fragment, suffix, basename, backslash).
- [x] Each probed-and-confirmed failing form either fixed with a test or documented in the findings as out of scope with a reason.
- [x] No ranking change for basename queries (existing tests stay green).
- [x] Focused SearchToolTests pass; handed to lead per commit mode.

### Task T1.4: Content-search empty output surfaces its diagnosis

**Files:**
- Modify: `src/Miller.Server/Tools/ContentTool.cs` (`SetContentSearchEmptyTelemetry` :471, `RenderNoResultsCompact` :596)
- Test: `tests/Miller.Tests/Server/ContentToolTests.cs`

**Interfaces:**
- Consumes: `SearchTool.EmptyDiagnosisForContentSearch` (:471); content's existing empty renderer, which already states facts + emits next actions (:596-608).
- Produces: the existing empty output **reworked in place** (not appended to): one diagnosis-specific hint sentence replaces the generic fact line where a diagnosis exists; the Next block is trimmed to one primary action (two for ambiguity). Same ≤6-line/≤400-char budget. Compact-only; JSON empty shape pinned unchanged.

**File ownership:** Modify `src/Miller.Server/Tools/ContentTool.cs`; Test `tests/Miller.Tests/Server/ContentToolTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**Acceptance criteria:**
- [x] Content-search empty output carries the diagnosis-specific hint + ≤2 next actions within budget; nothing stacked on the old text.
- [x] Empty JSON pinned byte-identical.
- [x] Focused ContentToolTests pass; handed to lead per commit mode.

# Phase 2 — Content contract friction (kill the wasted round-trips)

Re-baselined 2026-07-16: exact/unique display_path resolution and kind aliases already shipped. What remains is suffix resolution, miss recovery, and window clamping.

### Task T2.1: Content-read unique-suffix resolution + near-path suggestions on miss

**Files:**
- Modify: `src/Miller.Server/Tools/ContentTool.cs` (`ResolveReadSourceId` :285) and the store's `ResolveSourceId` if suffix matching belongs there (implementer inspects `ContentCorpusExternalStore` first)
- Test: `tests/Miller.Tests/Server/ContentToolTests.cs`

**Interfaces:**
- Consumes: shipped resolution (exact `source_id` → exact/unique case-insensitive `display_path` → ambiguity error listing candidates).
- Produces: two additions, `content read` only (content search has no source selector): (a) unique **path-suffix** resolution (`plans/x.md` resolves when exactly one display_path ends with it; ambiguous → existing candidates error, capped at 5); (b) on a full miss, the not-found error appends ≤3 nearest display_path suggestions (simple ranked containment/suffix similarity — no new ranking machinery).

**Contract inputs:** pin the shipped resolution behaviors as regression tests first. Whether `content remove` gains the same resolution: **no** — removal keeps strict `source_id` (destructive ops don't get fuzzy matching); record this decision in the task findings.

**File ownership:** Modify `src/Miller.Server/Tools/ContentTool.cs` (+`ResolveSourceId` store path if needed); Test `tests/Miller.Tests/Server/ContentToolTests.cs`

**Serialization required:** Yes

**Dependency reason:** Same files as T1.4; runs after T1.4 lands.

**Acceptance criteria:**
- [x] Shipped exact/unique display_path + ambiguity behaviors pinned as regression tests.
- [x] Unique suffix resolves; ambiguous suffix lists ≤5 candidates; full miss suggests ≤3 near paths.
- [x] `content remove` behavior unchanged (strict source_id), asserted by test.
- [x] Focused ContentToolTests pass; handed to lead per commit mode.

### Task T2.2: Read-window clamp with exact semantics + kind-alias regression pins

**Files:**
- Modify: `src/Miller.Server/Tools/ContentTool.cs`, `src/Miller.Indexing/ContentCorpusExternalStore.cs` (window build :213)
- Test: `tests/Miller.Tests/Server/ContentToolTests.cs`

**Interfaces:**
- Consumes: current semantics — `line` is the window **center**, `context_lines` symmetric, >200 total lines errors (17 errors/month).
- Produces: clamping with these exact semantics:
  1. Compute the requested window `[line − context_lines, line + context_lines]`, clip to source bounds (as today).
  2. If the clipped window exceeds 200 lines, trim from the **tail** (higher line numbers) until exactly 200 remain — the requested center always survives.
  3. Prepend note: `window clamped to 200 lines (requested <requested_lines>) — continue with line=<next_center> context_lines=<same>` where `requested_lines` = pre-clip `2×context_lines+1` and `next_center` = last rendered line + `context_lines` + 1 (so the next symmetric window starts exactly after the last rendered line).
  4. Worked examples required as tests: middle-of-file, window clipped at start of file, clipped at end, one-line source, `context_lines` ≥ source length.
- Also: kind aliases (`source|docs|doc|config|external|file`, case-insensitive) pinned as regression tests; the unknown-kind error message extended to mention accepted aliases. No new aliases.

**Contract inputs:** compact and JSON must render the same clamped window (JSON gets no new fields; the note is compact-only, and JSON consumers see the same line rows as compact — assert parity in tests).

**File ownership:** Modify `src/Miller.Server/Tools/ContentTool.cs`, `src/Miller.Indexing/ContentCorpusExternalStore.cs`; Test `tests/Miller.Tests/Server/ContentToolTests.cs`

**Serialization required:** Yes

**Dependency reason:** Same files as T2.1; runs after T2.1 lands.

**Acceptance criteria:**
- [x] Oversized windows return clamped content + note (no error); all five worked examples pass; compact/JSON render identical line sets.
- [x] Alias regression pins green; unknown kinds error with canonical values + aliases listed.
- [x] Focused ContentToolTests pass; handed to lead per commit mode.

# Phase 3 — Output token trims

### Task T3.1: Cap impact's compact impacted-symbol rows

**Files:**
- Modify: `src/Miller.Server/Tools/ImpactTool.cs` (`CompactLikelyTestsLimit` :39, param default :83, `RenderCompact` :734, `AppendReachedGroups` :757)
- Test: `tests/Miller.Tests/Server/ImpactToolTests.cs`

**What to build:** `CompactImpactedLimit = 40`: compact output renders ≤40 impacted rows then `... N more impacted; use format=json for the full list.` JSON output unchanged (bounded by `limit` param as today; pin test).

**File ownership:** Modify `src/Miller.Server/Tools/ImpactTool.cs`; Test `tests/Miller.Tests/Server/ImpactToolTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**Acceptance criteria:**
- [x] Compact impact output never exceeds 40 impacted rows; overflow line states remainder + JSON escape hatch; JSON pinned unchanged.
- [x] Focused ImpactToolTests pass; handed to lead per commit mode.

### Task T3.2: Deduplicate source_id noise in content-search compact output

**Files:**
- Modify: `src/Miller.Server/Tools/ContentTool.cs` (search render :511, cross-workspace variant :533)
- Test: `tests/Miller.Tests/Server/ContentToolTests.cs`

**What to build:** Group hits by source: `display_path  content_kind  source_id=<id>` once per source, hits as indented `:line  snippet` rows beneath (SearchTool's repeated-file grouping pattern). Cross-workspace: `workspace_id` once per workspace group. Keep the trailing `read:` handoff line. JSON unchanged (pin).

**File ownership:** Modify `src/Miller.Server/Tools/ContentTool.cs`; Test `tests/Miller.Tests/Server/ContentToolTests.cs`

**Serialization required:** Yes

**Dependency reason:** Same files as T2.2; runs after T2.2 lands.

**Acceptance criteria:**
- [x] Multi-hit-per-source results render each `source_id`/workspace id exactly once; JSON pinned unchanged.
- [x] Focused ContentToolTests pass; handed to lead per commit mode.

### Task T3.3: Compress edit's match-proof block in compact output

**Files:**
- Modify: `src/Miller.Server/Tools/EditService.cs` (`AppendEvidence` :823)
- Test: `tests/Miller.Tests/Server/EditToolTests.cs`

**What to build:** Collapse the fixed 8-label evidence block to ≤2 lines in compact output: `match: <mode> ×<count> @ <file>:<range> (<disk_verified>, index <state>)`, second line only for abnormal states (`stale_allowed`, `content_index_note`, occurrence disambiguation). JSON keeps every existing evidence field byte-for-byte (pin). Cover preview, applied, and abnormal paths.

**File ownership:** Modify `src/Miller.Server/Tools/EditService.cs`; Test `tests/Miller.Tests/Server/EditToolTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**Acceptance criteria:**
- [x] Normal-path evidenced edits render ≤2 evidence lines across preview/applied; abnormal states still surface loudly; JSON pinned unchanged.
- [x] Focused EditToolTests pass; handed to lead per commit mode.

### Task T3.4: Single home for SignatureMaxLength

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs:615`, `src/Miller.Server/Tools/InspectTool.cs:116`, `src/Miller.Server/Tools/ContextTool.cs:145`; Create the shared const in an existing shared tools helper (e.g. alongside `NextStepHint` in `src/Miller.Server/Tools/`)
- Test: one const-reference guard test; existing suites cover behavior.

**What to build:** One `internal const int SignatureMaxLength = 110` consumed by all three renderers.

**File ownership:** Modify `SearchTool.cs`, `InspectTool.cs`, `ContextTool.cs`; Create shared const home; Test: existing suites + const guard

**Serialization required:** Yes

**Dependency reason:** Touches lane A + two otherwise-untouched files; runs after Batches 1–4 land (Batch 5a, before T4.2).

**Acceptance criteria:**
- [x] Literal `110` appears once; all three tools reference the shared const; fast suite passes.

### Task T3.5: Refresh ADR-0001's stale measurement

**Files:**
- Modify: `docs/adr/ADR-0001-guidance-delivery-channels.md:49`

**What to build:** Re-measure the descriptions-only total **after T4.2's final edits** and update the recorded figure (was 5,821; 5,881 as of 2026-07-16 pre-T4.2) with the measurement date, plus one sentence noting the test gate, not the ADR figure, is authoritative.

**File ownership:** Modify `docs/adr/ADR-0001-guidance-delivery-channels.md`

**Serialization required:** Yes

**Dependency reason:** Measures description totals; must run after T4.2's final description edits (Batch 6).

**Acceptance criteria:**
- [x] ADR figure matches a post-T4.2 measurement; measurement date recorded.

# Phase 4 — Guidance vocabulary rework

### Task T4.1: Affirmative-redirect rewrite of the instruction core

**Files:**
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`, `tests/Miller.Tests/Server/AgentInstructionsTests.cs` (golden-clause pins :97-100 only — budgets untouched)
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

**What to build:** Rewrite Rule 6 ("Trust the index: do NOT re-verify Miller results with grep/find…") and any other bare-NOT constructs into affirmative redirects, e.g. "Trust the index: Miller results are current for the indexed revision. If something looks stale, run `workspace refresh` and retry — that beats re-checking with grep." Stay ≤1,900 chars (current headroom: 16 chars — the rewrite must be net-neutral or shorter). Update golden-clause pins to the new wording.

**Contract inputs:** context-mode ADR-0003 evidence (measured on their probes, treated as directional): restriction vocabulary induces capitulation; bare-NOT primes the negated frame. Borrow the *rule*, not their text.

**File ownership:** Modify `MILLER_AGENT_INSTRUCTIONS.md`, `AgentInstructionsTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**Acceptance criteria:**
- [x] No bare-NOT construct without a named alternative remains in the instruction core; char count ≤1,900 post-CRLF-normalization.
- [x] AgentInstructionsTests pass with updated pins; handed to lead per commit mode.

### Task T4.2: Description sweep for adoption-hostile phrasing

**Files:**
- Modify: `[Description]` attributes in the nine `src/Miller.Server/Tools/*Tool.cs`
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

**What to build:** Audit all nine descriptions for bare-NOT-without-alternative phrasing. The template's `NOT for: X (use Y)` clause is compliant (names the alternative) and test-required — keep it. Fix only constructs that negate without redirecting. Budgets unchanged.

**File ownership:** Modify description strings across nine tool files; Test `AgentInstructionsTests.cs`

**Serialization required:** Yes

**Dependency reason:** Same files as T3.4 (SearchTool/InspectTool/ContextTool); runs after T3.4 lands (Batch 5b).

**Acceptance criteria:**
- [x] Every description passes the `NOT for:`+`Example` template test and per-tool budgets; no bare-NOT-without-alternative remains.
- [x] Fast suite passes; handed to lead per commit mode.

### Task T4.3: Extend the nudge graph toward underused tools

**Files:**
- Modify: `src/Miller.Server/Tools/InspectTool.cs` (refs render :426, `RefLimit` :117)
- Test: `tests/Miller.Tests/Server/InspectToolTests.cs`

**What to build:** One new nudge: when inspect truncates references at `RefLimit` (50), append `next: trace target="<sym>" mode=refs — full reference list` via `NextStepHint`. Preserve the ≤1-nudge-per-response invariant (truncation nudge replaces, never stacks with, the impact nudge — truncation wins because it recovers lost data). No other new nudges in this plan; further graph growth waits for Phase 7 evidence.

**File ownership:** Modify `src/Miller.Server/Tools/InspectTool.cs`; Test `tests/Miller.Tests/Server/InspectToolTests.cs`

**Serialization required:** Yes

**Dependency reason:** Depends on T4.2 wording being final so hints match descriptions.

**Acceptance criteria:**
- [ ] Truncated-refs inspect responses carry the trace nudge; exactly one `next:` line per response in all cases.
- [ ] Focused InspectToolTests pass; handed to lead per commit mode.

# Phase 5 — Injection-only session hooks (Claude Code + Codex plugins)

Policy note: this softens the repo's no-hooks stance to "prompt-injection-only hooks, fail-open, never blocking" — approved by approving this plan. Non-hook harnesses keep today's behavior. **Hooks reach no user until T5.5 delivers a release — the Phase 7 adoption clock starts there, not at merge.**

### Task T5.1: Routing block with budget + drift gates

**Files:**
- Create: `hooks/miller-routing-block.md`, `tests/plugin/hooks-routing-block.test.cjs`

**Interfaces:**
- Consumes: T4.1's finalized instruction-core wording.
- Produces: a ≤3,000-char routing block: the one-line-per-tool routing table (superset of the instruction core's), the six rules in affirmative-redirect voice, and the `workspace onboarding` pointer. This is the payload T5.2 emits and T6.1 embeds.

**Contract inputs:** hard 3,000-char budget test; canary test asserting each of the nine tool names + its routing line's key verb appears in BOTH this file and `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` (ponytail's canary pattern, re-implemented) so the surfaces can't drift silently.

**File ownership:** Create `hooks/miller-routing-block.md`, `tests/plugin/hooks-routing-block.test.cjs`

**Serialization required:** Yes

**Dependency reason:** Canary cross-checks the instruction core; needs T4.1's final wording.

**Acceptance criteria:**
- [x] Block ≤3,000 chars, gated by test; canary test cross-checks the nine routing lines against the embedded instruction core file.
- [x] `node --test tests/plugin/hooks-routing-block.test.cjs` passes.

### Task T5.2: Session hook script + shared hooks manifest (explicit event argument)

**Files:**
- Create: `hooks/miller-session-hook.cjs`, `hooks/claude-codex-hooks.json`
- Test: `tests/plugin/hooks-session-hook.test.cjs`

**Interfaces:**
- Consumes: `hooks/miller-routing-block.md` (T5.1).
- Produces: one emitter script invoked as `node miller-session-hook.cjs <event>` where `<event>` ∈ `session-start` (this task) / `subagent-start` (T5.4). The hooks manifest registers each event **separately** with the event passed as an explicit argument — no env-var host sniffing, no stdin-shape inference. SessionStart matcher `startup|resume|clear|compact`.

**Contract inputs:** Global hook constraints (injection-only, fail-open, exit 0 on every failure, ≤1s stdin timeout with `unref` that emits anyway, no network/binary/index access, `MILLER_SESSION_HOOKS=0` opt-out). **Payload-shape verification is part of this task** (razorback:grounding-in-current-docs): secondhand claims conflict — ponytail's shipped fixes imply Codex needs `hookSpecificOutput` JSON while the 2026-07 Codex docs reportedly accept plain stdout for both events. Verify against current official Claude Code + Codex hook docs (and a live smoke where available), implement the verified shape per (host × event), and record the verified facts + doc URLs in the task findings. Windows variant command guarded on Node presence so a missing runtime degrades silently.

**File ownership:** Create `hooks/miller-session-hook.cjs`, `hooks/claude-codex-hooks.json`; Test `tests/plugin/hooks-session-hook.test.cjs`

**Serialization required:** Yes

**Dependency reason:** Consumes T5.1's routing block file.

**Acceptance criteria:**
- [x] Script emits the verified payload shape per host×event, selected by the explicit event argument (unit-tested by invoking the script with each argument and faked stdin); every failure path (missing block file, wedged stdin, bad payload, `MILLER_SESSION_HOOKS=0`) exits 0 — silently for opt-out/errors, within 1s for wedged stdin.
- [x] Verified host payload facts + doc URLs recorded in task findings.
- [x] `node --test` passes.

### Task T5.3: Wire hooks into the Claude Code and Codex plugin manifests

**Files:**
- Modify: `.claude-plugin/plugin.json`, `.codex-plugin/plugin.json`, `tests/plugin/plugin-manifest.test.cjs`, `README.md` (plugin-install section: document the hook, what it injects, and the `MILLER_SESSION_HOOKS=0` opt-out)

**Interfaces:**
- Consumes: `hooks/claude-codex-hooks.json` (T5.2).
- Produces: both manifests reference the shared hooks file; manifest test asserts the hooks path resolves, the JSON parses, every referenced script exists, every hook command passes an explicit event argument, and the event set is exactly `SessionStart`/`SubagentStart` (guardrail: any other event fails the test).

**File ownership:** Modify `.claude-plugin/plugin.json`, `.codex-plugin/plugin.json`, `tests/plugin/plugin-manifest.test.cjs`, `README.md`

**Serialization required:** Yes

**Dependency reason:** Consumes T5.2's hooks JSON.

**Acceptance criteria:**
- [x] `scripts/test-plugin.sh` passes including the new manifest assertions; README documents the hook + opt-out env var.
- [x] Cursor manifest intentionally unchanged (Cursor hook support unverified — instruction-tier covers it via Phase 6; revisit only with evidence).

### Task T5.4: SubagentStart re-injection

**Files:**
- Modify: `hooks/claude-codex-hooks.json`, `hooks/miller-session-hook.cjs` (same emitter, `subagent-start` argument)
- Test: `tests/plugin/hooks-session-hook.test.cjs`

**What to build:** Register the emitter for SubagentStart with its own explicit argument and the payload shape verified in T5.2 for that host×event (ponytail's experience says Claude Code drops SubagentStart raw stdout and requires `hookSpecificOutput.additionalContext` — verify, don't assume). Subagents are the heaviest built-in-grep users; this is the adoption payload reaching them.

**File ownership:** Modify `hooks/claude-codex-hooks.json`, `hooks/miller-session-hook.cjs`; Test `tests/plugin/hooks-session-hook.test.cjs`

**Serialization required:** Yes

**Dependency reason:** Extends T5.2/T5.3 artifacts.

**Acceptance criteria:**
- [x] SubagentStart emission uses the verified shape; SessionStart behavior unchanged; failure paths still exit 0.
- [x] `scripts/test-plugin.sh` passes.

### Task T5.5: Hook delivery gate — release, install, trust, smoke (**approval-gated**)

**Files:**
- Modify: version-bearing manifests per the release process (`Directory.Build.props`, `miller-plugin.json`, plugin manifests ×4, marketplace ×2)
- Create: `docs/findings/<date>-hook-delivery-verification.md`

**What to build:** Hooks ship to nobody until a release exists: the marketplace serves manifests from `origin/main` HEAD and the launcher downloads `releases/download/v<version>/…`, and Codex additionally gates bundled hooks behind a user trust review. This task, run only with **explicit user approval** in a single session per the release rule: (1) version-bump all manifests (alignment test green); (2) push + publish the GitHub release and verify assets; (3) install/update the plugin on Claude Code and Codex locally; (4) complete the Codex trust flow and confirm the hook actually fires (fresh session shows the routing block; spawn a subagent and confirm SubagentStart injection); (5) verify `MILLER_SESSION_HOOKS=0` suppresses both; (6) record all evidence in the findings doc. **T7.2's measurement clock starts at this task's completion.**

**File ownership:** Version-bearing manifests per release process; Create `docs/findings/<date>-hook-delivery-verification.md`

**Serialization required:** Yes

**Dependency reason:** Requires T5.1–T5.4 merged and all gates green; push/publish requires explicit user approval.

**Acceptance criteria:**
- [ ] Release published with verified assets; both plugins updated; hook observed firing on SessionStart AND SubagentStart in live sessions on both hosts (or a documented host limitation).
- [ ] Opt-out verified. Findings doc records versions, evidence, and the T7.2 clock start date.

# Phase 6 — Instruction-tier harness expansion

### Task T6.1: `miller rules` CLI verb (embedded routing block)

**Files:**
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`, `src/Miller.Server/Miller.Server.csproj` (add `hooks/miller-routing-block.md` as an `EmbeddedResource`, same mechanism as `MILLER_AGENT_INSTRUCTIONS.md` — packaged binaries have no repo checkout)
- Create: render helper beside the other CLI verbs + `docs/contracts/rules-v1.md`
- Test: `tests/Miller.Tests/Cli/` (follow the existing CLI verb test pattern)

**Interfaces:**
- Consumes: `hooks/miller-routing-block.md` (T5.1) as the canonical text, embedded at build time.
- Produces: `miller rules` prints the routing block; `miller rules --harness <name>` wraps it in that harness's file format and prints the target path convention. **Supported harness list is pinned at implementation time**: for each candidate (cursor `.mdc` + `alwaysApply: true` frontmatter, windsurf, cline, kiro, copilot-instructions, generic `AGENTS.md` append-block), verify the format against current official docs (razorback:grounding-in-current-docs), record the doc URL in `docs/contracts/rules-v1.md`, and **drop any harness whose format cannot be verified** rather than guessing. Print-only — no file writes into user projects.

**Contract inputs:** no new MCP tool (CLI-only per the stinginess rule); `version`/`help`-class verb — must not load an index; the embedded-resource copy is what ships, so the CLI works identically from a release archive (test must load the resource, not the repo file).

**File ownership:** Modify `src/Miller.Server/Cli/CliDispatch.cs`, `src/Miller.Server/Miller.Server.csproj`; Create render helper + `docs/contracts/rules-v1.md`; Test `tests/Miller.Tests/Cli/*`

**Serialization required:** Yes

**Dependency reason:** Consumes final T4.1 wording + T5.1 routing block as embedded source text.

**Acceptance criteria:**
- [x] Each shipped `--harness` variant has a verified format with doc URL recorded in the contract doc; unverifiable harnesses documented as dropped.
- [x] Bare verb prints the embedded block; verb dispatch happens before workspace hydration (no index load, asserted by test pattern used for `version`).
- [x] Resource-embedding test proves the block loads from the compiled assembly, not the repo path; fast suite passes.

### Task T6.2: Document the instruction-tier install paths

**Files:**
- Modify: `README.md` (install section), `docs/README.md` (map entry)

**What to build:** A short "other harnesses" install section: MCP config for any MCP-speaking harness + `miller rules --harness <name>` for the guidance file, with two-tier honesty framing (what instruction-tier installs lose: hooks, skills). Mirrors ponytail's portability-matrix candor. Documents only the harnesses T6.1 actually shipped.

**File ownership:** Modify `README.md`, `docs/README.md`

**Serialization required:** Yes

**Dependency reason:** Documents T6.1 output; runs after T6.1.

**Acceptance criteria:**
- [x] README documents plugin-tier vs instruction-tier support and exact per-harness steps for shipped harnesses only.

# Phase 7 — Measurement and follow-up (report-only gates)

### Task T7.1: Edit-failure telemetry review (date-gated: not before 2026-07-28)

**Files:**
- Create: `docs/findings/<date>-edit-failure-telemetry.md`

**What to build:** Aggregate ≥2 weeks of 1.9.0 `edit_failure_reason` data (`~/.miller/telemetry.db`, `tool='edit' AND outcome='error'`). Decide follow-up: if `stale_target` dominates, spec an auto-refresh-and-retry slice; if `target_not_found`, spec candidate-suggestion parity with inspect. The 26% error rate is the single largest per-tool quality gap. Independent of T7.2's release gate.

**File ownership:** Create `docs/findings/<date>-edit-failure-telemetry.md`

**Serialization required:** Yes

**Dependency reason:** Needs ≥2 weeks of 1.9.0 `edit_failure_reason` data (from ~2026-07-28); independent of T7.2.

**Acceptance criteria:**
- [ ] Findings doc with reason split + a go/no-go recommendation for the follow-up slice.

### Task T7.2: Post-ship adoption re-measure (release-gated: T5.5 + 2 weeks)

**Files:**
- Create: `docs/findings/<date>-agent-interaction-remeasure.md`

**What to build:** Re-run the audit aggregations over a ≥2-week window starting at T5.5's delivery verification. Quantitative success criteria (report-only, drive the next plan): text-mode search empty rate < 30% (from ~40%) with `mode_mismatch`+`query_shape` share materially down; content error rate < 5% (from 13.8%); non-inspect/search tool share > 25% (from 19%). Compare same-workspace cohorts pre/post to reduce workload-mix confounds and state the confound honestly in the doc. (The earlier qualitative "grep displacement" criterion is dropped — Miller telemetry cannot observe built-in tool usage and no transcript-cohort method is defined; revisit only if a measurement method is designed first.)

**File ownership:** Create `docs/findings/<date>-agent-interaction-remeasure.md`

**Serialization required:** Yes

**Dependency reason:** Clock starts at T5.5 delivery verification, not merge; needs ≥2 weeks of post-release telemetry.

**Acceptance criteria:**
- [ ] Findings doc comparing pre/post windows on the three quantitative criteria, with confounds stated and next-step recommendations.

---

## Explicitly Out of Scope

- Blocking/deny/modify hooks of any kind (context-mode's enforcement layer) — rejected on evidence: their history is a catalog of walk-backs and host-degradation hazards.
- JSON output enrichment for empty results — empty search JSON stays `[]` within this plan to keep it single-repo; since Eros is owned in-house, a follow-up plan may break the shape with a coordinated Eros update and contract rev.
- Widening default symbol ranking or any ranking change — stays benchmark-gated per the existing work-queue rule; this plan attacks empties via recovery output, not ranking.
- New MCP tools; semantic/embedding anything (Eros boundary).
- Copying code from context-mode (Elastic-2.0). Ponytail patterns are re-implemented from behavior, not lifted.
- Cursor hooks (support unverified) and per-turn re-injection (nag risk) — revisit only with Phase 7 evidence.
- `content remove` selector loosening (destructive ops keep strict `source_id`).

## Revision History

- **v2 (2026-07-16):** Reworked after Codex adversarial plan review. Accepted: JSON-freeze for empty results (empty search JSON is literal `[]` — verified `SearchTool.cs:740,:750`); re-baselined T1.3/T2.1/T2.2 against already-shipped display_path resolution, kind aliases, and file-path fragment matching (verified in code); exact clamp semantics for T2.2; `EditService.cs:823` anchor for T3.3; serialized T3.4→T4.2, moved T3.5 after T4.2, made T5.1 depend on T4.1; added empty-output line/char budget and replace-don't-stack rule; explicit-event-argument hook design (no env sniffing) with in-task live verification of host payload shapes; added approval-gated T5.5 delivery/trust/smoke gate and tied T7.2's clock to it; embedded-resource packaging for T6.1; dropped T7.2's unmeasurable qualitative criterion. Partially rejected: Codex's claim that both hosts accept plain stdout for both events is treated as unverified (conflicts with ponytail's shipped fixes) — resolved empirically in T5.2 rather than assumed either way.
- **v1 (2026-07-16):** Initial plan from the six-topic audit.
