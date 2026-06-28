# Overview-First Agent Workflow Guidance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make Miller's existing `inspect depth=overview`, `context`, `trace`, and `impact` paths easier for agents to discover and use correctly before expensive full-body reads.

**Architecture:** Keep the MCP tool surface unchanged and keep `inspect`'s default depth as `summary`. This slice changes prompt-facing guidance, skill guidance, onboarding hints, public docs, and focused evidence so agents naturally route from `search` to `inspect depth=overview`, then to `trace`/`impact`/`depth=full` only when the task calls for them.

**Tech Stack:** .NET 10, existing `Miller.Server.Tools` MCP/CLI surfaces, `WorkspaceOnboardingFacts`, server instructions markdown, mirrored Miller skills, README/GitHub Pages docs, xUnit fast tests, `scripts/bench-foundation-matrix.py`.

**Architecture Quality:** Affected modules are prompt/docs surfaces plus `WorkspaceOnboardingFacts` aggregate guidance. Caller-facing interfaces stay inside existing `inspect`, `workspace onboarding`, `trace`, and `impact`; no new MCP tools, CLI commands, extractor contracts, telemetry fields, or raw-query storage are introduced. Architecture risk is low-medium because guidance changes affect high-traffic agent behavior, but implementation locality is strong and tests can pin the shipped text contracts without changing code-navigation semantics.

## Global Constraints

- Do not add MCP tools.
- Do not change `inspect`'s default depth; omitted depth remains `summary`.
- Preserve `depth=summary`, `depth=overview`, and `depth=full` behavior. This plan changes guidance and onboarding, not inspect rendering semantics.
- Do not store raw queries or raw targets in telemetry. Onboarding may use only existing aggregate facts: tool mix, op names, successful flows, common misses, friction, and current-index target recovery.
- Keep semantic/vector retrieval out of Miller; Eros owns those workflows.
- Keep `.agents/skills/` canonical and regenerate `skills/` with `scripts/sync-plugin-skills.sh`.
- If `CLAUDE.md` changes, regenerate `AGENTS.md` with `scripts/sync-agents.sh` and verify byte-for-byte sync.
- Keep server instructions under the existing `AgentInstructionsTests` budget.
- Keep tool descriptions under the existing per-description budget.
- Generated benchmark evidence belongs under `docs/findings/benchmarks/2026-06-27-foundation-matrix/overview-first-agent-guidance/`.
- Every task follows TDD: write or update the relevant failing assertion first, verify it fails for the expected reason, then implement the smallest passing change.

---

## File Structure

- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` - server-level agent guidance for choosing `inspect overview`, `trace`, and `impact`.
- `src/Miller.Server/Tools/InspectTool.cs` - inspect tool description text only; no depth default or rendering behavior change.
- `src/Miller.Server/Tools/WorkspaceOnboardingFacts.cs` - aggregate onboarding hint selection.
- `tests/Miller.Tests/Server/AgentInstructionsTests.cs` - prompt/tool-description budget and content assertions.
- `tests/Miller.Tests/Server/WorkspaceRenderTests.cs` - onboarding compact/JSON guidance assertions through the public renderer.
- `.agents/skills/miller-orientation/SKILL.md` - first-call tool-selection guidance.
- `.agents/skills/miller-explore-area/SKILL.md` - area-orientation guidance.
- `.agents/skills/miller-editing/SKILL.md` - edit-preparation guidance.
- `skills/` - generated mirror from `.agents/skills/`.
- `README.md` - public quickstart and workflow copy.
- `docs/site/index.html` - GitHub Pages tool/workflow copy.
- `docs/README.md` - docs map entry for the new evidence.
- `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md` - candidate status and evidence links.
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md` - candidate status update.
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.csv` - generated or manually aligned candidate status update.
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.json` - generated or manually aligned candidate status update.
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/overview-first-agent-guidance/` - focused evidence output.

## Task 1: Pin Overview-First Server And Tool Guidance

**Files:**
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Modify: `src/Miller.Server/Tools/InspectTool.cs`
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

**Interfaces:**
- Consumes: existing inspect depth names: `summary`, `overview`, `full`.
- Produces: prompt-facing guidance that says omitted `inspect depth` is `summary`, first symbol reads should use `depth=overview`, and `depth=full` is for complete bodies or complete relation lists.

**What to build:** Strengthen server instructions and the inspect tool description so agents stop treating `full` as the normal first read. Keep the current default and current behavior unchanged.

**Approach:** Add focused assertions before editing text. Pin the exact guidance concept rather than full paragraphs: default is `summary`; use `inspect depth=overview` before reading full bodies; use `depth=full` only for complete bodies or complete relation lists; use `trace` for refs/path questions and `impact` before refactors.

**Acceptance criteria:**
- [x] `AgentInstructionsTests` fails before text updates because overview-first guidance is missing or too weak.
- [x] Server instructions say default inspect depth is `summary`.
- [x] Server instructions route first symbol understanding through `inspect target depth=overview`.
- [x] Server instructions reserve `inspect depth=full` for complete body or complete relation needs.
- [x] Inspect tool description stays under `MaxToolDescriptionChars`.
- [x] `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentInstructionsTests"` passes.
- [x] Worker-scope verification passes, committed.

## Task 2: Update Miller Skill Guidance And Mirrors

**Files:**
- Modify: `.agents/skills/miller-orientation/SKILL.md`
- Modify: `.agents/skills/miller-explore-area/SKILL.md`
- Modify: `.agents/skills/miller-editing/SKILL.md`
- Generated: `skills/miller-orientation/SKILL.md`
- Generated: `skills/miller-explore-area/SKILL.md`
- Generated: `skills/miller-editing/SKILL.md`

**Interfaces:**
- Consumes: canonical `.agents/skills/` tree and `scripts/sync-plugin-skills.sh`.
- Produces: mirrored skill guidance that routes agents to overview-first inspect usage in local Codex/Claude/Cursor plugin surfaces.

**What to build:** Update the Miller skills agents actually read before using tools. The skill guidance should make `overview` the natural first symbol-read depth and make `full` an explicit escalation.

**Approach:** Edit only canonical `.agents/skills/` files, then run `scripts/sync-plugin-skills.sh`. Keep the guidance compact: the orientation table should distinguish "understand a symbol" from "need complete body"; explore-area should use overview before full; editing should use overview to choose/edit targets and full only when rewriting or auditing the complete body.

**Acceptance criteria:**
- [x] `miller-orientation` says `inspect(target="<symbol>", depth="overview")` is the first call for understanding a symbol.
- [x] `miller-orientation` says `inspect(target="<symbol>", depth="full")` is for complete body or complete relation needs.
- [x] `miller-explore-area` routes named symbols through `inspect depth=overview` before `depth=full`.
- [x] `miller-editing` tells agents to inspect overview before choosing edit targets and full before body rewrites when complete source context is needed.
- [x] `scripts/sync-plugin-skills.sh` has been run and `diff -qr .agents/skills skills` reports no differences.
- [x] `node --test tests/plugin/plugin-manifest.test.cjs` passes if the plugin test file exists in this checkout.
- [x] Worker-scope verification passes, committed.

## Task 3: Add Aggregate Onboarding Hints For Overview, Trace, And Impact

**Files:**
- Modify: `src/Miller.Server/Tools/WorkspaceOnboardingFacts.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`

**Interfaces:**
- Consumes: `TelemetryOnboardingFacts.ToolMix` rows with `Tool`, `Op`, `Calls`, `EmptyCount`, and `ErrorCount`; existing `WorkspaceOnboardingFacts.StartHere` and `Notes` arrays.
- Produces: compact and JSON onboarding guidance through existing `WorkspaceRender.Onboarding` output.

**What to build:** Add privacy-preserving onboarding hints that make the existing deterministic tools easier to discover. No raw telemetry values are added; hints are selected from aggregate tool/op counts.

**Approach:** Keep `start_here` short and deterministic. Add a generic start-here line for `inspect depth=overview` before full-body reads. Add or preserve `impact` before refactors as a generic line, not only when there is no telemetry or prior impact use. Add notes for low-use or expensive-use patterns using aggregate counts: when `inspect/full` calls exceed `inspect/overview` calls, note that overview should be the first read; when `trace` has no aggregate use in a non-empty telemetry window, note that trace is available for refs/path questions.

**Acceptance criteria:**
- [x] A no-telemetry onboarding render includes `inspect depth=overview` guidance.
- [x] A no-telemetry onboarding JSON render includes the same guidance in `start_here`.
- [x] An aggregate telemetry fixture with more `inspect/full` calls than `inspect/overview` calls includes a note telling agents to start with `inspect depth=overview`.
- [x] A non-empty telemetry fixture with no trace usage includes a note pointing refs/path questions to `trace`.
- [x] `WorkspaceOnboardingFacts` still reports the three existing privacy lines.
- [x] No telemetry reader schema, raw query storage, or target hash exposure changes are introduced.
- [x] `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkspaceRenderTests"` passes.
- [x] Worker-scope verification passes, committed.

## Task 4: Update Public Docs, Site, And Matrix Candidate Status

**Files:**
- Modify: `README.md`
- Modify: `docs/site/index.html`
- Modify: `docs/README.md`
- Modify: `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md`
- Modify: `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md`
- Modify: `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.csv`
- Modify: `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.json`

**Interfaces:**
- Consumes: current public docs layout and foundation matrix candidate language.
- Produces: public workflow guidance and updated candidate status without changing tool contracts.

**What to build:** Make the public docs explain the intended workflow: `search` for candidates, `inspect depth=overview` for first symbol understanding, `trace` for refs/path questions, `impact` before refactors, and `inspect depth=full` only when complete source is needed.

**Approach:** Keep README and site copy concise. Update the foundation finding so candidate 3 and candidate 6 point to this implementation evidence once generated. Preserve the existing Miller-vs-Julie framing: this is not Julie cloning and not an MCP surface expansion.

**Acceptance criteria:**
- [ ] README includes an overview-first workflow example or bullet sequence.
- [ ] GitHub Pages copy under `docs/site/index.html` describes `inspect overview` as the normal first symbol read.
- [ ] Docs map links the new evidence directory or plan.
- [ ] Foundation finding marks overview-first guidance as implemented after this slice.
- [ ] Adaptation candidate Markdown/CSV/JSON agree on candidate 3 and candidate 6 status.
- [ ] Docs do not claim `inspect full` is the default or preferred first read.
- [ ] `git diff --check` passes.
- [ ] Worker-scope verification passes, committed.

## Task 5: Generate Focused Foundation Evidence

**Files:**
- Create: `docs/findings/benchmarks/2026-06-27-foundation-matrix/overview-first-agent-guidance/summary.md`
- Create: `docs/findings/benchmarks/2026-06-27-foundation-matrix/overview-first-agent-guidance/results.csv`
- Create: `docs/findings/benchmarks/2026-06-27-foundation-matrix/overview-first-agent-guidance/results.json`
- Create: `docs/findings/benchmarks/2026-06-27-foundation-matrix/overview-first-agent-guidance/calibration.md`
- No benchmark code changes are expected for this task. Reuse existing rows for `inspect.overview` and `miller.contract.cli.workspace-onboarding`.

**Interfaces:**
- Consumes: existing foundation matrix rows for `inspect.overview` and `miller.contract.cli.workspace-onboarding`.
- Produces: focused evidence that this slice preserves overview inspect quality and onboarding contract parseability.

**What to build:** Reuse existing matrix rows to generate focused evidence. The hard gate is behavior preservation and parseability; usage interpretation remains report-only.

**Approach:** Run a focused matrix command after implementation:

```bash
python3 scripts/bench-foundation-matrix.py \
  --repos miller \
  --tasks inspect.overview,miller.contract.cli.workspace-onboarding \
  --skip-julie \
  --out-dir docs/findings/benchmarks/2026-06-27-foundation-matrix/overview-first-agent-guidance \
  --gate
```

If existing rows cannot prove the focused evidence, stop and report a plan mismatch instead of adding runner features or new manifest rows inside this task.

**Acceptance criteria:**
- [ ] Focused matrix gate passes for Miller `inspect.overview`.
- [ ] Focused matrix gate passes for Miller `workspace onboarding` JSON parseability.
- [ ] Generated summary records hard-gate results separately from report-only interpretation.
- [ ] No benchmark runner, scorer, or manifest changes are included in this slice.
- [ ] Worker-scope verification passes, committed.

## Task 6: Final Sync, Regression Gate, And Plan Closeout

**Files:**
- Modify: `docs/plans/2026-06-28-overview-first-agent-workflow-guidance-plan.md`
- Create: `.memories/<date>/<checkpoint>.md`

**Interfaces:**
- Consumes: completed task commits and verification evidence.
- Produces: checked-off plan acceptance criteria, Goldfish checkpoint, and a locally verifiable branch state.

**What to build:** Finish the slice as a clean local branch stack with synced generated files and recorded evidence.

**Approach:** Run the project fast suite and required sync checks after the focused tasks. Update this plan's task checkboxes only after the evidence exists. Save a Goldfish checkpoint before the final commit so future sessions can recover the implementation context.

**Acceptance criteria:**
- [ ] `scripts/sync-plugin-skills.sh` has been run.
- [ ] `diff -qr .agents/skills skills` reports no differences.
- [ ] If `CLAUDE.md` changed, `scripts/sync-agents.sh` has been run and `cmp -s CLAUDE.md AGENTS.md` passes.
- [ ] `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentInstructionsTests|FullyQualifiedName~WorkspaceRenderTests"` passes.
- [ ] Focused foundation matrix gate from Task 5 passes.
- [ ] `scripts/test.sh` passes.
- [ ] `git diff --check` passes.
- [ ] Goldfish checkpoint records what changed, why, verification evidence, and any remaining follow-up.
- [ ] Final local commit includes source, docs, generated evidence, synced skill mirror files, and checkpoint.

## Verification Strategy

**Project source of truth:** `AGENTS.md` testing section, `CLAUDE.md` generated-mirror rule, `tests/Miller.Tests/Miller.Tests.csproj`, `tests/plugin/plugin-manifest.test.cjs`, and existing foundation matrix runner conventions.

**Worker red/green scope:** Run the narrowest affected test first:
- Server/tool guidance: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentInstructionsTests"`
- Onboarding guidance: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkspaceRenderTests"`
- Skill sync: `scripts/sync-plugin-skills.sh && diff -qr .agents/skills skills`
- Plugin manifest guard: `node --test tests/plugin/plugin-manifest.test.cjs` when the file exists.

**Worker ceiling:** Workers may run focused xUnit filters, skill sync checks, plugin manifest test, Python benchmark py_compile, scorer unit tests when changed, and the focused foundation matrix command in Task 5. Workers do not own release, push, publish, or external review.

**Worker gate invariant:** Each worker gate proves the changed guidance surface is present, budget-safe, mirrored where required, and still parseable through the public renderer or benchmark command.

**Lead affected-change scope:** After a coherent batch, run:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentInstructionsTests|FullyQualifiedName~WorkspaceRenderTests|FullyQualifiedName~CliDispatchTests"
```

Run `CliDispatchTests` because README/site examples and onboarding CLI contract evidence depend on CLI rendering staying aligned.

**Branch gate:** Run `scripts/test.sh` before final handoff or merge.

**Replay/metric evidence:** The focused foundation matrix gate is hard for `inspect.overview` and `miller.contract.cli.workspace-onboarding`. Any usage/adoption interpretation about low-use tools remains report-only.

**Escalation triggers:** Broaden to `dotnet build Miller.slnx -c Release` if tool descriptions, source generators, project files, or warning-prone code paths change. Run `scripts/test.sh scale` only if indexing/extraction paths or `julie-extract` subprocess usage changes; this plan should not touch those areas.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the failure is in an assertion this plan explicitly says to update.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the final report or checkpoint. For benchmark evidence, record hard-gate pass/fail and report-only interpretation separately. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Model Routing

**Project source of truth:** No `RAZORBACK.md` exists in this checkout. Use the current harness default and record `inherit` for all worker model choices unless the lead explicitly supplies a model map.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage.
- Harness mapping: inherit.

**Implementation tier:** bounded worker tasks from this clear plan.
- Harness mapping: inherit.

**Mechanical tier:** docs, fixtures, generated mirrors, and generated benchmark evidence with no independent gate interpretation.
- Harness mapping: inherit.

**Gate-interpretation reviewer:** reading failing assertions, benchmark output, or generated evidence to decide whether the test or implementation is wrong.
- Harness mapping: inherit.

**Escalation tier:** subtle prompt-contract ambiguity, repeated test failures, changed public JSON shape, or any proposal to change inspect defaults.
- Harness mapping: inherit.

**Worker eligibility:** Implementation workers may execute one task at a time when they can use Miller for file orientation, write the failing assertion first, and run the assigned worker gate.

**Escalation triggers:** Stop and report if implementation requires changing `inspect` default depth, adding MCP parameters/tools, storing raw telemetry, changing telemetry schema, or weakening an existing contract/test.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, replay evidence, metrics, or acceptance gates. Split docs-only mirror updates from evidence interpretation.

**Unsupported harness behavior:** If the harness cannot choose models per agent, use `inherit`, note it in the final report, and continue.

## Final Acceptance Criteria

- [ ] Existing `inspect` default remains `summary`.
- [ ] Existing `inspect depth=overview` behavior remains present and hard-gated.
- [ ] Server instructions, inspect tool description, Miller skills, README, and GitHub Pages all route first symbol reads through `inspect depth=overview`.
- [ ] `workspace onboarding` compact and JSON output surface overview-first guidance without raw-query or raw-target telemetry.
- [ ] Low-use deterministic tools are discoverable through onboarding and instructions: `context` for orientation, `trace` for refs/path, and `impact` before refactors.
- [ ] Foundation candidate status is updated with focused evidence.
- [ ] No MCP tools or CLI commands are added.
- [ ] Fast suite passes.
