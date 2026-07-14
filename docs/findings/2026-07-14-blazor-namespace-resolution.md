# Blazor namespace resolution evidence

**Status:** Implemented locally on `codex/julie-extract-2.14`; final branch SHA and branch-wide gates remain pending Task 6.

## Current behavior

Miller resolves `.razor` component-reference facts into graph `uses` edges without adding a public JSON, CLI,
MCP, artifact contract, or Eros dependency. Fully qualified tags resolve exactly. Simple tags resolve only when
the target namespace is supported by fact-local context, the source component namespace, inherited
`_Imports.razor` `@using`/`@namespace` directives, or the bounded project/folder heuristic below.

Project inference starts at a materialized component path, walks only toward the artifact `root_path`, and stops
at the nearest directory containing project files. Exactly one `.csproj` is required. Miller accepts one
unconditional literal `RootNamespace`, or the SDK-style project-name default with spaces replaced by underscores,
then appends the component folder. A nearer inherited `@namespace` is authoritative and appends its descendant
folder suffix. Same-folder and project-root component namespaces are implicit source scopes.

| Behavior | Proving test |
| --- | --- |
| Fully qualified tags resolve only the exact qualified component. | `BlazorComponentGraphReaderTests.Read_FullyQualifiedTag_ResolvesExactQualifiedName` |
| Fact-local namespace context resolves a simple tag and preserves reverse reachability. | `BlazorComponentGraphReaderTests.Read_SimpleComponentWithLocalNamespaceContext_ProducesUsesEdgeAndReverseReachability` |
| A source component namespace can disambiguate a simple tag. | `BlazorComponentGraphReaderTests.Read_AmbiguousSimpleTag_UsesSourceNamespace` |
| Root-to-leaf inherited `@using` directives accumulate without crossing sibling subtrees. | `BlazorComponentGraphReaderTests.Read_InheritedUsingsAccumulateRootToLeafAndStayWithinSubtree` |
| The nearest inherited `@namespace` supplies the base namespace plus the descendant folder suffix. | `BlazorComponentGraphReaderTests.Read_NearestImportNamespaceAddsDescendantFolderSuffix`; `BlazorNamespaceCatalogTests.QualifiedNames_NearestNamespaceDirectiveOverridesProjectRootAndAppendsSuffix` |
| A literal project root, project-relative folders, same-folder scope, and project-root scope resolve deterministically. | `BlazorComponentGraphReaderTests.Read_LiteralProjectRootNamespaceResolvesSameFolderAndProjectRootComponents`; `BlazorNamespaceCatalogTests.QualifiedNames_LiteralRootNamespaceIncludesProjectRelativeFolders`; `BlazorNamespaceCatalogTests.EffectiveNamespaces_IncludeSourceFolderAndProjectRoot` |
| The project-name default replaces spaces with underscores and works with both artifact separators. | `BlazorComponentGraphReaderTests.Read_ProjectNameDefaultReplacesSpacesForBothArtifactSeparators`; `BlazorNamespaceCatalogTests.QualifiedNames_ProjectFileNameDefaultReplacesSpacesOnly` |
| A globally unique simple component still requires effective namespace evidence. | `BlazorComponentGraphReaderTests.Read_SimpleUniqueComponentOutsideEffectiveNamespaces_ProducesNoEdge` |
| The live extractor builds navigation, component, reverse-dependency, and path evidence from inherited namespaces without linking the wrong homonym. | `LiveBridgeTraceTests.BlazorFixture_LiveExtractBuildsNavigationComponentAndDependencyChains` (`Category=Scale`) |

## Fail-closed boundary

Miller does not evaluate MSBuild or parse raw Razor source to invent missing extractor evidence. Unsupported or
unsafe inputs contribute no derived namespace candidate and do not prevent unrelated repository evidence from
loading.

| Boundary | Proving test |
| --- | --- |
| Aliased, static, generic, and property-expression `@using` values do not resolve simple tags. | `BlazorComponentGraphReaderTests.Read_UnsupportedInheritedUsing_ProducesNoEdge` |
| Conditional, property-expanded, conflicting, imported, target/choose-derived, DTD-bearing, invalid, and malformed `RootNamespace` project XML fails closed. | `BlazorNamespaceCatalogTests.QualifiedNames_UnsupportedProjectEvaluationFailsClosed` |
| Visible `Directory.Build.props`/`Directory.Build.targets` namespace or import evidence fails closed. | `BlazorNamespaceCatalogTests.QualifiedNames_VisibleDirectoryBuildNamespaceEvidenceFailsClosed` |
| Multiple nearest project files stop resolution instead of falling through to a parent project. | `BlazorNamespaceCatalogTests.QualifiedNames_AmbiguousNearestProjectStopsBeforeParentProject` |
| Sibling projects remain isolated. | `BlazorNamespaceCatalogTests.QualifiedNames_SiblingProjectsStayIsolated` |
| Symlinked component directories, symlinked project files, and paths outside the artifact root fail closed. | `BlazorNamespaceCatalogTests.QualifiedNames_SymlinkedComponentDirectoryFailsClosed`; `BlazorNamespaceCatalogTests.QualifiedNames_SymlinkedProjectFileFailsClosed`; `BlazorNamespaceCatalogTests.QualifiedNames_PathOutsideWorkspaceFailsClosed` |
| Project XML over the one-mebibyte limit fails closed. | `BlazorNamespaceCatalogTests.QualifiedNames_OversizedProjectFileFailsClosed` |
| Malformed component/directive metadata is ignored while unrelated repository graph evidence survives. | `BlazorComponentGraphReaderTests.Read_MalformedComponentMetadata_IsIgnored`; `BlazorComponentGraphReaderTests.Read_MalformedDirectiveMetadata_IsIgnored`; `RepositoryIndexLoaderBridgeTests.Load_MalformedBlazorMetadata_PreservesRepositoryAndUnrelatedGraphEvidence` |
| `_ViewImports.cshtml` does not scope `.razor` references. | `BlazorComponentGraphReaderTests.Read_ViewImportsDirectiveDoesNotScopeRazorComponentReference` |
| `.cshtml` component resolution remains deferred because julie-extract 2.14 emits directive symbols but no component-reference facts for tested `.cshtml` forms. | The live 2.14 baseline audit recorded below. |

## Extractor evidence

- Verified binary: `julie-extract 2.14.0`.
- SHA-256: `7086cf5d50eb58c539ad993317f4bde9ad315bf5f5f1eb3ce707de34701140fd`.
- Baseline live audit: 24 symbols, 4 structural facts, and 0 diagnostics.
- `_Imports.razor` and `_ViewImports.cshtml` directives were emitted as directive symbols; inherited imports were
  absent from the component-reference fact's local namespace context, and the tested `.cshtml` forms emitted no
  component-reference facts.

## Task 5 verification

- `scripts/test.sh`: PASS, 3,256 fast tests, 0 failures, 25 seconds against the documentation working tree based
  on `79bf1cb`.
- Assigned-file diff and trailing-whitespace checks: PASS.

## Implementation state

- `68e6ddd` — inherited `_Imports.razor` namespace resolution and namespace-evidence requirement.
- `891541a` — bounded project/folder namespace inference and filesystem/XML guardrails.
- `3598bb2` — malformed-metadata and repository-load hardening.
- `79bf1cb` — live 2.14 artifact coverage for inherited and project/folder namespace resolution.
- `58eabef` — reviewed plan, current public documentation, and this implementation evidence.

## Final verification

Verification head: `58eabefd14cd3e101f2d28f18eadbcb8e655d9bf`, with `julie-extract 2.14.0` at the SHA-256 above.

- Focused reader/catalog/loader/provider/path tests: PASS, 77 tests.
- `dotnet build Miller.slnx -c Release`: PASS, 0 warnings and 0 errors.
- `scripts/test.sh`: PASS, 3,256 fast tests in 26 seconds.
- `scripts/test.sh scale`: PASS, 52 Scale tests in 16 seconds.
- `scripts/test.sh all`: PASS, 3,256 fast tests in 24 seconds and 52 Scale tests in 16 seconds.
- Final branch review: no findings; public signatures, graph edge kind, MCP/CLI surface, and ownership boundaries
  remain unchanged.
- All three Miller worktrees were clean at their recorded heads. No Eros change, push, tag, publish, or release
  occurred.

The closeout commit after this verification changes evidence checkboxes only. Its handoff SHA is reported outside
this self-referential document.
