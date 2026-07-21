# Semantic P4 — Shadow Rollout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make shadow-mode vector convergence honest and operable for real users — pause states that report themselves, consented model download, disk safety, generation GC — and produce the evidence the P5 canary decision needs.

**Architecture:** Producers stamp artifact-mediated facts (`converge_pause_state` on `vectors_meta`) so any reader instance can report them; consent semantics live in a Miller CLI verb while download mechanics stay in the sidecar's `prepare` subcommand (design §4.4); GC decision logic stays pure in `VectorGenerationManager` with a thin scheduler wiring live inputs. No new MCP tools, no protocol changes.

**Tech Stack:** .NET 10 (Miller.Indexing / Miller.Server / Miller.Tests), Rust sidecar repo (docs + bench only), bash/python probe tooling.

**Architecture Quality:** Approved shape: pause facts are artifact-mediated (written via `VectorStore.SetMeta`, consumed by the existing `VectorSidecar.PauseState` precedence), never in-process flags; the GC scheduler owns *when*, `VectorGenerationManager` owns *what*; the live-reader registry is in-process only — cross-process protection remains the soak window (P2 B6 decision). Main risk: the `downloading` state is a cross-process fact Miller cannot observe from the sidecar; it is kept honest via a workspace-local marker owned by the prepare verb. Workers report plan mismatches instead of redesigning locally.

## Global Constraints

- **No pushes.** All Miller commits stay local (user directive 2026-07-20: "let's keep our miller changes local till we get through the plan"). Sidecar-repo pushes are allowed; sidecar *releases/promotions* require explicit user approval.
- **No new MCP tools or MCP params** (MCP-stinginess rule). Surface grows only via CLI verbs, status render, and docs.
- **`MILLER_SEMANTIC=off` stays a permanent zero-work guarantee**; lexical-only output stays byte-identical (ADR-0003).
- **Sidecar owns model-acquisition mechanics** (`julie-semantic-sidecar prepare`); Miller never parses model URLs or manifests (design §4.4). Consent semantics live in Miller.
- Status compact vocabulary (design §5.1, exact strings): `ready | ready (updating; N files pending) | building N% (not queryable) | downloading | unavailable (reason) | incompatible | circuit-open | disk-blocked | disabled`.
- `vectors_meta` keys for pause facts (P2 B6 contract, exact): `converge_pause_state` (values `circuit-open` | `disk-blocked`) and `converge_pause_reason`.
- Build must stay 0 warnings / 0 errors (`TreatWarningsAsErrors`); fast suite stays `Category!=Scale` with the guards intact — never weaken `ScaleTraitConventionTests` or the wall tripwire.
- Model-footprint decision (Q8_0 vs f16 vs bge-small) and RC→v0.1.0 promotion are **user decisions**; this plan produces evidence only.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section), `scripts/test.sh` / `scripts/test.ps1`.

**Worker red/green scope:** `dotnet test tests/Miller.Tests --filter "FullyQualifiedName~<TestClass>"` for the test class named in the task (Miller tasks); `cargo test` in the sidecar repo (sidecar tasks, both `--features metal` and default).

**Worker ceiling:** `scripts/test.sh` (fast suite). Workers do not run scale, Release builds, or benchmarks unless the task assigns them.

**Worker gate invariant:** each task's acceptance criteria name the behavior its tests prove (stated per task).

**Lead affected-change scope:** `scripts/test.sh` after each reviewed task lands.

**Branch gate:** `scripts/test.sh all` (fast + scale, real pinned binaries) + `dotnet build Miller.slnx -c Release` (0W/0E) before merge to local main.

**Replay/metric evidence:** benchmark numbers in Tasks 7–8 and dogfood observations in Task 9 are report-only evidence for user decisions, not hard gates. Hard gates are test results and build cleanliness.

**Escalation triggers:** any change touching the converge/indexing path → run `scripts/test.sh scale` before the task is accepted, not only at branch gate.

**Assigned verification failure:** workers stop and report; no gate updates without plan say-so.

**Verification ledger:** `.razorback/sdd/progress.md` + ledger table per SDD skill.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: circuit-open pause producer | Lane 1 | Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`; Test: `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs` | Yes | Tasks 1, 2, 5 all modify `VectorConvergeService.cs` and its test file; ordered lane. |
| Task 2: disk preflight + disk-blocked producer | Lane 1 | Create: `src/Miller.Indexing/Semantic/DiskPreflight.cs`; Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`; Test: `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`, `tests/Miller.Tests/Indexing/DiskPreflightTests.cs` | Yes | Follows Task 1 in Lane 1 (same files). |
| Task 3: `miller semantic prepare` CLI verb | Lane 2 | Create: `src/Miller.Server/Cli/SemanticPrepareCli.cs`; Modify: `src/Miller.Server/Cli/CliDispatch.cs`; Test: `tests/Miller.Tests/Server/SemanticPrepareCliTests.cs` | Yes | Task 4 consumes Task 3's marker contract; ordered lane. |
| Task 4: `downloading` status state (consumer + producer) | Lane 2 | Modify: `src/Miller.Indexing/VectorSidecar.cs`, `src/Miller.Server/Cli/SemanticPrepareCli.cs`; Test: `tests/Miller.Tests/Indexing/VectorSidecarClassificationTests.cs`, render tests | Yes | Follows Task 3 in Lane 2 (marker produced there). |
| Task 5: GC scheduler + live-reader registry | Lane 1 | Create: `src/Miller.Indexing/Semantic/VectorLiveReaderRegistry.cs`; Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`, `src/Miller.Indexing/Semantic/VectorGenerationManager.cs` (if needed), reader open sites; Test: `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`, `tests/Miller.Tests/Indexing/VectorGenerationManagerTests.cs` | Yes | Follows Task 2 in Lane 1 (same files). |
| Task 6: RC promotion-gate throughput floor (sidecar repo) | Batch A | Create/Modify in `/Users/murphy/source/julie-semantic-sidecar`: `docs/rc-promotion-gate.md`, `scripts/bench-throughput.py` | No | None - safe parallel batch. |
| Task 7: Q8_0 model-footprint benchmark (evidence) | None - serial | Create: sidecar-repo `docs/findings/` bench record + Miller `docs/findings/2026-07-XX-q8-footprint-benchmark.md` | Yes | Benchmark wall-clock accuracy requires a quiet machine; runs alone after Lanes 1–2 and Task 6. |
| Task 8: fast-suite wall-ceiling fix | None - serial | Modify: offending test files (discovered), possibly `scripts/test.sh` comment; Test: full fast suite | Yes | Profiles and edits test files other lanes own; runs after Lanes 1–2 complete. |
| Task 9: shadow dogfood + P4 findings | None - serial | Create: `docs/findings/2026-07-XX-p4-shadow-dogfood.md` | Yes | Exercises everything above end-to-end; must run last. |

Commit mode: **serial-worker-commit** for every task (all lanes are serialized; no two tasks share a dispatch window on the same files).

---

### Task 1: circuit-open pause producer

**Files:**
- Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`
- Test: `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`

**Interfaces:**
- Consumes: `SemanticSessionState.CircuitOpen` (existing), `VectorStore.SetMeta(key, value)` (`src/Miller.Indexing/Semantic/VectorStore.cs:186`), existing consumer `VectorSidecar.PauseState` (`src/Miller.Indexing/VectorSidecar.cs:400`) with keys `converge_pause_state`/`converge_pause_reason`.
- Produces: `converge_pause_state=circuit-open` + human-readable `converge_pause_reason` stamped on the active artifact when the session circuit opens during a drain; both keys **cleared** (deleted or set empty — match `PauseState`'s null semantics) on the first successful drain wake after recovery.

**Contract inputs:** `VectorSidecarClassificationTests.CircuitOpenPause_OverridesReady` (`tests/Miller.Tests/Indexing/VectorSidecarClassificationTests.cs:73`) fixes the consumer's expected key/values — the producer must emit exactly those.

**File ownership:** Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`; Test: `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`

**Serialization required:** Yes

**Dependency reason:** Tasks 1, 2, 5 all modify `VectorConvergeService.cs` and its test file; ordered lane.

**What to build:** When a drain wake ends with the circuit open, stamp the pause on the artifact so `workspace status` from ANY process reports `circuit-open` instead of a stale `ready`. When a later wake completes a request cleanly, clear the pause. This closes the top concern from the P2 B6 report ("a paused convergence reports ready").

**Approach:** Stamp inside the drain path where the session's circuit state is already observed (after `DrainOnceAsync`/error recording), via the already-open converge port's store. Write only on state *transitions* (open→stamp, recovered→clear), not every wake — vectors_meta writes on a hot loop would churn WAL. Reuse the existing `RecordError` neighborhood; do not add a new hosted service. TDD against the existing `FakePort` (extend it to expose meta writes).

**Acceptance criteria:**
- [x] Circuit opening during drain stamps `converge_pause_state=circuit-open` and a non-empty `converge_pause_reason` on the artifact.
- [x] A subsequent successful wake clears both keys; `workspace status` classification returns to `ready`/`building` (proved via `VectorSidecar.Inspect` on the same store in-test).
- [x] No meta write occurs on wakes with no state transition.
- [x] Worker-scope verification passes and the change is committed (parallel-lead-commit — lead commit; mode switched per SDD contract for the concurrent T1+T3 dispatch window).

### Task 2: disk preflight + disk-blocked producer

**Files:**
- Create: `src/Miller.Indexing/Semantic/DiskPreflight.cs`
- Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`
- Test: `tests/Miller.Tests/Indexing/DiskPreflightTests.cs`, `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`

**Interfaces:**
- Consumes: Task 1's transition-stamping pattern; `VectorSidecar` consumer already renders `disk-blocked` (P2 B6).
- Produces: `DiskPreflight.Check(path, requiredBytes)` → pure verdict record (ok / blocked with free-bytes fact) with an injectable free-space probe (so tests never depend on the real disk); `converge_pause_state=disk-blocked` stamped when a shadow build or bounded batch is refused for space; cleared when a later preflight passes.

**Contract inputs:** Design §4.4 ("Disk preflight before download") and §5.1 state vocabulary. Estimate `requiredBytes` conservatively from the work list size × observed bytes-per-unit of the current artifact, floor 256 MiB — a stated heuristic, not a contract.

**File ownership:** Create: `src/Miller.Indexing/Semantic/DiskPreflight.cs`; Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`; Test: `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`, `tests/Miller.Tests/Indexing/DiskPreflightTests.cs`

**Serialization required:** Yes

**Dependency reason:** Follows Task 1 in Lane 1 (same files).

**What to build:** Refuse to start a shadow rebuild (and to continue bounded batches) when free disk under `.miller/` cannot hold the projected shadow artifact; surface the refusal as `disk-blocked` with the free/required numbers in the reason, instead of failing mid-build with a corrupt half-artifact. Task 3 reuses `DiskPreflight` before model download.

**Approach:** Pure logic in `Miller.Core`-style (no I/O in the verdict; probe injected — default probe uses `DriveInfo`). Wire the check at `BuildShadowAsync` entry and at each bounded-batch slice boundary. Preflight failure is a hold (RecordError + pause stamp), never an exception.

**Acceptance criteria:**
- [x] Preflight verdict is pure and unit-tested (blocked/ok boundaries, probe injected).
- [x] A blocked shadow build stamps `disk-blocked` with free+required bytes in the reason and leaves no `.rebuild`/shadow debris.
- [x] Recovery (probe reports space) clears the pause on the next wake and the build proceeds.
- [x] Worker-scope verification passes and the change is committed (parallel-lead-commit — lead commit; circuit-open > disk-blocked precedence via single ResolvePause point).

### Task 3: `miller semantic prepare` CLI verb (consented model download)

**Files:**
- Create: `src/Miller.Server/Cli/SemanticPrepareCli.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Test: `tests/Miller.Tests/Server/SemanticPrepareCliTests.cs`

**Interfaces:**
- Consumes: sidecar `prepare [--model <id>]` subcommand (downloads+verifies into the shared cache; machine-readable progress on stdout; concurrent-safe via cache lock — mechanics all sidecar-owned per §4.4); sidecar binary resolution at `<ToolsRoot>/julie-semantic-sidecar[.exe]` (same as `VectorConvergeService.cs:794`); `DiskPreflight` from Task 2 (compile-time only — if Task 2 hasn't landed when this dispatches, preflight wiring moves to Task 4's lane-2 slot and this task notes the mismatch).
- Produces: CLI verb `miller semantic prepare [--model <id>] [--json]` — exit 0 on prepared (fresh or already-cached), nonzero with the sidecar's actionable message on failure (offline, sha mismatch, disk). A workspace-local marker file contract for Task 4: `<workspace>/.miller/semantic-prepare.marker` created before the child starts (content: model id + pid + ISO timestamp), always deleted on exit (finally). Verb help text registered in CLI `help`.

**Contract inputs:** Design §4.4 verbatim: "Miller's `miller semantic prepare` CLI verb … shell[s] out to the sidecar's `prepare`; consent semantics live in Miller, mechanics in the sidecar." Running the verb IS the consent act — Miller never auto-downloads; the converge path keeps its existing stated-refusal behavior on `model_not_prepared`.
**MCP-stinginess check:** CLI verb only; no MCP tool or param is added.

**File ownership:** Create: `src/Miller.Server/Cli/SemanticPrepareCli.cs`; Modify: `src/Miller.Server/Cli/CliDispatch.cs`; Test: `tests/Miller.Tests/Server/SemanticPrepareCliTests.cs`

**Serialization required:** Yes

**Dependency reason:** Task 4 consumes Task 3's marker contract; ordered lane.

**What to build:** The explicit, consented model-acquisition entry point. Streams the sidecar's progress through to the console (CLI owns stdout — no Serilog), runs a disk preflight against the model cache target before launching, and reports `model_not_prepared` remediation in `workspace status` messaging ("run `miller semantic prepare`") if that hint is not already present.

**Approach:** Follow the existing CLI verb pattern in `CliDispatch` (branch at the verb table, ~`CliDispatch.cs:92-148`; no host build, no index load — like `version`). Fake the sidecar in tests with a stub executable script/`FakeSemanticSidecar`-style process (existing support under `tests/Miller.Tests/Support/`); tag `Scale` if it must spawn a real process — prefer a pure argument/marker/exit-code core (`SemanticPrepareCli.Run(...)` with an injected process runner) so the fast suite covers logic without spawning.

**Acceptance criteria:**
- [x] `miller semantic prepare` shells to the pinned sidecar's `prepare`, streams progress, and returns the sidecar's exit status; `--model` passes through.
- [x] Marker file exists exactly while the child runs (created before spawn, removed on success, failure, and cancellation).
- [x] Missing sidecar binary fails loud with the restore-script message (same wording pattern as `CliDispatch.cs:529`).
- [x] Disk preflight refusal produces an actionable message and nonzero exit without spawning the child.
- [x] Worker-scope verification passes and the change is committed (parallel-lead-commit — lead commit; local preflight seam pending Task 4's DiskPreflight swap).

### Task 4: `downloading` status state (consumer + producer)

**Files:**
- Modify: `src/Miller.Indexing/VectorSidecar.cs`, `src/Miller.Server/Cli/SemanticPrepareCli.cs` (marker read helper if it lives there)
- Test: `tests/Miller.Tests/Indexing/VectorSidecarClassificationTests.cs` (+ the render/facts tests that cover status strings)

**Interfaces:**
- Consumes: Task 3's marker contract (`<workspace>/.miller/semantic-prepare.marker`, content model id + pid + timestamp).
- Produces: `DownloadingState = "downloading"` constant and classification in `VectorSidecar` — reported when the marker exists AND its pid is alive; a dead-pid marker is stale and ignored (classification falls through, and the next `semantic prepare` run replaces it). Precedence: `downloading` ranks below `circuit-open`/`disk-blocked` (a pause is more actionable) and above `unavailable(model_not_prepared)`.

**Contract inputs:** Design §5.1 compact vocabulary (exact string `downloading`); existing classification precedence tests in `VectorSidecarClassificationTests`.

**File ownership:** Modify: `src/Miller.Indexing/VectorSidecar.cs`, `src/Miller.Server/Cli/SemanticPrepareCli.cs`; Test: `tests/Miller.Tests/Indexing/VectorSidecarClassificationTests.cs`, render tests

**Serialization required:** Yes

**Dependency reason:** Follows Task 3 in Lane 2 (marker produced there).

**What to build:** `workspace status`/`health` say `downloading` while a consented prepare is in flight, so a user watching a fresh setup sees progress instead of `unavailable`. Wire the string through the same facts flow the other states use (`WorkspaceFactsAssembler` → `WorkspaceRender` — the P2 consumer work means only the new state constant and classification arm should be needed; report a plan mismatch if render needs more).

**Approach:** Marker probing goes through the existing `IVectorFileProbe` seam (extend it rather than raw `File` calls) so classification stays unit-testable. Pid-alive check: `Process.GetProcessById` try/catch behind the probe seam.

**Acceptance criteria:**
- [x] Live marker (pid alive) → compact status `downloading`; JSON carries the model id from the marker.
- [x] Stale marker (pid dead) → classification unchanged from today; no error.
- [x] Precedence: pause states beat `downloading`; `downloading` beats `unavailable (model_not_prepared)` (structural: marker consulted only when classification would report unavailable).
- [x] Worker-scope verification passes and the change is committed (parallel-lead-commit — lead commit; DiskPreflight swap into SemanticPrepareCli landed here).

### Task 5: GC scheduler + live-reader registry

**Files:**
- Create: `src/Miller.Indexing/Semantic/VectorLiveReaderRegistry.cs`
- Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`; `src/Miller.Indexing/Semantic/VectorGenerationManager.cs` only if an input seam is missing; reader open sites (`SemanticSearchArm`/`WorkspaceIndexProvider` vector open path) to register/unregister
- Test: `tests/Miller.Tests/Indexing/VectorGenerationManagerTests.cs` (registry), `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs` (scheduler wiring)

**Interfaces:**
- Consumes: `VectorGcInputs { Retained, ActiveIsReady, Now, TagsWithLiveReaders, SoakWindow, RetentionCap }` and the GC plan logic in `VectorGenerationManager` (`src/Miller.Indexing/Semantic/VectorGenerationManager.cs:41-68`) — pure, tested, currently caller-less; `RetainedPathFor`/`TagFromRetainedPath`/`EnumerateRetained`.
- Produces: `VectorLiveReaderRegistry` — process-wide, thread-safe `Register(tag) : IDisposable` / `LiveTags` snapshot; GC execution after each successful shadow promote and on leader wakes (piggybacked on the existing drain timer, no new hosted service): build inputs, apply `plan.Deletions` (delete files + fold WAL via `IVectorGenerationFiles`), log one line per deletion with the outcome reason.

**Contract inputs:** P2 B6 decision (recorded in `.razorback/sdd/progress.md`): "P2 posture = soak-window-only GC protection, registration lands with the P4 GC scheduler." Cross-process readers stay protected by the soak window ONLY — the registry is in-process; do not attempt cross-process reader tracking.

**File ownership:** Create: `src/Miller.Indexing/Semantic/VectorLiveReaderRegistry.cs`; Modify: `src/Miller.Server/Hosting/VectorConvergeService.cs`, `src/Miller.Indexing/Semantic/VectorGenerationManager.cs` (if needed), reader open sites; Test: `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`, `tests/Miller.Tests/Indexing/VectorGenerationManagerTests.cs`

**Serialization required:** Yes

**Dependency reason:** Follows Task 2 in Lane 1 (same files).

**What to build:** Retained generations currently accumulate forever (`vectors.gen-*.db` are never deleted). Wire the existing pure GC plan to real execution so rollback generations disappear after the soak window unless a live in-process reader holds them, capped at `DefaultRetentionCap`.

**Approach:** Registry is a `ConcurrentDictionary<string,int>` refcount; readers register on open, dispose on close. Scheduler runs under the leader's converge lock only (readers never GC). Deletion failures (Windows held handles) log and retry next wake — never crash the drain. TDD with the fake files seam (`IVectorGenerationFiles`) already used by `VectorGenerationManager` tests.

**Acceptance criteria:**
- [x] After promote, generations beyond the soak window with no live reader are deleted; `LiveReader`/`WithinSoakWindow`/`OnlyReadyGeneration` outcomes are respected (existing plan semantics unchanged).
- [x] A registered live reader blocks deletion until disposed; disposal makes the next wake collect it (arm registration window is per-query by design — reported plan mismatch, accepted per B6 soak-window posture).
- [x] GC never runs on reader instances (non-leader), proved by test.
- [x] Worker-scope verification passes and the change is committed (parallel-lead-commit — lead commit).

### Task 6: RC→v0.1.0 promotion gate — target-machine throughput floor (sidecar repo)

**Files:**
- Create (in `/Users/murphy/source/julie-semantic-sidecar`): `docs/rc-promotion-gate.md`, `scripts/bench-throughput.py`

**Interfaces:**
- Consumes: the existing probe methodology at `$HOME/.claude/jobs/385e567c/tmp/sidecar-timing-probe.py` (64-text and 250-text `embed_batch` rounds over stdio) and the v0.1.0-rc.2 measured facts: **78.9 units/s** steady-state (64-text), 77.4 (250-text), P0 llama-server floor 52.3 units/s, M2 Ultra.
- Produces: a repeatable `scripts/bench-throughput.py --binary <path> [--batch 64] [--rounds N]` that prints units/s and a PASS/FAIL against a floor; `docs/rc-promotion-gate.md` — the checklist an RC must pass before promotion to a non-prerelease, including the new floor: **≥ 40 units/s steady-state on the M2 Ultra reference machine (64-text batches, warm model)** — half of rc.2's observed rate, chosen to catch backend regressions (a CPU-only regression measures ~6.6) without flaking on machine noise. Also records the WHY: the rc.1 lesson (harness numbers ≠ engine numbers; CPU-only shipped 12× under the design floor — `docs/findings/2026-07-20-first-real-shadow-converge-benchmark.md` in Miller).

**Contract inputs:** Conformance suite and unit tests remain gate items; this task ADDS the throughput floor, it does not restate or renumber existing release steps. Pushing to the sidecar repo is allowed; promotion itself stays a user decision.

**File ownership:** Create/Modify in `/Users/murphy/source/julie-semantic-sidecar`: `docs/rc-promotion-gate.md`, `scripts/bench-throughput.py`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The gate document and the tool that makes the floor checkable in one command, so RC→v0.1.0 promotion (a pending user decision) can be run against rc.2 with the floor in force.

**Approach:** Port the probe script into the repo with a stable CLI (no `Date.now`-style hidden state; JSON output with `--json`). Bench script must verify the binary answers `health ready:true` before timing (a `model_not_prepared` run must FAIL the bench, not measure zeros). `cargo test` untouched.

**Acceptance criteria:**
- [x] `scripts/bench-throughput.py --binary target/release/julie-semantic-sidecar` on this machine reports ~rc.2 numbers and PASS.
- [x] Gate doc lists the floor, the reference machine, the command, and the rc.1 rationale.
- [x] Committed to the sidecar repo (push allowed; no release action).

### Task 7: Q8_0 model-footprint benchmark (evidence for the user decision)

**Files:**
- Create: `docs/findings/2026-07-XX-q8-footprint-benchmark.md` (Miller repo; XX = run date)

**Interfaces:**
- Consumes: fixed Metal sidecar (rc.2), Task 6's bench script, the sidecar's `prepare --model` model-override support (`main.rs` accepts `--model qwen3-0.6b-f16`-style ids — verify the manifest's available ids before running; if no Q8_0 id exists in the manifest, the finding records that the sidecar manifest needs a Q8_0 entry first and STOPS — manifest additions are sidecar-repo work needing conformance goldens, out of this task's scope).
- Produces: findings doc comparing f16 (~1.1 GiB) vs Q8_0 (~640 MB) on: throughput (Task 6 bench), embedding similarity drift vs f16 vectors on the conformance corpus (cosine of paired outputs), and disk/RAM footprint. NO decision — evidence for the user's P4/P5 footprint call (design §Model policy open question).

**Contract inputs:** Design §Model policy: "Decide on P4 shadow evidence of the Qwen3-vs-bge quality gap, not up front." bge-small comparison only if its manifest id exists; otherwise record as not-benchmarkable-yet.

**File ownership:** Create: sidecar-repo `docs/findings/` bench record + Miller `docs/findings/2026-07-XX-q8-footprint-benchmark.md`

**Serialization required:** Yes

**Dependency reason:** Benchmark wall-clock accuracy requires a quiet machine; runs alone after Lanes 1–2 and Task 6.

**What to build:** The measured facts the footprint decision needs, on the FIXED sidecar (the old CPU-only numbers are invalid for this comparison).

**Approach:** Lead-executed (not delegated) — benchmarks share the machine. If the manifest lacks a Q8_0 entry, write the short finding saying exactly that and what adding it costs (manifest entry + conformance goldens + eval-gate re-run), then stop.

**Acceptance criteria:**
- [x] Findings doc with either (a) measured f16-vs-Q8_0 table or (b) the manifest-gap record with the cost of closing it — (b) recorded, PLUS measured f16-vs-bge table (9.0× throughput, 0.15× RSS) since bge is manifest-pinned and cached.
- [x] No model pin, manifest, or default changed.

### Task 8: fast-suite wall-ceiling fix

**Files:**
- Modify: offending test files (discovered by profiling); `scripts/test.sh` only if the comment/target text needs updating — the 30s ceiling value itself does not move up

**Interfaces:**
- Consumes: `scripts/test.sh` tripwire (`FAST_BUDGET_SECONDS=30`, target <10s); current fast wall ~28s (4,168 tests) with observed ambient-load trips at 33s/63s this week.
- Produces: fast suite comfortably under the ceiling (target: ≤20s cold on this machine) via the profile's top offenders — typical moves: retag genuinely heavy tests `Scale`, collapse per-test artifact builds into shared fixtures (`IClassFixture`/collection fixtures), and cut redundant real-SQLite churn in hot test classes. No production code changes.

**Contract inputs:** CLAUDE.md testing rules: the split is load-bearing; guards must not be weakened; a "fast" test doing real I/O belongs in Scale. Raising `FAST_BUDGET_SECONDS` is NOT an accepted fix.

**File ownership:** Modify: offending test files (discovered), possibly `scripts/test.sh` comment; Test: full fast suite

**Serialization required:** Yes

**Dependency reason:** Profiles and edits test files other lanes own; runs after Lanes 1–2 complete.

**What to build:** Headroom. The suite trips its own tripwire under ambient load, which erodes trust in the gate.

**Approach:** `dotnet test --logger "console;verbosity=normal"`-level durations or `trx` report to rank test classes by wall time; fix the top ~5; re-run 3× to confirm stability. Every retag to Scale must satisfy the convention guard (spawns-julie ⟹ Scale stays one-directional; Scale-for-weight is allowed).

**Acceptance criteria:**
- [x] Fast suite ≤20s cold on this machine, 3 consecutive clean runs (18/19/18s), 0 failures, test count accounted for (no retags; fast 4225 / scale 86 unchanged).
- [x] No guard weakened; no production code changed; ceiling still 30s.
- [x] Worker-scope verification passes and the change is committed per `serial-worker-commit` (45ae5e4).

### Task 9: shadow dogfood + P4 findings (exit evidence)

**Files:**
- Create: `docs/findings/2026-07-XX-p4-shadow-dogfood.md`

**Interfaces:**
- Consumes: everything above, built from this branch; real registered workspaces (miller itself + at least 2 more from `workspace list` with meaningfully different sizes/languages).
- Produces: the P4 exit record — per-workspace: initial converge wall time, steady-state incremental converge behavior over a working session, disk delta (`vectors.db` + retained generations), sidecar RAM (resident set during and after converge), pause states observed (and whether they self-reported correctly), GC behavior after promote, `semantic prepare` UX notes. Plus the go/no-go facts P5's canary decision needs.

**Contract inputs:** Design P4 roadmap line: "existing users build vectors in `shadow` (no behavior change), model download explicit/consented; observe converge health, disk, RAM across the fleet." Shadow mode only — no search-behavior change, lexical output stays byte-identical.

**File ownership:** Create: `docs/findings/2026-07-XX-p4-shadow-dogfood.md`

**Serialization required:** Yes

**Dependency reason:** Exercises everything above end-to-end; must run last.

**What to build:** Real-workspace evidence that shadow mode is safe to leave on, gathered with the operability features this plan adds (so a pause actually shows up in `workspace status`, GC actually reclaims, disk refusal actually holds).

**Approach:** Lead-executed. Use `MILLER_SEMANTIC=shadow` per-invocation (no global env change); scratch workspaces cleaned up and pruned afterward (the 2026-07-20 benchmark's cleanup discipline). At least one deliberately-induced fault (e.g., kill the sidecar mid-converge) to verify the circuit-open pause self-reports and self-clears.

**Acceptance criteria:**
- [ ] Findings doc covering ≥3 real workspaces with the metrics above and at least one induced-fault observation.
- [ ] All observed defects either fixed on this branch (small) or recorded as explicit follow-ups with severity.
- [ ] Workspaces/registry left clean (`workspace prune` verified).
