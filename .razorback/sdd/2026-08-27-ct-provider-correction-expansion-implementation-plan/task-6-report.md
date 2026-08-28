# Task 6 report: first-class VB.NET selection and .NET backend evidence

## WHAT

- Added exact `.vbproj` inventory classification for MSTest, NUnit, xUnit v2/v3, Microsoft.NET.Test.Sdk, MSTest.Sdk, Microsoft.Testing.Platform, and unknown generic projects.
- Added `DotnetTestBackend` with a typed execution-backend discriminator, nearest `global.json` runner evidence, static project properties, and bounded/evaluation-only MSBuild property probing.
- Preserved current VSTest and xUnit framework identities and commands while enriching diagnostic discovery with source paths, symbol names, and symbol paths.
- Kept duplicate MSTest parameterized rows distinct and mapped TRX display names back to their selected provider case ids.
- Removed the unconditional fileless-C# path guess; VB path stems now require exact `vbnet` matching, while the existing C# compatibility path remains bounded to known C# changes.
- Added `VbDotnetScale` with MSTest discovery, parameterized execution, TRX parsing, Julie extraction, and Julie-backed impact selection.

## WHY

VB.NET was already extracted by Julie but could be misclassified or selected through same-stem C# cases. Generic MSTest discovery exposed only display names while TRX results could carry different qualified names, and duplicate parameterized rows shared one provider id. The backend evidence is separate from the public framework string so Task 7 can add MTP routing without changing stored framework identities.

## HOW

Static evidence reads bounded project XML and the nearest applicable `global.json`. The property probe is `dotnet msbuild <project> -nologo -getProperty:<five runner properties>`; parser acceptance requires complete JSON (including the real `Properties` wrapper) and boolean-valued properties. Incomplete, malformed, or unknown runner evidence resolves to `Unknown`.

Generic diagnostic discovery uses `CodeFilePath` when present, normalizes it relative to the workspace, and emits unique display-qualified ids only when a qualified name has multiple rows. TRX parsing matches those ids by display name. Julie facts verify the VB test symbol and current `vbnet` file identity before selection.

Official evidence used:

- [MSTest SDK configuration](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-sdk): MSTest.Sdk defaults to Microsoft.Testing.Platform and `UseVSTest=true` switches to VSTest.
- [Microsoft.Testing.Platform with dotnet test](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test): .NET 10 `global.json` `test.runner` selection and MTP version support.
- [MSBuild property evaluation](https://learn.microsoft.com/en-us/visualstudio/msbuild/evaluate-items-and-properties): multiple `-getProperty` values produce JSON grouped under `Properties`.

## VERIFICATION

- Red phase: initial focused run exposed the shared-xUnit false-positive, VB/C# family collision, and fileless-case KnownEmpty behavior; each was covered by a focused regression and corrected.
- Green phase: `dotnet test --filter "FullyQualifiedName~ContinuousTestProjectInventoryTests|FullyQualifiedName~DotnetTestProviderTests|FullyQualifiedName~ContinuousTestImpactSelectorTests" --no-restore` — 256 passed, 0 failed, 0 skipped.
- Scale: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~Real_vb_fixture_discovers_runs_and_selects_with_julie_identity" --no-restore` — 1 passed, 0 failed, 0 skipped.
- `git diff --check` passed.

## STATE

- Commit SHA: final commit created from this report; verify with `git rev-parse HEAD` at handoff.
- Base: `9b246e68`.
- Path: `/home/murphy/source/miller`.
- Branch: `feature/ct-provider-correction-expansion`.
- Worktree after commit: clean; no unrelated worktree was present.
- Worktree list: `/home/murphy/source/miller <final HEAD> [feature/ct-provider-correction-expansion]`.

## CONCERNS

- MTP execution is intentionally not routed in Task 6; Task 7 should consume `DotnetTestBackendEvidence` and preserve the existing VSTest/xUnit lanes.
- Static inventory metadata is explicitly marked `static`; an effective MSTest.Sdk backend requires complete evaluated properties unless `global.json` states the runner directly.
