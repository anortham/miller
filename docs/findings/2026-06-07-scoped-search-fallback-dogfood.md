# Scoped Search Fallback Dogfood

- **Date:** 2026-06-07
- **Purpose:** Close the scoped-miss UX decision from the post-beta search-quality rerun.

## Result

Miller now keeps scoped filters strict but gives compact recovery hints when a filter removes every otherwise
matching result.

Example:

```text
miller search SearchTool --file-pattern 'src/ui/**'

No results within file_pattern=src/ui/**.
Outside scope:
SearchTool  class  src/Miller.Server/Tools/SearchTool.cs:42  [McpServerToolType] public sealed class SearchTool
SearchTool  constructor  src/Miller.Server/Tools/SearchTool.cs:57  public SearchTool(IWorkspaceSearchProvider workspaceProvider, IWorkspaceContentSearchProvider contentProvider)
SearchTool  constructor  src/Miller.Server/Tools/SearchTool.cs:70  public SearchTool(
        IWorkspaceSearchProvider workspaceProvider,
        IWorkspaceContentSearchProvide…
```

## Design

- The requested scope is still honored: `renderedCount` stays `0` and JSON remains `[]`.
- Compact output shows up to 3 outside-scope candidates, preserving existing result shapes.
- The fallback only appears when `file_pattern` or `language` filters removed otherwise matching hits.
- Symbol, content, and source-region search all share the behavior.

## Verification

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~FilteredMiss --no-restore
Passed: 4

dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~Miller.Tests.Server.SearchToolTests --no-restore
Passed: 50

dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~Miller.Tests.Server.SearchToolTests|FullyQualifiedName~Miller.Tests.Server.Cli.CliDispatchTests" --no-restore
Passed: 117

dotnet build Miller.slnx -c Release --no-restore
Build succeeded: 0 warnings, 0 errors

scripts/test.sh
Passed: 1687 fast tests

src/Miller.Server/bin/Debug/net10.0/miller search SearchTool --file-pattern 'src/ui/**'
Confirmed compact fallback hint.

src/Miller.Server/bin/Debug/net10.0/miller search SearchTool --file-pattern 'src/ui/**' --json
Confirmed JSON remains [].

src/Miller.Server/bin/Release/net10.0/miller search SearchTool --file-pattern 'src/ui/**'
Confirmed release compact fallback hint.

src/Miller.Server/bin/Release/net10.0/miller search SearchTool --file-pattern 'src/ui/**' --json
Confirmed release JSON remains [].

git diff --check
Clean
```
