# Trace, Content, And Patterns Quality Goal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make `trace`, `content`, and `patterns` more useful and more naturally adopted by agents without adding MCP tools.

**Architecture:** Keep the existing tool boundaries and improve each tool at the caller-facing interface: compact recovery text, JSON `next_actions` parity where useful, telemetry empty/error classification, and prompt/docs guidance. This is not a semantic/vector slice and not a new-tool slice; Miller stays deterministic and surface-stingy while Eros remains the home for fleet-level semantic evaluation and orchestration.

**Tech Stack:** .NET 10, existing `Miller.Server.Tools` MCP/CLI cores, SQLite-backed Miller artifacts, xUnit fast tests, Miller skills under `.agents/skills/`, generated `skills/` mirror, README/GitHub Pages docs, and `scripts/bench-foundation-matrix.py` evidence rows.

**Architecture Quality:** Affected modules are `TraceTool`, `ContentTool`, `PatternsTool`, onboarding guidance, and prompt/docs surfaces. Caller-facing interfaces stay inside the existing `trace`, `content`, and `patterns` MCP/CLI contracts; additive JSON fields are allowed only when documented and tested through the public output. Architecture risk is medium because this changes high-level agent behavior across three tools, but locality is good if each task keeps recovery formatting inside the owning tool and proves behavior through public tool calls.

## Global Constraints

- Do not add MCP tools.
- Do not add semantic/vector retrieval to Miller.
- Do not move parser recognition into Miller; `patterns` must remain generic over `structural_facts` emitted by `julie-extractors`.
- Keep JSON changes additive and contract-documented when the output is part of CLI/Eros-facing surfaces.
- Keep compact output bounded and scan-friendly; no full raw logs, no unbounded source windows, and no broad file dumps.
- Preserve existing `trace`, `content`, and `patterns` successful-path behavior unless a task explicitly changes an output format and updates tests.
- Do not store raw queries or raw targets in telemetry. Aggregate tool/op/outcome/empty-reason metadata is allowed.
- Keep `.agents/skills/` canonical and regenerate `skills/` with `scripts/sync-plugin-skills.sh`.
- If `CLAUDE.md` changes, regenerate `AGENTS.md` with `scripts/sync-agents.sh` and verify byte-for-byte sync.
- Keep `MILLER_AGENT_INSTRUCTIONS.md` and tool descriptions under existing test budgets.
- Every implementation task follows TDD: write or update the failing assertion first, verify it fails for the expected reason, then implement the smallest passing change.

---

## Baseline Evidence

Live aggregate telemetry from `miller telemetry export --jsonl` on 2026-06-28 showed:

| tool | calls | ok | empty | error | read |
|---|---:|---:|---:|---:|---|
| `trace` | 343 | 164 | 177 | 2 | High empty rate; enough usage to matter. |
| `content` | 466 | 288 | 136 | 42 | Useful, but `content:read` has 28 errors out of 90 calls. |
| `patterns` | 39 | 29 | 9 | 1 | Low usage; likely discoverability and specialization, not proof of uselessness. |

Important op-level signals:

- `trace:path`: 18 calls, 0 ok, 18 empty.
- `trace:bridge`: 11 calls, 0 ok, 11 empty.
- `trace:refs`: 103 calls, 61 ok, 42 empty.
- `content:search`: 296 calls, 177 ok, 117 empty, 2 error.
- `content:read`: 90 calls, 62 ok, 28 error.
- `patterns:search`: 20 calls, 12 ok, 8 empty.
- `patterns:list`: 8 calls, 8 ok.

Interpretation:

- `trace` is the strongest implementation/recovery target because agents call it and frequently hit empty outcomes.
- `content` is useful but has too many read errors and terse no-result output.
- `patterns` is probably under-discovered; successful list/search behavior exists, but compact output and guidance do not yet make the next action obvious enough for agents.

## File Structure

- `src/Miller.Server/Tools/TraceTool.cs` - trace empty states, next actions, compact/JSON parity, telemetry empty reasons.
- `src/Miller.Server/Tools/ContentTool.cs` - content search/read recovery text, structured JSON error/no-result output, telemetry error/empty reasons.
- `src/Miller.Server/Tools/PatternsTool.cs` - pattern list/search/summary discoverability, query/no-match recovery, JSON next-action parity.
- `src/Miller.Server/Tools/WorkspaceOnboardingFacts.cs` - aggregate hints for trace/content/patterns adoption and friction.
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` - server-level workflow guidance.
- `tests/Miller.Tests/Tools/TraceToolTests.cs` - trace compact/JSON recovery tests.
- `tests/Miller.Tests/Server/ContentToolTests.cs` - content compact/JSON recovery tests.
- `tests/Miller.Tests/Server/PatternsToolTests.cs` - patterns compact/JSON discoverability tests.
- `tests/Miller.Tests/Server/WorkspaceRenderTests.cs` - onboarding output tests.
- `tests/Miller.Tests/Server/AgentInstructionsTests.cs` - server instruction and tool-description budget/content tests.
- `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` - CLI JSON contract coverage when shared output changes affect CLI read commands.
- `.agents/skills/miller-orientation/SKILL.md` - general routing guidance for all three tools.
- `.agents/skills/miller-explore-area/SKILL.md` - exploration workflow guidance.
- `.agents/skills/miller-text-audit/SKILL.md` - content search/read workflow guidance.
- `.agents/skills/miller-patterns-audit/SKILL.md` - patterns workflow guidance.
- `.agents/skills/miller-bridge-trace/SKILL.md` - bridge/trace workflow guidance.
- `skills/` - generated mirror from `.agents/skills/`.
- `README.md` - public workflow copy.
- `docs/site/index.html` - GitHub Pages workflow copy.
- `docs/README.md` - docs map entry for this plan and evidence.
- `docs/contracts/cli-eros-v1.md` - update only for additive JSON contract fields that Eros may consume.
- `scripts/benchmarks/miller-foundation-cases.json` - add focused workflow/contract rows if existing rows cannot prove the behavior.
- `scripts/bench-foundation-matrix.py` - modify only if the existing runner cannot score the new focused rows.
- `docs/findings/benchmarks/2026-06-28-trace-content-patterns-quality/` - generated focused evidence output.
- `docs/findings/2026-06-28-trace-content-patterns-quality-baseline.md` - investigation summary and before/after comparison.

## Task 1: Build The Focused Baseline And Replay Matrix

**Files:**
- Create: `docs/findings/2026-06-28-trace-content-patterns-quality-baseline.md`
- Create: `docs/findings/benchmarks/2026-06-28-trace-content-patterns-quality/`
- Modify: `scripts/benchmarks/miller-foundation-cases.json` only if existing rows cannot cover the focused workflows.
- Modify: `scripts/bench-foundation-matrix.py` only if no existing scorer can validate required anchors or JSON parseability.

**Interfaces:**
- Consumes: `miller telemetry export --jsonl`, existing MCP/CLI read tools, existing foundation matrix runner.
- Produces: a before/after evidence baseline and focused replay rows for trace/content/patterns behavior.

**What to build:** Create the hard evidence harness for this goal before changing behavior. The matrix must prove recovery guidance and parseability, while usage/adoption rates remain report-only.

**Approach:** Add focused rows for:

- `trace.path.no_path.next_actions`: compact and JSON output include mode-specific next actions.
- `trace.refs.empty.next_actions`: compact and JSON output guide source search and scoped inspect.
- `trace.bridge.unsupported_or_no_links.next_actions`: compact and JSON output distinguish provider/capability gaps from true no-link outcomes.
- `content.search.no_results.recovery`: compact and JSON output give content-kind/workspace/search rerun guidance.
- `content.read.missing_source.recovery`: compact and JSON output guide `content search`, `content list`, and `workspace_id` usage.
- `patterns.list.next_actions`: list output suggests concrete search/summary commands from observed pattern IDs.
- `patterns.search.no_match.recovery`: compact and JSON output suggest list/query/near-match next actions.

**Acceptance criteria:**
- [ ] Baseline doc records current telemetry counts for `trace`, `content`, and `patterns`.
- [ ] Focused matrix rows exist for each tool's main recovery workflow.
- [ ] Hard gates cover parseability and required recovery anchors, not adoption-rate targets.
- [ ] Usage/adoption numbers are explicitly report-only.
- [ ] No new MCP tools or telemetry raw-query/raw-target fields are introduced.
- [ ] Worker-scope verification passes and is committed.

## Task 2: Make Trace Empty States Actionable And Measurable

**Files:**
- Modify: `src/Miller.Server/Tools/TraceTool.cs`
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs`
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`
- Modify: `docs/contracts/cli-eros-v1.md` if JSON `next_actions` shape changes are documented for CLI use.

**Interfaces:**
- Consumes: existing trace modes `auto`, `refs`, `path`, `bridge`; existing `TraceNextAction` shape; existing compact and JSON renderers.
- Produces: richer compact/JSON next actions and more specific telemetry empty reasons.

**What to build:** Keep trace graph semantics honest while reducing dead ends. Empty outcomes should tell an agent what to try next and why, using tool calls that already exist.

**Approach:**

- `path` no-path should always include a text-search fallback, even when source/destination refs and a depth bump are also shown. If compact output needs more than three actions, raise the compact action cap deliberately and test it.
- `refs` empty should move from a prose-only hint to the same `TraceNextAction` mechanism used by ambiguous/no-path cases. JSON output must include `next_actions`.
- `auto` no-neighbours should expose structured next actions for `search mode=source`, `inspect depth=overview` on same-file candidates, and `trace mode=refs` when useful.
- `bridge` no-provider/no-links output should distinguish unsupported provider/capability gaps from no links within depth and include next actions such as `patterns query=route`, `trace mode=refs`, or `search mode=source` only when those are relevant.
- Telemetry empty reasons should become specific enough to separate `no_path`, `no_references`, `no_neighbours`, `bridge_no_provider`, `bridge_no_links`, and `unsupported`.

**Acceptance criteria:**
- [ ] `trace mode=path` no-path compact output includes source refs, destination refs, bounded depth bump, and source text search next actions.
- [ ] `trace mode=path` no-path JSON output includes equivalent `next_actions`.
- [ ] `trace mode=refs` empty compact output includes copyable next actions, not only prose.
- [ ] `trace mode=refs` empty JSON output includes equivalent `next_actions`.
- [ ] `trace mode=auto` no-neighbour compact and JSON output include useful next actions and same-file context when available.
- [ ] `trace mode=bridge` unsupported/no-links outcomes keep provider honesty and include relevant fallbacks.
- [ ] Telemetry empty reasons distinguish the major trace empty classes.
- [ ] Tool description and server instructions stay under budget and mention trace recovery accurately.
- [ ] `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~TraceToolTests|FullyQualifiedName~AgentInstructionsTests"` passes.
- [ ] Worker-scope verification passes and is committed.

## Task 3: Reduce Content Read Errors And Improve Content Recovery

**Files:**
- Modify: `src/Miller.Server/Tools/ContentTool.cs`
- Test: `tests/Miller.Tests/Server/ContentToolTests.cs`
- Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` if CLI JSON output changes.
- Modify: `docs/contracts/cli-eros-v1.md` if structured JSON errors or next actions are exposed through CLI contracts.

**Interfaces:**
- Consumes: existing content operations `import`, `add_markdown`, `search`, `read`, `list`, `remove`, `export`.
- Produces: recoverable read/search failures and clearer compact/JSON output.

**What to build:** Make content a reliable large-text workflow for agents. `content search` should lead naturally to `content read`, and failed reads should explain how to recover without guessing.

**Approach:**

- Replace terse compact search no-results (`No results.`) with bounded recovery guidance:
  - try the right `content_kind` (`docs`, `source`, `external_file`, `web`, or `all-text`);
  - use `workspace_id=all` only for registered workspace audits;
  - use `search mode=source` when the user is looking for current workspace source-body text.
- For `content read` missing `source_id`, missing `line`, unknown source, ambiguous display path, stale workspace source, and overlarge context windows, return clear compact recovery text.
- For `format=json`, return parseable structured errors with `operation`, `error`, `diagnostic_code`, and `next_actions` instead of plain `content failed: ...` when feasible.
- Preserve existing successful search/read compact shape, including `source_id` in each hit and the `read:` footer.
- Improve telemetry classification for `content:read` errors and `content:search` no-results using specific error/empty reasons.

**Acceptance criteria:**
- [ ] `content search` no-results compact output gives bounded rerun guidance.
- [ ] `content search` no-results JSON output is parseable and includes equivalent recovery fields.
- [ ] `content read` missing/unknown source compact output suggests `content search` and `content list`.
- [ ] `content read` ambiguous display path compact output keeps candidate source IDs and explains how to choose one.
- [ ] `content read` JSON errors are parseable and include diagnostic codes.
- [ ] Existing successful search/read output remains source-id driven and bounded.
- [ ] Telemetry distinguishes no-results from read parameter/source/window errors.
- [ ] `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~ContentToolTests|FullyQualifiedName~CliDispatchTests"` passes for affected tests.
- [ ] Worker-scope verification passes and is committed.

## Task 4: Make Patterns Discoverable And Workflow-Shaped

**Files:**
- Modify: `src/Miller.Server/Tools/PatternsTool.cs`
- Test: `tests/Miller.Tests/Server/PatternsToolTests.cs`
- Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` if CLI JSON output changes.
- Modify: `docs/contracts/cli-eros-v1.md` if additive JSON fields are contract-visible.

**Interfaces:**
- Consumes: existing `patterns operation=list|summary|search`, generic `pattern_id`, `query`, `where`, `path`, and `language` filters.
- Produces: next-action guidance that turns structural facts into usable follow-up workflows.

**What to build:** Keep patterns generic, but make the compact and JSON output teach agents how to use observed facts. Low usage should be addressed by discoverability and examples before any new capability is invented.

**Approach:**

- Add a compact `next:` footer to `patterns operation=list` using observed pattern IDs from the output:
  - `patterns operation=search pattern_id=<top-id>`;
  - `patterns operation=summary pattern_id=<top-id>`;
  - `patterns operation=search query=<domain-term>` when obvious domain terms such as `route`, `html`, `json`, `yaml`, or `markdown` are present.
- Add JSON `next_actions` to list/no-match outputs when `format=json`.
- Improve `patterns operation=search query=<term>` no-match recovery with near-match pattern IDs and `patterns operation=list` guidance.
- Improve search-by-pattern no-match output when the pattern ID exists but filters exclude every row: name the active filters and suggest loosening `path`, `language`, or `where`.
- Keep pattern recognition generic. Do not special-case ASP.NET/htmx/SQL beyond examples derived from observed `pattern_id` values.

**Acceptance criteria:**
- [ ] `patterns operation=list` compact output includes concrete next actions derived from observed pattern IDs.
- [ ] `patterns operation=list --json` includes parseable `next_actions`.
- [ ] `patterns search query=<miss>` compact and JSON output include recovery guidance and near matches when available.
- [ ] `patterns search pattern_id=<id>` with filters that remove all rows distinguishes no facts from filtered-out facts.
- [ ] Existing search/list/summary successful output remains bounded and generic over `pattern_id`.
- [ ] `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~PatternsToolTests|FullyQualifiedName~CliDispatchTests"` passes for affected tests.
- [ ] Worker-scope verification passes and is committed.

## Task 5: Surface Tool-Specific Guidance In Onboarding, Skills, And Public Docs

**Files:**
- Modify: `src/Miller.Server/Tools/WorkspaceOnboardingFacts.cs`
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Test: `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`
- Modify: `.agents/skills/miller-orientation/SKILL.md`
- Modify: `.agents/skills/miller-explore-area/SKILL.md`
- Modify: `.agents/skills/miller-text-audit/SKILL.md`
- Modify: `.agents/skills/miller-patterns-audit/SKILL.md`
- Modify: `.agents/skills/miller-bridge-trace/SKILL.md`
- Generated: `skills/`
- Modify: `README.md`
- Modify: `docs/site/index.html`
- Modify: `docs/README.md`

**Interfaces:**
- Consumes: aggregate telemetry facts from `WorkspaceOnboardingFacts`; canonical skill files under `.agents/skills/`.
- Produces: guidance that routes agents to the right underused tool at the right time.

**What to build:** Improve adoption guidance after the tools have better recovery behavior. Guidance must teach when to use each tool, not simply advertise that it exists.

**Approach:**

- Onboarding notes should be aggregate-only and specific:
  - if trace empties are high, suggest `trace mode=refs` and `search mode=source` fallback;
  - if content read errors are high, remind agents to use the `source_id` from `content search` and pass `workspace_id` when reading cross-workspace hits;
  - if patterns use is low but structural facts are present, suggest `patterns operation=list` before raw AST or route grepping.
- Server instructions should include compact recipes:
  - `trace`: refs/path/bridge and how to recover from no-path;
  - `content`: import/search/read flow and source-id discipline;
  - `patterns`: list/search/summary flow for structural facts.
- Skills should stay short and task-specific. Do not duplicate the whole README in every skill.
- README/site should show the three tools as workflow tools, not extra features.

**Acceptance criteria:**
- [ ] Onboarding compact and JSON output can surface trace/content/patterns guidance using aggregate telemetry only.
- [ ] Server instructions document the improved recovery workflows without exceeding budgets.
- [ ] Skills are updated in `.agents/skills/`, mirrored to `skills/`, and pass plugin mirror tests.
- [ ] README and GitHub Pages explain the intended workflows without implying new tools or semantic retrieval.
- [ ] `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkspaceRenderTests|FullyQualifiedName~AgentInstructionsTests"` passes.
- [ ] `scripts/sync-plugin-skills.sh && diff -qr .agents/skills skills` passes.
- [ ] `node --test tests/plugin/plugin-manifest.test.cjs` passes.
- [ ] Worker-scope verification passes and is committed.

## Task 6: Focused Evidence, Final Regression Gate, And Goal Closeout

**Files:**
- Modify/Create: `docs/findings/benchmarks/2026-06-28-trace-content-patterns-quality/summary.md`
- Modify/Create: `docs/findings/benchmarks/2026-06-28-trace-content-patterns-quality/results.csv`
- Modify/Create: `docs/findings/benchmarks/2026-06-28-trace-content-patterns-quality/results.json`
- Modify/Create: `docs/findings/benchmarks/2026-06-28-trace-content-patterns-quality/calibration.md`
- Modify: `docs/findings/2026-06-28-trace-content-patterns-quality-baseline.md`
- Modify: `docs/plans/2026-06-28-trace-content-patterns-quality-goal-plan.md`
- Create: `.memories/<date>/<checkpoint>.md`

**Interfaces:**
- Consumes: focused matrix rows, committed tool changes, and aggregate telemetry export.
- Produces: final evidence and a completed goal plan.

**What to build:** Prove the goal improved tool usefulness in deterministic, replayable ways. Do not treat usage-rate movement as a hard gate in the same run; adoption requires time and repeated dogfood.

**Approach:**

Run focused evidence after all behavior/docs slices:

```bash
python3 scripts/bench-foundation-matrix.py \
  --repos miller \
  --tasks trace,content,patterns \
  --skip-julie \
  --out-dir docs/findings/benchmarks/2026-06-28-trace-content-patterns-quality \
  --gate
```

If the runner cannot select by those task names, use the exact task IDs added in Task 1. If existing runner semantics cannot express the checks, update the runner in the narrowest way and document the hard-gate fields.

**Acceptance criteria:**
- [ ] Focused matrix gates pass for trace recovery anchors and JSON parseability.
- [ ] Focused matrix gates pass for content recovery anchors and JSON parseability.
- [ ] Focused matrix gates pass for patterns next-action anchors and JSON parseability.
- [ ] Baseline doc records before/after behavior and marks adoption metrics report-only.
- [ ] Plan checklist is updated only after evidence exists.
- [ ] Goldfish checkpoint records what changed, why, verification evidence, and remaining follow-up.
- [ ] `scripts/test.sh` passes.
- [ ] `git diff --check` passes.
- [ ] Final local commit includes code, tests, docs, generated evidence, skill mirrors, and checkpoint.

## Verification Strategy

**Project source of truth:** `AGENTS.md` testing section, `CLAUDE.md` generated-mirror rule, `tests/Miller.Tests/Miller.Tests.csproj`, `tests/plugin/plugin-manifest.test.cjs`, `docs/contracts/cli-eros-v1.md`, and existing foundation matrix runner conventions.

**Worker red/green scope:**
- Trace: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~TraceToolTests"`
- Content: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~ContentToolTests"`
- Patterns: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~PatternsToolTests"`
- Onboarding/instructions: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkspaceRenderTests|FullyQualifiedName~AgentInstructionsTests"`
- Skill sync: `scripts/sync-plugin-skills.sh && diff -qr .agents/skills skills`
- Plugin manifest: `node --test tests/plugin/plugin-manifest.test.cjs`

**Worker ceiling:** Workers may run focused xUnit filters, CLI tests for changed contract output, skill sync checks, plugin manifest tests, Python JSON/CSV parse checks, and focused matrix rows for their assigned tool. Workers do not own release, push, publish, or unrelated scale tests.

**Worker gate invariant:** Each worker gate proves the changed tool's compact output, JSON output, telemetry classification, and guidance surfaces match the task acceptance criteria.

**Lead affected-change scope:** After each coherent tool batch, run:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~TraceToolTests|FullyQualifiedName~ContentToolTests|FullyQualifiedName~PatternsToolTests|FullyQualifiedName~WorkspaceRenderTests|FullyQualifiedName~AgentInstructionsTests|FullyQualifiedName~CliDispatchTests"
```

Run `CliDispatchTests` when any shared JSON output or CLI contract surface changes.

**Branch gate:** Run `scripts/test.sh` before final handoff or merge.

**Replay/metric evidence:** Focused matrix recovery anchors and JSON parseability are hard gates. Aggregate usage counts, empty rates, and post-change adoption interpretation are report-only until enough dogfood accumulates after a rebuild/restart.

**Escalation triggers:** Run `dotnet build Miller.slnx -c Release` if tool descriptions, public contracts, project files, or warning-prone code paths change. Run `scripts/test.sh scale` only if indexing/extraction paths, `julie-extract` subprocess usage, or `structural_facts` extraction assumptions change; this plan should not touch those areas.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the failure is the expected RED assertion for that task.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the final report or checkpoint. For focused matrix evidence, record hard-gate recovery/parseability results separately from report-only adoption metrics.

## Model Routing

**Project source of truth:** No `RAZORBACK.md` exists in this checkout. Use the current harness default and record `inherit` for all worker model choices unless the lead explicitly supplies a model map.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage.
- Harness mapping: inherit.

**Implementation tier:** bounded worker tasks from this clear plan.
- Harness mapping: inherit.

**Mechanical tier:** docs, generated skill mirrors, generated benchmark evidence, and rote contract copy.
- Harness mapping: inherit.

**Gate-interpretation reviewer:** reading failing assertions, focused matrix output, telemetry aggregates, or JSON contract diffs to decide whether the test or implementation is wrong.
- Harness mapping: inherit.

**Escalation tier:** subtle contract compatibility, repeated failures, high-empty telemetry interpretation, or changes that risk Eros-facing process contracts.
- Harness mapping: inherit.

**Worker eligibility:** Workers may implement a single tool slice when the task has isolated files and a focused test scope.

**Escalation triggers:** Any proposed new MCP tool, semantic/vector behavior, extractor contract change, raw telemetry query/target storage, or non-additive JSON contract change must return to the lead and user for approval.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, telemetry interpretation, replay evidence, or acceptance gates. Split docs-only mirror updates from evidence interpretation.

**Unsupported harness behavior:** If the harness cannot choose models per agent, use `inherit`, note it in the verification ledger, and continue.
