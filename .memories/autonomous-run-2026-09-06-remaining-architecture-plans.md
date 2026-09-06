# Autonomous execution report: remaining architecture plans

**Status:** Verified, local integration pending

**Plans:** [Sidecar convergence costs](../docs/plans/2026-09-04-sidecar-convergence-costs.md), [Context query boundaries](../docs/plans/2026-09-04-context-query-boundaries.md), [Native-agent outcome evaluation](../docs/plans/2026-09-04-agent-outcome-evaluation.md)

**Branch:** `feature/remaining-architecture-plans`

**PR:** Not created. No push was authorized.

**Duration:** Not calculated. Checkpoint timestamps record the work sequence.

**Phases:** 3/3 implemented and verified

**Tasks:** 16/16 implementation tasks complete

**External-model policy:** No external reviewer or model campaign ran. Lead review stayed in this agent team.

## What shipped

- M2 added producer cursor integration and measurable incremental/full sidecar convergence. Cursor advancement follows sidecar commit, recovery is durable, and cleanup preserves other consumers.
- M4 split Context into the public adapter, query service, bundle builder, and pure renderer. MCP, CLI, and evaluator callers share the route, while the 16-case public fixture remains byte-identical.
- M5 added neutral task grading, a six-repository and six-language corpus, an isolated Codex runner, paired scoring, prepared dependencies, direct container qualification, and reproducible dry evidence.
- Public benchmark claims now separate the July and August evidence and state the bare-MCP baseline limits without presenting the comparison as native-agent evidence.

## Judgment calls

- [Agent outcome plan](../docs/plans/2026-09-04-agent-outcome-evaluation.md) keeps grading independent of Miller IDs and self-reported success. Source identities, allowed mutations, and repository-native verification decide outcomes.
- The CT campaign uses a private, qualified provider-case mapping. The agent sees only the public response schema, while the host verifies the exact affected provider case after the frozen source transition.
- Completion-only token reporting makes campaign ceilings soft for the attempt already in flight. Unknown prices, setup costs, and missing usage remain null. They are never reported as zero.
- Available S1 records identify different runtime bytes and process modes. They cannot qualify the prepared M5 image by version string, so semantic live admission remains refused.
- The proposed FTS rowid requirement was withdrawn after inspection showed readers join on `symbol_id`. No rowid rewrite was added.
- The proposed independently nullable token fields were also withdrawn. `RunRecord` requires all token totals measured or all null. Cached-input subset accounting remains separate and valid.

## External review

External review: none. This run used lead-only review and focused worker corrections. No external review campaign was requested or run.

## Review campaign

- **State:** Not run
- **Evidence:** Lead-only
- **Round:** 0/0
- **External invocations:** 0
- **Open critical/high:** 0
- **Open medium/low:** 0
- **Open at or above floor:** 0

## Tests

- M2 and M4 joint gates passed: Release build with 0 warnings/errors, fast suite 10,002 passed and 9 skipped, Scale 220 passed and 24 skipped, and the final cursor Scale check 2/2.
- M4's focused movement gate passed 468 tests with one platform skip. Its public fixture SHA-256 remains `308c7a27acf32fc7c2be2d94ad2ec98d015c6372f1de4a8fb2f7f208eb3b3f2c`.
- M2 verified five SQLite runs, Content/Search incremental-to-full logical parity, crash recovery, and all 40 supported languages with 237 non-empty language/kind groups.
- M5's final Python branch gate ran 436 tests: 433 passed, 3 skipped, and 0 failed. Ruff, format checking, Python compilation, link checks, and `git diff --check` passed.
- M5 physical evidence passed 24/24 isolation probes and 36/36 offline verifier states. The final paired CT proof matched source hashes across arms, performed zero CT work in native, and observed the exact affected case in treatment.
- The three Python skips were two Windows path-identity tests and one visible-corpus check requiring local `tree-sitter-c-sharp` and `tree-sitter-razor` checkouts. No skip was counted as a pass.

## Blockers hit

- None remain for implementation or local verification.
- A paid outcome campaign remains intentionally unrun. It needs an approved model, credential-free gateway, current pricing, a positive money ceiling, and an approval record bound to the frozen campaign.
- S1 semantic evidence does not match the prepared image. A separately qualified image/runtime/model observation is required before a semantic arm can run.

## Files changed

- M2 changes cover sidecar convergence details, producer cursor identity/session handling, Server orchestration, SQLite evidence, tests, and the joint finding.
- M4 changes cover Context characterization, query service, builder/model, renderer, adapter cleanup, caller migration, tests, and the joint finding.
- M5 changes cover benchmark contracts and corpus, runner/controller/CT/scoring modules, runtime image preparation, public docs, dry artifacts, findings, tests, and run memories.
- Exact pending integration inventory: `/tmp/miller-final-verification.eC3qbs/integration-files-inventory.txt`, SHA-256 `b18df71b8b4862eb426691add18158fbf5d6624a60b81b37c8e649b5108f7fcf`.

## Evidence

- M2/M4 finding: [2026-09-06-m2-m4-integration-verification.md](../docs/findings/2026-09-06-m2-m4-integration-verification.md)
- M5 qualification finding: [2026-09-04-agent-outcomes-harness-qualification.md](../docs/findings/2026-09-04-agent-outcomes-harness-qualification.md)
- M5 final verification: [final-verification-report.md](../.razorback/sdd/2026-09-04-agent-outcome-evaluation-0aee9f3d5a6b/final-verification-report.md)
- Prepared/runtime qualification: [physical-qualification-report.md](../.razorback/sdd/2026-09-04-agent-outcome-evaluation-0aee9f3d5a6b/physical-qualification-report.md)
- Final Python and CLI logs: `/tmp/miller-final-verification.eC3qbs`
- M2/M4 raw session record: `/home/murphy/.codex/sessions/2026/09/06/rollout-2026-09-06T00-16-33-01a07525-9800-7642-a723-6114e18fc54c.jsonl`
- Prepared image evidence: `/tmp/miller-agent-runtime-evidence.nGFJDI`

## Source control

- **Riding along:** M2 and M4 commits are on `feature/remaining-architecture-plans`. The verified M5 source, docs, evidence, and this report remain unstaged in the same worktree for lead integration.
- **Main:** `/home/murphy/source/miller` was not modified by this run. It is clean and was already one commit ahead of `origin/main` at the final inventory.
- **User worktrees:** `feature/ct-providers-jvm-ruby-php-gdscript`, `fix/julie-2402-adoption`, `fix/tool-latency`, and `fix/v1.27-postrelease-audit` are clean and untouched.
- **Unrelated local state:** `fix/v1.26.0-mcp-dogfood` retains its pre-existing untracked `.tools` path. This run did not clean or modify it.
- **Registry cleanup:** The prune dry run proposed 33 rows and reported 6 unconfirmed linked-worktree retirements. No cleanup ran because those entries are unrelated and unconfirmed.
- **Related repositories:** `julie-extractors` main is clean and aligned with origin at `b961e2dd1136189cd7e026536d4b52019ac1bce3`. `julie-semantic-sidecar` main is clean and five commits ahead of origin at `9ed082ba511aa8b10c9e7b47110c3a4dd1e98d59`.
- No worktree was removed. No branch was pushed, merged, released, or published.

## Next steps

- Lead reviews the final integration inventory, stages only the accepted M5 and report files, and commits them locally.
- Keep the branch and worktree until the user chooses whether to merge or push.
- Do not run the paid campaign until the missing approval inputs are frozen and explicitly authorized.
