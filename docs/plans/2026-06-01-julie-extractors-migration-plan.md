# julie-extractors Migration — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Move Miller's julie-facing seam off the pinned `julie-server` 7.13.2 (schema 28 / contract 3) onto the new `julie-extract` v1 CLI + SQLite artifact (schema 1 / contract 1), at parity.

**Architecture:** Miller stays a read-only consumer. This reshapes one seam: subprocess invocation (julie-extract top-level subcommands), the nested JSON report, the v1 SQLite read layer (renames + by-name reads), freshness (`extraction_revisions`), bootstrap/workspace identity, and the test fixtures that synthesize the artifact. File content moves to on-disk re-sourcing. No new subsystems.

**Tech Stack:** .NET 10, C#, Microsoft.Data.Sqlite, xUnit; Rust `julie-extract` (product `v2.0.0`, shipping SQLite **schema/contract v1** — product version and schema version are orthogonal) consumed as a spawned binary, not built here. Two distinct hash algorithms: **BLAKE3** for source-file `content_hash` (freshness) and **SHA-256** for capability fingerprints + release-asset integrity — never interchanged (reconciliation #9).

**Architecture Quality:** Approved shape — `Miller.Core` stays zero-I/O; `Miller.Indexing` owns the SQLite read + row→model mapping; the seam records keep their public accessor NAMES (`Revision`/`SymbolsExtracted`/`Status`/`Errors`/`Code`) so report-consumers keep compiling while their *semantics* are remapped. Main architecture risk: the read-layer (B/C) and test-fixture (H) rewrites must land in lockstep — the fixtures are a second implementation of the julie schema. No new module boundaries.

---

## Subsystem letter remap (read first — avoids false diffs vs the design)

The design doc §10 labels subsystems where **§10E = search-widen** and **§10D = bootstrap/services**. This PLAN relabels: **A** = invocation/report, **B** = symbol reader, **C** = remaining readers + disk content, **D** = freshness, **E** = bootstrap/services, **F** = test-signal consumers, **G** = packaging/docs, **H** = test fixtures. The design's search-widen (D3a) is **OUT of this plan** (pure-parity default, design §16; see Scope). Judge coverage by work-unit, not by letter.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` — the "Testing" and "Build" sections.
**Worker red/green scope:** `scripts/test.sh` — the fast suite (`Category!=Scale`, <30s wall-clock tripwire). The lowest-cost proof for every logic/contract task here.
**Worker ceiling:** workers run `scripts/test.sh` (fast) and, only when their task touches the extract/indexing path against a real binary, the relevant `scripts/test.sh scale`. Workers do not own broader regression than their assigned gate.
**Worker gate invariant:** each task's Acceptance lists the behavior its fast test proves (nested report parses; `path:line` exact; `change_kind` vocabulary; `content_hash` prefix normalized; `is_test` from column; etc.).
**Lead affected-change scope:** `scripts/test.sh` after each coherent subsystem batch.
**Branch gate:** `scripts/test.sh all` (fast + scale; needs `.tools/julie-extract`) **and** `dotnet build Miller.slnx -c Release` (0 warnings / 0 errors) before handoff/PR.
**Escalation triggers:** any task touching the extract subprocess, schema gate, freshness, or fixtures → run the Scale suite before declaring the batch green (a live `julie-extract` is required there).
**Assigned verification failure:** workers stop and report. Do NOT weaken the Scale-trait guard (`ScaleTraitConventionTests`) — add the trait, never remove the launch signal.
**Verification ledger:** record invariant, command, scope label, commit SHA, result, timestamp per batch; reuse a passing entry at the same HEAD instead of rerunning an expensive gate.

## Model Routing

**Project source of truth:** no `RAZORBACK.md` present → this plan sets a default policy. **Override at approval if you prefer.** Claude Code Agent `model` param accepts `opus`/`sonnet`/`haiku`.
**Strategy tier** (lead review, decomposition, finding triage): `opus`.
**Implementation tier** (bounded worker tasks from this plan): `sonnet`.
**Mechanical tier** (docs, fixture DDL, rote renames, manifests): `haiku` — EXCEPT any task owning a failing test or a freshness/contract invariant, which is implementation tier.
**Gate-interpretation reviewer / Escalation tier** (subtle correctness: report reshape, freshness rewrite, disk-slice invariant, bootstrap identity, the B1+F1+F4 atomic window): `opus`.
**Worker eligibility:** an implementation-tier worker may take any single task whose cross-deps are satisfied.
**Escalation triggers:** the atomic B1+F1+F4 commit, the C3 disk-slice freshness invariant, and the D freshness rewrite go to escalation tier.
**Mechanical exclusion:** mechanical workers cannot own failing tests or the contract/freshness gates.
**Unsupported harness behavior:** if the harness cannot select per-agent models, use `inherit` and note it.

## Cross-subsystem reconciliations (AUTHORITATIVE — override individual task wording on conflict)

From the plan critique (verdict: needs-fixes). Where a subsystem task below contradicts one of these, the reconciliation wins:

1. **One hash helper, one name:** `ContentHasher.NormalizeHash(string)` (subsystem D1). Subsystem C3's body references `ContentHashNormalizer.StripPrefix` — that symbol does not exist; C3 must call `ContentHasher.NormalizeHash`. This helper is **blake3-only** (see #9).
2. **`FreshnessReader.LatestRevision`/`ChangedSince` drop `workspaceId`:** v1 `extraction_revisions` has no workspace_id, so D3/D4's signatures become `LatestRevision()` / `ChangedSince(long sinceRevision)` (no workspace param). This is build-red until **every** call site updates in the SAME D3/D4+E batch. The COMPLETE call-site set (verified by grep, 2026-06-01) — E2/E4's "comment-only / keep the param" wording is **void**:
   - `src/Miller.Indexing/FreshnessReader.cs:68,90` (the definitions — D3/D4)
   - `src/Miller.Server/Hosting/IndexBootstrapService.cs:352` inside `ReadLatestRevisionOrZero` (E2) — the outer `workspaceId` param may stay only as a null-sentinel guard, but the inner call becomes `reader.LatestRevision()`
   - `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs:230` AND the delegate type `Func<string,string,long> _readLatestRevision` (:15,:38) → `Func<string,long>`, plus call sites :129,:212 (E4)
   - `src/Miller.Server/Hosting/FreshnessService.cs:230` in `PollThenSwap` + its XML-doc cref :13 — **not currently owned by any E task; fold into E2** (was orphaned, codex F8 / workflow C7)
   - Scale tests (compiled every phase — see reconciliation #17): `LiveFreshnessTests.cs:65,88,97,105,119`, `LiveEditTests.cs:90,133`, `MultiProcessWalTests.cs:47,66,70,75,108` (the `:70` hit is `ChangedSince`; the rest are `LatestRevision`). Fast test: `IndexBootstrapServiceTests.cs` (`ReadLatestRevisionOrZero` cases). D3/D4 drop the `workspaceId` arg at all of these in the Phase-4 commit.
3. **Disk-root fixture seam (C3 ↔ H3):** subsystem H must expose `WorkspaceRoot` on the fixture AND write each file's bytes to disk under that root (not just a `FileContents` dictionary). C3's `inspect(full)`/edit disk-slice tests consume `fx.WorkspaceRoot` + the on-disk bytes. Pin this exact surface in H3 before C3 implements.
4. **A2 diagnostic fixture is honest:** `data_loss_guard` is `recoverable: false` in real julie (`commands.rs:1099-1116`). A2's `Parse_Failed_NullArtifact` fixture must assert `recoverable == false` (or use a genuinely-recoverable code) — never ship a fixture that contradicts the contract.
5. **A6 timeout test is deterministic:** make the deterministic variant (large fixture, or a "completed-or-timed-out, never hung" assertion) the PRIMARY test. A "1ms timeout always trips before the child exits" assumption is flaky.
6. **Atomic build-red window (CORRECTED — the original `B1+F1+F4+H1` does NOT compile):** deleting the `TestRole` type touches SEVEN src files + their tests. The minimal set that compiles AND greens together is **`B1 + B2 + B4 + F1 + F2 + F3 + F4 + F5 + H1` (+ `H4` for the typed `is_test` column writer)** — i.e. the *entire* TestRole→IsTest graph in one commit. Verified `TestRole` references (grep, 2026-06-01): `src/Miller.Core/Contracts/TestRole.cs` (the type, F1 deletes), `src/Miller.Core/Contracts/SymbolDetail.cs:37`, `src/Miller.Core/Resolver/RouteBridge.cs:33`, `src/Miller.Core/Graph/BridgeGraphBuilder.cs:411`, `src/Miller.Indexing/IndexedSymbol.cs:25` (B1), `src/Miller.Indexing/SqliteSymbolReader.cs:63,77,111,117` (B2), `src/Miller.Indexing/RepositoryIndexLoader.cs:124` (F4) — plus test files (`TestRoleTests.cs`, `BridgeGraphBuilderTests.cs`, `SqliteSymbolReaderTests.cs`, `RouteBridgeTests.cs:203`, `SymbolResolverTests.cs`, `DtoEntityBridgeTests.cs`, `EntityTableBridgeTests.cs`, `FieldSetExtractorTests.cs`). **Acceptance gate:** `rg -n "TestRole" src tests` returns empty after the commit. The Phase-3 sequencing is corrected to fold these into the first commit, not list `B2` as a later step.
7. **Two unowned report-fixture test files** migrate to the nested ctor **in Phase 2, in the SAME commit as A2's `ExtractReport` rewrite** (assigned to subsystem **A**, which owns the report record): `tests/Miller.Tests/Server/IndexerServiceScanTests.cs:57` and `tests/Miller.Tests/Server/JulieExtractOpsTests.cs:23` (both construct the flat `ExtractReport`). They MUST migrate when A2 removes the flat ctor — deferring them is a Phase-2 compile break — so they belong in A2's red→green arc, NOT Phase 4.
8. **Metadata keys retained:** `created_at` AND `updated_at` are artifact_metadata KEYS in v1 (not dropped columns). Miller doesn't read them, but H's synthetic fixture should write both keys for fidelity.
9. **Three hash domains — never conflate (verified against julie-extractors README §Artifact Contract + `docs/contracts/{sqlite-schema-v1,reports}.md`):** v1 uses hashing in **three** distinct, non-interchangeable ways:
   - **(a) Source-file freshness** — `files.content_hash` is **`blake3:<hex>`**, and `artifact_metadata.hash_algorithm == "blake3"` (gated by `ExpectedHashAlgorithm`). This is the ONLY domain `ContentHasher.NormalizeHash` and `FreshnessGate`/`StalenessCheck` touch. `NormalizeHash` strips a `blake3:` scheme token ONLY; it must NOT be reused to strip a `sha256:` prefix, and the freshness path must never accept a non-blake3 content hash (the schema gate already rejects a non-`blake3` `hash_algorithm`).
   - **(b) Parser/capability fingerprints** — `parser_inventory_fingerprint` / `capability_snapshot_fingerprint` are **`sha256:<hex>`**-prefixed. `ExtractReport` (A2) deserializes them as opaque strings and Miller does NOT compare them; if any future task ever does, it needs a SEPARATE sha256-aware normalizer, never `NormalizeHash`.
   - **(c) Release-asset integrity** — `julie-pins.json` `sha256` digests (G2) verify the downloaded ARCHIVE bytes. Plain SHA-256 hex (no scheme prefix), validated by `IsSha256Hex` in `MillerExtractContractTests` (A1) and the restore script's `verify_sha`. Wholly unrelated to (a)/(b); shares nothing but the algorithm name.
   The plan keeps these in separate code paths today; the risk is a future refactor "unifying" them. Do not. A blake3 file hash fed through a sha256 check (or vice versa) silently reads every file as stale or every download as corrupt.

### Round-2 corrections (from the codex + multi-agent review, all verified against source — AUTHORITATIVE)

10. **`partial` status is NOT a hard failure.** v1 `scan` returns exit 1 with `status:"partial"` AND a fully consistent artifact (`.with_artifact(...)` + `rows_written` + `revision`) when some files fail to parse (`commands.rs:217-251`; README "Reports And Exit Status"). A5's `Interpret()` must special-case `status=="partial"`: **parse and RETURN the report** (let bootstrap load the consistent artifact), logging `counts.files_failed` + `errors[]` as a WARNING. Only `status=="failed"` on exit 1 throws `JulieExtractFailedException`. Aborting the whole index build because one file failed is wrong. (Behavior addition vs a naive port — flagged for Alan.)
11. **`code_context` is DROPPED from the v1 `symbols` table** (it now lives only on `identifiers`: `schema.rs:128` is inside `CREATE TABLE identifiers`, lines 112-131; `symbols` is 66-100). Miller's `ExtractReader.ReadDetail` (`ExtractReader.cs:32`) selects `code_context FROM symbols` and projects `SymbolDetail.CodeContext` (`:44`, `SymbolDetail.cs:11`). Task C2 MUST drop `code_context` from that SELECT and remove the now-dead `Indexing.SymbolDetail.CodeContext` field (only consumers are the assignment + two test asserts in `ExtractReaderTests.cs:26,43` and the fixture column `JulieDbFixture.cs:75,260,446` — H drops the fixture column). Otherwise: runtime `no such column: code_context`, breaking inspect-detail + EditService.
12. **`report_schema_version` must actually be gated.** A1 adds `ExpectedReportSchemaVersion=1` but A3's `VerifyReport` only checks artifact fields — the constant is dead. Add `report.ReportSchemaVersion == MillerExtractContract.ExpectedReportSchemaVersion` to `VerifyReport` (throw `IncompatibleExtractException` naming the value), with tests for missing/null and `2`. `REPORT_SCHEMA_VERSION=1` is real (`reports.rs:5,50`). Keep `tool.binary_version` OUT of the gate (D7).
13. **`SymbolsExtracted` = `counts.rows_written.symbols` (per-operation), NOT `totals.symbols`.** A2's record is correct; E1 (line ~2348) and E3 (line ~2503) wrongly say "A maps to `counts.totals.symbols`". Fix the E1/E3 prose to `rows_written.symbols` (matches current Miller "symbols extracted this op" semantics — `WorkspaceRender.cs:78`, `IndexBootstrapService.cs:135-137`). `SymbolsTotal` ⇒ `totals.symbols` stays for whole-artifact size.
14. **`ExtractReader.ReadRootPath` is C-owned, not B.** E1 replaces `ReadWorkspaceId` with `ReadRootPath(dbPath)` (`SELECT value FROM artifact_metadata WHERE key='root_path'`) and twice calls it "B-provided". `ExtractReader.cs` is subsystem **C** (Task C2). Add `ReadRootPath` to C2's scope (with a test); fix E1's "B-provided" → "C-provided (C2)"; C2 and E1 now land **together** in Phase 3's `ReadWorkspaceId`-removal atomic commit (reconciliation #17), so the `ReadRootPath` provider and its bootstrap consumer are in one commit.
15. **Fixture revision-row shape: DROP `WorkspaceId` — atomically, in the ONE Phase-4 freshness commit.** v1 `extraction_revisions` has no workspace_id (verified schema.rs:28-50), so the END-STATE `JulieDbFixture.RevisionRow`/`RevisionFileChangeRow` drop their `WorkspaceId` field (and `RevisionFileChangeRow` also drops the v1-less `OldHash`/`NewHash` and renames `FilePath`→`Path`). **Canonical end shapes:** `RevisionRow(long Revision, string Kind = "full")` and `RevisionFileChangeRow(long Revision, string Path, string ChangeKind)`. The NOT-NULL `revision_file_changes.file_id` (TEXT, PK component, **no FK** — schema.rs:48) is **derived**, not a record field: the fixture synthesizes it via the shared `FileId(path)` helper from H1 (`"file:" + path`). Do NOT add an explicit `file_id` ctor arg (it would be call-site noise and is the bug in any 4-arg `Fc(rev, "file-x", path, kind)` snippet — those must be 3-arg `Fc(rev, path, kind)`). `Kind` default is `"full"` everywhere (aligns the fixture default with a scan-produced revision; supersedes the old `"incremental"` default).

   **Single-step sequencing (corrected — workflow#1, verified 2026-06-01).** The earlier two-step (migrate revision DDL in Phase 3, drop the record fields in Phase 4) is **VOID**: it would flip the fixture's `canonical_revisions`→`extraction_revisions` in Phase 3 while the OLD `FreshnessReader` (and the FAST-suite `FreshnessReaderTests` / `FreshnessServicePollNowTests`) still query `canonical_revisions` until Phase 4 — making the Phase-3 fast suite RUNTIME-red (`no such table: canonical_revisions`). The fixture is monolithic, so a table it owns cannot flip a phase before that table's reader. Instead, **the entire revision/freshness flip is ONE Phase-4 commit**: the fixture's revision tables (**H2**, MOVED to Phase 4), the record-field drop (`WorkspaceId`, plus `OldHash`/`NewHash`, `FilePath`→`Path`, file_id-derivation), the reader (**D3/D4**, dropping the `workspaceId` param), and **every** consumer below land together. Phase 3 leaves the revision tables AND records fully OLD (`canonical_revisions` with `workspace_id`; `RevisionRow(rev, ws, kind)`), so the OLD reader + its tests stay green. No carried-but-not-written is needed — the records simply do not change until Phase 4. (Consequence: H1's `files.last_revision_id` is an FK-FREE plain column in Phase 3 — it cannot FK to an `extraction_revisions` table that does not exist yet; see H1.)

   **Authoritative call-site inventory (the Phase-4 commit must update ALL of these together; the compiler enforces it, this list assigns ownership so nothing is orphaned):**
   - `tests/Miller.Tests/Indexing/FreshnessReaderTests.cs` — uses BOTH a `Rev` and an `Fc` alias (`using Rev = …RevisionRow; using Fc = …RevisionFileChangeRow;` at lines 4-5 — these aliases DO exist; do not claim otherwise). `Rev` sites: lines 29, 40, 51, 72, 94, 105, 129. `Fc` sites: lines 75-78, 95, 108-110. Owned by **D3** (Rev / LatestRevision) + **D4** (Fc / ChangedSince). The workspace-leak tests (`Rev(9, Other)` :40, `Fc(3, Other, "leak.cs", …)` :78) are REWRITTEN, not arg-dropped — their per-workspace-scoping premise is gone in v1 (one DB = one root); D3/D4 replace them with separate-DB-no-leak coverage.
   - `tests/Miller.Tests/Server/IndexBootstrapServiceTests.cs` — `RevisionRow(3/7/5, "ws-…")` at :167-169 (owned by **E2**, lines 140-172; the `RevisionRow(5,"ws-other")` "must not leak in" premise is rewritten — v1 cannot hold two roots in one DB) AND `RevisionRow(2,"ws-1")` at :301 (the non-writable-dir test — **previously unowned; fold into E2**).
   - `tests/Miller.Tests/Server/WorkspaceToolTests.cs` — `RevisionRow(revision, workspaceId)` in the `CreateSynth` helper at :157 (**fold into E3**; E3 already rebuilds this file's `Report`/`RecordingScanOps` helpers).
   - `tests/Miller.Tests/Server/FreshnessServicePollNowTests.cs` — `RevisionRow(N, Ws)` at :51, :67, :86, :106 (**previously UNOWNED — fold into E2**'s freshness-caller batch).
   - `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs` — `RevisionRow(revision, workspaceId, "fresh")` at :500 → `RevisionRow(revision, "fresh")` (**previously UNOWNED — fold into E2**).
   - `tests/Miller.Tests/Indexing/JulieDbFixtureV1SchemaTests.cs` — **H2's OWN revision lock tests live in Phase 4** (H2 moved there) and construct the records in their final no-ws shape directly: `RevisionRow(1)` / `RevisionFileChangeRow(1, "a.cs", "inserted")`. There is NO Phase-3 write-with-ws/Phase-4-drop churn (that was the void two-step). H3's content lock tests (Phase 5) construct no revision rows.
   - Note: `JulieDbFixture.Create(…, workspaceId:)` is a SEPARATE, surviving parameter — it feeds `artifact_metadata.artifact_id`/`root_path` identity (H1) and stays (optional, defaulted). Only the revision-row *record field* is dropped; tests that pass `Create(workspaceId: …)` keep doing so.

   **Acceptance gate:** after the Phase-4 commit, `rg -n "RevisionRow\(|RevisionFileChangeRow\(|new Rev\(|new Fc\(" src tests` shows zero constructions passing a workspace-id argument, and the build is 0 warnings (the field is gone, so any stray ws arg is a compile error, not a silent pass).
16. **Single-owner + letter hygiene:** (a) `FreshnessGateTests.SetFileHash`/`SetHashAlgorithm` are owned by **D5** (the file is in D5's Files block); H3 only guarantees the fixture writes the `blake3:`-prefixed `content_hash` — H3's line referencing this helper becomes a cross-dep note, not an edit. (b) Freshness (`FreshnessReader`/`ChangedSince`/`ContentHasher`/`FreshnessGate`) is **subsystem D** in THIS plan; scrub the stray "subsystem C" references to freshness in E2/E4/H2/H3 (they're the design-doc letter). (c) `H3` is scheduled into **Phase 5** (it was orphaned — see corrected sequencing). (d) FreshnessGate v1 design: there is no independent stored snapshot (`files.content` is gone), so the gate relies on the `content_hash` compare ALONE and passes `indexedText: null` to `IndexedSnapshot` (the exact-text tiebreaker is honestly skipped — `StalenessCheck.cs:83` only tiebreaks when text is present on BOTH sides, so null is safe, NOT auto-Stale). **D5 (Phase 4) deletes `FreshnessGate.cs:70`'s `ReadIndexedFileText` call**, leaving the method with no `src/` caller; **C3 (Phase 5) then DELETES `ReadIndexedFileText` itself + its four `ExtractReaderEditTests.cs` test methods** (it is not migrated to a 3-arg disk reader — that would be dead code). The four methods are at `:142-150` (`_ReturnsTheIndexedFileContentVerbatim`), `:152-165` (`_Utf8FileRoundTripsThroughTheAccentByte`), `:167-172` (`_UnknownPath_ReturnsNull`), and `:192-198` (`_MissingDbFile_ThrowsFileNotFound`) — **the fourth sits AFTER and interleaved with `ReadEditSpan_MissingDbFile` (`:176-181`) and `ReadIdentifierSites_MissingDbFile` (`:184-190`), so delete by method name, NOT as a `:142-198` block**; also remove the `// ---- ReadIndexedFileText ----` section comment (`:140-141`). Keep every `ReadEditSpan_*`/`ReadIdentifierSites_*`/`ReadFileHash*`/`ReadHashAlgorithm*`/`Blake3*` test. The method survives intact through Phase 4 (reads the still-present `files.content`, exercised by its tests) and is removed in the same Phase-5 commit that drops the `files.content` column (H3). Acceptance: `grep -rn "ReadIndexedFileText" src tests` empty after Phase 5. (Resolves the C3↔D5 2-arg/3-arg break, the degenerate-tiebreaker, and the dead-code question in one stroke.)
17. **Scale/live tests compile in EVERY phase — symbol deletions & signature changes must update them in the SAME commit.** `Miller.Tests` is ONE assembly; `dotnet test` filters `Category!=Scale` at RUN time, but the Scale/live tests (`tests/Miller.Tests/Server/Live*.cs`, `MultiProcessWalTests.cs`) are still COMPILED. So deleting a method or changing a signature they reference breaks the test-project build, which blocks even the fast suite (codex#2/#3 + workflow, verified 2026-06-01). Two rules:
   - **(compile — per phase)** Every task that deletes a symbol or changes a signature MUST rewrite its Scale-test call sites in the same commit. Inventory + ownership:
     - `ExtractReader.ReadWorkspaceId` — **DELETED in C2 (Phase 3)**. It has BOTH `src` and scale-test callers; deleting it while ANY remain is build-red, so the deletion + every caller fix land in ONE Phase-3 commit (this forces **E1 into Phase 3** — see below):
       - **src (owned by E1):** `IndexBootstrapService.cs:117,150` — E1 replaces them with `ExtractReader.ReadRootPath(canonicalDbPath)` (the v1 identity signal — `artifact_metadata.root_path`) as part of its root_path-identity rework. (ReadWorkspaceId also runtime-fails against the v1 fixture's `artifact_metadata` — its `external_extract_metadata` table is gone — so this can't be deferred.)
       - **scale tests (owned by C2):** `LiveWorkspaceTests.cs:71`, `LiveFreshnessTests.cs:61`, `LiveSearchInspectTests.cs:71`, `LiveEditTests.cs:85`, `MultiProcessWalTests.cs:42` — rewrite each to `WorkspaceId.FromCanonicalRoot(<canonical root>)` (the SHA-256-of-root stable id Miller already uses, `WorkspaceId.cs:10`; NOT a DB read). `canonicalRoot` is in scope at the first four; `LiveSearchInspectTests` derives it from `repo` (`:63`) via `PathCanonicalizer.CanonicalizeRoot(repo)`. Compile-only swap: the id is still fed to the OLD `LatestRevision(workspaceId)`, which works against OLD `canonical_revisions` through Phase 3. If a site used `workspaceId` only for a now-removed path, drop the local instead.
     - `FreshnessReader.LatestRevision` / `ChangedSince` — **param DROPPED in D3/D4 (Phase 4)**. Callers: `LiveFreshnessTests.cs:65,88,97,105,119`, `LiveEditTests.cs:90,133`, `MultiProcessWalTests.cs:47,66,70,75,108`. **D3/D4 drop the `workspaceId` argument at each.** The three files that also appear above are touched TWICE — Phase 3 swaps the id source, Phase 4 drops the arg — both necessary.
   - **(runtime — once)** Scale tests spawn the REAL `julie-extract` (→ a v1 DB), so mid-migration an OLD reader against a v1 DB would fail at RUNTIME *if the scale suite were run*. It is not: per-phase gating is the FAST suite (`scripts/test.sh`); scale runtime correctness (`scripts/test.sh scale`) is a single gate at the **Phase-4 exit** (and again before PR), once the whole read+freshness stack is v1. A mid-Phase-3 scale-suite failure is expected, NOT a regression — only compilation is required there.

---


## Subsystem task drafts (A–H)

_Document order; see the sequencing section at the end for execution order. Each draft was authored against real source; reconciliations above override on conflict._


---

## Subsystem A: Subprocess invocation & report parsing

All paths under `/Users/murphy/source/miller/`. This subsystem owns the four julie-facing seam files in `src/Miller.Indexing/` plus their pinning tests in `tests/Miller.Tests/Indexing/`. It is **non-mechanical** (the report reshape, the version-mismatch rewrite, and the exit-3 mapping each get full TDD choreography). Verify the fast suite with `scripts/test.sh` (Category!=Scale, <30s) and build with `dotnet build Miller.slnx -c Release` (0 warnings) after every implementation step.

**Order is load-bearing:** A1 (constants) → A2 (report record) → A3 (version gate, depends on A2's `Artifact` shape) → A4 (argv) → A5 (Interpret case 3) → A6 (spawn hardening). A2/A3 break the build until both land, so do them as one red→green arc.

### Verified contract facts (from julie-extractors source, cited)

- `crates/julie-extract-cli/src/args.rs:16-100` — six top-level subcommands `Scan/Update/Delete/Info/Export/Languages`; **no `extract` parent, no `--workspace-id`**; every artifact command (`Scan/Update/Delete/Info/Export`) has `#[arg(long)] pub strict_schema: bool`; `Languages` does **not**.
- `crates/julie-extract-artifact/src/reports.rs:5` — `REPORT_SCHEMA_VERSION: i64 = 1`; `:28-66` the nested `Report` shape; `:43-65` the custom `Serialize` emits `report_schema_version` first then `status, operation, mode, input, artifact, tool, revision, counts, errors, warnings, [languages]`.
- `crates/julie-extract-artifact/src/reports.rs:122-134` — `ArtifactReport{ db_path, root_path, artifact_id, schema_version, extract_contract_version, sqlite_schema_version, jsonl_schema_version: Option, hash_algorithm, parser_inventory_fingerprint, capability_snapshot_fingerprint }`; `artifact` is `Option` on the `Report` (`:34`) → **null artifact = gate fail**.
- `crates/julie-extract-artifact/src/reports.rs:142-146` — `ReportRevision{ latest_revision_id: Option<i64>, created_revision_id: Option<i64> }`; the whole block is `Option`.
- `crates/julie-extract-artifact/src/reports.rs:299-307` — `ReportDiagnostic{ code: ReportCode, message: String, path: Option, root_relative_path: Option, recoverable: bool, details: Value }`; `:250-274` `ReportCode` is `#[serde(rename_all = "snake_case")]` (so `code` is a snake_case string on the wire).
- `crates/julie-extract-artifact/src/schema.rs:3-4` — `SQLITE_SCHEMA_VERSION: i64 = 1`, `EXTRACT_CONTRACT_VERSION: i64 = 1`.
- `crates/julie-extract-cli/tests/cli_contract.rs:131-170` — exit-code contract: `0` ok, `1` operation-failure (e.g. `unsupported_format`), `2` usage (`analyze`), `3` `schema_incompatible` (with `--strict-schema`).
- `crates/julie-extract-cli/src/commands.rs:1283-1339` — exit `3` also covers `SchemaMigrationRequired`, `ContractIncompatible`; `:1240-1279` `RootMismatch` is exit `3`. Path errors (`FileOutsideRoot`/`InvalidPath`/`FileNotFound`) stay exit `1`.

---

### Task A1 — Re-pin `MillerExtractContract` to v1

**Files:** `src/Miller.Indexing/MillerExtractContract.cs` (rewrite), `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs` (rewrite).

**What:** Flip the pinned versions 28→1 / 3→1, add the two new expected report/sqlite versions, rename `PinnedJulieServerVersion`→`PinnedJulieExtractVersion`, keep `ExpectedHashAlgorithm="blake3"`. Keep the names `ExpectedSchemaVersion` and `ExpectedExtractContractVersion` (only their values change) so `JulieDbFixture.PinnedSchema/PinnedContract/SchemaText` (subsystem H) and `ExtractVersionMismatch` keep compiling.

**This is the smallest task — do it first so A2/A3 reference the final constants.** It is partly mechanical (value flips) but the rename + two new constants warrant a locked test.

**Symbol mapping (current `MillerExtractContract.cs:13-16`):**

| Old | New |
|---|---|
| `ExpectedSchemaVersion = 28` | `ExpectedSchemaVersion = 1` (NAME KEPT — this is `sqlite_schema_version`/`schema_version`, both 1 in v1) |
| `ExpectedExtractContractVersion = 3` | `ExpectedExtractContractVersion = 1` (NAME KEPT) |
| (none) | `ExpectedSqliteSchemaVersion = 1` (NEW; alias of `ExpectedSchemaVersion`, named to match `report.artifact.sqlite_schema_version`) |
| (none) | `ExpectedReportSchemaVersion = 1` (NEW; gates `report.report_schema_version`) |
| `PinnedJulieServerVersion = "7.13.2"` | `PinnedJulieExtractVersion = "2.0.0"` (RENAME; PRODUCT version / download pin, orthogonal to the schema-contract runtime gate — D7) |
| `ExpectedHashAlgorithm = "blake3"` | unchanged |

**Steps (TDD):**

1. **Red — rewrite the test.** Replace `MillerExtractContractTests.cs` so it pins the v1 values, the new constants, and the renamed property. Real code:

```csharp
using System.Text.Json;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class MillerExtractContractTests
{
    [Fact]
    public void ContractPinsJulieExtractV1Versions()
    {
        Assert.Equal(1, MillerExtractContract.ExpectedSchemaVersion);
        Assert.Equal(1, MillerExtractContract.ExpectedSqliteSchemaVersion);
        Assert.Equal(1, MillerExtractContract.ExpectedExtractContractVersion);
        Assert.Equal(1, MillerExtractContract.ExpectedReportSchemaVersion);
        Assert.Equal("blake3", MillerExtractContract.ExpectedHashAlgorithm);
        Assert.False(string.IsNullOrWhiteSpace(MillerExtractContract.PinnedJulieExtractVersion));
    }

    [Fact]
    public void JuliePinsJsonMatchesContractVersion()
    {
        string pinsPath = Path.Combine(ScaleTestSupport.RepoRoot(), "scripts", "julie-pins.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(pinsPath));

        string pinnedVersion = MillerExtractContract.PinnedJulieExtractVersion;
        Assert.Equal(pinnedVersion, doc.RootElement.GetProperty("version").GetString());

        foreach (JsonProperty asset in doc.RootElement.GetProperty("assets").EnumerateObject())
        {
            string? name = asset.Value.GetProperty("name").GetString();
            string? sha256 = asset.Value.GetProperty("sha256").GetString();
            // The pins 'name' carries a literal {VER} placeholder; substitute before asserting (reconciliation #4).
            string? resolvedName = name?.Replace("{VER}", pinnedVersion);
            Assert.Contains($"v{pinnedVersion}", resolvedName, StringComparison.Ordinal); // published assets carry the leading 'v'
            Assert.True(IsSha256Hex(sha256), $"missing or invalid sha256 pin for {asset.Name}");
        }
    }

    [Theory]
    [InlineData("restore-julie-extract.sh")]
    [InlineData("restore-julie-extract.ps1")]
    public void RestoreScriptsSupportLocalSourceBuildUntilReleaseAssetsPublish(string scriptName)
    {
        string script = File.ReadAllText(Path.Combine(ScaleTestSupport.RepoRoot(), "scripts", scriptName));
        Assert.Contains("MILLER_JULIE_SOURCE", script, StringComparison.Ordinal);
        Assert.Contains("from-source", script, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value is not { Length: 64 }) return false;
        foreach (char c in value)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}
```

   **CROSS-DEP FLAG (sequencing — corrected per reconciliation #4/#16c):** `JuliePinsJsonMatchesContractVersion` and `RestoreScriptsSupportLocalSourceBuildUntilReleaseAssetsPublish` read `scripts/julie-pins.json` and `scripts/restore-julie-extract.{sh,ps1}`. To avoid a fast-suite red window from A1 (Phase 2) until G2 (Phase 7), the **`julie-pins.json` DATA repoint** (version `2.0.0` + slug `anortham/julie-extractors` + the four real sha256 + `{VER}`-templated names) and the **script rename** move into **G1 (Phase 1)** — they are a pure data file + a `git mv`, no build dependency. Only G2's download-branch logic rework (nested extract) stays in Phase 7. Published v1 asset names DO carry the leading `v` (`julie-extract-v2.0.0-…`, README:27 / workflow:94), so the test asserts `$"v{pinnedVersion}"` after `{VER}` substitution — no hedge. These two tests are tagged `Category!=Scale` (they only read files via `ScaleTestSupport.RepoRoot()`, no spawn) — leave them in the fast suite.

2. **Run** `scripts/test.sh` — expect `MillerExtractContractTests` red (missing `ExpectedSqliteSchemaVersion`/`ExpectedReportSchemaVersion`/`PinnedJulieExtractVersion`), and a compile break everywhere `PinnedJulieServerVersion` is referenced.

3. **Green — rewrite the constants.** Real code for `MillerExtractContract.cs`:

```csharp
namespace Miller.Indexing;

/// <summary>
/// The single source of truth for the julie-extract versions Miller is built against. Both
/// <see cref="JulieSchemaGate"/> (reading the DB's artifact_metadata) and <see cref="ExtractVersionMismatch"/>
/// (cross-checking the extract report's artifact block) gate on these constants. The runtime gate is the
/// schema/contract versions, NOT the product binary_version (D7 — product version and schema/contract version
/// are orthogonal: julie-extract 2.0.0 ships schema/contract 1, and a future product bump that keeps the
/// contract must not break Miller); <see cref="PinnedJulieExtractVersion"/> is the download pin only.
/// </summary>
internal static class MillerExtractContract
{
    // julie-extract v1: sqlite_schema_version 1 / extract_contract_version 1 / report_schema_version 1.
    // schema_version and sqlite_schema_version are both 1 in v1 (schema.rs).
    public const long ExpectedSchemaVersion = 1;
    public const long ExpectedSqliteSchemaVersion = 1;
    public const long ExpectedExtractContractVersion = 1;
    public const long ExpectedReportSchemaVersion = 1;
    public const string ExpectedHashAlgorithm = "blake3";

    // Download pin only (restore-script + julie-pins.json target). This is the PRODUCT version,
    // orthogonal to the runtime schema/contract gate above (D7): product 2.0.0 ships schema/contract 1.
    public const string PinnedJulieExtractVersion = "2.0.0"; // julie-extractors release tag v2.0.0 (README "Current Release", 2026-06-01).
}
```

   `PinnedJulieExtractVersion = "2.0.0"` is confirmed against the published release (`anortham/julie-extractors` tag `v2.0.0`, README "Current Release" table). It is the PRODUCT version, NOT the schema/contract version — those stay `1` and gate compatibility (D7). It must equal `scripts/julie-pins.json` `version` (G2); the `JuliePinsJsonMatchesContractVersion` test cross-locks the two so a future product bump can't drift the pin from the download URL silently.

4. **Run** `scripts/test.sh` — `MillerExtractContractTests.ContractPinsJulieExtractV1Versions` green; the `JuliePinsJsonMatchesContractVersion` cross-lock goes green once G2 writes the real pins (both land this branch; the release is published, so neither is blocked).

**Acceptance:**
- `ExpectedSchemaVersion == ExpectedSqliteSchemaVersion == ExpectedExtractContractVersion == ExpectedReportSchemaVersion == 1`; `ExpectedHashAlgorithm == "blake3"`.
- `PinnedJulieServerVersion` no longer exists; `PinnedJulieExtractVersion` does; no other file references the old name (verify `grep -rn PinnedJulieServerVersion src tests` is empty).
- Build 0 warnings.

---

### Task A2 — Rewrite `ExtractReport` flat → nested v1 (with `ReportDiagnostic` and convenience accessors)

**Files:** `src/Miller.Indexing/ExtractReport.cs` (rewrite), `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs` (rewrite), `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs` (rewrite the parser + cross-check tests — see A3), `tests/Miller.Tests/Indexing/JulieExtractRunnerUpdateDeleteTests.cs` (rewrite the Interpret fixtures — see A4/A5).

**What:** Replace the flat `ExtractReport` record with the v1 nested model: top-level `report_schema_version, status, operation, mode, input{}, artifact{}, tool{}, revision{}, counts{rows_written{},totals{}}, errors[], warnings[]`. Replace `ExtractError` with `ReportDiagnostic{code, message, path, root_relative_path, recoverable, details}`. Drop `analysis_state` (gone in v1). **Keep convenience accessors** (`Revision`, `CreatedRevision`, `SymbolsExtracted`, `FilesUpdated`, `FilesDeleted`, `FilesScanned`, `Errors`, `HashAlgorithm`) computed from the nested model so the cross-subsystem D consumers (`IndexBootstrapService`, `WorkspaceTool`, `CrossWorkspaceRefreshService`, `IndexerService`, `IndexerCore`) compile against the same accessor names while D owns the semantic remap (drop `WorkspaceId`).

**Revision mapping (design §4.2, tightened):** `Revision` (the freshness cursor) ⇒ `revision?.latest_revision_id`. `CreatedRevision` ⇒ `revision?.created_revision_id` (NEW accessor; **null on a no-op scan** — use only to detect whether *this* call mutated, never as the cursor). This preserves the `report.Revision ?? <DB fallback>` semantics D relies on.

**Why nested + accessors, not raw nested everywhere:** D's consumers read `report.Revision`/`report.SymbolsExtracted`/`report.FilesUpdated`/`report.FilesDeleted`. The design (§10D) keeps those reads and only remaps the workspace_id cross-check. Computed accessors keep the seam narrow; D does not relearn the nested path. The honest alternative (every D call-site digs `report.Revision?.LatestRevisionId`) is more churn for no gain. (Surfacing the tradeoff: this is a deliberate convenience layer, not silent scope reduction — the nested records are public so D *can* read raw fields if it needs `mode`/`input`/`artifact`.)

**Counts mapping** (old `*_total`/`*_extracted`/`*_scanned` → v1 `counts{}`, julie reports.rs:148-180):

| Old flat accessor | v1 source |
|---|---|
| `FilesScanned` | `counts.files_scanned` |
| `SymbolsExtracted` | `counts.rows_written.symbols` (rows written this op) |
| `FilesUpdated` | `counts.files_changed` (v1 names it `files_changed`) |
| `FilesDeleted` | `counts.files_deleted` |
| `FilesTotal` | `counts.totals.files` |
| `SymbolsTotal` | `counts.totals.symbols` |
| `RelationshipsTotal` | `counts.totals.relationships` |
| `IdentifiersTotal` | `counts.totals.identifiers` |
| `TypesTotal` | `counts.totals.type_argument_usages + type_arguments` is NOT a 1:1 — v1 has no single `types_total`. Map `TypesTotal` ⇒ `counts.totals.type_arguments` (the closest), and note the field is informational only (only logged). |

**Steps (TDD):**

1. **Red — rewrite `ExtractReportParsingTests.cs`** to feed v1-shaped nested JSON and assert the new accessors. v1 statuses replace the old ones (`ok/no_change/...`). Real code (representative subset — keep the five-outcome coverage):

```csharp
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins ExtractReport's v1 nested parse: revision.latest_revision_id is the freshness cursor;
/// created_revision_id is null on a no-op; counts/rows_written carry the per-op outcome; a null artifact
/// block is preserved (not invented). Each outcome the freshness path branches on (changed / no-op /
/// deleted / not_found / failed) is parsed from a representative report and asserted on the accessors.
/// </summary>
public sealed class ExtractReportParsingTests
{
    // update of a CHANGED file -> status=ok, files_changed=1, revision bumps (created==latest).
    private const string ChangedJson = """
        { "report_schema_version": 1, "status": "ok", "operation": "update", "mode": "single_file",
          "input": { "db_path": "/abs/.miller/symbols.db", "root_path": "/abs/repo", "file_path": "/abs/repo/src/a.cs",
                     "root_relative_path": "src/a.cs", "format": null, "output_path": null },
          "artifact": { "db_path": "/abs/.miller/symbols.db", "root_path": "/abs/repo", "artifact_id": "art-1",
                        "schema_version": 1, "extract_contract_version": 1, "sqlite_schema_version": 1,
                        "jsonl_schema_version": 1, "hash_algorithm": "blake3",
                        "parser_inventory_fingerprint": "sha256:pi", "capability_snapshot_fingerprint": "sha256:cs" },
          "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": { "latest_revision_id": 7, "created_revision_id": 7 },
          "counts": { "files_scanned": 0, "files_changed": 1, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 0,
                      "rows_written": { "symbols": 5 }, "totals": { "files": 12, "symbols": 134 } },
          "errors": [], "warnings": [] }
        """;

    // no-op update -> status=no_change, created_revision_id null, latest carries the prior cursor.
    private const string NoChangeJson = """
        { "report_schema_version": 1, "status": "no_change", "operation": "update", "mode": "single_file",
          "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": "/abs/r/a.cs",
                     "root_relative_path": "a.cs", "format": null, "output_path": null },
          "artifact": { "db_path": "/abs/db", "root_path": "/abs/r", "artifact_id": "a", "schema_version": 1,
                        "extract_contract_version": 1, "sqlite_schema_version": 1, "jsonl_schema_version": null,
                        "hash_algorithm": "blake3", "parser_inventory_fingerprint": "sha256:p",
                        "capability_snapshot_fingerprint": "sha256:c" },
          "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": { "latest_revision_id": 6, "created_revision_id": null },
          "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 1, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 0, "rows_written": {}, "totals": { "files": 12 } },
          "errors": [], "warnings": [] }
        """;

    private const string FailedJson = """
        { "report_schema_version": 1, "status": "failed", "operation": "update", "mode": "single_file",
          "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": "/abs/r/a.cs",
                     "root_relative_path": "a.cs", "format": null, "output_path": null },
          "artifact": null,
          "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": null,
          "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 1, "rows_written": {}, "totals": {} },
          "errors": [ { "code": "data_loss_guard", "message": "refusing to wipe a populated file",
                        "path": "/abs/r/a.cs", "root_relative_path": "a.cs", "recoverable": false, "details": {} } ],
          "warnings": [] }
        """;

    [Fact]
    public void Parse_Changed_CursorIsLatestRevision_AndCreatedSignalsMutation()
    {
        var r = JulieExtractRunner.ParseReport(ChangedJson);
        Assert.Equal("ok", r.Status);
        Assert.Equal("blake3", r.HashAlgorithm);   // sourced from artifact.hash_algorithm
        Assert.Equal(7L, r.Revision);              // latest_revision_id
        Assert.Equal(7L, r.CreatedRevision);       // this call mutated
        Assert.Equal(1u, r.FilesUpdated);          // counts.files_changed
        Assert.Equal(0u, r.FilesDeleted);
        Assert.Equal(5u, r.SymbolsExtracted);      // counts.rows_written.symbols
    }

    [Fact]
    public void Parse_NoChange_CreatedRevisionNull_CursorStillPresent()
    {
        var r = JulieExtractRunner.ParseReport(NoChangeJson);
        Assert.Equal("no_change", r.Status);
        Assert.Equal(6L, r.Revision);              // latest_revision_id still present after a no-op
        Assert.Null(r.CreatedRevision);            // no mutation -> created_revision_id null
        Assert.Equal(0u, r.FilesUpdated);
    }

    [Fact]
    public void Parse_Failed_NullArtifact_AndCarriesDiagnostics()
    {
        var r = JulieExtractRunner.ParseReport(FailedJson);
        Assert.Equal("failed", r.Status);
        Assert.Null(r.Artifact);                   // null artifact preserved, not invented
        Assert.Null(r.HashAlgorithm);              // no artifact => null accessor (gate fail in A3)
        Assert.Null(r.Revision);
        var d = Assert.Single(r.Errors);
        Assert.Equal("data_loss_guard", d.Code);
        Assert.False(d.Recoverable);               // data_loss_guard is non-recoverable in v1 (commands.rs:1099-1116); the per-diagnostic flag replaces the hardcoded transient set
    }
}
```

2. **Run** `scripts/test.sh` — red (the nested accessors / `ReportDiagnostic.Recoverable` / `Artifact` do not exist yet).

3. **Green — rewrite `ExtractReport.cs`.** Real code:

```csharp
using System.Text.Json.Serialization;

namespace Miller.Indexing;

/// <summary>
/// The nested JSON report julie-extract v1 emits on stdout (verified against
/// julie-extract-artifact/src/reports.rs). Top-level: report_schema_version, status, operation, mode,
/// input{}, artifact{}, tool{}, revision{}, counts{rows_written{},totals{}}, errors[], warnings[].
/// The flat M1/M3 accessors (Revision, SymbolsExtracted, FilesUpdated/Deleted, HashAlgorithm) are exposed
/// as computed properties over the nested model so the report-consuming services need not relearn the path;
/// the nested records stay public for callers that need mode/input/artifact directly.
/// </summary>
public sealed record ExtractReport(
    [property: JsonPropertyName("report_schema_version")] int? ReportSchemaVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("input")] ExtractReportInput? Input,
    [property: JsonPropertyName("artifact")] ExtractArtifact? Artifact,
    [property: JsonPropertyName("tool")] ExtractTool? Tool,
    [property: JsonPropertyName("revision")] ExtractRevision? RevisionBlock,
    [property: JsonPropertyName("counts")] ExtractCounts? Counts,
    [property: JsonPropertyName("errors")] IReadOnlyList<ReportDiagnostic> Errors,
    [property: JsonPropertyName("warnings")] IReadOnlyList<ReportDiagnostic> Warnings)
{
    /// <summary>The freshness cursor: revision.latest_revision_id (present after any scan; null when absent).</summary>
    [JsonIgnore] public long? Revision => RevisionBlock?.LatestRevisionId;

    /// <summary>revision.created_revision_id — NULL on a no-op; signals whether THIS call mutated. Never the cursor.</summary>
    [JsonIgnore] public long? CreatedRevision => RevisionBlock?.CreatedRevisionId;

    /// <summary>artifact.hash_algorithm; null when the artifact block is absent (a failed op).</summary>
    [JsonIgnore] public string? HashAlgorithm => Artifact?.HashAlgorithm;

    [JsonIgnore] public ulong FilesScanned => ToU(Counts?.FilesScanned);
    [JsonIgnore] public ulong FilesUpdated => ToU(Counts?.FilesChanged);   // v1 calls it files_changed
    [JsonIgnore] public ulong FilesDeleted => ToU(Counts?.FilesDeleted);
    [JsonIgnore] public ulong SymbolsExtracted => ToU(Counts?.RowsWritten?.Symbols);
    [JsonIgnore] public ulong FilesTotal => ToU(Counts?.Totals?.Files);
    [JsonIgnore] public ulong SymbolsTotal => ToU(Counts?.Totals?.Symbols);
    [JsonIgnore] public ulong RelationshipsTotal => ToU(Counts?.Totals?.Relationships);
    [JsonIgnore] public ulong IdentifiersTotal => ToU(Counts?.Totals?.Identifiers);

    // julie emits signed counts (i64) that are non-negative in practice; clamp the rare negative to 0.
    private static ulong ToU(long? v) => v is { } n && n > 0 ? (ulong)n : 0UL;
}

public sealed record ExtractReportInput(
    [property: JsonPropertyName("db_path")] string? DbPath,
    [property: JsonPropertyName("root_path")] string? RootPath,
    [property: JsonPropertyName("file_path")] string? FilePath,
    [property: JsonPropertyName("root_relative_path")] string? RootRelativePath,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("output_path")] string? OutputPath);

public sealed record ExtractArtifact(
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("root_path")] string RootPath,
    [property: JsonPropertyName("artifact_id")] string ArtifactId,
    [property: JsonPropertyName("schema_version")] long SchemaVersion,
    [property: JsonPropertyName("extract_contract_version")] long ExtractContractVersion,
    [property: JsonPropertyName("sqlite_schema_version")] long SqliteSchemaVersion,
    [property: JsonPropertyName("jsonl_schema_version")] long? JsonlSchemaVersion,
    [property: JsonPropertyName("hash_algorithm")] string HashAlgorithm,
    [property: JsonPropertyName("parser_inventory_fingerprint")] string? ParserInventoryFingerprint,
    [property: JsonPropertyName("capability_snapshot_fingerprint")] string? CapabilitySnapshotFingerprint);

public sealed record ExtractTool(
    [property: JsonPropertyName("binary_name")] string BinaryName,
    [property: JsonPropertyName("binary_version")] string BinaryVersion);

public sealed record ExtractRevision(
    [property: JsonPropertyName("latest_revision_id")] long? LatestRevisionId,
    [property: JsonPropertyName("created_revision_id")] long? CreatedRevisionId);

public sealed record ExtractCounts(
    [property: JsonPropertyName("files_scanned")] long FilesScanned,
    [property: JsonPropertyName("files_changed")] long FilesChanged,
    [property: JsonPropertyName("files_unchanged")] long FilesUnchanged,
    [property: JsonPropertyName("files_unsupported")] long FilesUnsupported,
    [property: JsonPropertyName("files_deleted")] long FilesDeleted,
    [property: JsonPropertyName("files_failed")] long FilesFailed,
    [property: JsonPropertyName("rows_written")] ExtractRowCounts? RowsWritten,
    [property: JsonPropertyName("totals")] ExtractRowCounts? Totals);

/// <summary>The 18 v1 row domains (reports.rs RowDomainCounts); Miller reads a handful, the rest deserialize for completeness.</summary>
public sealed record ExtractRowCounts(
    [property: JsonPropertyName("files")] long? Files,
    [property: JsonPropertyName("symbols")] long? Symbols,
    [property: JsonPropertyName("symbol_annotations")] long? SymbolAnnotations,
    [property: JsonPropertyName("identifiers")] long? Identifiers,
    [property: JsonPropertyName("relationships")] long? Relationships,
    [property: JsonPropertyName("type_arguments")] long? TypeArguments,
    [property: JsonPropertyName("type_argument_usages")] long? TypeArgumentUsages,
    [property: JsonPropertyName("literals")] long? Literals,
    [property: JsonPropertyName("extraction_revisions")] long? ExtractionRevisions,
    [property: JsonPropertyName("revision_file_changes")] long? RevisionFileChanges);

/// <summary>One julie-extract diagnostic (reports.rs ReportDiagnostic). `code` is a snake_case string.</summary>
public sealed record ReportDiagnostic(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("root_relative_path")] string? RootRelativePath,
    [property: JsonPropertyName("recoverable")] bool Recoverable,
    [property: JsonPropertyName("details")] System.Text.Json.JsonElement Details = default);
```

   Notes baked in:
   - `ReportDiagnostic.Code` stays a **string** (the snake_case enum serializes to it), so `ExtractErrorLog.Describe` (`e.Code`) and `IndexerCore`'s transient-set `Contains(e.Code)` keep compiling — D only swaps `flock_timeout`→`lock_timeout` and may prefer `Recoverable`.
   - `details` is a `JsonElement` (`Value` in Rust = arbitrary JSON); default `default` so it is optional.
   - `ExtractError` is deleted. **Mechanical follow-on:** `JulieExtractExceptions.cs` (subsystem-A-adjacent but not in my owned set — it's `src/Miller.Indexing/JulieExtractExceptions.cs`) has `IReadOnlyList<ExtractError> Errors` on `JulieExtractFailedException`. **I own the type rename, so I update that signature** to `IReadOnlyList<ReportDiagnostic>` and its XML-doc. One representative edit:
     - `JulieExtractExceptions.cs:35` `public IReadOnlyList<ExtractError> Errors { get; }` → `IReadOnlyList<ReportDiagnostic> Errors`
     - `:37` ctor param `IReadOnlyList<ExtractError> errors` → `IReadOnlyList<ReportDiagnostic> errors`
   - `JulieExtractRunner.Interpret` (A5) uses `Array.Empty<ExtractError>()` → `Array.Empty<ReportDiagnostic>()`.

4. **Run** `scripts/test.sh` — `ExtractReportParsingTests` green. Build may still be red from A3/A4/A5 fixtures; that is expected mid-arc. **Run** `dotnet build Miller.slnx -c Release` after the full A2-A5 arc lands; expect 0 warnings.

**Acceptance:**
- `ExtractReport` deserializes the nested v1 report; `Revision` ⇒ `latest_revision_id`, `CreatedRevision` ⇒ `created_revision_id` (null on no-op), `HashAlgorithm` ⇒ `artifact.hash_algorithm`, `SymbolsExtracted` ⇒ `rows_written.symbols`, `FilesUpdated` ⇒ `files_changed`, `FilesDeleted` ⇒ `files_deleted`.
- `Artifact` is `null` when the report omits it; the accessors over a null artifact return null (gated in A3).
- `ExtractError` is gone; `ReportDiagnostic` replaces it with `Recoverable`; `JulieExtractFailedException.Errors` is `IReadOnlyList<ReportDiagnostic>`.
- `analysis_state` no longer parsed.

---

### Task A3 — Rewrite `ExtractVersionMismatch` to gate on `report.Artifact.*` (null artifact = gate fail)

**Files:** `src/Miller.Indexing/ExtractVersionMismatch.cs` (rewrite), `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs` (rewrite the `VerifyReport` cases at `:285-360`).

**What:** The post-extract cross-check currently reads the flat `report.SchemaVersion`/`ExtractContractVersion`/`HashAlgorithm` (`ExtractVersionMismatch.cs:51-86`). In v1 those live in `report.Artifact.{SqliteSchemaVersion, ExtractContractVersion, HashAlgorithm}`. A **null `Artifact` block is itself a gate failure** (a successful artifact-producing op must carry it; its absence means the report is not a v1 artifact report). Update the `BuildMessage` wording (re-pin julie-extract). Keep the helper shared with `JulieSchemaGate` (subsystem B) so both emit identical text.

**Steps (TDD):**

1. **Red — rewrite the `VerifyReport` tests in `JulieExtractRunnerTests.cs`.** The old `ReportWith(...)` factory built the flat record; rewrite it to build the nested record (or a small `ArtifactWith(...)` helper). Real code:

```csharp
// ---- (4) post-extract version cross-check (D5/D7; gate on report.artifact.*) ----

private static ExtractReport ReportWith(
    long? sqliteSchema, long? contract, string? hashAlgorithm = MillerExtractContract.ExpectedHashAlgorithm,
    bool withArtifact = true)
{
    ExtractArtifact? artifact = withArtifact
        ? new ExtractArtifact(
            DbPath: "/abs/db", RootPath: "/abs/r", ArtifactId: "a",
            SchemaVersion: sqliteSchema ?? MillerExtractContract.ExpectedSchemaVersion,
            ExtractContractVersion: contract ?? MillerExtractContract.ExpectedExtractContractVersion,
            SqliteSchemaVersion: sqliteSchema ?? MillerExtractContract.ExpectedSqliteSchemaVersion,
            JsonlSchemaVersion: 1, HashAlgorithm: hashAlgorithm!,
            ParserInventoryFingerprint: "p", CapabilitySnapshotFingerprint: "c")
        : null;
    return new ExtractReport(
        ReportSchemaVersion: 1, Status: "ok", Operation: "scan", Mode: "force",
        Input: null, Artifact: artifact,
        Tool: new ExtractTool("julie-extract", "2.0.0"),
        RevisionBlock: new ExtractRevision(1, 1),
        Counts: null,
        Errors: Array.Empty<ReportDiagnostic>(), Warnings: Array.Empty<ReportDiagnostic>());
}

[Fact]
public void VerifyReport_AtPinnedSchemaAndContract_DoesNotThrow() =>
    ExtractVersionMismatch.VerifyReport(ReportWith(
        MillerExtractContract.ExpectedSqliteSchemaVersion, MillerExtractContract.ExpectedExtractContractVersion));

[Fact]
public void VerifyReport_NewerSchema_ThrowsNamingValueAndPointingAtUpgrade()
{
    var ex = Assert.Throws<IncompatibleExtractException>(() =>
        ExtractVersionMismatch.VerifyReport(ReportWith(
            MillerExtractContract.ExpectedSqliteSchemaVersion + 1, MillerExtractContract.ExpectedExtractContractVersion)));
    Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("upgrade Miller", ex.Message, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void VerifyReport_OlderContract_ThrowsNamingValueAndPointingAtRestore()
{
    var ex = Assert.Throws<IncompatibleExtractException>(() =>
        ExtractVersionMismatch.VerifyReport(ReportWith(
            MillerExtractContract.ExpectedSqliteSchemaVersion, MillerExtractContract.ExpectedExtractContractVersion - 1)));
    Assert.Contains("extract_contract_version", ex.Message);
    Assert.Contains("restore", ex.Message, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void VerifyReport_NullArtifact_FailsTheGate_NotASilentPass()
{
    // A v1 artifact-producing op MUST carry the artifact block; its absence is a contract failure.
    var ex = Assert.Throws<IncompatibleExtractException>(() =>
        ExtractVersionMismatch.VerifyReport(ReportWith(1, 1, withArtifact: false)));
    Assert.Contains("artifact", ex.Message, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void VerifyReport_WrongHashAlgorithm_ThrowsNamingValueAndExpectedValue()
{
    var ex = Assert.Throws<IncompatibleExtractException>(() =>
        ExtractVersionMismatch.VerifyReport(ReportWith(1, 1, hashAlgorithm: "sha256")));
    Assert.Contains("hash_algorithm", ex.Message);
    Assert.Contains("sha256", ex.Message);
    Assert.Contains("blake3", ex.Message, StringComparison.OrdinalIgnoreCase);
}

[Theory]
[InlineData(2)]      // a future/incompatible report envelope
[InlineData(null)]   // absent report_schema_version
public void VerifyReport_WrongOrMissingReportSchemaVersion_Throws(int? reportSchemaVersion)
{
    var ex = Assert.Throws<IncompatibleExtractException>(() =>
        ExtractVersionMismatch.VerifyReport(ReportWith(1, 1, reportSchemaVersion: reportSchemaVersion)));
    Assert.Contains("report_schema_version", ex.Message);
}
```
   (`ReportWith` gains an optional `int? reportSchemaVersion = 1` param threaded onto the top-level `ExtractReport.ReportSchemaVersion`; the existing happy-path tests keep the default `1`.)

   **Note:** the old `VerifyReport_NullVersions_DoesNotThrow` test (skip-null) is **deleted** — in v1 a present artifact always carries the versions (`ArtifactReport` fields are non-`Option` except `jsonl_schema_version`), so the new gate failure mode is "null artifact", not "null versions". The old `JulieDbFixture.SchemaText(1)`/`SchemaText()` message-substring assertions are replaced by the `newer`/`upgrade`/`restore` keyword assertions (those still survive in `BuildMessage`).

2. **Run** `scripts/test.sh` — red.

3. **Green — rewrite `ExtractVersionMismatch.cs`.** Real code (the `VerifyReport` method body and message wording; keep the `BuildMessage` signature since subsystem B's `JulieSchemaGate` calls it):

```csharp
public static void VerifyReport(ExtractReport report)
{
    ArgumentNullException.ThrowIfNull(report);

    // The report envelope must be a v1 report. report_schema_version frames artifact/counts/revision; a
    // missing or different value means the producer's report contract changed (reports.rs:5,50). Gate it
    // alongside schema/contract/hash; keep tool.binary_version OUT of the gate (D7). (reconciliation #12)
    if (report.ReportSchemaVersion != MillerExtractContract.ExpectedReportSchemaVersion)
        throw new IncompatibleExtractException(
            $"Extract report_schema_version is '{report.ReportSchemaVersion?.ToString() ?? "(absent)"}' but this " +
            $"Miller build expects {MillerExtractContract.ExpectedReportSchemaVersion}: incompatible julie-extract " +
            "report contract. Re-run restore + `julie-extract scan` with the pinned binary.");

    // A v1 artifact-producing op MUST carry the artifact block. Its absence means the report is not a
    // julie-extract v1 artifact report — fail loud, never a silent pass.
    if (report.Artifact is not { } artifact)
        throw new IncompatibleExtractException(
            "Extract report has no artifact block; a julie-extract v1 scan/update/delete/info must carry " +
            "report.artifact (schema/contract/hash). Re-run restore + `julie-extract scan` with the pinned binary.");

    if (artifact.SqliteSchemaVersion != MillerExtractContract.ExpectedSqliteSchemaVersion)
        throw new IncompatibleExtractException(BuildMessage(
            kind: "schema",
            actual: Str(artifact.SqliteSchemaVersion),
            expected: Str(MillerExtractContract.ExpectedSqliteSchemaVersion),
            isNewer: artifact.SqliteSchemaVersion > MillerExtractContract.ExpectedSqliteSchemaVersion,
            schemaVersion: artifact.SqliteSchemaVersion,
            contractVersionForMessage: artifact.ExtractContractVersion));

    if (artifact.ExtractContractVersion != MillerExtractContract.ExpectedExtractContractVersion)
        throw new IncompatibleExtractException(BuildMessage(
            kind: "extract_contract_version",
            actual: Str(artifact.ExtractContractVersion),
            expected: Str(MillerExtractContract.ExpectedExtractContractVersion),
            isNewer: artifact.ExtractContractVersion > MillerExtractContract.ExpectedExtractContractVersion,
            schemaVersion: artifact.SqliteSchemaVersion,
            contractVersionForMessage: artifact.ExtractContractVersion));

    if (string.IsNullOrWhiteSpace(artifact.HashAlgorithm))
        throw new IncompatibleExtractException(
            "Extract report artifact is missing hash_algorithm; expected " +
            $"'{MillerExtractContract.ExpectedHashAlgorithm}'. Re-run restore + `julie-extract scan`.");

    if (!StringComparer.Ordinal.Equals(artifact.HashAlgorithm, MillerExtractContract.ExpectedHashAlgorithm))
        throw new IncompatibleExtractException(
            $"Extract report hash_algorithm is '{artifact.HashAlgorithm}' but this Miller build expects " +
            $"'{MillerExtractContract.ExpectedHashAlgorithm}': not a julie-extract v1 artifact; " +
            "re-run restore + `julie-extract scan` with the pinned binary.");
}
```

   And rewrite `BuildMessage`'s older-path wording (`ExtractVersionMismatch.cs:34-38`): replace `PinnedJulieServerVersion` → `PinnedJulieExtractVersion` and `julie-server` / `extract scan` → `julie-extract` / `julie-extract scan`:

```csharp
long contractForMsg = contractVersionForMessage ?? MillerExtractContract.ExpectedExtractContractVersion;
return $"DB {kind} is {actual} but this Miller build expects {expected}: DB is not a " +
       $"julie-extract v{MillerExtractContract.PinnedJulieExtractVersion} artifact " +
       $"(schema {schemaVersion}, contract {contractForMsg}); re-run restore + `julie-extract scan` " +
       "with the pinned julie-extract.";
```

   And the newer-path string (`:30-31`): `…upgrade Miller or re-pin julie-server.` → `…upgrade Miller or re-pin julie-extract.`

4. **Run** `scripts/test.sh` — `VerifyReport*` green.

**Acceptance:**
- Version/hash read from `report.Artifact.*`; null artifact throws `IncompatibleExtractException` (not a silent pass).
- Gate compares `SqliteSchemaVersion`/`ExtractContractVersion` against the v1 constants; `binary_version` is NOT cross-checked (D7).
- `BuildMessage` wording says `julie-extract` (no `julie-server`); shared with `JulieSchemaGate` (B) so both paths emit identical text.

---

### Task A4 — `JulieExtractRunner` argv: `julie-extract` binary, drop `extract` token + `--workspace-id`, add `--strict-schema`

**Files:** `src/Miller.Indexing/JulieExtractRunner.cs` (the argv builders, `Locate`, `Scan`, the constructor error string), `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs` + `JulieExtractRunnerUpdateDeleteTests.cs` (the argv assertions).

**What:** Build v1 argv. This is **partly mechanical (argv shape) but load-bearing** — the exact token order is the contract, so it gets a locking test, not just a rename table.

**Argv mapping (verified against args.rs):**

| Op | OLD argv (current code) | NEW v1 argv |
|---|---|---|
| Scan | `extract --db D --root R --workspace-id ID --json scan [--force]` | `scan --root R --db D --strict-schema --json [--force]` |
| Info | `extract --db D --json info` | `info --db D --strict-schema --json` |
| Update | `extract --db D --root R --json update --file F` | `update --root R --db D --file F --strict-schema --json` |
| Delete | `extract --db D --root R --json delete --file F` | `delete --root R --db D --file F --strict-schema --json` |

Token-order note: v1 flags are order-independent (clap), but pick a stable order for the test. `BuildScanArgs` **drops the `workspaceId` parameter entirely** (no `--workspace-id` in v1) — this changes the signature, so `Scan()` stops computing `WorkspaceId.FromCanonicalRoot(absRoot)` (line 219). `--force` stays a top-level flag on `scan`.

**Steps (TDD):**

1. **Red — rewrite the argv tests.** `BuildScanArgs` loses its `workspaceId` parameter. Real code (representative — the scan + info + update; delete mirrors update):

```csharp
[Fact]
public void BuildScanArgs_ProducesV1Argv_NoExtractToken_NoWorkspaceId_StrictSchema()
{
    var args = JulieExtractRunner.BuildScanArgs(AbsDb, AbsRoot, force: false);
    Assert.Equal(
        new[] { "scan", "--root", AbsRoot, "--db", AbsDb, "--strict-schema", "--json" },
        args);
    Assert.DoesNotContain("extract", args);
    Assert.DoesNotContain("--workspace-id", args);
}

[Fact]
public void BuildScanArgs_Force_AppendsForceFlag()
{
    var args = JulieExtractRunner.BuildScanArgs(AbsDb, AbsRoot, force: true);
    Assert.Equal(
        new[] { "scan", "--root", AbsRoot, "--db", AbsDb, "--strict-schema", "--json", "--force" },
        args);
}

[Fact]
public void BuildInfoArgs_TopLevel_NoRoot_StrictSchema()
{
    var args = JulieExtractRunner.BuildInfoArgs(AbsDb);
    Assert.Equal(new[] { "info", "--db", AbsDb, "--strict-schema", "--json" }, args);
    Assert.DoesNotContain("--root", args);
    Assert.DoesNotContain("extract", args);
}

[Fact]
public void BuildUpdateArgs_ProducesV1Argv_FileBeforeStrictSchema()
{
    var args = JulieExtractRunner.BuildUpdateArgs(AbsDb, AbsRoot, AbsFile);
    Assert.Equal(
        new[] { "update", "--root", AbsRoot, "--db", AbsDb, "--file", AbsFile, "--strict-schema", "--json" },
        args);
    Assert.DoesNotContain("extract", args);
}
```

   Also delete the `WorkspaceId` const usage in `JulieExtractRunnerTests` (the `const string WorkspaceId = "...";` at `:17` becomes dead — remove it) and update `Constructor_BinaryNotFound_ThrowsPointingAtRestoreScript` (`:243-248`) to point at `julie-extract` and `restore-julie-extract`:

```csharp
[Fact]
public void Constructor_BinaryNotFound_ThrowsPointingAtRestoreScript()
{
    string missing = Path.Combine(Path.GetTempPath(), "miller-no-julie-" + Guid.NewGuid().ToString("N"), "julie-extract");
    var ex = Assert.Throws<FileNotFoundException>(() => new JulieExtractRunner(missing));
    Assert.Contains("restore-julie-extract", ex.Message);
}
```

2. **Run** `scripts/test.sh` — red (signature + token mismatch).

3. **Green — edit `JulieExtractRunner.cs`:**
   - `BuildScanArgs` (`:97-107`): drop the `workspaceId` parameter and its null-guard; build `{ "scan", "--root", absRoot, "--db", absDb, "--strict-schema", "--json" }`, then `if (force) args.Add("--force")`.
   - `BuildInfoArgs` (`:113-117`): `{ "info", "--db", absDb, "--strict-schema", "--json" }`.
   - `BuildFileOpArgs` (`:139-146`): `{ subcommand, "--root", absRoot, "--db", absDb, "--file", absFile, "--strict-schema", "--json" }` (subcommand = `update`/`delete`).
   - `Scan()` (`:208-221`): delete line 219 `string workspaceId = WorkspaceId.FromCanonicalRoot(absRoot);` and call `Run(BuildScanArgs(absDb, absRoot, force))`.
   - `Locate()` (`:58-74`) + constructor (`:41-51`): binary name `julie-server[.exe]` → `julie-extract[.exe]`; error strings `scripts/restore-julie-server.sh`→`scripts/restore-julie-extract.sh` (and `.ps1`), `v{PinnedJulieServerVersion}`→`v{PinnedJulieExtractVersion}`, `julie-server`→`julie-extract`. The `BinaryPath` XML doc (`:33`) `julie-server binary`→`julie-extract binary`.
   - Update the class-header XML comment (`:7-19`) and the inline argv comments (`:22-24`, `:92-95`, `:109-112`, `:119-135`) to the v1 shape (drop the `extract` parent + `--workspace-id` mentions).
   - Exec-failure wrap (`:296-308`): `julie-server`→`julie-extract`, `restore-julie-server.sh`→`restore-julie-extract.sh`.

   **CROSS-DEP:** `JulieExtractRunner.Scan(string root, string db, bool force)` keeps its **public signature** (used by `JulieExtractOps.Create`/`CreateForTest` and `IndexBootstrapService.runner.Scan` and `CrossWorkspaceRefreshService._scanForOpen`) — only the internal `BuildScanArgs` call drops `workspaceId`. So the D-subsystem call sites that call `runner.Scan(root, db, force)` are unaffected. `WorkspaceId.FromCanonicalRoot` stays for Miller's own registry (subsystem D `WorkspaceId.cs`) — A4 only stops the runner from passing it to julie.

4. **Run** `scripts/test.sh` — argv tests green; `JulieExtractRunnerUpdateDeleteTests` argv + reject-null tests green (their null-guard `[Theory]` cases at `:50-72` still hold — the builders still `ThrowIfNullOrWhiteSpace` db/root/file).

**Acceptance:**
- `BuildScanArgs` has no `workspaceId` parameter; emits `scan ... --strict-schema --json` with no `extract` token and no `--workspace-id`; `--force` appended on force.
- `BuildInfoArgs`/`BuildUpdateArgs`/`BuildDeleteArgs` are top-level with `--strict-schema --json`.
- `Locate`/constructor resolve `julie-extract[.exe]` and point at `restore-julie-extract.{sh,ps1}` / `PinnedJulieExtractVersion`.
- No `--workspace-id` or `extract` literal anywhere in `JulieExtractRunner.cs` (verify `grep -n "workspace-id\|\"extract\"" src/Miller.Indexing/JulieExtractRunner.cs` is empty).

---

### Task A5 — `Interpret()` `case 3` → `IncompatibleExtractException` via `errors[0].code`

**Files:** `src/Miller.Indexing/JulieExtractRunner.cs` (the `Interpret` switch `:162-199`), `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs` (the exit-code tests `:170-238`), `JulieExtractRunnerUpdateDeleteTests.cs` (the `Interpret` fixtures `:76-123`).

**What:** v1 exit codes are `0/1/2/3`. The current switch (`:167-198`) maps anything non-0/1/2 to a generic `JulieExtractException`. Add `case 3` that parses stdout for `errors[0].code` and throws `IncompatibleExtractException` (the schema/contract/root-incompatible signal — `schema_incompatible`/`schema_migration_required`/`contract_incompatible`/`root_mismatch`, all exit 3 per julie commands.rs). Path errors stay exit 1 → `JulieExtractFailedException` (branch on `errors[].code`, not on exit alone, per design §4.1). Also fix the exit-1 fixture/`Array.Empty<ExtractError>()` → `ReportDiagnostic` and the error wording `julie-server extract`→`julie-extract`.

**Why a typed case 3, not a generic crash:** exit 3 is the contract-drift signal Miller's fail-loud philosophy must surface as `IncompatibleExtractException` (same type the read-path gate throws), so the operator gets one actionable message regardless of which boundary caught it.

**Steps (TDD):**

1. **Red — rewrite the exit-code tests.** Real code (v1-shaped fixtures + the new case-3 test):

```csharp
[Fact]
public void Interpret_Exit3_SchemaIncompatible_ThrowsIncompatibleExtract_FromErrorCode()
{
    const string incompatible = """
        { "report_schema_version": 1, "status": "failed", "operation": "info", "mode": "read_only",
          "input": { "db_path": "/abs/db", "root_path": null, "file_path": null,
                     "root_relative_path": null, "format": null, "output_path": null },
          "artifact": null, "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": null,
          "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 0, "rows_written": {}, "totals": {} },
          "errors": [ { "code": "schema_incompatible", "message": "artifact schema version is newer than this binary supports",
                        "path": null, "root_relative_path": null, "recoverable": false, "details": {} } ],
          "warnings": [] }
        """;
    var ex = Assert.Throws<IncompatibleExtractException>(() =>
        JulieExtractRunner.Interpret(exitCode: 3, stdout: incompatible, stderr: ""));
    Assert.Contains("schema_incompatible", ex.Message);
}

[Fact]
public void Interpret_Exit3_RootMismatch_ThrowsIncompatibleExtract()
{
    const string rootMismatch = """
        { "report_schema_version": 1, "status": "failed", "operation": "scan", "mode": "incremental",
          "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": null,
                     "root_relative_path": null, "format": null, "output_path": null },
          "artifact": null, "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": null,
          "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 0, "rows_written": {}, "totals": {} },
          "errors": [ { "code": "root_mismatch", "message": "artifact root does not match requested root",
                        "path": "/abs/db", "root_relative_path": null, "recoverable": false, "details": {} } ],
          "warnings": [] }
        """;
    var ex = Assert.Throws<IncompatibleExtractException>(() =>
        JulieExtractRunner.Interpret(exitCode: 3, stdout: rootMismatch, stderr: ""));
    Assert.Contains("root_mismatch", ex.Message);
}

[Fact]
public void Interpret_Exit3_UnparseableStdout_StillThrowsIncompatible_CarryingStderr()
{
    var ex = Assert.Throws<IncompatibleExtractException>(() =>
        JulieExtractRunner.Interpret(exitCode: 3, stdout: "not json", stderr: "boom"));
    Assert.Contains("boom", ex.Message);  // never a silent pass
}

[Fact]
public void Interpret_Exit1_PathError_StaysOperationFailure_NotIncompatible()
{
    // FileOutsideRoot is exit 1 in v1 (commands.rs path policy), so it surfaces as a FAILED op, not an
    // incompatible-schema gate. Branch on errors[].code semantics, not the exit code alone.
    const string outsideRoot = """
        { "report_schema_version": 1, "status": "failed", "operation": "update", "mode": "single_file",
          "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": "/x",
                     "root_relative_path": null, "format": null, "output_path": null },
          "artifact": null, "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": null,
          "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 1, "rows_written": {}, "totals": {} },
          "errors": [ { "code": "file_outside_root", "message": "file is outside external extract root",
                        "path": "/x", "root_relative_path": null, "recoverable": false, "details": {} } ],
          "warnings": [] }
        """;
    var ex = Assert.Throws<JulieExtractFailedException>(() =>
        JulieExtractRunner.Interpret(exitCode: 1, stdout: outsideRoot, stderr: ""));
    Assert.Equal("file_outside_root", Assert.Single(ex.Errors).Code);
}

[Fact]
public void Interpret_UnexpectedExitCode_ThrowsBaseExtractException()
{
    var ex = Assert.Throws<JulieExtractException>(() =>
        JulieExtractRunner.Interpret(exitCode: 137, stdout: "", stderr: "killed"));
    Assert.Contains("137", ex.Message);
    Assert.IsType<JulieExtractException>(ex, exactMatch: true);  // base type, not a subclass
}
```

   Also rewrite `Interpret_Exit0_ReturnsParsedReport`, `Interpret_Exit1_Throws...`, `Interpret_Exit2_ThrowsUsage`, `Interpret_DeleteNotFound_Exit0_IsTolerated` and `JulieExtractRunnerUpdateDeleteTests`' `ChangedJson`/`FailedUpdateJson`/`NotFoundJson` to the v1 nested shape (status `ok`/`no_change`/`not_found`/`failed`, nested `counts`, `errors[].recoverable`). The `not_found` tolerated case stays exit 0 → returns the report (julie reports.rs `NotFound` status, exit 0).

2. **Run** `scripts/test.sh` — red (no `case 3`).

3. **Green — edit `Interpret` (`JulieExtractRunner.cs:162-199`).** Real code:

```csharp
public static ExtractReport Interpret(int exitCode, string stdout, string stderr)
{
    ArgumentNullException.ThrowIfNull(stdout);
    ArgumentNullException.ThrowIfNull(stderr);

    switch (exitCode)
    {
        case 0:
            return ParseReport(stdout);

        case 1:
            // stdout STILL holds a report. Two sub-cases (reconciliation #10):
            //  - status=="partial": some files failed to parse but the artifact is CONSISTENT
            //    (.with_artifact + rows_written + revision; commands.rs:217-251). RETURN it so bootstrap
            //    loads the usable rows; the caller logs counts.files_failed + errors[] as a WARNING.
            //    Aborting the whole index build because one file failed is wrong (README "Reports And Exit Status").
            //  - status=="failed" (or unparseable): a real failure → throw with the structured diagnostics.
            //    Path errors (file_outside_root/invalid_path/file_not_found) are status=="failed" here, NOT exit 3.
            ExtractReport? report1 = null;
            IReadOnlyList<ReportDiagnostic> errors;
            try { report1 = ParseReport(stdout); errors = report1.Errors; }
            catch (JsonException) { errors = Array.Empty<ReportDiagnostic>(); }

            if (report1 is { Status: "partial" })
                return report1; // consistent artifact; caller WARN-logs files_failed + errors[]

            string codes = errors.Count == 0
                ? "(no structured errors)"
                : string.Join(", ", errors.Select(e => e.Code));
            throw new JulieExtractFailedException(
                $"julie-extract failed (exit 1): {codes}.", errors, stderr);

        case 2:
            // Usage/argv error: NO JSON on stdout, clap usage text on stderr. Do not parse stdout.
            throw new JulieExtractUsageException(stderr);

        case 3:
            // Incompatible schema/contract/root (schema_incompatible / schema_migration_required /
            // contract_incompatible / root_mismatch). stdout holds a failed report; surface its code as the
            // SAME typed signal the read-path gate throws. Defensive: an unparseable stdout still throws
            // incompatible (carrying stderr), never a silent pass.
            string code;
            try
            {
                var report = ParseReport(stdout);
                code = report.Errors.Count > 0 ? report.Errors[0].Code : "(no structured errors)";
            }
            catch (JsonException)
            {
                code = string.IsNullOrWhiteSpace(stderr) ? "(unparseable report)" : stderr;
            }
            throw new IncompatibleExtractException(
                $"julie-extract reported an incompatible artifact (exit 3): {code}. " +
                "Re-run restore + `julie-extract scan` with the pinned julie-extract " +
                $"(v{MillerExtractContract.PinnedJulieExtractVersion}).");

        default:
            throw new JulieExtractException(
                $"julie-extract exited with unexpected code {exitCode}.", stderr);
    }
}
```

   Update the `Interpret` XML doc (`:157-161`) to name the four-code contract (0/1/2/3). Update the class header (A4 already touched it) to mention case 3.

4. **Run** `scripts/test.sh` — all `Interpret*` green.

**Acceptance:**
- `Interpret(3, ...)` throws `IncompatibleExtractException` carrying `errors[0].code` (or stderr/unparseable fallback — never a silent pass).
- `Interpret(1, ...)` with `status=="partial"` **RETURNS the report** (consistent artifact preserved); with `status=="failed"` (or unparseable stdout) it throws `JulieExtractFailedException` with the structured `ReportDiagnostic` list. Tests cover both exit-1 sub-cases. The bootstrap caller WARN-logs `counts.files_failed` + `errors[]` on a returned partial.
- Exit 2 stays `JulieExtractUsageException` (no stdout parse); unexpected codes stay base `JulieExtractException`.
- Error wording says `julie-extract` (no `julie-server extract`).

---

### Task A6 — Spawn hardening: `WaitForExit(timeout)` + `Kill` on a hung `julie-extract`

**Files:** `src/Miller.Indexing/JulieExtractRunner.cs` (`Run` `:274-324`), `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs` (new live-spawn test, **Scale-tagged**).

**What:** The current `Run` calls `process.WaitForExit()` with no timeout (`:314`). A hung julie-extract would block bootstrap `StartAsync` forever (the host-lifecycle gotcha in CLAUDE.md — a hosted-service `StartAsync` that never returns wedges the whole host graph). Add a bounded `WaitForExit(timeout)`; on timeout, `Kill(entireProcessTree: true)` and throw `JulieExtractException` with an actionable message. This is the design's **recommended in-scope correct fix** (§10A) — surfacing the tradeoff: it adds a timeout knob, but the alternative (a wedged bootstrap with no diagnostic) is strictly worse and matches Miller's fail-loud rule.

**Timeout value:** a generous default (e.g. 10 minutes for a cold full scan of a large repo) so a legitimate slow scan is never killed, but a truly hung process is bounded. Expose it as a constructor-injected `TimeSpan` with that default so the Scale test can pass a tiny value.

**Steps (TDD):**

1. **Red — add a Scale-tagged live test** that spawns a guaranteed-hung process via a tiny timeout. Because this spawns a real binary, it MUST be `[Trait("Category","Scale")]` and obtain julie via `ScaleTestSupport.RequireJulieServer()` (per CLAUDE.md). Use `info` against a never-finishing target is hard to force, so instead point the runner at a sleeping shim — but the cleanest deterministic test is to construct the runner against a real long-running process with a 1ms timeout. Real code:

```csharp
[Trait("Category", "Scale")]
public sealed class JulieExtractRunnerTimeoutTests
{
    [Fact]
    public void Run_HungProcess_TimesOut_KillsAndThrows()
    {
        // A real spawn that exceeds the timeout: point the runner at the julie-extract binary with an
        // impossibly small timeout against a scan of the repo, and assert it is killed (not wedged forever).
        string julie = ScaleTestSupport.RequireJulieServer();
        var runner = new JulieExtractRunner(julie, TimeSpan.FromMilliseconds(1));

        using var dir = new TempDir();
        string db = Path.Combine(dir.Path, ".miller", "symbols.db");

        var ex = Assert.Throws<JulieExtractException>(() => runner.Scan(ScaleTestSupport.RepoRoot(), db));
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
```

   (`TempDir` is the existing test helper; if the 1ms race is flaky on a trivially-fast scan, use a fixture root large enough that a 1ms budget is reliably exceeded, or assert "either completed or timed out, but never hung" with a hard outer wall-clock guard. Keep it deterministic — a 1ms timeout against any real subprocess startup reliably trips before the child can exit.)

   **Guard compliance:** this test references `ScaleTestSupport.RequireJulieServer()` (the single launch signal) and is `[Trait("Category","Scale")]` at the class level, so `ScaleTraitConventionTests` passes. It SKIPS if `.tools/julie-extract` is missing.

2. **Run** `scripts/test.sh scale` — red (the runner has no timeout ctor; `WaitForExit()` blocks).

3. **Green — edit `JulieExtractRunner.cs`:**
   - Add a `private readonly TimeSpan _timeout;` field and a `public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);`.
   - Overload the constructor: `public JulieExtractRunner(string binaryPath) : this(binaryPath, DefaultTimeout) {}` and `public JulieExtractRunner(string binaryPath, TimeSpan timeout)` storing `_timeout`. `Locate` passes `DefaultTimeout`.
   - In `Run` (`:314`), replace `process.WaitForExit();` with:

```csharp
if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
{
    try { process.Kill(entireProcessTree: true); }
    catch (InvalidOperationException) { /* already exited between the wait and the kill */ }
    process.WaitForExit(); // reap the killed child so the handle is released
    throw new JulieExtractException(
        $"julie-extract at '{_binaryPath}' timed out after {_timeout.TotalSeconds:0}s and was killed " +
        "(possible hang / wrong binary). Re-run scripts/restore-julie-extract.sh if this persists.",
        standardError: stderr.ToString().TrimEnd('\n', '\r'));
}
```

   Place the timeout check after `BeginOutputReadLine`/`BeginErrorReadLine` (so the async readers drain the pipes — keeps the existing deadlock fix intact).

4. **Run** `scripts/test.sh scale` — green (or skip if no binary). **Run** `scripts/test.sh` — fast suite unaffected (the timeout test is Scale-only).

**Acceptance:**
- A hung julie-extract is killed (process tree) after the bounded timeout and surfaces a `JulieExtractException` naming the timeout — bootstrap `StartAsync` can never wedge forever.
- The default 10-minute timeout does not kill a legitimate cold full scan; the Scale test passes a tiny timeout to force the path.
- The async stdout/stderr drain (the pipe-deadlock fix) is preserved.
- The new test is `[Trait("Category","Scale")]` and obtains the binary via `ScaleTestSupport`; `ScaleTraitConventionTests` stays green.

---

### Subsystem A exit checklist

- `scripts/test.sh` green (<30s); the rewritten `ExtractReportParsingTests`, `JulieExtractRunnerTests`, `JulieExtractRunnerUpdateDeleteTests`, `MillerExtractContractTests` (except the two G-dependent pins/script tests until G lands) pass.
- `scripts/test.sh scale` green or skipped (A6 timeout test).
- `dotnet build Miller.slnx -c Release` — 0 warnings / 0 errors.
- `grep -rn "PinnedJulieServerVersion\|--workspace-id\|\"extract\"\|ExtractError\b\|analysis_state\|julie-server" src/Miller.Indexing/JulieExtractRunner.cs src/Miller.Indexing/ExtractReport.cs src/Miller.Indexing/ExtractVersionMismatch.cs src/Miller.Indexing/MillerExtractContract.cs src/Miller.Indexing/JulieExtractExceptions.cs` returns empty.
- The `ExtractReport` convenience accessors (`Revision`, `CreatedRevision`, `SymbolsExtracted`, `FilesUpdated`, `FilesDeleted`, `Status`, `Errors`, `HashAlgorithm`) exist so subsystem D compiles; D owns the workspace_id-cross-check removal and the `flock_timeout`→`lock_timeout` transient-set fix.


---

## Subsystem B: Symbol reader + IndexedSymbol + test-signal column + path:line fixture

Scope: the `symbols`-table read seam only. Rewrite `SqliteSymbolReader`'s SELECT to v1 column names with **by-name** reads (D6), read the typed `symbols.is_test` column and **delete** `ParseTestSignals` (D4), reshape `IndexedSymbol` from `TestRole? TestRole` to a carried `bool IsTest`, and add the D6 `path:line` ordinal-guard fixture test. Freshness, annotations, bridge, body-slicing, report, bootstrap, and the fixture/`LargeDbWriter` rewrite are other subsystems — B coordinates, does not duplicate.

**Hard cross-subsystem ordering (read before starting):** B1 removes `IndexedSymbol.TestRole`. The only `src` consumer is `RepositoryIndexLoader.ProjectToSymbolDetails` at `RepositoryIndexLoader.cs:124` (`TestRole: symbol.TestRole`), which is **subsystem F's** to rewrite (it owns `SymbolDetail.cs` + `TestRole.cs`). B1 and F's `RepositoryIndexLoader`/`SymbolDetail` change are **co-requisite in the same branch** or the Release build is red. B2/B3/B4 read the v1 `JulieDbFixture`, which is **subsystem H's** to migrate; they cannot go green until H lands the v1 fixture (design §12 step 3 says fixtures + readers move together). The contract for what B needs from H is spelled out in B3.

---

### Task B1 — Reshape `IndexedSymbol`: drop `TestRole? TestRole`, carry `bool IsTest` from the v1 column

**Files**
- `/Users/murphy/source/miller/src/Miller.Indexing/IndexedSymbol.cs` (own)
- Coordinate: `/Users/murphy/source/miller/src/Miller.Indexing/RepositoryIndexLoader.cs:124` (subsystem F rewrites this)

**What.** Today `IndexedSymbol` (`IndexedSymbol.cs:13-25`) is a positional record ending in two test-signal params: `bool IsTest = false` (line 24) and `TestRole? TestRole = null` (line 25). v1 promotes the signal to a typed indexed `symbols.is_test INTEGER NOT NULL DEFAULT 0` column (`julie-extractors/.../schema.rs:93`) and **drops `test_role`** entirely (design §4.3, D4). Miller only ever used the test signal as a predicate. Remove the `TestRole` param; keep `IsTest` but make it a **required** positional (no default) so every construction is forced to supply the v1 value — a missing-signal silent-false is exactly the bug a required param prevents.

**Approach.** This is a record-signature change with two construction sites in `src` (`SqliteSymbolReader.Read` — B2; and the F-owned `RepositoryIndexLoader` consumer reads, not constructs) and several in tests. `MillerRepositoryIndex.cs:175` already reads `symbol.IsTest` (`new GraphNode(symbol.SymbolId, symbol.IsTest)`) — unaffected. The break is the `TestRole` removal.

**Steps (TDD).**
1. **Write/adjust the failing compile-contract first.** In `SqliteSymbolReaderTests.cs` the existing `Read_TestRole_FromMetadata_PopulatesRoleAndIsTest_CrossLanguage` test (lines 175-225) references `IndexedSymbol.TestRole` — it will no longer compile. That is the red signal. (B4 deletes/rewrites that test; do B1's record edit and B4's test edit together so the test project compiles.) Run `scripts/test.sh` and confirm the build fails ONLY on `TestRole` member references (expected red).
2. **Implement the record change.** Edit `IndexedSymbol.cs`:
   - Remove line 25 (`TestRole? TestRole = null) // ...`).
   - Change line 24 from `bool IsTest = false,  // ...` to a required, comment-updated param and close the param list on it:
     ```csharp
     bool IsTest)   // julie's typed symbols.is_test column (INTEGER NOT NULL, all 34 langs); see design D4
     ```
   - Update the type-doc on line 1-12 to drop the `TestRole` mention; keep the `using Miller.Core.Contracts;` only if still needed (after removing `TestRole`, check whether any other `Miller.Core.Contracts` type is referenced — `SearchableDocument` is `Miller.Core.Search`; if nothing else uses `Miller.Core.Contracts`, remove that `using` to keep the 0-warning build).
   - `ToSearchableDocument()` (lines 31-32) is unchanged (it already drops the signal).
3. Run `scripts/test.sh` — now the build fails on `RepositoryIndexLoader.cs:124` (`TestRole: symbol.TestRole`) and any test still passing `TestRole:`. This is the **F co-requisite boundary**: F changes `ProjectToSymbolDetails` to stop projecting `TestRole` (it rewrites `SymbolDetail`/`RouteBridge` per design §8). Do not patch F's files here — confirm with F that their change lands in the same branch.
4. After F's consumer change + B2/B4 land, run `dotnet build Miller.slnx -c Release` (0 warnings) and `scripts/test.sh` — green.

**Acceptance.**
- `IndexedSymbol` has `bool IsTest` as a **required** positional param and **no** `TestRole` member.
- `MillerRepositoryIndex.cs:175` and `SymbolGraphReader` still compile unchanged (they read `IsTest`).
- `dotnet build Miller.slnx -c Release` is 0 warnings / 0 errors after F's co-requisite consumer change.

---

### Task B2 — `SqliteSymbolReader`: v1 SELECT renames, by-name reads, typed `is_test`, delete `ParseTestSignals`

**Files**
- `/Users/murphy/source/miller/src/Miller.Indexing/SqliteSymbolReader.cs` (own)

**What.** The reader's SELECT (`SqliteSymbolReader.cs:40-45`) uses old julie column names (`id`, `file_path`, `parent_id`, `metadata`) and **positional** ordinal reads (`reader.GetString(0)` … `GetString(9)`, lines 52-61), then runs `ParseTestSignals` (lines 63, 94-127) — a JSON-parse + substring probe over `symbols.metadata`. v1 renames the columns and promotes the test signal to a typed `is_test` column (design §4.3 row "symbols.id/parent_id/file_path/metadata" + D4/D6). Rewrite the SELECT to v1 names, switch every read to **by-name** via `GetOrdinal` (D6 — permanently closes the silent column-drift trap), read `is_test` as a bool, and **delete** `ParseTestSignals` plus its now-dead `System.Text.Json` usage.

**Exact rename map (old SELECT → v1 SELECT):**

| Old column (`SqliteSymbolReader.cs:41`) | v1 column (`schema.rs`) | Old positional read | New by-name read |
|---|---|---|---|
| `id` | `symbol_id` (`:67`) | `GetString(0)` (`:52`) | `GetString(ord.SymbolId)` |
| `name` | `name` (`:71`) | `GetString(1)` (`:53`) | `GetString(ord.Name)` |
| `signature` | `signature` (`:73`) | `IsDBNull(2)?…GetString(2)` (`:54`) | `IsDBNull(ord.Signature)?…` |
| `kind` | `kind` (`:72`) | `GetString(3)` (`:55`) | `GetString(ord.Kind)` |
| `language` | `language` (`:70`) | `GetString(4)` (`:56`) | `GetString(ord.Language)` |
| `file_path` | `path` (`:69`) | `GetString(5)` (`:57`) | `GetString(ord.Path)` |
| `start_line` | `start_line` (`:77`, **NOT NULL**) | `IsDBNull(6)?0:GetInt32(6)` (`:58`) | `IsDBNull(ord.StartLine)?0:GetInt32(ord.StartLine)` |
| `end_line` | `end_line` (`:79`, **NOT NULL**) | `IsDBNull(7)?0:GetInt32(7)` (`:59`) | `IsDBNull(ord.EndLine)?0:GetInt32(ord.EndLine)` |
| `parent_id` | `parent_symbol_id` (`:76`) | `IsDBNull(8)?…GetString(8)` (`:60`) | `IsDBNull(ord.ParentSymbolId)?…` |
| `metadata` (→ parsed) | (gone for test signal) `is_test` (`:93`, NOT NULL) | `GetString(9)`+`ParseTestSignals` (`:61,63`) | `GetBoolean(ord.IsTest)` |

`ORDER BY file_path, start_line, id` (`:44`) → `ORDER BY path, start_line, symbol_id` (design §10 B + §4.3). `WHERE name IS NOT NULL` is unchanged (v1 `name` is NOT NULL but the predicate is harmless and matches the existing contract).

**Approach.** Resolve all ordinals once after `ExecuteReader()` (by-name `GetOrdinal` is O(1) cached per reader; resolving once avoids a per-row lookup over ~565k rows). Read `is_test` with `GetBoolean` — Microsoft.Data.Sqlite maps the `INTEGER` 0/1 to bool (verified against the v1 DDL `is_test INTEGER NOT NULL DEFAULT 0`). Keep the `IsDBNull`→0 guards on `start_line`/`end_line` even though v1 marks them NOT NULL: they are cheap and a drifted artifact must degrade, not crash the single startup pass — but update the trailing comments from "nullable -> 0" to "v1 NOT NULL; guard defensive".

**Representative new read block (replaces `:38-78`):**
```csharp
using var command = connection.CreateCommand();
// v1 columns. By-name reads (D6) decouple SELECT order from the GetX ordinals: a future column
// add/reorder can never silently shift a value into the wrong field again.
command.CommandText = """
    SELECT symbol_id, name, signature, kind, language, path,
           start_line, end_line, parent_symbol_id, is_test
    FROM symbols
    WHERE name IS NOT NULL
    ORDER BY path, start_line, symbol_id;
    """;

var results = new List<IndexedSymbol>();
int docId = 0;
using var reader = command.ExecuteReader();

// Resolve ordinals once (cheap, cached) — not per-row over ~565k startup rows.
int oSymbolId = reader.GetOrdinal("symbol_id");
int oName = reader.GetOrdinal("name");
int oSignature = reader.GetOrdinal("signature");
int oKind = reader.GetOrdinal("kind");
int oLanguage = reader.GetOrdinal("language");
int oPath = reader.GetOrdinal("path");
int oStartLine = reader.GetOrdinal("start_line");
int oEndLine = reader.GetOrdinal("end_line");
int oParent = reader.GetOrdinal("parent_symbol_id");
int oIsTest = reader.GetOrdinal("is_test");

while (reader.Read())
{
    string symbolId = reader.GetString(oSymbolId);
    string name = reader.GetString(oName);
    string? signature = reader.IsDBNull(oSignature) ? null : reader.GetString(oSignature);
    string kind = reader.GetString(oKind);
    string language = reader.GetString(oLanguage);
    string path = reader.GetString(oPath);
    int startLine = reader.IsDBNull(oStartLine) ? 0 : reader.GetInt32(oStartLine); // v1 NOT NULL; guard defensive
    int endLine = reader.IsDBNull(oEndLine) ? 0 : reader.GetInt32(oEndLine);       // v1 NOT NULL; guard defensive
    string? parentId = reader.IsDBNull(oParent) ? null : reader.GetString(oParent);
    bool isTest = reader.GetBoolean(oIsTest); // typed v1 column; replaces the metadata JSON-parse hack (D4)

    results.Add(new IndexedSymbol(
        DocId: docId++,
        SymbolId: symbolId,
        Name: name,
        Signature: signature,
        Kind: kind,
        Language: language,
        FilePath: path,
        StartLine: startLine,
        EndLine: endLine,
        ParentId: parentId,
        IsTest: isTest));
}
```

**Steps (TDD).**
1. **Run the existing reader tests first** (`scripts/test.sh`). Against the *current* old-schema fixture they pass; this captures the green baseline. The migration's red is driven by H's v1 fixture switch + B4's test rewrite — do B2 in the same branch as H/B4.
2. **Delete `ParseTestSignals`** (`SqliteSymbolReader.cs:83-127`) entirely, and the call at line 63. Remove the dead `System.Text.Json` references in that method (the file's only `System.Text.Json` usage is inside `ParseTestSignals`).
3. **Replace the SELECT + read loop** with the block above. Update the class/`Read` XML doc (`:6-28`): the "DocId is the 0-based ordinal of the SELECT order (file_path, start_line, id)" line (`:24`, `:39`) → "(path, start_line, symbol_id)". The `// v7.13.0 julie extract` phrasing in the `<exception>` doc (`:28`) → "v1 julie-extract artifact".
4. Run `scripts/test.sh`. With H's v1 fixture and B4's rewritten tests, the reader tests assert the v1 path. Run `dotnet build Miller.slnx -c Release` (0 warnings — verifies no orphaned `using System.Text.Json` if it was file-scoped).

**Acceptance.**
- SELECT uses v1 names (`symbol_id, name, signature, kind, language, path, start_line, end_line, parent_symbol_id, is_test`) and `ORDER BY path, start_line, symbol_id`.
- **Every** column read is by-name (`GetOrdinal`), zero positional `GetString(<int literal>)`.
- `is_test` read via `GetBoolean`; `ParseTestSignals` and its JSON-parse/substring code are deleted.
- `IndexedSymbol.IsTest` is populated from the column; no `TestRole`.
- `scripts/test.sh` green; `dotnet build Miller.slnx -c Release` 0 warnings.

---

### Task B3 — D6 `path:line` ordinal-guard fixture test (new fast-suite test)

**Files**
- `/Users/murphy/source/miller/tests/Miller.Tests/Indexing/SymbolReaderPathLineFixtureTests.cs` (**new**, own)
- Depends on: H's v1 `JulieDbFixture` (this task specifies the exact fixture surface B needs)

**What.** Design D6 (§3, §10 B, §14) requires "a fixture test asserting exact `path:line` for a known symbol." The existing reader tests assert ordering and NULL discipline but never pin a concrete `(FilePath, StartLine, EndLine)` triple for a named symbol — that is precisely the silent column-drift trap by-name reads close, and the assertion that proves a `path`/`start_line`/`end_line` rename didn't cross-wire. Add a dedicated fast-suite test that builds a minimal v1 fixture and asserts the exact path and line.

**Fixture-surface contract B needs from subsystem H (state in H's task):** `JulieDbFixture.Create(...)` must, after the v1 migration, write each `SymbolRow` into the v1 `symbols` table with `path` (from `SymbolRow.FilePath`), `start_line`, `end_line`, and a **typed `is_test` column** sourced from a new `SymbolRow.IsTest` bool init-prop (default false). B3 uses only the existing `SymbolRow(Id, Name, Kind, Language, FilePath, Signature, StartLine, ParentId)` ctor plus `{ EndLine = … }`; no new fixture API beyond H's planned `is_test` writer. If H has not yet added the typed `is_test` writer, B3 still passes (it does not assert `is_test`), but B4 needs it.

**Approach.** Self-contained fixture with two rows in two files so the test pins both the path and the line against a known, non-ambiguous symbol — and a second row at a different line in a path that sorts earlier, so the assertion is sensitive to a `path`↔`start_line` swap (a cross-wire would surface the wrong line for the named symbol).

**Steps (TDD).**
1. **Write the failing test first.** Create `SymbolReaderPathLineFixtureTests.cs`:
   ```csharp
   using Miller.Indexing;
   using Xunit;

   namespace Miller.Tests.Indexing;

   /// <summary>
   /// D6 ordinal guard: pins the EXACT (FilePath, StartLine, EndLine) the reader projects for a named
   /// symbol against a v1 fixture. This is the assertion that proves the by-name SELECT (path/start_line/
   /// end_line/symbol_id) never cross-wires a column — a positional drift or a path↔start_line swap would
   /// surface the wrong line here. Fast suite, no julie-extract spawn.
   /// </summary>
   public sealed class SymbolReaderPathLineFixtureTests
   {
       [Fact]
       public void Read_KnownSymbol_HasExactPathAndLine()
       {
           using var fx = JulieDbFixture.Create(
               JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
               {
                   // A symbol at a precise path:line, with a whole-span end_line. A column cross-wire
                   // (path vs start_line vs end_line) makes one of these three assertions fail loudly.
                   new JulieDbFixture.SymbolRow("aa00000000000000000000000000d601", "AuthGuard", "class",
                       "csharp", "src/Security/AuthGuard.cs", "public sealed class AuthGuard", 42, null)
                       { EndLine = 73 },
                   // A second row in a path that sorts EARLIER and a different line, so the SELECT ordering
                   // (path, start_line, symbol_id) is exercised and the named lookup can't accidentally
                   // match the wrong row.
                   new JulieDbFixture.SymbolRow("aa00000000000000000000000000d602", "AbcWidget", "class",
                       "csharp", "src/Aaa/Widget.cs", "public class AbcWidget", 7, null)
                       { EndLine = 19 },
               });

           var symbols = SqliteSymbolReader.Read(fx.DbPath);
           var guard = Assert.Single(symbols, s => s.Name == "AuthGuard");

           Assert.Equal("src/Security/AuthGuard.cs", guard.FilePath); // exact path (the `path` column)
           Assert.Equal(42, guard.StartLine);                         // exact 1-based start_line
           Assert.Equal(73, guard.EndLine);                           // exact whole-span end_line

           // The earlier-sorting row must precede AuthGuard under ORDER BY path, start_line, symbol_id.
           Assert.True(symbols.FindIndex(s => s.Name == "AbcWidget")
                       < symbols.FindIndex(s => s.Name == "AuthGuard"));
       }
   }
   ```
   (`symbols` is `IReadOnlyList<IndexedSymbol>`; if `FindIndex` is unavailable on the interface, use `symbols.ToList().FindIndex(...)` or `Array.FindIndex` over `symbols.ToArray()` — keep the index-ordering assertion.)
2. Run `scripts/test.sh`. Against the **old** fixture (pre-H) this compiles and may pass trivially; against H's **v1** fixture it asserts the real v1 read path. If B2's rename is wrong (e.g. `path` mis-mapped), one of the three `Assert.Equal` lines fails — the guard working as designed.
3. No implementation step (the implementation is B2). This task is the test artifact + its place in the fast suite.
4. Run `scripts/test.sh` — green; confirm it runs in the fast (`Category!=Scale`) suite (no trait, no julie spawn).

**Acceptance.**
- New fast-suite test asserts exact `FilePath == "src/Security/AuthGuard.cs"`, `StartLine == 42`, `EndLine == 73` for the named symbol.
- Asserts the earlier-sorting path precedes it (locks `ORDER BY path, start_line, symbol_id`).
- No `[Trait("Category","Scale")]`, no `ScaleTestSupport`, no subprocess — runs in `scripts/test.sh`.

---

### Task B4 — Migrate `SqliteSymbolReaderTests` off `TestRole`/metadata-JSON onto the typed `is_test` column

**Files**
- `/Users/murphy/source/miller/tests/Miller.Tests/Indexing/SqliteSymbolReaderTests.cs` (own)
- Depends on: H's v1 fixture exposing a typed `SymbolRow.IsTest` writer

**What.** Two existing tests encode the deleted metadata-JSON test-signal contract and must be replaced with the typed-column contract:
- `Read_IsTest_FromMetadata_IsCrossLanguage` (`:129-173`) — seeds `Metadata = "{\"is_test\":true}"` etc. and asserts `IsTest`. Under v1 the signal is a typed column, not parsed from JSON; the substring-trap / malformed-JSON cases (`this_test_helper`, `broken`, lines 154-159, 171-172) are **no longer reachable** (no JSON parse exists) and must be removed, not kept as dead asserts.
- `Read_TestRole_FromMetadata_PopulatesRoleAndIsTest_CrossLanguage` (`:175-225`) — references `IndexedSymbol.TestRole`, which B1 deletes. Remove this test entirely (`test_role` is gone in v1; design §8/D4 says delete the TestRole cases).

Other tests in the file (`Read_ProjectsRowsInDeterministicFilePathStartLineIdOrder…`, `Read_NullSignature…`, `Read_NullStartLine…`, `Read_EndLine…`, `Read_NullParentId…`, `Read_CarriesKindLanguageAndRelativeFilePath`, `ToSearchableDocument…`, `Read_IncompatibleSchema…`, `Read_NonWritableDbDirectory…`, `Read_MissingDbFile…`) stay; they assert reader behavior orthogonal to the test signal and pass once H's v1 fixture + B2 land. Note `Read_ProjectsRows…` (`:27-32`) computes `expectedOrder` by `OrderBy(FilePath).ThenBy(StartLine).ThenBy(Id)` — that already matches the v1 `ORDER BY path, start_line, symbol_id`, so it is correct as-is (the `.Id`/`.FilePath` here are `SymbolRow` properties, unchanged by H). The doc comment on `:9-11` mentioning "file_path,start_line,id ordering" should be updated to the v1 names.

**Approach.** Replace the JSON-seeded `is_test` test with a typed-column equivalent: seed rows with the new `SymbolRow.IsTest` bool (H's writer) and assert `IndexedSymbol.IsTest` reads `true`/`false` straight from the column — cross-language (go/python/csharp positives, a negative). Drop the JSON-only edge cases (substring trap, malformed JSON) because the parse path no longer exists; replacing them with column reads is the honest v1 contract.

**Steps (TDD).**
1. **Rewrite `Read_IsTest_FromMetadata_IsCrossLanguage`** → `Read_IsTest_FromTypedColumn_IsCrossLanguage`:
   ```csharp
   [Fact]
   public void Read_IsTest_FromTypedColumn_IsCrossLanguage()
   {
       // v1 promotes the cross-language test signal to a typed symbols.is_test column (INTEGER NOT NULL).
       // Miller reads it directly (no metadata JSON parse). Verified across go/python/csharp positives + a
       // negative; the old JSON substring-trap/malformed cases are gone with ParseTestSignals (D4).
       using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
       {
           new JulieDbFixture.SymbolRow("a0000000000000000000000000000001", "TestAdd", "function", "go",
               "math/add_test.go", "func TestAdd(t *testing.T)", 3, null) { IsTest = true },
           new JulieDbFixture.SymbolRow("a0000000000000000000000000000002", "test_add", "function", "python",
               "calc/test_calc.py", "def test_add()", 2, null) { IsTest = true },
           new JulieDbFixture.SymbolRow("a0000000000000000000000000000003", "Adds", "method", "csharp",
               "Tests/CalcTests.cs", "public void Adds()", 9, null) { IsTest = true },
           // Non-test: column defaults to 0/false.
           new JulieDbFixture.SymbolRow("b0000000000000000000000000000001", "Add", "function", "go",
               "math/add.go", "func Add(a, b int) int", 5, null),
           new JulieDbFixture.SymbolRow("b0000000000000000000000000000002", "helper", "function", "python",
               "calc/util.py", "def helper()", 1, null) { IsTest = false },
       });

       var symbols = SqliteSymbolReader.Read(fx.DbPath);
       bool IsTestOf(string name) => symbols.Single(s => s.Name == name).IsTest;

       Assert.True(IsTestOf("TestAdd"), "go test → is_test column true");
       Assert.True(IsTestOf("test_add"), "python test → is_test column true");
       Assert.True(IsTestOf("Adds"), "csharp test → is_test column true");
       Assert.False(IsTestOf("Add"), "default column 0 → not a test");
       Assert.False(IsTestOf("helper"), "explicit is_test=0 → not a test");
   }
   ```
2. **Delete** `Read_TestRole_FromMetadata_PopulatesRoleAndIsTest_CrossLanguage` (`:175-225`) in full.
3. Update the file-level doc (`:9-11`) ordering phrasing `file_path,start_line,id` → `path,start_line,symbol_id`.
4. Run `scripts/test.sh`. Co-requisite: this needs H's `SymbolRow.IsTest` writer; flag to H that B4 consumes `SymbolRow.IsTest` (bool, writes the typed `is_test` column, default false). With B1+B2+H landed, the file compiles (no `TestRole`) and the `is_test` test reads the typed column.
5. Run `dotnet build Miller.slnx -c Release` (0 warnings) and `scripts/test.sh` — green.

**Acceptance.**
- No reference to `IndexedSymbol.TestRole`, `Metadata = "{...is_test...}"`, or the substring/malformed-JSON cases remains in the test.
- `is_test` is asserted via the typed `SymbolRow.IsTest` writer → `IndexedSymbol.IsTest`, cross-language (go/python/csharp positive + negative).
- `Read_TestRole_…` deleted; remaining reader tests unchanged and green.
- `scripts/test.sh` green; `dotnet build Miller.slnx -c Release` 0 warnings.

---

**Commit boundary for B.** Because B1 (record shape) and F's `RepositoryIndexLoader`/`SymbolDetail` change are co-requisite, and B2/B3/B4 depend on H's v1 fixture, B lands as part of design §12 step 3's atomic "read layer + fixtures move together" commit — not standalone. Within that, B's own commit message: "Read layer: v1 symbols SELECT (by-name, typed is_test), IndexedSymbol bool IsTest, D6 path:line guard." Commit only when the user asks; if on `main`, branch first.


---

## Subsystem C: Remaining readers + file-content disk re-source

This subsystem ports the four remaining SQLite readers and the schema gate onto the julie-extract v1 contract, and replaces the now-gone `files.content` read with a disk re-source that enforces the hard freshness invariant (design §7). All readers adopt by-name column reads (D6) where they are touched. Verified against the v1 schema at `/Users/murphy/source/julie-extractors/crates/julie-extract-artifact/src/schema.rs` and the metadata keys at `crates/julie-extract-artifact/src/metadata.rs`.

**Ordering inside C:** C1 (gate) first — every other reader calls `JulieSchemaGate.Verify` and the fast suite won't open a v1 fixture until the gate accepts it. Then C2/C6/C7 (mechanical-ish renames), then C5 (bridge restructure), then C3+C4 together (disk re-source + InspectTool plumbing) — C3/C4 are gated on subsystem H writing fixture files to disk.

**Constant-name note:** subsystem A renames the `MillerExtractContract` constants. This plan writes the FINAL names (`ExpectedSqliteSchemaVersion`, `ExpectedExtractContractVersion`, `ExpectedHashAlgorithm`, `PinnedJulieExtractVersion`). If A has not landed when you start, use the current names (`ExpectedSchemaVersion`, `PinnedJulieServerVersion`) and switch in the same PR — do not invent a parallel constant.

---

### Task C1 — Rewrite JulieSchemaGate onto v1 `artifact_metadata` (drop the `schema_version` table)

**Files:** `src/Miller.Indexing/JulieSchemaGate.cs`; tests `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs`.

**What:** Today (`JulieSchemaGate.cs:58-71`) the gate reads `SELECT COALESCE(MAX(version),0) FROM schema_version;` — that table is GONE in v1 (verified: not in `schema.rs`; the only metadata surface is `artifact_metadata`). The gate must read the schema version from `artifact_metadata` key `sqlite_schema_version`, the contract version from `extract_contract_version`, and `hash_algorithm` — all from one table — and stop referencing `external_extract_metadata` and the dead `schema_version` table. This is the "first failure point today" (design §4.3 line 115).

**Why this is non-mechanical:** the schema-version source changes from a table-MAX query to a metadata-key lookup, the missing-table error path collapses (one table instead of two), and every error string that says `julie-server` / `extract scan` / `external_extract_metadata` must change. The control flow (three checks, isNewer branch) is preserved.

**v1 facts (verified):**
- `artifact_metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL)` — no `updated_at` (schema.rs:13-16).
- Keys written: `artifact_id`, `root_path`, `schema_version`, `extract_contract_version`, `sqlite_schema_version`, `hash_algorithm` (metadata.rs:36-45). `sqlite_schema_version` and `schema_version` both carry `1`; the design picks `sqlite_schema_version` as the gate key.
- No `workspace_id` key exists.

**Steps (TDD):**

1. **Write the failing tests.** Rewrite `JulieSchemaGateTests.cs`. The fixture API stays `JulieDbFixture.Create(schema, contract, rows, ...)` (H migrates its internals so `schema`/`contract` now seed `artifact_metadata` keys, no `schema_version` table). The pin-relative structure is preserved. Change the constant aliases at the top to the new names, and rewrite the two "missing table" tests to target `artifact_metadata`:

   ```csharp
   private static readonly long PinSchema = MillerExtractContract.ExpectedSqliteSchemaVersion;
   private static readonly long PinContract = MillerExtractContract.ExpectedExtractContractVersion;
   private static readonly string PinnedVer = MillerExtractContract.PinnedJulieExtractVersion;

   [Fact]
   public void Verify_AtPinnedSchemaAndContract_DoesNotThrow()
   {
       using var fx = JulieDbFixture.Create(PinSchema, PinContractStr, NoRows);
       using var conn = OpenReadOnly(fx.DbPath);
       JulieSchemaGate.Verify(conn); // no throw == compatible
   }

   [Fact]
   public void Verify_NewerSchema_ThrowsNamingTheValueAndPointsAtUpgrade()
   {
       using var fx = JulieDbFixture.Create(PinSchema + 1, PinContractStr, NoRows);
       using var conn = OpenReadOnly(fx.DbPath);
       var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
       Assert.Contains(S(PinSchema + 1), ex.Message);
       Assert.Contains(S(PinSchema), ex.Message);
       Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
       Assert.Contains("upgrade Miller", ex.Message, StringComparison.OrdinalIgnoreCase);
   }

   // The non-julie / corrupt-DB case now hinges on artifact_metadata being absent,
   // not the dead schema_version table.
   [Fact]
   public void Verify_MissingMetadataTable_ThrowsNamingArtifactMetadata()
   {
       using var fx = JulieDbFixture.Create(PinSchema, null, NoRows, createMetadataTable: false);
       using var conn = OpenReadOnly(fx.DbPath);
       var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
       Assert.Contains("artifact_metadata", ex.Message);
       Assert.Contains($"not a julie-extract", ex.Message, StringComparison.OrdinalIgnoreCase);
   }

   [Fact]
   public void Verify_MissingSqliteSchemaVersionKey_ThrowsNamingTheKey()
   {
       // Table present, but the sqlite_schema_version row absent (older/corrupt artifact).
       using var fx = JulieDbFixture.Create(schemaVersion: null, PinContractStr, NoRows, createMetadataTable: true);
       using var conn = OpenReadOnly(fx.DbPath);
       var ex = Assert.Throws<IncompatibleExtractException>(() => JulieSchemaGate.Verify(conn));
       Assert.Contains("sqlite_schema_version", ex.Message);
   }
   ```

   Keep the existing newer/older-contract, non-integer-contract, missing-hash-algorithm, and wrong-hash-algorithm tests (they already target `hash_algorithm` and contract keys; just update the constant names and any `external_extract_metadata` / `extract scan` / `v{7.13.2}` assertions to `artifact_metadata` / `scan` / `v{1}`-pin wording). Delete `Verify_MissingSchemaVersionTable_ThrowsNamingTheTable` (the table no longer exists) — replaced by `Verify_MissingMetadataTable_ThrowsNamingArtifactMetadata`.

2. **Run** `scripts/test.sh` — the gate tests fail to compile/assert (old gate reads `schema_version` table, new constants don't exist yet). Expected red.

3. **Implement.** Rewrite `JulieSchemaGate.cs`:
   - Delete `ReadSchemaVersion` (the `schema_version`-table query, :58-71).
   - Add `ReadSchemaVersion(connection)` reading from metadata: reuse `ReadRequiredMetadataValue(connection, "sqlite_schema_version", expected)` and `long.TryParse` it (same shape as `ReadContractVersion`). A non-integer value → typed error naming it.
   - Point `ReadRequiredMetadataValue` at `artifact_metadata` (was `external_extract_metadata`, :92) and at the `IsMissingTable(ex, "artifact_metadata")` branch (:99).
   - First check in `Verify` becomes the metadata-sourced schema check; the missing-`artifact_metadata` table is detected on the FIRST metadata read and reported once.
   - Error strings: `external_extract_metadata` → `artifact_metadata`; `julie-server (v{PinnedJulieServerVersion})` → `julie-extract`; `extract scan` → `scan`; the "not a v{...} julie extract" phrasing → "not a julie-extract v1 artifact". Use `MillerExtractContract.PinnedJulieExtractVersion` only where a restore-download version is meant (the older-schema/restore path), and gate on `ExpectedSqliteSchemaVersion` / `ExpectedExtractContractVersion`.

   Representative new metadata read:
   ```csharp
   private static long ReadSchemaVersion(SqliteConnection connection)
   {
       string text = ReadRequiredMetadataValue(
           connection, "sqlite_schema_version",
           MillerExtractContract.ExpectedSqliteSchemaVersion.ToString(CultureInfo.InvariantCulture));
       if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
           throw new IncompatibleExtractException(
               $"DB has a non-integer sqlite_schema_version value '{text}'; it is not a valid julie-extract v1 artifact.");
       return value;
   }
   ```

4. **Run** `scripts/test.sh` — gate tests green.

5. **Commit** `migrate(C1): gate on artifact_metadata.sqlite_schema_version, drop schema_version table`.

**Acceptance:**
- `JulieSchemaGate` reads schema + contract + hash from `artifact_metadata`; no reference to `schema_version` table or `external_extract_metadata` anywhere in the file.
- Newer/older schema, newer/older contract, non-integer contract, missing `artifact_metadata` table, missing `sqlite_schema_version` key, missing/wrong `hash_algorithm` each throw `IncompatibleExtractException` naming the offending value.
- Error strings say `julie-extract` / `scan` (never `julie-server` / `extract scan`).
- `scripts/test.sh` green, `dotnet build Miller.slnx -c Release` 0 warnings.

---

### Task C2 — ExtractReader column/table renames + drop `ReadWorkspaceId` (mechanical, by-name reads)

**Files:** `src/Miller.Indexing/ExtractReader.cs`; `src/Miller.Indexing/SymbolDetail.cs` (drop the dead `CodeContext` field — #11); tests `tests/Miller.Tests/Indexing/ExtractReaderTests.cs`.

**What:** Rename every `symbols`/`identifiers`/`artifact_metadata` column the reader touches to v1 names, switch positional reads to by-name reads (D6), DROP `ReadWorkspaceId` (`:235-243`) — v1 has no `workspace_id` metadata key (verified absent in metadata.rs) — and ADD `ReadRootPath` (reconciliation #14, consumed by E1). Also DROP the `code_context` column from `ReadDetail`'s SELECT and remove the now-dead `Indexing.SymbolDetail.CodeContext` field: v1's `symbols` table has no `code_context` (it moved to `identifiers`), and the only consumers are the `ReadDetail` projection + two test asserts (`ExtractReaderTests.cs:26,43`) + the fixture column (H drops it) — reconciliation #11. The body-slice methods (`ReadBody`/`ReadFileContent`) and the deletion of `ReadIndexedFileText` are handled in C3, not here — C2 leaves them temporarily compiling against the old `files.content` (the suite still has the old fixture only until H lands; sequence C2 to NOT touch the content path).

**Scope boundary:** C2 covers `ReadDetail` (incl. the `code_context` drop), `ReadEditSpan`, `ReadReferences`, `ReadCallees`, `ReadIdentifierSites`, removing `ReadWorkspaceId`, and adding `ReadRootPath`. C3 covers `ReadBody`/`ReadFileContent` (disk re-source) and the deletion of `ReadIndexedFileText`.

**Exact rename map (file:line → old → new):**

| Method | line | OLD column / clause | NEW (v1) | by-name read change |
|---|---|---|---|---|
| `ReadDetail` SELECT | 31-33 | selects `code_context` + `WHERE id = $id` | **DROP `code_context`** (gone from v1 `symbols` — it lives only on `identifiers` now; reconciliation #11) + `WHERE symbol_id = $id` | columns by name via `GetOrdinal`; also remove the `CodeContext:` projection at `:44` |
| `ReadEditSpan` SELECT | 71 | `WHERE id = $id` | `WHERE symbol_id = $id` | by name |
| `ReadRootPath` (NEW) | n/a | — (replaces the deleted `ReadWorkspaceId` for E1's identity compare, #14) | `SELECT value FROM artifact_metadata WHERE key='root_path'` → bare string or `null` if absent | by name; C-owned, consumed by E1 |
| `ReadReferences` SELECT | 98-101 | `file_path`, `ORDER BY file_path, start_line, id` | `path`, `ORDER BY path, start_line, identifier_id` | by name |
| `ReadCallees` SELECT | 117-120 | `file_path`, `ORDER BY file_path, start_line, id` | `path`, `ORDER BY path, start_line, identifier_id` | by name |
| `ReadIdentifierSites` SELECT | 157-161 | `file_path`, `ORDER BY file_path, start_byte` | `path`, `ORDER BY path, start_byte` (no `id` tiebreaker needed; already absent) | by name |
| `ReadWorkspaceId` | 235-243 | `SELECT value FROM external_extract_metadata WHERE key='workspace_id'` | **DELETE the method** | n/a |

Note: `SymbolRef.FilePath` / `IdentifierSite.FilePath` record property names stay `FilePath` (Miller's internal contract) — only the SQL column name `file_path`→`path` changes. The reader projects v1 `path` INTO the `FilePath` field.

**By-name read pattern** (apply to every SELECT in this reader). Replace positional `reader.GetString(0)` etc. with ordinals captured once:

```csharp
using var reader = command.ExecuteReader();
int oName = reader.GetOrdinal("name");
int oKind = reader.GetOrdinal("kind");
int oPath = reader.GetOrdinal("path");
int oStartLine = reader.GetOrdinal("start_line");
int oCid = reader.GetOrdinal("containing_symbol_id");
var results = new List<SymbolRef>();
while (reader.Read())
{
    results.Add(new SymbolRef(
        Name: reader.GetString(oName),
        Kind: reader.GetString(oKind),
        FilePath: reader.GetString(oPath),
        StartLine: reader.IsDBNull(oStartLine) ? 0 : reader.GetInt32(oStartLine),
        ContainingSymbolId: reader.IsDBNull(oCid) ? null : reader.GetString(oCid)));
}
```

**Steps (TDD):**

1. **Update the failing tests.** In `ExtractReaderTests.cs`:
   - DELETE `ReadWorkspaceId_ReturnsTheMetadataValue` (:153-158) and `ReadWorkspaceId_AbsentKey_ReturnsNull` (:160-165) — the method is gone.
   - The remaining `ReadDetail`/`ReadReferences`/`ReadCallees` tests run unchanged against the v1 fixture (H migrates `JulieDbFixture` so `CreateForInspect` emits v1 columns). Add an explicit by-name regression test that locks the rename so a future positional reshuffle is caught:
   ```csharp
   [Fact]
   public void ReadReferences_ProjectsV1PathColumn_NotPositional()
   {
       using var fx = JulieDbFixture.CreateForInspect();
       var refs = ExtractReader.ReadReferences(fx.DbPath, "GetUser");
       // path column (v1) is surfaced as FilePath; if the reader read by ordinal off a reshaped row
       // this would read the wrong column and the path would not match.
       Assert.Contains(refs, r => r.FilePath == "web/Controller.cs" && r.StartLine == 4);
       Assert.Contains(refs, r => r.FilePath == "auth/Repo.cs" && r.StartLine == 9);
   }
   ```

2. **Run** `scripts/test.sh` — fails (old SQL hits `no such column: file_path`/`id` against the v1 fixture; `ReadWorkspaceId` test references a deleted method). Expected red.

3. **Implement** the rename map + by-name reads in `ExtractReader.cs`. Delete `ReadWorkspaceId`. Leave `ReadFileContent`/`ReadBody`/`ReadIndexedFileText` for C3 (they still reference `files.content`; C3 re-sources `ReadBody`/`ReadFileContent` from disk and deletes `ReadIndexedFileText`, removing the temporary breakage — sequence C3 immediately after).

4. **Run** `scripts/test.sh` — C2 reader tests green (C3 body tests may be red until C3 lands; that is expected within the atomic migration — do not declare C2 "done" in isolation if `ReadBody` tests fail, note they are C3's).

5. **Commit** `migrate(C2): ExtractReader v1 column renames + by-name reads, drop ReadWorkspaceId`.

**Acceptance:**
- All `ExtractReader` SELECTs touched here use v1 columns (`symbol_id`, `path`, `identifier_id`) and by-name reads.
- `ReadWorkspaceId` is deleted **atomically with E1's removal of its two `src` callers** (`IndexBootstrapService.cs:117,150`, migrated to `ReadRootPath`) **and the 5 scale-test caller swaps** (reconciliation #17) — all in ONE Phase-3 commit (the method cannot be deleted while any caller remains, in `src` OR the compiled-but-filtered scale tests). After it, `grep -rn "ReadWorkspaceId" src tests` is empty.
- The by-name regression test passes; build 0 warnings.

---

### Task C3 — ExtractReader disk re-source of body text with the hard freshness invariant (+ delete dead `ReadIndexedFileText`)

**Files:** `src/Miller.Indexing/ExtractReader.cs`; tests `tests/Miller.Tests/Indexing/ExtractReaderTests.cs` and `tests/Miller.Tests/Indexing/ExtractReaderEditTests.cs` (the latter only to DELETE the four `ReadIndexedFileText_*` methods — `:142-150`, `:152-165`, `:167-172`, `:192-198` — by NAME, not as a block; see the deletion note in step 3). Depends on subsystem D (`ContentHasher`, content_hash prefix normalizer) and subsystem H (fixture writes files to disk + exposes root + blake3-prefixed `content_hash`).

**What:** `files.content` is GONE in v1 (verified: `files` has `content_hash`/`content_bytes`, no `content` — schema.rs:52-64). `ReadFileContent` (`:245-253`) and `ReadBody` (`:201-229`) must re-source body text from DISK by the symbol's byte span, and BEFORE slicing must hash the on-disk file with BLAKE3, strip the `blake3:` prefix from the stored `content_hash`, and compare. On mismatch they MUST NOT slice stale bytes (design §7 hard invariant) — return a staleness signal instead. **`ReadIndexedFileText` (`:189-193`) is DELETED, not migrated (reconciliation #16d).** Its sole production caller is `FreshnessGate.cs:70`, which D5 stops calling (v1 has no stored snapshot text, so the gate's exact-text tiebreaker is gone — freshness is a pure `content_hash` compare). After D5, `ReadIndexedFileText` has zero `src/` callers (verified: `grep -rn ReadIndexedFileText src` → only `FreshnessGate.cs:70` + the definition), so migrating it to a 3-arg disk reader would be migrating dead code. Delete the method and its tests instead.

**Why non-mechanical:** the data source moves from SQLite TEXT to the filesystem; the methods need the workspace root (relative paths resolve against it); and a new freshness guard sits in front of every slice. The signatures change.

**New signatures:**
- `ReadFileContent` becomes a private disk read + freshness check that takes the workspace root and the indexed relative path:
  ```csharp
  // Returns the verified on-disk text, or a sentinel telling the caller the file drifted.
  private static FileContentResult ReadVerifiedFileContent(string dbPath, string workspaceRoot, string relPath);

  internal readonly record struct FileContentResult(string? Text, bool Stale);
  ```
- `ReadBody` gains `workspaceRoot`:
  ```csharp
  public static string? ReadBody(
      string dbPath, string workspaceRoot, string filePath,
      int? startByte, int? endByte, int? startLine, int? endLine);
  ```
  When the file drifted (`content_hash` mismatch), `ReadBody` returns `null` (the InspectTool then renders "(body unavailable — file changed since index; run workspace refresh)"). When fresh, slice by byte span exactly as today (the `SliceByBytes`/`SliceByLines` helpers are reused verbatim — they operate on the now-disk text).
- `ReadIndexedFileText` (`:189-193`) is **DELETED** (reconciliation #16d). It existed only to feed `FreshnessGate`'s exact-text tiebreaker, which v1 cannot support (no stored snapshot text) — D5 drops the gate's call and the gate becomes a pure `content_hash` compare (`indexedText: null`; `StalenessCheck.cs:83` only tiebreaks when text is on BOTH sides, so null is safe, not auto-Stale). With the gate call gone there are no `src/` callers, so the method is removed rather than migrated to a 3-arg disk reader. The prefix-strip lives ONCE in the shared `ContentHasher.NormalizeHash` (D1, reconciliation #1), used by `ReadVerifiedFileContent` (for `ReadBody`) and `FreshnessGate` (D5).

**Freshness invariant implementation (the load-bearing part):**
```csharp
private static FileContentResult ReadVerifiedFileContent(string dbPath, string workspaceRoot, string relPath)
{
    // D2's ReadFileHash already returns BARE hex (it strips julie's "blake3:" prefix via ContentHasher.NormalizeHash).
    string? storedHash = ExtractFileHashReader.ReadFileHash(dbPath, relPath); // bare hex (reconciliation #1/#20)
    if (string.IsNullOrWhiteSpace(storedHash))
        return new FileContentResult(Text: null, Stale: true); // no manifest entry -> treat as not-readable

    string abs = Path.IsPathRooted(relPath) ? relPath : Path.Combine(workspaceRoot, relPath);
    if (!File.Exists(abs))
        return new FileContentResult(Text: null, Stale: true);

    byte[] bytes = File.ReadAllBytes(abs);
    string diskHash = ContentHasher.Blake3Hex(bytes);                 // bare hex (ContentHasher, subsystem D)
    // Idempotent belt-and-suspenders in case a caller ever passes a still-prefixed hash; blake3-only (reconciliation #9).
    if (!StringComparer.OrdinalIgnoreCase.Equals(diskHash, ContentHasher.NormalizeHash(storedHash)))
        return new FileContentResult(Text: null, Stale: true);        // HARD INVARIANT: never slice drifted bytes

    return new FileContentResult(Text: Encoding.UTF8.GetString(bytes), Stale: false);
}
```
(The single normalizer is `ContentHasher.NormalizeHash` — subsystem D1, reconciliation #1. It strips `blake3:` ONLY; never feed it a `sha256:` value. Do NOT inline a second `Substring("blake3:".Length)` here, and do NOT reference a non-existent `ContentHashNormalizer`.)

**Steps (TDD):**

1. **Write the failing tests.** Rewrite the `ReadBody` block in `ExtractReaderTests.cs` (`:95-151`) to require the disk path + freshness guard. The fixture (H) now writes `auth/UserService.cs` to disk under `fx.WorkspaceRoot` with `content_hash = "blake3:" + blake3hex(UserServiceContent)`:

   ```csharp
   [Fact]
   public void ReadBody_FreshFile_SlicesByteRangeFromDisk()
   {
       using var fx = JulieDbFixture.CreateForInspect();
       var detail = ExtractReader.ReadDetail(fx.DbPath, JulieDbFixture.GetUserId)!;

       string? body = ExtractReader.ReadBody(
           fx.DbPath, fx.WorkspaceRoot, "auth/UserService.cs",
           detail.BodyStartByte, detail.BodyEndByte, detail.BodyStartLine, detail.BodyEndLine);

       Assert.NotNull(body);
       Assert.StartsWith("public User GetUser(int id)", body);
       Assert.Contains("return _repo.Find(id);", body!);
       Assert.EndsWith("}", body.TrimEnd());
   }

   [Fact]
   public void ReadBody_DriftedFile_ReturnsNull_NeverSlicesStaleBytes()
   {
       using var fx = JulieDbFixture.CreateForInspect();
       var detail = ExtractReader.ReadDetail(fx.DbPath, JulieDbFixture.GetUserId)!;

       // Mutate the on-disk file so its blake3 no longer matches the stored content_hash.
       string abs = Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs");
       File.WriteAllText(abs, "// completely different file\nclass X {}\n");

       string? body = ExtractReader.ReadBody(
           fx.DbPath, fx.WorkspaceRoot, "auth/UserService.cs",
           detail.BodyStartByte, detail.BodyEndByte, detail.BodyStartLine, detail.BodyEndLine);

       // Hard invariant (design §7): the stored byte offsets address the INDEXED content; slicing them out of
       // the drifted file would return the WRONG bytes. The reader must refuse and signal staleness.
       Assert.Null(body);
   }

   [Fact]
   public void ReadBody_MissingDiskFile_ReturnsNull()
   {
       using var fx = JulieDbFixture.CreateForInspect();
       File.Delete(Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs"));
       string? body = ExtractReader.ReadBody(
           fx.DbPath, fx.WorkspaceRoot, "auth/UserService.cs",
           startByte: 0, endByte: 10, startLine: 1, endLine: 1);
       Assert.Null(body);
   }

   [Fact]
   public void ReadBody_NoByteAndNoLineSpans_ReturnsNull()
   {
       using var fx = JulieDbFixture.CreateForInspect();
       Assert.Null(ExtractReader.ReadBody(
           fx.DbPath, fx.WorkspaceRoot, "auth/UserService.cs",
           startByte: null, endByte: null, startLine: null, endLine: null));
   }

   ```
   (No `ReadIndexedFileText` tests are added — the method is deleted, reconciliation #16d. The existing
   `ExtractReaderEditTests.cs` `ReadIndexedFileText_*` cases are REMOVED in this task — all FOUR methods
   (`:142-150`, `:152-165`, `:167-172`, `:192-198`) plus the `// ---- ReadIndexedFileText ----` comment
   (`:140-141`); the disk-read + freshness-guard logic they would have covered is exercised by the
   `ReadBody_*` cases above, which share the `ReadVerifiedFileContent` path.)
   Delete `ReadBody_NullByteSpans_FallsBackToLineSlice`/`ReadBody_EmptyFileContent_ReturnsNull` only if H's fixture no longer produces an empty-content file; KEEP a null-byte-span line-fallback test against a fresh disk file (the line-slice path must still work when byte spans are NULL but the file is fresh):
   ```csharp
   [Fact]
   public void ReadBody_NullByteSpans_FallsBackToLineSlice_FromDisk()
   {
       using var fx = JulieDbFixture.CreateForInspect();
       string? body = ExtractReader.ReadBody(
           fx.DbPath, fx.WorkspaceRoot, "auth/UserService.cs",
           startByte: null, endByte: null, startLine: 2, endLine: 4);
       Assert.NotNull(body);
       Assert.Contains("GetUser", body!);
       Assert.Contains("return _repo.Find(id);", body);
   }
   ```

2. **Run** `scripts/test.sh` — fails to compile (`ReadBody` arity changed; `fx.WorkspaceRoot` doesn't exist until H lands). This is the gating point on subsystem H. Expected red.

3. **Implement.** In `ExtractReader.cs`:
   - Replace `ReadFileContent` (`:245-253`) with `ReadVerifiedFileContent` (above), reading `files.content_hash` via `ExtractFileHashReader.ReadFileHash` (subsystem D renames its SQL to `content_hash`) and hashing disk bytes via `ContentHasher`.
   - Rewrite `ReadBody` to take `workspaceRoot`, call `ReadVerifiedFileContent`, and on `Stale` (or null text) return `null`; on fresh text reuse `SliceByBytes`/`SliceByLines` unchanged (`:257-283`).
   - DELETE `ReadIndexedFileText` (`:189-193`) and its four `ExtractReaderEditTests.cs` test methods (reconciliation #16d — no caller remains once D5 drops the gate's use). Delete the methods BY NAME — `_ReturnsTheIndexedFileContentVerbatim` (`:142-150`), `_Utf8FileRoundTripsThroughTheAccentByte` (`:152-165`), `_UnknownPath_ReturnsNull` (`:167-172`), `_MissingDbFile_ThrowsFileNotFound` (`:192-198`) — plus the `// ---- ReadIndexedFileText ----` comment (`:140-141`). Do NOT block-delete `:142-198`: the fourth method is interleaved AFTER `ReadEditSpan_MissingDbFile` (`:176-181`) and `ReadIdentifierSites_MissingDbFile` (`:184-190`), which must survive. Verify with `grep -rn "ReadIndexedFileText" src tests` returning empty after this task + D5 land.
   - Add `using Miller.Core.Freshness;` only if needed; otherwise no new deps beyond `ContentHasher` (already in `Miller.Indexing`).

4. **Run** `scripts/test.sh` — C3 tests green (requires H's fixture; if H not yet landed, this stays red and C3 is blocked — flag in the PR).

5. **Commit** `migrate(C3): re-source body/indexed text from disk with hard content_hash freshness invariant`.

**Acceptance:**
- No SELECT in `ExtractReader` reads `files.content` (the column is gone).
- `ReadBody` hashes the disk file and compares to the prefix-stripped `content_hash` before slicing; a drifted file yields `null`, never sliced stale bytes (the `ReadBody_DriftedFile` test proves it).
- `ReadBody` resolves relative paths against `workspaceRoot`.
- `ReadIndexedFileText` is gone: `grep -rn "ReadIndexedFileText" src tests` is empty (after D5 also lands).
- Prefix stripping is done via subsystem D's shared normalizer, not duplicated.
- Build 0 warnings; `scripts/test.sh` green.

---

### Task C4 — InspectTool body-slice path plumbs the workspace root + freshness gate

**Files:** `src/Miller.Server/Tools/InspectTool.cs`. Depends on C3.

**What:** `InspectTool.RenderSymbolCompact`/`RenderSymbolJson` call `ExtractReader.ReadBody(dbPath, sym.FilePath, ...)` (`:239-240`, `:296-297`) with NO workspace root. C3 changes `ReadBody`'s signature to require the root. The root IS available: `WorkspaceReadContext.WorkspaceRoot` (`WorkspaceReadContext.cs:15`), captured in `Inspect` at `:57` but never threaded into `Run`. Thread it through.

**Why this is in-scope (flagging):** the design's §10 lists only `ExtractReader` under D2, but the disk-slice cannot resolve `sym.FilePath` (a julie-relative path) without the root, and InspectTool is the sole `ReadBody` caller for inspect. This is in-scope-by-necessity, not scope creep.

**Steps (TDD):**

1. **Write the failing test.** Add to the InspectTool test suite (the file that exercises `Run` — locate via `grep -rln "InspectTool.Run\|RenderSymbol" tests/`). Drive an `inspect ... depth=full` against the v1 inspect fixture and assert the body renders when fresh and degrades when drifted:
   ```csharp
   [Fact]
   public void Run_FullDepth_FreshFile_RendersBody()
   {
       using var fx = JulieDbFixture.CreateForInspect();
       var (index, resolver) = BuildIndexAndResolver(fx); // existing test helper pattern
       string output = InspectTool.Run(
           index, resolver, fx.DbPath, fx.WorkspaceRoot,
           target: "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false,
           out _);
       Assert.Contains("return _repo.Find(id);", output);
   }

   [Fact]
   public void Run_FullDepth_DriftedFile_DegradesBodyGracefully()
   {
       using var fx = JulieDbFixture.CreateForInspect();
       File.WriteAllText(Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs"), "changed\n");
       var (index, resolver) = BuildIndexAndResolver(fx);
       string output = InspectTool.Run(
           index, resolver, fx.DbPath, fx.WorkspaceRoot,
           target: "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false,
           out _);
       Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
       Assert.DoesNotContain("changed", output); // never slices the drifted file
   }
   ```

2. **Run** `scripts/test.sh` — fails (Run has no `workspaceRoot` param). Expected red.

3. **Implement.** In `InspectTool.cs`:
   - Add `string workspaceRoot` to `Run` (`:89-93`), `RenderSymbolCompact` (`:182-183`), `RenderSymbolJson` (`:246-247`).
   - Pass it at the call site in `Inspect` (`:59-60`): `Run(context.Index, context.Resolver, context.IndexDbPath, context.WorkspaceRoot, target, ...)`.
   - Update the two `ReadBody` calls (`:239`, `:296`) to `ExtractReader.ReadBody(dbPath, workspaceRoot, sym.FilePath, detail.BodyStartByte, ...)`.
   - The existing compact fallback string `"(body unavailable — no span recorded)"` (`:241`) covers the null-return case; keep it (it now also covers the drift case). Confirm the wording with Alan per open note (drift vs no-span are conflated under one message; acceptable for inspect's non-mutating read).

4. **Run** `scripts/test.sh` — green.

5. **Commit** `migrate(C4): thread workspace root into InspectTool body slice for disk re-source`.

**Acceptance:**
- `InspectTool.Run`/`RenderSymbol*` take and forward `workspaceRoot`; both `ReadBody` calls pass it.
- Full-depth inspect renders the body for a fresh file and degrades to "body unavailable" for a drifted file, never emitting drifted content.
- Build 0 warnings; `scripts/test.sh` green.

---

### Task C5 — SqliteBridgeReader v1 restructure (type_argument_usages JOIN, annotation `ordinal` drop, literals/DbSet renames)

**Files:** `src/Miller.Indexing/SqliteBridgeReader.cs`; tests `tests/Miller.Tests/Indexing/SqliteBridgeReaderTests.cs`. The bridge tests build their OWN inline schema (`CreateSchemaAndGate`, `SqliteBridgeReaderTests.cs:45-78`) — C5 is self-contained for them (no JulieDbFixture dependency for the bridge SELECTs themselves; the gate seed must match v1).

This task has THREE non-mechanical sub-changes plus mechanical renames.

**v1 facts (verified schema.rs):**
- `type_arguments(type_argument_id, usage_id, parent_type_argument_id, ordinal, type_name)` — NO `identifier_id`/`file_path`/`language` (those moved to `type_argument_usages`). (schema.rs:203-211)
- `type_argument_usages(usage_id, identifier_id, file_id, path, language, metadata_json)` — the new join table. (schema.rs:192-201)
- `symbol_annotations(annotation_id, symbol_id, annotation, annotation_key, raw_text, carrier, metadata_json)` — NO `ordinal`. (schema.rs:101-110)
- `literals(literal_id, file_id, path, language, literal_text, kind, carrier, arg_position, containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte, confidence, metadata_json)`. (schema.rs:213-233)
- `symbols(symbol_id, file_id, path, ...)` — for the DbSet query. (schema.rs:66-99)

**Sub-change 5a — type_arguments JOIN type_argument_usages (non-mechanical):**
The reader currently reads `identifier_id`/`file_path`/`parent_arg_id`/`id` straight off `type_arguments` (`:66-71`). In v1 those come from the JOINed usage row. New SELECT:
```sql
SELECT u.identifier_id, t.ordinal, t.parent_type_argument_id, t.type_name, u.path
FROM type_arguments t
JOIN type_argument_usages u ON u.usage_id = t.usage_id
WHERE u.identifier_id IS NOT NULL AND t.type_name IS NOT NULL
ORDER BY u.identifier_id, t.ordinal, t.type_argument_id;
```
The `TypeArgument` Core record is unchanged (`IdentifierId, Ordinal, ParentArgId, TypeName, FilePath`) — `parent_type_argument_id` maps to `ParentArgId`, `u.path` to `FilePath`. Read by name.

**Sub-change 5b — symbol_annotations ordinal drop + deterministic re-key (non-mechanical; the explicit decision in design §4.3 line 129):**
The reader reads `ordinal` and orders `ORDER BY symbol_id, ordinal, id` (`:138-143`). v1 has NO `ordinal` column. Decision (locked by design): re-key the deterministic order to `(symbol_id, annotation_id)`. The `SymbolAnnotation` Core record currently carries an `Ordinal` field (`:156`). **Decision required and resolved by design:** annotation order becomes opaque-id order, not insertion ordinal. Drop reading `ordinal`; pass `0` (or remove the field — see cross-dep). New SELECT:
```sql
SELECT symbol_id, annotation, annotation_key, raw_text, carrier
FROM symbol_annotations
WHERE symbol_id IS NOT NULL
ORDER BY symbol_id, annotation_id;
```
The `SymbolAnnotation(symbolId, ordinal, annotation, annotationKey, rawText, carrier)` constructor: pass `ordinal: 0` (keeps the Core record stable, minimizing blast radius into `BridgeGraphBuilder`). **Flag to Alan:** removing the `Ordinal` field from `SymbolAnnotation` is cleaner but ripples into Core; the design says "re-key deterministic order to `(symbol_id, annotation_id)`" — it does NOT mandate removing the field. Default: keep the field, hardcode `0`, document that ordinal is no longer meaningful. Confirm.

**Sub-change 5c — literals/DbSet mechanical renames:**

| Method | line | OLD | NEW |
|---|---|---|---|
| `ReadLiterals` SELECT | 96-102 | `file_path`, `ORDER BY file_path, start_byte, id` | `path`, `ORDER BY path, start_byte, literal_id` |
| `ReadDbSetProperties` SELECT | 170-175 | `SELECT id, ...`, `file_path`, `ORDER BY file_path, start_line, id` | `SELECT symbol_id, ...`, `path`, `ORDER BY path, start_line, symbol_id` |

All four readers switch to by-name reads while edited (D6).

**Steps (TDD):**

1. **Migrate the test schema + expectations.** In `SqliteBridgeReaderTests.cs`, rewrite `CreateSchemaAndGate` (`:45-78`) to the v1 DDL: `artifact_metadata` instead of `schema_version`+`external_extract_metadata`; v1 `symbols`/`type_arguments`/`type_argument_usages`/`literals`/`symbol_annotations` columns. Seed gate via:
   ```sql
   INSERT INTO artifact_metadata(key, value) VALUES
     ('sqlite_schema_version', '1'),
     ('extract_contract_version', '1'),
     ('hash_algorithm', 'blake3');
   ```
   Rewrite the four insert-and-assert tests:
   - `Read_TypeArguments_*` (:89-120): insert into BOTH `type_argument_usages` (the usage rows carrying `identifier_id`/`path`) and `type_arguments` (carrying `usage_id`/`ordinal`/`parent_type_argument_id`/`type_name`). Keep the same ordering assertion (`idA(0,1)` then `idB(0,1)` then nested). Example seed:
     ```sql
     INSERT INTO type_argument_usages(usage_id, identifier_id, file_id, path, language) VALUES
       ('uA','idA','fA','src/Map.cs','csharp'),
       ('uB','idB','fB','src/Profile.cs','csharp');
     INSERT INTO type_arguments(type_argument_id, usage_id, parent_type_argument_id, ordinal, type_name) VALUES
       ('t1','uA',NULL,0,'ApplicationUser'),
       ('t2','uA',NULL,1,'UserDto'),
       ('t3','uB',NULL,0,'List'),
       ('t4','uB',NULL,1,'ApplicationUser'),
       ('t5','uB','t3',0,'Inner');
     ```
     Assertion array unchanged; the nested arg's `ParentArgId` is now `"t3"` (the `parent_type_argument_id`).
   - `Read_Literals_*` (:124-163): rename insert column `file_path`→`path`, `id`→`literal_id`; assertions unchanged (`LiteralSites` still maps to `(path, start_line)`).
   - `Read_Annotations_OrderedBySymbolThenOrdinal_*` (:167-194): RENAME the test to `Read_Annotations_OrderedBySymbolThenAnnotationId` and drop the `ordinal` insert column; insert `(annotation_id, symbol_id, annotation, annotation_key, raw_text, carrier)`. Assert ordering is now by `(symbol_id, annotation_id)` — pick ids so the assertion is deterministic (e.g. `a1=sym-class`, `a2=sym-method`, ordered class-before-method by symbol_id). Drop any assertion reading the `Ordinal` value.
   - `Read_DbSetProperties_*` (:198-233): rename insert column `id`→`symbol_id`, `file_path`→`path`; v1 `symbols` has no `parent_id`/`metadata` (it's `parent_symbol_id`/`metadata_json`) — emit the v1 columns. Assertions unchanged.
   - `Read_IncompatibleSchema_Throws` (:252-274): seed `sqlite_schema_version='2'` in `artifact_metadata` (instead of `schema_version=27`) so the gate rejects.

2. **Run** `scripts/test.sh` — fails (old reader SQL hits `no such column` against v1 DDL). Expected red.

3. **Implement** sub-changes 5a/5b/5c + by-name reads in `SqliteBridgeReader.cs`. Update the XML doc comments that say "28/2" / "findings 28-2" / "v7.13.0" to "v1". Fix the stale comment at `:137` claiming `UNIQUE(symbol_id, ordinal)` (that column is gone).

4. **Run** `scripts/test.sh` — green.

5. **Commit** `migrate(C5): SqliteBridgeReader onto v1 (usages JOIN, annotation ordinal drop, renames)`.

**Acceptance:**
- `type_arguments` read JOINs `type_argument_usages` for `identifier_id`/`path`; `parent_type_argument_id` maps to `ParentArgId`.
- `symbol_annotations` read has no `ordinal`; deterministic order is `(symbol_id, annotation_id)`.
- `literals`/`symbols` reads use v1 columns (`literal_id`/`path`, `symbol_id`/`path`); all reads by-name.
- Gate seed in the test schema uses `artifact_metadata`.
- Build 0 warnings; `scripts/test.sh` green.

---

### Task C6 — WorkspaceIndexFactsReader `file_path` → `path` (mechanical)

**Files:** `src/Miller.Indexing/WorkspaceIndexFactsReader.cs`; add/extend a test in `tests/Miller.Tests/Indexing/` (locate the existing facts-reader test via `grep -rln "WorkspaceIndexFactsReader" tests/`; if none exists, add `WorkspaceIndexFactsReaderTests.cs`).

**Rename map:**

| line | OLD | NEW |
|---|---|---|
| 34 | `SELECT DISTINCT file_path FROM symbols WHERE name IS NOT NULL;` | `SELECT DISTINCT path FROM symbols WHERE name IS NOT NULL;` |

`ReadDocumentCount` (`:26`) is unchanged (`COUNT(*) FROM symbols WHERE name IS NOT NULL` — `name` is NOT NULL in v1, still valid). The `ReadKnownExtensionsCount` reader at `:37-44` reads positionally (single column) — fine, but make it by-name for D6 consistency:
```csharp
int oPath = reader.GetOrdinal("path");
... reader.GetString(oPath) ...
```

**Steps (TDD):**

1. **Write/extend the failing test** against the v1 `JulieDbFixture` (H):
   ```csharp
   [Fact]
   public void Read_CountsSymbolsAndDistinctExtensions_FromV1PathColumn()
   {
       using var fx = JulieDbFixture.CreateDefault(); // mixed .cs/.ts files
       var facts = WorkspaceIndexFactsReader.Read(fx.DbPath);
       Assert.True(facts.DocumentCount >= 5);
       Assert.True(facts.KnownExtensionsCount >= 2); // .cs and .ts
   }
   ```

2. **Run** `scripts/test.sh` — fails (`no such column: file_path`). Expected red.

3. **Implement** the rename + by-name read.

4. **Run** `scripts/test.sh` — green.

5. **Commit** `migrate(C6): WorkspaceIndexFactsReader symbols.file_path -> path`.

**Acceptance:** the reader selects `path` from `symbols`; counts are correct against the v1 fixture; build 0 warnings.

---

### Task C7 — SymbolGraphReader verify-and-lock the surviving payload

**Files:** `src/Miller.Indexing/SymbolGraphReader.cs` (NO production change expected); tests `tests/Miller.Tests/Indexing/SymbolGraphReaderTests.cs` (add a lock test).

**What:** The design (§4.3 line 126, §10B "confirm payload survives") asserts `SymbolGraphReader` needs NO change: it reads only `from_symbol_id, to_symbol_id, kind` from `relationships` (`:77-80`) and `name, kind, containing_symbol_id` from `identifiers` (`:107-111`). VERIFIED against v1 schema.rs: `relationships` has `from_symbol_id`, `to_symbol_id`, `kind` (schema.rs:137,138,141) and `identifiers` has `name`, `kind`, `containing_symbol_id` (schema.rs:117,118,119) — all present, all NOT NULL on the relationship side. So the production SELECTs survive unchanged. The reader uses positional `GetString(0/1/2)` (`:85-87`, `:116-118`) — these are robust because the SELECT lists explicit columns, but per D6 the design wants by-name; convert for drift safety since we are "VERIFY its SELECT survives unchanged and add a test locking that".

**Decision:** Keep the SELECT text byte-for-byte unchanged (it already names columns explicitly, so v1 satisfies it). Convert the three positional reads in `ReadRelationships` and `ReadIdentifiers` to by-name (`GetOrdinal`) so a future v1 column reshuffle can't silently feed garbage. This is the minimal, on-design (D6) change.

**Steps (TDD):**

1. **Add the lock test** to `SymbolGraphReaderTests.cs`. The existing tests (`:69-207`) already run against `JulieDbFixture` (H migrates it to v1 `relationships`/`identifiers`). Add one explicit payload-survival lock that would fail if a reader read the wrong column after a reshape:
   ```csharp
   [Fact]
   public void Read_V1RelationshipsAndIdentifiers_ProjectExactPayloadByName()
   {
       // relationships row -> (from,to,kind) verbatim; identifier row -> name resolves to containing's targets.
       using var fx = FixtureWith(
           relationships: new[] { new JulieDbFixture.RelationshipRow("r1", ProcessId, ValidateId, "calls") },
           identifiers:  new[] { new JulieDbFixture.IdentifierRow("i1", "Handle", "call", "csharp", "src/A.cs", 2, ProcessId) });

       var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

       // relationships payload survives the v1 reshape (extra columns ignored, three read by name):
       Assert.Contains(edges, e => e.From == ProcessId && e.To == ValidateId && e.Kind == "calls");
       // identifiers payload survives (name 'Handle' -> HandleId, kind carried):
       Assert.Contains(edges, e => e.From == ProcessId && e.To == HandleId && e.Kind == "call");
   }
   ```

2. **Run** `scripts/test.sh` — with the H-migrated v1 fixture, the existing reader SELECTs should already pass (payload survives). If the test goes green WITHOUT any production change, that confirms the design's "no breakage" claim. If it goes red, the reader read a positional column that shifted in v1 — proceed to step 3.

3. **Implement** the by-name conversion in `ReadRelationships` (`:82-87`) and `ReadIdentifiers` (`:113-118`):
   ```csharp
   using var reader = command.ExecuteReader();
   int oFrom = reader.GetOrdinal("from_symbol_id");
   int oTo = reader.GetOrdinal("to_symbol_id");
   int oKind = reader.GetOrdinal("kind");
   while (reader.Read())
   {
       string from = reader.GetString(oFrom);
       string to = reader.GetString(oTo);
       string kind = reader.GetString(oKind);
       if (string.Equals(from, to, StringComparison.Ordinal)) continue;
       edges.Add(new GraphEdge(from, to, kind));
   }
   ```
   (and the analogous `name`/`kind`/`containing_symbol_id` ordinals in `ReadIdentifiers`).

4. **Run** `scripts/test.sh` — all SymbolGraphReader tests green.

5. **Commit** `migrate(C7): lock SymbolGraphReader v1 payload + by-name reads`.

**Acceptance:**
- A test asserts the `relationships` `(from,to,kind)` and `identifiers` `(name,kind,containing_symbol_id)` payload survives the v1 reshape.
- Reads are by-name; the SELECT column lists are unchanged from the current file.
- Build 0 warnings; `scripts/test.sh` green; `scripts/test.sh scale` (when `.tools/julie-extract` is present) still produces a valid edge graph from a live `julie-extract scan`.


---

## Subsystem D: Freshness

Scope: the freshness/revision read seam and the content-hash plumbing. Migrates `FreshnessReader`, `ExtractFileHashReader`, `FreshnessGate`, and `ContentHasher` from the `julie-server` 7.13.2 schema (28/3) to the `julie-extract` v1 schema (sqlite 1 / contract 1). Provides the single canonical hash-normalization helper that the disk-slice freshness invariant (subsystem C/B, design §7) also consumes.

Verified facts driving these tasks (read from `/Users/murphy/source/julie-extractors`):
- `extraction_revisions(revision_id INTEGER PRIMARY KEY, …)` — no `workspace_id` (schema.rs:28; contract doc lines 84-97). One DB = one root.
- `revision_file_changes(revision_id, file_id, path, change_kind)` — `change_kind TEXT NOT NULL` with **no CHECK constraint** (schema.rs:43-49). Writer emits exactly `inserted|updated|deleted|unsupported` (`model.rs:60-66`).
- `files.content_hash` value is `format!("blake3:{}", blake3::hash(content).to_hex())` (extraction.rs:644) — lowercase hex, `blake3:` prefix. `hash_algorithm` metadata value is `"blake3"` (commands.rs:1851).
- `artifact_metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL)` — no `updated_at` (schema.rs:13; contract doc lines 51-55).

Canonical decisions for this subsystem:
- **Canonical hash form = bare lowercase hex (no prefix).** Normalization happens at the read boundary, not in `Miller.Core`. `StalenessCheck` stays a pure string comparator and is left unchanged.
- The `LatestRevision`/`ChangedSince` `workspaceId` parameter is **dropped** (v1 has no workspace column). Caller updates in `IndexBootstrapService`, `CrossWorkspaceRefreshService`, `FreshnessService` are owned by the bootstrap subsystem; D provides the new signatures (see cross-dependencies).

Sequencing within D: D1 first (the helper everyone consumes), then D2 (ExtractFileHashReader), then D3/D4 (FreshnessReader), then D5 (FreshnessGate wiring). D2-D5 tests depend on the v1 `JulieDbFixture` landing with subsystem H (design §12 step 3).

---

### Task D1 — Canonical hash-normalization helper

**Files**
- `src/Miller.Indexing/ContentHasher.cs` (add `NormalizeHash`)
- `tests/Miller.Tests/Indexing/ExtractReaderEditTests.cs` (existing home of `Blake3Hex`/`Blake3FileHex` tests at lines 253-275; add `NormalizeHash` cases here — fast suite, no Scale trait)

**What**
Provide one helper that converts julie's v1 prefixed `content_hash` (`blake3:<hex>`) into the bare-hex canonical form Miller compares against (disk hashes from `Blake3FileHex` are already bare hex). This is the single normalization function that `FreshnessGate` (D5), `ExtractFileHashReader` (D2), and subsystem B's disk-slice freshness invariant (design §7) all call — no second implementation.

**Approach**
- Add `public static string NormalizeHash(string hash)` to `ContentHasher`. It strips a leading scheme token of the form `<algo>:` only when the algo is `blake3` (case-insensitive on the scheme token), returning the bare hex unchanged in value/case. A hash with no recognized prefix is returned as-is (already canonical). Null/whitespace throws `ArgumentException` (consistent with the other two methods).
- Do NOT lowercase or otherwise rewrite the hex digits — julie already emits lowercase (`to_hex()`), and `Blake3Hex` already produces lowercase via `Convert.ToHexStringLower`. Keeping the digits byte-exact preserves the `StringComparison.Ordinal` contract `StalenessCheck` relies on (StalenessCheck.cs:78).

**Steps (TDD)**
1. Write failing tests in `ExtractReaderEditTests.cs` (alongside the existing `Blake3Hex_*` tests):
```csharp
[Fact]
public void NormalizeHash_StripsBlake3Prefix_LeavingBareLowercaseHex()
{
    // julie v1 stores files.content_hash as "blake3:<hex>" (extraction.rs:644). Miller compares bare hex.
    string bare = ContentHasher.Blake3Hex(Encoding.UTF8.GetBytes("namespace A { }"));
    Assert.Equal(bare, ContentHasher.NormalizeHash("blake3:" + bare));
}

[Fact]
public void NormalizeHash_BareHash_ReturnedUnchanged()
{
    // A disk hash from Blake3FileHex has no prefix; normalization is a no-op so disk==stored stays comparable.
    string bare = ContentHasher.Blake3Hex(Encoding.UTF8.GetBytes("x"));
    Assert.Equal(bare, ContentHasher.NormalizeHash(bare));
}

[Fact]
public void NormalizeHash_PrefixSchemeIsCaseInsensitive_ValuePreservedOrdinal()
{
    Assert.Equal("ABCDEF", ContentHasher.NormalizeHash("BLAKE3:ABCDEF")); // scheme token case-insensitive
    Assert.Equal("ABCDEF", ContentHasher.NormalizeHash("ABCDEF"));        // value left byte-exact (not lowered)
}

[Fact]
public void NormalizeHash_NullOrWhitespace_Throws()
{
    Assert.Throws<ArgumentException>(() => ContentHasher.NormalizeHash(null!));
    Assert.Throws<ArgumentException>(() => ContentHasher.NormalizeHash("   "));
}
```
(Requires `using System.Text;` — already present in that file for the existing Blake3 tests.)
2. Run `scripts/test.sh` — the four `NormalizeHash_*` tests fail to compile (method does not exist). Compile failure is the red state.
3. Implement in `ContentHasher.cs`:
```csharp
/// <summary>
/// Reduce a julie content-hash token to Miller's canonical bare-hex form. julie v1 stores
/// <c>files.content_hash</c> as <c>blake3:<hex></c> (the algo prefix), while a disk hash from
/// <see cref="Blake3FileHex"/> is bare hex. Strips a leading <c>blake3:</c> scheme token (scheme matched
/// case-insensitively) and returns the hex value byte-exact (no case folding) so the result stays
/// <see cref="StringComparison.Ordinal"/>-comparable. A token with no recognized prefix is already canonical.
/// </summary>
public static string NormalizeHash(string hash)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(hash);

    const string Blake3Scheme = "blake3:";
    if (hash.StartsWith(Blake3Scheme, StringComparison.OrdinalIgnoreCase))
        return hash[Blake3Scheme.Length..];

    return hash;
}
```
4. Run `scripts/test.sh` — green. Fast suite stays <30s (pure logic).
5. Commit: `feat(freshness): add ContentHasher.NormalizeHash for julie v1 blake3: prefix`.

**Acceptance**
- `NormalizeHash("blake3:" + h) == h` and `NormalizeHash(h) == h` for any bare hex `h`.
- Scheme token matched case-insensitively; hex value preserved byte-exact (no lowering).
- Null/whitespace throws `ArgumentException`.
- Fast suite green; build 0 warnings (`dotnet build Miller.slnx -c Release`).

---

### Task D2 — `ExtractFileHashReader` → v1 columns/tables + prefix-strip

**Files**
- `src/Miller.Indexing/ExtractFileHashReader.cs`
- `tests/Miller.Tests/Indexing/ExtractReaderEditTests.cs` (the `ReadFileHash_*` / `ReadHashAlgorithm_*` tests at lines 203-249 — fast suite)

**What**
Move `ReadFileHash` from `files.hash` → `files.content_hash`, returning the **normalized (bare-hex)** value via D1's helper. Move `ReadHashAlgorithm` from `external_extract_metadata` → `artifact_metadata` (same `(key, value)` shape, `updated_at` column gone). The metadata key name (`hash_algorithm`) is unchanged in v1 (contract doc line 44).

**Approach**
- `ReadFileHash`: `SELECT content_hash FROM files WHERE path = $path;` (was `SELECT hash …` at ExtractFileHashReader.cs:17). Wrap the scalar result through `ContentHasher.NormalizeHash` before returning, so every freshness consumer receives bare hex and never has to know about the `blake3:` prefix. Preserve the existing null-on-absent-path contract: a missing row yields `null` (not a normalized empty string).
- `ReadHashAlgorithm`: `SELECT value FROM artifact_metadata WHERE key = 'hash_algorithm';` (was `external_extract_metadata` at ExtractFileHashReader.cs:28). Update the `SqliteException` catch filter at line 36 from `"external_extract_metadata"` → `"artifact_metadata"` so an absent table still degrades to `null` (the same fail-soft contract; the *gate* failing loud is `JulieSchemaGate`'s job, not this reader's).

**Steps (TDD)**
1. The existing tests (`ReadFileHash_ReturnsTheFilesTableHashForThePath` :203, `ReadHashAlgorithm_ReturnsTheExtractMetadataValue` :223, `ReadHashAlgorithm_AbsentKey_ReturnsNull` :231) drive this. After subsystem H migrates `JulieDbFixture` to v1, add a prefix-strip assertion and adjust the existing one to expect bare hex:
```csharp
[Fact]
public void ReadFileHash_StripsBlake3Prefix_ReturningBareHex()
{
    using var fx = JulieDbFixture.CreateForEdit();
    // v1 fixture stores files.content_hash as "blake3:<hex>"; the reader must hand back bare hex.
    string? hash = ExtractFileHashReader.ReadFileHash(fx.DbPath, "orders/OrderService.cs");
    Assert.NotNull(hash);
    Assert.DoesNotContain(":", hash);                                  // no scheme prefix leaked
    Assert.Equal(
        ContentHasher.Blake3Hex(Encoding.UTF8.GetBytes(JulieDbFixture.OrderServiceContent)),
        hash);                                                          // equals the bare disk hash
}
```
2. Run `scripts/test.sh` — fails: reader still selects `files.hash` (no such column in v1 fixture) → `SqliteException`, and/or returns the prefixed value.
3. Implement the column/table renames + `NormalizeHash` wrap:
```csharp
public static string? ReadFileHash(string dbPath, string filePath)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

    using var connection = Open(dbPath);
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT content_hash FROM files WHERE path = $path;";
    command.Parameters.AddWithValue("$path", filePath);

    var value = command.ExecuteScalar();
    // v1 stores "blake3:<hex>"; normalize to bare hex so freshness consumers compare against disk hashes.
    return value is string s ? ContentHasher.NormalizeHash(s) : null;
}
```
and in `ReadHashAlgorithm` change the SELECT table to `artifact_metadata` and the catch-filter substring to `"artifact_metadata"`. Also fix the class-summary XML doc (lines 5-8) from `files.hash` + `external_extract_metadata.hash_algorithm` to `files.content_hash` + `artifact_metadata.hash_algorithm`.
4. Run `scripts/test.sh` — green.
5. Commit: `refactor(freshness): ExtractFileHashReader reads v1 content_hash + artifact_metadata`.

**Acceptance**
- `ReadFileHash` selects `files.content_hash` and returns bare hex (prefix stripped via `NormalizeHash`); unknown path → `null`; missing DB still throws `FileNotFoundException` (test `ReadFileHash_MissingDbFile_ThrowsFileNotFound` :243 still passes).
- `ReadHashAlgorithm` selects `artifact_metadata`; absent key → `null`; absent table → `null` (catch filter on `"artifact_metadata"`).
- Class XML doc names the v1 tables/columns.
- Fast suite green; build 0 warnings.

---

### Task D3 — `FreshnessReader.LatestRevision` → `extraction_revisions` (drop workspace_id)

**Files**
- `src/Miller.Indexing/FreshnessReader.cs`
- `tests/Miller.Tests/Indexing/FreshnessReaderTests.cs`

**What**
v1's `extraction_revisions` has no `workspace_id` (one DB = one root). `LatestRevision` becomes `SELECT MAX(revision_id) FROM extraction_revisions` with no parameter. This **changes the method signature** (drops the `workspaceId` arg) — a cross-subsystem ripple (see cross-dependencies; D owns only the signature here).

**Approach**
- New signature: `public long LatestRevision()` (was `LatestRevision(string workspaceId)` at FreshnessReader.cs:68). Remove the `ArgumentNullException.ThrowIfNull(workspaceId)` guard (line 70) and the `$ws` parameter (lines 75-76). Keep the `ObjectDisposedException` guard and the `MAX over zero rows → 0` mapping (lines 79-80) — the "no revision yet" sentinel is unchanged.
- New SQL: `SELECT MAX(revision_id) FROM extraction_revisions;`.
- Update the method XML doc (lines 61-66) to name `extraction_revisions`/`revision_id` and drop the `WHERE workspace_id` and the `workspaceId` param doc.
- The two existing scoping tests (`LatestRevision_IsScopedByWorkspaceId_DoesNotLeakAcrossWorkspaces` :36, `LatestRevision_UnknownWorkspace_ReturnsZero` :48) are now **semantically wrong** (v1 has no per-workspace scoping). Replace them with v1-appropriate tests rather than deleting coverage: one DB returns its own single MAX; two separate DB files do not leak.

**Steps (TDD)**
1. Rewrite the `FreshnessReaderTests` `LatestRevision` cases against the v1 fixture (after H2 migrates `JulieDbFixture` to `extraction_revisions` — same Phase-4 commit). These rewrites **omit** the now-optional `Create(workspaceId:)` arg (the param survives — it feeds `artifact_metadata` identity — but `LatestRevision` no longer depends on workspace, so identity is irrelevant here) and use the dropped-`WorkspaceId` `Rev` shape. The record reshape lands in THIS Phase-4 commit per reconciliation #15:
```csharp
[Fact]
public void LatestRevision_ReturnsMaxRevisionId()
{
    using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
        revisions: new[] { new Rev(1), new Rev(2), new Rev(3) });
    using var reader = new FreshnessReader(fx.DbPath);

    Assert.Equal(3, reader.LatestRevision());
}

[Fact]
public void LatestRevision_EmptyTable_ReturnsZero()
{
    using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols);
    using var reader = new FreshnessReader(fx.DbPath);

    // MAX over zero rows is SQL NULL → the "no revision yet" sentinel 0 (unchanged from the old contract).
    Assert.Equal(0, reader.LatestRevision());
}

[Fact]
public void LatestRevision_TwoSeparateDbs_DoNotLeak()
{
    // v1 has one DB per root (no workspace_id column); separate roots are separate files, so a reader
    // over one DB can never observe another root's MAX. This replaces the old per-workspace scoping test.
    using var a = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
        revisions: new[] { new Rev(2) });
    using var b = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
        revisions: new[] { new Rev(9) });
    using var ra = new FreshnessReader(a.DbPath);
    using var rb = new FreshnessReader(b.DbPath);

    Assert.Equal(2, ra.LatestRevision());
    Assert.Equal(9, rb.LatestRevision());
}
```
Delete `LatestRevision_NullWorkspaceId_Throws` (:162) — there is no workspaceId to be null. Update `Poll_AfterASecondConnectionCommitsANewRevision_SeesItWithoutReopen` (:121) so its writer INSERT targets `extraction_revisions (revision_id, …)` and its assertions call `LatestRevision()` (this test is the load-bearing "no reopen" contract — keep it, just retarget the table; H supplies the v1 column list for the INSERT).
2. Run `scripts/test.sh` — fails to compile (`LatestRevision()` arity mismatch).
3. Implement in `FreshnessReader.cs`:
```csharp
public long LatestRevision()
{
    ObjectDisposedException.ThrowIf(_disposed, this);

    using var cmd = _connection.CreateCommand();
    cmd.CommandText = "SELECT MAX(revision_id) FROM extraction_revisions;";

    object? result = cmd.ExecuteScalar();
    // MAX over zero matching rows is SQL NULL → DBNull here. Map to 0 (no revision yet).
    return result is null or DBNull ? 0L : Convert.ToInt64(result);
}
```
4. Run `scripts/test.sh` — D's own tests green. Cross-subsystem callers (`IndexBootstrapService`, `CrossWorkspaceRefreshService`, `FreshnessService`, and the `Func<string,string,long>` seam) will fail to compile until the bootstrap subsystem adopts the new signature — coordinate landing order (see cross-dependencies). D's commit is gated behind those caller updates compiling.
5. Commit (with bootstrap subsystem caller updates in the same atomic change): `refactor(freshness): LatestRevision reads extraction_revisions (one DB = one root)`.

**Acceptance**
- `LatestRevision()` takes no parameter; SQL is `SELECT MAX(revision_id) FROM extraction_revisions;`.
- Empty table → 0; populated → correct MAX; two separate DBs do not leak.
- The "no reopen, poll sees a second connection's commit" contract test still passes against `extraction_revisions`.
- Build 0 warnings (requires bootstrap callers updated — cross-dependency).

---

### Task D4 — `ChangedSince` + `RevisionChangeKind`/`ParseChangeKind` → v1 re-key, 4-value vocab, fix false CHECK comment

**Files**
- `src/Miller.Indexing/FreshnessReader.cs` (the `RevisionChangeKind` enum :6-16, `RevisionFileChange` record :23, `ChangedSince` :90-114, `ParseChangeKind` :116-126)
- `tests/Miller.Tests/Indexing/FreshnessReaderTests.cs` (the `ChangedSince_*` cases :68-119)

**What**
Re-key `revision_file_changes` reads to v1 columns (`revision_id`, `path`, `change_kind`; drop `workspace_id`, rename `revision`→`revision_id` and `file_path`→`path`). Expand the `change_kind` vocabulary from `{added,modified,deleted}` to v1's `{inserted,updated,deleted,unsupported}`, mapping `inserted→Added`, `updated→Modified`, `deleted→Deleted`, and adding a new `Unsupported` member. Keep the fail-loud-on-unknown stance but **fix the false comment** (FreshnessReader.cs:116-117) that claims a CHECK constraint enforces the vocabulary — v1 has no CHECK on `change_kind` (verified schema.rs:47).

**Approach**
- Enum: add `Unsupported` to `RevisionChangeKind` with a doc explaining the freshness treatment (a file that became unsupported is no longer represented in the artifact → treat as remove-from-index for incremental purposes, design §4.4). Keep `Added`/`Modified`/`Deleted`.
- `RevisionFileChange` record: rename `long Revision` → `long RevisionId` and keep `FilePath` (it maps to v1's `path` column; the public Miller-side name can stay `FilePath` since it is still a workspace-relative path — but the SQL column is `path`). To minimize churn and stay honest about the source, rename the record positional to `(long RevisionId, string Path, RevisionChangeKind ChangeKind)` and update the three test assertions that read `.FilePath`/`.Revision` (:85-86, :116-118). Update the record XML doc (:18-23) to drop the stale "verified-fact 5" julie-server reference and name v1 columns.
- `ChangedSince` signature drops `workspaceId` (same v1 rationale as D3) → `public IReadOnlyList<RevisionFileChange> ChangedSince(long sinceRevision)`. SQL: `SELECT revision_id, path, change_kind FROM revision_file_changes WHERE revision_id > $since ORDER BY revision_id, path;`. By-name reads are unnecessary for a 3-column fixed SELECT but keep the read order matching the SELECT order; map the three columns to the record.
- `ParseChangeKind`: switch on the 4 v1 values; throw `InvalidOperationException` on anything else with a corrected message and a corrected comment.

**Steps (TDD)**
1. Rewrite the `ChangedSince_*` tests against the v1 fixture (this is the Phase-4 step-2 commit that also drops the record's `WorkspaceId` and renames `FilePath`→`Path` per reconciliation #15). The v1 `revision_file_changes` PK is `(revision_id, file_id)`, but `file_id` is **derived inside the fixture** via the shared `FileId(path)` helper — it is NOT a `RevisionFileChangeRow` ctor arg — so the rewritten `Fc(...)` calls are 3-arg `Fc(revision, path, change_kind)` (reconciliation #15: do not pass an explicit `"file-x"`):
```csharp
[Fact]
public void ChangedSince_ReturnsOnlyRowsAfterTheGivenRevision()
{
    using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
        revisions: new[] { new Rev(1), new Rev(2), new Rev(3) },
        fileChanges: new[]
        {
            new Fc(1, "a.cs", "inserted"),
            new Fc(2, "b.cs", "updated"),
            new Fc(3, "a.cs", "deleted"),
        });
    using var reader = new FreshnessReader(fx.DbPath);

    var changes = reader.ChangedSince(1); // strictly after revision 1

    Assert.Equal(2, changes.Count);
    Assert.Contains(changes, c => c.Path == "b.cs" && c.ChangeKind == RevisionChangeKind.Modified && c.RevisionId == 2);
    Assert.Contains(changes, c => c.Path == "a.cs" && c.ChangeKind == RevisionChangeKind.Deleted && c.RevisionId == 3);
}

[Fact]
public void ChangedSince_ParsesAllFourV1ChangeKinds()
{
    using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
        revisions: new[] { new Rev(1) },
        fileChanges: new[]
        {
            new Fc(1, "inserted.cs", "inserted"),
            new Fc(1, "updated.cs", "updated"),
            new Fc(1, "deleted.cs", "deleted"),
            new Fc(1, "unsupported.cs", "unsupported"),
        });
    using var reader = new FreshnessReader(fx.DbPath);

    var changes = reader.ChangedSince(0);

    Assert.Contains(changes, c => c.Path == "inserted.cs"    && c.ChangeKind == RevisionChangeKind.Added);
    Assert.Contains(changes, c => c.Path == "updated.cs"     && c.ChangeKind == RevisionChangeKind.Modified);
    Assert.Contains(changes, c => c.Path == "deleted.cs"     && c.ChangeKind == RevisionChangeKind.Deleted);
    Assert.Contains(changes, c => c.Path == "unsupported.cs" && c.ChangeKind == RevisionChangeKind.Unsupported);
}

[Fact]
public void ChangedSince_UnknownChangeKind_ThrowsLoudly_NoCheckConstraintInV1()
{
    // v1 has NO CHECK constraint on change_kind (schema.rs:47) — Miller is the only guard. Inject a drifted
    // value directly and assert the reader fails loud rather than silently misclassifying.
    using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, NoSymbols,
        revisions: new[] { new Rev(1) });
    InsertRawFileChange(fx.DbPath, revisionId: 1, fileId: "f-x", path: "x.cs", changeKind: "renamed");
    using var reader = new FreshnessReader(fx.DbPath);

    var ex = Assert.Throws<InvalidOperationException>(() => reader.ChangedSince(0));
    Assert.Contains("renamed", ex.Message, StringComparison.Ordinal);
    Assert.Contains("inserted|updated|deleted|unsupported", ex.Message, StringComparison.Ordinal);
}
```
Add the `InsertRawFileChange` local helper in the test (raw INSERT into `revision_file_changes`, bypassing the typed `Fc` record so a drifted value can be written). Update `ChangedSince_AtLatestRevision_ReturnsEmpty` (:90) to drop `Ws` and use `ChangedSince(2)`.
2. Run `scripts/test.sh` — fails (old columns/vocab; `RevisionChangeKind.Unsupported` does not exist; `.Path`/`.RevisionId` not on the record).
3. Implement in `FreshnessReader.cs`:
```csharp
/// <summary>The kind of change a <see cref="RevisionFileChange"/> records (julie v1 <c>change_kind</c>).</summary>
public enum RevisionChangeKind
{
    /// <summary>A new file appeared in this revision (v1 <c>inserted</c>).</summary>
    Added,
    /// <summary>An existing file's content changed in this revision (v1 <c>updated</c>).</summary>
    Modified,
    /// <summary>A file was removed in this revision (v1 <c>deleted</c>).</summary>
    Deleted,
    /// <summary>A file became unsupported and is no longer represented (v1 <c>unsupported</c>); for
    /// freshness/incremental purposes treat it as a removal from the index.</summary>
    Unsupported,
}

public sealed record RevisionFileChange(long RevisionId, string Path, RevisionChangeKind ChangeKind);

public IReadOnlyList<RevisionFileChange> ChangedSince(long sinceRevision)
{
    ObjectDisposedException.ThrowIf(_disposed, this);

    using var cmd = _connection.CreateCommand();
    cmd.CommandText =
        "SELECT revision_id, path, change_kind FROM revision_file_changes " +
        "WHERE revision_id > $since ORDER BY revision_id, path;";
    cmd.Parameters.AddWithValue("$since", sinceRevision);

    var changes = new List<RevisionFileChange>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        long revisionId = reader.GetInt64(0);
        string path = reader.GetString(1);
        string changeKind = reader.GetString(2);
        changes.Add(new RevisionFileChange(revisionId, path, ParseChangeKind(changeKind)));
    }

    return changes;
}

// v1's revision_file_changes.change_kind has NO CHECK constraint (julie-extractors schema.rs:47) — Miller is
// the only guard. The writer emits exactly inserted|updated|deleted|unsupported (model.rs:60-66); anything
// else means the v1 contract drifted (a future julie-extract), so fail loud rather than misclassify.
private static RevisionChangeKind ParseChangeKind(string changeKind) => changeKind switch
{
    "inserted" => RevisionChangeKind.Added,
    "updated" => RevisionChangeKind.Modified,
    "deleted" => RevisionChangeKind.Deleted,
    "unsupported" => RevisionChangeKind.Unsupported,
    _ => throw new InvalidOperationException(
        $"Unknown revision_file_changes.change_kind '{changeKind}'; expected " +
        "inserted|updated|deleted|unsupported (the julie-extract v1 schema may have drifted)."),
};
```
4. Run `scripts/test.sh` — green. `ChangedSince` callers: the only production caller is the future incremental path (none today; grep confirms `ChangedSince` is exercised only by `FreshnessReaderTests` and `MultiProcessWalTests`). `MultiProcessWalTests` (Scale) calls `fr.ChangedSince(startRevision, workspaceId)` at line 70 — the bootstrap/scale subsystem must drop the `workspaceId` arg there (it is `[Trait("Category","Scale")]`, not in the fast suite). Flag in cross-dependencies.
5. Commit: `refactor(freshness): ChangedSince reads v1 revision_file_changes + 4-value change_kind`.

**Acceptance**
- `ChangedSince(long)` selects `revision_id, path, change_kind` from `revision_file_changes`, ordered `revision_id, path`, no workspace filter.
- Vocabulary maps `inserted→Added`, `updated→Modified`, `deleted→Deleted`, `unsupported→Unsupported`.
- Unknown value throws `InvalidOperationException` naming the bad value and the expected set; the in-code comment correctly states there is no CHECK constraint in v1.
- `RevisionFileChange` exposes `RevisionId`/`Path`/`ChangeKind`.
- Fast suite green; build 0 warnings (after the one Scale caller in `MultiProcessWalTests` is updated — cross-dependency).

---

### Task D5 — `FreshnessGate`: normalize prefixed `content_hash` vs bare-hex disk hash before `StalenessCheck`

**Files**
- `src/Miller.Server/Hosting/FreshnessGate.cs`
- `tests/Miller.Tests/Server/FreshnessGateTests.cs`

**What**
`FreshnessGate.Check` compares julie's stored hash (now `content_hash`, prefixed `blake3:`) against a disk hash from `ContentHasher.Blake3FileHex` (bare hex). The two must be normalized to the same canonical form before `StalenessCheck` (which is a pure ordinal comparator) sees them — otherwise every file reads stale (prefixed != bare). Since D2 makes `ExtractFileHashReader.ReadFileHash` return the already-normalized bare hex, the gate's stored side is canonical the moment it leaves the reader; D5 verifies that end-to-end and removes any residual prefix assumption. The gate also references `external_extract_metadata` indirectly via `ReadHashAlgorithm` (handled in D2) — no SQL change in the gate file itself.

**Approach**
- `FreshnessGate.cs:66` already calls `ExtractFileHashReader.ReadFileHash(dbPath, indexedFilePath)`, which after D2 returns bare hex. `currentHash` at line 71 is `ContentHasher.Blake3FileHex(diskPath)` (bare hex). So both sides are bare hex and `StalenessCheck.Check` compares like-for-like. To be defensive against a caller passing a still-prefixed indexed hash (and to make the normalization explicit at the comparison boundary per the design's "one canonical form applied consistently"), wrap `indexedHash` through `ContentHasher.NormalizeHash` at the point of `IndexedSnapshot` construction. This is idempotent on already-bare hashes (D1 guarantee) and self-documents the invariant.
- **DELETE `FreshnessGate.cs:70`'s `ReadIndexedFileText` call — reconciliation #16d.** v1 stores no snapshot text, so there is nothing for the gate to read as an indexed-side baseline. The gate stops reading indexed text and passes `indexedText: null` (verified safe: `StalenessCheck.cs:83` runs the exact-text tiebreaker only when text is on BOTH sides; null falls through to the hash verdict, not auto-Stale). Dropping this call removes the method's last `src/` caller, which is why C3 (Phase 5) can then delete `ReadIndexedFileText` outright. This is the resolution of the "C3↔D5 2-arg/3-arg break" — the gate's freshness is now a pure `content_hash` compare. (Ordering: D5 lands in Phase 4 and removes the call; the method still compiles against `files.content` until C3+H3 in Phase 5 delete it and the column together.)
- Update the class XML doc (FreshnessGate.cs:9-11): "reads julie's BLAKE3 snapshot from `files.hash`" → `files.content_hash` (normalized), and DROP the "Exact text is still supplied when available as the collision/normalization guard" sentence (no stored text in v1; the tiebreaker is honestly skipped).

**Steps (TDD)**
1. Add a gate-level test proving a `blake3:`-prefixed stored hash still reads Fresh against a byte-identical disk file. The `FreshnessGateTests.SetFileHash` helper (currently `UPDATE files SET hash = …` at FreshnessGateTests.cs:192) must target `content_hash` (H/B migrate the column; D updates this helper since it lives in D's test file). Store the **prefixed** value to mirror what julie writes:
```csharp
[Fact]
public void Check_StoredHashHasBlake3Prefix_StillFreshAgainstByteIdenticalFile()
{
    using var fx = JulieDbFixture.CreateForEdit();
    using var workspace = FreshWorkspace();
    string diskPath = WriteFile(workspace, "orders/OrderService.cs",
        Encoding.UTF8.GetBytes(JulieDbFixture.OrderServiceContent));
    // julie v1 stores files.content_hash as "blake3:<hex>". The gate must normalize before comparing to the
    // bare-hex disk hash, else a byte-identical file would read Stale.
    SetFileHash(fx.DbPath, "orders/OrderService.cs",
        "blake3:" + ContentHasher.Blake3FileHex(diskPath));

    var result = FreshnessGate.Check(fx.DbPath, "orders/OrderService.cs", diskPath,
        JulieDbFixture.OrderServiceContent);

    Assert.Equal(FreshnessResult.Fresh, result.Result);
    Assert.True(result.IndexedContentFound);
}
```
Update the existing `Check_ByteIdenticalFileHash_IsFresh` (:21) and the other `SetFileHash` callers to store the prefixed form (matching real julie output) so the whole file exercises the v1 value format. Update `SetFileHash` (:188-196) to `UPDATE files SET content_hash = $hash …` and `SetHashAlgorithm` (:198-217) to upsert into `artifact_metadata (key, value)` (drop the `updated_at` column and the `DELETE … external_extract_metadata` → `artifact_metadata`).
2. Run `scripts/test.sh` — the new prefix test fails if the gate compares a prefixed indexed hash against a bare disk hash (it would, absent normalization in the gate, unless D2's reader already stripped it — this test guards the invariant regardless of where the strip happens).
3. Implement in `FreshnessGate.cs` — normalize at the boundary and fix the doc:
```csharp
string? indexedHash = ExtractFileHashReader.ReadFileHash(dbPath, indexedFilePath);
if (string.IsNullOrWhiteSpace(indexedHash))
    return new GateResult(FreshnessResult.Stale, IndexedContentFound: false);

string currentHash = ContentHasher.Blake3FileHex(diskPath);
// v1 artifacts store NO snapshot text (files.content is gone), so the indexed side is hash-only:
// DROP the old `ExtractReader.ReadIndexedFileText(dbPath, indexedFilePath)` call (reconciliation #16d) — it
// is now 3-arg (needs a workspaceRoot the gate doesn't have) and there is no stored text to read anyway.
// Pass indexedText: null. StalenessCheck then decides on the hash compare ALONE: it runs the exact-text
// tiebreaker only when text is present on BOTH sides (StalenessCheck.cs:83), so null indexedText is NOT
// auto-Stale — it just skips the tiebreaker. NormalizeHash is the idempotent belt-and-suspenders at the
// comparison boundary (D2's reader already returns bare hex; this guards a still-prefixed caller).
var indexed = new IndexedSnapshot(ContentHasher.NormalizeHash(indexedHash), indexedText: null);
var current = new CurrentProbe(currentHash, diskText);
return new GateResult(StalenessCheck.Check(indexed, current), IndexedContentFound: true);
```
4. Run `scripts/test.sh` — green. All existing gate tests (byte-identical Fresh, changed-bytes Stale, BOM-differs Stale, missing/empty/wrong-algorithm Stale) still pass against the v1 column + prefixed value.
5. Commit: `fix(freshness): FreshnessGate normalizes v1 blake3: content_hash before staleness compare`.

**Dependency note (design §7 disk-slice invariant):** subsystem B's `inspect(full)` body-slice path and the edit baseline must apply this same `ContentHasher.NormalizeHash`-based comparison before slicing disk bytes (a slice from a drifted file returns wrong bytes). D1's helper is the shared primitive; B owns wiring it into the slice path. Surfaced in cross-dependencies.

**Acceptance**
- A `blake3:`-prefixed stored `content_hash` reads Fresh against a byte-identical disk file.
- All pre-existing gate verdicts (Stale on changed/BOM/missing/wrong-algorithm/empty) preserved against the v1 fixture.
- `StalenessCheck` and its tests remain unchanged (it stays a pure comparator; normalization lives at the read/gate boundary).
- Gate class XML doc names `files.content_hash`.
- Fast suite green; build 0 warnings.


---

## Subsystem E: Bootstrap, workspace identity & report-consuming services

These tasks move the bootstrap and the two other report-consuming services (`WorkspaceTool.Open`, `CrossWorkspaceRefreshService.Refresh`) off the julie-echoed `workspace_id` identity model onto v1's producer-enforced `root_path`/exit-3 model, repoint the revision cursor + DB fallback at `extraction_revisions`, and align `IndexerCore`'s transient-error handling with v1's per-diagnostic `recoverable` flag. `WorkspaceId` keeps its SHA-256 registry identity unchanged (Miller-internal only).

**Ordering inside E:** E5 first (pure, no deps), then E1+E2 (bootstrap), then E3+E4 (services), then E6. All of E lands AFTER subsystem A (report rewrite) and alongside C (FreshnessReader) per the design's sequencing step 4. Run `scripts/test.sh` (fast suite, `Category!=Scale`, <30s) after every implement step; `dotnet build Miller.slnx -c Release` must stay 0 warnings.

---

### Task E5: WorkspaceId — keep SHA-256 for Miller's own registry; stop expecting julie to echo it

**Files**
- `src/Miller.Indexing/WorkspaceId.cs` (no code change expected — verify + lock with a test)
- `tests/Miller.Tests/Indexing/WorkspaceIdTests.cs`

**What**
`WorkspaceId.FromCanonicalRoot` (SHA-256 hex of the canonical root) and `WorkspaceId.Display` stay exactly as-is — they are Miller's **own** registry identity for `~/.miller/workspaces.db`, independent of julie. The migration's only change here is conceptual: julie-extract v1 does NOT store or echo a `workspace_id` (it stores `artifact_id` + `root_path` in `artifact_metadata`), so nothing in the codebase may treat `WorkspaceId.FromCanonicalRoot(root)` as something julie will return. That coupling lived in `JulieExtractRunner.Scan` (line 219, subsystem A drops it), `IndexBootstrapService` (E1), `WorkspaceTool` (E3), `CrossWorkspaceRefreshService` (E4). After those land, `WorkspaceId` is purely Miller-internal.

**Approach**
This is the cheapest task: confirm `WorkspaceId.cs` needs no edit and add one regression test that pins the contract "this id is Miller's, derived only from the canonical root, never read back from a julie artifact." The existing `WorkspaceIdTests` (3 tests, lines 8-36) already pin `FromCanonicalRoot` stability and `Display`. Add a test asserting `FromCanonicalRoot` is a pure function of the root string and does not depend on any DB/artifact input — documents that the julie echo is gone.

**Steps (TDD)**
1. Add the lock test to `tests/Miller.Tests/Indexing/WorkspaceIdTests.cs`:
   ```csharp
   [Fact]
   public void FromCanonicalRoot_IsPureFunctionOfRoot_NotDerivedFromAnyArtifact()
   {
       // v1 julie-extract stores artifact_id + root_path in artifact_metadata; it does NOT echo a
       // workspace_id. Miller's workspace_id is its OWN registry identity, derived solely from the
       // canonical root, never read back from a julie DB. Same root in, same id out, every time.
       const string root = "/abs/work/repo";
       string a = WorkspaceId.FromCanonicalRoot(root);
       string b = WorkspaceId.FromCanonicalRoot(root);
       Assert.Equal(a, b);
       Assert.Equal("a0efc97f7ea34ca9673db9e8a54459b869b3de0f386f8140de8177c6b947a311", a);
   }
   ```
2. Run `scripts/test.sh` — the new test should pass immediately (no production change), proving `WorkspaceId` already satisfies the post-migration contract.
3. If it passes, no `WorkspaceId.cs` edit is needed; commit the lock test. If for any reason it fails (it should not), the failure is a real bug to fix in `WorkspaceId.cs`, not a test to weaken.

**Acceptance**
- `WorkspaceIdTests` has 4 tests, all green in the fast suite.
- `WorkspaceId.cs` is unchanged (SHA-256 retained); `git diff src/Miller.Indexing/WorkspaceId.cs` is empty.
- Commit: `test: lock WorkspaceId as Miller-internal identity (julie no longer echoes it)`.

---

### Task E1: Bootstrap identity — drop workspace_id rebind/assertion, adopt root_path compare + exit-3 root_mismatch

> **Phase placement: E1 is in PHASE 3, not Phase 4** (moved — reconciliation #17). E1 removes `IndexBootstrapService`'s two `src` callers of `ExtractReader.ReadWorkspaceId` (`:117,150`), which **C2 deletes in Phase 3**; deleting the method while these callers exist is build-red (and `ReadWorkspaceId` also runtime-fails against H1's v1 `artifact_metadata` fixture). So E1 lands in the SAME Phase-3 commit as C2's `ReadWorkspaceId` deletion + the 5 scale-test swaps. E1 depends only on **A** (report accessors, Phase 2) and **C2's `ReadRootPath`** (Phase 3) — it does NOT touch the revision/freshness path: the `builtRevision` seed at `IndexBootstrapService.cs:164-165` (OLD `ReadLatestRevisionOrZero(dbPath, stableWorkspaceId)` → OLD `canonical_revisions`) is OUTSIDE E1's `:112-153` range and stays OLD through Phase 3; **E2 (Phase 4)** drops that inner `workspaceId`. E5's `WorkspaceId` lock test stays in Phase 4 (it pins the end state after E1/E3/E4).

**Files**
- `src/Miller.Server/Hosting/IndexBootstrapService.cs` (lines 112-153, 245-260)
- `tests/Miller.Tests/Server/IndexBootstrapServiceTests.cs` (lines 20-65: `DecideBootstrapScan` tests)

**What**
Today the bootstrap (lines 112-153) does three workspace_id-coupled things that v1 makes obsolete:
1. **Force-rebind decision** (`DecideBootstrapScan`, lines 245-260): if the existing DB's `external_extract_metadata.workspace_id` != Miller's stable id, force a `--force` scan to "rebind" the DB to the stable id. v1 has no echoed workspace_id and the `--workspace-id` flag is gone (design §4.1), so there is nothing to rebind TO. Worse, v1's `scan` itself returns **exit 3 `RootMismatch`** (verified: `scan`→`open_artifact_for_root` guard at julie-extractors `commands.rs:1266-1279`, recoverable:false; the `update`/`delete` path enforces the same at `commands.rs:1239`) when the DB at `--db` was built for a different `--root`. So the producer now enforces DB-belongs-to-root; Miller must not pre-empt it with a guessed rebind.
2. **`ExtractReader.ReadWorkspaceId(canonicalDbPath)`** (line 117) to feed that decision — the key is gone in v1.
3. **The hard post-load assertion** (lines 150-153): re-reads `ReadWorkspaceId` and throws if it != stable id. The signal is gone; the assertion must be replaced by a `root_path` compare (the producer-aligned identity) or removed entirely in favor of relying on exit-3.

**Approach (design §10D)**
- Replace the workspace_id-mismatch branch in `DecideBootstrapScan` with a **root_path** comparison. On an existing DB, read `artifact_metadata.root_path` via the C-provided (C2, reconciliation #14) `ExtractReader.ReadRootPath(dbPath)` and compare (canonicalized) against `canonicalRoot`. A mismatch means the DB at `<root>/.miller/symbols.db` was built for a different root (a moved/symlinked/copied checkout) — force a `--force` scan to rebuild it for THIS root. This preserves the existing "an existing DB bound to the wrong identity gets force-rebuilt before load" behavior, keyed on the signal v1 actually has.
- A missing/unreadable `root_path` key (a legacy/pre-v1 DB) ALSO forces a scan — same as the old missing-workspace_id case.
- Drop the post-load hard assertion (old lines 150-153). The producer already guaranteed root match: either the scan succeeded with the right root, or (for a reused DB we did NOT scan) the `DecideBootstrapScan` root_path compare already passed. A belt-and-suspenders re-read adds no signal v1 provides. Keep the bootstrap failing loud on a genuine scan exit-3 (that propagates as `IncompatibleExtractException` from A's `Interpret`, which the existing `catch`/cleanup at line 196 already handles by disposing the ledger and rethrowing).
- `scanRevision = report.Revision` (line 134) stays, but now resolves through A's accessor mapping `revision.latest_revision_id` (null on a no-op scan). The log line at 135-137 reads `report.SymbolsExtracted` (A maps to `counts.rows_written.symbols` — the per-operation count, matching today's "symbols extracted this scan" semantics; reconciliation #13) and `report.Revision` — unchanged call shape.
- Rename the `existingWorkspaceId` parameter/locals to `existingRootPath` and the `BootstrapScanDecision` semantics comment to root-path language.

**Steps (TDD)**
1. **Write failing tests.** Rewrite the three `DecideBootstrapScan` tests (lines 20-65) to the root_path model and add the new force-on-root-mismatch case. The signature changes from `(bool dbExists, string? existingWorkspaceId, string stableWorkspaceId)` to `(bool dbExists, string? existingRootPath, string canonicalRoot)`:
   ```csharp
   [Fact]
   public void DecideBootstrapScan_MissingDb_DeltaScansBeforeFirstLoad()
   {
       var decision = IndexBootstrapService.DecideBootstrapScan(
           dbExists: false, existingRootPath: null, canonicalRoot: "/work/repo");
       Assert.True(decision.ShouldScan);
       Assert.False(decision.Force);
       Assert.Equal(WorkspaceRegistryState.Ready, decision.RegistryStateAfterLoad);
   }

   [Fact]
   public void DecideBootstrapScan_ExistingDbForThisRoot_LoadsExistingWithoutScan()
   {
       var decision = IndexBootstrapService.DecideBootstrapScan(
           dbExists: true, existingRootPath: "/work/repo", canonicalRoot: "/work/repo");
       Assert.False(decision.ShouldScan);
       Assert.False(decision.Force);
       Assert.Equal(WorkspaceRegistryState.LoadedExisting, decision.RegistryStateAfterLoad);
   }

   [Theory]
   [InlineData(null)]                       // legacy / pre-v1 DB: no root_path key
   [InlineData("/work/OTHER-repo")]         // DB built for a different root (moved/copied checkout)
   public void DecideBootstrapScan_ExistingDbForMissingOrDifferentRoot_ForceScansBeforeLoad(string? existingRootPath)
   {
       var decision = IndexBootstrapService.DecideBootstrapScan(
           dbExists: true, existingRootPath: existingRootPath, canonicalRoot: "/work/repo");
       Assert.True(decision.ShouldScan);
       Assert.True(decision.Force);
       Assert.Equal(WorkspaceRegistryState.Ready, decision.RegistryStateAfterLoad);
   }
   ```
   Delete the old `WorkspaceId.FromCanonicalRoot`-keyed mismatch theory (old lines 48-65).
2. Run `scripts/test.sh` — the rewritten tests fail to compile (`DecideBootstrapScan` still has the old signature). Compile failure is the red state.
3. **Implement.** In `IndexBootstrapService.cs`:
   - Change `DecideBootstrapScan` (lines 245-260) signature + body to compare canonicalized `existingRootPath` against `canonicalRoot`:
     ```csharp
     internal static BootstrapScanDecision DecideBootstrapScan(
         bool dbExists, string? existingRootPath, string canonicalRoot)
     {
         ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);

         if (!dbExists)
             return new BootstrapScanDecision(
                 ShouldScan: true, Force: false, WorkspaceRegistryState.Ready);

         // v1 has no echoed workspace_id; the identity signal is artifact_metadata.root_path. A missing key
         // (legacy DB) or a DB built for a different root (moved/copied/symlinked checkout) is force-rebuilt
         // for THIS root before load — same convergence the old workspace_id rebind gave, keyed on the v1
         // signal. (julie's own scan also self-rejects a true root mismatch with exit 3, design §4.1.)
         if (!RootPathsEqual(existingRootPath, canonicalRoot))
             return new BootstrapScanDecision(
                 ShouldScan: true, Force: true, WorkspaceRegistryState.Ready);

         return new BootstrapScanDecision(
             ShouldScan: false, Force: false, WorkspaceRegistryState.LoadedExisting);
     }

     private static bool RootPathsEqual(string? existingRootPath, string canonicalRoot) =>
         existingRootPath is not null
         && string.Equals(
             PathCanonicalizer.CanonicalizeRoot(existingRootPath), canonicalRoot, StringComparison.Ordinal);
     ```
     Note: `CanonicalizeRoot` of a non-existent path throws; guard by only canonicalizing when the dir exists, else treat as not-equal (force). Use a try/`Directory.Exists` check inside `RootPathsEqual` so a stale root_path pointing at a deleted dir forces a rebuild rather than crashing bootstrap.
   - In `Run()` (lines 116-118): replace `ReadWorkspaceId` with `ReadRootPath` (C-provided, C2 — reconciliation #14), feeding the decision:
     ```csharp
     bool dbExists = File.Exists(canonicalDbPath);
     string? existingRootPath = dbExists ? ExtractReader.ReadRootPath(canonicalDbPath) : null;
     var scanDecision = DecideBootstrapScan(dbExists, existingRootPath, canonicalRoot);
     ```
   - Update the force-log (lines 123-125) to say `root_path` instead of `workspace_id`.
   - **Delete the post-load hard assertion** (lines 150-153) entirely, including the `persistedWorkspaceId` local. Add a one-line comment in its place: `// No post-load identity re-check: julie-extract self-rejects a root mismatch at scan (exit 3, RootMismatch — design §4.1), and DecideBootstrapScan already compared root_path for a reused DB.`
   - `stableWorkspaceId` (line 106) STAYS — it is Miller's registry id, still written to the registry rows (lines 157, 168, 171). Only the julie-echo coupling is removed.
4. Run `scripts/test.sh` — the three rewritten `DecideBootstrapScan` tests pass.
5. Run `dotnet build Miller.slnx -c Release` — 0 warnings (a removed local must not leave an unused-variable warning).
6. Commit: `feat(bootstrap): adopt root_path identity + exit-3 root_mismatch, drop workspace_id rebind`.

**Acceptance**
- `IndexBootstrapService` no longer calls `ExtractReader.ReadWorkspaceId` (grep returns 0 hits in `src/Miller.Server/Hosting/IndexBootstrapService.cs`).
- `DecideBootstrapScan` keys the force decision on `root_path`; a DB built for a different/missing root force-rebuilds; a matching root loads existing.
- The post-load hard `InvalidOperationException` workspace_id assertion is gone.
- Full `Run()` path is exercised by the Scale suite (real `julie-extract scan`); the fast suite pins `DecideBootstrapScan`. `dotnet build Miller.slnx -c Release` is 0 warnings.

---

### Task E2: ReadLatestRevisionOrZero queries extraction_revisions (drop workspace_id filter)

**Files**
- `src/Miller.Server/Hosting/IndexBootstrapService.cs` (lines 339-361)
- `tests/Miller.Tests/Server/IndexBootstrapServiceTests.cs` (lines 140-172: the `ReadLatestRevisionOrZero` happy-path tests)

**What**
`ReadLatestRevisionOrZero(dbPath, workspaceId)` (lines 345-361) is the DB-fallback cursor read used when a reused DB had no scan report. It delegates to `FreshnessReader.LatestRevision(workspaceId)`. In v1, `FreshnessReader.LatestRevision` (subsystem **D**, Task D3) changes to `SELECT MAX(revision_id) FROM extraction_revisions` with **no workspace filter** (design §4.4: "one DB = one root"). So the `workspaceId` argument is no longer a SQL filter — it survives ONLY as the null-sentinel guard (line 347-348: `if (workspaceId is null) return 0;`) and for the existing degrade/propagate discipline.

**Approach**
- Keep the `ReadLatestRevisionOrZero(string dbPath, string? workspaceId)` OUTER signature (callers in `WorkspaceTool.cs:399`, `CrossWorkspaceRefreshService` and `IndexBootstrapService.cs:165` pass a workspaceId; keeping the param avoids a wider ripple). But the INNER call MUST change: D3 (subsystem D) DROPS the parameter from `FreshnessReader.LatestRevision`, so `reader.LatestRevision(workspaceId)` becomes `reader.LatestRevision()` (reconciliation #2 — a REQUIRED edit, not comment-only; it won't compile against D3's no-arg signature otherwise). The outer `workspaceId` survives ONLY as the null-sentinel guard (workspaceId null → 0, "no workspace yet"), preserved verbatim.
- The error discipline (FileNotFoundException → 0; InvalidOperationException + SqliteException propagate loudly) is unchanged — only the underlying query (in D3) moves to `extraction_revisions`.
- My responsibility is the inner-call edit + the test that proves `ReadLatestRevisionOrZero` returns the right MAX(revision_id) over a v1 `extraction_revisions` fixture, and that the null/missing/corrupt branches still hold. The query rewrite itself is D3's; this task lands in the SAME Phase-4 atomic commit as D3.

**Steps (TDD)**
1. **Write failing tests.** Update the happy-path test (lines 157-172) to build a v1 fixture with `extraction_revisions` rows (depends on H's migrated `JulieDbFixture`). The new fixture builder writes `extraction_revisions(revision_id, ...)` instead of `canonical_revisions(revision, workspace_id)`:
   ```csharp
   [Fact]
   public void ReadLatestRevisionOrZero_ReusedDbWithRevisions_ReturnsTheMaxRevisionId()
   {
       // v1: revisions live in extraction_revisions(revision_id), one DB = one root (no workspace filter).
       using var fx = JulieDbFixture.Create(
           JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, JulieDbFixture.DefaultRows,
           rootPath: "/work/seed",
           revisions: new[]
           {
               new JulieDbFixture.RevisionRow(3),
               new JulieDbFixture.RevisionRow(7),
               new JulieDbFixture.RevisionRow(5),
           });

       // workspaceId is now only the null-sentinel guard; the SQL no longer filters by it.
       Assert.Equal(7L, IndexBootstrapService.ReadLatestRevisionOrZero(fx.DbPath, "ws-anything"));
   }
   ```
   Keep `ReadLatestRevisionOrZero_NullWorkspaceId_ReturnsZero` (lines 140-145), `_MissingDbFile_DegradesToZero` (148-153), `_CorruptDb_ThrowsLoudly` (176-190), `_NonWritableDbDirectory_ThrowsLoudly` (290-313) as-is — their behavior is unchanged. Update any `JulieDbFixture.RevisionRow(revision, workspaceId)` call to the new `RevisionRow(revisionId)` shape (H removes the workspace_id field).
2. Run `scripts/test.sh` — fails to compile until H's `JulieDbFixture.RevisionRow` drops `workspaceId` and C's `LatestRevision` reads `extraction_revisions`. Coordinate landing order: H + C before E2's test goes green.
3. **Implement.** In `ReadLatestRevisionOrZero` (lines 345-361): change the inner call `reader.LatestRevision(workspaceId)` → `reader.LatestRevision()` (D3 drops the parameter — reconciliation #2), and update the "latest persisted revision for a reused DB" doc comment (lines 339-344) to note the v1 source table and that `workspaceId` is now the null-sentinel guard only:
   ```csharp
   // The latest persisted revision for a reused DB, read from v1's extraction_revisions (MAX(revision_id);
   // one DB = one root, so no workspace filter — design §4.4). workspaceId here is ONLY the null-sentinel
   // guard (a never-scanned workspace has no revision → 0); it is NOT a SQL filter. ... [degrade discipline unchanged]
   ```
   The inner-call edit is REQUIRED (it will not compile against D3's no-arg `LatestRevision()`); it lands in the Phase-4 atomic commit alongside D3.
4. Run `scripts/test.sh` — the v1 fixture revision test passes; the null/missing/corrupt tests still pass.
5. Commit: `feat(bootstrap): read fallback revision from extraction_revisions (no workspace filter)`.

**Acceptance**
- `ReadLatestRevisionOrZero` returns `MAX(revision_id)` over a v1 `extraction_revisions` fixture, ignoring the `workspaceId` arg as a filter.
- Null workspaceId → 0; missing DB → 0; corrupt/non-writable DB → throws loudly (all unchanged).
- No `canonical_revisions` reference remains reachable from this path (verified via C's `LatestRevision`).

---

### Task E3: WorkspaceTool.Open — drop workspace_id echo cross-check, remap revision to latest_revision_id + DB fallback

**Files**
- `src/Miller.Server/Tools/WorkspaceTool.cs` (lines 384-411)
- `tests/Miller.Tests/Server/WorkspaceToolTests.cs` (lines 168-197: `Report`/`RecordingScanOps`; lines 400-422: `Open_PrimesAndRegistersTheStableWorkspaceId`; any mismatch test)

**What**
`WorkspaceTool.Open` runs a prime scan (line 384) then:
1. **Cross-checks `report.WorkspaceId` against `stableWorkspaceId`** (lines 385-396): if julie's echoed id != Miller's stable id, it marks the registry row `Error` and returns a "cannot register" note. v1 has NO `report.WorkspaceId` (subsystem A removes the field), so this entire block is dead/uncompilable and must be removed. The producer-side root guarantee (exit-3 RootMismatch) replaces it: a scan against the wrong DB fails the scan itself, surfacing through the outer try/catch as `workspace failed: ...`.
2. **Maps revision** (lines 398-399): `report.Revision ?? IndexBootstrapService.ReadLatestRevisionOrZero(dbPath, stableWorkspaceId)`. A's accessor maps `report.Revision` to `revision.latest_revision_id` (null on a no-op delta scan). The `?? ReadLatestRevisionOrZero` DB fallback is the design-mandated behavior (§4.2: "Preserve the existing `report.Revision ?? <read latest from DB>` fallback") and STAYS — but now reads `extraction_revisions` via E2.
3. **Reads `report.SymbolsExtracted`** (line 407) for the open result — A maps to `counts.rows_written.symbols` (per-operation; reconciliation #13), call shape unchanged.

**Approach**
- Delete lines 385-396 (the whole `if (report.WorkspaceId is { } reportedWorkspaceId ...)` block including the registry `Error`/`MarkError` and the early-return note). The `_registry.UpsertSeen(... Ready)` + `MarkScanned` (lines 400-401) become unconditional after the scan.
- Keep `stableWorkspaceId`/`displayId` (lines 363-364) — they are Miller's registry identity, still passed to `UpsertSeen`/`MarkScanned`/`WorkspaceOpenResult.WorkspaceId`.
- Keep the revision fallback (lines 398-399) and `SymbolsExtracted` (line 407) verbatim.
- The comment at 405-406 ("SymbolsExtracted is julie's unsigned count...") stays accurate.

**Steps (TDD)**
1. **Write failing test.** In `WorkspaceToolTests`, the `RecordingScanOps.Scan` (lines 165-174) and `Report(...)` helper (lines 177-197) build the FLAT `ExtractReport` with `WorkspaceId:`/`Revision:` named args and `MillerExtractContract.Expected*` constants — all gone after A. Rebuild the `Report` helper against A's nested `ExtractReport` ctor (no `WorkspaceId` arg; revision via the `revision` block). Then update `Open_PrimesAndRegistersTheStableWorkspaceId` (lines 400-422) so the faked scan no longer echoes a workspace_id, and assert the open still registers the STABLE id and the revision from the report:
   ```csharp
   [Fact]
   public void Open_PrimesAndRegistersTheStableWorkspaceId_FromReportRevision()
   {
       using var fx = ... ; // existing arrange
       var harness = BuildHarness(
           fx, builtRevision: 4, workspaceId: Ws,
           openScan: (root, db, force) =>
               // v1 report: no WorkspaceId echo; latest_revision_id = 13 (A's accessor exposes report.Revision).
               ReportV1(root, db, latestRevisionId: 13, symbols: 42));

       string canonicalTarget = PathCanonicalizer.CanonicalizeRoot(targetDir);
       string output = harness.Tool.Workspace(operation: "open", path: targetDir, format: "json");

       using var doc = JsonDocument.Parse(output);
       string stableWorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalTarget);
       Assert.Equal(stableWorkspaceId, doc.RootElement.GetProperty("workspace_id").GetString());
       Assert.Equal(13, doc.RootElement.GetProperty("revision").GetInt64());
       WorkspaceRegistryRow? row = harness.Registry.Get(stableWorkspaceId);
       Assert.NotNull(row);
       Assert.Equal(13, row!.LastRevision);
   }

   [Fact]
   public void Open_NoOpScan_FallsBackToDbRevision()
   {
       // A no-op delta scan leaves report.Revision (latest_revision_id) null; Open must fall back to the
       // DB's MAX(revision_id) via ReadLatestRevisionOrZero (E2). Seed the prime DB with extraction_revisions=9.
       using var fx = ... ; // a v1 fixture written at targetDir/.miller/symbols.db with revision_id 9
       var harness = BuildHarness(
           fx, builtRevision: 4, workspaceId: Ws,
           openScan: (root, db, force) => ReportV1(root, db, latestRevisionId: null, symbols: 0));
       ...
       Assert.Equal(9, row!.LastRevision);
   }
   ```
   Replace the old `Report(string root, string dbPath, string workspaceId, long revision)` helper with `ReportV1(string root, string db, long? latestRevisionId, ulong symbols)` built on A's nested ctor.
   **Delete** any test that asserted the workspace_id-echo-mismatch "cannot register" note (there is no such echo in v1; that branch is removed). If one exists keyed on the old `reportedWorkspaceId`, remove it and note the removal in the commit message (it tested a now-impossible state).
2. Run `scripts/test.sh` — fails to compile (helper uses removed `WorkspaceId:` arg / `MillerExtractContract.Expected*`). Red.
3. **Implement.** In `WorkspaceTool.cs` delete lines 385-396; the scan + registration becomes:
   ```csharp
   ExtractReport report = _scanForOpen(canonicalRoot, dbPath, false);

   // v1 has no echoed workspace_id to cross-check: julie-extract self-rejects a DB built for a different
   // root (exit 3 RootMismatch, design §4.1), so a wrong-DB prime fails the scan above and surfaces through
   // the outer catch. The id we register is Miller's own stable id for canonicalRoot.
   long revision = report.Revision
       ?? IndexBootstrapService.ReadLatestRevisionOrZero(dbPath, stableWorkspaceId);
   _registry.UpsertSeen(stableWorkspaceId, displayId, canonicalRoot, dbPath, WorkspaceRegistryState.Ready);
   _registry.MarkScanned(stableWorkspaceId, revision);

   var result = new WorkspaceOpenResult(
       Path: canonicalRoot, DbPath: dbPath,
       SymbolsExtracted: (long)report.SymbolsExtracted,
       Revision: revision,
       WorkspaceId: stableWorkspaceId,
       DisplayId: displayId);
   return (WorkspaceRender.Open(result, json), 1, TelemetryOutcome.Ok);
   ```
4. Run `scripts/test.sh` — Open tests pass (stable id registered, revision from report, no-op falls back to DB).
5. Run `dotnet build Miller.slnx -c Release` — 0 warnings.
6. Commit: `feat(workspace): drop workspace_id echo cross-check in open; revision from latest_revision_id + DB fallback`.

**Acceptance**
- `WorkspaceTool.Open` no longer reads `report.WorkspaceId` (grep 0 hits in `WorkspaceTool.cs`).
- A prime scan registers `WorkspaceId.FromCanonicalRoot(canonicalRoot)` (Miller's stable id) unconditionally; a wrong-DB prime fails via the scan's exit-3, not a Miller echo check.
- `report.Revision` (latest_revision_id) drives the recorded revision; a no-op scan falls back to `ReadLatestRevisionOrZero` (extraction_revisions).
- `SymbolsExtracted` still rendered. Fast suite green; build 0 warnings.

---

### Task E4: CrossWorkspaceRefreshService — drop workspace_id echo cross-check, remap revision to latest_revision_id + DB fallback

**Files**
- `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs` (lines 110-141, 227-231)
- `tests/Miller.Tests/Server/CrossWorkspaceRefreshServiceTests.cs` (lines 321-339+: `Report` helper; any mismatch test)

**What**
Structurally identical to E3 for the cross-workspace refresh path. After acquiring the writer lock (line 106), `Refresh` runs a scan (line 112) then:
1. **Cross-checks `report.WorkspaceId` against `row.WorkspaceId`** (lines 113-127): on mismatch marks the row `Error` and returns `Failed`. v1 has no echoed id; A removes the field; this block is removed.
2. **Maps revision** (line 129): `report.Revision ?? _readLatestRevision(row.IndexDbPath)`. The `_readLatestRevision` delegate defaults to `ReadLatestRevision` (lines 227-231) which calls `FreshnessReader.LatestRevision`. D3 drops the workspace param (reconciliation #2): the delegate type `Func<string,string,long>` (`:15`,`:38`) becomes `Func<string,long>`, the `ReadLatestRevision` static (`:227-231`) loses its `workspaceId` parameter, and both call sites (`:129`,`:212`) drop the `row.WorkspaceId` argument. After D3 the underlying query reads `extraction_revisions` with no workspace filter. Keep the fallback itself.

**Approach**
- Delete lines 113-127 (the `if (report.WorkspaceId is { } reportedWorkspaceId ...)` block and its `Failed` return). The revision map + `MarkScanned` + status compute (lines 129-140) become unconditional after the scan.
- Change the `_readLatestRevision` delegate type `Func<string,string,long>` → `Func<string,long>` (`:15`,`:38`,`:59`), drop the `workspaceId` parameter from the `ReadLatestRevision` static (`:227-231`) so its body calls `reader.LatestRevision()` (D3, reconciliation #2), and drop the `row.WorkspaceId` argument at both call sites (`:129`,`:212`). This is a REQUIRED compile-driven edit, not optional.
- The `WaitForExternalRevision` path (lines 156-206) and `TryReadLatestRevision` (208-221) are unaffected (no workspace_id echo there) — leave them.

**Steps (TDD)**
1. **Write failing test.** Rebuild the `Report` helper (lines 321-339) on A's nested `ExtractReport` ctor (drop `WorkspaceId:`/`MillerExtractContract.Expected*`). Add a test that a refresh records the report's `latest_revision_id` and a no-op falls back to the DB read; delete any echo-mismatch test:
   ```csharp
   [Fact]
   public void Refresh_RecordsLatestRevisionId_FromReport()
   {
       var registry = ...; // a registered row at canonicalRoot, LastRevision 4
       var svc = BuildService(
           registry,
           scan: (root, db, force) => ReportV1(root, db, latestRevisionId: 11, symbols: 1),
           acquireLock: _ => new NoopLease());

       WorkspaceRefreshResult result = svc.Refresh(workspaceId);

       Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
       Assert.Equal(11, result.Revision);
       Assert.True(result.Scanned);
   }

   [Fact]
   public void Refresh_NoOpScan_FallsBackToDbRevision()
   {
       var svc = BuildService(
           registry,
           scan: (root, db, force) => ReportV1(root, db, latestRevisionId: null, symbols: 0),
           acquireLock: _ => new NoopLease(),
           readLatestRevision: _ => 8); // the DB fallback (extraction_revisions MAX) — Func<string,long> after D3

       WorkspaceRefreshResult result = svc.Refresh(workspaceId);
       Assert.Equal(8, result.Revision);
   }
   ```
   Replace `Report(root, dbPath, workspaceId, revision)` with `ReportV1(root, db, long? latestRevisionId, ulong symbols)`. Remove the test that fed a mismatched echoed id and expected `Failed` (impossible in v1) — note the removal in the commit message. **Update every `BuildService(readLatestRevision: …)` lambda in this file from the two-arg `(_, _) => …` to the one-arg `_ => …` shape** (the delegate is now `Func<string,long>`) — the current two-arg lambdas are at `:151`, `:181`, `:213`, `:299`, `:307` (reconciliation #2).
2. Run `scripts/test.sh` — fails to compile (helper uses removed args). Red.
3. **Implement.** In `CrossWorkspaceRefreshService.Refresh` delete lines 113-127; the body inside the `try` becomes:
   ```csharp
   ExtractReport report = _scan(row.CanonicalRoot, row.IndexDbPath, force);

   // No workspace_id echo to cross-check in v1: julie-extract self-rejects a DB built for a different root
   // (exit 3 RootMismatch, design §4.1), so a wrong-DB scan throws and is handled by the catch below.
   long revision = report.Revision ?? _readLatestRevision(row.IndexDbPath); // Func<string,long> after D3
   _registry.MarkScanned(row.WorkspaceId, revision, _utcNow());
   WorkspaceRefreshStatus status = revision > (row.LastRevision ?? 0)
       ? WorkspaceRefreshStatus.Refreshed
       : WorkspaceRefreshStatus.Unchanged;
   return new WorkspaceRefreshResult(
       status, row.WorkspaceId, row.CanonicalRoot, row.IndexDbPath, revision, Scanned: true);
   ```
   The outer `catch (Exception ex)` (lines 142-153) already converts a scan exit-3 (`IncompatibleExtractException`) into `Failed` with the message — that is the producer-enforced replacement for the removed echo check.
4. Run `scripts/test.sh` — refresh tests pass.
5. Run `dotnet build Miller.slnx -c Release` — 0 warnings (removed `error`/`reportedWorkspaceId` locals must not leave warnings).
6. Commit: `feat(workspace): drop workspace_id echo in cross-workspace refresh; revision from latest_revision_id + DB fallback`.

**Acceptance**
- `CrossWorkspaceRefreshService` no longer reads `report.WorkspaceId` (grep 0 hits).
- A successful refresh records `report.Revision` (latest_revision_id) or the DB fallback; a wrong-DB scan surfaces as `Failed` via the existing catch (exit-3), not a Miller echo compare.
- `WaitForExternalRevision` / lock-busy path unchanged. Fast suite green; build 0 warnings.

---

### Task E6: IndexerCore — flock_timeout->lock_timeout and prefer ReportDiagnostic.recoverable over hardcoded transient set

**Files**
- `src/Miller.Server/Hosting/IndexerCore.cs` (lines 115-120, 142-168)
- `tests/Miller.Tests/Server/IndexerCoreTests.cs` (lines 51-58 `Stub`; lines 225-315 transient/abnormal cases)

**What**
`IndexerCore.ExecuteIsolated` classifies a `JulieExtractFailedException` as transient (Info, keep-prior) vs abnormal (Error) using a **hardcoded** set `{ "data_loss_guard", "flock_timeout" }` (lines 119-120, used at 152). Two v1 changes:
1. **Rename `flock_timeout` → `lock_timeout`** (v1 `ReportCode::LockTimeout`, design §4.2).
2. **Prefer the per-diagnostic `recoverable: bool`** flag (A adds it to `ReportDiagnostic`) over the hardcoded set (design §10D).

**Plan tension to resolve (flagged):** v1 builds `data_loss_guard` with `recoverable=false` (verified: julie-extractors `commands.rs:1116`). A naive `if (ex.Errors.Any(e => e.Recoverable))` would FLIP `data_loss_guard` from transient (today) to abnormal, regressing the keep-prior contract Miller relies on (an empty re-parse must NOT escalate to Error — it self-heals on the next scan). **Resolution:** classify as recoverable if ANY diagnostic has `Recoverable == true` OR its `Code` is in a small explicit keep-prior set `{ "data_loss_guard" }`. This makes julie's `recoverable` signal the primary driver while preserving the data-loss-guard semantics v1's flag does not yet encode. `lock_timeout` is then covered by julie's `recoverable` flag (when a real lock-timeout path emits it) AND can be dropped from any hardcoded set entirely once julie marks it recoverable. (Note: `LockTimeout` is declared but not yet emitted in the current v1 working tree, so the scale suite cannot exercise it — coverage is unit-level here.)

**Approach**
- `ExtractError` becomes `ReportDiagnostic` (A's rename) with a `bool Recoverable` field; `JulieExtractFailedException.Errors` becomes `IReadOnlyList<ReportDiagnostic>` (A's change). `IndexerCore`'s `e.Code` reads still work; add `e.Recoverable`.
- Replace `TransientErrorCodes = { "data_loss_guard", "flock_timeout" }` with `KeepPriorCodes = { "data_loss_guard" }` (the codes whose keep-prior semantics v1's `recoverable` flag does not encode). Rename the field + update the comment to describe the new dual signal.
- The classification becomes: `bool isRecoverable = ex.Errors.Count > 0 && ex.Errors.Any(e => e.Recoverable || KeepPriorCodes.Contains(e.Code));`. Rename `isTransient` → `isRecoverable` and the log wording from "transient/expected" to "recoverable" to match v1's vocabulary.
- The empty-errors case (line 152's `ex.Errors.Count > 0` guard) stays abnormal → Error, preserving `ExecuteIsolated_FailedWithNoStructuredErrors_LogsAtError`.

**Steps (TDD)**
1. **Write failing tests.** In `IndexerCoreTests`, the `Failed(params string[] codes)` helper (lines 225-231) builds `ExtractError` with only `Code/Message/Path`. Rebuild it for A's `ReportDiagnostic` and add a `recoverable` knob:
   ```csharp
   private static JulieExtractFailedException Failed(params (string code, bool recoverable)[] diags)
   {
       var errors = diags
           .Select(d => new ReportDiagnostic(
               Code: d.code, Message: $"{d.code} happened", Path: "/repo/x.cs",
               RootRelativePath: "x.cs", Recoverable: d.recoverable, Details: null))
           .ToArray();
       return new JulieExtractFailedException(
           $"exit 1: {string.Join(",", diags.Select(d => d.code))}", errors, standardError: "");
   }
   ```
   Update the transient theory to v1 codes + the recoverable flag, and pin the data_loss_guard-with-recoverable:false keep-prior carve-out:
   ```csharp
   [Theory]
   [InlineData("lock_timeout", true)]          // julie marks a lock timeout recoverable
   [InlineData("data_loss_guard", false)]      // v1 emits recoverable:false, but Miller keeps-prior on it
   public void ExecuteIsolated_RecoverableFailure_LogsAtInformation_AndContinuesTheBatch(
       string code, bool recoverable)
   {
       var ops = new RecordingOps();
       ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] = Failed((code, recoverable));
       var logger = new RecordingLogger();
       var core = NewCore(ops, _ => true, logger);
       core.Queue.Enqueue(new WatchEvent("/repo/good1.cs", WatchEventKind.Modified));
       core.Queue.Enqueue(new WatchEvent("/repo/bad.cs", WatchEventKind.Modified));
       core.Queue.Enqueue(new WatchEvent("/repo/good2.cs", WatchEventKind.Modified));

       core.DrainAndProcess(headChanged: false);

       Assert.Equal(
           new[] { "update:/repo/good1.cs", "update:/repo/bad.cs", "update:/repo/good2.cs" }, ops.Calls);
       var entry = Assert.Single(logger.Entries, e => e.Exception is JulieExtractFailedException);
       Assert.Equal(LogLevel.Information, entry.Level);
       Assert.Contains(code, entry.Message, StringComparison.Ordinal);
       Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Debug));
   }

   [Theory]
   [InlineData("file_outside_root")]   // real v1 wire code (ReportCode::FileOutsideRoot, snake_case); recoverable:false, NOT keep-prior
   [InlineData("usage_error")]
   [InlineData("root_mismatch")]
   public void ExecuteIsolated_NonRecoverableFailure_LogsAtError(string abnormalCode)
   {
       var ops = new RecordingOps();
       ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] = Failed((abnormalCode, recoverable: false));
       ...
       Assert.Equal(LogLevel.Error, entry.Single().Level);
   }

   [Fact]
   public void ExecuteIsolated_MixedRecoverableAndAbnormal_TreatedAsRecoverable_IfAnyIsRecoverable()
   {
       var ops = new RecordingOps();
       ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] =
           Failed(("lock_timeout", recoverable: true), ("some_other_code", recoverable: false));
       ...
       Assert.Equal(LogLevel.Information, entry.Single().Level);
   }
   ```
   Keep `ExecuteIsolated_FailedWithNoStructuredErrors_LogsAtError_NotInformation` (a no-diag failure stays abnormal) and `ExecuteIsolated_UnexpectedException_LogsAtWarning`. Update the stderr-tail test (lines 361-378) to use `lock_timeout`. Also rebuild the `Stub(string status)` helper (lines 51-58) on A's nested `ExtractReport` ctor (its `Errors: Array.Empty<ExtractError>()` becomes `Array.Empty<ReportDiagnostic>()`).
2. Run `scripts/test.sh` — fails to compile (`ExtractError` removed, `Recoverable` not yet read). Red.
3. **Implement.** In `IndexerCore.cs`:
   - Replace lines 115-120:
     ```csharp
     // v1 emits a per-diagnostic `recoverable` flag; that is the primary keep-prior signal. The data-loss
     // guard is emitted recoverable:false by julie (commands.rs), yet its semantics ARE keep-prior (an empty
     // re-parse self-heals on the next scan), so it is carved in explicitly until julie marks it recoverable.
     // lock_timeout (v1 ReportCode::LockTimeout, formerly flock_timeout) rides julie's recoverable flag.
     private static readonly HashSet<string> KeepPriorCodes =
         new(StringComparer.Ordinal) { "data_loss_guard" };
     ```
   - At line 152 replace the classification + the Info/Error branches:
     ```csharp
     bool isRecoverable = ex.Errors.Count > 0
         && ex.Errors.Any(e => e.Recoverable || KeepPriorCodes.Contains(e.Code));

     if (isRecoverable)
     {
         _logger?.LogInformation(ex,
             "extract op {Op} hit a recoverable/expected failure ({Codes}); keeping the prior index and " +
             "retrying on the next scan. julie stderr: {ExtractStderrTail}",
             Describe(op), described.Codes, described.StderrTail);
     }
     else { ... existing Error branch ... }
     ```
   - `ExtractErrorLog.Describe(ex)` (Logging subsystem) iterates `ex.Errors` codes — confirm it compiles against A's `ReportDiagnostic` (it reads `e.Code`; if it reads `ExtractError` by type, that's an A-coordinated change, not E's). Flag if `ExtractErrorLog.cs` needs a follow-up.
4. Run `scripts/test.sh` — recoverable/abnormal/mixed/no-diag cases pass.
5. Run `dotnet build Miller.slnx -c Release` — 0 warnings.
6. Commit: `feat(indexer): classify keep-prior by ReportDiagnostic.recoverable + data_loss_guard carve-out; lock_timeout rename`.

**Acceptance**
- `IndexerCore` reads `ReportDiagnostic.Recoverable` as the primary keep-prior signal; `KeepPriorCodes` is the minimal `{ data_loss_guard }` carve-out (NOT a re-listing of every transient code).
- `flock_timeout` no longer appears in `IndexerCore.cs` (replaced by `lock_timeout`/the recoverable flag).
- A `data_loss_guard` failure (recoverable:false) still logs Info/keep-prior; a `file_outside_root`/`root_mismatch`/`usage_error` (recoverable:false, not carved in) logs Error; a no-diagnostic failure logs Error; an unexpected exception logs Warning.
- Mixed-diagnostic failure with any recoverable diagnostic logs Info. Fast suite green; build 0 warnings.


---

## Subsystem F: Test-signal consumers (TestRole removal)

Drops the `TestRole` string-record type from Miller entirely and replaces every consumer with the typed `bool IsTest` signal that julie-extractors v1 promotes to an indexed column (`symbols.is_test INTEGER NOT NULL DEFAULT 0`, verified at `julie-extractors/crates/julie-extract-artifact/src/schema.rs:93`). Per design §8 (D4) Miller only ever used `TestRole` as a presence predicate (exclude a test HttpClient url literal from the route bridge), so a bool is a lossless replacement. `test_container`/`test_lifecycle` are NOT needed for parity and are left unread (future opportunity).

**Atomic-commit constraint (read first):** subsystem B owns `IndexedSymbol.cs` and `SqliteSymbolReader.cs`. F1 deletes `TestRole.cs` from `Miller.Core`; that compile-breaks `IndexedSymbol.cs:25` and `SqliteSymbolReader.cs:77,111-118` (both B-owned) until B removes the `TestRole? TestRole` member and stops constructing it. F4 consumes `symbol.IsTest` (already present on `IndexedSymbol` at line 24). So **F1+F4 and B's IndexedSymbol/SqliteSymbolReader edits compile together or not at all** — land them in one commit (design §12: one atomic migration). The fast suite is only green once both halves are in.

The whole subsystem is pure `Miller.Core` + one `Miller.Indexing` projection method + their tests; no DB, no I/O, no subprocess. Every test here is fast-suite (`Category!=Scale`). Verification is `scripts/test.sh` (fast, <30s) and `dotnet build Miller.slnx -c Release` (0 warnings).

---

### Task F1 — Replace `SymbolDetail.TestRole` with `bool IsTest` and delete `TestRole.cs`

**Files**
- `src/Miller.Core/Contracts/SymbolDetail.cs` (rewrite record member + doc)
- `src/Miller.Core/Contracts/TestRole.cs` (DELETE)
- `tests/Miller.Tests/Contracts/TestRoleTests.cs` (DELETE + replace with `IsTest`-carrier tests)

**What**
`SymbolDetail` is a pure value record (`src/Miller.Core/Contracts/SymbolDetail.cs:30-38`) whose 7th positional parameter is `TestRole? TestRole`. Replace it with `bool IsTest`. Delete the `TestRole` record type (`TestRole.cs`) — nothing in Miller needs the verbatim role string once the predicate is a bool. The old `TestRoleTests.cs` pinned `TestRole.IsTest` semantics (presence of a non-blank role => test); that semantic moves upstream into julie's typed column, so the file is deleted. Because `SymbolDetail` no longer has a meaningful standalone test assertion (it is a plain projection), the replacement test pins the new shape: a `SymbolDetail` carries an `IsTest` bool that flows verbatim from its constructor.

**Approach (non-mechanical — record-shape change with a deletion):** This is the load-bearing change of the subsystem. TDD: write the new contract test red (against the not-yet-changed record), watch it fail to compile, change the record, delete the dead type and its test, go green.

**Steps**

1. Write the replacement test file. Delete `tests/Miller.Tests/Contracts/TestRoleTests.cs` and create `tests/Miller.Tests/Contracts/SymbolDetailTests.cs` with real assertions on the new `IsTest` member:

```csharp
using Miller.Core.Contracts;
using Xunit;

namespace Miller.Tests.Contracts;

/// <summary>
/// Pins <see cref="SymbolDetail.IsTest"/> — the typed test signal Miller reads from julie-extractors v1's
/// indexed <c>symbols.is_test</c> column (schema v1). It replaces the old <c>TestRole</c> string record:
/// Miller only ever used the role as a presence predicate (exclude a test HttpClient url literal from the
/// route bridge), so the typed boolean is a lossless, parse-free replacement.
/// </summary>
public sealed class SymbolDetailTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsTest_FlowsVerbatimFromConstructor(bool isTest)
    {
        var detail = new SymbolDetail(
            Id: "s1",
            Name: "Foo",
            Kind: "method",
            FilePath: "src/Foo.cs",
            Signature: "void Foo()",
            Namespace: null,
            IsTest: isTest,
            ParentClassName: null);

        Assert.Equal(isTest, detail.IsTest);
    }

    [Fact]
    public void IsTest_DefaultsAreNotAssumed_ProductionSymbolIsFalse()
    {
        var prod = new SymbolDetail("s2", "Service", "class", "src/Service.cs", "class Service", "App", IsTest: false, ParentClassName: null);
        Assert.False(prod.IsTest);
    }
}
```

2. Run `scripts/test.sh`. It FAILS TO COMPILE: `SymbolDetail` has no `IsTest` member and its 7th parameter is still `TestRole? TestRole`. This is the expected red — the harness reports build errors against `SymbolDetail`.

3. Rewrite `src/Miller.Core/Contracts/SymbolDetail.cs`. Replace the `TestRole` parameter (line 37) and its doc block (lines 21-24). New record:

```csharp
public sealed record SymbolDetail(
    string Id,
    string Name,
    string Kind,
    string FilePath,
    string Signature,
    string? Namespace,
    bool IsTest,
    string? ParentClassName);
```

   Replace the `<param name="TestRole">` doc block (lines 21-24) with:

```csharp
/// <param name="IsTest">
/// julie-extractors v1's typed <c>symbols.is_test</c> signal (cross-language, AST-accurate). Used to exclude
/// test HttpClient url literals from the route bridge (see <see cref="Resolver.RouteBridge"/>). Replaces the
/// pre-v1 <c>test_role</c> string — Miller only ever needed the presence predicate.
/// </param>
```

   Keep `IsTest` in the SAME positional slot (7th) the old `TestRole` occupied, so callers using positional or trailing-named args have a 1:1 column swap. Note the parameter is NON-nullable `bool` (no default) — every constructor must now pass it.

4. Delete `src/Miller.Core/Contracts/TestRole.cs`. (The `<see cref="Contracts.TestRole"/>` cross-references in `RouteBridge.cs` are removed in F2; the build will surface any remaining reference as an error, which is the guard that nothing was missed.)

5. Run `scripts/test.sh`. Still red until F2/F3/F4/F5 land (every `SymbolDetail`/`TsClientCall` construction and the `RouteBridge` predicate are stale). That is expected — F1 alone is not independently green; the subsystem goes green at the end of F5. (If you want an intermediate green, do F1→F5 as one working set, then run once.)

6. After F2–F5 and B's IndexedSymbol/reader edits: `scripts/test.sh` green, `dotnet build Miller.slnx -c Release` 0 warnings. Commit.

**Acceptance**
- `src/Miller.Core/Contracts/TestRole.cs` no longer exists.
- `SymbolDetail` exposes `bool IsTest` (7th param), no `TestRole` member.
- `tests/Miller.Tests/Contracts/TestRoleTests.cs` removed; `SymbolDetailTests.cs` asserts `IsTest` round-trips and a production symbol is `false`.
- `grep -rn "Contracts.TestRole\|new TestRole(" src tests` returns zero hits (excluding obj/bin).

---

### Task F2 — `RouteBridge` / `TsClientCall`: bool `IsTest` predicate

**Files**
- `src/Miller.Core/Resolver/RouteBridge.cs` (`TsClientCall` record + `IsRealClientCall`)
- `tests/Miller.Tests/Resolver/RouteBridgeTests.cs` (`Call` factory + the test-exclusion case)

**What**
`TsClientCall` (`RouteBridge.cs:31-35`) carries `TestRole? TestRole` as its 2nd parameter; `IsRealClientCall` (`RouteBridge.cs:202-212`) filters it out with the property-pattern `if (call.TestRole is { IsTest: true })` at line 206. Replace the carrier with `bool IsTest` and the predicate with a plain `if (call.IsTest)`. Semantics are unchanged: a test client call is still excluded from the route bridge.

**Approach (non-mechanical — predicate rewrite + record member; pin the filter behaves identically).** TDD: the existing `Resolve_CsharpTestHttpClientLiteral_Excluded_FromTsSide` test (`RouteBridgeTests.cs:196-209`) already pins "a test client call is excluded." Update it to pass `IsTest: true` instead of `testRole: new TestRole("test_case")`, keep the assertion. Add an explicit positive: a non-C# url literal flagged `IsTest: true` is ALSO excluded (proves the predicate keys on the bool, not on the C# language short-circuit at line 209).

**Steps**

1. Update the `Call` factory in `RouteBridgeTests.cs:32-44` from `TestRole? testRole = null` to `bool isTest = false`, and pass it as the `TsClientCall`'s 2nd positional arg:

```csharp
private static TsClientCall Call(
    string carrier,
    string route,
    string language = "typescript",
    bool isTest = false,
    string file = "web/src/api/client.ts",
    int line = 12,
    string containingSymbolId = "ts.fn") =>
    new(
        new LiteralRecord(route, "url", carrier, 0, language, containingSymbolId, new SourceSpan(0, route.Length)),
        isTest,
        file,
        line);
```

2. Update the exclusion test (`RouteBridgeTests.cs:196-209`) call site at line 203 to `language: "csharp", isTest: true` (drop `new TestRole("test_case")`), and update the comment. Add a new test that pins the bool drives the filter independently of language:

```csharp
[Fact]
public void Resolve_TypescriptTestClientCall_Excluded_FromTsSide()
{
    // A test-flagged url literal is excluded even in a frontend language — the predicate keys on IsTest,
    // not on the C# language short-circuit. A test fetch() in a *.spec.ts must not produce a Hits edge.
    var resolver = new SymbolResolver([]);
    var input = new RouteBridgeInput(
        [Call("axios.get", "/api/appsettings", language: "typescript", isTest: true)],
        [Endpoint("httpget", "AppSettingsController", "List")]);

    Assert.DoesNotContain(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Hits);
}
```

   Also fix the sql-literal raw-construction at `RouteBridgeTests.cs:218`: `new TsClientCall(sql, null, "Data/Repo.cs", 5)` becomes `new TsClientCall(sql, false, "Data/Repo.cs", 5)` (2nd arg is now `bool`, `null` no longer compiles).

3. Run `scripts/test.sh`. FAILS TO COMPILE: `TsClientCall`'s 2nd parameter is still `TestRole?`; `Call` passes a `bool`; `RouteBridge.cs:206` still references `call.TestRole`. Expected red.

4. Rewrite `TsClientCall` in `RouteBridge.cs:31-35`:

```csharp
public sealed record TsClientCall(
    LiteralRecord Literal,
    bool IsTest,
    string FilePath,
    int Line);
```

   Replace its `<param name="TestRole">` doc (lines 25-28) with an `<param name="IsTest">` block describing julie v1's typed `is_test` of the containing TS function (a present test flag excludes the literal as a test HttpClient call).

5. Rewrite the predicate at `RouteBridge.cs:202-212`. Change line 206 from:

```csharp
        if (call.TestRole is { IsTest: true })
            return false;
```

   to:

```csharp
        if (call.IsTest)
            return false;
```

   Update the `IsRealClientCall` doc (lines 196-201) wording from `test_role HttpClient call` to `is_test HttpClient call`.

6. Run `scripts/test.sh` after F1/F3/F4/F5 + B land — green. `dotnet build Miller.slnx -c Release` 0 warnings.

**Acceptance**
- `TsClientCall.IsTest` is a `bool`; no `TestRole` reference in `RouteBridge.cs`.
- `IsRealClientCall` excludes a call iff `call.IsTest || kind!=url || language==csharp` (the bool replaces the `{ IsTest: true }` pattern).
- `RouteBridgeTests`: existing C# exclusion case still green via `isTest: true`; the new TS-test-exclusion case green.
- `grep -n "TestRole" src/Miller.Core/Resolver/RouteBridge.cs` returns zero hits.

---

### Task F3 — `BridgeGraphBuilder.ReduceClientCalls`: carry bool `IsTest`

**Files**
- `src/Miller.Core/Graph/BridgeGraphBuilder.cs` (`ReduceClientCalls` + class doc-comment)
- `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs` (`Type`/`Method` factories — see F5; plus add a reduction test)

**What**
`ReduceClientCalls` (`BridgeGraphBuilder.cs:398-422`) builds each `TsClientCall` by looking up the literal's containing symbol and reading its `TestRole` (`testRole = container.TestRole;` at line 415, declared `TestRole? testRole = null` at line 411). After F2, `TsClientCall`'s carrier is `bool IsTest`, and after F1+B, `SymbolDetail` exposes `bool IsTest` (not `TestRole`). Rewrite the reduction to read `container.IsTest` (bool; default `false` when the container symbol is absent from the lookup — semantically identical to the old null = "unknown container, treat as non-test").

**Approach (non-mechanical — the reduction wires the bridge's test signal; add a test proving the container's IsTest flows into the TsClientCall and excludes it).** There is currently NO `BridgeGraphBuilderTests` case that exercises a test-flagged container through `ReduceClientCalls` (verified: `grep test_role tests/.../BridgeGraphBuilderTests.cs` only hits the doc-comment at line 10). Add one — TDD red first.

**Steps**

1. Add a reduction test to `BridgeGraphBuilderTests.cs`. It builds a url literal whose containing symbol is `IsTest: true`, and asserts the end-to-end build produces NO Hits edge (the test container call is excluded), versus a production container which DOES. Use the file's existing `Method`/`Type` factories (updated in F5) and the `LiteralRecord` shape from `RouteBridgeTests`:

```csharp
[Fact]
public void ReduceClientCalls_TestContainerLiteral_ExcludedFromBridge()
{
    // A url literal whose containing symbol is julie-flagged is_test => the reduced TsClientCall carries
    // IsTest=true => RouteBridge.IsRealClientCall drops it => no Hits edge, even with a matching endpoint.
    var symbols = new List<SymbolDetail>
    {
        // containing TS function, flagged test (IsTest is the 7th positional SymbolDetail param)
        new("ts.testfn", "should_call_api", "function", "web/src/api.spec.ts", "function should_call_api()", "Web", IsTest: true, ParentClassName: null),
        new("cs.endpoint", "List", "method", "Api/Controllers/AppSettingsController.cs", "Task<ActionResult> List()", "Api.Controllers", IsTest: false, ParentClassName: "AppSettingsController"),
        new("cs.ctrl", "AppSettingsController", "class", "Api/Controllers/AppSettingsController.cs", "class AppSettingsController", "Api.Controllers", IsTest: false, ParentClassName: null),
    };
    var literals = new List<LiteralRecord>
    {
        new("/api/appsettings", "url", "axios.get", 0, "typescript", "ts.testfn", new SourceSpan(0, 16)),
    };
    var annotations = new List<SymbolAnnotation>
    {
        new("cs.endpoint", "httpget", "[HttpGet]"),     // verb on the method
        new("cs.ctrl", "route", "[Route(\"api/[controller]\")]"),
    };

    var graph = BridgeGraphBuilder.Build(symbols, [], literals, annotations, []);

    Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
}
```

   (Confirm the `SymbolAnnotation` constructor arity against `Miller.Core.Contracts.SymbolAnnotation` while wiring this — the example assumes `(SymbolId, AnnotationKey, RawText)`; adjust to the real record if it differs. If a positive-control sibling is wanted, add a near-identical test with the container `IsTest: false` asserting `Assert.Contains(... Hits)`.)

2. Run `scripts/test.sh`. Red (compile error: `SymbolDetail` 7th arg / `ReduceClientCalls` reads `container.TestRole`).

3. Rewrite `ReduceClientCalls` in `BridgeGraphBuilder.cs:398-422`. Change lines 411-419:

```csharp
        foreach (var literal in ordered)
        {
            bool isTest = false;
            if (!string.IsNullOrEmpty(literal.ContainingSymbolId) &&
                symbolsById.TryGetValue(literal.ContainingSymbolId, out var container))
            {
                isTest = container.IsTest;
            }

            var site = SiteFor(literal, symbolsById, literalSites);
            calls.Add(new TsClientCall(literal, isTest, site.FilePath, site.Line));
        }
```

   Update the `ReduceClientCalls` doc (lines 393-396): `attaching the containing symbol's <c>test_role</c>` => `attaching the containing symbol's <c>is_test</c> flag`.

4. Update the class-level doc-comment reference at `BridgeGraphBuilder.cs:23-24` (the `TsClientCall reduction` bullet): `attach the containing symbol's <c>test_role</c>` => `attach the containing symbol's <c>is_test</c> flag`.

5. Run `scripts/test.sh` (after the full F set + B) — green. Build 0 warnings.

**Acceptance**
- `ReduceClientCalls` reads `container.IsTest` into a `bool`; no `TestRole` reference remains in `BridgeGraphBuilder.cs`.
- New `ReduceClientCalls_TestContainerLiteral_ExcludedFromBridge` test green; an `IsTest:false` container still yields a Hits edge (positive control if added).
- `grep -n "test_role\|TestRole" src/Miller.Core/Graph/BridgeGraphBuilder.cs` returns zero hits.

---

### Task F4 — `RepositoryIndexLoader.ProjectToSymbolDetails`: map `IndexedSymbol.IsTest`

**Files**
- `src/Miller.Indexing/RepositoryIndexLoader.cs` (`ProjectToSymbolDetails`, lines 102-128)

**What**
`ProjectToSymbolDetails` (`RepositoryIndexLoader.cs:102-128`) projects each `IndexedSymbol` into a Core `SymbolDetail`, currently passing `TestRole: symbol.TestRole` at line 124. `IndexedSymbol` already carries `bool IsTest` (`IndexedSymbol.cs:24`) in addition to the to-be-removed `TestRole? TestRole` (`IndexedSymbol.cs:25`, removed by subsystem B). Change the projection to pass `IsTest: symbol.IsTest`.

**Cross-dependency (atomic):** This line cannot compile until F1 has changed `SymbolDetail` (no `TestRole` param) AND B has kept/confirmed `IndexedSymbol.IsTest`. B independently removes `IndexedSymbol.TestRole`; once removed, `symbol.TestRole` here would be a compile error anyway — so this edit and B's land together.

**Approach (mechanical single-line projection swap; covered by existing integration assertions).** No new test is authored here in isolation — `ProjectToSymbolDetails` is private and exercised through `RepositoryIndexLoader`/`MillerRepositoryIndex` build tests and the bridge tests. The `IsTest` flow is already pinned at the graph layer (`MillerRepositoryIndexTests.cs:185-186` asserts `repo.Graph.IsTest(...)` via `IndexedSymbol.IsTest`) and now additionally through F3's bridge-exclusion test. The change is verified by those plus the build.

**Steps**

1. Edit `RepositoryIndexLoader.cs:124`. In the `details.Add(new CoreSymbolDetail(...))` block (lines 117-125), change:

```csharp
                TestRole: symbol.TestRole,
```

   to:

```csharp
                IsTest: symbol.IsTest,
```

   (The named-arg slot maps 1:1 to the new `SymbolDetail.IsTest` 7th parameter from F1.)

2. Run `scripts/test.sh` (with the full F set + B's IndexedSymbol/reader edits in the working tree). Green: `MillerRepositoryIndexTests` `Graph.IsTest` assertions still pass (they read `IndexedSymbol.IsTest`, unchanged), and F3's bridge test now sees the test flag flow `IndexedSymbol.IsTest -> SymbolDetail.IsTest -> TsClientCall.IsTest`.

3. `dotnet build Miller.slnx -c Release` — 0 warnings.

**Acceptance**
- `ProjectToSymbolDetails` passes `IsTest: symbol.IsTest`; no `TestRole` reference in `RepositoryIndexLoader.cs`.
- `grep -n "TestRole" src/Miller.Indexing/RepositoryIndexLoader.cs` returns zero hits.
- Existing `MillerRepositoryIndexTests` `Graph.IsTest` cases green; F3 bridge-exclusion test green (end-to-end `IsTest` flow).

---

### Task F5 — Mechanical `SymbolDetail`-ctor fixup across sibling bridge test fixtures

**Files (test fixtures owned by other bridge-leg subsystems' tests; F owns the compile-fix because F changed the record arity)**
- `tests/Miller.Tests/Resolver/DtoEntityBridgeTests.cs`
- `tests/Miller.Tests/Resolver/EntityTableBridgeTests.cs`
- `tests/Miller.Tests/Resolver/FieldSetExtractorTests.cs`
- `tests/Miller.Tests/Resolver/SymbolResolverTests.cs`
- `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`

**What**
Changing `SymbolDetail`'s 7th parameter from `TestRole? TestRole` to `bool IsTest` (F1) breaks every fixture that constructs a `SymbolDetail` passing a `TestRole`/`null` in that slot. These are pure 1:1 substitutions; the assertions in these files are NOT about the test signal and stay untouched.

**Approach (mechanical bulk rename — exact mapping table + one representative snippet + a locking signal).** Two construction styles exist: trailing-named `TestRole: null` and trailing-positional `..., null, null)` / `..., null, <parent>)`. Both map the test-signal slot to `IsTest: false` (named) or `false` (positional). No fixture passed a non-null `TestRole` here (verified: only `RouteBridgeTests.cs:203` did, handled in F2), so every occurrence becomes the `false`/`IsTest: false` literal.

**Exact mapping table** (file:line — current text -> replacement text):

| File:line | Old (test-signal slot) | New |
|---|---|---|
| `BridgeGraphBuilderTests.cs:23` | `Namespace: ns, TestRole: null, ParentClassName: null` | `Namespace: ns, IsTest: false, ParentClassName: null` |
| `BridgeGraphBuilderTests.cs:26` | `Namespace: "Api.Controllers", TestRole: null, ParentClassName: parentClassName` | `Namespace: "Api.Controllers", IsTest: false, ParentClassName: parentClassName` |
| `BridgeGraphBuilderTests.cs:15` (doc) | `...Signature, Namespace, TestRole, ParentClassName)` | `...Signature, Namespace, IsTest, ParentClassName)` |
| `DtoEntityBridgeTests.cs:28` (doc) | `...Signature, Namespace, TestRole, ParentClassName).` | `...Signature, Namespace, IsTest, ParentClassName).` |
| `DtoEntityBridgeTests.cs:417` | `..."Domain/Account.cs", "public class Account", "Domain", null, null)` | `..."Domain/Account.cs", "public class Account", "Domain", false, null)` |
| `DtoEntityBridgeTests.cs:418` | `..."Dtos/AccountDto.cs", "public class AccountDto", "Dtos", null, null)` | `..."Dtos/AccountDto.cs", "public class AccountDto", "Dtos", false, null)` |
| `EntityTableBridgeTests.cs:21` (doc) | `...Signature, Namespace, TestRole, ParentClassName).` | `...Signature, Namespace, IsTest, ParentClassName).` |
| `EntityTableBridgeTests.cs:76` | `new SymbolDetail("ctx", "MyraNextContext", "class", "Data/MyraNextContext.cs", ... null, null)` | replace the trailing `null, null` test-signal+parent pair -> `false, null` (read the full multi-line ctor at :76 and swap the test-signal `null` to `false`; leave the `ParentClassName` arg as-is) |
| `FieldSetExtractorTests.cs:19` | `Namespace: null, TestRole: null, ParentClassName: null` | `Namespace: null, IsTest: false, ParentClassName: null` |
| `SymbolResolverTests.cs:19` | `Namespace: ns, TestRole: null, ParentClassName: null` | `Namespace: ns, IsTest: false, ParentClassName: null` |

(Note `RouteBridgeTests.cs:266` — `new SymbolDetail("i1", "IProject", ..., "Models", null, null)` — is owned by F via RouteBridgeTests and is also a `null -> false` swap in the test-signal slot; fix it in F2's edit pass or here. `RouteBridgeTests.cs:29`'s `Dto` factory `new(id, name, "class", file, $"public class {name}", ns, null, null)` likewise: the 7th positional `null` -> `false`.)

**One representative snippet** (the `BridgeGraphBuilderTests` `Type` factory, lines 22-23):

```csharp
private static SymbolDetail Type(string id, string name, string kind = "class", string? ns = null, string file = "src/X.cs") =>
    new(id, name, kind, file, Signature: name, Namespace: ns, IsTest: false, ParentClassName: null);
```

**Steps**

1. Apply the mapping table above with exact `Edit` calls (one per line; the named-arg form `TestRole: null -> IsTest: false`, the positional form `, null, <parent>` -> `, false, <parent>` keeping the `ParentClassName` arg). For `EntityTableBridgeTests.cs:76` read the full ctor (it spans multiple lines) before editing to identify which trailing `null` is the test-signal slot vs `ParentClassName`.

2. Run `scripts/test.sh`. With F1–F4 + B in the tree, the whole fast suite now compiles and is GREEN: the sibling bridge-leg assertions (Dto-entity, entity-table, field-set, symbol-resolver) pass unchanged because their behavior never depended on the test signal — the swap is type-only.

3. Lock the rename so a stray `TestRole` cannot reappear in fixtures: the grep guard below is the acceptance signal (no new test file needed — the compile + green suite IS the lock, plus the explicit `IsTest`-flow tests added in F2/F3).

```bash
grep -rn "TestRole" src tests | grep -v "/obj/" | grep -v "/bin/"
```

   must return ZERO lines across the whole repo after F1–F5.

4. `dotnet build Miller.slnx -c Release` — 0 warnings.

**Acceptance**
- All five fixture files compile against the new `SymbolDetail(..., bool IsTest, ...)` signature.
- Repo-wide `grep -rn "TestRole" src tests` (excluding obj/bin) returns zero hits.
- Fast suite green; build 0 warnings.


---

## Subsystem G: Packaging / restore / docs

This subsystem moves Miller's acquisition + packaging seam off `julie-server` 7.13.2 and onto `julie-extract` v1. It is **download-based** (the design's D1 — the release is **published** as `v2.0.0`, repo `anortham/julie-extractors`, with the four asset names + SHA-256 digests confirmed, fully resolving design §9.1-9.3) with a **validated from-source path as the early unblocker** so subsystems B/C/D/H can build and read a real v1 DB without waiting on the network. Per design §12 step 1 and §12 step 7, **G1 lands first**; the download repoint (G2) lands last — but G2 is **no longer blocked**, since every §9 unknown is now resolved.

**Verified upstream facts (read, not guessed):**
- From-source target: `cargo build --release -p julie-extract-cli --bin julie-extract` against a julie-extractors checkout (workspace root manifest) emits `target/release/julie-extract`. Confirmed: `crates/julie-extract-cli/Cargo.toml` `name = "julie-extract-cli"`, `[[bin]] name = "julie-extract"`; it is a `[workspace] members` entry.
- `julie-extract --version` self-identifies via clap's default (`CARGO_PKG_VERSION`); the crate is `version = "2.0.0"`, so a **fresh from-source build and the published v2.0.0 assets both print `julie-extract 2.0.0`**. (A locally-cached pre-bump binary may still print `0.1.0` — irrelevant once rebuilt.) The restore script must **not** hard-compare the product version: the from-source path legitimately builds whatever the checkout holds, and the real compatibility contract is the schema/contract version enforced at runtime by the gate (D7), not the product-version string. G1 therefore replaces the old version-equality assert with a name-only smoke assert (binary runs and self-identifies as `julie-extract`).
- Download asset shape (`.github/workflows/release-binaries.yml` + `xtask/src/release.rs:91-100`): archives named `julie-extract-v{VER}-{triple}.tar.gz` (`.zip` for `x86_64-pc-windows-msvc`); inside, the binary is **nested at `./dist/{triple}/julie-extract[.exe]`** (the workflow does `tar -czf -C $PACKAGE_DIR .`), NOT flat at the archive root like old julie. A per-binary `.sha256` sidecar is published. All four triples build.

### Task G1 — From-source restore + binary-name plumbing (EARLY UNBLOCKER)

**Files**
- Rename `scripts/restore-julie-server.sh` → `scripts/restore-julie-extract.sh` (git mv).
- Rename `scripts/restore-julie-server.ps1` → `scripts/restore-julie-extract.ps1` (git mv).
- `src/Miller.Indexing/JulieExtractRunner.cs` (ctor `:41-51`, `Locate` `:58-74` — binary-name literal + restore-script-name strings ONLY; coordinate with A).
- `src/Miller.Server/Hosting/WorkspaceContext.cs` (doc comments `:11`, `:19`).
- `tests/Miller.Tests/ScaleTestSupport.cs` (`:33` binary literal, `:47` skip message; coordinate with H).
- `tests/Miller.Tests/Server/WorkspaceToolTests.cs` (`:120` binary literal in the fast suite).

**What**
Get a runnable `julie-extract` binary into `.tools/` via the from-source path (`cargo build --release -p julie-extract-cli --bin julie-extract`), and flip every `julie-extract`-vs-`julie-server` binary-name literal Miller uses to resolve/spawn the tool. This is the **first** task in the whole migration (design §12.1): once `.tools/julie-extract` exists, subsystems B/C/D/H can run their readers against a real v1 DB. It deliberately does NOT touch the download path (G2) or the version contract constant (A's `MillerExtractContract`).

**Approach**
- Bash + PowerShell restore from-source change is **not a pure literal swap** — the build invocation and the version-assert semantics change:
  - Build: old `cargo build --manifest-path "${SOURCE_MANIFEST}" --bin julie-server --release` (`.sh:101`) → `cargo build --manifest-path "${SOURCE_MANIFEST}" --release -p julie-extract-cli --bin julie-extract`. `SOURCE_MANIFEST` stays the workspace-root `Cargo.toml`; `-p julie-extract-cli` selects the member crate. (Equivalent `.ps1:54`.)
  - Output path: `SOURCE_BINARY="${SOURCE_ROOT}/target/release/julie-server"` (`.sh:98`) → `.../target/release/julie-extract`. (`.ps1:51` → `target\release\julie-extract.exe`.)
  - **Version assert (load-bearing correctness fix):** the current `.sh:109-115` greps `--version` output for the pin string and exits 1 on mismatch. That couples the restore to an exact product-version string, which is wrong for the from-source path (it builds whatever version the checkout holds — e.g. a dev on a newer julie-extractors) and is a brittle proxy for compatibility. Replace with a name-only smoke assert: run `"${BINARY}" --version`, require the output to be non-empty AND start with `julie-extract` (i.e. the binary executes and self-identifies). Do **not** compare the numeric version — the compatibility gate is the schema/contract version (subsystem A/B/C), per design D7. Same change in `.ps1:63-66` (`-notlike "* $($config.version)*"` → `-notlike "julie-extract*"`).
  - The `--from-source path is not a Julie checkout` guard (`.sh:87-90`, `.ps1:42-44`) still checks `Cargo.toml` at the source root — correct, since julie-extractors' workspace root has one.
  - Rename every in-script self-reference: header comment `restore-julie-server.sh` → `restore-julie-extract.sh`, the `--from-source` usage hints (`.sh:18-19,156`, `.ps1:11-12,101`), and all `julie-server`/`julie-server.exe` literals → `julie-extract`/`julie-extract.exe`. The `.ps1` temp staging dir already coincidentally named `julie-extract-<guid>` (`.ps1:129`) — rename it to avoid confusion with the real binary, e.g. `julie-extract-stage-<guid>`.
- `JulieExtractRunner.cs`: `Locate` `:61` `string binaryName = OperatingSystem.IsWindows() ? "julie-server.exe" : "julie-server";` → `"julie-extract.exe" : "julie-extract"`. Update the ctor `:47-49` and `Locate` `:71-73` error strings: `julie-server binary not found` → `julie-extract binary not found`; `scripts/restore-julie-server.sh`/`.ps1` → `scripts/restore-julie-extract.sh`/`.ps1`. The `PinnedJulieServerVersion` reference in those strings is **A's rename** — coordinate: if A lands `MillerExtractContract.PinnedJulieExtractVersion` first, use the new name; otherwise leave the symbol untouched and let A flip it in the contract task (the string interpolation site moves with the constant rename). Do NOT touch `BuildScanArgs`/`BuildInfoArgs`/`Interpret` (A owns those).
  - **PATH-lookup test seam (codex#4 — the no-binary test is PATH-flaky without it).** `Locate` falls back to PATH via `FindOnPath` (`:66`→`:76-88`, reads `Environment.GetEnvironmentVariable("PATH")` live). Now that `julie-extract` is *installable on a developer/CI PATH* (julie-extractors README:34-37), a stray binary on the ambient PATH makes any "absent ⇒ throws" test resolve a real binary and flake. Add a parallel-safe seam so the not-found branch is deterministic with **no process-global PATH mutation**: extract an internal overload `internal static JulieExtractRunner Locate(string toolsRoot, IReadOnlyList<string> pathDirs)` that takes the PATH directories explicitly; the public `Locate(string toolsRoot)` becomes a one-line delegate passing the split live PATH (`(Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)`). `FindOnPath` then iterates the passed `pathDirs` instead of reading the env var. The absent-binary test calls `Locate(tools, pathDirs: Array.Empty<string>())`. (Env mutation under a non-parallel `[Collection]` is the fallback codex listed, but xUnit still runs *different* collections in parallel, so the seam — not the env mutation — is the robust choice.)
- `WorkspaceContext.cs`: doc-comment-only — `:11` `<see cref="ToolsRoot"/> is where the pinned julie-server ships` → `julie-extract`; `:19` inline comment `where pinned julie-server ships` → `julie-extract`. No code change (ToolsRoot is a directory path; `WorkspaceContextTests` asserts `<appbase>/.tools` and stays green).
- `ScaleTestSupport.cs` `:33` `OperatingSystem.IsWindows() ? "julie-server.exe" : "julie-server"` → `julie-extract`; `:47` skip message `julie-server not found in .tools/. Run scripts/restore-julie-server.sh` → `julie-extract not found in .tools/. Run scripts/restore-julie-extract.sh`. This is the single launch signal the `ScaleTraitConventionTests` guard keys on (per CLAUDE.md) — keep it the one place the binary name lives for the Scale suite.
- `WorkspaceToolTests.cs:120` `OperatingSystem.IsWindows() ? "julie-server.exe" : "julie-server"` → `julie-extract` (fast-suite literal that constructs a `JulieExtractRunner` path; H owns the file body, this literal flips with the rename).

**Rename map (binary/script literals — G1 scope)**

| Location | OLD | NEW |
|---|---|---|
| `scripts/restore-julie-server.{sh,ps1}` (filename) | `restore-julie-server.{sh,ps1}` | `restore-julie-extract.{sh,ps1}` |
| `.sh:101` / `.ps1:54` (build cmd) | `--bin julie-server` | `-p julie-extract-cli --bin julie-extract` |
| `.sh:98` / `.ps1:51` (output) | `target/release/julie-server[.exe]` | `target/release/julie-extract[.exe]` |
| `.sh:97,107` / `.ps1:50,62` (install dest) | `.tools/julie-server[.exe]` | `.tools/julie-extract[.exe]` |
| `.sh:109-115` / `.ps1:63-66` (version assert) | `grep -F " ${VERSION}"` / `-notlike "* $ver*"` | name-only: starts-with `julie-extract`, no numeric compare |
| `JulieExtractRunner.cs:61` | `"julie-server.exe" : "julie-server"` | `"julie-extract.exe" : "julie-extract"` |
| `JulieExtractRunner.cs:47-49,71-73` (err strings) | `julie-server` / `restore-julie-server.sh` | `julie-extract` / `restore-julie-extract.sh` |
| `WorkspaceContext.cs:11,19` (doc comment) | `julie-server` | `julie-extract` |
| `ScaleTestSupport.cs:33,47` | `julie-server` / `restore-julie-server.sh` | `julie-extract` / `restore-julie-extract.sh` |
| `WorkspaceToolTests.cs:120` | `"julie-server.exe" : "julie-server"` | `"julie-extract.exe" : "julie-extract"` |

**Representative snippet (the load-bearing semantic change — `.sh` version assert):**
```bash
# OLD (.sh:109-115): couples restore to an exact product-version string; wrong for the from-source path
# (builds whatever the checkout holds) and a brittle proxy for the real schema/contract compatibility gate.
# VERSION_OUTPUT="$("${BINARY}" --version 2>/dev/null || true)"
# if ! grep -F " ${VERSION}" <<<"${VERSION_OUTPUT}" >/dev/null; then ... exit 1; fi
# NEW: compatibility is gated on schema/contract version at runtime (design D7); here assert only that
# the binary runs and self-identifies as julie-extract.
VERSION_OUTPUT="$("${BINARY}" --version 2>/dev/null || true)"
if [[ "${VERSION_OUTPUT}" != julie-extract* ]]; then
  echo "error: restored binary does not self-identify as julie-extract" >&2
  echo "  actual: ${VERSION_OUTPUT:-"(no --version output)"}" >&2
  exit 1
fi
```

**Steps (TDD)**
1. **Add a locking test for the binary-name rename (fast suite).** Create `tests/Miller.Tests/Indexing/JulieExtractBinaryNameTests.cs`. This guards that `Locate` resolves `julie-extract` (not `julie-server`) under a tools root, by staging a fake executable file and asserting the resolved `BinaryPath`. Real C#:
   ```csharp
   using Miller.Indexing;
   using Xunit;

   namespace Miller.Tests.Indexing;

   public sealed class JulieExtractBinaryNameTests
   {
       [Fact]
       public void Locate_ResolvesTheJulieExtractBinary_NotJulieServer()
       {
           string tools = Path.Combine(Path.GetTempPath(), "miller-locate-" + Guid.NewGuid().ToString("N"));
           Directory.CreateDirectory(tools);
           try
           {
               string name = OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract";
               string binary = Path.Combine(tools, name);
               File.WriteAllText(binary, "#!/bin/sh\nexit 0\n");
               if (!OperatingSystem.IsWindows())
                   File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

               var runner = JulieExtractRunner.Locate(tools);

               Assert.Equal(Path.GetFullPath(binary), runner.BinaryPath);
               Assert.EndsWith(name, runner.BinaryPath);
               Assert.DoesNotContain("julie-server", runner.BinaryPath);
           }
           finally { Directory.Delete(tools, recursive: true); }
       }

       [Fact]
       public void Locate_WithNoJulieExtractAnywhere_ThrowsPointingAtTheRenamedRestoreScript()
       {
           string tools = Path.Combine(Path.GetTempPath(), "miller-locate-empty-" + Guid.NewGuid().ToString("N"));
           Directory.CreateDirectory(tools);
           try
           {
               // Locate falls back to PATH after the tools dir. julie-extract is now installable on a
               // dev/CI PATH (README:34-37), so asserting "absent" against the AMBIENT PATH flakes. Use the
               // pathDirs seam (empty list) — deterministic AND parallel-safe (no process-global PATH mutation).
               var ex = Assert.Throws<FileNotFoundException>(
                   () => JulieExtractRunner.Locate(tools, pathDirs: Array.Empty<string>()));
               Assert.Contains("restore-julie-extract", ex.Message);
               Assert.DoesNotContain("restore-julie-server", ex.Message);
           }
           finally { Directory.Delete(tools, recursive: true); }
       }
   }
   ```
   Run `scripts/test.sh` — the first test FAILS (Locate still resolves `julie-server`), the second FAILS on the message assertion. (The `BinaryPath` getter exists at `JulieExtractRunner.cs:34`; ctor existence check at `:45`.)
2. **Implement** the `JulieExtractRunner.cs` `Locate`/ctor literal + error-string edits per the map. Run `scripts/test.sh` — green.
3. **Rename the scripts** (`git mv`) and apply the `.sh`/`.ps1` body edits per the map (build cmd, output path, install dest, version-assert semantics, self-references). There is no unit harness for shell; **manually validate the from-source path end-to-end** (this is the unblocker, so it must actually run):
   ```bash
   MILLER_JULIE_SOURCE=/Users/murphy/source/julie-extractors bash scripts/restore-julie-extract.sh --from-source
   .tools/julie-extract --version    # expect: julie-extract 2.0.0 (a stale pre-bump local build may show 0.1.0; rebuild)
   .tools/julie-extract scan --root /tmp/probe-repo --db /tmp/probe/symbols.db --strict-schema --json  # smoke: produces a v1 DB
   ```
   The script must exit 0, install `.tools/julie-extract`, and the smoke scan must write a SQLite file. (A pre-made tiny `/tmp/probe-repo` with one source file suffices.)
4. **Update the rename-coupled literals** in `WorkspaceContext.cs` (doc comments), `ScaleTestSupport.cs` (`:33,47`), `WorkspaceToolTests.cs` (`:120`). Run `scripts/test.sh` (fast) — green.
5. **Build:** `dotnet build Miller.slnx -c Release` — 0 warnings / 0 errors. (Note: the csproj Content/Link still says `julie-server` until G3; the build is conditioned on `Exists(.tools/julie-server)`, so with `.tools/julie-extract` now present and `.tools/julie-server` absent, the copy item is simply skipped — build still succeeds. G3 fixes the copy.)
6. **Commit** (the user must request it; if requested: branch first, include the renamed scripts + the new test).

**Acceptance**
- `scripts/restore-julie-extract.sh --from-source` (and `.ps1 -FromSource`) build `julie-extract` via `-p julie-extract-cli --bin julie-extract` and install `.tools/julie-extract[.exe]`, exiting 0 against the real julie-extractors checkout (a fresh build self-identifies as `julie-extract 2.0.0`).
- The version assert is name-based, never numeric (does not regress when the built/on-disk product version differs from `julie-pins.json` `version` — e.g. a dev building a newer checkout from source).
- `JulieExtractRunner.Locate` resolves `julie-extract[.exe]`; `JulieExtractBinaryNameTests` is green and the absent-binary error names `restore-julie-extract`.
- No `julie-server` literal remains in either restore script, `JulieExtractRunner.Locate`/ctor, `WorkspaceContext.cs`, `ScaleTestSupport.cs:33,47`, or `WorkspaceToolTests.cs:120`.
- Fast suite green (`scripts/test.sh`); `dotnet build Miller.slnx -c Release` 0 warnings.

### Task G2 — Repoint `julie-pins.json` + wire the download path to the v1 asset shape

**Files**
- `scripts/julie-pins.json`
- `scripts/restore-julie-extract.sh` (download branch `:122-209`)
- `scripts/restore-julie-extract.ps1` (download branch `:79-148`)

**What**
Repoint the pins (repo slug + version + binary name + per-triple asset names + checksums) and rework the download/extract logic for the **nested** v1 archive layout (`./dist/{triple}/julie-extract[.exe]` inside the tarball, vs old julie's flat `julie-server` at root). **All §9 unknowns are resolved**: the release is published as `v2.0.0` under `anortham/julie-extractors` with the four asset names + SHA-256 digests confirmed from the README "Current Release" table and `docs/release-evidence/2026-06-01-v2-0-0-release.md`. The pins below are concrete, not placeholders.

**Approach**
- `julie-pins.json` new shape (verified asset names + digests from the published v2.0.0 release):
  ```json
  {
    "version": "2.0.0",
    "binary": "julie-extract",
    "archiveInnerPathTemplate": "dist/{triple}/julie-extract{exe}",
    "urlTemplate": "https://github.com/anortham/julie-extractors/releases/download/v{VER}/{asset}",
    "assets": {
      "aarch64-apple-darwin":    { "name": "julie-extract-v{VER}-aarch64-apple-darwin.tar.gz",    "sha256": "bc9e21ef0b119bb9ab9bc2eb8a7260990244d8c9912047166beeae5ee51ea6bb" },
      "x86_64-apple-darwin":     { "name": "julie-extract-v{VER}-x86_64-apple-darwin.tar.gz",     "sha256": "2f06df2731639bcb0153c2b4e5f8d858ffda664f9f1f42b9e8558c10f9cd0988" },
      "x86_64-unknown-linux-gnu":{ "name": "julie-extract-v{VER}-x86_64-unknown-linux-gnu.tar.gz","sha256": "582febb8c7f6dda99df6e8aa219a9437640535c4751515925858fda87363e07b" },
      "x86_64-pc-windows-msvc":  { "name": "julie-extract-v{VER}-x86_64-pc-windows-msvc.zip",     "sha256": "ee2a3c52e1b6972ef67ea267458b75d7f7b289585f51bb70424c1bd44657112e" }
    }
  }
  ```
  - The asset `name` is **templated on `{VER}`** in the new scheme (old pins hardcoded `7.13.2` into the name). Both `urlTemplate` and `assets[].name` substitute `{VER}` so a version bump touches only the top-level `version`. The script's `read_pin`/`ConvertFrom-Json` reader must substitute `{VER}` in the asset name too (old code only substituted it in the URL template at `.sh:160`). Add an `archiveInnerPathTemplate` so the nested extract is data-driven, not hardcoded.
  - **These four `sha256` values pin the v2.0.0 archives and are the tamper-evidence record.** They MUST stay in lockstep with `MillerExtractContract.PinnedJulieExtractVersion` (`2.0.0`); the `JuliePinsJsonMatchesContractVersion` test enforces it. The publishing slug is `anortham/julie-extractors` per the working release URLs — note that Cargo.toml `[workspace.package].repository` reads `murphy/julie-extractors`, which is stale/internal and is NOT where assets are served; trust the release evidence, not the manifest field.
- Download/extract changes (`.sh` download branch):
  - Asset-name read must apply `{VER}`: after `ASSET="$(read_pin ".assets[\"${TRIPLE}\"].name")"` (`.sh:149`), add `ASSET="${ASSET/\{VER\}/${VERSION}}"`. The `BINARY` install dest is `.tools/julie-extract` (renamed in G1).
  - **Nested extract (the layout change):** old code `tar -xzf "${ARCHIVE}" -C "${TOOLS_DIR}" julie-server` (`.sh:197`) extracted a flat root member. New archives nest the binary at `dist/${TRIPLE}/julie-extract`. Replace with an extract-then-move that does not assume cwd layout:
    ```bash
    INNER="$(read_pin .archiveInnerPathTemplate)"
    INNER="${INNER/\{triple\}/${TRIPLE}}"; INNER="${INNER/\{exe\}/}"   # no exe suffix on unix
    tar -xzf "${ARCHIVE}" -C "${TOOLS_DIR}" "${INNER}"
    mv "${TOOLS_DIR}/${INNER}" "${BINARY}"
    rm -rf "${TOOLS_DIR}/dist"   # drop the now-empty nested staging dir
    ```
  - **sha256 verification:** the existing `verify_sha` flow (`.sh:175-194`) verifies the downloaded ARCHIVE against the pinned `sha256` — keep it. Optionally also fetch+verify the published `.sha256` sidecar for the inner binary (julie-extract publishes one per binary; old julie did not), but the archive-level pin remains the authoritative gate so the pin file stays self-contained. (Flag to Alan: do we trust the upstream sidecar or keep the committed-pin model? Default: keep committed pins, they are the tamper-evidence record.)
  - macOS quarantine clear (`.sh:201-203`) + exec bit (`.sh:200`) + archive cleanup (`.sh:206`) unchanged.
- `.ps1` download branch: same — `$asset = $config.assets.$triple.name -replace '\{VER\}', $config.version`; the Windows archive is a `.zip` so `Expand-Archive` already stages into a temp dir (`.ps1:128-142`) then `Get-ChildItem -Recurse -Filter 'julie-extract.exe'` finds the nested binary — change the filter from `julie-server.exe` to `julie-extract.exe` (`.ps1:133`) and the asset slug regex; the recursive find already tolerates the `dist\{triple}\` nesting, so no path-template logic needed on Windows.
- Update the unsupported-platform + missing-pin error messages (`.sh:142-146,154-157`; `.ps1:88-92,98-103`) to name `julie-extract` and `restore-julie-extract.{sh,ps1}`.

**Steps**
1. Apply the `julie-pins.json` reshape with the **concrete published values** (slug `anortham/julie-extractors`, version `2.0.0`, the four real `sha256` digests above, asset names + binary + inner-path-template).
2. Apply the `.sh`/`.ps1` download-branch edits (asset-name `{VER}` substitution, nested extract+move, error strings, `.ps1` filter rename).
3. **Validate against the real published artifact** (the release is live): run `bash scripts/restore-julie-extract.sh` on macOS arm64; it must download `julie-extract-v2.0.0-aarch64-apple-darwin.tar.gz`, verify it against the pinned `sha256`, extract `.tools/julie-extract` from the nested `dist/aarch64-apple-darwin/` path, and smoke `--version` (expect `julie-extract 2.0.0`). This is no longer blocked — do it as the closing validation of the migration.
4. `dotnet build Miller.slnx -c Release` — unaffected (pins/scripts are not compiled). Run `scripts/test.sh` — green (no test depends on the download path; Scale tests use from-source-installed `.tools/julie-extract`).

**Acceptance**
- `julie-pins.json` names `julie-extract` archives per the verified workflow scheme (`julie-extract-v{VER}-{triple}.{tar.gz|zip}`), templates `{VER}` into both URL and asset name, carries the nested-path template, and pins the four published v2.0.0 `sha256` digests + slug `anortham/julie-extractors`. No TBD fields remain.
- A real `bash scripts/restore-julie-extract.sh` download on macOS arm64 verifies sha256, extracts from the nested path, and installs a `--version`-able `.tools/julie-extract` (`julie-extract 2.0.0`).
- The download branch extracts the binary from the nested `dist/{triple}/julie-extract[.exe]` archive layout (not a flat root member) and installs `.tools/julie-extract[.exe]`.
- `.ps1` finds `julie-extract.exe` (not `julie-server.exe`) inside the `.zip`.
- All download-branch error/usage strings name `julie-extract` / `restore-julie-extract`.

### Task G3 — csproj Content/Link/Exec + finalize WorkspaceContext

**Files**
- `src/Miller.Server/Miller.Server.csproj` (`:37-58`)
- `src/Miller.Server/Hosting/WorkspaceContext.cs` (already done in G1; verify clean)

**What**
Point the build's tool-copy machinery at `.tools/julie-extract`. Without this, `dotnet publish`/`dotnet build` would copy the (now absent) `julie-server` into the output and the runtime `Locate` (which looks under `AppContext.BaseDirectory/.tools`) would not find `julie-extract` next to the published binary.

**Approach (mechanical rename within the csproj)**

| csproj line | OLD | NEW |
|---|---|---|
| `:44` | `Condition="Exists('$(MSBuildProjectDirectory)/../../.tools/julie-server')"` | `.../.tools/julie-extract` |
| `:45` | `<Content Include="$(MSBuildProjectDirectory)/../../.tools/julie-server">` | `.../.tools/julie-extract` |
| `:46` | `<Link>.tools/julie-server</Link>` | `<Link>.tools/julie-extract</Link>` |
| `:55-56` | `EnsureJulieServerExecutable` / `Exists('$(OutDir).tools/julie-server')` | `EnsureJulieExtractExecutable` / `.tools/julie-extract` |
| `:57` | `chmod +x "$(OutDir).tools/julie-server"` | `.tools/julie-extract` |
| `:37-43,52-54` (comments) | `julie-server` / `restore-julie-server.sh` | `julie-extract` / `restore-julie-extract.sh` |

Representative snippet (the Content item — the load-bearing copy):
```xml
<ItemGroup Condition="Exists('$(MSBuildProjectDirectory)/../../.tools/julie-extract')">
  <Content Include="$(MSBuildProjectDirectory)/../../.tools/julie-extract">
    <Link>.tools/julie-extract</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Visible>false</Visible>
  </Content>
</ItemGroup>
```

**Steps (TDD via build observation — there is no unit test for MSBuild copy; the assertion is the on-disk artifact)**
1. With `.tools/julie-extract` present (from G1's from-source restore), confirm the **pre-change** state: `dotnet build Miller.slnx -c Release` then check `find src/Miller.Server/bin/Release -name 'julie-*' -path '*/.tools/*'` shows NO `julie-extract` (the csproj still keys off `julie-server`, which is absent). This is the failing observation.
2. Apply the csproj rename per the table.
3. `dotnet build Miller.slnx -c Release` — 0 warnings / 0 errors. Re-run the `find`; it now shows `.../.tools/julie-extract` in the output dir, and on unix `test -x` on it passes (the `EnsureJulieExtractExecutable` target reasserted +x). This is the passing observation.
4. Add/extend a Scale test that proves the runtime resolves the copied binary from `AppContext.BaseDirectory/.tools` (this is the production code path that the Content/Link copy exists to satisfy). If `LiveWorkspaceTests` already exercises `Locate` against the output-dir tools root, no new test is needed — confirm it resolves `julie-extract` there. Run `scripts/test.sh scale` (skips cleanly if `.tools/julie-extract` is absent; with G1's restore it runs).
5. Run `scripts/test.sh` (fast) — green. Build — 0 warnings.

**Acceptance**
- `dotnet build`/`dotnet publish` copies `.tools/julie-extract[.exe]` into `<out>/.tools/` with the exec bit set on unix.
- The csproj contains no `julie-server` literal (Content, Link, Condition, Exec, target name, comments all `julie-extract`).
- A no-restore machine (neither `.tools/julie-extract` nor `.tools/julie-server`) still builds (the `Exists` condition skips the copy), and the runtime then fails loudly via `Locate` with the renamed restore-script message.
- `scripts/test.sh` fast green; `scripts/test.sh scale` resolves the copied `julie-extract` (or skips if not restored).

### Task G4 — CLAUDE.md rewrite + regenerate AGENTS.md

**Files**
- `CLAUDE.md`
- `AGENTS.md` (generated — never hand-edited)
- `scripts/sync-agents.sh` (run only)

**What**
Update every `julie-server` / `restore-julie-server.sh` / `MILLER_JULIE_SOURCE=/path/to/julie` reference in `CLAUDE.md` to the `julie-extract` world, then regenerate `AGENTS.md` byte-for-byte via `scripts/sync-agents.sh`. The pre-commit hook (`.githooks/pre-commit:18`) fails the commit if `CLAUDE.md` and `AGENTS.md` diverge, so the regeneration is mandatory, not optional.

**Approach (CLAUDE.md edits — verified locations from grep)**

| CLAUDE.md line | OLD | NEW |
|---|---|---|
| `:3-4` | `consumer of \`julie-server extract\` output ... delegated to the pinned \`julie-server\` binary` | `consumer of \`julie-extract\` v1 output ... delegated to the pinned \`julie-extract\` binary` |
| `:20` | `tests spawn the real \`julie-server\`` | `tests spawn the real \`julie-extract\`` |
| `:22` | `if \`.tools/julie-server\` is missing` | `if \`.tools/julie-extract\` is missing` |
| `:34` | `A test that spawns \`julie-server\`` | `A test that spawns \`julie-extract\`` |
| `:55` | `build COPIES \`.tools/julie-server\`` | `\`.tools/julie-extract\`` |
| `:59` | `MILLER_JULIE_SOURCE=/path/to/julie scripts/restore-julie-server.sh --from-source` | `MILLER_JULIE_SOURCE=/path/to/julie-extractors scripts/restore-julie-extract.sh --from-source` |

Additional content updates beyond literal swaps (CLAUDE.md is documentation, so it must describe the NEW reality, not just rename):
- The §"Build" bullet at `:55-59` references the pre-`v7.13.2`-release from-source instruction. Rewrite it to describe the v1 reality: the build copies `.tools/julie-extract`; production locates it at `AppContext.BaseDirectory/.tools` (`WorkspaceContext.ToolsRoot`); until the julie-extract release assets publish (§9), use `MILLER_JULIE_SOURCE=/path/to/julie-extractors scripts/restore-julie-extract.sh --from-source` (which runs `cargo build --release -p julie-extract-cli --bin julie-extract`).
- The `MILLER_JULIE_SOURCE` example path changes from `/path/to/julie` to `/path/to/julie-extractors` (it is now a julie-extractors checkout, not a julie checkout).

**Steps**
1. Apply the CLAUDE.md edits per the table + the Build-bullet rewrite.
2. Run `scripts/sync-agents.sh` — regenerates `AGENTS.md` as a byte-for-byte copy. (No edit to sync-agents.sh itself; it is `cp` of CLAUDE.md → AGENTS.md.)
3. **Verify the sync guard passes:** `diff -q CLAUDE.md AGENTS.md` must be silent (the pre-commit hook runs exactly this). If it diverges, AGENTS.md was hand-edited — re-run sync-agents.sh.
4. `grep -n 'julie-server\|restore-julie-server\|/path/to/julie ' CLAUDE.md AGENTS.md` must return nothing (no stale literal, and the `/path/to/julie ` example is now `/path/to/julie-extractors`).
5. Run `scripts/test.sh` (fast) — green (docs do not affect tests, but confirm nothing in the suite asserts on CLAUDE.md content).
6. **Commit** (user-requested only): include both `CLAUDE.md` and `AGENTS.md` in the same commit so the pre-commit hook passes; branch first if on `main`.

**Acceptance**
- `CLAUDE.md` describes the `julie-extract` v1 reality: binary `julie-extract`, restore script `restore-julie-extract.sh`, from-source against a julie-extractors checkout, no `julie-server` literal remaining.
- `AGENTS.md` is byte-for-byte identical to `CLAUDE.md` (`diff -q` silent — the pre-commit hook would otherwise block the commit).
- `grep julie-server CLAUDE.md AGENTS.md` returns nothing.
- Fast suite green.

### Cross-subsystem coordination (G)

- **A owns `MillerExtractContract`** (`src/Miller.Indexing/MillerExtractContract.cs:16` `PinnedJulieServerVersion` → `PinnedJulieExtractVersion`). G1's `JulieExtractRunner.Locate`/ctor error strings interpolate that constant (`:49,73`). Coordinate the symbol name; G1 only requires the binary-name literal, so it can land before A's contract rename if the version-interpolation site is left to A.
- **A owns the rest of `JulieExtractRunner.cs`** (argv builders, `Interpret`, the bulk of error strings). G touches ONLY the binary-name literal in `Locate:61` and the restore-script-name strings in ctor/`Locate`. Split so neither overwrites the other.
- **H owns the Scale fixtures + `WorkspaceToolTests`/`Live*Tests` bodies.** G1 flips the binary-name literal in `ScaleTestSupport.cs:33,47` and `WorkspaceToolTests.cs:120` because those are packaging renames; the `Live*Tests` doc comments are cosmetic and can ride with H.
- **README.md, docs/miller-mvp-plan.md, .gitignore:21, scripts/test.sh header** all name `julie-server`/`restore-julie-server.sh` and go stale on rename. They are outside G's strict file list; recommend G fix `.gitignore` + `scripts/test.sh` comments (packaging-adjacent) and flag README/mvp-plan to the docs owner so the rename is complete repo-wide.


---

## Subsystem H: Test fixtures (co-requisite)

These two files are a SECOND implementation of the julie schema that the **fast suite reads on every change**. They MUST migrate to the v1 `julie-extract` artifact schema **in the same commit** as the subsystem-B readers, or every fast test fails to open its DB. The authoritative v1 schema is `/Users/murphy/source/julie-extractors/crates/julie-extract-artifact/src/schema.rs` (`SQLITE_SCHEMA_VERSION = 1`, `EXTRACT_CONTRACT_VERSION = 1`); the metadata keys are in `crates/julie-extract-artifact/src/metadata.rs:7-19`; `content_hash` is `blake3:<hex>`-prefixed (`crates/julie-extract-cli/src/extraction.rs:644`); `change_kind` ∈ `{inserted,updated,deleted,unsupported}` and `files.status` ∈ `{indexed,unsupported,failed_preserved}` (`crates/julie-extract-artifact/src/model.rs:36-68`).

**Design conformance note (read before starting).** §10H of the design doc names exactly these two files and says they "MUST migrate to v1 schema **in lockstep** with the readers (subsystem B)" and "also defines the canonical v1 synthetic schema the fast suite asserts against." The plan below conforms. One **plan-mismatch flag**: §10H lists only `JulieDbFixture.cs` + `LargeDbWriter.cs`, but two further test files build the old schema inline (`tests/Miller.Tests/Indexing/SqliteBridgeReaderTests.cs`, `tests/Miller.Tests/Indexing/RepositoryIndexLoaderBridgeTests.cs`). They are NOT in this subsystem's ownership (they belong with subsystem B's `SqliteBridgeReader`/`RepositoryIndexLoader` work) — surfaced here as a cross-dependency so they are not missed.

**Shared API-stability rule for all H tasks.** Keep the public helper surface of `JulieDbFixture` (the `Create(...)` signature including its surviving optional `workspaceId:` param, `CreateDefault`/`CreateForInspect`/`CreateForEdit`, the `SymbolRow`/`IdentifierRow`/`RelationshipRow`/`RevisionRow`/`RevisionFileChangeRow` records, the `PinnedSchema`/`PinnedContract`/`SchemaText` statics, and the `*Content`/`*Id` constants) **byte-for-byte where possible**, so the ~25 consumer test files compile with minimal call-site edits. **One end-state exception (reconciliation #15):** the revision-row records drop `WorkspaceId` (and `RevisionFileChangeRow` drops `OldHash`/`NewHash` and renames `FilePath`→`Path`), because v1 `extraction_revisions` has no workspace concept. **This is NOT staged across phases** (the earlier carried-but-not-written two-step is void — workflow#1): **H2 moved to Phase 4** and the record-table flip + the field drop + the reader (D3/D4) + every call site land in ONE Phase-4 commit. Through Phase 3 the revision tables and records stay fully OLD (`canonical_revisions` + `RevisionRow(rev, ws, kind)`), so the OLD freshness reader and its fast-suite tests stay green; H1's `files.last_revision_id` is an FK-free plain column (no `extraction_revisions` to FK to yet). The `Rev`/`Fc` aliases in `FreshnessReaderTests.cs:4-5` DO exist — reconciliation #15's inventory enumerates them; do not assume full record names everywhere. Any other field that genuinely cannot survive is called out per task. **H subsystem landing model:** H does NOT land as one commit; it splits BY TABLE GROUP across phases, each group atomic with its readers — H1 (non-revision spine: files[+transitional `content`]/symbols/identifiers/relationships/artifact_metadata) + H4/H5/H6 + B + C2 in **Phase 3**; H2 (revisions) + D3/D4 + E-freshness in **Phase 4**; H3 (`content` removal + disk materialization) + C3/C4 in **Phase 5**; H7 (standalone `LargeDbWriter`) in **Phase 3**. The global sequencing (bottom of this doc) is authoritative over any "H lands as one commit" phrasing in the per-task bodies below.

---

### Task H1 — Rewrite JulieDbFixture DDL + core INSERTs to the v1 artifact schema (keep helper API stable)

**Files**
- `tests/Miller.Tests/Indexing/JulieDbFixture.cs` (CREATE TABLE constants at lines 684-853; INSERT bodies at lines 200-389)

**What**
Replace the old-schema DDL constants and the `files`/`symbols`/`identifiers`/`relationships`/`artifact_metadata` INSERTs with the v1 artifact shapes, adopting by-name-safe column lists that match subsystem B's post-migration SELECTs. This is the spine task; H2-H6 layer the remaining tables on top. It is **non-mechanical** (column renames + structural changes + new NOT NULL/FK obligations + a metadata-table replacement), so full TDD choreography follows.

**Exact rename / structural map (old → v1)**

| Const / INSERT (old line) | Old column set | v1 column set | Source of truth |
|---|---|---|---|
| `FilesDdl` (684-697) | `path PK, language, hash, size, last_modified, last_indexed, parse_cache, symbol_count, content, line_count` | `file_id PK, path UNIQUE, language, content_hash, content_bytes, line_count, indexed_at, last_revision_id, status, metadata_json` — **two Phase-3 deviations:** (a) `last_revision_id` is a **plain column, NO FK** (`extraction_revisions` is Phase-4 / H2, recon #15); (b) **keep a TRANSITIONAL `content` column** (OLD `ReadBody` reads it until C3/Phase 5; H3 drops it) | schema.rs:52-64 |
| `SymbolsDdl` (699-723) | `id PK, …, file_path (FK), …, parent_id (self-FK), metadata, file_hash, …, reference_score` | `symbol_id PK, file_id (FK), path, language, name, kind, signature, doc_comment, visibility, parent_symbol_id (self-FK), start_line, start_column, end_line, end_column, start_byte, end_byte, body_* , body_hash, semantic_group, confidence, content_type, is_test, test_container, test_lifecycle, metadata_json` | schema.rs:66-99 |
| `IdentifiersDdl` (725-740) | `id PK, …, file_path (FK), …, code_context, last_indexed` | `identifier_id PK, file_id (FK), path, language, name, kind, containing_symbol_id, target_symbol_id, start_*, end_*, start_byte, end_byte, confidence (NOT NULL), code_context, metadata_json` | schema.rs:112-133 |
| `RelationshipsDdl` (742-754) | `id PK, from_symbol_id, to_symbol_id, kind, file_path, line_number, confidence, metadata, created_at` | `relationship_id PK, from_symbol_id, to_symbol_id, file_id (FK), path, kind, start_*/end_*/start_byte/end_byte (nullable), confidence (NOT NULL), metadata_json` | schema.rs:135-153 |
| `MetadataDdl` (817-823) + `SchemaVersionDdl` (809-815) | two tables: `external_extract_metadata(key,value,updated_at)` + `schema_version(version,applied_at,description)` | ONE table: `artifact_metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL)` — **drop `schema_version` table and `updated_at`**; versions become metadata KEYS | schema.rs:13-16; metadata.rs:7-19 |

Column→GetX ordinal contract: subsystem B switches readers to **by-name** reads (`GetOrdinal`), so the fixture's INSERT column order no longer has to match a positional SELECT — but the **names** must match exactly. Verify against the post-migration reader SELECTs: `SqliteSymbolReader` selects `symbol_id, name, signature, kind, language, path, start_line, end_line, parent_symbol_id, is_test, (test_container, test_lifecycle)` (was `id, …, file_path, …, parent_id, metadata`); `ExtractReader.ReadReferences/ReadCallees/ReadIdentifierSites` select identifier `name, kind, path, start_line, containing_symbol_id` and `path, start_byte, end_byte, start_line`; `WorkspaceIndexFactsReader` selects `symbols.path` (was `file_path`); `SymbolGraphReader` selects `from_symbol_id, to_symbol_id, kind` (unchanged).

**Metadata keys to write** (replacing the three `external_extract_metadata` upserts at 361-389). Write **all 11 `REQUIRED_METADATA_KEYS`** the real producer emits (`metadata.rs:7-19`), so the synthetic fixture is a faithful v1 artifact (reconciliation #3/#23): `schema_version`, `sqlite_schema_version`, `extract_contract_version` (all `MillerExtractContract` values, written as TEXT), `hash_algorithm` (`"blake3"`), `binary_version` (`"2.0.0"`), `parser_inventory_fingerprint` and `capability_snapshot_fingerprint` (deterministic **`sha256:`-prefixed** 64-hex synthetic values — exercises the hash-domain distinction in #9; Miller stores but never compares them), `created_at` and `updated_at` (deterministic ISO-8601 strings), plus identity keys `artifact_id` and `root_path` (driven by the existing `workspaceId:` param — see H2/cross-deps). Subsystem B's `JulieSchemaGate` only reads `sqlite_schema_version`/`schema_version`/`extract_contract_version`/`hash_algorithm`, but the full key set keeps the fixture honest and lets a future fingerprint consumer test against real-shaped values. Extend `Fixture_ArtifactMetadata_*` to assert the fingerprint keys carry the `sha256:` prefix (never `blake3:`).

**Approach**
Open the v1 schema in `schema.rs` side-by-side. Rewrite each DDL const and its matching INSERT together. Add a private `FileId(path)` helper (e.g. `"file:" + path`) and thread `file_id` into the `files`, `symbols`, `identifiers`, `relationships`, and `literals` INSERTs. **Do NOT seed an `extraction_revisions` row and do NOT FK `files.last_revision_id`** — that table is Phase-4 (H2, recon #15 single-step). In Phase 3 write `last_revision_id` as a plain column (`0` or NULL) with no FK constraint, so the fixture satisfies with no revision table present. **Keep a TRANSITIONAL `content TEXT` column** populated from the same source text (the `*Content` constants) ALONGSIDE `content_hash`/`content_bytes`: the OLD `ReadBody`/`ReadFileContent` still read `files.content` until C3 moves body reads to disk (Phase 5), so dropping `content` now would red the Phase-3 `ReadBody` tests; H3 (Phase 5) drops the column when C3 lands. Keep the symbol position columns **nullable** in the synthetic DDL (deviation from julie's strict NOT NULL — see open_notes) so the existing NULL-discipline tests keep their coverage; map the `SymbolRow.StartByte/EndByte/StartLine/EndLine` init-props straight through as today.

**Steps (TDD)**
1. **Add a failing canonical-schema lock test.** New file `tests/Miller.Tests/Indexing/JulieDbFixtureV1SchemaTests.cs`:
   ```csharp
   using Microsoft.Data.Sqlite;
   using Xunit;

   namespace Miller.Tests.Indexing;

   /// <summary>
   /// Locks JulieDbFixture to the v1 julie-extract artifact schema (schema.rs v1). This is the canonical
   /// synthetic-schema guard the design (§10H) calls for: if the fixture drifts off v1, the readers it feeds
   /// would silently test against the wrong contract. Asserts the v1 table set exists and the old-schema
   /// tables/columns are GONE.
   /// </summary>
   public sealed class JulieDbFixtureV1SchemaTests
   {
       private static SqliteConnection Open(string dbPath)
       {
           var c = new SqliteConnection(new SqliteConnectionStringBuilder
           { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
           c.Open();
           return c;
       }

       private static bool TableExists(SqliteConnection c, string name)
       {
           using var cmd = c.CreateCommand();
           cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n;";
           cmd.Parameters.AddWithValue("$n", name);
           return cmd.ExecuteScalar() is not null;
       }

       private static bool ColumnExists(SqliteConnection c, string table, string column)
       {
           using var cmd = c.CreateCommand();
           cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name=$c;";
           cmd.Parameters.AddWithValue("$c", column);
           return cmd.ExecuteScalar() is not null;
       }

       [Fact]
       public void Fixture_EmitsV1ArtifactTables_AndDropsOldSchemaTables()
       {
           using var fx = JulieDbFixture.CreateDefault();
           using var c = Open(fx.DbPath);

           // Phase 3 = the non-revision v1 spine. The revision tables (extraction_revisions/revision_file_changes)
           // and the canonical_revisions DROP are H2's lock assertions (Phase 4): in Phase 3 canonical_revisions is
           // still present and untouched, so do NOT assert on revision tables here.
           foreach (var t in new[] { "artifact_metadata", "files", "symbols", "identifiers",
               "relationships", "type_argument_usages", "type_arguments", "literals", "symbol_annotations",
               "parse_diagnostics", "parser_inventory", "language_capabilities" })
               Assert.True(TableExists(c, t), $"v1 table '{t}' must exist");

           // Old-schema artifacts H1 removes are gone.
           Assert.False(TableExists(c, "schema_version"), "schema_version table is dropped in v1");
           Assert.False(TableExists(c, "external_extract_metadata"), "renamed to artifact_metadata in v1");
       }

       [Fact]
       public void Fixture_SymbolsAndFiles_UseV1ColumnNames()
       {
           using var fx = JulieDbFixture.CreateDefault();
           using var c = Open(fx.DbPath);

           Assert.True(ColumnExists(c, "symbols", "symbol_id"));
           Assert.True(ColumnExists(c, "symbols", "path"));
           Assert.True(ColumnExists(c, "symbols", "parent_symbol_id"));
           Assert.True(ColumnExists(c, "symbols", "metadata_json"));
           Assert.True(ColumnExists(c, "symbols", "is_test"));
           Assert.False(ColumnExists(c, "symbols", "file_path"), "old column renamed to path");
           Assert.False(ColumnExists(c, "symbols", "parent_id"), "old column renamed to parent_symbol_id");

           Assert.True(ColumnExists(c, "files", "content_hash"));
           Assert.True(ColumnExists(c, "files", "content_bytes"));
           Assert.False(ColumnExists(c, "files", "hash"), "renamed to content_hash");
           // The transitional `content` column is still present in Phase 3 (OLD ReadBody reads it until C3/Phase 5);
           // the "files is content-free" assertion lives in H3's Phase-5 lock test, not here.

           Assert.True(ColumnExists(c, "artifact_metadata", "key"));
           Assert.True(ColumnExists(c, "artifact_metadata", "value"));
       }

       [Fact]
       public void Fixture_ArtifactMetadata_CarriesVersionKeys()
       {
           using var fx = JulieDbFixture.CreateDefault();
           using var c = Open(fx.DbPath);
           using var cmd = c.CreateCommand();
           cmd.CommandText = "SELECT value FROM artifact_metadata WHERE key='sqlite_schema_version';";
           Assert.Equal(MillerExtractContract.ExpectedSchemaVersion.ToString(
               System.Globalization.CultureInfo.InvariantCulture), cmd.ExecuteScalar()?.ToString());
       }
   }
   ```
   Run `scripts/test.sh` — this MUST fail (old fixture still emits `schema_version`/`external_extract_metadata`/`file_path`).
2. **Implement** the DDL + INSERT rewrite per the map above (and the metadata keys). This is the bulk of the work; do `files`/`symbols`/`identifiers`/`relationships`/`artifact_metadata` here, leaving the freshness/bridge/typed-test/new-table pieces to H2-H6 (those tasks add their own DDL + INSERT changes and tests).
3. Run `scripts/test.sh`. Expect the new lock test green for the tables H1 touches; H4/H5/H6's tests land in the SAME **Phase-3** read-layer commit (with B/C2). H2 (revisions) is **Phase 4** and H3 (`content` removal) is **Phase 5** — their lock tests do NOT run here (H does not land as one commit; it splits by table group across Phases 3/4/5 per the global sequencing). The Phase-3 gating run is at the end of the read-layer batch.
4. Run `dotnet build Miller.slnx -c Release` — must be 0 warnings.

**Acceptance**
- `JulieDbFixtureV1SchemaTests` passes (v1 tables present, old tables/columns absent, version keys in `artifact_metadata`).
- `SqliteSymbolReaderTests` (subsystem B/F, same commit) passes against the rewritten fixture: deterministic DocId ordering over `path, start_line, symbol_id`; opaque id round-trips; NULL signature→null; NULL start_line→0; NULL parent→null.
- `JulieSchemaGateTests` passes against `artifact_metadata`-keyed versions.
- Build 0 warnings; fast suite still <30s wall (`scripts/test.sh`).

---

### Task H2 — Migrate the freshness fixture helpers to `extraction_revisions` / `revision_file_changes` v1 shape

**Files**
- `tests/Miller.Tests/Indexing/JulieDbFixture.cs`: `RevisionRow` record (142-145), `RevisionFileChangeRow` record (151-159), `CanonicalRevisionsDdl` (828-841), `RevisionFileChangesDdl` (843-853), the revisions INSERT (314-330) and fileChanges INSERT (333-350)

**What**
Replace the `canonical_revisions`/`revision_file_changes` tables (which carry `workspace_id` + a CHECK constraint) with v1's `extraction_revisions`/`revision_file_changes` (no `workspace_id`, no CHECK on `change_kind`, `revision_id` PK, new vocabulary). **H2 is in Phase 4** (MOVED from Phase 3 — reconciliation #15 single-step / workflow#1). It does the DDL+INSERT migration AND drops the record fields (`WorkspaceId`; `RevisionFileChangeRow` also drops `OldHash`/`NewHash` and renames `FilePath`→`Path`, deriving the NOT-NULL `file_id` via the shared `FileId(path)` helper) **in the SAME commit** as D3/D4's reader rewrite and every consumer in reconciliation #15's inventory (`FreshnessReaderTests` via the `Rev`/`Fc` aliases, `IndexBootstrapServiceTests`, `WorkspaceToolTests`, `FreshnessServicePollNowTests`, `WorkspaceIndexProviderTests`). The compiler enforces the atomicity: once the fields are gone, every call site must update in this commit. Through Phase 3 these tables + records stay fully OLD and untouched (so the OLD reader and its fast-suite tests stay green); they cannot flip earlier because the monolithic fixture's revision tables would then outrun their Phase-4 reader (`no such table: canonical_revisions`). Non-mechanical (the freshness contract changes shape and the subsystem-**D** reader loses its workspace filter), so full TDD.

**v1 facts (verified)**
- `extraction_revisions(revision_id INTEGER PRIMARY KEY, parent_revision_id, operation NOT NULL, mode, started_at NOT NULL, completed_at NOT NULL, binary_version NOT NULL, extract_contract_version NOT NULL, sqlite_schema_version NOT NULL, input_root, counts_json NOT NULL)` — schema.rs:28-41. NOT autoincrement; explicit `revision_id` inserts are valid.
- `revision_file_changes(revision_id NOT NULL, file_id NOT NULL, path NOT NULL, change_kind NOT NULL, PK(revision_id, file_id), FK revision_id)` — schema.rs:43-50. **No `workspace_id`. No CHECK** on `change_kind`.
- `change_kind` vocabulary `{inserted, updated, deleted, unsupported}` — model.rs:54-67.

**Approach**
- `RevisionRow` (final v1 shape): `(long Revision, string Kind = "full")` + `CreatedAt` — the `WorkspaceId` field is DROPPED here (Phase 4). `Kind` default is `"full"` (a scan-produced revision is a full extraction; supersedes the old `"incremental"`). The INSERT maps `Revision`→`revision_id`, `Kind`→`mode`, `CreatedAt`→`completed_at`/`started_at` as TEXT; supply the NOT NULL `operation='scan'`, `started_at=''`, `completed_at=''`, `binary_version='2.0.0'` (match H1's `artifact_metadata.binary_version`), `extract_contract_version=MillerExtractContract.ExpectedExtractContractVersion`, `sqlite_schema_version=MillerExtractContract.ExpectedSchemaVersion`, `counts_json='{}'`. No `workspace_id` column exists. (Workspace identity still flows through H1's SEPARATE surviving `Create(workspaceId:)` param → `artifact_metadata.artifact_id`/`root_path`; only the revision-row FIELD is dropped.)
- `RevisionFileChangeRow` (final v1 shape): `(long Revision, string Path, string ChangeKind)` — `WorkspaceId`/`OldHash`/`NewHash` are DROPPED and `FilePath`→`Path` here (Phase 4). INSERT maps `Revision`→`revision_id`, `Path`→`path`, `ChangeKind`→`change_kind`, plus the synthetic NOT NULL `file_id = FileId(Path)` (the shared `"file:" + path` helper from H1; `file_id` has no FK, so a path-derived value is valid and PK-unique within a revision). No `workspace_id`/`old_hash`/`new_hash` columns exist in v1 (verified: the only `.OldHash`/`.NewHash` reads were the fixture's own INSERT params at JulieDbFixture.cs:346-347, removed with the fields).
- **No seed-revision row is needed.** H1's `files.last_revision_id` is an FK-free plain column (reconciliation #15), so the fixture no longer requires an `extraction_revisions` row to exist for `files` to insert. `Create` writes only the `revisions`/`fileChanges` the caller supplies (empty by default).
- **Update the change_kind values the freshness tests use.** This is the coordination seam with subsystem **D**'s `ParseChangeKind` (Task D4, `FreshnessReader.cs`). The fixture itself does not parse, but H2 updates the fixture's DDL/INSERT to accept the new vocab; the `FreshnessReaderTests` literals (`"added"/"modified"/"deleted"` at **D4**-owned lines 75-78, 95, 108-110, 116-118) move to `"inserted"/"updated"/"deleted"/"unsupported"` in subsystem **D**'s TDD. Flag in cross-deps; the fixture must not CHECK-reject the new vocab (drop the CHECK).

**Steps (TDD)**
1. **Add a failing freshness-shape test** to `JulieDbFixtureV1SchemaTests.cs`:
   ```csharp
   [Fact]
   public void Fixture_RevisionTables_AreV1_CanonicalRevisionsGone()  // moved from H1's lock test (now Phase 4)
   {
       using var fx = JulieDbFixture.CreateDefault();
       using var c = Open(fx.DbPath);
       Assert.True(TableExists(c, "extraction_revisions"), "v1 revision table present");
       Assert.True(TableExists(c, "revision_file_changes"), "v1 per-file change table present");
       Assert.False(TableExists(c, "canonical_revisions"), "old revision table renamed to extraction_revisions in v1");
   }

   [Fact]
   public void Fixture_ExtractionRevisions_AreWorkspaceIdFree_AndKeyedByRevisionId()
   {
       using var fx = JulieDbFixture.Create(
           JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
           Array.Empty<JulieDbFixture.SymbolRow>(),
           revisions: new[] { new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2) });
       using var c = Open(fx.DbPath);

       Assert.False(ColumnExists(c, "extraction_revisions", "workspace_id"),
           "v1 extraction_revisions has no workspace_id (one DB = one root)");
       Assert.True(ColumnExists(c, "extraction_revisions", "revision_id"));

       using var max = c.CreateCommand();
       max.CommandText = "SELECT MAX(revision_id) FROM extraction_revisions;";
       Assert.Equal(2L, System.Convert.ToInt64(max.ExecuteScalar()));
   }

   [Fact]
   public void Fixture_RevisionFileChanges_UseV1VocabularyWithoutCheckConstraint()
   {
       // v1 has NO CHECK on change_kind; 'unsupported' (a v1-only value) must insert without error.
       using var fx = JulieDbFixture.Create(
           JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
           Array.Empty<JulieDbFixture.SymbolRow>(),
           revisions: new[] { new JulieDbFixture.RevisionRow(1) },
           fileChanges: new[]
           {
               new JulieDbFixture.RevisionFileChangeRow(1, "a.cs", "inserted"),
               new JulieDbFixture.RevisionFileChangeRow(1, "b.cs", "unsupported"),
           });
       using var c = Open(fx.DbPath);
       Assert.False(ColumnExists(c, "revision_file_changes", "workspace_id"));
       using var cmd = c.CreateCommand();
       cmd.CommandText = "SELECT COUNT(*) FROM revision_file_changes;";
       Assert.Equal(2L, System.Convert.ToInt64(cmd.ExecuteScalar()));
   }
   ```
   Run `scripts/test.sh` — fails (old `canonical_revisions` + workspace_id column + CHECK).
2. **Implement** the two DDL constants and the two records' INSERT mappings per Approach (no seed-revision logic — H1's `last_revision_id` is FK-free, recon #15). Drop the `WorkspaceId`/`OldHash`/`NewHash` record fields and rename `FilePath`→`Path` in the SAME commit (the compiler then drives the call-site updates in reconciliation #15's inventory).
3. Run `scripts/test.sh`. The H2 lock tests go green. `FreshnessReaderTests` (subsystem **D**, D3/D4) must land in the SAME Phase-4 commit with its reader + vocab updates — verify together.
4. `dotnet build Miller.slnx -c Release` — 0 warnings.

**Acceptance**
- `extraction_revisions`/`revision_file_changes` exist (and `canonical_revisions` is gone), have NO `workspace_id`, and accept the v1 `{inserted,updated,deleted,unsupported}` vocab with no CHECK rejection.
- `RevisionRow`/`RevisionFileChangeRow` records are at their final v1 shape (no `WorkspaceId`/`OldHash`/`NewHash`; `Path` not `FilePath`); EVERY call site in reconciliation #15's inventory is updated in THIS Phase-4 commit (the compiler enforces it — `rg -n "RevisionRow\(|RevisionFileChangeRow\(|new Rev\(|new Fc\(" src tests` shows zero workspace-id args).
- `FreshnessReaderTests` (subsystem **D**, same commit) passes against `MAX(revision_id)` (no workspace filter) and the new vocab.
- Build 0 warnings.

---

### Task H3 — Migrate file-content + `content_hash` handling for the D2 disk-read seam

**Files**
- `tests/Miller.Tests/Indexing/JulieDbFixture.cs`: the `files` INSERT (224-234), the `fileContent:` parameter (175), the `*Content` constants (406-411, 493-513)

**What**
v1 `files` has **no `content` column** (D2: Miller reads body text from disk) and stores `content_hash` as `blake3:<hex>` (prefixed) plus `content_bytes` (a count). H1 already writes `content_hash`/`content_bytes` (Phase 3) AND kept a TRANSITIONAL `content` column so OLD `ReadBody` works through Phase 4. H3 (Phase 5, with C3) must (a) **DROP the transitional `content` column** H1 kept (DDL column + INSERT value), now that C3 re-sources body text from disk; (b) leave H1's `content_bytes` = UTF-8 byte length and `content_hash` = `"blake3:" + ContentHasher.Blake3Hex(bytes)` in place; and (c) **materialize each file's bytes to disk under a fixture-owned `WorkspaceRoot`** and expose that root, so the inspect/edit/freshness tests (subsystem D2/C3) can read and mutate the real on-disk bytes for the disk-slice path (reconciliation #3). The on-disk bytes are the exact UTF-8 of the `*Content` source, so their blake3 matches the stored `content_hash` by construction — a fresh `ReadBody` succeeds with no test-side write. Non-mechanical (a consumer-contract shift), so full TDD.

**Approach**
- Keep the `fileContent:` parameter and the `UserServiceContent`/`OrderServiceContent`/`InvoiceContent`/`CafeContent`/`GetUserId`/`TotalMethodId`/… constants exactly as-is (the inspect/edit tests reference them by name).
- In the `files` DDL + INSERT: **DROP the transitional `content` column** H1 kept (the DDL column AND its INSERT value). `content_hash`/`content_bytes` are already written by H1 (`content_bytes = bytes.Length`, `content_hash = "blake3:" + ContentHasher.Blake3Hex(bytes)`) — H3 leaves those untouched. `indexed_at`/`status` and the FK-free `last_revision_id` plain column are also H1's (no seed revision — recon #15); H3 does not touch them.
- The current default-fixture behavior of `content=""` (used by `ReadBody_EmptyFileContent_ReturnsNull` at ExtractReaderTests:142-151) shifts: under D2 `ReadBody` reads disk, so that test is rewritten by subsystem D2 to assert "no on-disk file → null". **Expose the workspace root and pre-materialize the bytes.** Add `public string WorkspaceRoot => _dir;` (the fixture's existing temp dir at JulieDbFixture.cs:182-184, which already holds `symbols.db` and is already deleted recursively in `Dispose` at :672-673 — so no new cleanup is needed). In `Create`, for every fixture file (the `fileContent:` entries plus the `*Content` files baked into `CreateForInspect`/`CreateForEdit`), write the exact UTF-8 bytes to `Path.Combine(_dir, relativePath)`, creating parent directories first (e.g. `auth/`, `unicode/`). Because the same bytes feed both the disk write and the stored `content_hash`, a fresh disk read matches the stored hash with no test-side setup — the C3/D2 tests just call `ReadBody(fx.DbPath, fx.WorkspaceRoot, relPath, …)` and only the drift/missing-file cases do their own `File.WriteAllText`/`File.Delete`. This (not a `FileContents` dictionary) is the explicit seam the design's §7 disk-slice path needs; there is no in-memory content accessor because no consumer reads one — they read disk.
- Coordinate the `blake3:` prefix with subsystem **D** (cross-dep note, not an H3 edit — reconciliation #16a/#16d): `FreshnessGate` normalizes via `ContentHasher.NormalizeHash` (strips `blake3:`) before comparing to a bare-hex disk hash. The fixture writes the prefixed `content_hash` (matching real julie); **D5** normalizes. `FreshnessGateTests.SetFileHash`/`SetHashAlgorithm` are **owned by D5** (that file is in D5's Files block) — D5 retargets them from `files.hash` to `files.content_hash` with the `blake3:` prefix; H3 only guarantees the fixture emits the prefixed value. Flag in cross-deps.

**Steps (TDD)**
1. **Add a failing content-format test** to `JulieDbFixtureV1SchemaTests.cs`:
   ```csharp
   [Fact]
   public void Fixture_FilesStoreContentHashPrefixedAndByteCount_NotContent()
   {
       using var fx = JulieDbFixture.CreateForInspect(); // writes UserServiceContent
       using var c = Open(fx.DbPath);
       using var cmd = c.CreateCommand();
       cmd.CommandText = "SELECT content_hash, content_bytes FROM files WHERE path='auth/UserService.cs';";
       using var r = cmd.ExecuteReader();
       Assert.True(r.Read());
       string hash = r.GetString(0);
       long bytes = r.GetInt64(1);

       Assert.StartsWith("blake3:", hash);
       var expected = System.Text.Encoding.UTF8.GetBytes(JulieDbFixture.UserServiceContent);
       Assert.Equal(expected.Length, bytes);
       Assert.Equal("blake3:" + Miller.Indexing.ContentHasher.Blake3Hex(expected), hash);

       // H3 drops H1's transitional `content` column in Phase 5 — v1 is content-free (D2). (Moved here from
       // H1's lock test, which keeps `content` through Phases 3-4 for the OLD ReadBody path.)
       Assert.False(ColumnExists(c, "files", "content"), "v1 files has no content column after H3");
   }

   [Fact]
   public void Fixture_MaterializesFilesUnderWorkspaceRoot_MatchingStoredHash()
   {
       using var fx = JulieDbFixture.CreateForEdit();

       // Bytes are on disk under WorkspaceRoot (no test-side write), and their blake3 equals the stored content_hash.
       foreach (var (rel, content) in new[]
       {
           ("orders/OrderService.cs", JulieDbFixture.OrderServiceContent),
           ("unicode/Café.cs", JulieDbFixture.CafeContent),
       })
       {
           string abs = Path.Combine(fx.WorkspaceRoot, rel);
           Assert.True(File.Exists(abs), $"{rel} must be materialized under WorkspaceRoot");
           var bytes = File.ReadAllBytes(abs);
           Assert.Equal(content, System.Text.Encoding.UTF8.GetString(bytes));

           using var c = Open(fx.DbPath);
           using var cmd = c.CreateCommand();
           cmd.CommandText = "SELECT content_hash FROM files WHERE path=$p;";
           cmd.Parameters.AddWithValue("$p", rel);
           Assert.Equal("blake3:" + Miller.Indexing.ContentHasher.Blake3Hex(bytes), (string)cmd.ExecuteScalar()!);
       }
   }
   ```
   Run `scripts/test.sh` — fails (old `content` column; no `WorkspaceRoot`/on-disk bytes; bare-hex hash).
2. **Implement** the `files` INSERT change + `WorkspaceRoot` accessor + on-disk materialization + `content_hash`/`content_bytes` write.
3. Run `scripts/test.sh`. H3 lock tests green. The D2-owned rewrites of `ExtractReaderTests.ReadBody*`/inspect-full and `FreshnessGateTests` land in the same commit — verify together.
4. `dotnet build Miller.slnx -c Release` — 0 warnings.

**Acceptance**
- `files` has `content_hash` (`blake3:`-prefixed) + `content_bytes`, no `content` column.
- `fx.WorkspaceRoot` exists; every fixture file's bytes are present on disk under it (parent dirs created), with blake3 matching the stored `content_hash`, and are removed when the fixture is disposed (no temp leak).
- The `content_hash` value equals `"blake3:" + ContentHasher.Blake3Hex(utf8 bytes)` exactly.
- Build 0 warnings.

---

### Task H4 — Migrate the typed test-signal columns in the fixture (D4)

**Files**
- `tests/Miller.Tests/Indexing/JulieDbFixture.cs`: `SymbolRow.Metadata` init-prop (102) and the `symbols` INSERT (240-267)

**What**
v1 promotes the test signal to typed indexed columns `is_test`/`test_container`/`test_lifecycle` (`INTEGER NOT NULL DEFAULT 0`, schema.rs:93-95), still mirrored in `metadata_json`. Subsystem F drops `IndexedSymbol.TestRole` for `bool IsTest` read from the typed column. The fixture must let tests seed the typed columns directly. Non-mechanical (the test-signal seam moves from JSON-in-`metadata` to typed columns), so full TDD.

**Approach**
- Add three init-props to `SymbolRow`: `public bool IsTest { get; init; }`, `public bool TestContainer { get; init; }`, `public bool TestLifecycle { get; init; }` (default false). Keep `Metadata` (now writes to `metadata_json`) so the JSON-mirror is still seedable for any consumer that asserts on `metadata_json`.
- In the `symbols` INSERT, add `is_test, test_container, test_lifecycle` columns written as `r.IsTest ? 1 : 0` etc., and rename `metadata`→`metadata_json`.
- The F-owned `SqliteSymbolReaderTests.Read_IsTest_FromMetadata_IsCrossLanguage` (lines 130-173) and `Read_TestRole_*` (175-225) are rewritten by subsystem F to seed the typed `IsTest` column instead of `Metadata = "{\"is_test\":true}"`. The fixture's job (H4) is to provide the `IsTest` init-prop those rewrites use. Flag in cross-deps.

**Steps (TDD)**
1. **Add a failing typed-column test** to `JulieDbFixtureV1SchemaTests.cs`:
   ```csharp
   [Fact]
   public void Fixture_SymbolRow_SeedsTypedTestColumns()
   {
       using var fx = JulieDbFixture.Create(
           JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
           {
               new JulieDbFixture.SymbolRow("a0000000000000000000000000000001", "T", "method", "csharp",
                   "Tests/T.cs", "public void T()", 3, null) { IsTest = true, TestContainer = true },
               new JulieDbFixture.SymbolRow("b0000000000000000000000000000001", "P", "method", "csharp",
                   "src/P.cs", "public void P()", 3, null), // defaults: all 0
           });
       using var c = Open(fx.DbPath);
       using var cmd = c.CreateCommand();
       cmd.CommandText = "SELECT is_test, test_container, test_lifecycle FROM symbols WHERE symbol_id=$id;";
       cmd.Parameters.AddWithValue("$id", "a0000000000000000000000000000001");
       using var r = cmd.ExecuteReader();
       Assert.True(r.Read());
       Assert.Equal(1L, r.GetInt64(0));
       Assert.Equal(1L, r.GetInt64(1));
       Assert.Equal(0L, r.GetInt64(2));
   }
   ```
   Run `scripts/test.sh` — fails (no `IsTest` init-prop; no `is_test` column).
2. **Implement** the three init-props + the typed-column INSERT (and `metadata`→`metadata_json` rename).
3. Run `scripts/test.sh`. H4 lock test green; subsystem F's reader-test rewrites land same commit.
4. `dotnet build Miller.slnx -c Release` — 0 warnings.

**Acceptance**
- `SymbolRow` exposes `IsTest`/`TestContainer`/`TestLifecycle` init-props; the fixture writes them to the typed `symbols` columns (DEFAULT 0 when unset).
- `metadata`→`metadata_json` rename complete; JSON mirror still seedable.
- Subsystem F's `SqliteSymbolReaderTests` typed-column cases pass (same commit).
- Build 0 warnings.

---

### Task H5 — Migrate `symbol_annotations` (drop `ordinal`) + the `type_argument_usages`/`type_arguments` split + `literals` rename

**Files**
- `tests/Miller.Tests/Indexing/JulieDbFixture.cs`: `TypeArgumentsDdl` (764-776), `LiteralsDdl` (778-794), `SymbolAnnotationsDdl` (796-807), and the header DDL provenance comment (756-762)

**What**
Reshape the three bridge-table DDL constants to v1. These tables are created **empty** by `JulieDbFixture` (the bridge rows are seeded by subsystem-B's `SqliteBridgeReaderTests`, not by H), so H5 owns only the empty-table DDL shape; getting the column names right is what lets `SqliteBridgeReader` open against any fixture without "no such table". Non-mechanical because `type_arguments` splits into two tables and `symbol_annotations` drops a column the old DDL keyed on — so a focused TDD lock.

**v1 shapes (verified)**
- `type_argument_usages(usage_id PK, identifier_id NOT NULL (FK), file_id NOT NULL (FK), path, language, metadata_json)` — schema.rs:192-201 (NEW table).
- `type_arguments(type_argument_id PK, usage_id NOT NULL (FK), parent_type_argument_id (self-FK), ordinal NOT NULL, type_name NOT NULL)` — schema.rs:203-211. The old `identifier_id`/`file_path`/`parent_arg_id`/`id` columns are gone; `identifier_id`/`path` now live on `type_argument_usages` and the reader JOINs.
- `literals(literal_id PK, file_id NOT NULL (FK), path, language, literal_text NOT NULL, kind NOT NULL, carrier, arg_position NOT NULL, containing_symbol_id, start_*/end_*/start_byte/end_byte NOT NULL, confidence NOT NULL, metadata_json)` — schema.rs:213-233. Rename `id`→`literal_id`, `file_path`→`path`.
- `symbol_annotations(annotation_id PK, symbol_id NOT NULL (FK), annotation NOT NULL, annotation_key NOT NULL, raw_text, carrier, metadata_json)` — schema.rs:101-110. **No `ordinal`, no UNIQUE(symbol_id,ordinal).** Subsystem B re-keys the reader's ORDER BY to `(symbol_id, annotation_id)`.

**Approach**
Rewrite the three DDL constants to the v1 column sets above; add the new `TypeArgumentUsagesDdl` constant and `Exec` it alongside the others in `Create` (around line 214). Update the provenance comment block (756-762) to cite `schema.rs` v1 and the new column lists. No INSERT bodies in H (these tables stay empty in `JulieDbFixture`); the seeding/ordering tests are subsystem B's `SqliteBridgeReaderTests` (which must migrate its own inline INSERTs to the JOIN shape in the same commit — flagged in cross-deps).

**Steps (TDD)**
1. **Add a failing bridge-shape test** to `JulieDbFixtureV1SchemaTests.cs`:
   ```csharp
   [Fact]
   public void Fixture_BridgeTables_UseV1ShapesAndAnnotationsHaveNoOrdinal()
   {
       using var fx = JulieDbFixture.CreateDefault();
       using var c = Open(fx.DbPath);

       Assert.True(TableExists(c, "type_argument_usages"));
       Assert.True(ColumnExists(c, "type_argument_usages", "usage_id"));
       Assert.True(ColumnExists(c, "type_argument_usages", "identifier_id"));

       Assert.True(ColumnExists(c, "type_arguments", "type_argument_id"));
       Assert.True(ColumnExists(c, "type_arguments", "usage_id"));
       Assert.True(ColumnExists(c, "type_arguments", "parent_type_argument_id"));
       Assert.False(ColumnExists(c, "type_arguments", "identifier_id"),
           "v1 moves identifier_id onto type_argument_usages");
       Assert.False(ColumnExists(c, "type_arguments", "file_path"), "v1 has no file_path here");

       Assert.True(ColumnExists(c, "literals", "literal_id"));
       Assert.True(ColumnExists(c, "literals", "path"));
       Assert.False(ColumnExists(c, "literals", "file_path"), "renamed to path");

       Assert.True(ColumnExists(c, "symbol_annotations", "annotation_id"));
       Assert.True(ColumnExists(c, "symbol_annotations", "annotation_key"));
       Assert.False(ColumnExists(c, "symbol_annotations", "ordinal"),
           "v1 drops ordinal; ordering re-keys to (symbol_id, annotation_id)");
   }
   ```
   Run `scripts/test.sh` — fails (old `type_arguments.identifier_id`, `literals.file_path`, `symbol_annotations.ordinal`).
2. **Implement** the four DDL constants (incl. the new `TypeArgumentUsagesDdl`), wire `Exec(conn, TypeArgumentUsagesDdl)` into `Create`, and rewrite the provenance comment.
3. Run `scripts/test.sh`. H5 lock test green; subsystem B's `SqliteBridgeReaderTests` (JOIN + `(symbol_id, annotation_id)` ordering) lands same commit.
4. `dotnet build Miller.slnx -c Release` — 0 warnings.

**Acceptance**
- `type_argument_usages` exists; `type_arguments` is the v1 split shape (no `identifier_id`/`file_path`); `literals` uses `literal_id`/`path`; `symbol_annotations` has `annotation_id`, no `ordinal`.
- `SqliteBridgeReader.Read` opens against `CreateDefault()` with no missing-table/missing-column error (verified via subsystem B's tests in the same commit).
- Build 0 warnings.

---

### Task H6 — Add the v1-only tables (`parse_diagnostics`, `parser_inventory`, `language_capabilities`) and refresh DDL provenance

**Files**
- `tests/Miller.Tests/Indexing/JulieDbFixture.cs`: new DDL constants + `Exec` wiring in `Create` (alongside 214-216); the class header docstring (7-15) and the per-DDL provenance comments (682, 756-762, 825-826)

**What**
A v1 artifact always contains `parser_inventory`, `parse_diagnostics`, and the `language_capabilities*` tables (schema.rs:18-26, 235-288). Miller does not currently read them (design §4.3 marks them "opportunity, not required"), but the fixture is "the canonical v1 synthetic schema the fast suite asserts against" (§10H), so it must emit a faithful v1 artifact — and the `--strict-schema`/info path (subsystem A) and any future reader should see a complete artifact. Mostly mechanical (add empty-table DDL), but it includes the load-bearing docstring rewrite, so a small lock test plus careful comment updates.

**Approach**
- Add DDL constants for `parser_inventory`, `parse_diagnostics`, `language_capabilities`, `language_capability_fixtures`, `language_capability_gaps`, `pending_relationships`, `type_facts` (schema.rs:18-26, 155-178, 180-190, 235-288), and the indexes are optional for the synthetic DB (skip — they are not part of the read contract). Create them empty in `Create`. `pending_relationships` and `type_facts` are also v1-only; add them for completeness so the synthetic artifact matches a real one (the `JulieDbFixtureV1SchemaTests.Fixture_EmitsV1ArtifactTables` list from H1 already includes `parse_diagnostics`/`parser_inventory`/`language_capabilities`; extend that list to assert these too).
- Rewrite the class header docstring (lines 7-15): drop "schema_version 28, extract_contract_version 3" and "transcribed verbatim from julie's src/database/schema.rs"; cite `julie-extractors crates/julie-extract-artifact/src/schema.rs` (`SQLITE_SCHEMA_VERSION = 1` / `EXTRACT_CONTRACT_VERSION = 1`). Update the inline provenance comments at 682 ("transcribed verbatim from julie src/database/schema.rs (contract-verified §1)"), 756-762 (M4 bridge tables), and 825-826 (M3 freshness, "pinned julie-server v7.13.1 (schema 28)").

**Steps (TDD)**
1. **Extend the H1 lock test** to require the new tables:
   ```csharp
   [Fact]
   public void Fixture_EmitsV1OnlyTables()
   {
       using var fx = JulieDbFixture.CreateDefault();
       using var c = Open(fx.DbPath);
       foreach (var t in new[] { "parser_inventory", "parse_diagnostics", "language_capabilities",
           "language_capability_fixtures", "language_capability_gaps",
           "pending_relationships", "type_facts" })
           Assert.True(TableExists(c, t), $"v1 artifact table '{t}' must exist");
   }
   ```
   Run `scripts/test.sh` — fails (tables absent).
2. **Implement** the DDL constants + `Exec` wiring + docstring/comment rewrites.
3. Run `scripts/test.sh`.
4. `dotnet build Miller.slnx -c Release` — 0 warnings.

**Acceptance**
- All v1-only tables exist in the synthetic DB.
- The class docstring and every per-DDL provenance comment cite `julie-extract-artifact/src/schema.rs` v1 (no remaining "schema 28"/"contract 3"/"julie src/database/schema.rs" references).
- Build 0 warnings; fast suite green (`scripts/test.sh`).

---

### Task H7 — Migrate LargeDbWriter to the v1 schema in lockstep (scale fixture builder)

**Files**
- `tests/Miller.Tests/Server/LargeDbWriter.cs` (full file: DDL at 31-79, INSERTs at 81-156)

**What**
`LargeDbWriter` is the 50k-symbol bulk builder for the Scale `RebuildLatencyTests`. It builds the **same old schema subset** as `JulieDbFixture` and so must migrate to v1 in the same commit. It feeds `RepositoryIndexLoader.Load` → `SqliteSymbolReader.Read` + `SymbolGraphReader.Read` + `SqliteBridgeReader.Read` + `JulieSchemaGate`, so it must satisfy every post-migration reader SELECT. Non-mechanical (same structural changes as H1-H6, in the bulk-prepared-command style), but the column maps are identical to H1/H2/H4/H5 — apply them. The consuming test is `[Trait("Category","Scale")]`, so verification is `scripts/test.sh scale`.

**Exact change map (mirror H1/H2/H4/H5)**
| Old DDL/INSERT (line) | v1 change |
|---|---|
| `files` (32-34) | `file_id PK, path UNIQUE, language, content_hash, content_bytes, line_count, indexed_at, last_revision_id, status, metadata_json`; INSERT (88-90) writes `content_hash='blake3:h'`, `content_bytes=1`, `status='indexed'`, `last_revision_id=0`, drop `content` |
| `symbols` (36-41) | `symbol_id, file_id, path, …, parent_symbol_id, …, is_test/test_container/test_lifecycle NOT NULL DEFAULT 0, metadata_json`; INSERT (104-106) renames `id→symbol_id`, `file_path→path`, `parent_id→parent_symbol_id`, `metadata→metadata_json`, adds `is_test=0` etc. and a synthetic `file_id` |
| `relationships` (44-46) | `relationship_id, from_symbol_id, to_symbol_id, file_id, path, kind, …, confidence NOT NULL, metadata_json`; INSERT (141-142) renames `id→relationship_id`, adds `file_id`/`path`/`confidence`/`kind` (keep the chain logic at 137-154) |
| `identifiers` (48-52) | `identifier_id, file_id, path, …, confidence NOT NULL, …` (created empty; rename `id→identifier_id`, `file_path→path`) |
| `type_arguments`/`literals`/`symbol_annotations` (58-74) | v1 shapes per H5 (created empty); add `type_argument_usages` empty table |
| `schema_version`+`external_extract_metadata` (75-79) | drop `schema_version` table; single `artifact_metadata(key,value)`; write keys `schema_version`, `sqlite_schema_version`, `extract_contract_version`, `hash_algorithm` from `MillerExtractContract` |
| (none) | add `extraction_revisions` + a seed `revision_id=0` row (FK target for `files.last_revision_id`); add `parse_diagnostics`/`parser_inventory`/`language_capabilities*`/`pending_relationships`/`type_facts` empty tables for v1 fidelity (the gate + readers only need the four it reads, but keep the synthetic artifact complete to match `JulieDbFixture`) |
| header docstring (6-16) | rewrite "schema 28 / contract 3" + "verbatim from julie v7.13.1 schema.rs" to cite `julie-extract-artifact/src/schema.rs` v1 |

`PRAGMA foreign_keys=OFF` (line 28) stays (bulk load), so the FK ordering constraint is relaxed here — but still insert the seed revision and use real `file_id`s so the readers' by-name SELECTs find their columns.

**Representative INSERT snippet (symbols, post-migration)** — locks the rename for the bulk writer:
```csharp
cmd.CommandText =
    "INSERT INTO symbols (symbol_id, file_id, path, language, name, kind, signature, " +
    "start_line, start_column, end_line, end_column, start_byte, end_byte, " +
    "parent_symbol_id, is_test, test_container, test_lifecycle, metadata_json) " +
    "VALUES ($id, $fid, $path, $lang, $name, $kind, $sig, " +
    "$sl, 0, $el, 0, 0, 0, $pid, 0, 0, 0, NULL);";
// $id<-s.SymbolId, $fid<-FileId(s.FilePath), $path<-s.FilePath, $name<-s.Name, $kind<-s.Kind,
// $lang<-s.Language, $sig<-(object?)s.Signature ?? DBNull.Value, $sl<-s.StartLine, $el<-s.EndLine,
// $pid<-(object?)s.ParentId ?? DBNull.Value
```
(`IndexedSymbol` already exposes `SymbolId`/`FilePath`/`ParentId`/`StartLine`/`EndLine` — used at the current lines 120-128, so only the SQL text + a `file_id`/`path` rename + the typed-test columns change.)

**Steps (TDD)**
1. **Add a failing Scale lock test** `tests/Miller.Tests/Server/LargeDbWriterV1SchemaTests.cs`:
   ```csharp
   using Microsoft.Data.Sqlite;
   using Miller.Indexing;
   using Xunit;

   namespace Miller.Tests.Server;

   /// <summary>
   /// Locks the scale fixture builder (LargeDbWriter) to the v1 artifact schema. Scale because it builds a
   /// (small here) DB the production loader path opens; the loader's schema gate must accept it.
   /// </summary>
   [Trait("Category", "Scale")]
   public sealed class LargeDbWriterV1SchemaTests
   {
       [Fact]
       public void Write_ProducesV1Artifact_LoaderOpensAndCounts()
       {
           var symbols = new[]
           {
               new IndexedSymbol(0, "id1", "A", "sig A", "method", "csharp", "src/A.cs", 1, 2, null),
               new IndexedSymbol(1, "id2", "B", "sig B", "method", "csharp", "src/B.cs", 3, 4, null),
           };
           string dir = Path.Combine(Path.GetTempPath(), "miller-ldw-v1-" + Guid.NewGuid().ToString("N"));
           Directory.CreateDirectory(dir);
           string db = Path.Combine(dir, "symbols.db");
           try
           {
               LargeDbWriter.Write(db, symbols);

               using var c = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = db, Mode = SqliteOpenMode.ReadOnly }.ToString());
               c.Open();
               using var cmd = c.CreateCommand();
               cmd.CommandText = "SELECT 1 FROM pragma_table_info('symbols') WHERE name='symbol_id';";
               Assert.NotNull(cmd.ExecuteScalar());
               cmd.CommandText = "SELECT 1 FROM pragma_table_info('files') WHERE name='content_hash';";
               Assert.NotNull(cmd.ExecuteScalar());

               // The production read path must open it (gate + readers) and count both symbols.
               var read = SqliteSymbolReader.Read(db);
               Assert.Equal(2, read.Count);
           }
           finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
       }
   }
   ```
   Run `scripts/test.sh scale` — fails (old `id`/`file_path`/`content`/`schema_version`/`external_extract_metadata`).
   (Note: this test does NOT spawn julie-extract, but it is tagged Scale because it exercises the scale-fixture builder + full loader path; it does not touch `ScaleTestSupport.RequireJulieServer`, so the `ScaleTraitConventionTests` guard is satisfied — the guard is one-directional (spawns julie ⟹ Scale), not the converse.)
2. **Implement** the LargeDbWriter rewrite per the change map (DDL + INSERT column lists + `file_id`/seed-revision/`artifact_metadata`/typed-test-columns + docstring).
3. Run `scripts/test.sh scale`. The new lock test + `RebuildLatencyTests` (`Measure_FullRebuild_*`) must pass against the v1 DB. Also run `scripts/test.sh` to confirm the fast suite stays green (LargeDbWriter is not compiled out of the fast build).
4. `dotnet build Miller.slnx -c Release` — 0 warnings.

**Acceptance**
- `LargeDbWriter.Write` emits a v1 artifact: `symbols.symbol_id`/`path`/`parent_symbol_id`/`is_test`, `files.content_hash`/`content_bytes`, `artifact_metadata` version keys, `extraction_revisions` seed row, no `schema_version`/`external_extract_metadata`/`content`/`file_path`.
- `SqliteSymbolReader.Read` + `RepositoryIndexLoader.Load` + `SymbolGraphReader.Read` + `SqliteBridgeReader.Read` all open the produced DB with no missing-table/column error.
- `RebuildLatencyTests` (Scale) passes and still prints its latency.
- `LargeDbWriterV1SchemaTests` passes (`scripts/test.sh scale`); fast suite green (`scripts/test.sh`); build 0 warnings.

---

### Subsystem H verification gate (per phase — H is split across Phases 3/4/5, not one commit)
- **Phase 3 (H1/H4/H5/H6 + B/C2):** `scripts/test.sh` — fast suite green, <30s wall (the Phase-3 H lock tests + the subsystem-B/C/F consumer tests land in the same read-layer commit). The revision/freshness tests stay OLD-green here (the OLD `FreshnessReader` against the untouched `canonical_revisions`).
- **Phase 4 (H2 + D/E):** `scripts/test.sh` (fast) green AND the first `scripts/test.sh scale` green — the revision/freshness flip lands; scale runtime correctness is validated here (reconciliation #17).
- **Phase 5 (H3 + C3/C4):** `scripts/test.sh` green AND `scripts/test.sh scale` green (disk re-source + `content`-column drop).
- `scripts/test.sh scale` — H7's `LargeDbWriterV1SchemaTests` + `RebuildLatencyTests` green against the v1 `LargeDbWriter` output (H7 is Phase 3, a standalone complete-v1 writer).
- `dotnet build Miller.slnx -c Release` — 0 warnings / 0 errors at every phase.


---

## Global execution sequencing (overrides A–H document order)

Document order is A–H for readability; EXECUTE in these phases (from the critique, aligned to design §12). Each phase compiles/greens only where noted.

- **Phase 1 — unblock with a real v1 DB:** `G1` (from-source restore: `cargo build --release -p julie-extract-cli --bin julie-extract`, plus binary-name plumbing in `JulieExtractRunner.Locate`, `WorkspaceContext.ToolsRoot`, `ScaleTestSupport`, and the `WorkspaceToolTests` literals) **and the `julie-pins.json` DATA repoint + script rename** (version `2.0.0`, slug `anortham/julie-extractors`, four real sha256, `{VER}`-templated names — pure data, lands now so A1's pins-test greens in Phase 2; reconciliation #4/#16c).
- **Phase 2 — contract gate + report:** `A1 → A2 → A3 → A4 → A5 → A6` (one red→green arc; A2+A3 break the build until both land). **A2 also migrates the two report-fixture test files** `IndexerServiceScanTests.cs:57` + `JulieExtractOpsTests.cs:23` to the nested ctor in the same commit it removes the flat `ExtractReport` ctor (reconciliation #7 — deferring them red-breaks Phase 2).
- **Phase 3 — read layer + fixtures (ONE phase; compiles/greens only at the end):** begin with the **TestRole→IsTest atomic commit `B1 + B2 + B4 + F1 + F2 + F3 + F4 + H1 + H4`** (record + reader + bridge/Core consumers + projection + fixture-DDL spine + typed `is_test` writer — reconciliation #6; gate `rg -n "TestRole" src tests` empty), then `C1` (schema gate first), then the **`ReadWorkspaceId`-removal atomic commit `C2 + E1` + the 5 scale-test swaps** (`C2` = ExtractReader v1 renames, drop `code_context` per #11, add `ReadRootPath` per #14, the PATH-lookup test seam per codex#4, DELETE `ReadWorkspaceId`; `E1` = bootstrap root_path identity, migrating the two `IndexBootstrapService` src callers to `ReadRootPath`; plus the 5 scale `ReadWorkspaceId`→`WorkspaceId.FromCanonicalRoot` swaps — the method + ALL src & scale callers vanish in ONE commit, reconciliation #17), `C6`, `C7`, `C5`, `B3`, `H5`/`H6`, `F5`, `H7`. **Phase 3 leaves the revision layer fully OLD** (`canonical_revisions` + `RevisionRow(rev, ws, kind)` untouched; OLD `FreshnessReader` + `FreshnessReaderTests`/`FreshnessServicePollNowTests` stay green), and H1's `files.last_revision_id` is an FK-free plain column with a TRANSITIONAL `content` column (reconciliation #15 / workflow#1) — **`H2` moved to Phase 4.** H5/H6 add only non-revision tables (types/literals/annotations/diagnostics/parser_inventory/language_capabilities), so they do not depend on `extraction_revisions`. (H7's standalone `LargeDbWriter` builds its OWN complete v1 DB incl. `extraction_revisions`; no OLD reader queries it, so it stays in Phase 3.)
- **Phase 4 — freshness + bootstrap + services (one atomic commit):** the entire revision/freshness flip is ONE commit (reconciliation #15 single-step — the void two-step is gone). It begins with **`H2`** (migrate the fixture's `canonical_revisions`→`extraction_revisions`/`revision_file_changes` v1 tables) AND the record-field drop — remove `WorkspaceId` from `JulieDbFixture.RevisionRow`/`RevisionFileChangeRow` (also drop `OldHash`/`NewHash`, rename `FilePath`→`Path`, derive `file_id` via `FileId(path)`); the compiler then forces every call site in #15's inventory to update in THIS commit (D3/D4 rewrite `FreshnessReaderTests` incl. the now-invalid leak tests; E2 also takes `FreshnessServicePollNowTests`, `WorkspaceIndexProviderTests:500`, `IndexBootstrapServiceTests:301`; E3 takes `WorkspaceToolTests` `CreateSynth:157`). Then `D1 → D2 → D3 → D4 → D5` (D3/D4 drop the `workspaceId` param — reconciliation #2), then `E5 → E2 → E3 + E4 → E6` (**E1 already landed in Phase 3** — reconciliation #17), with **every** `LatestRevision`/`ChangedSince` call site updated — incl. `FreshnessService.cs:230` AND the scale callers in `LiveFreshnessTests`/`LiveEditTests`/`MultiProcessWalTests` (reconciliation #17). (The two report-fixture files of reconciliation #7 were already migrated in Phase 2 with A2 — not here.) **Phase-4 exit gates: `scripts/test.sh` (fast) green; `dotnet build Miller.slnx -c Release` 0/0; the scale suite COMPILES and every *freshness* scale test (`LiveFreshnessTests`, `LiveEditTests`, the `MultiProcessWalTests` revision/WAL cases) passes against the real `julie-extract`.** (Implementation reality, verified 2026-06-02: this commit also fixed a pre-existing `JulieExtractRunner` async-output drain race — a missing parameterless `WaitForExit()` after the timed wait left stdout empty → JSON-parse failure on *every* spawned scan; it must land here or no scale test runs.) **The FIRST FULL `scripts/test.sh scale` green is the Phase-5 exit gate, NOT Phase 4** — reconciliation #17 ties first-green-scale to "once the whole *read*+freshness stack is v1," and the read stack's `ExtractReader.ReadBody`/`ReadFileContent` still slices `files.content` (a column the real v1 DB does not have) until C3 (Phase 5). So exactly ONE scale test is a KNOWN, tracked red at Phase-4 exit: `LiveSearchInspectTests.Live_ScanBuildServeSearchAndInspect_WithTelemetryRows` (its `inspect depth:full` reaches `ReadBody`→`SELECT content FROM files`). This is a Phase-5 target, not a Phase-4 regression — the original "first scale green at Phase-4 exit" wording above was internally inconsistent with the Phase-4/5 split (the synthetic fixture's transitional `content` column shields the *fast* suite's `ReadBody`, but nothing shields the *scale* test against the real binary). Acceptance: `rg -n "RevisionRow\(|RevisionFileChangeRow\(|new Rev\(|new Fc\(" src tests` shows zero workspace-id args (reconciliation #15).
- **Phase 5 — file-content disk re-source (one atomic unit):** **`H3 + C3 + C4`** — H3 first adds `WorkspaceRoot` + materializes file bytes to disk AND **drops H1's transitional `content` column** (reconciliation #3/#16); then C3 (disk slice for `ReadBody` with the hard `content_hash` freshness invariant, calling `ContentHasher.NormalizeHash` per #1, AND deletion of the now-dead `ReadIndexedFileText` — reconciliation #16d) + C4 (InspectTool root). The FreshnessGate (D5, Phase 4) relies on `content_hash` alone and passes `indexedText:null`, so it no longer calls `ReadIndexedFileText` at all — which is why C3 can delete it. Gated on D1/D2/D5 (Phase 4). **Phase-5 exit gate: the FIRST FULL `scripts/test.sh scale` green** — once `ReadBody`/`ReadFileContent` re-source from disk and H3 materializes fixture bytes / drops `files.content`, the one Phase-4-deferred red (`LiveSearchInspectTests` inspect-body path) turns green, completing the read+freshness stack's v1 migration (reconciliation #17).
- **Phase 6 — OUT by default:** the design §10E / D3a search-widen is excluded (pure parity). Include only if Alan opts in (design §16); it would add a subsystem here.
- **Phase 7 — download restore + docs:** `G2` (download-branch nested-extract logic only — the pins DATA + script rename already landed in Phase 1), `G3` (csproj Content/Link/Exec), `G4` (CLAUDE.md edit + regenerate AGENTS.md via `scripts/sync-agents.sh`).

Critical inter-phase gates: G1 before anything needing a real DB; A before D/E consume the nested report; the schema gate (C1) before reads; the `is_test` column (B1) before its consumers (F); G2 last.

## Upstream status (design §9) — RESOLVED

All design §9 unknowns closed by the published julie-extractors release (README "Current Release" + `docs/release-evidence/2026-06-01-v2-0-0-release.md`):

- **Release is published:** `v2.0.0`, repo `anortham/julie-extractors`, commit `a1f5069`, published `2026-06-01T21:08:05Z`. The four asset names + SHA-256 digests are confirmed and pinned in G2 (no TBDs remain). The download path (G2) is **no longer blocked** — it runs as the migration's closing validation.
- **From-source still leads (Phase 1, G1)** for fast local iteration; the published assets are the production acquisition route. Both produce a `--version`-able `julie-extract 2.0.0`.
- **Product version ≠ schema/contract version.** Product `2.0.0` ships SQLite **schema 1 / extract-contract 1 / report-schema 1**. The plan pins schema/contract = 1 (the compatibility gate); `PinnedJulieExtractVersion = 2.0.0` is only the download/product pin. Do not collapse the two.
- `change_kind` vocabulary `{inserted,updated,deleted,unsupported}` confirmed final in v1 (no CHECK constraint enforces it; Miller's parser must accept all four and fail loud on anything else).

## Scope

Pure parity with today's Miller behavior on the new contract. **OUT:** the D3a search-widen (design §16 default), incremental in-memory rebuild, and content/full-text search — the latter two remain `TODO.md` items 3 and 4, both gated on post-migration performance measurement. No new user-facing features.

## Acceptance criteria

Inherits the design doc's §14 checklist. Additionally, this plan is complete when:

- [ ] All 17 reconciliations above are honored in the landed code (including #9: blake3 freshness and sha256 fingerprint/asset paths stay separate; #15/#17: the revision/freshness flip is one Phase-4 commit and scale tests compile every phase).
- [ ] `IndexerServiceScanTests` and `JulieExtractOpsTests` construct the nested report and pass.
- [ ] `julie-pins.json` carries the published v2.0.0 slug/version/sha256 (no TBD); `JuliePinsJsonMatchesContractVersion` is a hard assert, not skipped.
- [ ] `scripts/test.sh` (fast) is green and <30s; `scripts/test.sh scale` is green against a from-source `julie-extract`; `dotnet build Miller.slnx -c Release` is 0 warnings / 0 errors.
- [ ] `ScaleTraitConventionTests` still passes (no julie-spawning test left untagged).
- [ ] `MillerExtractContract` pins schema 1 / extract-contract 1 / `blake3`; no `schema_version` table read remains.
- [ ] CLAUDE.md updated and AGENTS.md regenerated (the pre-commit sync hook passes).

## Provenance

Drafted by an 8-subsystem fan-out (each agent read real source via the code-intelligence MCP), then a completeness + quality critique (verdict: needs-fixes → the first reconciliations applied), preceded by a Codex spec review (verdict: not-safe-yet → §10 production-path gaps and §4.3 schema-breakage understatements corrected in the design doc). Two further review rounds expanded the reconciliations to **17**: round 2 resolved internal contradictions (alias/file_id/disk-root/dead-code), and round 3 — an independent Codex adversarial review plus a parallel verification workflow, both verdict needs-fixes — added #17 (scale-test compile discipline), corrected #15 to a single-step Phase-4 flip (the two-step was runtime-broken — workflow#1), fixed the A2 `recoverable` contract (#4) + fingerprint hash-domain (#9), the `ReadIndexedFileText` deletion range (#16d), the E2/E4 `workspaceId` drop (#2), and the PATH-flaky `Locate` test (codex#4). Every round-3 finding was re-verified against real julie-extractors / Miller source before applying. Cited line numbers were spot-checked against real files; two trivial line-drifts are noted in-task. Full design rationale and the contract delta: `docs/plans/2026-06-01-julie-extractors-migration-design.md`.
