# P0 — Governance & Hard Gates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Land phase P0 of the semantic integration program: boundary reversal (ADR-0003 + docs), the sqlite-vec-on-AOT spike, the canary telemetry contract, telemetry version-stamping, edit failure-reason completeness, the retrieval eval harness + dev golden set, and the model benchmark harness that produces the model/dims/quantization pins.

**Architecture:** P0 is gates and instrumentation — no retrieval behavior changes, no new MCP surface, no vectors.db. Code changes are confined to telemetry (additive), edit failure classification (additive), a standalone spike project, and standalone eval tooling. Authoritative design: `docs/plans/2026-07-19-miller-semantic-integration-design.md` (§10 P0, §7, §8, §9).

**Tech Stack:** .NET 10 (existing solution conventions), sqlite-vec v0.1.9 loadable extension, upstream llama.cpp prebuilt releases (benchmark only — NOT the future sidecar), JSONL fixtures.

**Architecture Quality:** Approved shape per design §Architecture Quality: telemetry changes are additive to the shared `~/.miller/telemetry.db` (older writers must keep working); the spike and eval harness live OUTSIDE `Miller.slnx` (no AOT/warnings coupling); no `Miller.Core` I/O. Architecture risk for P0 itself: LOW-MEDIUM (the spike is the risk probe for the program's HIGH-risk storage bet). If code reality contradicts this shape, report a plan mismatch — do not redesign locally.

## Global Constraints

- Build must stay 0 warnings / 0 errors: `dotnet build Miller.slnx -c Release` (`TreatWarningsAsErrors`).
- Fast/Scale test split is load-bearing: nothing in P0 spawns julie-extract or the sidecar; all new tests are fast-suite. Anything slow/IO-heavy must be `[Trait("Category","Scale")]`.
- Telemetry privacy: no raw query text, target paths, or user content in any new telemetry field. New fields are enums/counters/versions only.
- Shared-DB rule: `~/.miller/telemetry.db` schema changes must be additive (`ALTER TABLE … ADD COLUMN` nullable) and tolerated by OLDER Miller writers still running (their INSERT column lists must remain valid).
- Every downloaded artifact (sqlite-vec extension, llama.cpp prebuilt, GGUF models) is pinned by exact version AND sha256 in a checked-in manifest. No unpinned fetches.
- CLAUDE.md edits: edit CLAUDE.md only, then `scripts/sync-agents.sh`; `cmp -s CLAUDE.md AGENTS.md` must pass.
- No new MCP tools, no MCP parameter changes, no ServerInstructions changes in P0.
- Spike + eval projects live outside `Miller.slnx` (own csproj/scripts), so they never affect the product build.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing + Build sections), `scripts/test.sh`.

**Worker red/green scope:** narrowest filter for the behavior, e.g. `dotnet test --filter "FullyQualifiedName~TelemetryLedger"` (fast suite; the csproj default filter keeps Scale out). For docs-only tasks: the stated file-level checks (sync/cmp, link targets exist).

**Worker ceiling:** `scripts/test.sh` (fast suite). Workers do not run the scale suite or CI matrices.

**Worker gate invariant:** each task's acceptance criteria state the invariant its tests prove (listed per task).

**Lead affected-change scope:** `scripts/test.sh` after each merged batch.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh all` (`.tools/julie-extract` is present in this worktree) before handoff/PR.

**Replay/metric evidence:** spike results and benchmark outputs are recorded in `docs/findings/` (hard gates: spike pass/fail per RID, benchmark pin recommendation with metrics; report-only: latency curves, backend micro-bench numbers).

**Escalation triggers:** any change touching `TelemetryLedger` schema → run the full fast suite, not just telemetry filters; any touch of `.github/workflows/` → note that CI proof arrives only after push (report as pending evidence, not as passed).

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp per task. Reuse passing evidence for the same HEAD instead of rerunning expensive gates.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: ADR-0003 + boundary docs | Batch A | Create: `docs/adr/ADR-0003-semantic-retrieval-ownership.md`; Modify: `CLAUDE.md`, `README.md`, `docs/README.md`; Generated: `AGENTS.md` (via sync script) | No | None - safe parallel batch. |
| Task 2: Telemetry version stamping | Batch A | Modify: `src/Miller.Server/Telemetry/TelemetryLedger.cs`, `src/Miller.Server/Telemetry/TelemetryRecord.cs` + its writer call sites; Test: `tests/Miller.Tests/Server/Telemetry/TelemetryLedgerTests.cs` | No | None - safe parallel batch. |
| Task 3: Edit failure-reason completeness | Batch A | Modify: `src/Miller.Server/Tools/EditTool.cs`, `src/Miller.Server/Tools/EditService.cs`; Test: `tests/Miller.Tests/Server/EditToolTests.cs` | No | None - safe parallel batch. |
| Task 4: sqlite-vec AOT spike | Batch A | Create: `spike/SqliteVec.AotSpike/**`, `scripts/spike-sqlite-vec.sh`, `scripts/spike-pins.json`, `docs/findings/2026-07-19-sqlite-vec-aot-spike.md`; Modify: `.github/workflows/ci.yml` (new independent job only) | No | None - safe parallel batch. |
| Task 5: Canary telemetry contract | Batch A | Create: `docs/contracts/canary-telemetry-v1.md`; Modify: `docs/README.md` is OWNED BY TASK 1 — hand the one-line map addition to the lead, do not edit `docs/README.md` | No | None - safe parallel batch (docs/README.md line goes through the lead to avoid conflict with Task 1). |
| Task 6: Eval harness + dev golden set | Batch A | Create: `eval/retrieval-eval/**` (harness project + fixtures + docs) | No | None - safe parallel batch. |
| Task 7: Model benchmark harness + benchmark run | None - serial | Create: `eval/model-bench/**`, `docs/findings/2026-07-19-model-benchmark.md`; Modify: `eval/retrieval-eval/README.md` (integration note) | Yes | Consumes the Task 6 harness CLI contract and dev golden set to score candidates. |

Commit mode: Batch A runs `parallel-lead-commit` (workers hand verified diffs to the lead; lead reviews inline, stages, commits). Task 7 runs `serial-worker-commit`.

---

### Task 1: ADR-0003 + boundary reversal docs

**Files:**
- Create: `docs/adr/ADR-0003-semantic-retrieval-ownership.md`
- Modify: `CLAUDE.md` (the "## 1.0 replacement boundary" section, the "Do not add Miller surfaces that need embeddings or semantic ranking" sentence, and the search-sidecar bullet's scope note), `README.md` (the architecture section that assigns semantic/vector retrieval to Eros — locate with Miller `search query="semantic" mode=content`), `docs/README.md` (map entries for ADR-0003 + the two design docs)
- Generated: `AGENTS.md` via `scripts/sync-agents.sh`

**Interfaces:**
- Consumes: design doc §2.1 (decision statement), §12 (rejected alternatives), doubt-pass requirement: Eros migration inventory + named Julie-compatibility owner.
- Produces: ADR-0003 as the citable authority all later phases reference; CLAUDE.md text that PERMITS local semantic retrieval in Miller (later-phase workers will read it).

**Contract inputs:** ADR format from `docs/adr/ADR-0001-guidance-delivery-channels.md` / `ADR-0002` (follow existing convention). Decision wording verbatim from design §2.1: "Miller owns optional local semantic retrieval; Eros owns fleet-level semantics: cross-workspace ranking, guidance/confidence views, embeddings-as-a-service orchestration."

**File ownership:** Create: `docs/adr/ADR-0003-semantic-retrieval-ownership.md`; Modify: `CLAUDE.md`, `README.md`, `docs/README.md`; Generated: `AGENTS.md` (via sync script)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The ADR recording the boundary reversal, plus edits to every doc that currently forbids or misassigns semantic retrieval. The ADR's Context section cites the telemetry evidence (symbol 0.7–2% empty vs source 42–60%/content 26–46%) and Eros's status; Consequences includes the Eros migration inventory (what Eros was slated to own → where it goes now: local semantic retrieval → Miller; fleet ranking/guidance/suppression persistence/commercial orchestration → remains Eros-reserved); Future Agents names the Julie-compatibility owner (the user, anortham) and the rule that the sidecar protocol is shared property — breaking changes require a Julie compatibility check.

**Approach:** Edit CLAUDE.md minimally — change the boundary sentences, do not restructure. The CLAUDE.md "replacement boundary" paragraph gains one sentence citing ADR-0003 and the design doc. README's Eros assignment gets the same treatment. Run `scripts/sync-agents.sh` last.

**Acceptance criteria:**
- [x] ADR-0003 exists in existing ADR format with Context/Decision/Consequences/Applies To/Future Agents, Eros migration inventory, and named Julie-compat owner
- [x] CLAUDE.md and README.md no longer forbid local semantic retrieval in Miller; both cite ADR-0003; no other guidance weakened (MCP-stinginess, language parity, test split untouched) — checked mechanically: `AgentInstructionsTests` green (`dotnet test --filter "FullyQualifiedName~AgentInstructionsTests"`)
- [x] `docs/README.md` maps ADR-0003 + both plan docs under active/current
- [x] `cmp -s CLAUDE.md AGENTS.md` passes after `scripts/sync-agents.sh`
- [x] Verified diff handed to lead (parallel-lead-commit)

### Task 2: Telemetry version stamping

**Files:**
- Modify: `src/Miller.Server/Telemetry/TelemetryLedger.cs` (schema DDL + additive migration + INSERT at :77 region), `src/Miller.Server/Telemetry/TelemetryRecord.cs:8` (new field), all `TelemetryRecord` construction sites (find with Miller `trace target=TelemetryRecord`)
- Test: `tests/Miller.Tests/Server/Telemetry/TelemetryLedgerTests.cs` (or the existing ledger test file — locate with Miller `search query="TelemetryLedger" mode=file`)

**Interfaces:**
- Consumes: `MillerVersion.Current` (existing single-sourced version, `src/Miller.Server/MillerVersion.cs` — verify exact symbol with Miller inspect before use).
- Produces: nullable `miller_version` TEXT column on `tool_telemetry`; every new row stamped with the semantic version + short SHA string. Cohort queries: `WHERE miller_version >= …`. The canary contract (Task 5) and §9 gates rely on this column existing.

**Contract inputs:** Shared-DB additive rule from Global Constraints. The table is STRICT — the new column must be declared with a type STRICT accepts (`TEXT`).

**File ownership:** Modify: `src/Miller.Server/Telemetry/TelemetryLedger.cs`, `src/Miller.Server/Telemetry/TelemetryRecord.cs` + its writer call sites; Test: `tests/Miller.Tests/Server/Telemetry/TelemetryLedgerTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Additive migration: **reuse the existing helper `EnsureTextColumn(connection, "miller_version")`** (`TelemetryLedger.cs:151` — it already does the pragma_table_info guard + `ALTER TABLE … ADD COLUMN … TEXT`). Do NOT write a parallel helper. One improvement to make in the helper itself: the pragma check has a TOCTOU race between two concurrent adders — wrap the ALTER in a try/catch for the duplicate-column error so concurrent opens tolerate each other. Extend the INSERT column list + parameters. Older Miller writers keep working because their INSERT names explicit columns and the new column is nullable.

**Approach:** Follow the ledger's existing connection/setup pattern. Test invariants: (a) migration is idempotent across two opens; (b) a row written after migration carries the current version string; (c) an INSERT using the OLD column list (simulating an older writer, via raw SQL like `InsertRawForTest` at TelemetryLedger.cs:431) still succeeds post-migration.

**Acceptance criteria:**
- [ ] Column is named exactly `miller_version` (Task 5's frozen contract references it by name); added additively via `EnsureTextColumn`; migration idempotent AND concurrent-adder-safe; old-writer INSERT proven still valid by test
- [ ] Every `TelemetryRecord` write path stamps the version (no null versions from current-binary writes, proven by test)
- [ ] No query text/paths in the new field (it is a version string only)
- [ ] Worker-scope verification passes; diff handed to lead (parallel-lead-commit)

### Task 3: Edit failure-reason completeness

**Files:**
- Modify: `src/Miller.Server/Tools/EditTool.cs` (telemetry stamping around :120), `src/Miller.Server/Tools/EditService.cs` (audit every `Error(...)` return for null `failureReason`; `FailureReasonFor` and `FailureUnknown` already exist — see :218, :571, :1091)
- Test: `tests/Miller.Tests/Server/EditToolTests.cs` (extend near the existing `Edit_PropagatesStructuredFailureReasonWithoutPersistingRawEditData` at :1461)

**Interfaces:**
- Consumes: existing `EditResult.FailureReason` contract ("privacy-safe stable failure bucket", EditService.cs:113).
- Produces: the invariant §7.1 of the design needs: **every** edit telemetry row with `outcome=error` carries a non-null `edit_failure_reason` bucket. Exception paths use bucket `unhandled_<ExceptionTypeName>` (type name only — never message text).

**Contract inputs:** Telemetry privacy constraint (no paths/content in buckets); historical gap: 41/52 error rows carried no reason.

**File ownership:** Modify: `src/Miller.Server/Tools/EditTool.cs`, `src/Miller.Server/Tools/EditService.cs`; Test: `tests/Miller.Tests/Server/EditToolTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Audit with Miller `search query="Error(" mode=source file_pattern=src/Miller.Server/Tools/EditService.cs` — every failure return must supply a stable bucket (use `FailureReasonFor(kind)` or a named constant). Note `Error(...)` at EditService.cs:1002 already defaults `failureReason = FailureUnknown` (`"unknown"`), so the real gaps are paths that BYPASS `Error(...)` — exceptions thrown out of EditTool/EditService before a result is built, and any return path constructing `EditResult` directly. Bucket semantics (document in the task's test): `unknown` = a known code path that reached `Error(...)` without a specific bucket; `unhandled_<ExceptionTypeName>` = the EditTool backstop for exceptions (type name only, never message text). Do NOT introduce a third `unclassified` bucket. Cover: validation failures, exception paths, rename/body-rewrite failures (not just replace_text).

**Approach:** Tests enumerate each failure class (usage error, no_match, ambiguous, stale_target, io failure, unhandled exception via a rigged provider) and assert a non-null, path-free bucket lands in `scope.MetadataJson`.

**Acceptance criteria:**
- [x] Test-enforced invariant: error-outcome edit telemetry always carries `edit_failure_reason`; buckets are stable enums, no paths/content (existing :1461 privacy assertions extended)
- [x] All EditService failure returns audited; no remaining null-reason paths for known error kinds
- [x] Worker-scope verification passes; diff handed to lead (parallel-lead-commit)

### Task 4: sqlite-vec Native-AOT spike (program HARD GATE probe)

**Files:**
- Create: `spike/SqliteVec.AotSpike/SqliteVec.AotSpike.csproj` (+ `Program.cs`), `scripts/spike-sqlite-vec.sh` (download + verify + publish + run), `scripts/spike-pins.json` (sqlite-vec v0.1.9 per-RID asset names + sha256s), `docs/findings/2026-07-19-sqlite-vec-aot-spike.md`
- Modify: `.github/workflows/ci.yml` — one new independent job (matrix over the 4 release RIDs: osx-arm64, osx-x64, linux-x64, win-x64) running the spike script; must not touch existing jobs. The spike job is its own `needs:` island — it gates no other job and is gated on no other job — and the findings doc records that it should be made a **required branch check** (branch-protection change is user-owned; note it, don't attempt it)

**Interfaces:**
- Consumes: sqlite-vec v0.1.9 release assets (loadable extension per platform) — pinned in `scripts/spike-pins.json`.
- Produces: the design's P0 hard-gate verdict, recorded in the findings doc: per-RID pass/fail for AOT `LoadExtension`. Later phases consume `spike-pins.json`'s shape as the template for `semantic-pins.json`.

**Contract inputs:** Design §5.4: absolute-path `LoadExtension` via `Microsoft.Data.Sqlite` (SQLitePCLRaw bundle), `vec_version()` verification. Spike stays OUTSIDE `Miller.slnx`.

**File ownership:** Create: `spike/SqliteVec.AotSpike/**`, `scripts/spike-sqlite-vec.sh`, `scripts/spike-pins.json`, `docs/findings/2026-07-19-sqlite-vec-aot-spike.md`; Modify: `.github/workflows/ci.yml` (new independent job only)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** A console app with `<PublishAot>true</PublishAot>` that: loads the pinned vec0 extension from an absolute path, asserts `vec_version()`, creates a `vec0` table (`float[8] distance_metric=cosine` — dims don't matter for the spike), inserts vectors via integer rowids, runs a KNN `MATCH ? AND k=3` query, exercises DELETE-then-INSERT in a transaction, and runs a WAL two-connection reader/writer smoke. Exit code 0 = pass; any failure prints the failing stage. The script downloads the RID's asset, verifies sha256, publishes AOT, runs, and echoes the verdict.

**Approach:** Mirror `spike/Codesearch.Spike` conventions for placement. Windows/Linux legs run in CI (report as pending evidence until push); the local leg (osx-arm64) must pass before handoff. Findings doc records: verdict per RID, binary sizes, load mechanism notes, and any SQLitePCLRaw/AOT trimming flags needed — these notes feed P2b directly. If the spike FAILS on any RID, that is a program-level gate failure: report it prominently; do not work around it silently.

**Acceptance criteria:**
- [ ] Spike passes locally on osx-arm64 under `dotnet publish -c Release` with AOT (no JIT fallback)
- [ ] CI job added for all 4 RIDs (evidence pending until push — stated in findings doc)
- [ ] All downloads sha256-pinned in `scripts/spike-pins.json`; script fails loud on mismatch
- [ ] Findings doc records verdicts + AOT flags/trimming notes for P2b
- [ ] Product build unaffected: `dotnet build Miller.slnx -c Release` still 0 warnings
- [ ] Worker-scope verification passes; diff handed to lead (parallel-lead-commit)

### Task 5: Canary telemetry contract (frozen)

**Files:**
- Create: `docs/contracts/canary-telemetry-v1.md`
- Note: the `docs/README.md` map line is handed to the lead as text (Task 1 owns that file)

**Interfaces:**
- Consumes: design §9.1 (canary requirements), Task 2's `miller_version` column (referenced, not implemented here).
- Produces: the frozen field/semantics contract P2b implements and P5 gates on. Field list is exact and exhaustive — implementers may not add fields without a v2.

**Contract inputs:** Telemetry privacy constraint. Existing telemetry vocabulary (`tool_telemetry` columns, `metadata_json` conventions — verify names with Miller before writing).

**File ownership:** Create: `docs/contracts/canary-telemetry-v1.md`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch (docs/README.md line goes through the lead to avoid conflict with Task 1).

**What to build:** The complete contract: assignment unit (stable per workspace+day+query-class bucket, hash-derived — define the exact derivation so assignment is deterministic and balanced); `experiment_id`/`arm` enums; `query_class` enum (mirrors SemanticQueryPolicy classes, enumerated now: `identifier`, `path`, `short_token`, `prose`, `docs_like`, `mixed`); opaque result identifiers (existing target-hash mechanism, named explicitly) with a follow-up attribution window (definition: a subsequent `inspect`/`content read` whose target hash matches a result served within the window; window length stated); the success event definition; per-row fields (arm, eligibility, per-arm result counts, rescue/fallback reason enum, backend enum, cold/warm flag, latency bucket enum with exact bucket edges); shadow-population semantics for identifier non-inferiority (shadow-execute, compare offline, never affects served results); retention and aggregation-export shapes (enums/counters only). State explicitly which fields land in columns vs `metadata_json`.

**Approach:** Follow `docs/contracts/` house style (e.g. `references-candidates-v1.md`, `metrics-history-v1.md`). Every field gets: name, type, enum values, when written, privacy note.

**Acceptance criteria:**
- [ ] Contract is implementable without further design decisions (a P2b worker could build it from this doc alone)
- [ ] No field can carry query text or paths; each field's privacy note says why
- [ ] Assignment determinism + attribution window + success event are exactly defined
- [ ] Worker-scope verification (doc self-check: no TBDs, all enums enumerated); diff handed to lead (parallel-lead-commit)

### Task 6: Retrieval eval harness + dev golden set

**Files:**
- Create: `eval/retrieval-eval/` — harness project (console, NOT in Miller.slnx), `eval/retrieval-eval/README.md` (usage + set-construction protocol), `eval/retrieval-eval/sets/dev/*.jsonl` (dev golden set), `eval/retrieval-eval/sets/SEALED-SET-PROTOCOL.md`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: harness CLI contract Task 7 depends on: `dotnet run --project eval/retrieval-eval -- score --corpus <dir> --queries <jsonl> --results <jsonl> --out <report.json>` where `--results` is arm output in a defined JSONL shape (`query_id`, ranked `doc_id` list), and the report contains recall@10, nDCG@10, per-language macro-average, worst-language, per-intent-cluster rollup. Also the query-set JSONL schema: `query_id`, `intent_cluster`, `query_class` (same enum as Task 5), `repo`, `language`, `relevant` (doc ids + grades), `negative` (bool).

**Contract inputs:** Design §8 (eval protocol): intent clusters scored as clusters; macro-average AND worst-language; negatives included; sealed-set separation.

**File ownership:** Create: `eval/retrieval-eval/**` (harness project + fixtures + docs)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** (a) The scoring harness — pure computation over JSONL inputs, no embedding calls (backends produce results files; the harness only scores). Unit-tested metric math (recall@k, nDCG@k with graded relevance, cluster-max scoring: a cluster counts as hit if any paraphrase in it retrieves a relevant doc). (b) The dev golden set: ≥60 queries spanning miller + julie repos: ≥6 paraphrase intent clusters per repo (3+ paraphrases each), ≥15 identifier queries (non-inferiority set), ≥5 short-token, ≥5 negation/ambiguous, ≥5 irrelevant negatives; every query labeled with `query_class`, language, and graded relevant docs (symbol ids/file paths verified against the real repos with Miller). (c) `SEALED-SET-PROTOCOL.md`: the acceptance set is user-owned, same schema, stored outside the repo until evaluation events, never used during tuning; document the handoff procedure.

**Approach:** Keep the harness dependency-free (System.Text.Json only). Seed dev queries from the design's documented failure modes (paraphrase queries that lexical search currently misses — mine candidates by running Miller `search mode=source` for prose phrasings of known subsystems and recording misses).

**Acceptance criteria:**
- [ ] Harness scores a synthetic fixture correctly (unit tests for recall@k, nDCG@k, cluster scoring, macro/worst-language rollups)
- [ ] Dev set meets the composition minimums above; all relevant-doc references verified to exist; a manifest in `eval/retrieval-eval/sets/dev/` pins the miller + julie repo paths AND the exact commit SHAs the set was constructed against (later re-tuning must not silently drift the ground truth)
- [ ] Results/queries JSONL schemas documented in README (Task 7's integration contract)
- [ ] Sealed-set protocol documented; no sealed data in repo
- [ ] Worker-scope verification passes; diff handed to lead (parallel-lead-commit)

### Task 7: Model benchmark harness + benchmark run → pin recommendation

**Files:**
- Create: `eval/model-bench/` — `bench-pins.json` (llama.cpp prebuilt release + candidate GGUF URLs, all sha256-pinned), `run-bench.sh` (download → verify → embed corpus+queries per candidate → emit results JSONL per arm → invoke retrieval-eval scorer), `eval/model-bench/README.md`, `docs/findings/2026-07-19-model-benchmark.md`
- Modify: `eval/retrieval-eval/README.md` (integration note)

**Interfaces:**
- Consumes: Task 6 harness CLI + JSONL schemas; dev golden set.
- Produces: the design's P0 model gate: `docs/findings/2026-07-19-model-benchmark.md` with the pin recommendation (model + dims + quantization lane) and its evidence. P1's `semantic-pins.json` and the sidecar's model manifest consume this.

**Contract inputs:** Design §2.4 + §4.1: candidates = Qwen3-Embedding-0.6B (pooling `last`, `<|endoftext|>` append, instruction prefixes, MRL 256/512/1024 slice→renormalize) vs Apache/MIT fallback tier (bge-small-en-v1.5 384d pooling `cls`; snowflake-arctic-embed-s if GGUF availability confirms — verify availability, do not assume). Correctness gotchas from the runtime research: wrong pooling silently degrades — the harness must include a sanity check (self-similarity of a known-similar pair must beat a known-dissimilar pair by margin) per candidate before scoring.

**File ownership:** Create: `eval/model-bench/**`, `docs/findings/2026-07-19-model-benchmark.md`; Modify: `eval/retrieval-eval/README.md` (integration note)

**Serialization required:** Yes

**Dependency reason:** Consumes the Task 6 harness CLI contract and dev golden set to score candidates.

**What to build:** A scripted, reproducible benchmark: download pinned upstream llama.cpp prebuilt (macos-arm64 for the local run) + pinned candidate GGUFs; build the corpus (symbol cards generated from the dev-set repos using the design's v1 card template — a small generator script reading Miller's `symbols.db` via sqlite; this generator is throwaway bench tooling, not product code); embed corpus + queries per candidate/dims-lane with correct per-model flags; produce ranked results per query (cosine over the embedded corpus — brute force in the script); score via Task 6 harness; emit the comparison table. Then RUN it locally (macos-arm64) for all candidate/lane combinations and write the findings doc: metrics per candidate/lane (macro + worst-language + identifier non-inferiority vs a BM25 baseline arm produced from Miller's actual `search mode=symbol` output for the same queries), model load + embed throughput observations (report-only), and the pin recommendation with rationale.

**Approach:** Downloads total ~1–2GB (models + llama.cpp) — proceed; they are cached under `eval/model-bench/.cache/` (gitignored). If a candidate's GGUF or license claim fails verification at download time, record it in the findings and drop the candidate rather than substituting an unpinned source. If the local hardware cannot complete all lanes in reasonable time, complete Qwen3 lanes + one fallback fully and record which lanes remain, with the exact command to run them. **Pin decision rule (evidence-gated):** the default pin may name Qwen3 only from completed Qwen3 lanes, and a fallback pin may only be named from a completed fallback lane — the fallback tier is the license-safe escape hatch, so it must be evidence-backed in P0, never inferred. If no fallback lane completes, the findings doc says so explicitly and the fallback pin is recorded as OPEN, not defaulted.

**Acceptance criteria:**
- [ ] All artifacts sha256-pinned; cache gitignored; re-run reproducible from clean cache
- [ ] Per-candidate pooling sanity check passes before scoring (guards the silent-garbage failure mode)
- [ ] Benchmark run completed locally; findings doc contains per-candidate/lane metrics vs BM25 baseline, identifier non-inferiority table, and an explicit pin recommendation (model + dims + quantization)
- [ ] Worker-scope verification passes (harness unit checks + successful end-to-end run); worker commits (serial-worker-commit)

---

## Post-plan notes for the lead

- Batch A merges in task order 1→6. The `docs/README.md` line for the canary contract is applied by the lead exactly once, during **Task 1's** commit (Task 1 owns `docs/README.md`; Task 5 only hands the line text to the lead — Task 5's commit must not touch that file).
- After Batch A + Task 7: run branch gate (`dotnet build Miller.slnx -c Release`, `scripts/test.sh all`), then goldfish checkpoint, then report P0 complete with the two gate verdicts (spike, model pins) and CI-pending evidence. P1 planning is a new writing-plans invocation against the design doc.
- User-owned follow-up (not a task): sealing the acceptance set per `SEALED-SET-PROTOCOL.md`, and reviewing the pin recommendation before P1 freezes it.
