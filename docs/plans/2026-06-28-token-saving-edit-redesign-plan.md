# Token-Saving Edit Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Redesign Miller's existing `edit replace_text` path so agents can make small localized edits with fewer returned tokens than harness-native Read/Edit workflows, while preserving Miller's preview, freshness, atomic apply, and convergence guarantees.

**Architecture:** Keep the MCP surface stingy: deepen the existing `edit` tool instead of adding `edit_file`. Add a pure text-match planner in `Miller.Core`, an indexed-content candidate reader in `Miller.Indexing`, and wire `EditService` so `replace_text` can use `content.db` to locate candidate spans from small selectors, verify against current disk text, preview exact diffs, and apply safely.

**Tech Stack:** .NET 10, Miller MCP tool surface, SQLite `symbols.db` and `content.db`, existing `Miller.Core.Editing` planner/splicer/diff code, xUnit fast tests, Miller skills under `.agents/skills/`, generated `skills/` mirror, README/GitHub Pages docs, and the foundation matrix benchmark runner.

**Architecture Quality:** Affected modules are `EditTool`, `EditRequest`, `EditService`, `EditPlanner`, new core matching code, new content-corpus candidate read code, tests, and prompt/docs surfaces. Caller-facing interface stays inside the existing `edit` MCP tool; `replace_text` becomes deeper by accepting small selectors and hiding indexed-content lookup, disk verification, matching, diff preview, and safe apply. Architecture risk is medium because this changes the semantics agents should rely on for mutation, but locality is good if matching stays pure, content reads stay advisory, and all writes continue through the existing apply path.

## Global Constraints

- Do not add a new MCP tool.
- Do not remove any existing `edit` operations.
- Keep current exact `replace_text` calls backward compatible: `operation`, `target`, `old_text`, `new_text`, `occurrence`, `apply`, `allow_stale`, `scope`, and `format` must keep working.
- Additive `edit` parameters must be optional.
- `content.db` is current search/read infrastructure, not an edit buffer. Use it to find candidate spans; verify against current disk text before preview/apply.
- Do not treat overlapped content chunks as a complete file unless the code proves coverage and hash/revision facts.
- Do not store raw `old_text`, `query`, `anchor`, target text, or replacement text in telemetry. Aggregate metadata such as `match_mode`, `selector_kind`, and `match_source` is allowed.
- Do not add external runtime dependencies for fuzzy matching without explicit approval. Implement a bounded local matcher in `Miller.Core`.
- Keep semantic/vector retrieval out of Miller; Eros owns those workflows.
- Keep `.agents/skills/` canonical and regenerate `skills/` with `scripts/sync-plugin-skills.sh`.
- If `CLAUDE.md` changes, regenerate `AGENTS.md` with `scripts/sync-agents.sh` and verify byte-for-byte sync.
- Keep `MILLER_AGENT_INSTRUCTIONS.md` and MCP tool descriptions under existing test budgets.
- Every implementation task follows TDD: write or update the failing assertion first, verify it fails for the expected reason, then implement the smallest passing change.

---

## Interface Decision

Chosen lane: deepen existing `edit`.

`replace_text` gains optional selector and match parameters:

- `match_mode`: `auto | exact | normalized | fuzzy`, default `auto`.
- `query`: optional small indexed-content selector used to find candidate chunks/lines without returning the file to the model.
- `anchor`: optional nearby text selector used to narrow the candidate window.
- `line`: optional 1-based line hint used to narrow the candidate window.

`old_text` remains required for this slice. `query`, `anchor`, and `line` narrow where Miller looks; they do not authorize blind replacement without a known old value.

Preview output must prove why the edit is safe enough to apply:

- path
- matched line or line range
- `match_mode` actually used
- match source: `indexed_content`, `disk`, or `disk_after_index_candidate`
- match count and occurrence choice
- disk verification result
- concise unified diff
- existing "pass apply=true to commit" guidance

JSON output must include additive fields for the same facts. Existing JSON fields must remain valid.

Rejected lanes:

- **Remove `edit`:** rejected for this slice because Miller would give up a unique cross-harness token-saving opportunity before proving whether the redesigned tool can earn adoption.
- **Add `edit_file` as a new MCP tool:** rejected for this slice because Miller's product rule is to keep the MCP surface stingy, and the existing `edit` interface can be deepened first.
- **Only add fuzzy matching to exact `old_text`:** rejected as incomplete because it leaves the tool shaped as a patch applier and does not use indexed content for token-saving selectors.

## File Structure

- `src/Miller.Core/Editing/EditRecords.cs` - add match-mode records/enums only when they are pure editing contracts.
- `src/Miller.Core/Editing/EditPlanner.cs` - keep existing operation planners; route text replacement to the new matcher without weakening existing byte-span behavior.
- Create: `src/Miller.Core/Editing/TextReplaceMatcher.cs` - pure exact/normalized/fuzzy matching, occurrence selection, byte-span conversion, and match metadata.
- `tests/Miller.Tests/Editing/EditPlannerTests.cs` - existing exact replace coverage plus new matching-mode behavior through public planner/matcher contracts.
- Create: `tests/Miller.Tests/Editing/TextReplaceMatcherTests.cs` - focused pure matching tests when coverage would otherwise make `EditPlannerTests` too noisy.
- Create: `src/Miller.Indexing/IndexedEditCandidateReader.cs` - read-only candidate discovery over revision-fresh `content.db`; returns bounded candidate windows and metadata, never writes.
- Create: `tests/Miller.Tests/Indexing/IndexedEditCandidateReaderTests.cs` - content-corpus candidate read behavior, skipped/missing/stale sidecar behavior, and no whole-file reconstruction assumptions.
- `src/Miller.Server/Tools/EditRequest.cs` - add optional `MatchMode`, `Query`, `Anchor`, and `Line` request properties.
- `src/Miller.Server/Tools/EditTool.cs` - add optional MCP parameters, descriptions, telemetry metadata, and prompt-facing tool description updates.
- `src/Miller.Server/Tools/EditService.cs` - use indexed candidates for `replace_text`, verify disk text, render preview metadata, preserve apply/freshness path.
- `tests/Miller.Tests/Server/EditToolTests.cs` - public service behavior for preview/apply/error cases.
- `tests/Miller.Tests/Server/LiveEditTests.cs` - only update if the live edit contract needs a narrow scale proof for content-corpus-assisted replace.
- `tests/Miller.Tests/Server/AgentInstructionsTests.cs` - tool description and server-instruction content/budget assertions.
- `.agents/skills/miller-editing/SKILL.md` - token-saving edit workflow guidance.
- `.agents/skills/miller-orientation/SKILL.md` - first-call guidance for using edit when it avoids full-file reads.
- `skills/` - generated mirror from `.agents/skills/`.
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` - server-level guidance for token-saving edit use.
- `README.md` - public workflow copy for edit's value.
- `docs/site/index.html` - GitHub Pages workflow copy.
- `docs/findings/2026-06-28-julie-miller-edit-adoption.md` - update with implemented status and evidence links.
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md` - candidate status update.
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.csv` - candidate status update.
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.json` - candidate status update.
- `scripts/benchmarks/miller-foundation-cases.json` - add edit hard-gate rows.
- Create: `docs/findings/benchmarks/2026-06-28-token-saving-edit-redesign/` - focused evidence output.

## Task 1: Build Pure Text Matching For Token-Saving Replace

**Files:**
- Create: `src/Miller.Core/Editing/TextReplaceMatcher.cs`
- Modify: `src/Miller.Core/Editing/EditRecords.cs`
- Modify: `src/Miller.Core/Editing/EditPlanner.cs`
- Modify: `tests/Miller.Tests/Editing/EditPlannerTests.cs`
- Create: `tests/Miller.Tests/Editing/TextReplaceMatcherTests.cs`

**Interfaces:**
- Consumes: existing `Occurrence` enum and `TextEdit` byte-span contract.
- Produces: pure text-match contract for exact, normalized, fuzzy, and auto matching with match metadata.

**What to build:** Add a pure matcher that can find replacement spans without requiring exact caller text. `auto` must try exact first, then normalized line matching, then bounded fuzzy matching for small snippets.

**Approach:** Keep the matcher deterministic and bounded. Normalized matching should tolerate indentation, trailing whitespace, CRLF/LF differences, and tabs-vs-spaces where line text is otherwise the same. Fuzzy matching must be limited by snippet length and edit distance so it cannot silently rewrite unrelated text. Every returned span is a UTF-8 byte span over the original current content, not normalized text.

**Acceptance criteria:**
- [x] Exact matching preserves current first/last/all behavior and byte offsets.
- [x] Normalized matching handles indentation differences.
- [x] Normalized matching handles trailing whitespace differences.
- [x] Normalized matching handles CRLF/LF differences without corrupting line endings outside the replacement span.
- [x] Normalized matching handles tabs-vs-spaces indentation differences.
- [x] Fuzzy matching handles a short extra-character difference.
- [x] Fuzzy matching handles a short missing-character difference.
- [x] Fuzzy matching refuses long snippets beyond the explicit bound.
- [x] Fuzzy matching refuses low-confidence candidates instead of guessing.
- [x] `occurrence="all"` never produces overlapping edits.
- [x] Multi-byte characters before the match still produce correct UTF-8 byte spans.
- [x] Worker-scope verification passes, committed.

## Task 2: Add Indexed Content Candidate Discovery For Edit

**Files:**
- Create: `src/Miller.Indexing/IndexedEditCandidateReader.cs`
- Test: `tests/Miller.Tests/Indexing/IndexedEditCandidateReaderTests.cs`
- Modify: `tests/Miller.Tests/Indexing/ContentCorpusWriterTests.cs` only if fixture helpers need reusable candidate-reader setup.

**Interfaces:**
- Consumes: `ContentCorpusSidecar.ContentDbPathFor`, `content_sources`, `content_chunks`, `content_meta`, and active workspace source rows.
- Produces: bounded `IndexedEditCandidate` results with path, line range, byte range, raw chunk text, source hash, revision, and match source metadata.

**What to build:** Add a read-only content-corpus reader for edit candidate discovery. It must not pretend chunks are a complete file and must not write or rebuild sidecars.

**Approach:** Query only revision-fresh `content.db`. For `target` file plus `old_text`, `query`, `anchor`, or `line`, return a small ordered set of candidate windows. Use chunk metadata for narrowing, then let `EditService` read disk and verify before planning. If `content.db` is missing, stale, corrupt, or the file is skipped, return a typed "unavailable" result so `EditService` can fall back to disk matching with honest preview metadata.

**Acceptance criteria:**
- [x] Candidate reader returns a candidate chunk for a file path and literal `old_text` present in `content.db`.
- [x] Candidate reader returns a candidate chunk for a `query` selector present in indexed content.
- [x] Candidate reader narrows by `line` when supplied.
- [x] Candidate reader narrows by `anchor` when supplied.
- [x] Candidate reader reports missing/stale/corrupt `content.db` as unavailable, not as "no match."
- [x] Candidate reader reports skipped files as unavailable when no active source row exists.
- [x] Candidate reader opens SQLite read-only with `Pooling=false`.
- [x] Candidate reader returns bounded candidates and does not dump full files.
- [x] Worker-scope verification passes, committed.

## Task 3: Wire `edit replace_text` To Indexed Candidates And Disk Verification

**Files:**
- Modify: `src/Miller.Server/Tools/EditRequest.cs`
- Modify: `src/Miller.Server/Tools/EditTool.cs`
- Modify: `src/Miller.Server/Tools/EditService.cs`
- Test: `tests/Miller.Tests/Server/EditToolTests.cs`

**Interfaces:**
- Consumes: `TextReplaceMatcher`, `IndexedEditCandidateReader`, existing `FreshnessGate`, `EditApplier`, and `IEditWriteThrough`.
- Produces: backward-compatible `edit replace_text` with optional selector parameters and richer preview metadata.

**What to build:** Make `replace_text` use indexed content as a candidate finder, then verify and plan against current disk content. Existing exact-match calls must keep working.

**Approach:** Add optional `match_mode`, `query`, `anchor`, and `line` parameters to the MCP tool and request record. In `EditService`, resolve the target file as today, ask the candidate reader for bounded hints, read disk internally, verify the candidate against disk/current hash, run the pure matcher in the candidate window when available, and fall back to disk-wide matching only when indexed candidates are unavailable. Apply still uses the existing freshness gate and atomic write path.

**Acceptance criteria:**
- [x] Existing exact `replace_text` preview output remains valid for callers that do not pass new parameters.
- [x] Existing exact `replace_text apply=true` writes and converges as before.
- [x] `match_mode=auto` preview reports `match_mode=exact` when exact match succeeds.
- [x] `match_mode=auto` preview reports `match_mode=normalized` for indentation/line-ending tolerant matches.
- [x] `match_mode=auto` preview reports `match_mode=fuzzy` for accepted bounded fuzzy matches.
- [x] `match_mode=exact` refuses normalized/fuzzy-only matches.
- [x] `query` narrows matching through indexed content and preview reports indexed candidate use.
- [x] `line` narrows matching and prevents the same `old_text` elsewhere from being selected.
- [x] `anchor` narrows matching and prevents the same `old_text` elsewhere from being selected.
- [x] Missing/stale/unavailable content corpus falls back to verified disk matching and preview states the fallback.
- [x] Ambiguous candidate matches return a clear no-write error with narrowed rerun guidance.
- [x] Raw selector text is not written to telemetry metadata.
- [x] Worker-scope verification passes, committed.

## Task 4: Make Preview And JSON Output Prove Token-Saving Edit Safety

**Files:**
- Modify: `src/Miller.Server/Tools/EditService.cs`
- Modify: `tests/Miller.Tests/Server/EditToolTests.cs`
- Modify: `tests/Miller.Tests/Server/AgentInstructionsTests.cs` if tool-description budget/content assertions need updates.

**Interfaces:**
- Consumes: `EditService.EditResult` and existing compact/JSON edit rendering.
- Produces: additive compact and JSON match evidence for previews and applies.

**What to build:** Change edit output from "diff only" to "diff plus concise match proof." The proof should show the agent enough evidence to safely re-call with `apply=true` without reading the full file.

**Approach:** Keep output bounded. Compact output should include one short metadata block before the diff. JSON output should add fields rather than replacing existing fields: `match_mode`, `match_source`, `matched_path`, `line_start`, `line_end`, `match_count`, `occurrence`, `disk_verified`, and `content_index_state` where applicable.

**Acceptance criteria:**
- [x] Compact preview includes match mode, source, line range, occurrence, and disk verification status.
- [x] Compact apply output includes the same match proof or a clear applied summary plus diff.
- [x] JSON preview remains parseable and includes additive match fields.
- [x] JSON apply remains parseable and includes additive match fields.
- [x] No-change previews still return `empty` outcome and include enough context to explain the no-op.
- [x] Error output for ambiguous/no-match cases gives copyable narrowed rerun examples.
- [x] Existing rename/symbol edit preview behavior is not regressed.
- [x] Worker-scope verification passes, committed.

## Task 5: Update Agent Guidance, Skills, README, And GitHub Pages

**Files:**
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Modify: `src/Miller.Server/Tools/EditTool.cs`
- Modify: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`
- Modify: `.agents/skills/miller-editing/SKILL.md`
- Modify: `.agents/skills/miller-orientation/SKILL.md`
- Generated: `skills/miller-editing/SKILL.md`
- Generated: `skills/miller-orientation/SKILL.md`
- Modify: `README.md`
- Modify: `docs/site/index.html`

**Interfaces:**
- Consumes: implemented `edit` parameters and preview behavior.
- Produces: prompt-facing and public guidance that tells agents when Miller edit saves tokens.

**What to build:** Make the value proposition explicit: use `edit` for small localized existing-file edits when it avoids a full-file Read or gives safer preview/freshness guarantees than harness-native editing.

**Approach:** Do not claim Miller edit is always better. Guidance should route broad handcrafted multi-hunk edits to normal patching, and route token-saving localized changes to `edit`. Include examples using `query`, `line`, `anchor`, and `match_mode=auto`.

**Acceptance criteria:**
- [x] Server instructions explain that `edit` can avoid full-file Read for localized edits.
- [x] Edit tool description names `match_mode`, `query`, `anchor`, and `line` clearly and stays within budget.
- [x] `miller-editing` skill no longer says `replace_text` requires exact `old_text` in all cases.
- [x] `miller-editing` skill explains preview-first apply flow with match proof.
- [x] `miller-orientation` points localized edit tasks to `edit` when it avoids a full-file read.
- [x] `scripts/sync-plugin-skills.sh` has been run and `diff -qr .agents/skills skills` reports no differences.
- [x] README and GitHub Pages describe edit as token-saving for localized edits, not just as a patch tool.
- [x] Worker-scope verification passes, committed.

## Task 6: Add Foundation Matrix Rows And Evidence

**Files:**
- Modify: `scripts/benchmarks/miller-foundation-cases.json`
- Create: `docs/findings/benchmarks/2026-06-28-token-saving-edit-redesign/`
- Modify: `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md`
- Modify: `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.csv`
- Modify: `docs/findings/benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.json`
- Modify: `docs/findings/2026-06-28-julie-miller-edit-adoption.md`

**Interfaces:**
- Consumes: implemented MCP `edit` behavior and existing foundation matrix runner.
- Produces: hard-gate evidence that Miller edit can perform localized edits without returning full-file content.

**What to build:** Add focused matrix rows for Julie-winning edit workflows. These rows should prove behavior, not adoption rates.

**Approach:** Add rows for version-string update, indentation-tolerant update, CRLF/trailing-whitespace tolerant update, short fuzzy update, line-scoped duplicate text update, anchor-scoped duplicate text update, and no-match/ambiguous recovery. If the existing matrix runner cannot assert returned-token or full-file absence, stop and report a plan mismatch instead of expanding the runner casually.

**Acceptance criteria:**
- [x] Matrix row proves a version string update without full-file output.
- [x] Matrix row proves normalized indentation matching.
- [x] Matrix row proves CRLF or trailing-whitespace tolerant matching.
- [x] Matrix row proves bounded fuzzy matching.
- [x] Matrix row proves `line` prevents editing the wrong duplicate.
- [x] Matrix row proves `anchor` prevents editing the wrong duplicate.
- [x] Matrix row proves ambiguous/no-match output gives recovery guidance.
- [x] Evidence output records hard-gate pass/fail separately from report-only usage/adoption interpretation.
- [x] Adaptation candidate docs mark edit redesign as implemented with evidence links.
- [x] Worker-scope verification passes, committed.

## Verification Strategy

**Project source of truth:** `AGENTS.md`, `CLAUDE.md`, `tests/Miller.Tests/Miller.Tests.csproj`, and `scripts/test.sh`.

**Worker red/green scope:** Run the narrowest focused test for the touched behavior:

- Core matching: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~TextReplaceMatcherTests|FullyQualifiedName~EditPlannerTests"`
- Indexed candidate reader: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~IndexedEditCandidateReaderTests"`
- Edit service/tool: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~EditToolTests"`
- Prompt/tool descriptions: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentInstructionsTests"`

**Worker ceiling:** Workers may run `scripts/test.sh` for confidence after their slice. Workers should not run `scripts/test.sh scale` unless they changed live extractor/indexer subprocess behavior or the lead explicitly asks.

**Worker gate invariant:** Each worker gate proves the caller-facing behavior named by that task through public planner, reader, service, or rendered tool output. Private helper tests are allowed only when paired with public behavior coverage.

**Lead affected-change scope:** After coherent implementation batches, run focused edit/indexing tests together:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj --no-restore --filter "FullyQualifiedName~TextReplaceMatcherTests|FullyQualifiedName~EditPlannerTests|FullyQualifiedName~IndexedEditCandidateReaderTests|FullyQualifiedName~EditToolTests|FullyQualifiedName~AgentInstructionsTests"
```

**Branch gate:** Run:

```bash
scripts/test.sh
dotnet build Miller.slnx -c Release
```

**Replay/metric evidence:** Focused foundation matrix edit rows are hard gates for behavior, parseability, concise preview evidence, and "no full-file output" assertions. Usage/adoption telemetry remains report-only.

**Escalation triggers:** Run `scripts/test.sh scale` if implementation changes indexer refresh, content corpus generation, `julie-extract` subprocess paths, file watcher convergence, or live edit scale behavior. Broaden to CLI contract tests if an existing CLI `edit` path is found during implementation.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the failure is the expected RED assertion for that task.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the final report or checkpoint. For matrix evidence, record hard-gate metrics and report-only metrics separately.

## Model Routing

**Project source of truth:** No repo-local `RAZORBACK.md` exists in this checkout. Use harness default model selection and mark all mappings `inherit`.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage.
- Harness mapping: inherit.

**Implementation tier:** bounded worker tasks from this plan.
- Harness mapping: inherit.

**Mechanical tier:** docs, fixtures, skill mirrors, benchmark metadata updates with no gate interpretation.
- Harness mapping: inherit.

**Gate-interpretation reviewer:** reviewer tier for deciding whether failing edit/matrix behavior means the test or implementation is wrong.
- Harness mapping: inherit.

**Escalation tier:** subtle correctness, write-safety regression, ambiguous fuzzy matching, raw telemetry leakage, or repeated gate failures.
- Harness mapping: inherit.

**Worker eligibility:** Workers may implement tasks with clear tests and no product-surface redesign beyond this plan.

**Escalation triggers:** Any proposal to add a new MCP tool, remove `edit`, store raw selector text in telemetry, add external matching dependencies, or treat content chunks as a complete edit buffer without coverage/hash proof must return to the lead/user.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, replay evidence, metrics, or acceptance gates. Split docs-only mirror updates from behavior interpretation.

**Unsupported harness behavior:** If the harness cannot choose models per agent, use `inherit`, note it in the verification ledger, and continue.
