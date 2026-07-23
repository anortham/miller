# Miller Julie Takeover Audit Plan

**Execution status:** Complete. The audit used one broad Claude review plus nine fresh tool-specific Claude reviews; the resulting accepted, corrected, rejected, and unproven dispositions are recorded in the findings matrix.

> **For agentic workers:** REQUIRED SUB-SKILL: Use `razorback:claude-cli` for the independent review requested by the user. Use `razorback:systematic-debugging` for suspected defects and `razorback:architecture-quality` when a finding implies changing a public tool or module boundary.

**Goal:** Produce a source-verified, behavior-verified comparison matrix that identifies everything Miller must keep, change, replace, add, or delete before it can take over Julie's local agent-information role with higher correctness, lower token cost, fewer calls, and lower wall time.

**Architecture:** This is an evidence-producing audit, not an implementation plan. It compares each Miller MCP tool with the equivalent Julie tool or workflow, traces each behavior through extractor contracts and consumer code, tests representative failure-prone paths read-only, and records an unrestricted recommendation. The completed findings document becomes the source for a separate multi-phase implementation plan.

**Tech Stack:** .NET 10, Rust, SQLite schema 4 / extract contract 3, MCP, FTS5/BM25, vector retrieval through `julie-semantic-sidecar`, Miller telemetry and agent-efficiency evidence, Claude Code CLI read-only review.

**Architecture Quality:** The audit may recommend preserving, deepening, replacing, adding, consolidating, or deleting caller-facing tools. No existing MCP-count preference, historical ownership boundary, or current implementation shape may suppress a supported finding. Architecture risk is high because the resulting implementation plan may change public MCP contracts, retrieval composition, and cross-repository ownership.

## Global Constraints

- The objective is to get an agent the information it needs to act correctly with the fewest calls, fewest delivered tokens, lowest wall time, and highest evidence quality.
- Every claim about current behavior must cite current source, a current artifact query, a direct tool reproduction, or current benchmark evidence.
- Historical plans and findings are leads, not proof; current source and behavior win when they disagree.
- Julie and `julie-semantic-sidecar` are read-only during this audit. Do not build, test, index, refresh, start, stop, or modify either active workspace.
- Claude review is read-only and independent. Every Claude claim must be validated against source or behavior before entering the accepted findings.
- Claude must participate in every one of the nine tool passes through a fresh, focused, ephemeral read-only review. The initial cross-project review does not satisfy this requirement.
- Review all nine Miller MCP tools: `search`, `inspect`, `context`, `trace`, `impact`, `edit`, `content`, `patterns`, and `workspace`.
- Compare workflows, not only names. A Miller tool may map to several Julie tools, and a Julie capability may map to a Miller tool plus CLI, skill, or missing orchestration.
- New tools, deleted tools, renamed tools, replacement tools, and contract-breaking redesigns are all permitted recommendations when evidence supports them.
- Preserve Miller behavior only when it contributes to takeover quality, compatibility that still matters, or a load-bearing safety/reliability invariant.
- Semantic evaluation must distinguish model quality, retrieval/routing policy, evidence composition, indexing reliability, activation defaults, and hardware/runtime concerns.
- Extraction shortcomings belong to `julie-extractors`; consumer misuse belongs to Miller; shared-sidecar defects belong to `julie-semantic-sidecar`. The matrix must name the owning repository for every required change.
- Any extractor-backed recommendation must account for every supported language rather than treating one-language evidence as complete.
- Do not produce the implementation plan in this audit. Produce prioritized, implementation-ready requirements and acceptance proofs from which the full multi-phase plan can be written.

## Deliverables

1. `docs/plans/2026-07-22-miller-julie-takeover-audit-plan.md` — this execution contract.
2. `docs/findings/2026-07-22-miller-julie-takeover-matrix.md` — the complete comparison matrix, accepted findings, disputed findings, evidence ledger, and takeover requirements.
3. A validated initial Claude review plus nine validated tool-specific Claude review sections in the findings document, including agreements, corrections, and rejected claims.
4. A prioritized takeover backlog grouped by owning repository and by P0 correctness, P1 agent efficiency, P2 capability/operability, and P3 optional optimization.
5. Explicit readiness gates that define when Miller has actually surpassed Julie rather than merely reached feature parity.

## Evidence Standard

Each matrix row must carry:

- **Workflow:** the agent task being answered.
- **Miller surface:** tool, mode, parameters, CLI/skill fallback, and guidance.
- **Julie surface:** equivalent tool or composed workflow.
- **Miller implementation:** exact symbols, tables, sidecars, ranking/resolution path, output renderer, and error behavior.
- **Julie implementation:** the same evidence categories at current committed HEAD.
- **Observed behavior:** direct reproduction, artifact query, telemetry, or benchmark row when available.
- **Verdict:** Miller advantage, parity, Miller shortcoming, Miller defect, Julie defect not to port, or unproven.
- **Root cause:** extraction, indexing, resolution, ranking, orchestration, rendering, guidance, lifecycle, or contract design.
- **Recommendation:** keep, improve, replace, add, consolidate, delete, or move ownership.
- **Owner:** Miller, `julie-extractors`, `julie-semantic-sidecar`, Eros, skill/docs layer, or multiple repositories with an explicit contract boundary.
- **Priority:** P0 through P3.
- **Acceptance proof:** exact unit, contract, live-extract, replay, benchmark, platform, or agent-trajectory evidence required before shipping.
- **Confidence:** high, medium, or low with missing evidence stated.

A capability is not credited merely because a similarly named tool exists. The current implementation must deliver correct, bounded, actionable evidence under realistic ambiguity, large-output, stale-index, cross-workspace, and failure conditions.

## Comparison Matrix Taxonomy

| Verdict | Meaning |
| --- | --- |
| Miller advantage | Miller should preserve the behavior unless a stronger redesign subsumes it. |
| Parity | Both products adequately satisfy the workflow; no takeover blocker exists. |
| Miller shortcoming | Miller works but costs more calls, tokens, time, or agent reasoning than Julie. |
| Miller defect | Miller returns incorrect, misleading, incomplete, unsafe, or contract-inconsistent results. |
| Julie defect not to port | Julie behavior is inferior or unreliable and must not become a parity target. |
| Unproven | Evidence is insufficient; the exact proof needed is recorded. |

## Tool-by-Tool Audit Questions

Every tool pass must answer all applicable questions:

1. Does the tool use the strongest available extractor/artifact data, or does it discard identity, confidence, spans, relationships, or metadata?
2. Does it resolve ambiguity honestly and efficiently across duplicate names, overloads, languages, files, and workspaces?
3. Does the default route match the task an agent is likely attempting?
4. Does one call return enough evidence to answer or act, or merely tell the agent which calls to make next?
5. Are compact and JSON outputs hard-bounded, deterministic, continuation-safe, and semantically equivalent?
6. Do empty, partial, stale, unavailable, corrupt, and product-error states preserve truth and provide the smallest useful recovery?
7. Are semantic, lexical, graph, content, and structural arms fused appropriately without hiding provenance?
8. Are test linkage, impact, references, callers, callees, source bodies, and confidence exposed at the point the workflow needs them?
9. Does guidance cause unnecessary chaining, over-inspection, mode guessing, or unsupported claims?
10. Is telemetry sufficient to distinguish true absence, retrieval failure, resolution failure, truncation, and agent misuse?
11. Does the equivalent Julie workflow do better, and if so is the advantage implementation, default, interface, or guidance?
12. Should Miller keep, redesign, replace, add, consolidate, or delete the surface?

Every tool pass also requires its own fresh Claude CLI review focused on that Miller tool and the equivalent Julie tool or workflow. Each matrix section must identify the Claude invocation, summarize its claims, and classify each material claim as accepted, corrected, rejected, or unproven after local source and behavior validation.

## Audit Tasks

### Task 1: Establish Current Identity And Prior-Evidence Baseline

**Files:**
- Read: `CLAUDE.md`
- Read: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Read: `docs/adr/ADR-0001-guidance-delivery-channels.md`
- Read: `docs/adr/ADR-0003-semantic-retrieval-ownership.md`
- Read: `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md`
- Read: `docs/findings/2026-07-22-miller-julie-agent-efficiency-visible-baseline.md`
- Read: `/Users/murphy/source/julie/JULIE_AGENT_INSTRUCTIONS.md`
- Read: `/Users/murphy/source/julie/CLAUDE.md`

**What to produce:** Record exact repo/worktree/commit identity, active public surfaces, prior benchmark limitations, superseded assumptions, and evidence that remains current.

**Acceptance criteria:**
- [ ] Every compared repository has an exact committed HEAD and dirty-state record.
- [ ] Historical claims are marked current, superseded, or requiring revalidation.

### Task 2: Seed The Findings With The Reference-Resolution Defect

**Files:**
- Create: `docs/findings/2026-07-22-miller-julie-takeover-matrix.md`
- Read: `src/Miller.Server/Tools/TraceTool.cs`
- Read: `src/Miller.Indexing/ExtractReader.cs`
- Read: `src/Miller.Indexing/SymbolGraphReader.cs`
- Read: `src/Miller.Indexing/SymbolDetail.cs`

**What to produce:** Document that `trace mode=refs`, inspect references/callers, and reference-aware context resolve a target symbol but query identifier rows by bare name, ignoring available `target_symbol_id`, precise relationships, and resolved pending relationships. Include live artifact counts, the scoped `ContextTool.Run` reproduction, affected surfaces, root cause, immediate Miller fix, extractor follow-up, and acceptance proof.

**Acceptance criteria:**
- [ ] The defect is supported by source, current artifact queries, and a direct tool reproduction.
- [ ] The finding distinguishes reference-list defects from graph/path/impact behavior that already consumes stronger data.

### Task 3: Obtain And Validate Independent Claude Review

**Files:**
- Read-only review roots: `/Users/murphy/source/miller/.worktrees/semantic-maturity-decision` and `/Users/murphy/source/julie`
- Modify: `docs/findings/2026-07-22-miller-julie-takeover-matrix.md`

**What to produce:** Ask a fresh ephemeral Claude CLI session to compare the products, validate the reference defect, and identify additional capability, correctness, efficiency, and workflow gaps. Validate every material claim locally and record accepted, corrected, and rejected review items. This is the broad baseline review; Tasks 4-12 still require separate focused Claude passes.

**Acceptance criteria:**
- [ ] Claude preflight records binary, version, auth method, plan, and organization.
- [ ] Claude has read-only `Read,Grep,Glob` access with strict MCP isolation and no session persistence.
- [ ] The findings document separates Claude's opinion from locally validated conclusions.

### Task 4: Audit `search`

**Miller surface:** `search` auto/text/symbol/file/markers/content/source/external/web/all-text, region filtering, lexical/semantic fusion, rescue, sidecars, filters, and empty guidance.

**Julie equivalents:** `fast_search` lexical/semantic/hybrid, mixed file/symbol search, source-region filtering, line enrichment, scope rescue, semantic fallback, and `get_symbols` follow-up.

**Acceptance criteria:**
- [ ] Ranking inputs, routing defaults, semantic activation, fusion, provenance, output bounds, corpus coverage, and failure recovery are compared.
- [ ] Model choice is separated from routing and evidence-composition quality.
- [ ] RC3 integration assumptions are recorded as pending release facts rather than guessed.
- [ ] A fresh Claude `search` comparison is completed and every material claim is locally classified.

### Task 5: Audit `inspect`

**Miller surface:** file listing and symbol summary/overview/full, smart resolution, bodies, children, complexity, references, callers, callees, freshness guards, and hints.

**Julie equivalents:** `get_symbols`, `deep_dive`, targeted minimal extraction, linked tests, implementations, semantic neighbors, progressive depth, and hard budgets.

**Acceptance criteria:**
- [ ] The pass tests exact names, duplicate names, overloads, large bodies, large relationship sets, stale bodies, linked tests, and semantically related symbols.
- [ ] Shared reference defects are not double-counted but their impact on inspect is explicit.
- [ ] A fresh Claude `inspect`/`get_symbols`/`deep_dive` comparison is completed and every material claim is locally classified.

### Task 6: Audit `context`

**Miller surface:** lexical seed selection, task signals, graph expansion, reference-aware mode, packing, render budget, next-inspect guidance, and semantic exclusion.

**Julie equivalent:** `get_context` hybrid pivots, full pivot bodies, graph neighbors, edited files, failing tests, stack traces, test preference, adaptive allocation, and spillover.

**Acceptance criteria:**
- [ ] The pass determines whether each default can answer a task in one call and why agents continue after useful retrieval.
- [ ] Evidence composition, semantic seeding, test linkage, stop guidance, and continuation are compared independently.
- [ ] A fresh Claude `context`/`get_context` comparison is completed and every material claim is locally classified.

### Task 7: Audit `trace`

**Miller surface:** refs, path, auto, bridge providers, confidence bands, ambiguity handling, limits, and next actions.

**Julie equivalents:** `fast_refs`, `call_path`, web mode, SQL/query edges, semantic near-symbol fallback, and deep-dive relation views.

**Acceptance criteria:**
- [ ] Every edge source is traced from extractor table through target resolution to output.
- [ ] Precise, inferred, ambiguous, and unresolved evidence remain distinguishable.
- [ ] The audit recommends whether refs/path/bridge remain one tool or should be redesigned, split, replaced, or partly deleted.
- [ ] A fresh Claude `trace`/`fast_refs`/`call_path` comparison is completed and every material claim is locally classified.

### Task 8: Audit `impact`

**Miller surface:** target/path/diff/git seeds, reverse reachability, likely tests, graph limits, compact/JSON rendering, and telemetry.

**Julie equivalent:** `blast_radius` symbol/file/revision seeds, centrality/hop ranking, related tests, web callers, deleted-file handling, and spillover.

**Acceptance criteria:**
- [ ] Seed correctness, edge provenance, ranking, test linkage, truncation, continuation, changed-line mapping, and cross-language coverage are compared.
- [ ] Fast but misleading over-approximation and slow but precise behavior are both treated as defects, not acceptable tradeoffs.
- [ ] A fresh Claude `impact`/`blast_radius` comparison is completed and every material claim is locally classified.

### Task 9: Audit `edit`

**Miller surface:** text replacement, rename, symbol rewrite operations, match proof, dry-run/apply, freshness, atomicity, and post-edit index behavior.

**Julie equivalents:** `edit_file`, `rewrite_symbol`, `rename_symbol`, DMP fuzzy matching, targeted minimal reads, and required reference/deep-dive workflow.

**Acceptance criteria:**
- [ ] Safety, expressiveness, token cost, preview fidelity, stale state, ambiguity, multi-file atomicity, and recovery are compared.
- [ ] The audit decides whether Miller's consolidation is an advantage or a discoverability/interface liability.
- [ ] A fresh Claude `edit`/Julie editing-workflow comparison is completed and every material claim is locally classified.

### Task 10: Audit `content`

**Miller surface:** workspace source/docs/config corpus, external-file and web import, search/list/read/remove, bounded windows, continuation, freshness, and cross-workspace audit.

**Julie equivalents:** lexical/source-region search, external web-research skill workflow, file reads, and any absence of a durable imported corpus.

**Acceptance criteria:**
- [ ] Corpus ownership, import lifecycle, retrieval quality, duplication, bounding, provenance, and deletion are compared.
- [ ] Miller-only capabilities are tested for actual agent value rather than credited by feature count.
- [ ] A fresh Claude `content`/Julie content-workflow comparison is completed and every material claim is locally classified.

### Task 11: Audit `patterns`

**Miller surface:** generic structural-fact list/search/summary/facets and extractor-owned pattern catalog.

**Julie equivalent:** `patterns` over the same extractor contract.

**Acceptance criteria:**
- [ ] Contract parity, genericity, discovery guidance, filtering, output bounds, telemetry, and language parity are compared.
- [ ] Any divergence over the shared artifact is classified as consumer behavior, not extraction capability.
- [ ] A fresh Claude `patterns` comparison is completed and every material claim is locally classified.

### Task 12: Audit `workspace`

**Miller surface:** status, refresh, full rebuild, health, list/open/remove/prune, onboarding, leadership, dashboard, registry, sidecar state, and cross-workspace routing.

**Julie equivalent:** `manage_workspace`, registry/index lifecycle, health/repair, primary/target workspace behavior, leader/follower handling, and dashboard workflows.

**Acceptance criteria:**
- [ ] Startup, freshness, concurrency, rebuild promotion, version compatibility, corruption recovery, sensitive-root safety, registry lifecycle, and agent-facing status are compared.
- [ ] Operational complexity is judged by reliability and agent cost, not by feature breadth alone.
- [ ] A fresh Claude `workspace`/`manage_workspace` comparison is completed and every material claim is locally classified.

### Task 13: Audit Cross-Cutting Product Behavior

**Areas:** server guidance, tool descriptions, skills, CLI parity, telemetry, output contracts, continuation, error taxonomy, semantic defaults, sidecar lifecycle, packaging, release/install, platform acceleration, language parity, cross-workspace behavior, and Eros handoff contracts.

**Acceptance criteria:**
- [ ] The matrix identifies problems spanning multiple tools and assigns one architectural owner.
- [ ] The audit names obsolete ADRs, plans, comments, tests, or assumptions that would preserve known defects.
- [ ] Miller advantages and Julie defects not to port are recorded with the same rigor as Miller shortcomings.

### Task 14: Finalize Takeover Requirements And Readiness Gates

**Files:**
- Modify: `docs/findings/2026-07-22-miller-julie-takeover-matrix.md`
- Modify: `docs/README.md`

**What to produce:** Convert accepted findings into a prioritized requirement catalog without designing the implementation phases prematurely. Include ownership, dependencies, contract impact, migration/compatibility concern, and exact acceptance evidence for every requirement.

**Acceptance criteria:**
- [ ] Every Miller tool has a complete matrix section and disposition.
- [ ] Every P0/P1 requirement has exact acceptance proof and repository ownership.
- [ ] Readiness gates require Miller to meet or beat Julie on correct completion, call count, delivered tokens, wall time, product-error rate, and critical workflows.
- [ ] No placeholders, deferred investigations, unsupported claims, or unvalidated Claude findings remain.
- [ ] The documentation map points to both the audit plan and current findings.

## Verification Strategy

**Project source of truth:** `CLAUDE.md`, `docs/README.md`, current ADRs/contracts, current committed source in Miller and Julie, and current SQLite artifact schemas.

**Worker red/green scope:** Documentation structure checks, JSON/SQL parse checks for recorded evidence, exact source-link existence, and focused direct tool reproductions.

**Worker ceiling:** Read-only source and artifact inspection plus documentation edits. No Miller product code, Julie files, sidecar files, indexes, servers, releases, or external state changes.

**Worker gate invariant:** Every accepted finding is reproducible and distinguishes observed fact, inference, recommendation, and unproven hypothesis.

**Lead affected-change scope:** `git diff --check`; heading/link/path validation; scan for placeholders; compare matrix coverage against all nine registered MCP tools and all Julie equivalents.

**Branch gate:** Miller fast tests are not required for documentation-only changes unless a generated-doc or instruction synchronization surface is modified. Run the narrowest documentation/link integrity checks available and record why code tests were not triggered.

**Replay/metric evidence:** Existing visible agent-efficiency results are evidence, not the final takeover gate. A future sealed paired benchmark must be created from the completed requirements and must not optimize against its sealed tasks.

**Escalation triggers:** Any edit to product code, generated guidance, tool registration, schemas, package manifests, or active index state requires leaving this audit plan and creating an approved implementation plan with the repository's full verification tiers.

**Assigned verification failure:** Investigate and correct documentation/evidence failures in this audit. Do not weaken evidence criteria to make the matrix look complete.

**Verification ledger:** Record artifact/query identity, source commit, command or tool call, result, and timestamp for material behavioral claims.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
| --- | --- | --- | --- | --- |
| Tasks 1-2 | None - serial | Audit plan and initial findings | Yes | The evidence standard and initial defect establish the matrix schema. |
| Task 3 | None - serial | Claude review section in findings | Yes | Claude must review the initial scope and defect, then its output must be validated before acceptance. |
| Tasks 4-12 | Conceptually independent, executed serially by the lead | One findings section and one fresh Claude review per tool; no product files | Yes | One lead owns the shared matrix and must reconcile each focused Claude review immediately to avoid contradictory dispositions. |
| Task 13 | None - serial | Cross-cutting findings section | Yes | Requires all tool-level evidence. |
| Task 14 | None - serial | Requirement catalog and docs map | Yes | Requires the completed matrix and validated findings. |

## Completion Definition

The audit is complete only when:

- all nine Miller tools and all equivalent Julie workflows have evidence-backed dispositions;
- the reference-resolution defect and every additional correctness defect have an owner and acceptance proof;
- the broad Claude review and all nine tool-specific Claude reviews have been independently checked rather than copied;
- cross-cutting concerns and Miller-only advantages are represented;
- the takeover backlog is complete enough to write the full multi-phase implementation plan without another discovery audit;
- the findings distinguish parity from superiority and define a sealed benchmark gate proving Miller has surpassed Julie;
- no work was deferred merely because it was large, inconvenient, cross-repository, or inconsistent with an earlier product boundary.
