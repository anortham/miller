# Agent usefulness execution program

This is the execution map for the [validated review](../findings/2026-09-07-agent-usefulness-validation.md). Implementation and corrective dogfood work now exist in the source checkouts. The [2026-09-08 verification ledger](../findings/2026-09-08-agent-usefulness-dogfood.md) records verified behavior, test scope and remaining checks; unchecked boxes below are the original acceptance checklist, not a current claim that no implementation exists.

**Status — 2026-09-08:** Source verification and the public julie-extract 2.41.1 pin adoption are complete; the [adoption finding](../findings/2026-09-08-julie-extract-2.41.1-adoption.md) records the four public hashes, epoch-10 upgrade and final Miller gates. Actual trusted, untrusted and disabled hook delivery in named Codex/Cursor runtimes remains unverified; adapter tests alone do not close G2. Miller release/Windows verification is not claimed. Final integration/comparison status follows the verification ledger.

## Plans

| Plan | Scope | Task labels |
|---|---|---|
| [Workspace, guidance, storage](2026-09-07-workspace-guidance-agent-usefulness.md) | Truthful freshness/recovery, compact diagnostics, recent onboarding, hooks/skills, retention visibility, partial prune | W1–W4, G1–G2, D1–D2 |
| [Navigation and editing](2026-09-07-navigation-edit-agent-usefulness.md) | Evidence precision, impact pages, refs, members, edit safety, read costs, rename and batch edits, producer return/template facts | N1–N9 |
| [Retrieval](2026-09-07-retrieval-agent-usefulness.md) | Classification repair, ranking/evidence, context anchors, pattern values, source scope, long lines, missing-root search, producer route facts | R1–R9, meaning numbered tasks 1–9 in that plan |
| [Continuous testing](2026-09-07-ct-agent-usefulness.md) | Fail-closed selection, responsive activity, command-correlated wait, discovery artifacts, hints, runner recipes across providers | C1–C7, meaning numbered tasks 1–7 in that plan |

## Implementation order

1. Start W1, C1, R1, R4, R6, N4, and N5 independently. These establish honest state/evidence and fix local correctness without requiring new public tool capabilities.
2. Follow C1 with C2 then C3. N2 waits for C1 because both touch `QueryTimeResolutionReader.cs`. Follow W1/R1 with W2, then W3 and W4. Integrate N6 after W1; it owns the shared projection-cache decision used by R5.
3. Land R2 before R3/R8; R9 follows R8. R5 profiles the corrected R4 path and reuses N6's cache owner only when evidence supports it.
4. Develop producer R7 and N9 under one coordinated source owner for shared framework recognition/tests. Implement all applicable language cases before adoption. Existing literal and nullable facts must not regress. Producer release and Miller pin changes follow their normal explicit approval gates.
5. Within CT, integrate C5 and C6 after the host/protocol work; then C4 advice and C7 documentation/replay. They share TestsCore and queue/provider contracts, so do not dispatch all of them against the same files at once.
6. Integrate N1 after N2 and C6; N3 after N2, with templated-route adoption after N9/R7. N7 follows N2/N5; N8 follows N5/N6/N7. N6 cannot run concurrently with N5/N7/N8 edits to EditTool or W1 provider edits.
7. Land D1 after W3/W4 and CT's TestsCore changes; D2 serializes with the other WorkspaceTool/WorkspaceRender tasks. Finish integration task I1 below, then G1/G2 and the final verification ledger. Docs-only corrections can be drafted earlier but must describe the final behavior.

This ordering is a dependency graph, not a requirement to delay a small independent correction until every large feature finishes. The lead may split renderer-only changes from provider additions when each slice builds, tests, and preserves compatibility. Keep the same acceptance criteria and record ownership before dispatch.

## Shared contracts and ownership

- `QueryTimeResolutionReader.cs`: C1 reverse-read optimization before N2 evidence changes. Revalidate CT conservative selection after both.
- `WorkspaceIndexProvider.cs`: W1 background-state correction before N6 immutable projection reuse. R5 does not introduce a second cache.
- `ContentCorpusSidecar.cs`: R1 persisted classification convergence before W2 typed errors. Neither change may erase the other's freshness signal.
- `GraphTraversal.cs` and evidence adapters: N2 owns path-wide certainty; N1/N3/CT consume it without reclassifying ambiguity as exact.
- `TestsCore.cs`, CT protocol, and provider contracts: serialize C2/C3/C5/C6/C4 as their local dependencies require; D1 accounting projection follows them.
- `ImpactTool.cs`, `EditTool.cs`, `CrossToolHandoff.cs`: N1, edit tasks, and R3 own their implementation phases; I1 integrates the final CT handoff after those changes.
- `WorkspaceTool.cs` / `WorkspaceRender.cs`: W2, W3, W4, D1, D2 serialize their edits. Each uses the same assembled evidence rather than private competing status reads.
- Producer `markup.rs` and `structural_facts.rs`: one owner coordinates R7 dictionary recognition and N9 template evidence. The C# return fix is independent but shares the parity/release gate.
- `scripts/julie-pins.json`: one lead-owned adoption after all required producer facts are verified. No unapproved dependency release.
- Core, hook block, `.agents/skills/`, mirrored skills, setup snippet, CLAUDE/AGENTS, and install docs: G1/G2 consolidate after final contracts; no worker edits installed plugin caches.

## I1: connect impact and applied edits to the CT decision

**Files:** `src/Miller.Server/Tools/ImpactTool.cs`, `EditTool.cs`, `CrossToolHandoff.cs`, `TestsCore.cs` only if a shared read-only projection is required; tests `tests/Miller.Tests/Server/ImpactToolTests.cs`, `EditToolTests.cs`, `CrossToolHandoffTests.cs`, and `TestsToolTests.cs`. New APIs, if required, are internal proposals and must be agreed with C4/C6 first.

**Dependencies:** C4/C6, N1, N7/N8, R3. Run serially because it integrates their shared files. Commit mode is parallel-lead-commit; the lead reviews the combined diff.

Use the existing compact-only handoff decision table and the CT plan's read-only state projection. After an applied edit or a useful impact result, when the selected workspace has enabled CT and stale/owed work is observed, offer the precise next step. If state has not caught up with the edit, suggest `tests status`, not an unsupported claim that particular tests are already stale. If idle selection scope is known, a run hint states the candidate count and scope; ongoing selection or a pending command points to status. Reuse cached/bounded metadata and never start inventory, a daemon, provider execution, or a full impact query just to generate a hint. Disabled CT uses the provider-owned direct-run recipe where available and does not get an enable prompt as its primary action. Preview-only edits do not claim new stale work.

- [ ] Impact and applied-edit results offer CT guidance for the explicitly selected workspace, including a process with no bound primary.
- [ ] Preview, CT-off, unknown state, red, stale/owed, and active-selection cases have truthful distinct advice.
- [ ] Missing/stale CT evidence never becomes an assertion that a cheap precise run exists.
- [ ] Hints add no JSON next_actions changes, process spawn, filesystem writes, or second expensive traversal.
- [ ] Focused tool/handoff tests pass and descriptions/routing guidance match the same decision.

## Coverage of the ten obvious gaps

| Original gap | Owning tasks |
|---|---|
| 1. Runnable test selector | C6, N1, I1 |
| 2. Tests-only impact and evidence split | N1, N2 |
| 3. Complete class members | N4 |
| 4. Batch/multi-hunk edits | N8 |
| 5. Homonym-aware rename | N2, N7 |
| 6. Non-renamed doc/string/test-name mentions | N7, with producer-backed exact references separated from text mentions |
| 7. Context relevance and edited-file callers | R4 |
| 8. CT discovery diagnostics | C5 |
| 9. Evidence-based freshness | W1, W2, G1 |
| 10. Source-scoped content, complete lines, literal ranking | R2, R8 |

The review's recommendations are covered by the same tasks: freshness W1/W2; impact N1/N2; CT loop C1–C3; hook/core G1/G2; edit cost N6 and body guard N5; overload refs N2; classifier R1; context R4/R5; runners C6; skills G1; storage D1/D2; trace N2/N3. Lower-severity table rows remain assigned in the detailed validation ledgers.

## Decisions embodied in these plans

- Name CT beside impact while preserving opt-in and direct focused tests. Tool order does not grant permission to enable CT.
- Provide separate impact row budgets and bounded complete-row pages; preserve old contracts explicitly when introducing the new view/paging contract.
- Preserve uncertainty throughout trace, impact, rename, and runnable selectors. Callbacks and method-group references are not execution evidence by themselves.
- Reduce duplicate Razorback guidance with a source-level fallback, not by assuming every host injects Miller rules.
- Keep existing producer retention policy. Diagnose allocation/protection and expose pressure before proposing different retention limits.
- Use task-level implementation plans with concrete files, evidence, regression scenarios, and acceptance criteria. Proposed interfaces are identified as proposals; speculative full implementation snippets would incorrectly suggest the shared contracts already exist.

## Completion and verification

Implementation completion means every verified table row and every applicable acceptance box is closed. A disproved suggestion is closed by its preserved evidence, not by an unnecessary code change. A historical performance claim is closed by a controlled replay and the appropriate measured fix if a defect reproduces. Record unresolved external access or product-policy decisions explicitly; never call a missing language/provider gate a pass.

Use the focused red/green scopes from each plan, then one fast suite and warning-free Release build for the coherent implementation boundary. Indexing, extraction, graph/provider changes require the documented Scale gates. Verify all applicable languages with real extraction evidence; run Windows verification before any release. Record source SHA, tree state, invariant, command, outcome, and timestamp; reuse unchanged green evidence.

For this planning task, verification is the validation ledger, path/link/coverage checks, peer review of dependencies, the single existing newline regression, and the isolated C# extraction. No production changes, broad suite, live cleanup, commit, push, or release were performed. Implementation can begin after a separate instruction to execute these plans.
