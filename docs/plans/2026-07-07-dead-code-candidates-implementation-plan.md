# Dead-Code Candidates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Ship `miller references candidates [--json]` — a deterministic, conservatively-suppressed dead-code candidate list built on julie-extract schema v4 reference resolution, per the approved design `docs/plans/2026-07-07-dead-code-candidates-design.md` (rev 2, Codex doubt-pass folded in).

**Architecture:** Pure candidate/suppression/evidence logic in `Miller.Core/DeadCode` (zero I/O); a `DeadCodeCandidateReader` in `Miller.Indexing` that pages symbols/identifiers/resolutions and runs the string-literal span scan last over survivors only; CLI wiring in `CliDispatch` under the existing `references` verb. Follows the `ReferenceExportReader` seam — no new architecture.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, xUnit. No new dependencies.

**Architecture Quality:** Follows the established artifact-reader seam (`ReferenceExportReader` / `SqliteSourceRegionReader`). Approved shape: Core evaluator consumes plain row records and returns a typed result; Indexing owns all SQL and source re-reads; Server owns rendering/JSON/capabilities. Risk: low (read-only surface). If code reality contradicts this shape, report a plan mismatch rather than redesigning locally.

## Global Constraints

- **The design doc is the spec.** `docs/plans/2026-07-07-dead-code-candidates-design.md` rev 2 governs; where this plan and the design conflict, report the mismatch.
- **False positives are the failure mode.** Every rule ambiguity resolves toward NOT flagging.
- Candidate kinds: `function, method, class, struct, interface, enum, delegate, property, constant`. Syntax-invoked name-shape exclusions: names starting `~`, containing `this[`, starting `operator`, starting `op_`, or equal to `Finalize`.
- Nine named suppression rule ids, exactly: `public_api`, `visibility_unknown`, `test_symbol`, `entry_point`, `framework_bound`, `annotated`, `generated_path`, `low_evidence_language`, `string_literal_match`.
- Evidence labels are provenance, never certainty: `name` and `name+resolver` (resolver label requires ≥ 10% measured per-language resolution coverage in the artifact, computed at query time — no hardcoded language lists).
- Compact header must include verbatim: `resolver: <status> — candidates are facts to check, not deletions to make.` where `<status>` is the artifact's `reference_resolution_status` metadata value (fallback `unknown` when the key is absent).
- JSON envelope: `schema_version: 1`, `candidates[]` (symbol_id, name, kind, language, path, start_line, visibility, evidence_label, evidence{name_matches, resolved_inbound, pending_resolved_inbound, calls_inbound}), `suppressions{rule_id: count}`, `literal_scan{files_scanned, files_skipped_stale}`, `language_coverage[]` (language, identifiers, resolved_pct), `examined`, `artifact{artifact_id, revision, reference_resolution_status, reference_resolution_version}`.
- Generated-path globs (conservative): `*.g.cs`, `*.generated.*`, `obj/`, `bin/`, `node_modules/`, `*.designer.cs`, `wwwroot/lib/`.
- No new MCP tool. No dashboard/`miller report`/README/agent-instructions surfacing (evidence-gated). `MILLER_AGENT_INSTRUCTIONS.md` untouched.
- Build: 0 warnings / 0 errors (warnings are errors). `Miller.Core` stays zero-I/O.
- Test split: julie-spawning tests are `[Trait("Category","Scale")]` via `ScaleTestSupport.RequireJulieServer()`; fast suite stays fast/pure.
- CLAUDE.md edits go through `scripts/sync-agents.sh` then `cmp -s CLAUDE.md AGENTS.md`.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing + Build sections), `scripts/test.sh`.

**Worker red/green scope:** focused filters, e.g. `dotnet test tests/Miller.Tests -c Release --filter "FullyQualifiedName~DeadCodeCandidates"` (Task 1), `~DeadCodeCandidateReader` (Task 2), `~CliDispatchTests` (Task 3). Fast-suite category rules apply automatically.

**Worker ceiling:** `scripts/test.sh` (fast suite). Workers do not run the scale suite on their own except the Task 3 worker's single new Scale test via `scripts/test.sh scale`.

**Worker gate invariant:** each task's acceptance criteria are proven by tests the worker runs red→green in their own scope.

**Lead affected-change scope:** `scripts/test.sh` after each task lands.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh` + `scripts/test.sh scale` before the final report. `.tools/julie-extract` 2.9.0 is present at repo root (restored this session) — scale tests must NOT silently skip; verify `Skipped: 0`.

**Replay/metric evidence:** Task 5's dogfood counts are report-only evidence recorded in the findings doc; the hard gate is hand-verified precision (no confirmed-live symbol may appear as a candidate).

**Escalation triggers:** any change under `src/Miller.Indexing/` or to fixtures ⇒ run `scripts/test.sh scale` at branch gate (already required).

**Assigned verification failure:** workers stop and report; do not weaken guards or fixtures to pass.

**Verification ledger:** record invariant, command, scope label, commit SHA, result, timestamp per task. Reuse passing same-HEAD entries rather than rerunning expensive gates.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Core evaluator | None - serial | Create: `src/Miller.Core/DeadCode/*.cs`; Test: `tests/Miller.Tests/Core/DeadCodeCandidatesTests.cs` | Yes | Produces the row-record and result contracts Tasks 2–3 consume. |
| Task 2: Indexing reader + v4 fixture | None - serial | Create: `src/Miller.Indexing/DeadCodeCandidateReader.cs`; Modify: `tests/Miller.Tests/Indexing/JulieDbFixture.cs`, `tests/Miller.Tests/Indexing/JulieDbFixtureCurrentSchemaTests.cs`; Test: `tests/Miller.Tests/Indexing/DeadCodeCandidateReaderTests.cs` | Yes | Consumes Task 1's contracts; produces the reader API Task 3 wires. |
| Task 3: CLI + capabilities + Scale test | None - serial | Modify: `src/Miller.Server/Cli/CliDispatch.cs`, `src/Miller.Server/Cli/CliCapabilities.cs`; Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`, `tests/Miller.Tests/Server/Cli/DeadCodeCandidatesScaleTests.cs` (new) | Yes | Consumes Task 2's reader API. |
| Task 4: Contracts + boundary docs | None - serial | Create: `docs/contracts/references-candidates-v1.md`; Modify: `docs/contracts/references-export-v1.md`, `docs/contracts/cli-eros-v1.md`, `docs/README.md`, `CLAUDE.md`, `AGENTS.md` (generated) | Yes | Documents Task 3's shipped shapes verbatim (JSON field names, capabilities keys). |
| Task 5: Dogfood evidence | None - serial | Create: `docs/findings/2026-07-07-dead-code-candidates-dogfood.md` | Yes | Runs the Task 3 binary against real artifacts; is the design's evidence gate. |

Commit mode: `serial-worker-commit` for every task.

---

### Task 1: Core evaluator (`Miller.Core/DeadCode`)

**Files:**
- Create: `src/Miller.Core/DeadCode/DeadCodeCandidates.cs` (evaluator + result types)
- Create: `src/Miller.Core/DeadCode/DeadCodeRows.cs` (input row records)
- Test: `tests/Miller.Tests/Core/DeadCodeCandidatesTests.cs`

**Interfaces:**
- Consumes: nothing (pure).
- Produces: `DeadCodeSymbolRow` (SymbolId, Name, Kind, Language, Path, StartLine, StartByte, EndByte, Visibility (string?), IsTest (bool), ParentSymbolId (string?), HasAnnotation (bool), HasStructuralFact (bool), NameMatchesOutside (int), ResolvedInbound (int), PendingResolvedInbound (int), CallsInbound (int), LiteralMatch (bool?) — null = not yet scanned); `LanguageCoverageRow` (Language, IdentifierCount, ResolvedCount); `DeadCodeCandidates.Evaluate(IReadOnlyList<DeadCodeSymbolRow>, IReadOnlyList<LanguageCoverageRow>) -> DeadCodeResult` where `DeadCodeResult` carries `Candidates` (list with `EvidenceLabel`), `Suppressions` (rule_id -> count, all nine ids always present), `Examined` (int). Also `DeadCodeCandidates.IsSyntaxInvokedName(string name, string kind) -> bool` and `DeadCodeCandidates.CandidateKinds` (the set), both public for the reader's SQL prefilter and tests.
- Produces: two-phase contract — `Evaluate` treats `LiteralMatch == true` as the `string_literal_match` suppression; `LiteralMatch == null` rows are returned in `DeadCodeResult.NeedsLiteralScan` so the reader can scan survivors only and call `Evaluate` semantics again via `DeadCodeCandidates.ApplyLiteralScan(result, matchedSymbolIds)`.

**Contract inputs:** Global Constraints (kinds, exclusions, nine rule ids, evidence-label rules, ≥10% coverage threshold). Entry-point names: `Main`, `main`, plus path-tail heuristic `Program.cs` per the design.

**File ownership:** Create: `src/Miller.Core/DeadCode/*.cs`; Test: `tests/Miller.Tests/Core/DeadCodeCandidatesTests.cs`

**Serialization required:** Yes

**Dependency reason:** Produces the row-record and result contracts Tasks 2–3 consume.

**What to build:** The entire candidate/suppression/evidence decision as pure functions over plain records, per design §Candidate rule + §Suppression rules + §Evidence provenance. Suppression precedence: kind/name-shape exclusion removes a symbol from `Examined` candidates entirely (not a suppression count); the nine rules apply in table order, first match wins for the count, and any match suppresses.

**Approach:** TDD. Follow `Miller.Core` conventions (records, no I/O, no Sqlite references). Keep evidence-label logic reading only `LanguageCoverageRow` data. Cover in tests: candidate found; alive-by-name / alive-by-resolved / alive-by-pending / alive-by-calls each prevent candidacy; each of the nine rules fires and counts; finalizer `~Resource`, indexer `this[int index]`, `operator +`, `op_Addition`, `Finalize` excluded; evidence-label split at the 10% boundary (9.9% → `name`, 10% → `name+resolver`); NULL visibility → `visibility_unknown`; two-phase literal-scan contract (`NeedsLiteralScan` then `ApplyLiteralScan` suppresses matched ids under `string_literal_match`).

**Acceptance criteria:**
- [ ] All rule-fidelity behaviors above proven red→green in `DeadCodeCandidatesTests`
- [ ] `Miller.Core` has no new I/O dependency (no Sqlite/file APIs)
- [ ] Worker-scope verification passes; `serial-worker-commit` with recorded SHA

### Task 2: Indexing reader + v4 fixture extension

**Files:**
- Create: `src/Miller.Indexing/DeadCodeCandidateReader.cs`
- Modify: `tests/Miller.Tests/Indexing/JulieDbFixture.cs` (add v4 resolution tables + builder helpers)
- Modify: `tests/Miller.Tests/Indexing/JulieDbFixtureCurrentSchemaTests.cs` (guard the new tables)
- Test: `tests/Miller.Tests/Indexing/DeadCodeCandidateReaderTests.cs`

**Interfaces:**
- Consumes: Task 1's `DeadCodeCandidates` API (rows, `Evaluate`, `ApplyLiteralScan`, `IsSyntaxInvokedName`, `CandidateKinds`).
- Produces: `DeadCodeCandidateReader.Read(string symbolsDbPath, string workspaceRoot) -> DeadCodeCandidateReport` where the report carries `DeadCodeResult` plus `LanguageCoverage`, `LiteralScan` (FilesScanned, FilesSkippedStale), and `Artifact` (ArtifactId, Revision, ReferenceResolutionStatus (fallback `"unknown"`), ReferenceResolutionVersion (nullable)). Task 3 renders exactly this report.

**Contract inputs:** Real v4 DDL verified live this session — `identifier_resolutions` (identifier_id PK REFERENCES identifiers CASCADE, target_symbol_id REFERENCES symbols CASCADE, tier, confidence REAL, method TEXT, outcome TEXT NOT NULL, candidates INTEGER, resolved_at_revision INTEGER NOT NULL, CHECK ((outcome = 'resolved') = (target_symbol_id IS NOT NULL))); `pending_resolutions` (pending_relationship_id PK REFERENCES pending_relationships CASCADE, target_symbol_id TEXT NOT NULL REFERENCES symbols CASCADE, tier INTEGER NOT NULL, confidence REAL NOT NULL, method TEXT NOT NULL, resolved_at_revision INTEGER NOT NULL) with index `idx_pending_resolutions_target`; artifact_metadata keys `reference_resolution_status` / `reference_resolution_version`. Copy DDL into the fixture verbatim (fixture-fidelity rule). Existing span-read pattern: `src/Miller.Indexing/SqliteSourceRegionReader.cs` + `SourceRegionRow.cs`; freshness guard via `files.content_hash` (blake3, normalized) as used by existing source re-readers.

**File ownership:** Create: `src/Miller.Indexing/DeadCodeCandidateReader.cs`; Modify: `tests/Miller.Tests/Indexing/JulieDbFixture.cs`, `tests/Miller.Tests/Indexing/JulieDbFixtureCurrentSchemaTests.cs`; Test: `tests/Miller.Tests/Indexing/DeadCodeCandidateReaderTests.cs`

**Serialization required:** Yes

**Dependency reason:** Consumes Task 1's contracts; produces the reader API Task 3 wires.

**What to build:** The SQL + literal-scan half per design §Architecture. One pass, no full identifiers materialization: per-symbol counts via indexed subqueries/CTEs (`identifiers` name-match outside the symbol's `[start_byte,end_byte]`+file OR `containing_symbol_id`; `identifier_resolutions` inbound from outside; `pending_resolutions JOIN pending_relationships` inbound from outside using the pending row's file/`caller_scope_symbol_id`/span context; `relationships` inbound from outside). Then `Evaluate`, then the literal scan LAST over `NeedsLiteralScan` survivors only: collect `string_literal` rows from `source_regions` grouped by file, re-read each file once, verify `files.content_hash` (skip + count stale files as `FilesSkippedStale`), substring-search surviving candidate names within literal spans, `ApplyLiteralScan`. Missing v4 tables (v3 artifact) must fail with the schema-gate error, not silently return zero — the D5 gate runs before the read, same as `ReferenceExportReader`.

**Approach:** TDD against the extended `JulieDbFixture`. Extend the fixture with the three v4 tables (also `pending_relationships` if absent — copy real columns from a live probe: run `.tools/julie-extract scan` on a temp dir and `.schema pending_relationships`, do not invent) and builder methods (`AddIdentifierResolution`, `AddPendingResolution`, …). Extend `JulieDbFixtureCurrentSchemaTests` to require the new tables/columns/CHECK. Reader tests cover: candidate emitted with all evidence counts zero; saved-by-pending-resolution-only (no `identifier_resolutions` row — doubt-pass finding 2); literal match found in a real temp file under the workspace root suppresses (`string_literal_match`); stale content hash counts as skipped and does NOT suppress; artifact block populated incl. `reference_resolution_status` fallback `unknown` when key absent; coverage rows computed per language.

**Acceptance criteria:**
- [ ] Fixture carries the real v4 resolution DDL incl. the CHECK constraint; schema-guard test extended
- [ ] Reader behaviors above proven red→green in `DeadCodeCandidateReaderTests`
- [ ] Literal scan reads each literal-bearing file at most once and only runs when survivors exist
- [ ] Worker-scope verification passes; `serial-worker-commit` with recorded SHA

### Task 3: CLI surface, capabilities, Scale test

**Files:**
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` (the `case "references":` arm at `src/Miller.Server/Cli/CliDispatch.cs:114` currently hardwires `export`; route ops `export` | `candidates`, add `--json` + `--limit` for candidates, update the help text at the `references <op>` line)
- Modify: `src/Miller.Server/Cli/CliCapabilities.cs` (`optional_features.references_candidates: true`; `json_commands` entry `references candidates --json`; `json_contracts` entry `references-candidates-v1`)
- Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` (extend; existing seams `SetIdentifierTarget`, `MarkSymbolAsTest`)
- Test: `tests/Miller.Tests/Server/Cli/DeadCodeCandidatesScaleTests.cs` (new, `[Trait("Category","Scale")]`, binary via `ScaleTestSupport.RequireJulieServer()`)

**Interfaces:**
- Consumes: Task 2's `DeadCodeCandidateReader.Read(symbolsDbPath, workspaceRoot) -> DeadCodeCandidateReport`.
- Produces: the user-facing contract — compact render and the exact JSON envelope from Global Constraints; `--limit N` (default 50, ordered by path then start_line); workspace selection flags identical to `references export` (`--workspace-id`, `--workspace`).

**Contract inputs:** Global Constraints (header string verbatim, JSON field names verbatim). Compact line shape from design §Output. Follow the existing compact/JSON render conventions in `CliDispatch` (see `metrics`/`report` renders for footer-table precedent).

**File ownership:** Modify: `src/Miller.Server/Cli/CliDispatch.cs`, `src/Miller.Server/Cli/CliCapabilities.cs`; Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`, `tests/Miller.Tests/Server/Cli/DeadCodeCandidatesScaleTests.cs`

**Serialization required:** Yes

**Dependency reason:** Consumes Task 2's reader API.

**What to build:** `miller references candidates [--json] [--limit N] [--workspace-id SELECTOR] [--workspace DIR]`. Compact: header with examined count + verbatim resolver line; candidate lines; footer with suppression counts (all nine ids), literal-scan coverage, per-language coverage table. JSON: the exact envelope. AOT note: add any new serialized types to the existing JSON serializer context (`CliDispatch` JSON output follows the source-generated context pattern — match it, reflection-based serialization breaks the AOT publish).

**Approach:** TDD via `CliDispatchTests` against the extended fixture: compact candidate + suppressed counts + header string; `--json` envelope field-complete (assert every Global-Constraints field name); `--limit` honored; v3-artifact fixture (schema gate) exits 3 with the rebuild message like other artifact commands; capabilities assertions extended (boolean + json_commands + json_contracts). Scale test: scan a small temp fixture workspace containing a deliberately-dead private helper plus a reflection-string case with the REAL binary, run the CLI end-to-end, assert the dead helper appears and the reflection-named symbol is suppressed `string_literal_match`, and per-language coverage renders.

**Acceptance criteria:**
- [ ] Compact and JSON outputs match Global Constraints exactly; `--limit` honored; resolver status line present
- [ ] Capabilities advertises all three surfaces; capabilities test extended
- [ ] Scale test green against the real 2.9.0 binary (`scripts/test.sh scale`, `Skipped: 0` for the new test)
- [ ] Worker-scope verification passes; `serial-worker-commit` with recorded SHA

### Task 4: Contract docs + boundary amendments

**Files:**
- Create: `docs/contracts/references-candidates-v1.md`
- Modify: `docs/contracts/references-export-v1.md` (lines 3–7: replace the "Eros owns candidate ranking, generated/framework suppression …" sentence per the 2026-07-06 consensus)
- Modify: `docs/contracts/cli-eros-v1.md` (add `references candidates --json` to the documented surfaces; fix any Eros-ownership sentence about dead-code)
- Modify: `docs/README.md` (contracts map entry)
- Modify: `CLAUDE.md` (the "Dead-code candidates stay out until extractor reference resolution earns them" sentence in the 1.0-replacement-boundary section → now: shipped as an evidence-gated CLI prototype consuming schema v4 resolution; still absent from report/dashboard/MCP until the evidence gate passes), then `scripts/sync-agents.sh`, then `cmp -s CLAUDE.md AGENTS.md`

**Interfaces:**
- Consumes: Task 3's shipped shapes — copy JSON field names, capabilities keys, and the resolver header string verbatim from the code/tests, not from memory.
- Produces: `references-candidates-v1` contract doc (status: experimental/evidence-gated; full envelope; the nine suppression rule-id semantics; evidence-label semantics incl. the explicit "provenance, not certainty" statement and the partial-resolver caveat).

**Contract inputs:** Design §Output + §Evidence provenance; existing contract-doc format (`docs/contracts/references-export-v1.md` as the template).

**File ownership:** Create: `docs/contracts/references-candidates-v1.md`; Modify: `docs/contracts/references-export-v1.md`, `docs/contracts/cli-eros-v1.md`, `docs/README.md`, `CLAUDE.md`, `AGENTS.md` (generated)

**Serialization required:** Yes

**Dependency reason:** Documents Task 3's shipped shapes verbatim (JSON field names, capabilities keys).

**What to build:** The contract + boundary truth-up per doubt-pass finding 4. Keep amendments surgical: ranking-beyond-the-deterministic-rule, suppression *persistence*, history, and fleet workflows remain out of Miller.

**Acceptance criteria:**
- [ ] New contract doc complete (no TBDs), listed in `docs/README.md`
- [ ] Stale Eros-ownership sentences amended in both existing contracts
- [ ] `CLAUDE.md` boundary sentence updated; `cmp -s CLAUDE.md AGENTS.md` clean
- [ ] Worker-scope verification passes (fast suite — `AgentInstructionsTests`/doc gates); `serial-worker-commit` with recorded SHA

### Task 5: Dogfood evidence (the design's evidence gate)

**Files:**
- Create: `docs/findings/2026-07-07-dead-code-candidates-dogfood.md`

**Interfaces:**
- Consumes: the built Release CLI (`src/Miller.Server/bin/Release/net10.0/miller`) and v4 artifacts produced by `.tools/julie-extract` 2.9.0.
- Produces: the findings doc gating any future surfacing (report/dashboard/README/agent guidance).

**Contract inputs:** Design §Evidence gate. v4 scratch artifacts: build fresh ones with `.tools/julie-extract scan --root <repo> --db <scratch>/x.db` for `/Users/murphy/source/miller` and `/Users/murphy/source/julie-extractors` (do NOT rebuild the live `.miller/symbols.db` artifacts — the user-scope installed Miller still expects schema 3; run the CLI against the scratch DBs via `--workspace`-style selection or the reader's direct db path as `CliDispatchTests` does with `Context(...)` — if the CLI cannot target a scratch artifact cleanly, run `references candidates` through a temporary registered workspace copy and say so in the findings doc).

**File ownership:** Create: `docs/findings/2026-07-07-dead-code-candidates-dogfood.md`

**Serialization required:** Yes

**Dependency reason:** Runs the Task 3 binary against real artifacts; is the design's evidence gate.

**What to build:** Run candidates against both repos. Record: examined/candidate/suppression counts, per-language coverage, literal-scan coverage, full candidate lists. **Hand-verify every Miller-repo candidate** (read each symbol and its references; classify true-dead / false-positive / uncertain with file:line notes). Hard gate: zero confirmed-live symbols in the list. If any false positive is found: record it, tighten per design intent (report a plan mismatch if the fix needs a rule change beyond the design), re-run.

**Acceptance criteria:**
- [ ] Findings doc records both repos' runs with the full evidence and the hand-verification table
- [ ] Hard gate met: no confirmed-live symbol among Miller-repo candidates (or the mismatch is reported and resolved)
- [ ] Worker-scope verification passes; `serial-worker-commit` with recorded SHA
