# Task 7 report — Provenance surfacing + contract docs

**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/rebind-p3-miller-wiring`
**Branch:** `rebind-p3-miller-wiring`
**HEAD at start:** `0d8fb3e7`
**Commit SHA:** none - parallel-lead-commit

## What I implemented

A rebound workspace now says so, on every surface that renders `scan_failure`, plus the dashboard
detail and the Eros-facing contract doc.

1. **Read path.** `RebindProvenanceReader` (new, `src/Miller.Indexing/RebindProvenance.cs`) reads the
   three additive optional artifact-metadata keys — `rebound_from_root`,
   `rebound_from_artifact_id`, `rebound_at` — and answers `RebindProvenanceMetadata?`. It mirrors
   `ExtractIndexLevelReader`'s tolerance exactly: absent key, absent table, absent file, corrupt DB,
   or any read failure all answer **null**, the never-rebound state. `rebound_from_root` is the
   identity of the record: blank or absent ⇒ null, so an empty object can never reach the JSON.
2. **Fact.** `RebindProvenanceFacts(SourceRoot, SourceWorkspace, SourceArtifactId, ReboundAt)` in
   `WorkspaceRender.cs`, beside `IndexLevelFacts`; `WorkspaceFacts` gains a trailing optional
   `RebindProvenance` param (additive, defaults null).
3. **Gather + registry lookup.** `WorkspaceFactsAssembler.RebindProvenanceFactsFor(dbPath, registry)`
   maps the metadata to the fact and resolves `SourceWorkspace` by ROOT —
   `registry.Get(WorkspaceId.FromCanonicalRoot(sourceRoot))?.DisplayId`. Unregistered or
   unresolvable source root ⇒ null display id, raw root still rendered. Wired into
   `FromRegisteredRow` (CLI + MCP status/health for a registered workspace),
   `FromUnregisteredLocal` (no registry ⇒ null display id), and `WorkspaceTool.AssembleFacts` (the
   in-server MCP status path for the current workspace).
4. **Render (4 sites, exactly where `scan_failure` renders).**
   - status compact: `rebound_from: <display id> (<root>) at <rebound_at>`
   - status JSON: top-level `rebound_from` object
   - health compact: same line, through `HealthCompactValue`
   - health JSON: the byte-identical object (asserted by a parity test)
   `json-summary` health is untouched — the object never appears there.
   `rebound_at` is written verbatim as stored; nothing reparses or reformats it.
5. **Dashboard.** `DashboardWorkspaceFacts` gains four additive optional flat fields
   (`rebound_from_root`, `rebound_from_workspace`, `rebound_from_artifact_id`, `rebound_at`).
   `DashboardData.WithRebindProvenance` enriches the SELECTED workspace's facts (the detail view)
   from the artifact, resolving the display id against the registry rows the snapshot already read —
   no extra registry open, no index hydration. `BuildWorkspaceFacts` carries the same fact into the
   `WorkspaceFacts` the health panel is built from. `WorkspaceDetailPanel.razor` renders a
   conditional "Rebound from" row (display id or raw root, relative timestamp, source artifact chip).
6. **Contract doc.** New `### rebound_from (additive, conditional)` section in
   `docs/contracts/cli-eros-v1.md`, written in the `scan_failure` section's exact style: what it is,
   when it is **omitted entirely**, that its absence is never an error, that it is not in
   `json-summary`, a field table with types and null semantics, and the read-it-as guidance (it is
   lineage/history, not a pending or degraded state — a rebound artifact is a NEW generation, so
   `index.artifact_id` never equals `rebound_from.source_artifact_id`). Placed immediately before
   `index_level`, after `scan_failure`.

## Verification

| Check | Command | Result |
|---|---|---|
| Red first | `dotnet test --filter "FullyQualifiedName~WorkspaceRenderTests"` before implementation | FAILED — `CS0246: RebindProvenanceFacts could not be found` |
| Render tests | `dotnet test --filter "FullyQualifiedName~WorkspaceRenderTests"` | PASS — 95/95 (12 new) |
| Reader + render | `dotnet test --filter "…RebindProvenanceReaderTests\|…WorkspaceRenderTests"` | PASS — 103/103 (20 new) |
| src build, warnings-as-errors | `dotnet build Miller.slnx -c Debug`; `dotnet build src/Miller.Server -c Release`; `dotnet build src/Miller.Dashboard -c Release` | Build succeeded, 0 warnings |
| Worker ceiling | `scripts/test.sh` | PASS — 6112 passed, 0 failed, 2 skipped (env-gated), 24s |

**One self-inflicted red on the way:** `RebindProvenanceReader.Read(null)` in my new test was
ambiguous between the `string?` and `SqliteConnection` overloads (`CS0121`). The connection overload
had no caller outside the type, so I made it private rather than casting at the call site — the
public surface is now the single `Read(string?)`.

**A note on suite timing:** for roughly 25 minutes the shared test assembly could not compile because
Task 6 was mid red-green in its own files — first `CS0246` for the not-yet-created `RebindBootstrap*`
types, then `xUnit1051` in `RebindBootstrapTests.cs`, then `CS0103` for `TryRebindFromMainCheckout`
in `IndexBootstrapService.cs`. Every one of those was in Task 6's files. The suite run recorded above
was taken after they cleared.

## Files changed

| File | Change |
|---|---|
| `src/Miller.Indexing/RebindProvenance.cs` | NEW — `RebindProvenanceMetadata` + tolerant `RebindProvenanceReader` |
| `src/Miller.Server/Tools/WorkspaceRender.cs` | `RebindProvenanceFacts` record, `WorkspaceFacts.RebindProvenance`, `RebindProvenanceLabel`, `WriteRebindProvenanceJson`, 4 render sites |
| `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs` | `RebindProvenanceFactsFor` + `SourceDisplayId`; wired into `FromRegisteredRow` and `FromUnregisteredLocal` |
| `src/Miller.Server/Tools/WorkspaceTool.cs` | one line in `AssembleFacts` so the in-server MCP status path carries the fact |
| `src/Miller.Dashboard/DashboardData.cs` | four additive fields on `DashboardWorkspaceFacts`; `WithRebindProvenance`; `BuildWorkspaceFacts` carries the fact |
| `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor` | conditional "Rebound from" fact row |
| `docs/contracts/cli-eros-v1.md` | new `rebound_from` section |
| `tests/Miller.Tests/Server/WorkspaceRenderTests.cs` | 12 new render tests |
| `tests/Miller.Tests/Indexing/RebindProvenanceReaderTests.cs` | NEW — 8 read-boundary tests |

## Miller calls and what each confirmed

The worktree index predates this branch's commits, so several answers came from HEAD reads after
the Miller attempt; each is marked.

| Call | Confirmed |
|---|---|
| `inspect(target='src/Miller.Server/Tools/WorkspaceRender.cs', depth=summary)` | The file's fact records (`IndexLevelFacts`, `SemanticBrokerFacts`, `WorkspaceFacts`, `WorkspaceListFacts`) and that `WorkspaceFacts` is a positional record struct with trailing optional params — the additive seam. Symbol listing paged (586 more), so the render-site line numbers came from a HEAD read. |
| `search(query='scan_failure', mode='source')` | Found the precedent's test fixtures and the policy/journal types, but the *render* sites were not in the indexed hits — this is the stale-index case. Fell back to a HEAD `grep` for `scan_failure` / `ScanFailure`, which located `WorkspaceRender.cs:401` (status compact), `:741` (status JSON), `:1087` (health compact), `:1267` (health JSON) — the exact four-site set `rebound_from` had to match. |
| HEAD read `WorkspaceRender.cs:360-560, 640-780, 1040-1340` | The conditional-object pattern verbatim: `XLabel(fact) is { } label` for compact (null ⇒ no line at all) and `facts.X is { } x` + `WritePropertyName` for JSON; `HealthSummaryJson` is a separate writer, which is why `json-summary` stays clean without extra work. |
| HEAD read `WorkspaceFactsAssembler.cs` | The metadata read path that serves status facts: `IndexLevelFactsFor(indexDbPath, registryPolicy)` and `ScanFailureFacts(indexDbPath)` are the shape to mirror, and `FromRegisteredRow` already takes the `WorkspaceRegistry` I needed for the display-id lookup. |
| HEAD grep `new WorkspaceFacts(` | The six construction sites: 4 in `WorkspaceFactsAssembler`, 1 in `WorkspaceTool.AssembleFacts`, 1 in `DashboardData.BuildWorkspaceFacts` — i.e. render-only would have surfaced nothing in production. |
| HEAD read `WorkspaceRegistry.cs` (`Get`, `List`, `FindMainCheckoutByCommonDir`, DDL) + `WorkspaceId.cs` | The registry root-lookup API. `Get(workspaceId)` is the exact-and-cheap path because `WorkspaceId.FromCanonicalRoot` already strips the Windows verbatim prefix and case-folds on Windows/macOS — the same normalization `ArtifactRootIdentity.Matches` performs. No `List()` scan needed. |
| HEAD read `IndexLevels.cs:199-250` | `ExtractIndexLevelReader`'s tolerance contract — the template `RebindProvenanceReader` copies (parameterized scalar read, `SqliteException`/`IOException`/`InvalidOperationException`/`UnauthorizedAccessException` all degrade). |
| HEAD read `DashboardData.cs:120-142, 940-960, 1101-1250` + `WorkspaceDetailPanel.razor` | The dashboard detail seam: `DashboardWorkspaceFacts` is the detail DTO, `selectedFacts` is where the detail view's copy is produced, `workspaces` is already in scope for the display-id resolution, and the panel renders a `fact-list` of `<dt>/<dd>` rows. |
| HEAD read `docs/contracts/cli-eros-v1.md:173-260` | The `scan_failure` documentation style: heading form, "omitted entirely" sentence, "never as an error", "NOT part of json-summary", field table, then read-it-as prose. |
| HEAD read `tests/.../WorkspaceRenderTests.cs` | Fixture style: static `Facts()` + `with { … }`, `Json(string)` helper, byte-identical-baseline assertions for every conditional object. |

## API-shape evidence

- **Metadata key spellings** — `rebound_from_root`, `rebound_from_artifact_id`, `rebound_at`,
  verified at HEAD in `tests/Miller.Tests/Indexing/RebindVerbScaleTests.cs:49-50,86` (Task 5's
  fixtures assert the verb writes exactly these) and in the design doc
  `docs/plans/2026-08-05-rebind-contract-design.md:100-101`. Not invented.
- **Status/health JSON composition sites** — `WorkspaceRender.StatusJson` (the `scan_governor` →
  `scan_failure` → `index_level` conditional block before `"index"`) and `WorkspaceRender.HealthJson`
  (the same block before `"index"`). `HealthSummaryJson` is a distinct writer and was left alone.
- **Metadata read path** — `WorkspaceFactsAssembler.IndexLevelFactsFor` /
  `ScanFailureFacts` / `TryReadArtifactId`, all `(string? indexDbPath) → fact?` statics called from
  the `new WorkspaceFacts(...)` argument lists.
- **Registry root lookup** — `WorkspaceRegistry.Get(string workspaceId) → WorkspaceRegistryRow?`
  with `WorkspaceRegistryRow.DisplayId`, keyed by `WorkspaceId.FromCanonicalRoot(root)`.
- **Dashboard detail seam** — `DashboardData.ReadSnapshot`'s `selectedFacts` →
  `DashboardSnapshot.SelectedWorkspaceFacts` → `WorkspaceDetailStack.razor` →
  `WorkspaceDetailPanel.razor`'s `Facts` parameter.

## Self-review

- **Never an empty object.** Two independent guards: the reader answers null unless
  `rebound_from_root` is present and non-blank (`RebindProvenanceReaderTests`), and the render sites
  are `is { } x` conditionals (`Status_*_IsByteIdenticalWhenTheArtifactWasNeverRebound`, which also
  assert the string `rebound_from` does not appear at all).
- **Byte-identical default output.** Four tests compare the baseline render against an explicit
  `RebindProvenance = null` render, for status compact/JSON and health compact/JSON.
- **Health/status JSON parity** is asserted structurally (`GetRawText()` equality), not by
  duplicated field assertions, so a future field added to one writer and not the other fails.
- **`rebound_at` verbatim.** The fixture uses `2026-08-05T09:14:22.123456789Z` — nanosecond
  precision that a `DateTimeOffset` round-trip would not reproduce — and the test asserts the exact
  string. The code path is `string` end to end; no parse.
- **Render stays pure.** The registry lookup happens at fact-gather time in the assembler;
  `WorkspaceRender` does no I/O and takes no registry.
- **No schema changes, no new services, no caching.** One tolerant static reader; one `with`
  enrichment on an already-cached dashboard record.
- **Additive everywhere.** `WorkspaceFacts` and `DashboardWorkspaceFacts` gained trailing optional
  params, so every existing positional construction site (including the dashboard tests) still
  compiles.

## Judgment calls

1. **Reader placed in `Miller.Indexing`, not `WorkspaceRender.cs`.** The brief offered
   `WorkspaceRender.cs` as the fallback home for a metadata-read helper. I created
   `src/Miller.Indexing/RebindProvenance.cs` instead, because both Miller.Server AND Miller.Dashboard
   need the read, and the brief's fallback would have forced the SQL to be duplicated into
   `DashboardData.cs`. The new file sits beside `ExtractIndexLevelReader` and copies its contract; it
   is a new file, so it cannot collide with any parallel task. `WorkspaceRender.cs` still owns the
   FACT record (`RebindProvenanceFacts`), matching where `IndexLevelFacts` lives.
2. **Wired the fact into the gather sites, which are outside the declared modify list.**
   `WorkspaceFactsAssembler.cs` (2 sites) and `WorkspaceTool.cs` (1 line). Render-only would have
   made the acceptance criterion "`workspace status --json` includes `rebound_from` when the artifact
   carries the keys" false in production and true only in unit tests — a stub. Neither file is owned
   by Task 6 (`RebindBootstrap.cs` / `IndexBootstrapService.cs` / its own tests), so there is no
   parallel-edit conflict. The two never-readable arms (`MissingIndexFacts`, `UnreadableIndexFacts`)
   were deliberately left alone: the artifact is missing or unreadable there, so the reader would
   answer null anyway.
3. **Dashboard razor edit.** `WorkspaceDetailPanel.razor` was not in the modify list, but
   `DashboardWorkspaceFacts` is a DTO — without the panel row, "the dashboard workspace detail shows
   the same facts" would not hold. One conditional `<div>` in the existing `fact-list`.
4. **Second test file.** `RebindProvenanceReaderTests.cs` — the brief scoped tests to
   `WorkspaceRenderTests.cs`, but that file is documented as the PURE, no-I/O renderer suite, and the
   "keys absent ⇒ no object" rule actually lives at the read boundary. Putting a temp-file SQLite
   fixture into the pure suite would have broken its stated contract, so the read-boundary tests got
   their own fast-suite file next to `ExtractIndexLevelReaderTests`.
5. **Compact line shape.** The brief's phrasing is "rebound from `<display id>` at `<rebound_at>`".
   I render `rebound_from: <display id> (<source root>) at <rebound_at>` so the line matches the
   house `key: value` compact style, and keeps the raw root visible in BOTH the registered and
   unregistered cases (the unregistered case drops the display id and its parentheses). The ` at …`
   clause is dropped when the artifact records no instant. Documented in the contract doc.
6. **Object gated on `rebound_from_root` alone.** The verb writes all three keys in one
   transaction, so a partial set should be impossible; gating on the root and rendering the other
   two as explicit nulls degrades field-by-field instead of suppressing the whole fact, and keeps the
   JSON object's field set fixed for Eros.

## Concerns

- **The fast suite passed against Task 6's tree as it stood at that moment.** Task 6 is still
  in flight; if it changes `IndexBootstrapService` further, the suite is worth one more run at the
  branch gate.
- **No end-to-end assertion that a really-rebound artifact renders `rebound_from`.** That needs a
  real `julie-extract rebind` run, which lives in Task 6's Scale test (`RebindBootstrapScaleTests`)
  and at the branch gate. My coverage stops at the read boundary (a hand-built artifact carrying the
  keys) and the render.
- **The `WorkspaceTool.AssembleFacts` path still omits `index_level`** — a pre-existing gap I did not
  widen or close (out of scope). `rebound_from` is wired there.
