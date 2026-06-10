# Review Findings Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Fix all findings from the 2026-06-10 multi-agent review of the freshness/leader work: one silent-corruption bug, five robustness gaps in the cross-process machinery, two agent-UX residuals, fast-suite sleeps, and doc drift.

**Architecture:** All fixes are local hardening of existing seams — no new modules or interfaces. The one behavior change agents will see: a stale-gate edit that self-heals now re-plans from the converged index instead of applying pre-recovery spans, and missing-required-param tool calls return a friendly tool error instead of a protocol exception.

**Tech Stack:** .NET 10, xUnit, ModelContextProtocol SDK, SQLite.

**Architecture Quality:** No new seams; complexity stays local to `EditService`, `LeaderScanRequestQueue`, `LeaderWriteThrough`, `LeaderIdentityFile`, `IndexerService`, `SmartTargetResolver`, and `TelemetryCallToolFilter`. Caller-facing interfaces (MCP tool shapes, CLI verbs) are unchanged except for friendlier error payloads. Tests prove behavior through the tool/service entry points callers use. Architecture risk: low.

---

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (test split + build rules).

**Worker red/green scope:** `dotnet test tests/Miller.Tests --filter "FullyQualifiedName~<TestClassName>"` for the touched test class(es); must show the new test fail before the fix and pass after.

**Worker ceiling:** `scripts/test.sh` (fast suite, ~13s). Workers do not run the Scale suite.

**Worker gate invariant:** each task's acceptance criteria below; new tests must exercise the caller-facing entry point (EditService.Execute / MCP round-trip / queue drain API), not private plumbing.

**Lead affected-change scope:** `dotnet build Miller.slnx -c Release` (0 warnings/0 errors) + `scripts/test.sh` after each batch.

**Branch gate:** `scripts/test.sh all` (fast + Scale) before final handoff — the indexing/extract path is touched.

**Escalation triggers:** any Scale-suite failure, or any fix that requires changing `FreshnessGate` semantics or the MCP tool schemas — stop and report.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record command, scope, SHA, result per task in the task report.

## Model Routing

**Project source of truth:** none (`RAZORBACK.md` absent); user CLAUDE.md grants subagent autonomy.

**Strategy tier:** lead (this session) — inherit.
**Implementation tier:** workers — inherit.
**Mechanical tier:** Task 7 (docs/test-sleep cleanup) — inherit (harness default).
**Gate-interpretation reviewer:** lead — inherit.
**Escalation tier:** lead — inherit.
**Worker eligibility:** all tasks are bounded with exact file targets.
**Escalation triggers:** Scale failures, FreshnessGate semantic changes, schema changes.
**Mechanical exclusion:** Task 7 owns no failing-test evidence beyond the suite staying green.
**Unsupported harness behavior:** n/a (Claude Code Agent tool).

## Batching

- **Batch 1 (parallel, disjoint files):** Tasks 1, 2, 3, 5, 7.
- **Batch 2 (single worker, after Batch 1 — all centered on `IndexerService.cs`):** Tasks 4 and 6.
- Each worker commits its own task(s) locally (no push). Run `scripts/test.sh` before each commit.

---

### Task 1: Re-plan after gate-time freshness recovery (H1) + harden recovery poll (L2)

**Files:**
- Modify: `src/Miller.Server/Tools/EditService.cs` (`FinishSingleFile` ~:219-244, `ExecuteRename` ~:248+, `TryRecoverFreshness`/poll loop ~:516-541)
- Test: `tests/Miller.Tests/Server/EditToolTests.cs`

**What to build:** When the apply-path freshness gate finds the index stale and `TryRecoverFreshness` succeeds, the current code applies the pre-recovery plan whose spans came from the stale index — if drift shifted the symbol's offsets (e.g. lines prepended), the splice rewrites the wrong bytes silently. After successful recovery, the plan must be rebuilt from the now-converged index before applying.

**Approach:**
- `replace_text` plans derive spans from disk content, not the index — they are safe to apply unchanged after recovery.
- For symbol ops (`replace_symbol_body`, `replace_symbol_signature`, `insert_before`, `insert_after`, `add_doc`) and `rename_symbol`: after recovery reports fresh, re-run resolve + span read + plan (one internal retry of the existing pipeline) and apply the fresh plan. If re-resolution fails (symbol id changed and gone), return the existing clean "no recorded span / not found" error — never apply the stale plan.
- Guard against recursion: the retry runs with the gate already known-fresh; do not re-enter recovery from the retry.
- L2: wrap the in-loop `FreshnessGate.Check` call inside the recovery poll in try/catch; treat transient `SqliteException`/`FileNotFoundException`/`InvalidOperationException` as not-yet-fresh and keep polling within budget, so `Execute` keeps its "never throws for expected conditions" promise.

**Acceptance criteria:**
- [ ] New test: prepend-drift (`"// drifted\n" + content`) + successful recovery + symbol-op apply → file content is correct (spliced at the new offsets) or the call refuses cleanly; assert the file is never corrupted.
- [ ] New test: recovery poll survives a gate check that throws mid-poll (fake/shim) without escaping `Execute`.
- [ ] Existing append-drift recovery tests still pass unchanged.
- [ ] `scripts/test.sh` green; committed.

### Task 2: Central missing-required-param catch for all MCP tools

**Files:**
- Modify: `src/Miller.Server/Telemetry/TelemetryCallToolFilter.cs` (~:103-113 catch block)
- Test: `tests/Miller.Tests/Server/CallToolFilterTelemetryTests.cs`

**What to build:** Five tools (`inspect`/`trace`/`edit` missing `target`, `search`/`context` missing `query`) still surface `Microsooft.Extensions.AI` marshalling `ArgumentException` as a protocol-level unhandled exception — agents retry-loop on the opaque error (seen live in the 0.3.1 Windows dogfood log in TODO.md). Catch it centrally and return a friendly tool result.

**Approach:** In the filter's existing catch path, detect the marshaller shape (`ArgumentException` with `ParamName == "arguments"`); extract the missing parameter name from the message; return `CallToolResult { IsError = true }` with a one-line usage hint built from the request's tool name (e.g. `inspect requires 'target'. Example: inspect(target="WorkspaceTool.ResolveTarget")`). Keep telemetry outcome = Error. All other exceptions keep the existing rethrow-for-SDK-redaction behavior. Use a small static map of tool → example call for the 9 tools; default to a generic "missing required parameter '<name>'" hint for unknown tools.

**Acceptance criteria:**
- [ ] New in-process MCP round-trip test: calling `inspect` with `{}` returns `IsError=true` with a message naming `target`, no unhandled exception, telemetry error row recorded.
- [ ] Test that a non-marshalling exception still rethrows (existing behavior preserved).
- [ ] `scripts/test.sh` green; committed.

### Task 3: Harden leader liveness probe (M1) + clear stale identity on write failure (L1)

**Files:**
- Modify: `src/Miller.Server/Hosting/LeaderIdentityFile.cs` (`IsProcessAlive` ~:75-86, identity record)
- Modify: `src/Miller.Server/Hosting/IndexerService.cs` (leader identity write catch ~:201-209 — small, isolated from Batch 2's regions)
- Test: `tests/Miller.Tests/Server/LeaderIdentityFileTests.cs`, `tests/Miller.Tests/Server/WorkspaceHealthLeaderTests.cs`

**What to build:** `Process.HasExited` can throw `Win32Exception` (access denied on pid reuse by an elevated process) — uncaught, it crashes `workspace health` on Windows. Pid reuse by any process also makes a dead leader read "alive". And if the new leader's identity write fails, a crashed predecessor's `leader.json` survives, reporting a dead/mismatched leader while a healthy one runs.

**Approach:**
- `IsProcessAlive`: catch all exceptions from the probe (it is advisory) and treat probe failure as "unknown → not provably alive" for health rendering — but do not report `indexer_leader_dead` solely on a probe exception; add a distinct "liveness unknown" outcome if the current return shape allows, otherwise document the chosen collapse in the test.
- Pid-reuse cross-check: compare `Process.StartTime` (UTC) against the identity record's `StartedAtUtc` with a small tolerance (e.g. ±10s); a process that started long after the recorded time is a different process. `StartedAtUtc` already exists in the record per the takeover design — verify and use it; if absent, add it (writer side already stamps time).
- L1: in `IndexerService`'s catch around the identity write, call `LeaderIdentityFile.TryDelete(millerDir)` so a failed write never leaves a predecessor's stale identity as the visible truth.

**Acceptance criteria:**
- [ ] Test: probe that throws (shimmed or wrapped) does not crash health-facts reading.
- [ ] Test: identity with matching pid but a `StartedAtUtc` far older than the live process's start time is not reported alive.
- [ ] Test: failed identity write deletes any pre-existing identity file.
- [ ] `scripts/test.sh` green; committed.

### Task 5: Not-found near-miss candidates + scope-mask fallback

**Files:**
- Modify: `src/Miller.Server/Resolution/SmartTargetResolver.cs` (`ResolveByName` ~:112-146, `ScopeMatches` ~:148-156)
- Modify: `src/Miller.Server/Tools/InspectTool.cs:189`, `src/Miller.Server/Tools/ImpactTool.cs:239`, `src/Miller.Server/Tools/TraceTool.cs:455,738` (not-found message sites)
- Test: `tests/Miller.Tests/Server/SmartTargetResolverTests.cs` and the touched tool test files

**What to build:** Two residual UX gaps from the Windows dogfood session: (a) true not-found returns zero near-miss candidates, so agents can't self-correct in one turn; (b) a wrong `scope` (e.g. backslash-separated Windows path) silently filters valid matches to zero and reports "not found" even though the target exists.

**Approach:**
- Scope-mask fallback in the resolver: when scoped matches = 0 but unscoped matches > 0, return the unscoped result(s) as candidates with a note that they live in other files (or normalize separators before comparing — do both: normalize `\` → `/` and case per the existing path-canonicalization convention, and fall back to unscoped candidates when scope still filters everything).
- Near-miss candidates: when resolution truly fails, query the name index for up to 3 close names (case-insensitive exact, then substring/last-segment match) and append them to the not-found message: `'{target}' not found. Closest: A, B, C. Try search to locate it.` Put the candidate lookup in the resolver (shared by inspect/trace/impact/edit) so the 4 message sites just render what resolution returns.
- Keep the resolver's existing return shapes; extend `NotFound` with optional suggestions rather than inventing a new case if that is lighter.

**Acceptance criteria:**
- [ ] Test: backslash or wrong-file `scope` with an otherwise-resolvable target yields candidates (not bare not-found).
- [ ] Test: misspelled target (`WorkspceTool.ResolveTarget`-style) yields up to 3 near-miss suggestions in the message.
- [ ] Existing qualified-member resolution tests pass unchanged.
- [ ] `scripts/test.sh` green; committed.

### Task 7 (mechanical): Fast-suite sleeps + doc drift

**Files:**
- Modify: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs:864,899` (replace `Thread.Sleep(200)` loader stubs with `TaskCompletionSource`/`ManualResetEventSlim` gating released once the second resolve is queued)
- Modify: `CLAUDE.md:118` (replace stale "v0.3.2 … four platform archives" release-facts example with v0.3.6), then run `scripts/sync-agents.sh` and verify `cmp -s CLAUDE.md AGENTS.md`
- Verify v0.3.6 facts from the latest release-evidence doc in `docs/` (commit f0c27fe "docs: verify v0.3.6 release") — do not guess archive counts; if the evidence doc lacks them, keep the sentence version-agnostic ("the live release") instead.

**Acceptance criteria:**
- [ ] The two single-flight tests pass deterministically with no fixed sleeps.
- [ ] `cmp -s CLAUDE.md AGENTS.md` clean.
- [ ] `scripts/test.sh` green (and measurably faster); committed.

---

### Task 4 (Batch 2): Request-queue hardening — TTL, claim-by-rename, leader short-circuit, coalesced drain (M2, M3, M4)

**Files:**
- Modify: `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs` (drain methods ~:150-191, `TryDelete`)
- Modify: `src/Miller.Server/Hosting/LeaderWriteThrough.cs` (`TryRecoverStaleFile`)
- Modify: `src/Miller.Server/Hosting/IndexerService.cs` (`TryProcessFileConvergeRequests` ~:612-635, debounce tick ~:236-237)
- Test: `tests/Miller.Tests/Server/LeaderScanRequestQueueTests.cs`, `tests/Miller.Tests/Server/LeaderWriteThroughTests.cs`, `tests/Miller.Tests/Server/IndexerServiceScanTests.cs`

**What to build:** Four robustness gaps in the new cross-process converge machinery:
1. (M2-drain) Requests have no TTL — a leader on an old build never drains them, they accumulate forever.
2. (M2-writer) `TryRecoverStaleFile` writes a request and reports `Requested` even when no live, capable leader exists — every stale-gate edit then burns the full 2.5s poll with zero chance of success.
3. (M4) `TryDelete` silently swallows `IOException`; an undeletable request is re-serviced every 250ms tick (a julie-extract per tick; a full force-scan per tick in the full-scan variant).
4. (M3) A drained converge request and the FileSystemWatcher event for the same file each trigger an extract on the same tick — two subprocess runs per reader edit.

**Approach:**
- TTL: on drain, discard (delete without servicing) requests whose stamp is older than a constant (suggest 10 minutes); log at information level with the count.
- Claim-by-rename: before servicing, `File.Move` the request to a `*.claimed` name; on move failure (held/contended), skip it this tick and log once at warning (not per-tick spam — track last-logged or log at debug after first). Delete the claimed file after servicing; claimed files older than the TTL are also swept.
- Leader short-circuit: before writing a request, `TryRecoverStaleFile` consults `LeaderIdentityFile.TryRead` + the liveness probe; when no identity exists, the leader is dead, or its recorded version predates the converge-request protocol, return the no-recovery outcome immediately (gate refuses with the existing stale message) instead of `Requested`. Treat probe-unknown as "assume capable" (do not regress the happy path).
- Coalesced drain: `TryProcessFileConvergeRequests` enqueues drained paths as synthetic Changed events into the core's `WatchEventQueue` instead of calling `TryReindexAsLeader` directly, so the queue's existing coalescing collapses request + watcher event into one extract on the same tick. Verify the gate-poll convergence guarantee still holds (the gate polls the DB after the tick; the queue is drained on the same 250ms cadence).

**Acceptance criteria:**
- [ ] Test: expired request is deleted without servicing.
- [ ] Test: request that cannot be claimed (file held) is skipped, not re-serviced in a tight loop, and a diagnostic is logged.
- [ ] Test: `TryRecoverStaleFile` returns no-recovery (no request written, no `Requested`) when leader identity is absent or dead; still `Requested` when a live capable leader is recorded.
- [ ] Test: a converge request plus a watcher event for the same file on the same tick produce exactly one reindex call (count via the existing fake/spy in `IndexerServiceScanTests`).
- [ ] `scripts/test.sh` green; committed.

### Task 6 (Batch 2): Sidecar converge corrupt-escalation (M5)

**Files:**
- Modify: `src/Miller.Server/Hosting/IndexerService.cs` (`TryConvergeSidecar` ~:475-545)
- Test: `tests/Miller.Tests/Server/IndexerServiceScanTests.cs` (or the existing sidecar-converge test home)

**What to build:** Every converge exception is `LogWarning` + continue; a persistently corrupt `search.db`/`content.db` warns forever while readers get the stale-sidecar error, and `workspace refresh` re-enters the same failing path. Sidecars are derived artifacts — rebuild is always safe.

**Approach:** On a converge exception that indicates corruption (the sidecar layer's malformed-meta error or a `SqliteException` corruption code), delete the artifact and retry once with a full rebuild in the same converge call. One escalation per converge attempt (no loop); if the rebuild also fails, keep the existing warning path. Log the escalation at warning with the artifact path.

**Acceptance criteria:**
- [ ] Test: corrupt sidecar (truncated/garbage file) → converge deletes and rebuilds; readers then open it successfully.
- [ ] Test: non-corruption converge failure (e.g. transient IO) does not trigger delete/rebuild.
- [ ] `scripts/test.sh` green; committed.
