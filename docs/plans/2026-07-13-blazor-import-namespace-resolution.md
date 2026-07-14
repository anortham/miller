# Blazor Import Namespace Resolution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Resolve `.razor` component references with inherited `_Imports.razor` directives, project root namespaces, and folder namespaces while preserving Miller's evidence-first, fail-closed graph behavior. `_ViewImports.cshtml` remains extractor-gated because julie-extract 2.14.0 emits its directive symbols but no `blazor.component_reference.v1` facts for `.cshtml` component usage.

**Architecture:** Keep `BlazorComponentGraphReader.Read(string dbPath, IReadOnlyList<StructuralFactRecord> facts)` and `RepositoryIndexLoader.Load` stable. Add one internal `BlazorNamespaceCatalog` that reads existing Julie directive/component evidence, derives bounded .NET project namespace context from `artifact_metadata.root_path`, and returns candidate namespaces to the existing reader. Fully qualified tags and explicit component qualified names remain authoritative; same-namespace and imported-name resolution use only one unambiguous effective namespace; ambiguous or missing evidence emits no edge.

**Tech Stack:** .NET 10, C#, Microsoft.Data.Sqlite, System.Text.Json, System.Xml.Linq, julie-extract 2.14.0 SQLite artifacts, xUnit.

**Architecture Quality:** Affected modules are `BlazorComponentGraphReader`, a new internal namespace catalog, repository-index tests, and live bridge fixtures. The caller-facing interface remains `BlazorComponentGraphReader.Read`; tests prove behavior through that interface and through reverse graph traversal. Risk is medium because default namespace inference can create false dependency edges. Keep filesystem/project parsing inside one deep internal module, use `WorkspaceRelativePath.ResolveUnderRoot` for lexical containment plus catalog-local symlink rejection, reject raw Razor parsing, MSBuild execution, and new MCP surfaces, and fail closed on aliases, property expressions, visible imported root-namespace evidence, multiple projects, or ambiguous candidates. Existing ADR-0001 and ADR-0002 concern guidance delivery and dashboard registry mutations and are not reopened.

**Doubt-pass reconciliation:** The July 13 design already received an external Claude review before this execution session. The refreshed 2.14 audit preserved its private-catalog/API-stability decision but corrected four assumptions: imports-file `@namespace` values require descendant folder suffixes; the SDK default is `MSBuildProjectName.Replace(" ", "_")`; raw project XML is a bounded Miller heuristic rather than full MSBuild evaluation; and `.cshtml` reference resolution must wait for extractor facts. A live Gemini re-check was unavailable because the installed client is no longer supported, and a fresh Claude retry stopped at the programmatic spending cap; no paid retry was authorized.

**Reviewed evidence:** Microsoft documents `_Imports.razor` folder inheritance and component scope in [Blazor layouts](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/layouts?view=aspnetcore-10.0#apply-a-layout-to-a-folder-of-components), closest-import namespace bases and folder suffixes in the [Razor syntax reference](https://learn.microsoft.com/en-us/aspnet/core/mvc/views/razor?view=aspnetcore-10.0#namespace), component namespace/name binding in [ASP.NET Core Blazor components](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/?view=aspnetcore-10.0#component-name-class-name-and-namespace), and imported/conditional MSBuild properties in [Customize the build by folder](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory?view=vs-2022). The SDK source defines the [RootNamespace default](https://github.com/dotnet/sdk/blob/main/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.Sdk.props#L38-L45). A live 2.14.0 scan produced 24 symbols and four structural facts with no diagnostics: inherited imports were directive symbols but absent from fact-local `namespace_context`; `.cshtml` produced page facts but no component-reference facts.

## Global Constraints

- Miller remains a read-only deterministic consumer; Julie owns parser-backed extraction and Eros remains out of scope.
- Do not add an MCP tool, CLI verb, artifact schema, or structural-fact family.
- Use existing Julie evidence: component symbols, `type=razor-directive` symbols, component-reference facts, paths, metadata, and `artifact_metadata.root_path`; ignore `type=razor-token-directive` rows.
- Do not parse Razor source text in Miller. Parsing bounded `.csproj` XML for project-boundary/root-namespace evidence is allowed inside the private resolver.
- Resolve all artifact-relative paths through `WorkspaceRelativePath.ResolveUnderRoot` before filesystem access.
- Because that shared helper is lexical, reject symlink/reparse-point directories and project files before enumerating or reading project evidence.
- Consume only `.razor` component-reference facts and `_Imports.razor` directives. Do not simulate `.cshtml` component references or consume `_ViewImports.cshtml` until julie-extract emits a live reference fact for the documented Component Tag Helper shape.
- Explicit dotted tag names resolve only by exact qualified name.
- An explicit dotted `qualifiedName` on a component symbol is authoritative; derived project/folder names are used only when Julie's value is the simple component name.
- Accumulate applicable ancestor `@using` directives; the nearest applicable `@namespace` directive establishes the base namespace and descendant folders append namespace segments relative to that imports file.
- Include the source component's one unambiguous effective namespace so same-folder and project-root components follow documented C# name binding.
- Skip aliases, `@using static`, open generics, MSBuild property expressions, conditional/conflicting/imported root namespaces, and all ambiguous cases unless a test and official rule prove a deterministic result. Alias rejection is an explicit Miller coverage limit, not a Blazor rule.
- Never invoke MSBuild from repository loading. The nearest single-project ancestor is a bounded Miller heuristic, not an assertion of compiler evaluation.
- Preserve the default/Scale test split and the `<30s` fast-suite tripwire.
- Do not edit Julie Extractors, tree-sitter-razor, or Eros.
- Do not push, tag, publish, or release without explicit approval.

## Verification Strategy

**Project source of truth:** `AGENTS.md`, `tests/Miller.Tests/Miller.Tests.csproj`, `scripts/test.sh`, `ScaleTestSupport`, the existing Blazor reader tests, the julie-extract 2.14.0 artifact contract, and current Microsoft Razor/MSBuild documentation.

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~BlazorComponentGraphReaderTests` for pure resolution behavior.

**Worker ceiling:** Workers run focused fast tests and `scripts/test.sh`. The lead owns build, Scale, and `scripts/test.sh all` acceptance.

**Worker gate invariant:** Every asserted edge must be supported by exact tag/name or one unambiguous effective namespace; every ambiguous/missing-evidence fixture must produce no edge.

**Lead affected-change scope:** Focused Blazor reader, repository-loader, graph traversal, and workspace-relative path tests, followed by `scripts/test.sh`.

**Branch gate:** `dotnet build Miller.slnx -c Release`, `scripts/test.sh`, `scripts/test.sh scale`, then `scripts/test.sh all` at the final HEAD.

**Replay/metric evidence:** Hard gates are expected graph edges, expected no-edge cases, reverse dependents/shortest paths, successful live Julie extraction, and zero fast-suite budget violations. Bridge build timing is report-only unless the existing budget gate fails.

**Escalation triggers:** A public signature change, artifact query shape change, graph edge-kind change, unbounded filesystem scan, a change to `WorkspaceRelativePath`, or a cross-platform path failure requires repository-loader, bootstrap, CLI, workspace-provider, and Windows CI regression suites.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless this plan explicitly changes that gate.

**Verification ledger:** Record invariant, command, scope label, Miller commit SHA, Julie binary version/SHA, result, and timestamp. Reuse evidence only at the same HEAD and binary. Baseline binary proof: julie-extract 2.14.0, SHA-256 `7086cf5d50eb58c539ad993317f4bde9ad315bf5f5f1eb3ce707de34701140fd`.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Resolve inherited import namespaces | None - serial | `src/Miller.Indexing/BlazorComponentGraphReader.cs`, new `src/Miller.Indexing/BlazorNamespaceCatalog.cs`, `tests/Miller.Tests/Indexing/BlazorComponentGraphReaderTests.cs`, `tests/Miller.Tests/Indexing/JulieDbFixture.cs` | Yes | Establishes the private resolver seam and ships one green vertical slice. |
| Task 2: Derive project and folder namespaces | None - serial | `src/Miller.Indexing/BlazorNamespaceCatalog.cs`, `tests/Miller.Tests/Indexing/BlazorComponentGraphReaderTests.cs`, `tests/Miller.Tests/Indexing/BlazorNamespaceCatalogTests.cs`, `tests/Miller.Tests/Indexing/JulieDbFixture.cs` | Yes | Extends the Task 1 catalog with bounded project evidence and a faithful fixture root. |
| Task 3: Harden repository loading and failure behavior | None - serial | `src/Miller.Indexing/BlazorComponentGraphReader.cs`, `tests/Miller.Tests/Indexing/BlazorComponentGraphReaderTests.cs`, `RepositoryIndexLoaderBridgeTests.cs` | Yes | Proves the Task 1/2 integration remains safe at the repository boundary. |
| Task 4: Add live artifact and cross-platform evidence | Batch A | `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs`, path-specific tests only if required | No | Runs after Task 3 and owns the existing live Blazor fixture plus separate path files. |
| Task 5: Document support and verification | Batch A | `README.md`, `docs/README.md`, `docs/findings/2026-07-14-blazor-namespace-resolution.md`, plan checkboxes | No | Runs after Task 3 without editing implementation/test files. |
| Task 6: Run branch gates and prepare handoff | None - serial | Plan progress and approved release-prep evidence only | Yes | Requires Tasks 1-5 at one HEAD. |

### Task 1: Resolve inherited import namespaces

**Files:**
- Modify: `src/Miller.Indexing/BlazorComponentGraphReader.cs:15-214`
- Create: `src/Miller.Indexing/BlazorNamespaceCatalog.cs`
- Modify: `tests/Miller.Tests/Indexing/BlazorComponentGraphReaderTests.cs`
- Modify: `tests/Miller.Tests/Indexing/JulieDbFixture.cs` only if directive-symbol fixture helpers are missing

**Interfaces:**
- Consumes: Existing `BlazorComponentGraphReader.Read` and Julie directive/component artifact rows.
- Produces: Working ancestor-import resolution behind the unchanged public reader.

**Contract inputs:** julie-extract 2.14.0 Razor directives are `kind=import` symbols whose metadata contains `type=razor-directive`, `directiveName`, and `directiveValue`; token directives use a different type and are ignored. Component-reference facts carry only file-local `namespace_context`, not inherited imports.

**File ownership:** `BlazorComponentGraphReader.cs`, new `BlazorNamespaceCatalog.cs`, `BlazorComponentGraphReaderTests.cs`, and directive-symbol fixture helpers in `JulieDbFixture.cs`

**Serialization required:** Yes.

**Dependency reason:** Establishes the private resolver seam and ships one green vertical slice.

**Step 1: Add fixture helpers for directive evidence**

Add a helper that writes a contract-faithful symbol row rather than inventing a new table. Julie names `@using` symbols with their directive value and names `@namespace` symbols literally `@namespace`:

```csharp
private static JulieDbFixture.SymbolRow RazorDirective(
    string id,
    string path,
    string directiveName,
    string directiveValue)
{
    string symbolName = directiveName == "using"
        ? directiveValue
        : $"@{directiveName}";

    return new(
        id,
        symbolName,
        "import",
        "razor",
        path,
        $"@{directiveName} {directiveValue}",
        1,
        null)
    {
        Metadata = JsonSerializer.Serialize(new
        {
            type = "razor-directive",
            directiveName,
            directiveValue,
        }),
    };
}
```

**Step 2: Write failing import-resolution tests**

Cover through `BlazorComponentGraphReader.Read`:

- root `_Imports.razor` `@using Sample.Components` resolves one of two `Widget` candidates;
- nested imports accumulate `@using` directives and nearest `@namespace` wins;
- nearest `@namespace Sample.Pages` on `Pages/_Imports.razor` gives `Pages/Admin/Widget.razor` the candidate namespace `Sample.Pages.Admin`;
- imports apply only to the same directory subtree, not siblings or parent files;
- local fact `namespace_context` joins inherited imports without duplication;
- one globally unique simple-name component outside every effective namespace produces no edge;
- explicit local/dotted component qualified names override derived defaults;
- fully qualified tags stay exact;
- aliases, `@using static`, and duplicate matching candidates produce no edge;
- `.razor` consumes `_Imports.razor` and ignores `_ViewImports.cshtml` evidence;
- `/` and `\` artifact paths follow existing normalization and sibling isolation.

**Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~BlazorComponentGraphReaderTests`

Expected: existing tests pass and new inherited-import tests fail with missing edges; ambiguity tests remain empty.

**Step 4: Implement the import catalog and reader integration**

Create immutable internal component/directive records and a `BlazorNamespaceCatalog` with `Build`, `EffectiveNamespaces`, and `QualifiedNames` operations. Read directive rows once from SQLite by parsing `metadata_json` with `JsonDocument`; accept only `type=razor-directive` `using`/`namespace` rows from `_Imports.razor`. Normalize paths by the repository's existing relative-path policy, accumulate ancestor `@using` values root-to-leaf, apply the nearest relevant `@namespace` with its relative folder suffix, include the source's effective namespace, filter aliases/static/property expressions, and deduplicate ordinally.

Use this internal interface; keep all records immutable and internal:

```csharp
internal sealed record BlazorComponentIdentity(
    string Id,
    string Path,
    string Name,
    string DeclaredQualifiedName);

internal sealed record RazorImportDirective(
    string Path,
    string DirectiveName,
    string DirectiveValue);

internal sealed class BlazorNamespaceCatalog
{
    public static BlazorNamespaceCatalog Build(
        string? workspaceRoot,
        IReadOnlyList<BlazorComponentIdentity> components,
        IReadOnlyList<RazorImportDirective> directives);

    public IReadOnlyList<string> EffectiveNamespaces(
        BlazorComponentIdentity source,
        IReadOnlyList<string> localNamespaces);

    public IReadOnlyList<string> QualifiedNames(BlazorComponentIdentity component);
}
```

Wire the catalog into `BlazorComponentGraphReader.Read` without changing its signature. Dotted tags remain exact. Every simple tag, including a workspace-wide unique name, must match local, inherited, or one unambiguous source namespace and emit an edge only for one distinct target. Index both explicit and derived qualified names, but never add a derived name when a component already has a dotted declared name. Replace the existing unique-name positive fixture with one that supplies real namespace scope, and add a no-scope negative fixture. For positive fixtures, assert reverse dependents; for negative fixtures, assert direct and reverse edges are absent.

**Step 5: Apply commit mode**

Run the focused reader tests and `scripts/test.sh`. Expected: PASS within the fast-suite budget. Use `serial-worker-commit` and record the green slice SHA.

**Acceptance criteria:**
- [x] Tests cover import inheritance, namespace suffixes, isolation, precedence, ambiguity, and both path separators.
- [x] Positive cases assert reverse reachability, not only raw edge rows.
- [x] Negative cases enforce fail-closed behavior.
- [x] Public reader signature and exact-name behavior remain unchanged; the unsupported workspace-global unique-name shortcut is removed.

### Task 2: Derive project and folder namespaces

**Files:**
- Modify: `src/Miller.Indexing/BlazorNamespaceCatalog.cs`
- Modify: `tests/Miller.Tests/Indexing/BlazorComponentGraphReaderTests.cs`
- Create: `tests/Miller.Tests/Indexing/BlazorNamespaceCatalogTests.cs`
- Modify: `tests/Miller.Tests/Indexing/JulieDbFixture.cs`

**Interfaces:**
- Consumes: Task 1 catalog, workspace root, component paths, and import namespace evidence.
- Produces: Authoritative or derived project/folder qualified names for ambiguous component resolution.

**Contract inputs:** Public reader signature remains unchanged; project discovery starts from trusted component/reference paths and walks ancestors only to `root_path`. The SDK default is `MSBuildProjectName.Replace(" ", "_")`; raw XML is not full MSBuild evaluation.

**File ownership:** `BlazorNamespaceCatalog.cs`, project-namespace reader cases in `BlazorComponentGraphReaderTests.cs`, `BlazorNamespaceCatalogTests.cs`, and a parameterized `JulieDbFixture.SetArtifactMetadata` helper

**Serialization required:** Yes.

**Dependency reason:** Extends the Task 1 catalog with bounded project evidence.

**Step 1: Write failing project/folder tests**

Add `JulieDbFixture.SetArtifactMetadata(key, value)` using a parameterized update, and point filesystem-backed fixtures at `fixture.WorkspaceRoot` instead of the fixture's historical `/work/repo` default. Add public-reader cases proving literal `RootNamespace` plus component folders, the exact SDK project-name default including space-to-underscore replacement, nearest nested `@namespace` plus its relative folder suffix, same-folder/project-root implicit scope, explicit dotted component namespace precedence, multiple-project ambiguity, conditional/property-expanded/imported roots, sibling project isolation, symlinked directories/project files, oversized XML, and both artifact separators. Include a component beneath a directory with two project files and a valid parent project; assert the resolver stops at the ambiguous directory and does not fall through to the parent.

**Step 2: Run the focused tests**

Run the focused reader command. Expected: Task 1 import cases pass and new project/folder cases fail.

**Step 3: Implement bounded project evidence**

For a component path:

1. Call `WorkspaceRelativePath.ResolveUnderRoot(workspaceRoot, component.Path)`, then reject any existing directory or project-file segment with `FileAttributes.ReparsePoint` before enumerating or reading it. Do not weaken or generalize the shared lexical path helper for this feature.
2. Walk ancestor directories toward `workspaceRoot` and stop at the nearest directory containing any `*.csproj`; never continue past a directory that contains project files.
3. Require exactly one project file in that directory. If there are zero after enumeration or more than one, fail closed for that component and produce no project-derived candidate.
4. Reject project files larger than 1 MiB. Parse the single file with `XmlReaderSettings` using `DtdProcessing.Prohibit`, `XmlResolver = null`, and `MaxCharactersInDocument = 1_048_576`. Accept exactly one unconditional literal `<RootNamespace>` value. When the element is absent, apply the SDK's exact project-file-name default by replacing spaces with underscores.
5. Reject conditional/conflicting values, `$(...)`, invalid namespace segments, explicit `<Import>` elements, paths outside the root, or I/O/XML errors. Also walk only the in-root ancestor chain for `Directory.Build.props`/`Directory.Build.targets`; if an applicable file contains `RootNamespace` or another import, fail closed rather than pretending to evaluate MSBuild.
6. Derive the component namespace from the project root plus relative folder. The nearest applicable `@namespace` replaces that base and appends only folders below its imports file. An explicit dotted component qualified name remains authoritative.

Do not enumerate the whole workspace and do not cache across `Read` calls.

**Step 4: Extend qualified-name resolution**

Treat a dotted `DeclaredQualifiedName` as authoritative. When Julie's qualified name equals the simple component name, derive candidates from the nearest applicable `@namespace`; otherwise use the unambiguous project root plus the component's project-relative folder. Derive the source component namespace through the same path so documented same-folder and project-root binding can disambiguate simple tags. Cache project lookup per directory for the duration of one `Read` call and never enumerate outside ancestor directories.

**Step 5: Run focused tests and apply commit mode**

Run Task 1/project tests plus `scripts/test.sh`. Expected: PASS within budget. Use `serial-worker-commit` and record the SHA.

**Acceptance criteria:**
- [x] Filesystem access is bounded, root-safe, symlink-aware, size-capped, and cached per read.
- [x] Project/folder rules are deterministic and ambiguity produces no candidate.
- [x] Tests distinguish the bounded project heuristic from unsupported imported/conditional MSBuild evaluation.
- [x] The module hides project XML, path traversal, precedence, and filtering behind a small internal interface.

### Task 3: Harden repository loading and failure behavior

**Files:**
- Modify: `src/Miller.Indexing/BlazorComponentGraphReader.cs:15-214`
- Modify: `tests/Miller.Tests/Indexing/BlazorComponentGraphReaderTests.cs`
- Modify: `tests/Miller.Tests/Indexing/RepositoryIndexLoaderBridgeTests.cs`

**Interfaces:**
- Consumes: The Task 1/2 reader integration and existing repository loader.
- Produces: Safe degradation and unchanged repository-loading behavior when optional namespace evidence is missing or malformed.

**Contract inputs:** `ExtractReader.ReadRootPath(dbPath)` supplies the canonical root or `null`; a missing root disables derived namespace logic but must not break exact or import-supported resolution.

**File ownership:** `BlazorComponentGraphReader.cs`, its tests, and repository-loader bridge tests

**Serialization required:** Yes.

**Dependency reason:** Proves the completed Task 1/2 integration remains safe at the repository boundary.

**Step 1: Add failing degradation tests**

Add tests proving:

- the no-Blazor-facts fast path never opens the database;
- malformed component/directive metadata is ignored rather than aborting index load;
- a missing `root_path` disables only project-derived candidates while retaining exact and inherited-import resolution;
- unreadable, malformed, conditional, imported, or ambiguous project files disable only their derived candidate;
- `RepositoryIndexLoader.Load` still returns the repository and all unrelated graph evidence.

**Step 2: Define precise fail-closed rules**

Localize JSON, path, project-file, and I/O failures to the affected row or candidate. Do not catch database-open/schema failures that existing loader policy treats as artifact corruption. Preserve deterministic row ordering and return no edge when candidate construction is incomplete or ambiguous.

**Step 3: Keep the loader integration single-pass**

Confirm `RepositoryIndexLoader.Load` invokes `BlazorComponentGraphReader.Read` once and does not add a second artifact scan. Build the catalog even when `root_path` is absent so import-only resolution remains available; project derivation checks the nullable root internally:

```csharp
string? rootPath = ExtractReader.ReadRootPath(dbPath);
BlazorNamespaceCatalog namespaces = BlazorNamespaceCatalog.Build(
    rootPath,
    components,
    directives);
```

Do not change `RepositoryIndexLoader.Load`, `BlazorComponentGraphReader.Read`, provider selection, edge kind, or public result types unless a failing regression proves a pre-existing integration defect.

**Step 4: Run reader, loader, and graph tests**

Run focused reader tests, `RepositoryIndexLoaderBridgeTests`, `BlazorBridgeProviderTests`, and graph traversal tests.

Expected: PASS; exact, namespace-supported, external, and no-facts behavior remains correct, while out-of-scope unique simple tags remain unresolved.

**Step 5: Apply commit mode**

Use `serial-worker-commit`; record the failure-hardening SHA.

**Acceptance criteria:**
- [x] Public reader and repository-loader signatures remain unchanged.
- [x] Derived context resolves only one distinct target.
- [x] Missing root/project/import evidence preserves existing behavior and never throws during index load.
- [x] Existing bridge provider selection and edge kind stay unchanged.

### Task 4: Add live artifact and cross-platform evidence

**Files:**
- Modify: `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs`
- Modify: `tests/Miller.Tests/Indexing/WorkspaceRelativePathTests.cs` only if a missing cross-platform invariant is found

**Interfaces:**
- Consumes: Real `julie-extract` output and Task 3 graph loader.
- Produces: End-to-end proof from source tree to artifact to Miller graph.

**Contract inputs:** Scale tests must use `ScaleTestSupport.RequireJulieServer()` and carry `[Trait("Category", "Scale")]` at class level.

**File ownership:** Existing live Blazor Scale fixture/test and narrowly related path tests

**Serialization required:** No.

**Dependency reason:** Runs after Task 3 and owns separate Scale/path files.

**Step 1: Write the live fixture**

Extend the existing live Blazor fixture with a `.csproj`, root and nested `_Imports.razor`, two same-named components in different namespaces, one consuming component, and a fully qualified tag. Do not seed SQLite directly. Query the live artifact to prove inherited imports remain absent from each fact's local `namespace_context` and that `type=razor-directive` rows supply the missing evidence.

**Step 2: Run the Scale test before final wiring**

Run: `scripts/test.sh scale` with the pinned Julie binary available.

Expected: the new test fails before Task 3 integration or passes after it; record the exact binary version.

**Step 3: Assert live graph behavior**

Load the extracted artifact through `RepositoryIndexLoader.Load`, assert the expected `uses` edges, reverse dependents, and a shortest path; assert the sibling/ambiguous component has no edge. Record that julie-extract 2.14.0 emits `_ViewImports.cshtml` directives but no `.cshtml` component-reference facts, so that path remains deferred rather than simulated.

**Step 4: Run cross-platform path cases**

Exercise both artifact separators through catalog normalization without changing `WorkspaceRelativePath`'s OS-native trust boundary. Windows drive casing stays owned by the unchanged path abstraction; run Windows CI only if that abstraction changes or a platform-specific failure appears.

**Step 5: Apply commit mode**

Use `parallel-lead-commit`; hand off the verified Scale/path diff.

**Acceptance criteria:**
- [x] A real julie-extract 2.14.0 artifact resolves inherited component namespaces end to end.
- [x] The test is correctly categorized Scale and does not leak into the fast suite.
- [x] Both artifact separators preserve the unchanged root trust boundary.
- [x] Evidence records the current `.cshtml` extractor limitation without claiming `_ViewImports.cshtml` resolution.

### Task 5: Document support and verification

**Files:**
- Modify: `README.md` only where current Blazor capabilities are summarized
- Modify: `docs/README.md`
- Create: `docs/findings/2026-07-14-blazor-namespace-resolution.md`
- Modify: this plan's progress checkboxes

**Interfaces:**
- Consumes: Verified Task 3/4 behavior.
- Produces: Exact current capability and limitation documentation.

**Contract inputs:** No new public JSON, CLI, MCP, or artifact contract is introduced, so no contract version bump is allowed.

**File ownership:** README/docs map, new evidence note, plan progress

**Serialization required:** No.

**Dependency reason:** Runs after Task 3 without editing implementation/test files.

**Step 1: Write evidence-backed documentation**

State that Miller resolves `.razor` component references using exact tags, local contexts, inherited import directives, source namespaces, and deterministic bounded project/folder namespaces. State that aliases, imported/conditional/property-expanded roots, or ambiguous project models fail closed. State that `.cshtml`/`_ViewImports.cshtml` component resolution waits for upstream component-reference facts.

**Step 2: Link active documentation**

Add the new plan/evidence to `docs/README.md` under the current Blazor/bridge section; do not rewrite the completed 2026-07-11 plan as though it had included this follow-up.

**Step 3: Verify docs against tests**

Map each capability sentence to a named fast or Scale test. Remove any sentence without a proving test.

**Step 4: Run documentation/convention gates**

Run the repo's docs/link/convention checks included by `scripts/test.sh`.

Expected: PASS with no prompt-facing stale version or unsupported claim.

**Step 5: Apply commit mode**

Use `parallel-lead-commit`; hand off the docs diff.

**Acceptance criteria:**
- [x] Docs distinguish supported deterministic cases from fail-closed limitations.
- [x] Historical plans remain historical.
- [x] No new public contract or Eros claim is introduced.

### Task 6: Run branch gates and prepare handoff

**Files:**
- Modify: this plan's progress checkboxes
- Modify: release-prep files only after separate user approval

**Interfaces:**
- Consumes: Tasks 1-5 at one HEAD.
- Produces: A merge-ready Miller branch with exact test/binary evidence.

**Contract inputs:** Push and release remain explicit approval boundaries.

**File ownership:** Plan progress and separately approved release-prep files only.

**Serialization required:** Yes.

**Dependency reason:** Requires Tasks 1-5 at one HEAD.

**Step 1: Run affected-change verification**

Run focused reader/catalog/loader/graph tests, then `scripts/test.sh`.

Expected: PASS within the fast-suite budget.

**Step 2: Run branch verification**

Run:

```bash
dotnet build Miller.slnx -c Release
scripts/test.sh scale
scripts/test.sh all
```

Expected: zero warnings/errors and all fast/Scale tests pass.

**Step 3: Review architecture and contract boundaries**

Confirm `Miller.Core` gained no I/O dependency, no public reader/loader signature changed, no raw Razor parser was added, no MCP/CLI surface changed, and ambiguity still emits no edge.

**Step 4: Verify all worktrees**

Run root, branch, HEAD, status, and worktree inventory checks. Inspect every related Miller worktree status and preserve unrelated changes.

**Step 5: Apply commit mode and stop at approval**

Use `serial-worker-commit`; record the final SHA, Julie binary version, verification ledger, and any platform-specific CI evidence. Do not push, version-bump, tag, or publish without explicit approval.

**Acceptance criteria:**
- [x] Fast, Scale, all, and Release build gates pass at one HEAD.
- [x] Public and ownership boundaries remain intact.
- [x] Final evidence records Miller SHA and Julie binary version.
- [x] No Eros change, push, or release occurred.
