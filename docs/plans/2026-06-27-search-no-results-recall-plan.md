# Search No-Results Recall Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Reduce false-empty `search` results without adding semantic/vector search to Miller.

**Architecture:** Treat this as a deterministic lexical retrieval problem first. Fix text-content recall inside the disk-backed FTS path by widening candidate retrieval from strict AND to ranked OR candidates, then apply shared coverage and phrase rules so short identifier/path searches stay precise while longer natural-language searches can return high-coverage partial matches. Add cross-route symbol fallback only after the text-content fix is measured, and keep semantic search out of Miller unless new evidence shows deterministic retrieval cannot cover the workflow.

**Tech Stack:** .NET 10, C#, SQLite FTS5, Miller content sidecar `content.db`, xUnit, existing Miller telemetry.

**Architecture Quality:** Low-to-medium architecture risk. The approved shape is to improve existing `search` behavior and existing output surfaces, not add a new MCP tool, embeddings, or a new index store. The main risk is over-widening text search and increasing noisy hits; mitigate it with shared lexical query planning, deterministic coverage thresholds, focused tests, and telemetry before/after comparison.

## Global Constraints

- Do not add semantic/vector search to Miller in this plan; semantic retrieval remains an Eros boundary unless the user explicitly changes that product direction.
- Do not add a new MCP tool.
- Do not introduce a `content.db` schema bump in phase 1.
- Do not modify the GitHub issue #4 `content read` workspace-routing diff except for conflict resolution.
- Preserve existing JSON result shapes unless a task explicitly says otherwise; compact output may include fallback guidance because that is agent-facing.
- Keep symbol search narrow to symbol/file behavior; do not mix broad source text directly into symbol ranking.
- Keep the default fast suite as the normal branch gate: `scripts/test.sh`.

---

## File Map

- Modify: `src/Miller.Core/Search/ContentSearchIndex.cs`
  - Extract or share query-planning logic currently private to in-memory content search: coverage terms, stopword filtering, phrase-required detection, and required coverage count.
- Create: `src/Miller.Core/Search/TextSearchQueryPlan.cs`
  - Small shared deterministic query-plan helper used by both in-memory content search and disk-backed text-content search.
- Modify: `src/Miller.Indexing/FtsTextContentSearchIndex.cs`
  - Replace strict all-term FTS candidate retrieval with staged lexical recall: strict candidates first, then OR candidates when needed, scored and filtered in C#.
- Modify: `tests/Miller.Tests/Indexing/FtsTextContentSearchIndexTests.cs`
  - Add regression coverage for high-coverage long queries, strict short queries, path/identifier precision, content-kind filtering, and test exclusion.
- Modify: `tests/Miller.Tests/Core/ContentSearchIndexTests.cs` if present, otherwise create focused coverage in the existing core search test file.
  - Lock parity between in-memory content search and FTS text-content search for query planning.
- Modify only if phase 2 is approved by measurement: `src/Miller.Server/Tools/SearchTool.cs`, `src/Miller.Server/Tools/SearchRouteExecutor.cs`, and `tests/Miller.Tests/Server/SearchToolTests.cs`
  - Add compact-only symbol fallback for identifier-ish/path-ish misses.

## Task 1: Lock Current Search Evidence And Failure Modes

**Files:**
- Modify: `tests/Miller.Tests/Indexing/FtsTextContentSearchIndexTests.cs`

**Work:**
- Add failing tests that describe the actual gap before changing implementation:
  - A long natural-language query with more than five meaningful tokens should return a chunk that matches at least 60% of meaningful terms even when no chunk matches every token.
  - A short two-to-five-term query should not return a chunk that only matches one term.
  - A path-like or identifier-like query containing `_`, `:`, `/`, or `\` should remain strict and require full phrase/coverage behavior.
  - Existing `content_kind`, language, and `excludeTests` filters should still apply after candidate widening.
- Add one parity-oriented assertion between `ContentSearchIndex` and `FtsTextContentSearchIndex` for the long-query coverage threshold, so the two lexical search paths do not drift again.

**Acceptance Criteria:**
- The new tests fail against the current strict-AND FTS implementation for the long-query high-coverage case.
- Existing FTS text-content tests still describe current behavior accurately.
- No production code changes in this task.

## Task 2: Extract Shared Lexical Query Planning

**Files:**
- Create: `src/Miller.Core/Search/TextSearchQueryPlan.cs`
- Modify: `src/Miller.Core/Search/ContentSearchIndex.cs`
- Modify: relevant core search tests under `tests/Miller.Tests/`

**Work:**
- Move the following behavior out of `ContentSearchIndex` private helpers into a shared core helper:
  - stopword-filtered coverage terms
  - fallback to all distinct terms when all terms are stopwords or short tokens
  - phrase-required detection for `_`, `:`, `/`, and `\`
  - required coverage count: all terms for one-to-five meaningful terms; `ceil(0.6 * termCount)` with minimum 2 for longer natural-language queries
- Keep the helper deterministic and allocation-light. It should not know about SQLite, chunks, files, or symbols.
- Update `ContentSearchIndex` to consume the helper without changing its output ordering.

**Acceptance Criteria:**
- Core search tests prove the helper keeps the existing thresholds and phrase-required behavior.
- `ContentSearchIndex` behavior is unchanged for existing tests.

## Task 3: Fix FTS Text-Content Recall Without Schema Changes

**Files:**
- Modify: `src/Miller.Indexing/FtsTextContentSearchIndex.cs`
- Modify: `tests/Miller.Tests/Indexing/FtsTextContentSearchIndexTests.cs`

**Work:**
- Replace the current `MATCH term1 AND term2 ...`-only candidate path with staged retrieval:
  - Build the query plan from shared `TextSearchQueryPlan`.
  - First retrieve strict candidates using all required coverage terms joined by `AND`.
  - If strict candidates produce fewer than `limit` filtered hits, retrieve widened candidates using coverage terms joined by `OR`.
  - Deduplicate candidates by `chunk_id`; strict candidates should be scored together with widened candidates, not duplicated.
- Score candidates in C# with existing BM25 term scoring, using document frequency from FTS counts.
- Filter candidates with the shared query plan:
  - For short or phrase-required queries, preserve strict all-coverage behavior.
  - For longer natural-language queries, allow the shared high-coverage threshold.
  - Keep `content_kind`, `excludeTests`, language, and file-pattern handling unchanged at the tool layer.
- Bound widened candidate retrieval so broad OR queries cannot hydrate the entire content corpus. Prefer rare-term ordering or FTS rank ordering before applying a hard cap; the cap must be deterministic and covered by a test or benchmark-style assertion.
- Preserve current sorting after scoring: score descending, display path, line, chunk id.

**Acceptance Criteria:**
- The Task 1 long-query test passes.
- Short-query and path/identifier precision tests pass.
- Existing `FtsTextContentSearchIndexTests`, `SearchToolTests`, and content search tests pass.
- No `content.db` schema change is required.

## Task 4: Measure Before Adding Cross-Route Fallback

**Files:**
- No required production files.
- Optional: add a small local script under `scripts/` only if there is already a nearby telemetry-analysis script pattern to follow.

**Work:**
- Capture before/after counts from `~/.miller/telemetry.db` for `tool='search'`, grouped by `op`, `outcome`, and `metadata_json.search_backend`.
- Replay a focused sample of recent empty `source`, `content`, and `all-text` searches if raw query text is available locally. If only target hashes are available, use aggregate telemetry plus deterministic fixture tests as the hard evidence.
- Record whether phase 1 reduces text-content false empties enough to defer S2.

**Acceptance Criteria:**
- Report the before/after empty rates for `source`, `content`, and `all-text`.
- Explicitly state whether remaining misses look like wrong-route queries, path/file queries, or legitimate no-match searches.
- Do not implement semantic fallback based on aggregate empties alone.

## Task 5: Add Compact Symbol Fallback Only If Measurement Justifies It

**Files:**
- Modify if needed: `src/Miller.Server/Tools/SearchTool.cs`
- Modify if needed: `src/Miller.Server/Tools/SearchRouteExecutor.cs`
- Modify if needed: `tests/Miller.Tests/Server/SearchToolTests.cs`

**Work:**
- Gate this task on Task 4 showing a meaningful remaining wrong-route class: text/file searches that miss but symbol search would have produced useful results.
- Add a compact-output fallback for zero-result searches when the query is identifier-ish or path-shaped:
  - Applies to `mode=source`, `mode=content`, `mode=all-text`, and `mode=file`.
  - Runs a symbol search with the same `workspace_id`, `file_pattern`, `language`, and test-exclusion policy where applicable.
  - Appends a `Symbol matches:` block after the no-results guidance.
  - Does not change JSON array shapes in this task.
- Record fallback count in telemetry metadata, for example `symbol_fallback_count`, without changing the top-level outcome contract unless the primary route result count changes by design.

**Acceptance Criteria:**
- Compact output helps agents recover from wrong-route symbol queries.
- JSON output remains backward-compatible.
- Telemetry can distinguish primary-route empties with fallback suggestions from true no-match empties.

## Task 6: Defer Trigram Content Search And Semantic Search

**Files:**
- No phase-1 production changes.

**Work:**
- Do not implement collapsed-trigram `content_fts` in this plan.
- Do not implement Julie-style semantic fallback in this plan.
- If Task 4 still shows high false-empty rates after Tasks 1-3, write a separate phase-2 plan for one of:
  - collapsed-trigram content arm in `content.db` with schema bump, or
  - Eros-owned semantic workflow integration outside Miller.

**Acceptance Criteria:**
- The branch does not introduce embeddings, vector store dependencies, or a content sidecar schema bump.
- Any remaining recommendation is evidence-based and separated from this deterministic recall fix.

## Implementation Status

- [x] Tasks 1-3 implemented: `FtsTextContentSearchIndex` now uses shared lexical query planning, strict candidates first, widened OR candidates second, and C# coverage/phrase filtering.
- [x] Task 4 measured as far as local telemetry allows: aggregate `~/.miller/telemetry.db` rows confirm the empty-result problem is concentrated in content-backed modes, but raw query text is not available for historical replay because telemetry stores target hashes.
- [x] Task 5 deferred: no compact symbol fallback was added in this implementation slice.
- [x] Task 6 honored: no content trigram schema bump, semantic fallback, embeddings, vector store, new MCP tool, or `julie-extractors` change was introduced.

## Verification Strategy

**Project source of truth:** `AGENTS.md` / `CLAUDE.md` define Miller's test split. Default verification is `scripts/test.sh`; scale tests are opt-in.

**Worker red/green scope:** For Tasks 1-3, run focused tests:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~FtsTextContentSearchIndexTests|FullyQualifiedName~ContentSearchIndexTests|FullyQualifiedName~SearchToolTests"
```

If the exact core test class name differs, use the discovered core search test class plus `FtsTextContentSearchIndexTests` and `SearchToolTests`.

**Worker ceiling:** Workers may run focused `dotnet test` filters and `scripts/test.sh`. Workers should not run `scripts/test.sh scale` unless implementation touches real extraction, full indexing, or `julie-extract` subprocess paths.

**Worker gate invariant:** Focused tests prove lexical query planning, FTS candidate widening, filters, and output compatibility.

**Lead affected-change scope:** Run `git diff --check`, focused tests above, and `scripts/test.sh` after the coherent batch.

**Branch gate:** `scripts/test.sh`. Add `dotnet build Miller.slnx -c Release` before commit/PR if analyzer warnings or public API changes are involved.

**Replay/metric evidence:** Hard gates are passing tests and no schema/tool-surface expansion. Report-only metrics are before/after empty rates by search mode and backend from `~/.miller/telemetry.db`.

**Escalation triggers:** Escalate before broadening scope if the fix requires a `content.db` schema bump, adding a new MCP tool, changing JSON result shapes, adding semantic/vector dependencies, or changing `julie-extractors`.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. For metric evidence, also record hard-gate metrics and report-only empty-rate metrics.

## Model Routing

**Project source of truth:** No `RAZORBACK.md` was present in the repo at plan time; use the active harness defaults unless the lead supplies a routing policy.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage
- Harness mapping: inherit

**Implementation tier:** bounded worker tasks from this plan
- Harness mapping: inherit

**Mechanical tier:** docs, rote edits, formatting, fixture updates without acceptance-gate ownership
- Harness mapping: inherit

**Gate-interpretation reviewer:** reviewer tier for deciding whether a failing test exposes a test bug or implementation bug
- Harness mapping: inherit

**Escalation tier:** schema changes, JSON shape changes, semantic/vector search, repeated verification failures, or broad ranking-quality regressions
- Harness mapping: inherit

**Worker eligibility:** Workers may implement Tasks 1-3 when they have inspected the target symbols with Miller and can run focused tests locally.

**Escalation triggers:** Use strategy/escalation tier before implementing Tasks 5 or 6, or before accepting noisy search output as a tradeoff.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, metric interpretation, replay evidence, or acceptance-gate decisions.

**Unsupported harness behavior:** If the harness cannot choose models per agent, use `inherit` and continue.
