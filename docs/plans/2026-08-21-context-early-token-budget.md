# Context Early Token Budget Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make the `context` tool's token budget bound the work it does, not just the output it keeps — approved by the user 2026-08-21 ("we don't want to pay the cost to get the info and then throw it away").

**Architecture:** The usage branch today reads reference evidence for EVERY candidate (`BuildReferenceItems`, `ContextTool.cs:2523`) and only then packs to budget (`ContextPacker.PackAllocated`, `ContextPacker.cs:58`). The fix reads evidence incrementally, in candidate order, in batched chunks, and stops once the built items already carry an overscan multiple of the budget — then packs the built subset with the unchanged packer. Work becomes proportional to the budget; packing semantics inside the read window are unchanged.

**Tech Stack:** C#/.NET 10, Miller.Core + Miller.Server, xUnit.

**Architecture Quality:** `ContextPacker` (Miller.Core) is untouched; the change lives in the evidence-read loop of `ContextTool`. Risk: the seed-gate lesson (commit `acef33e3`) — a retrieval limit is a ranking input, and a "harmless" widening/narrowing changes output. Mitigations: an explicit overscan window with a named constant, a byte-identical A/B test at default budget on a fixture where the window covers everything, and a mutation-proven read-count test that FAILS when the stop condition is removed.

## Global Constraints

- Evidence (findings doc `docs/findings/2026-08-21-context-latency-diagnosis.md`): `--token-budget 100` costs the same as `2000` today; the `reference_items` phase measured 17.1 s cold / 16.2 s warm per cross-workspace usage call at HEAD `3c8ab6ce` — a per-call cost, the caches are not the problem.
- The off branch (`reference_mode=off`) output must stay byte-identical for every budget — this plan touches only the reference/usage item construction. (The off branch packs already-built cheap candidates; no evidence reads to bound.)
- Deviation contract for the usage branch: output may differ from today ONLY when the unread tail beyond the overscan window would have contributed packed items. At default budget on the test fixtures the window must cover all candidates, proven by an A/B assert (old path vs new path byte-identical).
- The stop condition is a named constant (suggested `ReferenceReadOverscanFactor = 2`): stop reading once built items' summed `TokenCost` ≥ budget × factor, and always read at least one chunk.
- Evidence reads use the existing batched `ReferenceEvidenceReader.ReadMany` in chunks (suggested chunk = 8 candidates), never per-symbol round trips — the promotion-read batching lesson (`ab22a402`).
- `exclude_tests` filtering must run BEFORE reads (it already does — keep it that way so filtered candidates cost nothing).
- Phase instrumentation: `reference_items` keeps its phase stamp; add the count of candidates read vs skipped to the phase log line so the effect is measurable in telemetry.
- No new MCP tools, no tool-parameter changes; `token_budget` semantics as documented stay valid.
- Build: 0 warnings 0 errors (`dotnet build Miller.slnx -c Release`; Debug if the running server locks Release). Tests: focused classes only in the inner loop.

## Verification Strategy

**Project source of truth:** repo CLAUDE.md (testing split; wrapper scripts).

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~ContextToolTests"` and `dotnet test --filter "FullyQualifiedName~ContextPackerTests"`.

**Worker ceiling:** the two focused filters above plus any new test class added by this plan.

**Worker gate invariant:** (a) read-count boundedness — with a tight budget and many candidates, the number of candidates whose evidence is read is ≤ the window, asserted by a counting fake `readReferenceEvidence`, and the assert FAILS when the stop condition is disabled (mutation-proven, the vacuous-test lesson from `acef33e3`); (b) byte-identical A/B at default budget on a fixture the window covers.

**Lead affected-change scope:** `scripts/test.ps1` (fast suite) once the batch lands.

**Branch gate:** fast suite + `scripts/test.ps1 scale` at the branch gate per CLAUDE.md; cite the existing pass for unchanged trees.

**Security scope:** none declared.

**Replay/metric evidence:** report-only — re-run the two live cross-workspace usage probes from the findings doc and record the new `reference_items` phase time next to the 16.2 s baseline. Not a hard gate (machine-load dependent).

**Escalation triggers:** any change to `ContextPacker` selection semantics, or any off-branch output diff → stop and report.

**Assigned verification failure:** Workers stop and report when assigned verification fails.

**Verification ledger:** record command, scope, SHA, result, timestamp per task.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Budget-bounded reference reads | None - serial | Modify: `src/Miller.Server/Tools/ContextTool.cs`; Test: `tests/Miller.Tests/Server/ContextToolTests.cs` | Not applicable - single implementation task | Task 2 is measurement/docs only. |
| Task 2: Measure and record | None - serial | Modify: `docs/findings/2026-08-21-context-latency-diagnosis.md` | Yes | Needs Task 1 built and served. |

Commit mode: `serial-worker-commit`.

## Task 1: Budget-bounded reference reads in the usage branch

**Files:**
- Modify: `src/Miller.Server/Tools/ContextTool.cs` (`BuildReferenceItems` :2523-2740 and its caller `RunReferenceAwareActionableWithCancellation` :1199-1300)
- Test: `tests/Miller.Tests/Server/ContextToolTests.cs`

**Interfaces:**
- Consumes: `ContextPacker.PackAllocated` unchanged (`src/Miller.Core/Graph/ContextPacker.cs:58`); `ReferenceEvidenceReader.ReadMany` (used by the usage path near `ContextTool.cs:243`); `TokenEstimator.Count` (`src/Miller.Server/Telemetry/TokenEstimator.cs:17`).
- Produces: the same `IReadOnlyList<ReferenceContextItem>` shape; a phase log line carrying candidates-read vs candidates-skipped counts.

**Contract inputs:** Global Constraints above; `ReferenceReadOverscanFactor`; chunked `ReadMany`.

**File ownership:** Modify: `src/Miller.Server/Tools/ContextTool.cs`; Test: `tests/Miller.Tests/Server/ContextToolTests.cs`

**Serialization required:** Not applicable - single implementation task

**Dependency reason:** Task 2 is measurement/docs only.

**What to build:** Restructure `BuildReferenceItems` (or its call site) so candidates are processed in existing order, evidence is fetched with chunked `ReadMany`, items accumulate with their `TokenCost`, and the loop stops at the overscan threshold. Pack the built subset with `PackAllocated` exactly as today. Surface read/skip counts through the existing context-phase logging.

**Approach:** TDD with a counting fake for the evidence reader. Tests: (1) tight budget + 100 candidates → reads bounded by the window, assert fails when the stop is commented out (verify by temporarily reverting the guard, per the mutation-proof rule); (2) default budget + small fixture → output byte-identical with a reference implementation that reads everything (keep the old full-read path callable from the test, or capture its output as the expected value before wiring the stop); (3) `exclude_tests` still prevents reads for excluded candidates; (4) chunking: reads arrive as `ReadMany` batches, never one-by-one.

**Acceptance criteria:**
- [ ] Read-count bounded under tight budgets, mutation-proven.
- [ ] Byte-identical output at default budget where the window covers all candidates.
- [ ] Off-branch behavior untouched (no diff in its tests).
- [ ] Phase log line carries read/skipped counts.
- [ ] Focused tests pass; change committed per commit mode.

## Task 2: Measure and record

**Files:**
- Modify: `docs/findings/2026-08-21-context-latency-diagnosis.md` (open question 4 block)

**Interfaces:**
- Consumes: Task 1 landed; a rebuilt binary serving the store.
- Produces: the recorded before/after for `reference_items`.

**Contract inputs:** baseline 27.8 s / 21.1 s totals, 17.1 s / 16.2 s `reference_items`, recorded 2026-08-21.

**File ownership:** Modify: `docs/findings/2026-08-21-context-latency-diagnosis.md`

**Serialization required:** Yes

**Dependency reason:** Needs Task 1 built and served.

**What to build:** Re-run the two identical cross-workspace usage probes (`reference_mode=usage`, workspace `julie-extractors`, same query) against the new build, read the phase lines, and append the measured effect to the question-4 block. Note the read/skip counts the new log line reports.

**Acceptance criteria:**
- [ ] Before/after phase numbers recorded in the findings doc.
- [ ] Change committed per commit mode.
