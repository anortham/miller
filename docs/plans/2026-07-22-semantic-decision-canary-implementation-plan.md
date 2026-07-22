# Semantic Decision Canary Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Build a bounded canary v3 decision profile, privacy-safe multi-machine aggregation, and a reproducible frozen local cohort without changing v2 or lexical-only behavior.

**Architecture:** A single `CanaryContractProfile` resolves runtime policy for v2 versus v3. Existing local writer/export/gate modules accept an explicit contract version, while a new pure `CanaryAggregate` module validates and combines only privacy-safe v3 documents; CLI code stays thin. The final operator slice freezes one complete Release output and enrolls existing local MCP clients only after this plan and the sealed-evaluation plan pass.

**Tech Stack:** .NET 10, C#, SQLite telemetry ledger, `System.Text.Json`, xUnit, shell/PowerShell project scripts.

**Architecture Quality:** High-risk contract change. Contract selection and sampling policy stay deep in one profile; v2 overloads and JSON remain byte-identical; aggregation owns all validation/statistics; no new MCP tool, machine service, or fleet feature is introduced.

## Global Constraints

- `MILLER_SEMANTIC=off` remains a permanent zero-work and byte-identical lexical guarantee.
- `MILLER_SEMANTIC_CANARY=on|1` remains contract v2 with 10% identifier-unit shadow sampling.
- `MILLER_SEMANTIC_CANARY=decision` is contract v3 with the same hybrid assignment and 100% identifier-unit shadow sampling.
- V2 and v3 rows are never pooled; every gate and export operates on one explicit contract version.
- Omitting `--contract` selects 2; existing v2 export bytes and existing v2 CLI invocations do not change.
- V3 export requires an operator-supplied random 128-bit source id encoded as exactly 32 lowercase hex characters and never derives host, user, hardware, or path identity.
- All frozen v2 gate thresholds, assignment ids, assignment version, cohort fields, suppression floors, and statistical estimators remain unchanged.
- The v3 export contains counters, enums, opaque ids, and bucketed latency only: no query text, path, workspace id, raw per-call latency, or result content.
- The aggregate latency result is labeled a screen; only the local raw-row gate is authoritative for the 20% p95 threshold.
- No new MCP tool, dashboard mutation, Eros-owned fleet feature, arbitrary sampling percentage, threshold change, or machine-wide semantic service.
- Use TDD for every production behavior. Do not weaken existing canary, lexical-parity, privacy, or CLI tests.
- Do not push, publish, release, or change the normal product default.

---

## Verification Strategy

**Project source of truth:** `CLAUDE.md` / `AGENTS.md`, especially the fast/Scale split, warnings-as-errors build, semantic ownership boundary, MCP stinginess, and lexical-parity rules.

**Worker red/green scope:** Run the narrow test classes owned by each task with `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~<owned test class>"`. Task 1 covers canary telemetry/search/shadow; Task 2 covers export/gate/CLI; Task 3 covers aggregate/CLI.

**Worker ceiling:** The focused owned test classes plus `dotnet build src/Miller.Server/Miller.Server.csproj -c Release`. Workers do not run the full fast or Scale suites.

**Worker gate invariant:** V2 behavior is unchanged, v3 behavior is explicit and privacy-safe, invalid aggregate inputs fail closed, and no assigned test can pass by silently dropping a row or cohort.

**Lead affected-change scope:** `scripts/test.sh` after Tasks 1–3 form one coherent product batch, plus `dotnet test eval/retrieval-eval/tests/RetrievalEval.Tests.csproj` after the companion plan integrates.

**Branch gate:** `dotnet build Miller.slnx -c Release`, `scripts/test.sh`, `scripts/test.sh scale`, `dotnet test eval/retrieval-eval/tests/RetrievalEval.Tests.csproj`, and `python3 -m unittest eval/retrieval-eval/tests/test_run_live_arm.py`.

**Replay/metric evidence:** Hard gates are v2 byte identity, v3 schema/contract selection, exact success/shadow aggregation, overlap rejection, privacy-field absence, local v3 gate computation, and stable-binary identity. Aggregate latency is report-only screening; no canary metric is allowed to claim promotion before its minimum population.

**Escalation triggers:** Any change to assignment ids, assignment version, v2 JSON, cohort identity, threshold math, telemetry retention, semantic serving policy, public search JSON, or external MCP config shape requires lead review before implementation continues. Any real-sidecar or indexing failure triggers the Scale and packaged semantic-smoke tiers.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in `docs/findings/2026-07-22-semantic-decision-readiness.md`. For replay or metric evidence, also record hard-gate metrics and report-only metrics. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Contract v3 runtime profile | None - serial | Create `docs/contracts/canary-telemetry-v3.md`, `src/Miller.Server/Telemetry/CanaryContractProfile.cs`; modify `src/Miller.Server/Telemetry/CanaryTelemetry.cs`, `src/Miller.Server/Tools/SearchTool.cs`, `tests/Miller.Tests/Server/CanaryTelemetryTests.cs`, `tests/Miller.Tests/Server/CanaryShadowPopulationTests.cs`, `tests/Miller.Tests/Server/CanarySearchTests.cs`, `tests/Miller.Tests/Server/CanaryContentSearchTests.cs` | Yes | Contract and profile must be frozen before readers or aggregators consume v3. |
| Task 2: Version-selected local export and gate | None - serial | Modify `src/Miller.Server/Telemetry/CanaryExport.cs`, `src/Miller.Server/Telemetry/CanaryGateReport.cs`, `src/Miller.Server/Cli/CliDispatch.cs`, `tests/Miller.Tests/Telemetry/CanaryExportTests.cs`, `tests/Miller.Tests/Telemetry/CanaryGateReportTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` | Yes | Consumes Task 1 contract versions and profile invariants. |
| Task 3: Privacy-safe export combiner | None - serial | Create `src/Miller.Server/Telemetry/CanaryAggregate.cs`, `tests/Miller.Tests/Telemetry/CanaryAggregateTests.cs`; modify `src/Miller.Server/Cli/CliDispatch.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`, `docs/contracts/canary-telemetry-v3.md` | Yes | Consumes the exact v3 export shape produced by Task 2. |
| Task 4: Runbook, frozen build, and local enrollment | None - serial | Modify `docs/findings/2026-07-21-p5-canary-runbook.md`, `docs/README.md`, `/Users/murphy/.codex/config.toml`, `/Users/murphy/.claude.json`, `/Users/murphy/.claude/settings.json`, `/Users/murphy/.cursor/mcp.json`; create `docs/findings/2026-07-22-semantic-decision-readiness.md` and the versioned local install/manifest under `/Users/murphy/.local/share/miller/canary/` | Yes | Lead-only final integration after Tasks 1–3, the companion sealed-evaluation plan, and the full branch gate pass. |

### Task 1: Contract v3 runtime profile

**Files:**
- Create: `docs/contracts/canary-telemetry-v3.md`
- Create: `src/Miller.Server/Telemetry/CanaryContractProfile.cs`
- Modify: `src/Miller.Server/Telemetry/CanaryTelemetry.cs:8-29,342-444`
- Modify: `src/Miller.Server/Tools/SearchTool.cs:1323-1465,2060-2090`
- Test: `tests/Miller.Tests/Server/CanaryTelemetryTests.cs`
- Test: `tests/Miller.Tests/Server/CanaryShadowPopulationTests.cs`
- Test: `tests/Miller.Tests/Server/CanarySearchTests.cs`
- Test: `tests/Miller.Tests/Server/CanaryContentSearchTests.cs`

**Interfaces:**
- Consumes: frozen v2 contract, `CanaryAssignment.Bucket`, `CanaryAssignment.ResolveArm`, `CanaryMode`, and existing search canary facts.
- Produces: `CanaryMode.Decision`; `CanaryContractProfile.For(CanaryMode)` returning contract version and identifier shadow percentage; `CanaryTelemetry.StampShadow(TelemetryScope, CanaryMode, CanaryShadowFacts)`; v3 contract document.

**Contract inputs:** `decision -> (contract_version=3, identifier_shadow_percent=100)`; `on|1 -> (2,10)`; `off|0|invalid -> no profile work`. Hybrid experiment, assignment, and served results are unchanged.

**File ownership:** Create `docs/contracts/canary-telemetry-v3.md`, `src/Miller.Server/Telemetry/CanaryContractProfile.cs`; modify `src/Miller.Server/Telemetry/CanaryTelemetry.cs`, `src/Miller.Server/Tools/SearchTool.cs`, `tests/Miller.Tests/Server/CanaryTelemetryTests.cs`, `tests/Miller.Tests/Server/CanaryShadowPopulationTests.cs`, `tests/Miller.Tests/Server/CanarySearchTests.cs`, `tests/Miller.Tests/Server/CanaryContentSearchTests.cs`

**Serialization required:** Yes.

**Dependency reason:** Contract and profile must be frozen before readers or aggregators consume v3.

**What to build:** Add an explicit decision activation/profile and route both ordinary and shadow stamps through it. Change identifier shadow selection to compare the existing deterministic bucket against the profile percentage; do not change query classification, arm assignment, served output, or the semantic-off early return.

**Approach:** Keep the profile a small immutable policy object beside telemetry, not conditionals scattered through `SearchTool`. Preserve existing v2 tests as golden behavior and add v3 tests proving every identifier unit is sampled while identifiers still serve lexical bytes. The v3 contract inherits v2 and lists only replacements/additions.

**Acceptance criteria:**
- [ ] `CanaryActivation.Parse("decision")` selects `CanaryMode.Decision`; all existing values retain current results.
- [ ] V2 stamps contract 2 and samples exactly bucket `<10`; v3 stamps contract 3 and samples every identifier unit.
- [ ] V3 does not change hybrid arm assignment, identifier served results, or semantic-off side effects.
- [ ] Privacy tests prove no query/path text appears in either v2 or v3 metadata.
- [ ] Worker-scope verification passes and the change is committed with `serial-worker-commit`.

### Task 2: Version-selected local export and gate

**Files:**
- Modify: `src/Miller.Server/Telemetry/CanaryExport.cs:17-367`
- Modify: `src/Miller.Server/Telemetry/CanaryGateReport.cs:66-330`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs:1296-1352`
- Test: `tests/Miller.Tests/Telemetry/CanaryExportTests.cs`
- Test: `tests/Miller.Tests/Telemetry/CanaryGateReportTests.cs`
- Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Interfaces:**
- Consumes: Task 1 contract constants/profile, `CanaryLedgerReader`, `CanaryGateMath`, frozen v2 renderer.
- Produces: backward-compatible v2 overloads plus explicit `contractVersion` export/gate overloads; CLI `--contract 2|3`; v3 `--source-id`; `warm_total_latency_bucket_counts` on v3 treatment units.

**Contract inputs:** Default contract is 2. V2 rejects `--source-id` and renders byte-for-byte as before. V3 requires a source id matching `[0-9a-f]{32}`, schema version 3, contract version 3, and warm-only total latency buckets.

**File ownership:** Modify `src/Miller.Server/Telemetry/CanaryExport.cs`, `src/Miller.Server/Telemetry/CanaryGateReport.cs`, `src/Miller.Server/Cli/CliDispatch.cs`, `tests/Miller.Tests/Telemetry/CanaryExportTests.cs`, `tests/Miller.Tests/Telemetry/CanaryGateReportTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Serialization required:** Yes.

**Dependency reason:** Consumes Task 1 contract versions and profile invariants.

**What to build:** Parameterize the local reader paths without replacing their v2 defaults. Add only the v3 export fields needed for safe aggregation and warm-latency screening, then expose explicit contract selection through the existing CLI usage/error conventions.

**Approach:** Retain the current overloads as v2 delegates so source and output compatibility are testable. Validate contract/source combinations before reading the ledger. Filter rows before attribution and grouping, retain exact identity strata, and make v3 deterministic for a fixed window/source just like v2.

**Acceptance criteria:**
- [ ] Existing v2 fixture output is byte-identical and existing no-flag CLI behavior still selects v2.
- [ ] V3 export includes source identity and warm treatment latency buckets whose counts equal warm treatment rows.
- [ ] `--gate --contract 3` reads only v3 rows and reports the unchanged three clauses within exact cohorts.
- [ ] Unknown contracts, malformed/missing v3 source ids, v2 source ids, malformed dates, and conflicting CLI flags exit with usage code 2.
- [ ] V2 and v3 rows cannot contribute to the same export or gate cohort.
- [ ] Worker-scope verification passes and the change is committed with `serial-worker-commit`.

### Task 3: Privacy-safe export combiner

**Files:**
- Create: `src/Miller.Server/Telemetry/CanaryAggregate.cs`
- Create: `tests/Miller.Tests/Telemetry/CanaryAggregateTests.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs:1296-1352`
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- Modify: `docs/contracts/canary-telemetry-v3.md`

**Interfaces:**
- Consumes: v3 JSON strings, frozen enum vocabularies, `CanaryGateMath`, exact semantic cohort tuple.
- Produces: `CanaryAggregate.Combine(IReadOnlyList<string>)`, aggregate report records/renderers, and `miller telemetry canary combine <export.json>... [--json]`.

**Contract inputs:** Only schema 3 / contract 3 / known experiments and source ids matching `[0-9a-f]{32}`. Same-source windows must be disjoint unless the documents are exact duplicates. Same `unit_id` across different sources is merged as one randomized unit. Five-call suppression already occurred locally and is never reverse-engineered.

**File ownership:** Create `src/Miller.Server/Telemetry/CanaryAggregate.cs`, `tests/Miller.Tests/Telemetry/CanaryAggregateTests.cs`; modify `src/Miller.Server/Cli/CliDispatch.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`, `docs/contracts/canary-telemetry-v3.md`

**Serialization required:** Yes.

**Dependency reason:** Consumes the exact v3 export shape produced by Task 2.

**What to build:** Parse and validate every envelope before combining anything. Deduplicate exact repeated exports, reject ambiguous overlap, merge cross-source copies of one randomized unit, partition exact semantic cohorts, compute the frozen success/shadow clauses, and render a clearly non-authoritative bucketed latency screen.

**Approach:** Validate count-map vocabularies, nonnegative counts, count totals, arm/bucket consistency, identity completeness, 12-hex unit ids, date/window containment, and duplicate-unit consistency fail-closed. Keep file reads and usage errors in `CliDispatch`; the aggregate module consumes document strings so tests are pure.

**Acceptance criteria:**
- [ ] One valid v3 export reproduces its exact unit counts; several disjoint sources combine deterministically.
- [ ] Exact duplicates deduplicate; conflicting duplicates and partially overlapping same-source windows fail.
- [ ] Same randomized unit across sources merges before statistics and counts as one unit.
- [ ] Incompatible identities never pool, and null/incomplete identities cannot produce a passing cohort.
- [ ] Success and identifier-shadow math matches local gate fixtures; latency is labeled `screen`, never `gate_passes`.
- [ ] JSON and human output contain no source paths, query/result content, workspace ids, or raw milliseconds.
- [ ] CLI accepts one or more positional export paths, rejects missing/unknown options, and preserves other telemetry verbs.
- [ ] Worker-scope verification passes and the change is committed with `serial-worker-commit`.

### Task 4: Runbook, frozen build, and local enrollment

**Files:**
- Modify: `docs/findings/2026-07-21-p5-canary-runbook.md`
- Modify: `docs/README.md`
- Create: `docs/findings/2026-07-22-semantic-decision-readiness.md`
- Create: versioned install directory and manifest under `/Users/murphy/.local/share/miller/canary/semantic-decision-<short-sha>/`
- Modify: existing Miller MCP entries only in `/Users/murphy/.codex/config.toml`, `/Users/murphy/.claude.json`, `/Users/murphy/.claude/settings.json`, `/Users/murphy/.cursor/mcp.json`

**Interfaces:**
- Consumes: completed Tasks 1–3, completed companion sealed-evaluation plan, clean branch gate, pinned restore scripts, current client config entries.
- Produces: immutable canary executable/config identity, v3 source id and dates, weekly export commands, rollback command/path, readiness and verification ledger.

**Contract inputs:** Target day 14, hard-stop day 30; `MILLER_SEMANTIC=on`; `MILLER_SEMANTIC_CANARY=decision`; no machine service; no normal-default change; no release/push.

**File ownership:** Modify `docs/findings/2026-07-21-p5-canary-runbook.md`, `docs/README.md`, `/Users/murphy/.codex/config.toml`, `/Users/murphy/.claude.json`, `/Users/murphy/.claude/settings.json`, `/Users/murphy/.cursor/mcp.json`; create `docs/findings/2026-07-22-semantic-decision-readiness.md` and the versioned local install/manifest under `/Users/murphy/.local/share/miller/canary/`

**Serialization required:** Yes.

**Dependency reason:** Lead-only final integration after Tasks 1–3, the companion sealed-evaluation plan, and the full branch gate pass.

**What to build:** Update the operational documentation, restore pinned tools, build Release, copy the complete output to an immutable SHA-named directory, record hashes/semantic identity, and point only the existing enrolled MCP client entries at it with the decision environment. Preserve every unrelated config value and provide a one-edit rollback to the prior executable/env.

**Approach:** Run all worktree/branch checks before and after external config edits. Verify `miller version`, semantic prepare/status, vector cursor convergence, one lexical identifier query, one prose treatment-capable query, v3 export, and v3 local gate. Record RSS, idle CPU, process count, vector sizes, start/day-14/day-30 dates, source id, exact config files changed, and all verification commands in the readiness finding.

**Acceptance criteria:**
- [ ] Branch gate and companion evaluator gate pass at the exact frozen commit.
- [ ] Installed binary/tool payload hashes and semantic cohort identity are recorded and match live status.
- [ ] All four client configs resolve the same immutable binary with semantic on and canary decision; unrelated entries are byte-preserved.
- [ ] Identifier output stays lexical; v3 rows/export/gate appear; v2 history remains readable but separate.
- [ ] Runbook defines non-overlapping weekly exports taken at least 600 seconds after window close, day-14 review, day-30 forced verdict, rollback, promotion gates, and full-removal triggers.
- [ ] Baseline cost/reliability evidence and verification ledger are recorded without claiming an underpowered gate passed.
- [ ] Lead verifies all involved worktrees and the original unrelated machine-service draft remain intact, then commits only repo-owned documentation with `serial-worker-commit`.
