# Tool Output Usability Dogfood

- **Date:** 2026-06-07
- **Purpose:** Close the TODO follow-up to review tool results for practical usability while deciding whether
  search can default to a smaller page.
- **Binary:** `dotnet run --project src/Miller.Server`

## Result

Miller search can default to 6 results. The compact symbol output remains actionable because each normal hit
includes name, kind, file, line, and signature when available; exact symbol hits still promote a definition block,
show grouped "Other matches", and include a "more (raise limit)" note when the page is truncated.

Representative CLI dogfood covered:

| Tool | Command shape | Usability result |
| --- | --- | --- |
| search | `miller search SearchTool` | 6-result default showed the definition, grouped nearby constructors/methods, and an overflow note. |
| content search | `miller search "default search result count" --mode content` | Returned `path:line` plus snippet context. |
| inspect | `miller inspect src/Miller.Server/Tools/SearchTool.cs --limit 12` | Returned symbol groups with line numbers and a low-signal hidden note. |
| context | `miller context "default search result count" --token-budget 1200 --max-hops 1` | Returned a bounded bundle with provenance and hop metadata. |
| impact | `miller impact src/Miller.Server/Tools/SearchTool.cs --max-depth 1 --limit 6` | Returned impacted symbols plus likely tests. |
| trace | `miller trace SearchTool --scope src/Miller.Server/Tools/SearchTool.cs --depth 1 --limit 6` | Returned an ambiguity prompt with concrete candidates. |
| workspace status | `miller workspace status` | Surfaced a misleading sidecar revision comparison, fixed in this pass. |

## Changes Made

- Lowered MCP and CLI search default result count from 10 to 6.
- Centralized the rendered search default at `SearchTool.DefaultLimit`.
- Updated README and embedded agent instructions to document the 6-result default and how to widen with `limit`.
- Corrected CLI help so search lists both `--include-tests` and `--exclude-tests`.
- Fixed workspace status stale sidecar wording so newer-than-expected sidecars render `built N > expected M`
  instead of the incorrect `built N < expected M`.

## Verification

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~Search_DefaultLimit_RendersSixActionableRows_WithOverflowNote --no-restore
Passed: 2

dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~Miller.Tests.Server.SearchToolTests|FullyQualifiedName~Miller.Tests.Server.Cli.CliDispatchTests" --no-restore
Passed: 113

dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~Status_Compact_StaleSearchSidecar_ReportsRevisionDirectionHonestly --no-restore
Passed: 1

dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~Miller.Tests.Server.WorkspaceRenderTests --no-restore
Passed: 20

dotnet build Miller.slnx -c Release --no-restore
Build succeeded: 0 warnings, 0 errors

scripts/test.sh
Passed: 1683 fast tests

git diff --check
Clean

src/Miller.Server/bin/Release/net10.0/miller search SearchTool
Confirmed default page renders an overflow note.

src/Miller.Server/bin/Release/net10.0/miller workspace status
Confirmed stale sidecar output uses the correct revision direction (`built 1235 > expected 1029` in the local smoke).
```
