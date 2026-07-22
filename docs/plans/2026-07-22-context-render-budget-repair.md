# Context Render Budget Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make `context token_budget` bound the rendered response instead of only the compact per-candidate estimate used before rendering.

**Architecture:** Keep candidate discovery, ordering, and `ContextPacker` unchanged. After the existing priority pack, render through the requested compact or JSON path and remove only lowest-priority tail items until the complete rendered response fits the existing `TokenEstimator`; JSON signatures and snippets use the same public render limits as compact output.

**Tech Stack:** .NET 10, C#, xUnit, Miller's existing `TokenEstimator`, `ContextPacker`, and tool renderers.

**Architecture Quality:** The change stays local to `ContextTool`'s render boundary and does not add a new service, dependency, MCP surface, or public parameter. The caller-facing interface remains `ContextTool.Run` / `RunReferenceAware`; tests prove the budget through those interfaces. Risk is medium because both MCP and CLI context routes share this core, but the repair preserves ordering and only removes tail items that could not fit the declared budget.

## Global Constraints

- This is the one focused Miller repair permitted by the approved visible benchmark plan.
- Do not change the benchmark manifests, evidence predicates, frozen snapshots, semantic canary, semantic model, MCP tool count, or normal semantic defaults.
- `token_budget` remains an estimated-token contract using the existing UTF-8-bytes/4 `TokenEstimator`; no tokenizer dependency enters Miller.
- Preserve candidate discovery, seed ordering, neighbour ordering, JSON field names, compact grouping, and deterministic bytes for responses that already fit.
- A non-positive budget still returns the existing valid empty response because an envelope cannot occupy zero bytes.
- Follow TDD and do not weaken existing context tests.

---

## Verification Strategy

**Project source of truth:** `AGENTS.md` testing and build rules; `tests/Miller.Tests/Server/ContextToolTests.cs` for the caller-facing context contract.

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter FullyQualifiedName~ContextToolTests`

**Worker ceiling:** The focused `ContextToolTests` class. The worker does not run the full fast suite, Scale suite, live benchmark, or branch gate.

**Worker gate invariant:** Both ordinary and reference-aware JSON/compact outputs fit a positive estimated token budget, retain priority order, and report a selected count equal to the rendered bundle.

**Lead affected-change scope:** Focused context tests plus `scripts/test.sh` and `dotnet build Miller.slnx -c Release`.

**Branch gate:** Python benchmark tests with warnings as errors, `RetrievalEval.Tests.csproj`, Release build, `scripts/test.sh`, `scripts/test.sh scale`, and `git diff --check`.

**Replay/metric evidence:** Hard gates are zero unresolved harness voids, a complete post-repair 12-pair visible rerun, unchanged frozen task/snapshot identities, and a fresh `agent-score` aggregate. Report-only diagnostics are raw model tokens, bytes, and the identical-corpus embedding timings.

**Escalation triggers:** Any change outside `ContextTool.cs` and its focused tests, any candidate-order change, an existing byte-determinism failure, or a post-repair benchmark that still emits a context response above its positive estimated budget.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. For replay or metric evidence, also record hard-gate metrics and report-only metrics. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Bound rendered context output | None - serial | Modify `src/Miller.Server/Tools/ContextTool.cs`; modify `tests/Miller.Tests/Server/ContextToolTests.cs` | Not applicable - single task. | Not applicable - single task. |

### Task 1: Bound rendered context output

**Files:**
- Modify: `src/Miller.Server/Tools/ContextTool.cs:205-282,596-838`
- Test: `tests/Miller.Tests/Server/ContextToolTests.cs`

**Interfaces:**
- Consumes: existing ordered `Candidate` / `ReferenceContextItem` lists, `ContextPacker.Pack`, `TokenEstimator.Count`, and compact/JSON renderer delegates.
- Produces: unchanged `Run` and `RunReferenceAware` signatures whose positive `tokenBudget` bounds the complete rendered string and whose `selectedCount` matches the retained items.

**Contract inputs:** The visible run recorded JSON context responses of 16,228, 34,279, 47,876, and 37,154 characters after calls requested `token_budget=3000`; both concept targets ranked first under both BGE-small and CodeRankEmbed on the identical 22-excerpt corpus, ruling out the embedding model as this repair.

**File ownership:** Modify `src/Miller.Server/Tools/ContextTool.cs`; modify `tests/Miller.Tests/Server/ContextToolTests.cs`

**Serialization required:** Not applicable - single task.

**Dependency reason:** Not applicable - single task.

**What to build:** Add one private generic render-budget helper used by both ordinary and reference-aware paths. It renders the initially packed ordered list with the requested renderer, removes the lowest-priority tail item while a positive budget is exceeded, and returns the final text and retained count; JSON rendering truncates optional signatures/snippets using the same established limits as compact rendering.

**Approach:** First add focused tests that reproduce the JSON undercount with long signatures and many candidates and prove the reference-aware path separately. Keep the fast existing pack as the first pass, then enforce the complete-response invariant at the render seam; do not alter discovery/ranking or introduce a tokenizer package.

**Acceptance criteria:**
- [x] Ordinary JSON and compact responses fit every tested positive `tokenBudget` according to `TokenEstimator.Count`.
- [x] Reference-aware JSON and compact responses fit every tested positive `tokenBudget` according to `TokenEstimator.Count`.
- [x] `selectedCount` equals the final rendered item count, priority order is unchanged, and responses that already fit remain byte-identical.
- [x] JSON signatures and snippets respect existing render limits so one pathological field cannot consume the whole response.
- [x] Zero/non-positive budgets preserve the valid empty-response behavior.
- [x] Worker-scope verification passes and the change is handed to the lead for inline review without a worker commit.
