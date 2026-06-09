# Miller Quality Review Goal Design

- **Date:** 2026-06-09
- **Status:** Active goal design
- **Mode:** Fix-as-found phased review
- **Primary goal:** Find and fix quality problems before Miller accumulates hidden slop, missing implementation, performance debt, Windows breakage, or a slow default test suite.
- **Completion standard:** Every checklist item is checked with evidence, fixed with passing verification, or explicitly deferred with a reason and follow-up.

## Intent

Run a long, autonomous review of Miller as a quality program rather than a one-time static code review. The review should be thorough enough that a future agent can run it as a goal command, keep making progress for a long time, and use the finished checklists plus implemented fixes plus passing tests as proof of completion.

This review should prefer fixing high-confidence defects as soon as they are found. It should not stop at a findings list unless the finding needs approval, is too large for the current phase, or changes product intent.

The core risks to control are:

- incomplete or fake implementations that look finished,
- shortcuts left by prior agents,
- architecture seams that are shallow, duplicated, or leaky,
- first-read latency, memory spikes, and slow read paths,
- Unix-only assumptions that break Windows packaging, plugins, scripts, or runtime behavior,
- fast-suite erosion from slow or subprocess-heavy tests,
- stale docs, stale TODOs, or hidden follow-up work.

## Goal Runner Contract

An agent running this goal must:

- Work phase by phase, but fix high-confidence defects as they are found.
- Keep changes scoped to the phase unless a defect blocks verification.
- Use the repo's existing patterns before adding new abstractions.
- Use Miller tools first for orientation (`context`, `search`, `inspect`, `trace`, `impact`, `workspace`) before broad shell reading.
- Keep `Miller.Core` pure and free of I/O dependencies.
- Keep Miller read paths projection-specific where possible; cheap operations must not accidentally hydrate full indexes.
- Preserve the default fast test lane. Anything that spawns `julie-extract`, uses large fixtures, or performs scale/dogfood work belongs in Scale or in documented findings.
- Do not push, publish, release, retag, delete releases, or rewrite git history without explicit approval.
- Record every material conclusion in a durable artifact: this plan, a dated findings doc, `TODO.md`, an ADR, or a Goldfish checkpoint.

## Evidence Artifacts

Create or update these during the goal:

- `docs/findings/YYYY-MM-DD-miller-quality-review.md`
  - Main evidence ledger for commands, timings, issues found, fixes, and deferred risks.
- `TODO.md`
  - Only for real remaining work, not completed historical notes.
- `docs/adr/ADR-NNNN-*.md` if ADRs already exist, or `docs/findings/architecture-decision.md` only if the repo has not adopted `docs/adr/`.
  - Use only for accepted structural decisions or repeated findings future agents should not rediscover.
- Goldfish checkpoints
  - Save at meaningful phase boundaries, after large fixes, and before commits.

The findings ledger should use this shape for each issue:

```markdown
### Finding: [short name]

- **Phase:** ...
- **Severity:** blocker / high / medium / low
- **Status:** fixed / deferred / accepted / not a bug
- **Evidence:** commands, tool calls, file refs, timings, screenshots, workflow links, or test names
- **Root cause:** ...
- **Fix:** ...
- **Verification:** ...
- **Follow-up:** ...
```

## Architecture Quality

**Affected modules:** `Miller.Core`, `Miller.Indexing`, `Miller.Server`, `Miller.Dashboard`, CLI dispatch, MCP tools, plugin launchers/manifests, scripts, tests, release workflows, docs, and public contracts.

**Caller-facing interface:** MCP tools, CLI verbs, public JSON contracts, plugin entry points, release archives, dashboard routes, test wrappers, docs, and this goal plan's checklists.

**Depth/locality check:** Each phase reviews one concern deeply. Fixes should keep behavior local to the module that owns the policy. If a fix requires many files, first ask whether the policy is scattered and whether a smaller caller-facing interface would reduce future changes.

**Test surface:** Tests should prove behavior through caller-facing interfaces: tool calls, CLI commands, JSON contracts, package scripts, workflow/package checks, and public helper seams. Private plumbing tests are acceptable only when the caller-facing surface cannot isolate the behavior cheaply.

**Seams/adapters:** Review and preserve useful seams: `WorkspaceIndexProvider`, projection loaders, `SmartTargetResolver`, sidecar readers/builders, content corpus store, CLI dispatch, process runners, path canonicalization, watcher filters, plugin launcher/platform mapping, and release packaging adapters.

**Rejected shortcuts:** No static skim as a substitute for live validation; no TODO search as the whole completeness audit; no broad refactor without an architecture candidate; no ignoring Windows because local validation is macOS; no hiding slow tests in the fast suite; no release-facing claims without live evidence; no swallowing errors into default values unless the fallback is explicit and tested.

**Architecture risk:** Medium. The review is intentionally broad and may uncover structural problems. Use candidate records and ADRs to keep refactors deliberate.

## Architecture Review Checklist

Apply these questions to every non-trivial fix and every review-time refactor candidate:

- [ ] Does this keep complexity local?
- [ ] Is the caller-facing interface smaller than the behavior it unlocks?
- [ ] Are tests written through the same interface callers use?
- [ ] Did new seams earn their keep?
- [ ] Did this avoid speculative extensibility?
- [ ] Did it fix the structural cause, not only the symptom?

Candidate refactors must use this format before implementation:

```markdown
### Candidate: [Name]

- **Files:** ...
- **Current friction:** ...
- **Deletion test:** ...
- **Proposed module/interface:** ...
- **Why this improves locality/leverage:** ...
- **Test surface:** ...
- **Risk:** low / medium / high
- **Recommendation:** fold into current phase / split into separate phase / reject for now
```

## Phase 0: Baseline And Inventory

Purpose: establish current truth before changing code.

Checklist:

- [ ] Confirm clean or understood git state with `git status --short`.
- [ ] Record recent commits with `git log --oneline -10`.
- [ ] Record current Miller version from `Directory.Build.props`, `miller version`, and plugin manifests.
- [ ] Record current workspace status for `/Users/murphy/source/miller`.
- [ ] Run `dotnet build Miller.slnx -c Release`.
- [ ] Run `scripts/test.sh` and record elapsed time.
- [ ] Run `scripts/test.sh scale` or record why Scale could not run.
- [ ] Run `scripts/test-plugin.sh` or the repo's current plugin test command.
- [ ] Run `git diff --check`.
- [ ] Record current `search` / summary `inspect` / full `inspect` timings on Miller.
- [ ] Record at least one cross-workspace `search` and summary `inspect` timing on a large registered workspace if available.
- [ ] Record current `search-quality` baseline from `.miller/eval/search-quality/runs/` or run the documented runner if stale.
- [ ] Create the findings ledger for this goal.

Completion evidence:

- [ ] Findings ledger has command outputs summarized with dates and exact commands.
- [ ] Initial failures are either fixed immediately or listed as active findings.
- [ ] Baseline timings are specific enough to compare later before/after values.

## Phase 1: Missing Or Shortcut Implementation Audit

Purpose: find code that pretends to be done but is missing, weak, misleading, or only partially implemented.

Search checklist:

- [ ] Search source, tests, scripts, workflows, docs, plugin files, and `TODO.md` for `NotImplemented`, `TODO`, `FIXME`, `HACK`, `throw new`, `Unsupported`, `temporary`, `stub`, `placeholder`, `future`, `not yet`, `best effort`, and similar terms.
- [ ] Search for empty catch blocks, catch-and-log-only paths, and fallbacks to empty/default values.
- [ ] Search for compatibility branches or old code paths that may now duplicate current behavior.
- [ ] Search for environment flags with no tests or unclear default behavior.
- [ ] Search for public docs claims that no longer match current CLI/MCP behavior.
- [ ] Search `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` for every documented tool and confirm each tool still exists and has tests.

Inspection checklist:

- [ ] Compare README quickstart and CLI examples to current CLI verbs.
- [ ] Compare `docs/contracts/*.md` to implementation and tests.
- [ ] Compare `TODO.md` open items to live code and mark completed/stale items only when proven.
- [ ] Inspect MCP schemas for missing required arguments, misleading defaults, or handler behavior that differs from client-visible metadata.
- [ ] Inspect CLI JSON output paths for stable contracts versus accidental internal shapes.
- [ ] Inspect release/package docs for guessed facts versus live release facts.

Fix checklist:

- [ ] Fix high-confidence missing implementation or misleading behavior immediately.
- [ ] Add regression tests for every fixed shortcut.
- [ ] Convert acceptable placeholders into explicit deferred notes with a reason.
- [ ] Remove or narrow stale compatibility paths when the old path is no longer needed.
- [ ] Record every non-trivial deferred item in `TODO.md` or the findings ledger.

Completion evidence:

- [ ] Findings ledger classifies every reviewed placeholder/stub as fixed, deferred, accepted, or not a bug.
- [ ] No unclassified `TODO`/`FIXME`/stub terms remain in active source paths.
- [ ] Tests cover each fixed missing/shortcut implementation.

## Phase 2: Architecture-Quality Review

Purpose: prevent Miller from accumulating shallow wrappers, leaky interfaces, scattered policy, or test-hostile structure.

Module checklist:

- [ ] Review `Miller.Core` for accidental I/O, mutable global state, or logic that belongs nearer an adapter.
- [ ] Review `Miller.Indexing` for storage policy scattered into server/tool callers.
- [ ] Review `Miller.Server` hosting for lifecycle-order assumptions, hosted-service constructor hazards, and bootstrap getter usage.
- [ ] Review tool classes for duplicated argument parsing, rendering policy, workspace resolution, JSON shape policy, or error handling.
- [ ] Review CLI dispatch for server/CLI boundary leaks and accidental host startup in one-shot verbs.
- [ ] Review dashboard data paths for full-index hydration or expensive list/detail queries.
- [ ] Review plugin launchers and manifests for duplicated version/platform/package policy.
- [ ] Review scripts and workflows for duplicated release/package logic.

Architecture-smell checklist:

- [ ] Pass-through modules: identify wrappers that add no local policy or test value.
- [ ] Duplicated logic: identify repeated parsing, path, workspace, rendering, or fallback rules.
- [ ] Wrong abstraction level: identify APIs that force callers to know storage, lifecycle, or protocol details.
- [ ] Tests reaching past interfaces: identify tests that assert private plumbing instead of public behavior.
- [ ] Speculative seams: identify adapters or extension points with only one real use and no protocol/ownership need.
- [ ] Shotgun surgery: identify behavior changes that require scattered edits.
- [ ] Swallowed errors: identify failure paths that hide loss of correctness.
- [ ] Primitive obsession: identify raw strings/flags carrying repeated invariants.
- [ ] Over-decomposition: identify many tiny modules that do not reduce caller burden.
- [ ] Additive-only changes: identify old/new paths left side by side after migration.

Candidate handling checklist:

- [ ] For each structural issue, decide whether it is local cleanup, an architecture candidate, or acceptable.
- [ ] Write candidate records for non-trivial refactors.
- [ ] Implement only candidates required to fix current defects or approved by the user.
- [ ] Write ADRs for accepted structural decisions that future agents need to preserve.

Completion evidence:

- [ ] Every major module has an architecture-quality review note in the findings ledger.
- [ ] Refactor candidates are recorded with risk and recommendation.
- [ ] Implemented architecture fixes have caller-facing tests.
- [ ] ADRs or findings explain accepted/rejected structural decisions.

## Phase 3: Performance Review

Purpose: keep Miller fast in the paths agents use repeatedly.

Priority surfaces:

- `search` first-read and repeated-read latency.
- Summary `inspect` first-read and repeated-read latency.
- Full `inspect`, `context`, `impact`, and `trace` hydration costs.
- Cross-workspace reads.
- `workspace status`, `workspace list`, and dashboard list/detail views.
- `search.db` and `content.db` open, stale-check, rebuild, and query behavior.
- CLI startup and one-shot command latency.
- MCP server startup and steady-state memory.
- Test suite runtime.

Measurement checklist:

- [ ] Measure current workspace `search` compact and JSON latency.
- [ ] Measure current workspace summary `inspect` compact and JSON latency.
- [ ] Measure full `inspect` latency separately from summary inspect.
- [ ] Measure cross-workspace `search` and summary `inspect` on at least one large registered workspace.
- [ ] Measure `workspace status` and dashboard list/detail without full-index hydration.
- [ ] Measure CLI one-shot `version`, `workspace status`, `search`, and `inspect`.
- [ ] Measure memory/RSS for the largest practical read path available locally.
- [ ] Measure fast-suite wall clock and identify the slowest tests if the suite approaches the budget.

Review checklist:

- [ ] Confirm cheap read paths request the smallest projection that can answer the query.
- [ ] Confirm summary `inspect` does not call the full repository loader.
- [ ] Confirm `search` does not hydrate graph/bridge data.
- [ ] Confirm `workspace status` and dashboard list paths use cheap facts, not full index loads.
- [ ] Confirm sidecar readers avoid eager full-table materialization where not needed.
- [ ] Confirm caches include revision/path keys and evict stale workspace entries.
- [ ] Confirm corrupt/stale sidecars fail visibly or self-heal only where the current design says they should.
- [ ] Confirm search-quality improvements do not widen ranking inputs without measured cost and quality evidence.

Fix checklist:

- [ ] Fix unnecessary full hydration in cheap paths.
- [ ] Fix repeated loads or missing single-flight behavior.
- [ ] Fix accidental eager materialization in sidecar or content paths.
- [ ] Add regression tests or benchmarks for each fixed performance defect.
- [ ] Record before/after timing and memory evidence.

Completion evidence:

- [ ] Baseline and final timings are recorded in the findings ledger.
- [ ] Each performance fix has before/after evidence.
- [ ] No fast-suite performance test was added without guarding the default test budget.
- [ ] Remaining perf risks are explicit and ranked.

## Phase 4: Windows Portability Review

Purpose: catch Windows breakage without assuming macOS behavior is portable.

Runtime checklist:

- [ ] Audit all path joins, path comparisons, canonicalization, and root checks for separator and drive-root behavior.
- [ ] Audit any manual splitting on `/` or `\` and confirm tests cover both where needed.
- [ ] Audit file locks and lock-file behavior for Windows sharing semantics.
- [ ] Audit `FileSystemWatcher` usage, ignore-file handling, and nested workspace behavior for Windows assumptions.
- [ ] Audit process launch code for shell assumptions, quoting, executable suffixes, and `UseShellExecute`.
- [ ] Audit temp-file, archive extraction, chmod, and executable-bit assumptions.

Script/workflow checklist:

- [ ] Confirm critical shell scripts have PowerShell mirrors where Windows users need them.
- [ ] Confirm CI uses the PowerShell wrappers for Windows restore/build/test.
- [ ] Confirm release workflow produces the Windows target archive and checksum sidecar.
- [ ] Confirm Windows package smoke runs `miller version` and bundled `julie-extract --version`.
- [ ] Confirm checksums tolerate CRLF concerns where relevant.
- [ ] Confirm plugin launcher platform mapping covers Windows x64 and errors clearly for unsupported platforms.

Test checklist:

- [ ] Add or update tests for Windows path handling where defects are found.
- [ ] Add launcher tests for Windows archive URL, executable name, and checksum parsing when missing.
- [ ] Add workflow/static guards for Unix-only assumptions in critical install/test paths.
- [ ] Use live Windows CI evidence when available; otherwise record unverified Windows risks explicitly.

Completion evidence:

- [ ] Findings ledger lists Windows-verified, locally-tested, and unverified items separately.
- [ ] Any Windows-specific fixes have tests that run on macOS/Linux where possible.
- [ ] Remaining Windows risks are not hidden behind "not currently on Windows."

## Phase 5: Test Suite Discipline Review

Purpose: keep Miller from repeating Julie's slow-suite failure mode.

Fast-suite checklist:

- [ ] Confirm bare `dotnet test` and `scripts/test.sh` run only `Category!=Scale`.
- [ ] Confirm `Miller.Tests.csproj` still enforces the default filter.
- [ ] Confirm CI fast-suite job has a wall-clock budget.
- [ ] Record fast-suite elapsed time before and after fixes.
- [ ] Identify the slowest fast tests if runtime is near budget.
- [ ] Move or retag tests that spawn `julie-extract`, use large fixtures, watch real file systems, or depend on release packages.

Scale-suite checklist:

- [ ] Confirm every test that uses `ScaleTestSupport.RequireJulieServer()` has `[Trait("Category","Scale")]` at class level.
- [ ] Confirm no private `LocateJulieServer()` or duplicate repo-root helpers were reintroduced.
- [ ] Confirm scale tests skip, not fail, when `.tools/julie-extract` is absent.
- [ ] Confirm scale tests still exercise real extraction/indexing paths needed for release confidence.

Test-quality checklist:

- [ ] Identify tests asserting private plumbing instead of caller-facing behavior.
- [ ] Identify tests that hide real defects behind broad mocks.
- [ ] Identify duplicated test setup that should be shared only if it reduces caller burden.
- [ ] Fix xUnit analyzer warnings instead of suppressing them.
- [ ] Keep new tests focused on behavior and regression risk.

Completion evidence:

- [ ] Fast suite elapsed time is recorded and within budget.
- [ ] Scale suite status is recorded.
- [ ] Any retagged or moved tests have a clear reason.
- [ ] Test convention guards still pass.

## Phase 6: Package, Plugin, Release, And Docs Integrity

Purpose: make sure install and release surfaces match reality and do not regress silently.

Package checklist:

- [ ] Confirm release workflow matrix matches `scripts/julie-pins.json`.
- [ ] Confirm archives include `miller`, matching `.tools/julie-extract`, dashboard executable, and required dashboard assets.
- [ ] Confirm every archive has a `.sha256` sidecar.
- [ ] Confirm release smoke checks use robust shell matching, not brittle pipefail patterns.
- [ ] Confirm package validation can run without publishing.
- [ ] Do not publish or alter releases without explicit approval.

Plugin checklist:

- [ ] Confirm `.claude-plugin/plugin.json`, `.cursor-plugin/plugin.json`, `.codex-plugin/plugin.json`, `.mcp.json`, and `miller-plugin.json` are version-aligned.
- [ ] Confirm plugin launchers use platform-appropriate archive names and relative roots.
- [ ] Confirm Cursor does not depend on Claude-specific variables.
- [ ] Confirm Codex plugin manifest points to `.mcp.json` correctly.
- [ ] Confirm marketplace metadata is current where applicable.
- [ ] Run plugin manifest/launcher tests.

Docs checklist:

- [ ] Confirm README install paths match current hosted plugin, local clone, manual archive, manual MCP config, and source-checkout behavior.
- [ ] Confirm `docs/README.md` marks active docs versus historical evidence correctly.
- [ ] Confirm release notes and current-release docs cite live release facts only.
- [ ] Confirm `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` documents every MCP tool surface.
- [ ] If changing `CLAUDE.md`, run `scripts/sync-agents.sh` and confirm `cmp -s CLAUDE.md AGENTS.md`.

Completion evidence:

- [ ] Package/plugin/docs findings are recorded.
- [ ] Version alignment is verified by tests or exact file checks.
- [ ] Any release-facing claim changed during the goal has live evidence or is clearly marked as unverified.

## Phase 7: Security, Safety, And Failure-Mode Review

Purpose: catch defects that could damage user work, leak secrets, or hide broken state.

Checklist:

- [ ] Review workspace root safety for home, filesystem root, drive root, and system-dir refusal.
- [ ] Review edit/apply paths for stale-index refusal, lock handling, and no overwrite of unrelated changes.
- [ ] Review content import/export for path traversal, root escape, and size handling.
- [ ] Review archive extraction and plugin launchers for unsafe paths.
- [ ] Review logging for secret-like data, overbroad environment capture, or noisy sensitive paths.
- [ ] Review telemetry export for expected data boundaries.
- [ ] Review fallback behavior around corrupt sidecars, stale indexes, and failed extract operations.
- [ ] Confirm errors that affect correctness surface loudly enough for agents to act.

Completion evidence:

- [ ] Safety findings are classified by severity.
- [ ] High-confidence safety defects are fixed with tests.
- [ ] Any accepted risk is documented with rationale.

## Phase 8: Final Verification And Closeout

Purpose: prove the goal finished in a way future agents can trust.

Final verification checklist:

- [ ] `dotnet build Miller.slnx -c Release`
- [ ] `scripts/test.sh`
- [ ] `scripts/test.sh scale` or documented skip reason
- [ ] `scripts/test-plugin.sh`
- [ ] `git diff --check`
- [ ] Search-quality runner or documented current run evidence
- [ ] Representative MCP tool smoke: `workspace`, `search`, `inspect`, `content`, and one graph/read tool where practical
- [ ] Representative CLI smoke: `version`, `workspace status`, `search`, and `inspect`
- [ ] Windows CI/workflow evidence reviewed if relevant changes touched Windows surfaces

Closeout checklist:

- [ ] Every phase checklist item is checked, deferred, or marked not applicable with a reason.
- [ ] Findings ledger has final status for every finding.
- [ ] `TODO.md` contains only real remaining work from this goal.
- [ ] ADRs are written for accepted structural decisions that need durable memory.
- [ ] Goldfish checkpoint captures what changed, why, how verified, and what remains.
- [ ] Final summary includes fixes, verification, performance deltas, Windows status, and unresolved risks.

## Definition Of Done

This goal is done only when:

- All phase checklists have evidence-backed statuses.
- High and blocker severity defects found during the review are fixed or explicitly deferred with user-visible rationale.
- Implemented fixes have tests through caller-facing interfaces.
- The fast suite remains fast and the scale suite remains opt-in.
- Performance claims include measured before/after evidence.
- Windows-sensitive surfaces are tested, CI-verified, or clearly listed as unverified risk.
- Durable artifacts are updated so future agents do not need chat history to understand the outcome.

## Non-Goals

- No release publication unless separately approved.
- No product expansion beyond quality fixes found during review.
- No semantic/vector search work unless a quality defect in current Miller surfaces requires it.
- No broad replacement of `julie-extract`; Miller remains a consumer of the pinned extractor binary.
- No speculative architecture rewrites. Refactors must come from a concrete finding, a candidate record, and an earned test surface.
