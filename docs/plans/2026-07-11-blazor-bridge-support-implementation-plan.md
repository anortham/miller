# Blazor Bridge Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Blazor navigation, component references, and DI registrations participate in Miller trace/impact: `NavigateTo("/x")` → `@page` component (`NavigatesTo`), shared component edit → consuming pages, `AddScoped<IFoo, Foo>()` → `Foo`.

**Architecture:** A `blazor` `FileRouteBridgeProvider` descriptor (verb-blind `NavigatesTo` bridge, same seam as NextJs/Nuxt/Vue/React in `src/Miller.Core/Graph/FileRouteBridgeProvider.cs`) pairs `razor.route_reference.v1` with `razor.page_directive.v1`. The adapter preserves the raw ASP.NET brace template and the shared matcher learns brace optional-single and catch-all semantics without changing Next.js colon semantics. A focused `BlazorComponentGraphReader` resolves `blazor.component_reference.v1` facts against Razor component symbol metadata and emits `uses` dependency edges; DI edges come from loading resolved pending relationships that `SymbolGraphReader` ignores today.

**Tech Stack:** C#/.NET, xunit, bundled julie-extract binary (pin).

**Architecture Quality:** Approved shape per the umbrella design (`/Users/murphy/source/eros/docs/plans/2026-07-11-dotnet-blazor-stack-support-design.md`): reuse the file-route provider seam; do NOT touch `DotnetWebBridgeProvider` for navigation (page routes never mix with API endpoints). Rejected: Blazor arm in the HTTP provider (wrong edge kind); reinterpreting `:name?` (it means optional catch-all for Next.js today, `src/Miller.Core/Resolver/FileRouteMatcher.cs:110`). Risk: medium (fact plumbing across the versioned julie pin).

## Global Constraints

- Julie pin: bump `PinnedJulieExtractVersion` (`src/Miller.Indexing/MillerExtractContract.cs:26`) and `scripts/julie-pins.json` to released `julie-extract` `2.13.0`; update exact-version assertions in `MillerExtractContractTests` and `CliDispatchTests`; restore the released binary before scale/build gates. SQLite schema remains `4`, extract contract `3`, and report schema `3`.
- `dotnet build Miller.slnx -c Release` — warnings are errors.
- Fast suite must stay <10s; live-extract tests are `Category=Scale` and must skip (not fail) without `.tools/julie-extract`.
- A bare descriptor silently self-skips: every new fact id must be wired through ALL gates — `BridgeStructuralPatterns.BridgeFactPatternIds` (`src/Miller.Core/Graph/BridgeStructuralPatterns.cs:64-109`), both `StructuralRouteFactAdapter` allowlist gates (route-reference read ~`:31`, route read ~`:271`), both default-provider lists (`src/Miller.Indexing/BridgeProviderSelection.cs:8`, `src/Miller.Core/Graph/BridgeGraphBuilder.cs:23`), and the config-selection switch (`BridgeProviderSelection.cs:73`).
- Razor optional route segments are single-segment (`{id?}`); Miller's `:name?` optional catch-all semantics must NOT be applied to them.
- New capability feature string only if the tool contract grows (follow `impact_test_role_evidence` precedent in `src/Miller.Server/Cli/CliCapabilities.cs`).

## Verification Strategy

**Project source of truth:** `AGENTS.md` (test tiers, wrapper scripts, Release build gate).

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<TestClass>"` on the touched test class (fast tier).

**Worker ceiling:** `scripts/test.sh` (fast suite).

**Worker gate invariant:** per task — provider activates on synthetic Blazor facts and self-skips without them; `{id?}` matches zero-or-one segment and NOT multiple; component-reference facts produce impact reachability; DI registration reaches implementation via trace.

**Lead affected-change scope:** `scripts/test.sh` after each batch.

**Branch gate:** `scripts/test.sh all` (fast + scale, requires `.tools/julie-extract` at the new pin) + `dotnet build Miller.slnx -c Release`.

**Escalation triggers:** touching `SqliteBridgeReader`/`SymbolGraphReader`/pin → scale suite mandatory; changes to `FileRouteMatcher` → run the full bridge test suite including Next.js/Nuxt cases (semantic collision risk).

**Assigned verification failure:** Workers stop and report when assigned verification fails.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, timestamp per task.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Pattern ids + reader whitelist | None - serial | Modify: `src/Miller.Core/Graph/BridgeStructuralPatterns.cs`; Test: `tests/Miller.Tests/Indexing/SqliteBridgeReaderTests.cs` | Yes | Tasks 2–4 consume the new pattern constants. |
| Task 2: Blazor route adapter + matcher semantics | Batch A | Modify: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs`, `src/Miller.Core/Resolver/FileRouteMatcher.cs`; Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs` (new Blazor region) | No | None - safe parallel batch (no file overlap with Task 4). |
| Task 3: Blazor provider descriptor + registration | None - serial (after Task 2) | Modify: `src/Miller.Core/Graph/FileRouteBridgeProvider.cs`, `src/Miller.Core/Graph/BridgeGraphBuilder.cs:23`, `src/Miller.Indexing/BridgeProviderSelection.cs`; Test: new file `tests/Miller.Tests/Graph/BlazorBridgeProviderTests.cs` | Yes | Activation tests exercise Task 2's adapter arms; no stubs allowed, so the arms must exist first. |
| Task 4: Component-reference impact edges | Batch A | Create: `src/Miller.Indexing/BlazorComponentGraphReader.cs`; Modify: `src/Miller.Indexing/RepositoryIndexLoader.cs`; Test: new file `tests/Miller.Tests/Indexing/BlazorComponentGraphReaderTests.cs` | No | None - safe parallel batch (no file overlap with Task 2). |
| Task 5: DI edges from resolved pending relationships | None - serial (after Task 4) | Modify: `src/Miller.Indexing/SymbolGraphReader.cs:67`; Test: `tests/Miller.Tests/Indexing/SymbolGraphReaderTests.cs` or nearest existing reader test file | Yes | Shares graph-reader surface with Task 4's consumption path; land after Task 4 review. |
| Task 6: Pin bump + live fixture + local release readiness | None - serial | Modify: `src/Miller.Indexing/MillerExtractContract.cs`, `scripts/julie-pins.json`, `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`; Test: `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs` (new Blazor fixture) | Yes | Requires released julie 2.13.0 and Tasks 1–5 merged; push/publication remains approval-gated. |

Batch A = {Task 2, Task 4} runs `parallel-lead-commit`; serial tasks (1, 3, 5, 6) run `serial-worker-commit`.

---

### Task 1: Pattern ids and reader whitelist

**Files:**
- Modify: `src/Miller.Core/Graph/BridgeStructuralPatterns.cs`
- Test: `tests/Miller.Tests/Indexing/SqliteBridgeReaderTests.cs`

**Interfaces:**
- Produces: `public const string RazorRouteReference = "razor.route_reference.v1"`, `RazorPageDirective = "razor.page_directive.v1"`, `BlazorComponentReference = "blazor.component_reference.v1"`; all three in `BridgeFactPatternIds` so `SqliteBridgeReader.ReadStructuralFacts` (`src/Miller.Indexing/SqliteBridgeReader.cs:239`) loads them.

**What to build:** Constants + whitelist entries + reader test proving the three fact ids round-trip from a seeded sqlite fixture (follow existing whitelist tests in `SqliteBridgeReaderTests.cs`).

**Acceptance criteria:**
- [ ] Red first: reader test seeding the three fact ids fails before the whitelist change
- [ ] All three fact ids load; unrelated ids still excluded

### Task 2: Blazor route adapter and matcher semantics

**Files:**
- Modify: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs` (route-reference gate ~`:31`, route gate ~`:271`)
- Modify: `src/Miller.Core/Resolver/FileRouteMatcher.cs` (optional single-segment support)
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`

**Interfaces:**
- Consumes: Task 1 constants; released 2.13.0 fact payloads — `razor.route_reference.v1` uses `target_path`, `source_kind`, `route_source`, and `framework`; `razor.page_directive.v1` carries the raw ASP.NET brace template in `route`/`route_template` plus `route_parameters` metadata.
- Produces: `TryReadRouteReference` accepts `razor.route_reference.v1`; `TryReadFileRoute` accepts `razor.page_directive.v1` and returns its raw `route_template`; `FileRouteMatcher` treats `{id?}` as zero-or-one segment and `{*path}` as a non-empty catch-all while leaving `:name?`/`[[...slug]]` behavior unchanged.

**What to build:** Adapter arms reading `target_path` for route references and raw `route_template` for page directives. Extend `FileRouteMatcher` directly for ASP.NET brace optional/catch-all segments; do not add a multi-route adapter API and do not rewrite brace markers into colon encodings. Preserve all existing Next.js/Nuxt semantics.

**Acceptance criteria:**
- [ ] `/orders/{orderId?}` matches `/orders` and `/orders/42`, and does NOT match `/orders/a/b` (negative multi-segment assertion)
- [ ] `/files/{*path}` matches `/files/a/b/c`
- [ ] Existing Next.js `[[...slug]]`/`:name?` tests unchanged and green

### Task 3: Blazor provider descriptor and registration

**Files:**
- Modify: `src/Miller.Core/Graph/FileRouteBridgeProvider.cs` (add `public static FileRouteBridgeProvider Blazor { get; }` descriptor: ProviderId `"blazor"`, DisplayName `"Blazor"`, RouteReferencePattern `RazorRouteReference`, FileRoutePattern `RazorPageDirective`)
- Modify: `src/Miller.Core/Graph/BridgeGraphBuilder.cs:23` (default provider list)
- Modify: `src/Miller.Indexing/BridgeProviderSelection.cs` (`DefaultProviders` at `:8`, config switch at `:73`)
- Test: `tests/Miller.Tests/Graph/BlazorBridgeProviderTests.cs`

**Interfaces:**
- Consumes: Task 1 constants; Task 2 adapter arms.
- Produces: trace `mode=bridge` provider id `blazor` emitting `BridgeKind.NavigatesTo` chains.

**What to build:** Descriptor + all three registration points (the config switch silently skips unknown ids — that is the self-skip trap the design calls out).

**Acceptance criteria:**
- [ ] Synthetic facts: `NavigateTo("/edr/form")` reference + `@page "/edr/form"` route bridge to a `NavigatesTo` edge
- [ ] Provider self-skips (no activation, no errors) on a workspace with zero razor facts
- [ ] Provider selectable by config name `blazor` and present in both default lists

### Task 4: Component references in impact

**Files:**
- Create: `src/Miller.Indexing/BlazorComponentGraphReader.cs`
- Modify: `src/Miller.Indexing/RepositoryIndexLoader.cs`
- Test: `tests/Miller.Tests/Indexing/BlazorComponentGraphReaderTests.cs`

**Interfaces:**
- Consumes: the `SqliteBridgeReader` projection of `blazor.component_reference.v1` with `tag`, `containing_component`, `namespace_context`, and `generic_arguments`; released 2.13.0 emits no `external` key. Razor component symbols are `kind=class` with `metadata_json.type="razor-component"` and `metadata_json.qualifiedName`.
- Produces: `BlazorComponentGraphReader.Read(string dbPath, IReadOnlyList<StructuralFactRecord> facts)` returning `GraphEdge` rows with kind `uses`; reverse-reachability from `SharedWidget` reaches `PageA` when `PageA.razor` references `<SharedWidget />`.

**What to build:** Load only Razor component symbol ids, paths, names, and `qualifiedName` metadata from the artifact. Resolve the source by fact path plus `containing_component`. Resolve a fully-qualified tag by exact `qualifiedName`; resolve a simple tag when it has one workspace component candidate, or when exactly one candidate's qualified name equals `<namespace_context entry>.<tag>`. Add source→target `uses` edges. Missing, external, self, or still-ambiguous targets are skipped. Reorder `RepositoryIndexLoader.Load` so bridge facts are read once before dependency-graph construction, then append these edges to the ordinary relationship/identifier edges before building the index.

**Acceptance criteria:**
- [ ] `impact target=SharedWidget` (synthetic two-file fixture) lists the consuming page at hop 1
- [ ] Unmatched external (FluentUI) tags produce no edges without relying on a nonexistent `external` flag
- [ ] Ambiguous tag with two same-name components resolves by namespace context or is skipped with no wrong edge

### Task 5: DI edges from resolved pending relationships

**Files:**
- Modify: `src/Miller.Indexing/SymbolGraphReader.cs:67`
- Test: nearest existing reader test file (or new `tests/Miller.Tests/Indexing/SymbolGraphReaderDiTests.cs`)

**Interfaces:**
- Consumes: julie pending `instantiates` relationships (`csharp/di_relationships.rs` emits DI registrations as pending) + `pending_resolutions` rows that carry resolved targets (Terraform artifact: 1,726 pending `instantiates`, several resolved, zero in graph today).
- Produces: resolved pending relationships join the dependency graph used by trace/impact.

**What to build:** Extend the graph read to include pending relationships whose resolution row identifies a target symbol. Only resolved rows — unresolved pendings stay out (no name-guess edges).

**Acceptance criteria:**
- [ ] Seeded fixture: pending `instantiates` + resolution row yields a graph edge; unresolved pending yields none
- [ ] `trace` from the containing `Program`/file-scope owner symbol reaches `Foo` with `instantiates` evidence for `AddScoped<IFoo, Foo>()`
- [ ] Graph load performance: fast suite stays <10s

### Task 6: Pin bump, live Blazor fixture, and local release readiness

**Files:**
- Modify: `src/Miller.Indexing/MillerExtractContract.cs:26`
- Modify: `scripts/julie-pins.json`
- Modify: `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- Test: `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs` (new Blazor fixture group, `Category=Scale`)

**Interfaces:**
- Consumes: released julie-extract 2.13.0 (commit `9dcb12f9fbe65f83c2114ce5d4abb3f0d2c72826`, schema 4/contract 3/report 3), Tasks 1–5.
- Produces: a locally verified Miller release candidate that Eros can floor-bump after explicit push/publication approval.

**What to build:** Update all pin/assertion surfaces to 2.13.0, run the restore script, and verify the restored binary version. Add a live fixture with a `.razor` page using `@page "/orders/{orderId?}"`, a `NavigateTo("/orders")` call, a `<SharedWidget />` reference, a `Foo.razor.cs` code-behind member, and Program.cs `AddScoped<IWidgetService, WidgetService>()`; assert through real julie-extract output: NavigatesTo chain (including the optional-segment match), component impact reachability, and `Program`→`WidgetService` DI trace with `instantiates` evidence. Do not push, tag, publish, or update public release metadata in this task.

**Acceptance criteria:**
- [ ] Scale suite green at the new pin; fixture skips (not fails) without `.tools/julie-extract`
- [ ] All three live chains assert end-to-end
- [ ] `dotnet build Miller.slnx -c Release` clean; local release-readiness state recorded for the later approval-gated Miller publish and Eros floor bump
