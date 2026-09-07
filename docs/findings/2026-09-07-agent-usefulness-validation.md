# Validation of the agent usefulness review

The [original review](2026-09-07-agent-usefulness-review.md) identifies substantial real problems. Its proposed fixes are not all safe, several explanations omit existing behavior, and its historical timings should not be treated as fresh measurements. The [execution program](../plans/2026-09-07-agent-usefulness-program.md) assigns every verified issue to a plan and orders their shared dependencies.

## Scope and evidence

Validated Miller `40f7c71a7703b369998903e5f9a536fda006da57` on 2026-09-07 in `/home/murphy/source/miller`, branch `main`. The original review, docs index, and memory files already had local changes and were preserved. Validation stayed in that checkout because the task was to assess that uncommitted review and write documents; no implementation worktree was created.

Three independent review workers checked CT, retrieval, and navigation/editing. The lead checked workspace/guidance/storage, reviewed the resulting plans, re-inspected the CT wait and classifier defects, and reconciled shared ownership. Source exploration used Miller search/context/inspect/trace/impact. Read-only live calls covered workspace list/health/onboarding, pruning preview, overload references, impact, and bridge diagnostics. Source revisions advanced as planning documents were indexed; the source commit did not change.

Producer source was read-only at `/home/murphy/source/julie-extractors`, `main`, clean `b7a7c62a061c707dd5809d4c401bf36ed286fbbe`. Razorback source was read-only at `/home/murphy/source/razorback`, `main`, clean `464d4d317c206335e0b67ffdbc4f45a8e962b5e8`. Installed and injected guidance is host/version-specific; it is not interchangeable with these source checkouts.

The complete row-by-row evidence and disposition are in the four linked plans, rather than duplicated here:

| Original area | Validation and implementation owner |
|---|---|
| Freshness/workspace, guidance/skills, disk and retirement | [Workspace/guidance/storage](../plans/2026-09-07-workspace-guidance-agent-usefulness.md), validation IDs W1–W7, G1–G7, D1–D2 |
| Impact, Trace, Inspect, Edit | [Navigation/editing](../plans/2026-09-07-navigation-edit-agent-usefulness.md), validation ledger and tasks N1–N9 |
| Search, Context, Patterns/content | [Retrieval](../plans/2026-09-07-retrieval-agent-usefulness.md), validation IDs S1–S5, C1–C5, P1–P2, T1–T3 |
| Tests/CT, runnable test recipes, historical CT timeline | [CT](../plans/2026-09-07-ct-agent-usefulness.md), evidence table and tasks 1–7 |

## Material corrections

1. **Overloads:** zero visible references are a real problem. Attributing each call to every overload as exact would make impact and rename unsafe. Return an ambiguous overload family until facts identify one target. A receiver variable's spelling is not its type.
2. **Freshness:** the pending banner is unconditional on the background branch, including skipped scheduling. The background result is discarded. Equal served/registry revisions still do not establish that source files were checked. Report freshness and background activity independently; claim delegation only after a request is accepted.
3. **CT logs:** discovery already logs the full exception through `CtDaemonLog.FailureDetail`. Its persisted summary and navigation are deficient. A failed tracked project should still make aggregate status red; it needs a project-failure row and artifact link, not suppression of the failure.
4. **Existing safety:** asynchronous MCP retirement and intent-before-delete journaling already exist. Six missing-root refusals are unconfirmed worktree lineage, not evidence that the retirement queue is absent. Keep those safeguards and repair partial-success reporting.
5. **List behavior:** the source reserves a Current slot, and a live `limit=5` call kept Miller plus four errors. A deferred-primary process may have no Current row. Error-heavy selection is real; universally losing the current row is not.
6. **Retrieval:** token-phrase boosting, context graph neighbors, partial discovery disposition, error-state filtering, and degraded coverage already exist. The remaining defects concern literal priority and score representation, callers as pivots, anchor coverage, and missing-root filtering.
7. **Edit newline:** `insert_after` preserves a leading LF/CRLF. Its existing regression passed. Do not change that behavior. Signature whitespace and duplicate-signature body input are separate verified problems.
8. **Inspect miss:** the suggestion engine is lexical and already returns at most three suggestions. There is no semantic miss ladder to remove. Profile the work before selecting an optimization.
9. **Razor bridge:** three htmx facts preserve the interpolated `target_path`. Missing template interpretation and backend MapMethods recognition explain more than the proposed claim that extraction drops the URL. Preserve suffixes and uncertainty when matching templated paths.
10. **Hooks:** the current Codex manifest already declares hooks, and official docs describe trusted plugin-hook loading. The blanket no-Codex-hooks claim is outdated. Cursor needs a distinct response adapter. A hook path remains context to validate, not implicit workspace authority. [Codex hook documentation](https://learn.chatgpt.com/docs/hooks#plugin-bundled-hooks), [Cursor sessionStart documentation](https://prod.cursor.com/docs/hooks#sessionstart).
11. **Skill names:** this session exposes direct `mcp__miller__*` names. Replacing them everywhere with plugin-only names would break a supported installation. Test both forms. The current supplied Razorback guidance also includes tests and workspace_id.
12. **Retention:** the producer already implements logical/physical limits and compaction decisions. Large disk allocation establishes pressure, not which retained objects are safe to delete. Diagnose through that policy and expose its results; do not invent a second policy in Miller.

## Deeper defects and missing implementation requirements

- `TestsCore.WaitForDaemonToSettle` copies commandId into output but uses global executing-to-idle activity as completion. Another project/worktree can satisfy the wait. The command channel also loses the submitted ID on an ACK timeout. CT task 3 adds request-correlated completion.
- Fixing `ContentCorpusWriter.IsTestPath` alone leaves existing corpus rows misclassified at the same extractor revision. Retrieval task 1 includes a persisted policy version/migration and legacy/store convergence.
- Onboarding's rolling window ends at the last recorded event. A dormant workspace can present old errors as current friction indefinitely. Workspace task W4 anchors current recommendations to an explicit clock.
- Offloading selection without a family-wide limit could multiply memory pressure across worktrees. CT task 2 retains one active selection computation, bounded coalesced requests, and one owner for queue/store mutations.
- CT's disk-over-budget line has no corresponding agent-facing status fact. Workspace task D1 explicitly owns publication from existing accounting into TestsCore and health; status does not scan generation directories.
- Cross-workspace content source filtering must not turn shortened IDs or registry errors into implicit primary binding. Retrieval task 8 requires qualified IDs for all/registered and explicit failure distinctions.

## Reproductions and limits

| Check | Result |
|---|---|
| Live workspace read vs health | Pending read banner and fresh health at revision 87715 reproduced. |
| `workspace list limit=5` | Miller retained; four errors followed. |
| `workspace prune dry_run=true` | 33 would-prune, 60 kept, six unconfirmed-lineage refusals; whole-call refusal diagnostic reproduced. Nothing removed. |
| Overloaded `FullRebuildPromotion.Promote` exact ID | Zero exact refs plus suppressed fallback reproduced. |
| `trace mode=bridge target=/zzz/nope` | False frontend/backend route-presence statement reproduced. |
| Pinned one-file extraction | Bare C# return type absent; nullable return present. Fixture and SQL are preserved in navigation task N9's validation section. |
| Existing newline regression | `dotnet test --filter 'FullyQualifiedName~EditPlannerTests.InsertAfter_IsZeroWidthAtEndByte' --no-restore`, 1 passed, 24 ms reported test duration. No full suite rerun. |
| Physical Miller store file | `gen-001/store.db` size 15,473,782,784 bytes. |
| Read-only producer maintenance inspection | Exit 1, stale_plan / maintenance_inspection_raced, `coord.db changed during maintenance inspection`. Zero counters in that failure are invalid measurements. No GC or compaction applied. |

The 30-day telemetry table, 1.3% explicit CT-call adoption, exact retained-object counts, and five-task A/B are historical evidence from the original review. They were not independently reconstructed with a matching time window in this pass. Neither were the 11-minute CPU profile, exact discovery/build race, Windows behavior, or macOS behavior. Plans use these as motivations for controlled replay, not as proved universal latency or root-cause claims. Explicit tool telemetry cannot establish the absence of daemon auto-runs or shell tests.

No source code, settings, CT state, registry rows, or live store contents were intentionally changed. Miller's normal background refresh remains part of its read-tool operation. New repository artifacts are this validation, the execution program, four plans, and a memory checkpoint. Documentation verification checks paths, links, ownership, coverage, whitespace, and unchanged source state; it does not imply the planned fixes are implemented.
