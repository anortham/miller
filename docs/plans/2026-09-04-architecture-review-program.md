# Architecture review follow-up program and agent dispatch plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Turn the September 4 review into independently executable work that improves safe shared-store operation, predictable resource costs, maintainability, and evidence of agent usefulness.

**Architecture:** Preserve the existing extraction/query/embedding ownership split. Julie owns producer registration and maintenance contracts. Miller owns consumer lifetimes, sidecar convergence, query services, and agent-outcome evaluation. The semantic sidecar supplies existing runtime conformance and cost evidence rather than a speculative new model or protocol.

**Tech Stack:** Existing .NET 10/SQLite/MCP, Rust/rusqlite/tree-sitter, Rust/llama.cpp sidecar, Python evaluation tooling.

**Architecture Quality:** High risk for cross-process retention; medium for cache lifetimes, query extraction, and evaluation; low for documentation correction and existing-runtime qualification. The program does not authorize a rewrite, new MCP tools, new search features, or a model-default change. Each detailed plan contains its concrete affected interface, rejected shortcuts, test surface, and acceptance criteria.

**Status (2026-09-06 UTC, 2.40.2 rollout correction):** Julie Audit Plan 4 and J1 are integrated. Published julie-extract 2.40.2 adds immediate store-writer transactions after the successful 2.40.1 startup exposed import lock failures. Source CI and all four release targets passed. Miller M1 is implemented; the 2.40.2 pin and explicit reader capability passed Linux fast/Scale/build and focused Windows installed-package qualification at `eb93670f` for local main integration. The [restart-recovery finding](../findings/2026-09-05-reader-retention-restart-recovery.md) records the incident, preliminary-report corrections and qualification limits; the [M1 finding](../findings/2026-09-04-reader-retention-integration.md) retains the native identity limits. A user rebuild/restart must still demonstrate completion of the owed full upgrade and discharge of scan failure. M3 is completed and qualified on branch `feature/fact-cache-resource-accounting`; M2 is next. M4 and M5 remain planned and can be developed independently. S1 CPU evidence is on semantic-sidecar main, but reviewed correction `5c41f1d` remains unmerged; Vulkan qualification and M5 task efficacy remain unverified/not-run. No semantic runtime or Miller marketplace release is part of this follow-up.

## Global Constraints

- Julie **Audit Plan 4 is merged at `bb93a721`**. Do not repeat its refactors. The review found only a leftover unused Vue parsing wrapper and stale closure records; bounded correction ownership is separate from J1's store files.
- Read each repository's current `CLAUDE.md`/`AGENTS.md`, its test strategy, and the assigned plan before editing. Repo rules override copied historical plan assumptions.
- Use Miller to orient, inspect actual interfaces, and assess impact before implementing. Recheck line numbers and APIs against the assigned commit.
- The lead owns architecture and integration decisions. The user removed the earlier requirement to use Sol workers; select execution tooling under the current session instructions. Workers do not broaden scope, redesign contracts, or spawn additional agents.
- No production files were changed during plan preparation. Plans are documents in the main checkouts; creating them does not merge or release any pending dependency work.
- Do not create a new MCP tool. Preserve current public output contracts and the CLI/MCP common query behavior unless a detailed plan explicitly specifies an additive producer CLI contract.
- Keep `Miller.Core` free of I/O dependencies. Extraction recognition remains in Julie across every supported language, not in Miller.
- For changes consuming extractor data, preserve all-language behavior. At the integration gate record `SELECT language, kind, COUNT(*) FROM symbols GROUP BY language, kind` against a real exported extractor artifact, compare its language inventory with Julie's supported-language contract, and exercise the changed read/projection behavior across that inventory. Row counts prove presence, not correctness; retain the existing per-language contract fixtures. Do not infer 40-language parity from a C#-only test.
- Preserve exact/fallback provenance, uncertainty, lexical parity, source freshness, and CT's refusal/unknown rules. A full fallback that preserves correctness is preferable to inventing complete delta history.
- `MILLER_SEMANTIC=off` and `MILLER_CT=off` perform zero work for their respective optional systems.
- Paid model calls, new downloads requiring consent, pushes, releases, deployments, and publishing require explicit existing authorization. Prepare concrete evidence and commands before asking; do not pause ordinary local implementation or dry verification.
- No tests or acceptance criteria may be weakened to obtain a green result. Fix in-scope failures and continue. A genuine contract or authority mismatch goes to the lead.
- Preserve unrelated dirty files and existing worktrees. Never use stash, reset, checkout, or broad cleanup to hide them.

## Planning snapshot and changes already in flight

| Repository | Root used for planning | Observed base | Interpretation |
|---|---|---|---|
| Miller | `/home/murphy/source/miller` | `90220d7978fb4b59c593f8fd85b8bb8035700c32`, `main`, ahead of origin by one commit | Source baseline; review notes were already untracked. |
| Julie | `/home/murphy/source/julie-extractors` | `4d4ab1cd98f4eeeddb1725208ee1d71372b91538`, `main` | Wave 3 is now merged. Do not schedule its optimizations again. |
| Julie Plan 4 | `/home/murphy/source/julie-extractors/.worktrees/audit-4-dead-code-and-api-narrowing` | Observed advancing from `4d4ab1cd` to `e46b4ae2` during preparation | Live user work; re-inventory before using any result. |
| Semantic sidecar | `/home/murphy/source/julie-semantic-sidecar` | `60998defc5d2458358f0531cba9e8b7792508a23`, `main` | Existing v0.1.0 implementation and qualification scripts. |

These are provenance records, not commands to reset branches. The execution base should include the relevant merged prerequisites and preserve newer user work.

Julie receiver-type facts v1/v2 and extraction epoch 9 already ship in 2.39.0. Miller 1.27.0 already consumes them. The future-dated receiver-wave filename is historical planning evidence, not a missing feature. See `docs/release-notes/v1.27.0.md`.

Miller's older output-budget and context/impact latency findings have subsequent fixes. In particular, `docs/findings/2026-08-29-direction-aware-impact-reads.md` closes the recorded impact gate. This program must not reopen those tasks merely from an earlier audit.

The labels A7/A8 are ambiguous across repositories: Miller's old Ph3 labels mean reader retention and sidecar convergence; Julie's September audit A7/A8 mean different hot-path findings. Always cite a plan filename and the invariant, never an unqualified label.

## Plan inventory and priorities

| ID | Plan | Owner | Observable outcome | Prerequisite |
|---|---|---|---|---|
| J1 | [Producer retention contract](../../../julie-extractors/docs/plans/2026-09-04-producer-retention-contract.md) | Julie | Atomic reader admission and maintenance that cannot delete a live reader's exact roots | Julie Plan 4 merged and reviewed; verify its Task 6 export changes. |
| M1 | [Reader retention integration](2026-09-04-reader-retention-integration.md) | Miller | Every production family-store session owns a producer registration from before open until after final close | J1 contract frozen; real integration requires its built producer. |
| M2 | [Sidecar convergence costs](2026-09-04-sidecar-convergence-costs.md) | Miller | Honest fallback reasons, crash-safe consumer cursors, and reproducible incremental/full cost evidence | Existing cursor CLI; serialize session/reclaim changes with M1. |
| M3 | [Fact-cache resource accounting](2026-09-04-fact-cache-resource-accounting.md) | Miller | Soft retained budget and unique live-cache lifetime accounting, including evicted-but-held caches | Serialize `FamilyStoreReadSession` edits with M1. |
| M4 | [Context query boundaries](2026-09-04-context-query-boundaries.md) | Miller | Cohesive query/build/render responsibilities with byte-equivalent public behavior | Capture baseline before moving code; no producer dependency. |
| M5 | [Agent outcome evaluation](2026-09-04-agent-outcome-evaluation.md) | Miller | Honest current claims and a native-agent, product-neutral, outcome/cost experiment | Dry harness independent; paid campaign separately authorized. |
| S1 | [Runtime qualification and cost evidence](../../../julie-semantic-sidecar/docs/plans/2026-09-04-runtime-qualification-and-cost-evidence.md) | Semantic sidecar | Exact binary/model/backend conformance and runtime-cost evidence for M5 | Existing model files/hardware for physical tests; dry checks independent. |

Priority order is retention safety, convergence/resource predictability, query maintainability, and stronger product evidence. This is a prioritization rule, not a requirement to serialize independent documentation or test-harness work.

## Cross-repository contract decisions

### Reader registrations and consumer cursors have different jobs

Reader registrations protect an exact snapshot and its file-version/generation roots while a process reads it. Consumer cursors protect log history needed to bring a sidecar forward. A cursor is not a reader pin; a pin does not imply a sidecar has consumed a delta.

The producer plan owns the authoritative proposed reader CLI/report/schema. M1 must copy field names and failure semantics from that plan, not invent its own C# variant. Reader acquire must complete before Miller opens any generation SQLite handle. A lost acquire reply must be retryable without making a second registration.

A heartbeat deadline is diagnostic, never sufficient proof that a paused live reader can be reclaimed. Automatic cleanup requires definitive death of the original process instance, including birth identity so a reused PID is not confused with the old owner. If identity cannot be established, retain roots and report the cleanup debt. This deliberately strengthens the older v4 time-expiry text. Do not claim that strengthening provides a hard reclamation-time bound.

Acquisition, GC root collection, and maintenance mutation must agree under the existing maintenance intent/fencing protocol. A new registration between planning and apply must invalidate the plan or be included in protected roots. Old maintenance binaries must not be allowed to ignore registrations; compatibility enforcement is a release prerequisite, not an optional test.

The pin references the existing immutable manifest root, not a copied list of every version per reader. GC preserves that manifest, its entries, all referenced versions, and its physical generation. Per-reader acquisition performs indexed metadata lookups and one registration insert; the 1k/10k/100k fixture gate checks constant operation counts. Version enumeration belongs to maintenance. While holding the coordinator admission transaction, producer store access is query-only and non-waiting; contention rolls back the whole admission before any retry. No reader may take a store writer lease, run migrations, or wait for a writer while holding the coordinator transaction.

### Cursor and sidecar ordering

Establish a conservative cursor before consuming retained history. Commit sidecar data and stamp before advancing that cursor. A crash after publication but before cursor advancement leaves extra retained history, not lost history. A rejected advancement remains owed and retryable even when the sidecar itself is already current. Validate the producer-reported generation because the existing cursor CLI derives it from the store rather than accepting an expected-generation flag.

### Resource limits are described honestly

The fact-cache budget controls retained entries, not total process RSS. Live sessions may hold evicted entries; shared entries count once when reporting unique resident estimates. No plan may kill reads, force global garbage collection, or relabel a soft budget as a hard bound.

### Evaluation responsibility stays in Miller

The semantic sidecar certifies vectors/protocol/runtime behavior. Miller measures retrieval and coding-task outcomes. S1 cannot prove agent usefulness through embedding throughput, and M5 must not join task results with costs from a different binary/model/backend. No model/default change follows automatically from an inconclusive comparison.

## Verification Strategy

**Project source of truth:** Each repo's current `CLAUDE.md`/`AGENTS.md`, Julie `docs/testing-strategy.md`, and the detailed plan's exact commands.

**Worker red/green scope:** The narrow class/module or script pattern in the assigned task. No whole suite per edit.

**Worker ceiling:** The scope named by that plan. Workers investigate and fix failures within their ownership, then return evidence for lead review.

**Worker gate invariant:** Observable task behavior plus preservation of the shared contracts above. Passing mocks alone cannot close cross-process GC or exact-binary conformance claims.

**Lead affected-change scope:** Run once per coherent merged batch. Use Miller impact to select tests; retain the source and runtime identity used by the checks.

**Branch gate:** Miller production changes require `dotnet test` once on the final coherent tree and `dotnet build Miller.slnx -c Release` with zero warnings/errors. Indexing/extract/CT-provider changes require `scripts/test.sh scale`; physical Windows lifecycle verification is required for J1/M1 before claiming safety on Windows. Julie/sidecar exact branch commands are in their plans. Python/docs-only M5 uses its own verification tier.

**Security scope:** Each plan declares its scope. Review new producer nonce handling, local process boundaries, safe paths, and public evidence redaction. No broad external-model disclosure is authorized by this planning document.

**Replay/metric evidence:** Correctness, parity, no unsafe deletion, and complete accounting are hard gates. Runtime cost metrics require a fixed fixture and identified machine. Positive treatment lift is not a test gate; report a negative or inconclusive experiment honestly.

**Escalation triggers:** Schema/compatibility changes, failure of a GC race test, missing platform evidence, public output changes, inaccessible prerequisites, or requests to publish/spend beyond authority.

**Assigned verification failure:** Continue fixing in scope. Escalate only a genuine blocker or change to the agreed architecture/product contract. Never stop merely because a task boundary was reached.

**Verification ledger:** Each plan records exact commit, command, UTC time, scope, results, skip reasons, and artifact identities. A green unchanged tree is not rerun. A result against a previous binary is historical evidence, not certification of a new build.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Finish existing Julie Plan 4 | Existing owner | Its existing worktree and task matrix | Yes | User's current work has priority; J1 follows its export narrowing. |
| M4 characterization/refactor | A | Files enumerated in M4 | No | None - safe parallel batch. |
| M5 harness/docs | A | Files enumerated in M5, including public benchmark pages | No | None - safe parallel batch. |
| S1 qualification | A | Sidecar scripts/evidence/docs enumerated in S1 | No | None - safe parallel batch. |
| J1 producer contract | B | Producer coordinator/maintenance/schema/CLI files in J1 | Yes | Plan 4 must land; freeze report before M1 adapter implementation. |
| M1 consumer integration | C | Miller read-session and registration-runner files in M1 | Yes | J1 contract and binary; consumer opens must not outrun producer safety. |
| M3 cache lifetime changes | D | Cache store and shared read-session files in M3 | Yes | M1 touches the same disposal/open paths. |
| M2 cursor/convergence integration | E | Sidecar/reclaim/converger files in M2 | Yes | Rebase after M1/M3 integration so lifecycle ownership remains coherent. |
| Final paired integration and campaign | F | Verification findings and each plan's ledgers | Yes | Final binaries and schema identities must be frozen; paid campaign approval is separate. |

Documentation-only portions of M2/M3 and fake-process adapter tests may be prepared earlier if their exact files do not overlap. The lead must explicitly split ownership before dispatch; do not put two workers on `FamilyStoreReadSession.cs`, `docs/known-limits.md`, or `docs/README.md` simultaneously.

Commit mode is `parallel-lead-commit` unless the assigned detailed task explicitly uses a serial worker commit. A lead commits only reviewed owned files after verification and a Goldfish checkpoint. Push/release approval is still separate.

## Execution preflight for every assigned plan

1. Record `pwd`, `git rev-parse --show-toplevel`, `git rev-parse HEAD`, `git status --short --branch`, and `git worktree list`.
2. Check related worktrees with `git -C <exact-path> status --short --branch`. Reuse the task worktree when continuing; do not escape dirty task work by branching from main.
3. Confirm the assigned plan is actually in the chosen worktree. Untracked planning docs do not follow `git worktree add`. Either commit the intended planning docs first, or deliberately copy the assigned plan and referenced contract into the new worktree, compare their hashes, and record the move. Never stash or discard review notes to make branching easier.
4. Inspect the named existing symbols and verify prerequisites from commit ancestry and tests. Checked plan boxes or future-dated filenames are not proof of implementation status.
5. For J1/M1, compare both copies of the proposed CLI/report contract before writing code. If they disagree, the lead fixes the plans first.
6. Run the narrow baseline named by the plan, recording pre-existing failures. Do not rerun a green broad suite solely to establish a baseline.
7. Dispatch one bounded task per worker with exact ownership, input/output contracts, tests, and forbidden scope. The lead reviews each diff and reconciles integration before the next dependent batch.

## Integration acceptance matrix

| Scenario | Required result | Evidence owner |
|---|---|---|
| Old reader across generation promotion and GC | Original snapshot remains readable; no root disappears before release | J1 + M1 |
| Reader paused past heartbeat deadline | Retained, even if cleanup is delayed | J1 + M1 |
| Reader crashes or PID is reused | Only definitively dead original owner may be reaped | J1 |
| Producer reply lost after acquire | Retry returns same registration; no leaked duplicate | J1 + M1 |
| Ordinary writer and reader contend across databases | Reader completes or rolls back without waiting while holding coordinator admission | J1 |
| Manifest grows from 1k to 100k files | Constant registration row and metadata-query count; GC still retains every version | J1 |
| Sidecar commit then cursor process failure | Correct published sidecar; conservative cursor and retry remain | M2 |
| Cursor generation differs from session | No claimed incremental protection; conservative recovery | M2 |
| Cache eviction with live session | Reader still works; unique held bytes remain accounted | M3 |
| Context extraction/refactor | Existing compact/JSON/cancellation/read-count behavior preserved | M4 |
| Semantic off | No broker/model/vector work | M4 + M5 |
| Native baseline answers without Miller IDs | Neutral verifier accepts correct result | M5 |
| Missing hardware/model/budget | Explicit unverified/not-run status; no invented pass | S1 + M5 |

## Final handoff packet

Each completed plan returns its worktree path, branch, commit, dirty state, changed files, test ledger, open approval boundaries, and its contract/evidence artifacts. The program lead checks every criterion against the original review and these plans.

Reader safety is not complete until the producer and consumer have passed their joint race tests on Linux and Windows and older maintenance binaries are fenced. Resource work is not complete merely because a counter exists. Refactoring is not complete with duplicate old/new engines still present. Evaluation implementation can be qualified without paid runs, but task efficacy remains unmeasured until an authorized campaign completes.

J1 execution selects development version `2.40.0` because its permanent `min_writer_version` gate must exclude old v2.39.0 writers while supporting existing stores. This is not a release approval or a Miller pin selection. Once code is integrated, the repository's release process determines the verified candidate and the user approves publishing it.

## Historical preparation ledger

The table below preserves the accepted pre-M1 handoff. The current status paragraph above and each plan's qualification finding supersede its integration/publication state.

| Plan | State at preparation | Implementation evidence |
|---|---|---|
| J1 | Complete and qualified locally; integration/publication not performed | Branch `feature/reader-retention-contract` in Julie `.worktrees/reader-retention-contract`, verified implementation/test candidate `4ca16853ecb054f6989aafa1410381f41273adde`. Final changed-path/default/xtask/certification, contract, standalone crash, formatting, warning-free Clippy, and documentation gates pass. Required Windows admission, liveness, retention, rollback, crash, CLI, and mixed-version checks pass. Exact evidence: Julie `docs/evidence/2026-09-producer-retention-contract.md`. |
| S1 | CPU evidence and review corrections verified locally | Main `9ed082b`; correction branch `fix/s1-evidence-alignment` at `5c41f1d` is clean and unmerged. All four branch gates passed, including68 Python tests. Original BGE/Qwen CPU records unchanged; Vulkan unverified, M5 efficacy null/not-run. Four overwritten preliminary transcripts are explicitly disclosed. |
| M3 | Complete and qualified on branch `feature/fact-cache-resource-accounting` | Soft 256 MiB retained budget, unique live-cache accounting via `CacheResourceSnapshot`, `RevisionFactCacheLease` ownership with double-dispose and stale-loader fencing, all production holders migrated with zero bare-cache escape, full/bounded parity preserved across eviction/revision switch/duplicate/oversized entries on real SQLite fixtures, and deterministic resource benchmark at `scripts/bench-fact-cache-resources.sh`. RSS sampled separately and not inferred from cache estimates. |
| M2, M4, M5 | Planned, not executed | Existing dependency order remains. M5 shared required metric keys match S1; no outcome campaign was run. |

Update this table only from accepted implementation evidence. Do not mark the whole program complete when only the documentation or dry harness is ready.
