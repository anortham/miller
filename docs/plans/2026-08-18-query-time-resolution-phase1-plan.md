# Query-Time Resolution Phase 1 (Miller) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Miller computes reference resolution at query time from the store's fact tables, stops submitting resolve requests to julie-extract, and removes the dead-code candidates feature — so a save reaches a correct index in seconds instead of minutes.

**Architecture:** A pure resolver in `Miller.Core.Resolution` implements julie's resolution policy v6 (vendored as `docs/contracts/resolution-policy-v6.md`). A resident, interned `RevisionFactCache` in `Miller.Indexing` feeds it facts per index identity with file-local invalidation; identifiers are streamed per query, never resident. The two store read seams (`IFamilyGraphResolutionReader`, `ReferenceEvidenceReader` family-store arms) swap to the resolver in the same change that deletes the resolution-base ATTACH machinery and the resolution-state tool guards. Then resolve submission stops entirely. No fallback mode — the store-resolution read path is deleted, not flagged off (user decision 2026-08-18, recorded in `docs/plans/2026-08-18-query-time-resolution-integration-design.md`).

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, xUnit. No new dependencies.

## Cross-repo phase map (all phases, up front)

This is Plan A of a two-plan pair designed for parallel sessions:

- **Plan A (this plan, Miller repo):** query-time resolution, stop submitting resolves, remove dead-code candidates. Runs entirely against the pinned julie-extract **2.33.7** — the pin does NOT change in this plan.
- **Plan B (julie-extractors repo, sibling plan `docs/plans/2026-08-18-resolution-write-path-removal-plan.md` in that repo):** remove the resolution write path (resolve command, session, bases/deltas/scope journal, schema bump) and fix the dual language-classification `.h` bug. Independent of Plan A's code; can run in a parallel session.
- **Phase 3 (Miller repo, MANDATORY completion step after BOTH plans land — Plan A is not releasable without it):** bump `scripts/julie-pins.json` to the Plan B release, re-run restore + the Scale suite, verify the off-mode export + serve smoke (export works again — Plan B removes the exact-state refusal), adapt Plan A's parity fixture (see Task 6 — it must skip, not fail, when the pinned binary no longer ships `resolve`), adapt the store-client report DTO tolerance (Task 5's shim) to Plan B reports without resolution fields, update release notes, and release Miller + plugin manifests. Every release step needs explicit user approval.

**Parallel-session safety rules:** (1) Plan A never edits julie-extractors; Plan B never edits Miller. (2) Plan A's Task 6 parity gate REQUIRES the 2.33.7 binary's `resolve` — Plan B's work cannot break it because the pin is a released, restored binary. (3) Plan B's schema changes must not remove or alter any non-resolution table Miller reads (symbols, identifiers, type_facts, pending_relationships, relationships, structural_facts, manifest/generation/view tables) — that constraint is restated in Plan B. (4) Neither plan releases; Phase 3 is the only integration point and it happens after both plans are complete.

**Known interim regression (accepted, restored at Phase 3):** julie 2.33.7's `store export` refuses unless the view's `resolution_state` is `exact` (its `export.rs:146-149`). Once Task 5 stops submitting resolves, changed views never reach `exact` again, so the `MILLER_INDEX_STORE=off` export path (`StoreRollbackExporter`) fails with julie's refusal until the Phase 3 pin bump to a Plan B binary whose export no longer requires or copies resolution. Do NOT work around this by submitting resolves from the export path. No release ships inside the window. Phase 3's verification includes an off-mode export + serve smoke.

**Web facts / bridge impact: none negative, one improvement.** The `trace bridge` web-facts feature reads `structural_facts` and Miller's lexical name resolver (`Miller.Core.Resolver`), not the store resolution layer. Its only coupling is the `ResolutionLayerConverging` guard (`TraceTool.cs:137-147`), which today blanks ALL trace output — including bridge mode — while resolution converges. Task 4 deletes that guard, so `trace bridge` stops going dark after saves. `structural_facts` extraction is untouched by both plans.

**Architecture Quality:** Approved shape: policy engine is pure logic in `Miller.Core` behind `IResolutionFacts` (zero I/O — this is load-bearing project law); all SQLite access lives in `Miller.Indexing`. Main risk: memory — the naive spike held 2.96 GB at aspnetcore scale, so the cache MUST intern symbols into packed arrays and MUST NOT hold identifier rows resident. Second risk: parity drift — every behavioral rule comes from `docs/contracts/resolution-policy-v6.md`, and the spike source (`git show prototype/query-time-resolution:spike/query-time-resolution/Program.cs`) is the working reference port. If code reality contradicts this shape, report a plan mismatch; do not redesign locally.

## Global Constraints

- Read-output parity is PER SURFACE — the three read surfaces use DIFFERENT reason strings and confidence rules, and each must be copied from its own outgoing SQL, never assumed shared. Graph edges (`SqliteSymbolGraphIndex`): `identifier_target`/`identifier_name`, name-fallback confidence `× 0.5`. Evidence/JSON (`ReferenceEvidenceReader*`, rendered by e.g. `TraceTool`): `identifier_resolution`/`name_fallback`/`pending_resolution`/`relationship`, fallback `MIN(confidence, 0.5)`, and UNRESOLVED pending rows are emitted as `name_fallback` evidence rows. Export (`ReferenceExportReader`, `docs/contracts/references-export-v2.md`): the evidence label set with its documented column shape (NULL tier on fallback rows, `COALESCE`/`MIN` confidence rules). Every swap task must include golden serialized-output tests per surface built from the retired SQL's literals. JSON contracts must not change shape except where this plan names the change.
- `Miller.Core` keeps ZERO I/O dependencies. The resolver and its types never touch SQLite.
- Build is `dotnet build Miller.slnx -c Release`, warnings are errors. Target `net10.0`.
- File:line references in tasks are orientation hints taken from the branch-base tree; edits in earlier tasks shift them. Locate by symbol name (Miller `inspect`/`search`), never by raw line.
- Any test that spawns `julie-extract` gets class-level `[Trait("Category","Scale")]` and obtains the binary via `ScaleTestSupport.RequireJulieServer()`.
- No new MCP tools. `MILLER_AGENT_INSTRUCTIONS.md` is not edited (it does not mention resolution state or dead-code candidates).
- `AGENTS.md` is generated: edit `CLAUDE.md`, then run `scripts/sync-agents.sh`, then confirm `cmp -s CLAUDE.md AGENTS.md`.
- Tests carry zero comments; no narration comments anywhere; remove narration comments encountered in lines already being changed.
- Commits stay on this worktree branch (`worktree-query-time-resolution`). No push, no tag, no release — those need explicit user approval.
- Julie pin stays 2.33.7 for all of Phase 1; the pinned binary still supports `resolve`, which Task 6 uses to produce parity ground truth. Producer-side removal is Phase 2, a separate plan in julie-extractors.
- Env vars `MILLER_SEARCH_SIDECAR` and `MILLER_SEMANTIC` semantics are unchanged. `MILLER_INDEX_STORE=off` keeps its meaning but its EXPORT step is knowingly broken between Task 5 and the Phase 3 pin bump (see the phase map) — Plan A is NOT releasable until Phase 3 restores it. No new env var is introduced (no fallback flag — that is the point).

## Verification Strategy

**Project source of truth:** `CLAUDE.md` §Testing (fast suite = `Category!=Scale` enforced by csproj filter; Scale opt-in via `scripts/test.sh scale`).

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<TestClassName>"` for the classes the task creates or edits.

**Worker ceiling:** the fast suite (`scripts/test.sh`). Workers never run the Scale suite on their own; Task 6's worker is the exception because Scale IS its assigned scope.

**Worker gate invariant:** Task 1 — every policy rule in `resolution-policy-v6.md` has a passing unit test against in-memory facts. Task 2 — no symbol under `Miller.Core.DeadCode` or `DeadCodeCandidateReader` remains; fast suite green. Task 3 — cache answers `IResolutionFacts` queries identically to direct SQL on a fixture store, and file-local invalidation reloads only changed versions. Task 4 — graph/evidence reads on a fixture store return the same edge tuples the old resolution views returned. Task 5 — no code path constructs a resolve request; status/health JSON no longer emits resolution-layer fields. Task 6 — parity, latency, and memory gates measured and recorded.

**Lead affected-change scope:** `scripts/test.sh` (fast suite) after Batch A lands and after each serial task lands.

**Branch gate:** `dotnet build Miller.slnx -c Release` + `scripts/test.sh all` once, before handoff/PR.

**Security scope:** none declared.

**Replay/metric evidence:** hard gates — parity on the fixture store (100% or every divergence explained as producer under-resolution), warm refs p95 ≤ 500 ms at aspnetcore scale, WHOLE-HOST idle memory ≤ 350 MB and peak ≤ 600 MB at aspnetcore scale (the budgets from `docs/plans/2026-08-13-miller-performance-recovery-plan.md` — not cache-only), save-to-correct-answer ≤ 5 s live. Report-only — cold load time, cold FIRST-query latency, full-sweep time, peak RSS during load, multi-workspace eviction behavior.

**Escalation triggers:** any edit under `src/Miller.Indexing` or to the coordinator/indexer requires the Scale suite at the branch gate (it is already in the plan via Task 6). Unexplained parity divergence stops the run — that is a plan-mismatch report, not a local fix.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp per task. Baseline already on ledger: Release build 0 warnings/0 errors, fast suite 6,701 passed / 0 failed, 26 s, at branch base. Reuse passing evidence on an unchanged HEAD instead of rerunning.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Resolver core | Batch A | Create `src/Miller.Core/Resolution/**`, `tests/Miller.Tests/Core/Resolution/**` only | No | None - safe parallel batch. |
| Task 2: Dead-code removal | Batch A | Delete `src/Miller.Core/DeadCode/**`, `src/Miller.Indexing/DeadCodeCandidateReader.cs`, 3 dead-code test files; modify `CliDispatch.cs`, `CliCapabilities.cs`, `MetricsTool.cs`, `DashboardIndexFactsReader.cs` (usings/registration lines only), named docs incl. `CLAUDE.md` + regenerated `AGENTS.md` + a superseded-banner on `docs/plans/2026-08-17-windows-dogfood-read-availability-plan.md` dead-code references, named test files | No | None - safe parallel batch. Disjoint from Task 1's created paths. |
| Task 3: Fact cache | None - serial | Create `src/Miller.Indexing/Resolution/**`, `tests/Miller.Tests/Indexing/Resolution/**`; modify `MillerServiceRegistration.cs` | Yes | Implements Task 1's `IResolutionFacts`; must follow Batch A. |
| Task 4: Read-path swap | None - serial | Create `Reads/QueryTimeResolutionReader.cs`; modify `FamilyGraphResolutionReader.cs`, `FamilyStoreReadSession.cs`, `SqliteSymbolGraphIndex.cs`, `ReferenceEvidenceReader.FamilyStore.cs`, `ReferenceEvidenceReader.cs`, `ReferenceExportReader.cs`, `SymbolGraphReader.cs`, `RepositoryIndexLoader.cs`, `JulieSchemaGate.cs`, `WorkspaceIndexProvider.cs`, `WorkspaceReadSessionFactory.cs` + enumerated session-construction call sites, `IndexLevelGuard.cs`, `TraceTool.cs`, `ImpactTool.cs`, `ContextTool.cs`, `InspectTool.cs`, `EditService.cs`, `CliDispatch.cs` (export-warning block only), `docs/contracts/references-export-v2.md` (note only if wording changes), matching tests incl. delete `ResolutionLayerGuardTests.cs` | Yes | Consumes Task 3's cache and Task 1's resolver. |
| Task 5: Stop resolve submission | None - serial | Modify `StoreWorkspaceCoordinator.cs`, `JulieStoreClient` resolve surface, `IndexerCore.cs`, `IndexerService.cs`, `IndexBootstrapService.cs`, `CrossWorkspaceRefreshService.cs`, `WorkspaceRender.cs`, `WorkspaceFactsAssembler.cs`, `WorkspaceHealthFacts.cs`, `DashboardIndexFactsReader.cs`, `WorkspaceDetailPanel.razor`, `StoreSidecarStamp.cs`, `docs/contracts/workspace-status-v1.md`; delete `StoreResolutionCarry.cs` + its tests; matching tests | Yes | Tools must already answer without resolution state (Task 4) before submission stops. |
| Task 6: Gates, scale tests, docs | None - serial | Create/modify Scale test files (`LiveReferenceResolutionScaleTests.cs`, new parity test), `docs/findings/2026-08-18-query-time-resolution-phase1-gates.md`, `CLAUDE.md` + regenerated `AGENTS.md`, `docs/README.md`, design doc status line | Yes | Measures the finished system. |

Commit modes: Batch A runs `parallel-lead-commit` (workers hand verified diffs to the lead). Tasks 3–6 run `serial-worker-commit`.

Plan-package commit ownership: the currently untracked supporting documents — this plan, `docs/contracts/resolution-policy-v6.md`, `docs/plans/2026-08-18-query-time-resolution-integration-design.md`, `docs/findings/2026-08-18-query-time-resolution-spike.md`, `docs/findings/2026-08-18-whole-stack-architecture-assessment.md`, and the `.memories/` checkpoints — are committed by the LEAD as the first commit on this branch, before Batch A dispatch.

---

### Task 1: Resolver core in Miller.Core (pure policy engine)

**Files:**
- Create: `src/Miller.Core/Resolution/` — suggested split: `ResolutionTypes.cs` (inputs/outcomes/keys), `IResolutionFacts.cs`, `ResolutionPolicy.cs` (kind tables, tier chains, confidence constants), `QueryTimeResolver.cs` (driver + tiers), `ImportBinding.cs` (import metadata parse + module-path candidates), `PropagationLocator.cs` (span-based `locate_identifier` rule as pure logic)
- Test: `tests/Miller.Tests/Core/Resolution/` — one class per tier plus driver/chain tests, backed by a hand-built in-memory `IResolutionFacts` fixture

**Interfaces:**
- Consumes: `docs/contracts/resolution-policy-v6.md` (the complete behavioral spec — implement it rule-for-rule) and the spike reference port, retrievable read-only via `git show prototype/query-time-resolution:spike/query-time-resolution/Program.cs`.
- Produces (exact, later tasks compile against these):

```csharp
namespace Miller.Core.Resolution;

public enum ResolutionOrigin { Identifier, Pending }
public enum ResolutionRefKind { Call, Instantiates, TypeUsage, MemberAccess, VariableRef }
public enum ResolutionOutcomeKind { Resolved, Ambiguous, Missing, NoContext }

public readonly record struct FactSymbolKey(long VersionId, string SymbolId);

public sealed record ResolutionInput(
    ResolutionOrigin Origin, ResolutionRefKind RefKind, string Language, long VersionId,
    string Name, string? Receiver, string? ReceiverQualifier,
    string? CallerScopeSymbolId, double SourceConfidence);

public sealed record ResolutionOutcome(
    ResolutionOutcomeKind Kind, FactSymbolKey? Target,
    int? Tier, double? Confidence, string? Method, int? CandidateCount);

public interface IResolutionFacts
{
    IEnumerable<FactSymbol> SymbolsNamed(string name);
    FactSymbol? Symbol(FactSymbolKey key);
    IReadOnlyList<FactSymbol> ChildrenOf(FactSymbolKey parent);
    IReadOnlyList<FactSymbol> TopLevelOf(long versionId);
    IReadOnlyList<FactTypeFact> TypeFactsOf(FactSymbolKey symbol);
    IReadOnlyList<ImportBinding> ImportsOf(long versionId);
}

public sealed class QueryTimeResolver(IResolutionFacts facts)
{
    public ResolutionOutcome Resolve(ResolutionInput input);
}
```

`FactSymbol` carries at least key, name, kind (enum), language, parent key (nullable), signature, visibility, and the tri-state `isStatic`. `FactTypeFact` carries resolved type name + `is_inferred`. `ImportBinding` carries the parsed fields from the contract's ImportRecord plus resolved `ModuleVersionId` (nullable, resolved by the caller/cache — the pure module-candidate path computation lives here, the manifest lookup does not). Mapping helpers `ResolutionKinds.FromIdentifierKind(string)` and `FromPendingKind(string)` return nullable enum (`null` ⇒ no_context / skip). `PropagationLocator.Locate` implements the exactly-one span rule over a caller-supplied candidate list.

**Contract inputs:** `docs/contracts/resolution-policy-v6.md` verbatim; existing `Miller.Core` style (records, no I/O).

**File ownership:** Create `src/Miller.Core/Resolution/**`, `tests/Miller.Tests/Core/Resolution/**` only

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Port the spike's resolver into production-quality pure logic. The driver, tier chains, four tiers, scope walk, static-type refusal ladder, and import parsing all follow the contract doc exactly — including the gotchas (ambiguity never stops the chain; identifier TypeUsage with receiver still runs Import/StaticType/Global; C# dotted namespace flattening; scope-walk stop counts only kind-compatible candidates).

**Approach:** Test-drive from the contract: each tier gets tests for its resolve, refuse, and ambiguity cases; the driver gets chain-order, first-ambiguous, skip-vs-attempt (`no_context` vs `missing`), and confidence-min tests. The three parity-ladder bugs from the spike findings (span locate, dotted-namespace flattening, kind-filtered scope stop) each get a dedicated regression test. Keep allocation low on hot paths (candidate enumeration) but do not micro-optimize past clarity — the cache layer owns performance.

**Acceptance criteria:**
- [x] Every rule and gotcha in `resolution-policy-v6.md` has a covering unit test; all pass
- [x] `Miller.Core` still references no I/O packages
- [x] Worker-scope verification passes and the verified diff is handed to the lead (parallel-lead-commit)

### Task 2: Remove the dead-code candidates feature

**Files:**
- Delete: `src/Miller.Core/DeadCode/DeadCodeCandidates.cs`, `src/Miller.Core/DeadCode/DeadCodeRows.cs`, `src/Miller.Indexing/DeadCodeCandidateReader.cs`, `tests/Miller.Tests/.../DeadCodeCandidatesTests.cs`, `DeadCodeCandidateReaderTests.cs`, `DeadCodeCandidatesScaleTests.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` (`using` at :7; `candidates` verb branch :130-134; `ReferencesCandidates` + renderers :1732-1985; `CandidateSnapshotMetrics`/`SuppressionDetailJson` :2145-2177; help text :4288-4290 — KEEP `ReferencesExportLevelWarning` and the whole `references export` path), `src/Miller.Server/Cli/CliCapabilities.cs` (:107/:151/:193/:263 — drop `references_candidates` from `optional_features`), `src/Miller.Server/Tools/MetricsTool.cs` (:17/:29/:36-37/:88-99 — drop `dead_code_candidate_count`, `dead_code_suppressed_total`, and the candidates source from default snapshot metrics; `MetricHistoryStore` itself is untouched — old rows stay readable via explicit `--metric`), `src/Miller.Dashboard/.../DashboardIndexFactsReader.cs` (the two dead-code trend lines at :14-17/:31 only — resolution fields at :388/:392 belong to Task 5)
- Modify docs: `docs/contracts/references-candidates-v1.md` (mark RETIRED at top, keep file), `docs/contracts/cli-eros-v1.md` (:72-73/:291/:322/:546-551 + a compatibility note that `optional_features.references_candidates` is gone), `docs/contracts/references-export-v1.md` (:9-12), `docs/contracts/metrics-history-v1.md` (:37/:45/:48/:113/:131-133/:155-156 + note: historical dead-code rows remain readable), `docs/cli.md` (:111/:119/:154/:255-260), `docs/README.md` (:132), `TODO.md` (:54), `CLAUDE.md` (dead-code sentences in the 1.0-boundary section, roughly :37-45) then `scripts/sync-agents.sh`
- Test edits: `CliDispatchTests.cs` (:4581-4840 region — delete candidates tests, keep/rename `ReferencesExport_StillRoutesToJsonlExport`), `ReportToolTests.cs` (:164-231), `MetricsToolTests.cs` (:712-735), `DashboardRegistryReadTests.cs` (:2444-2491), `MetricHistoryStoreTests.cs` (:573/:578), `StoreResolutionReaderTests.cs` (:39-46 dead-code lines only), `LiveReferenceResolutionScaleTests.cs` (:110-111 — it reads `DeadCodeCandidateReader` only for `ReferenceResolutionVersion`; replace with a direct read)

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: a tree with no `references candidates` surface. Eros-facing wire change: `capabilities --json` no longer lists `references_candidates` (documented in cli-eros-v1.md). `miller report` and dashboard render without dead-code trend counts.

**Contract inputs:** User decision 2026-08-18 (design doc §5): the feature is REMOVED, not maintained. No suppression persistence exists; no MCP tool exposes it.

**File ownership:** as listed in the contract table (disjoint from Task 1's created paths; touches `DashboardIndexFactsReader.cs` usings/trend lines only).

**Serialization required:** No

**Dependency reason:** None - safe parallel batch. Disjoint from Task 1's created paths.

**What to build:** A clean removal. Delete the readers/renderers/metrics, retire the contract, update every doc that names the feature, and keep everything adjacent (`references export`, metric history of other metrics, report tool) working unchanged.

**Approach:** Delete whole files first, then chase compile errors — the inventory above is complete per a full-tree sweep, but trust the compiler over the line numbers. Docs: retired-contract header states the removal date, the user decision, and that recorded history rows remain readable.

**Acceptance criteria:**
- [x] `search`/grep for `DeadCode`, `dead_code`, `references_candidates`, `candidates` (CLI verb) finds no live code references — only the retired contract, release-note history, and historical findings docs
- [x] `references export` still routes and its test passes
- [x] Fast suite green; `cmp -s CLAUDE.md AGENTS.md` passes
- [x] Worker-scope verification passes and the verified diff is handed to the lead (parallel-lead-commit)

### Task 3: RevisionFactCache in Miller.Indexing

**Files:**
- Create: `src/Miller.Indexing/Resolution/RevisionFactCache.cs` (implements `IResolutionFacts`), `RevisionFactCacheLoader.cs` (SQL → interned arrays), `PropagationIndex.cs`, `IdentifierSiteReader.cs` (per-query streaming), `RevisionFactCacheStore.cs` (process-wide cache keyed by index identity)
- Modify: `src/Miller.Server/Hosting/MillerServiceRegistration.cs` (register the store as a singleton)
- Test: `tests/Miller.Tests/Indexing/Resolution/` — loader, interning, invalidation, and propagation tests against hand-built store-schema SQLite fixtures (reuse the schema helpers in `FamilyStoreReadSessionTests.cs`)

**Interfaces:**
- Consumes: Task 1's `IResolutionFacts`, `FactSymbol`, `ImportBinding`, `PropagationLocator`. Store schema: `symbols`, `type_facts`, `identifiers`, `pending_relationships`, `relationships`, `manifest_entries` under the pinned (view, generation) with status `indexed`/`failed_preserved` — the same visibility join `FamilyStoreReadSession` uses today.
- Produces (Task 4 compiles against these):

```csharp
namespace Miller.Indexing.Resolution;

internal sealed class RevisionFactCache : IResolutionFacts
{
    // identity = the workspace index identity Task 4's readers already carry
    internal static RevisionFactCache Load(SqliteConnection storeRead, StoreVisibility visibility);
    internal RevisionFactCache Advance(SqliteConnection storeRead, StoreVisibility newVisibility); // file-local patch
    internal PropagationIndex Propagation { get; }
}

internal sealed class PropagationIndex
{
    // located mapping only: identifier -> (pending row | relationship row); outcomes are computed per query
    internal bool TryGetOverride(long versionId, long identifierRowId, out PropagationSource source);
}

internal static class IdentifierSiteReader
{
    internal static IEnumerable<IdentifierSite> SitesNamed(SqliteConnection storeRead, StoreVisibility v, string name);
    internal static IEnumerable<IdentifierSite> SitesWithinSymbols(SqliteConnection storeRead, StoreVisibility v, IReadOnlyList<string> containingSymbolIds);
}

internal sealed class RevisionFactCacheStore // DI singleton
{
    // Two-level key: a STABLE workspace/view scope plus a CHANGING revision identity.
    // Atomic per scope: a new identity Advances from the previous cache (never a second
    // full load while one exists), replaces it, and EVICTS the old one — exactly one
    // current revision per scope (mirror SupplementalEdgeCache's eviction,
    // WorkspaceIndexProvider.cs:1607-1640). A process-wide byte budget bounds total
    // resident size across workspaces; exceeding it evicts least-recently-used scopes.
    internal RevisionFactCache GetOrAdvance(
        string workspaceScope, string revisionIdentity,
        Func<SqliteConnection> openRead, StoreVisibility visibility);
}
```

`IdentifierSite` carries version, row id, name, kind string, receiver/qualifier (from metadata_json), containing symbol id, confidence, span. Exact signatures may take the session's existing connection/visibility types — follow what `FamilyStoreReadSession` actually passes around; the shape above is the contract, not the letter.

**Contract inputs:** Memory budget: ≤350 MB idle at aspnetcore scale (553k symbols) — intern strings, pack symbols into arrays indexed by ordinal, key by-name lookups through an interned-name dictionary of int lists. Identifiers (2.15 M rows at that scale) are NEVER resident; `identifiers(name, kind, version_id)` index exists for the streaming reads. Follow the `SupplementalEdgeCache` pattern (see `WorkspaceIndexProvider.cs` around :1508 and its registration) for lifetime/keying.

**File ownership:** Create `src/Miller.Indexing/Resolution/**`, `tests/Miller.Tests/Indexing/Resolution/**`; modify `MillerServiceRegistration.cs`

**Serialization required:** Yes

**Dependency reason:** Implements Task 1's `IResolutionFacts`; must follow Batch A.

**What to build:** The fact layer: load small tables (symbols, type facts, imports with resolved module versions, pending rows, relationship spans) into interned resident structures per index identity; build the propagation LOCATION index (span → identifier, file-local by construction — outcomes are resolved lazily at query time so cross-file fact changes never invalidate it); stream identifier sites per query; patch file-locally when the identity advances (reload only versions whose `version_id` changed between the old and new manifest, drop removed versions).

Second loader, same cache: `RevisionFactCache.LoadFromArtifact(SqliteConnection artifactRead)` for the LEGACY standalone artifact schema (the `MILLER_INDEX_STORE=off` serve path). Same fact tables, different visibility model — the artifact has no `manifest_entries`; the visible set is the artifact's current file/version rows (read `ReferenceEvidenceReader.cs`'s legacy arm for the exact join it uses today). `IdentifierSiteReader` gets matching artifact-schema overloads. No `Advance` needed for the artifact loader — off-mode artifacts are replaced wholesale per export.

**Approach:** `Advance` diffs old vs new visible manifest by path → version_id; unchanged versions keep their interned rows. IMPORTANT exception to file-local invalidation: an import's `module_version` depends on MANIFEST MEMBERSHIP, not just the importing file — adding or deleting `foo.ts` changes an unchanged importer's binding (contract §imports, candidate-path rules). So the cache holds a resident path→version index, and `Advance` rebuilds that index and recomputes `module_version` for EVERY import binding whenever manifest membership changed (imports are a small resident set; this is cheap and correct), while symbols/type facts/propagation stay file-local. Cover with add/remove-file and extension-precedence tests. Propagation index stores only located (identifier ← pending/relationship row) links; Task 4 resolves pendings on demand. Test invalidation with a fixture that advances one file and asserts other versions' array segments are reused (reference-equal or counter-based proof).

**Acceptance criteria:**
- [x] Cache answers match direct SQL on a fixture store for every `IResolutionFacts` member
- [x] Artifact-schema loader answers match direct SQL on a legacy-artifact fixture
- [x] Advancing one file reloads only that version's facts and propagation entries; adding/removing a file recomputes import `module_version` bindings corpus-wide
- [x] `GetOrAdvance` keeps exactly one current revision per scope and enforces the process byte budget (eviction tested)
- [x] Identifier rows are provably not resident (no identifier collection field on the cache)
- [x] EARLY MEMORY CHECKPOINT (hard gate before Task 4 starts): load the aspnetcore-scale snapshot (`/tmp/qtr-aspnet-snapshot/`, 553k symbols) through the real loader and record TOTAL process memory after load + a query pass — the budget is whole-host idle ≤350 MB / peak ≤600 MB (`docs/plans/2026-08-13-miller-performance-recovery-plan.md`), not cache-only. A miss is a stop-and-report, not a note
- [x] Worker-scope verification passes; worker commits owned files (serial-worker-commit)

### Task 4: Swap the read seams to the resolver; delete resolution-state gating

**Files:**
- Create: `src/Miller.Indexing/Reads/QueryTimeResolutionReader.cs`
- Modify — store read path: `src/Miller.Indexing/Reads/FamilyStoreReadSession.cs` (`ReadResolutionEdges` :219 and its `resolution_state != "exact"` early-return; the resolution-state-gated `IFamilyGraphUnresolvedNameReader.ReadUnresolvedNameEdges` :271-313; `AttachValidatedResolutionBase` :1257; `CreateResolutionViews` :1389 incl. the `WHERE 0` empty-view arm :1432-1446; every resolution TEMP-view SQL constant; `ValidatedResolutionBases` plumbing), `src/Miller.Indexing/SqliteSymbolGraphIndex.cs` (consumer wiring :58-60, edge consumption :412-437 — semantics unchanged; ALL legacy raw-SQL resolution joins — sweep the whole file for `identifier_resolutions`/`pending_resolutions`, known clusters around :604-668 and :778-921, and swap each to the resolver)
- Modify — evidence + export: `src/Miller.Indexing/ReferenceEvidenceReader.FamilyStore.cs` (exact arms :209-290/:359-459 swap to resolver output; name-fallback arms :339/:533 keep serving identifiers whose outcome is not Resolved; unresolved pendings keep emitting `name_fallback` rows per the parity matrix), `src/Miller.Indexing/ReferenceEvidenceReader.cs` (the LEGACY standalone-artifact arm — resolution SQL at :485/:539/:566/:623/:924-1031, the required-tables probe at :41, AND the family-mode dispatch `IsFamilyStoreResolutionProjection` (~:60-74), which detects family mode by probing the ATTACHED RESOLUTION BASE and dies with it — replace the probe with an explicit mode signal carried by the session/handle, and make the legacy arm resolve over Task 3's artifact loader), `src/Miller.Indexing/ReferenceExportReader.cs` (:68-115 — the `references export` SQL reads `identifier_resolutions`/`pending_resolutions` directly; recompute its rows through the resolver PRESERVING the documented `docs/contracts/references-export-v2.md` schema: labels, NULL-tier fallback rows, ordering, confidence rules)
- Modify — graph load + gates: `src/Miller.Indexing/SymbolGraphReader.cs` (:98-141 reads `pending_resolutions`/`identifier_resolutions` into the in-memory holder graph, via `RepositoryIndexLoader.cs` :77-82 — this load path now runs a full resolver sweep at load time instead; spike cost 0.8 s at Miller scale, 5.0 s at aspnetcore scale, acceptable because this path serves the in-memory/off-mode index), `src/Miller.Indexing/JulieSchemaGate.cs` (tolerate legacy artifacts WITHOUT resolution tables/metadata — Plan B's julie stops writing them into exports), `src/Miller.Indexing/WorkspaceIndexProvider.cs` (thread the fact cache to every session construction site; cache-key interpolation of the resolution stamp at :1508 — see note), `src/Miller.Server/Tools/IndexLevelGuard.cs` (delete `ResolutionLayerConverging` :40-55), `TraceTool.cs` :137-147, `ImpactTool.cs` :128-129, `ContextTool.cs` :159-160, `InspectTool.cs` :82-92, `EditService.cs` :1111-1118 (rename refusal — renames now always proceed on the resolver's answers), `CliDispatch.cs` (the `references export` resolution-converging warning block near :1726-1730 only)
- Wiring (explicit, because `WorkspaceReadSessionFactory` is static and evidence entry points take raw session/connection args): the resolver service rides the index holder/handle that already owns `SupplementalEdgeCache`, and every construction path that opens a read session must hand it through — enumerate and update ALL of: bootstrap, freshness/refresh, CLI dispatch reads, cross-workspace provider, graph index construction, evidence reads, export. Grep for `WorkspaceReadSessionFactory` and `ReferenceEvidenceReader.` call sites; the enumeration is part of the task, not optional.
- Test: rewrite `StoreResolutionReaderTests.cs` against the new reader; update `FamilyStoreReadSessionTests.cs` (:1103/:1554/:1773 resolution-view cases become resolver-path cases); delete `tests/.../ResolutionLayerGuardTests.cs`; update tool tests that assert degraded/refusal messages; golden serialized-output parity tests PER SURFACE (graph edges, evidence bundle, export rows) with expected tuples captured from the retired SQL on the shared fixture; a family-mode evidence test with NO attached base (the new dispatch signal)

**Interfaces:**
- Consumes: Task 1's `QueryTimeResolver`/`ResolutionInput`/`PropagationLocator`; Task 3's `RevisionFactCache`, `PropagationIndex`, `IdentifierSiteReader`, `RevisionFactCacheStore`.
- Produces: `QueryTimeResolutionReader : IFamilyGraphResolutionReader` — same `FamilyGraphResolutionEdge(CurrentId, FromId, ToId, Kind, Confidence, Source)` tuples, same `Kind`/`Source` strings and confidence values the resolution views produced (graph-surface labels: `identifier_target`/`identifier_name`, per the parity matrix). Evidence and export readers emit THEIR OWN surface's labels (`identifier_resolution`/`name_fallback`/`pending_resolution`/`relationship`, `MIN(confidence, 0.5)` fallback) — never the graph labels; the Global Constraints parity matrix governs each surface. Tools answer whenever the store view is bound; there is no "resolution converging" degraded state anywhere.

**Contract inputs:** Global parity constraint (exact strings/confidences — read the outgoing SQL first and copy its literals). Edge derivation rules: contract doc §"How outcomes become reference edges". Direction semantics: outgoing = identifiers/pendings WITHIN the candidate symbols (`SitesWithinSymbols` + resolved pendings from the cache by `from_symbol`); incoming = stream sites named each candidate's name (`SitesNamed`) plus pendings by terminal name, keep those whose resolved target is the candidate.

**File ownership:** as listed in the contract table.

**Serialization required:** Yes

**Dependency reason:** Consumes Task 3's cache and Task 1's resolver.

**What to build:** The swap. Both store read seams compute answers through the resolver; the base ATTACH, resolution TEMP views, validated-base bookkeeping, and every `ResolutionState`-keyed refusal/degradation in the tools are deleted. Propagation overrides win before the identifier chain runs (contract §Propagation): check `PropagationIndex.TryGetOverride` per site; a pending override's outcome comes from resolving that pending row on demand (memoize per query).

**Approach:** Write the parity test FIRST: a fixture store built with the existing test schema helpers, populated so every tier/reason/fallback arm fires, asserting the new reader's edge set equals the old view SQL's edge set on the same fixture (run the old SQL in the test against the fixture's base tables before deleting it from production code — the test keeps a private copy of the expected tuples, not the old production SQL). `StoreVisibility.ResolutionState` may keep existing as a read field this task ignores; Task 5 removes its production surfacing. Note on `WorkspaceIndexProvider` :1508: leave the stamp interpolation intact — Task 5 owns the stamp decision.

**Acceptance criteria:**
- [x] Golden per-surface parity tests pass: identical serialized output (labels, tiers, confidences, ordering) vs the retired SQL for graph edges, evidence bundles, AND export rows — family-store arm and legacy-artifact arm both
- [x] No production code references `identifier_resolutions`, `pending_resolutions`, `resolution_identifier_deltas`, resolution bases, or `AttachValidatedResolutionBase` — verified by tree-wide grep, not just the plan's file list
- [x] Family-mode evidence dispatch works with no resolution base attached; `references export` produces contract-identical rows; the in-memory holder graph builds via the resolver sweep
- [x] All five tool degraded paths and the rename refusal are gone; tools answer on a bound view regardless of `views.resolution_state`
- [x] Worker-scope verification passes; worker commits owned files (serial-worker-commit)

### Task 5: Stop submitting resolves; remove the machinery and status surface

**Files:**
- Modify: `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs` (resolve submission in `ApplyIncrementalFileDelta` :568-583 and `Submit` :712-738; delete `ShouldSubmitResolve` :1034, `ResolutionAlreadyExact` :1031, `_tryCarryExact` :120/:167, `SubmitResolveRequest` :791-809, `_replayedResolveRequestIds` :116/:1009, `ResolveFingerprint`/`ResolveToken` :1017-1029, the `resolveAfter` parameter :698 and its threading through `Update` :400 / `Delete` :422 / `Scan` :458), callers `IndexerCore.cs` :392-394, `IndexerService.cs` :928/:1011/:1280, `IndexBootstrapService.cs` :1058, `CrossWorkspaceRefreshService.cs` :704; `JulieStoreClient` resolve-request surface (remove Miller-side submission API; if Task 6's parity fixture needs to drive `julie-extract resolve`, it does so via the CLI/subprocess in the test, not via a kept production API — check `JulieStoreClientTests.cs` :345-358), status surface `WorkspaceRender.cs` :59-62/:678/:713-719, `WorkspaceFactsAssembler.cs` :32/:47-50/:159-169 (the only producer of the `resolving` freshness value), `WorkspaceHealthFacts.cs` :122-124, `DashboardIndexFactsReader.cs` :388/:392, `src/Miller.Dashboard/.../WorkspaceDetailPanel.razor` :131, `StoreSidecarStamp.cs` :21/:58/:78 (see approach), `docs/contracts/workspace-status-v1.md` :59/:105 (drop the `resolving` freshness value — value removal, not field removal)
- Delete: `src/Miller.Indexing/Store/StoreResolutionCarry.cs`, `tests/.../StoreResolutionCarryTests.cs`
- Test: update `StoreWorkspaceCoordinatorTests.cs` (resolve-related cases at :334/:480/:500/:568/:727/:1308 and neighbors), `JulieStoreClientTests.cs` :345-358, `IndexerPhaseRecordTests.cs` :64, `WorkspaceRenderTests.cs` :212-215, `WorkspaceFactsAssemblerTests.cs` :706-801, `StoreSidecarStampTests.cs` :53/:194/:313

**Interfaces:**
- Consumes: Task 4's completed swap (tools no longer read resolution state — this is the precondition for cutting submission).
- Produces: saves and scans end at import/publish; no resolve request is ever constructed. `workspace status`/`health` JSON no longer emits resolution-layer fields; freshness never reports `resolving`. Scan intents, backoff journal, and leadership are untouched.

**Contract inputs:** `views.resolution_state` in the store may hold any value from julie 2.33.7 and MUST be ignored, not asserted on. Known interim regression (phase map): after this task, `StoreRollbackExporter` / `MILLER_INDEX_STORE=off` export fails on 2.33.7's exact-state gate until the Phase 3 pin bump — leave the export path's own code alone, do not submit resolves to appease it, and do not mask its error. `docs/contracts/cli-eros-v1.md` has no resolution_state references (verified) — only `workspace-status-v1.md` changes. Sidecar stamp decision (approved in this plan): KEEP the `ResolutionStamp` field/column, write the constant `"retired"`, and NORMALIZE legacy values ON READ — any persisted resolution-stamp value compares equal to `"retired"`. The empty-delta fast-forward CANNOT absorb this change (its `TryFastForwardEmptyDelta` rejects `previous.StoreLogSequence >= expected` — a stamp-only change at an unchanged sequence would force a full sidecar rebuild), so read-normalization is the mechanism, not fast-forward. Cover with tests reading a pre-change stamp at an UNCHANGED store sequence for the search, content, and vector sidecars. Leave a `razorback:` debt comment noting the column can be dropped in a future stamp-schema bump. Report DTO shim: `JulieStoreClient` (~:600-629, :795-801) currently REQUIRES and validates the producer's resolution report object — relax to tolerate-and-discard (present on 2.33.7, absent after Plan B); `StoreReports.cs` resolution result types stay as parse-only compatibility DTOs with a debt comment; Phase 3 removes them.

**File ownership:** as listed in the contract table.

**Serialization required:** Yes

**Dependency reason:** Tools must already answer without resolution state (Task 4) before submission stops.

**What to build:** The producer-side cut. Remove every resolve-request construction/submission/replay/carry path and the whole resolution status surface. The save pipeline becomes extract → import → publish → sidecar converge.

**Approach:** Follow the compiler after deleting `SubmitResolveRequest` and the `resolveAfter` parameter — the caller list above is the complete fan-in per a full-tree sweep. In status JSON, REMOVE resolution fields rather than emitting nulls (contract doc updated in the same change). Verify with a live smoke: `workspace status --json` on this worktree shows no resolution keys and freshness reaches `fresh` after a touch-save.

**Acceptance criteria:**
- [x] Tree-wide search for `Resolve` in the coordinator/indexer namespaces finds no request construction; no subprocess argv contains `resolve`
- [x] `workspace status --json` / `health --json` emit no resolution-layer fields; `workspace-status-v1.md` matches
- [x] Existing-workspace sidecar stamps do NOT trigger a rebuild — read-normalization tested with a pre-change stamp at an unchanged store sequence, for search/content/vector sidecars
- [x] Store-client report parsing tolerates both a present (2.33.7) and an absent (Plan B) resolution report object
- [x] Worker-scope verification passes; worker commits owned files (serial-worker-commit)

### Task 6: Gates, scale parity, and doc sync

**Files:**
- Modify: `tests/Miller.Tests/.../LiveReferenceResolutionScaleTests.cs` — repurpose as the live parity gate; `CLAUDE.md` (resolution architecture: replace materialized-resolution language — search-sidecar section stays; add one paragraph on query-time resolution + the fact cache; state julie's resolution artifacts are ignored until Phase 2 removes them) then `scripts/sync-agents.sh`; `docs/README.md` (map entries for the new contract + findings); `docs/plans/2026-08-18-query-time-resolution-integration-design.md` (status line → phase 1 executed)
- Create: `docs/findings/2026-08-18-query-time-resolution-phase1-gates.md`
- Test: the Scale parity test itself (`[Trait("Category","Scale")]`, `ScaleTestSupport.RequireJulieServer()`)

**Interfaces:**
- Consumes: the finished system (Tasks 1–5); pinned julie-extract 2.33.7 (still ships `resolve`).
- Produces: the recorded gate evidence the release decision needs.

**Contract inputs:** Hard gates from the Verification Strategy. Ground truth for parity: drive the pinned `julie-extract` binary to extract AND resolve a real fixture repo into a store, then compare every visible identifier's query-time outcome — outcome kind, target, tier, method, AND CONFIDENCE (the spike never compared confidence exactly; this gate must) — against the stored `identifier_resolutions` (+ max-generation delta overlay), plus standalone PENDING-row outcomes, plus per-surface serialized output (graph edge tuples, evidence source labels, export rows, ordering/dedup) on the same fixture. Divergences where the store under-resolved (`missing` in store, resolved by policy — the aspnetcore pattern from the spike findings) are acceptable when individually verified; any other divergence fails the gate.
- Local-only measurements (not CI): full-corpus parity sweeps against BOTH frozen snapshots — `/tmp/qtr-spike-snapshot/` (Miller scale) and `/tmp/qtr-aspnet-snapshot/` (aspnetcore scale), per the approved design's gate 1 — plus the aspnetcore p95/whole-host-memory gates and a live save-to-answer timing on this worktree's workspace.

**File ownership:** as listed in the contract table.

**Serialization required:** Yes

**Dependency reason:** Measures the finished system.

**What to build:** The proof. A Scale-suite parity test that owns its julie invocation (nothing in production submits resolves), plus the local gate measurements, recorded in the findings doc with commands, numbers, and pass/fail per gate. Future-proofing: the parity test probes whether the pinned binary supports `resolve` and SKIPS (with a reason) when it does not — after the Phase 3 pin bump to a resolve-less julie, the test retires gracefully instead of failing the Scale suite.

**Approach:** Parity test scope: the existing Scale fixture repo(s) used by `LiveReferenceResolutionScaleTests` — full-corpus sweep, not sampling. For the aspnetcore-scale gates, write a small measurement path reachable from test code or a throwaway harness under `tests/` (Scale-tagged, skipped when the snapshot directory is absent) — measure warm p95 over the spike's name mix (top-fan-out + random), and resident bytes after load + one query pass (`GC.GetTotalMemory` + RSS). Record report-only numbers too (cold load, full sweep). Then run the branch gate: Release build + `scripts/test.sh all`.

**Acceptance criteria:**
- [ ] Scale parity test green with zero unexplained divergences
- [ ] Findings doc records all four hard gates with measured numbers; all pass (a failed gate is a stop-and-report, not a silent accept)
- [ ] `cmp -s CLAUDE.md AGENTS.md` passes; docs map updated
- [ ] Branch gate green: Release build 0/0, `scripts/test.sh all` green
- [ ] Worker-scope verification passes; worker commits owned files (serial-worker-commit)

---

## Out of scope (tracked, not lost)

- **Plan B (julie-extractors):** the resolution write-path removal and the language-classification fix — see the cross-repo phase map above; sibling plan lives in that repo.
- **Phase 3 / Release:** pin bump, version bump, release notes (must announce the dead-code removal and the capabilities wire change), publish — all need explicit user approval.
- **Store-side cleanup of existing resolution bases on disk** (julie owns those files; Plan B's released binary reaps them).
