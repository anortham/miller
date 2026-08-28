# Task 6 report: first-class VB.NET selection and .NET backend evidence

## WHAT

- Added exact `.vbproj` inventory classification for MSTest, NUnit, xUnit v2/v3, Microsoft.NET.Test.Sdk, MSTest.Sdk, Microsoft.Testing.Platform, and unknown generic projects.
- Added `DotnetTestBackend` with a typed execution-backend discriminator, nearest `global.json` runner evidence, static project properties, and bounded/evaluation-only MSBuild property probing.
- Preserved current VSTest and xUnit framework identities and commands while enriching diagnostic discovery with source paths, symbol names, and symbol paths.
- Kept duplicate MSTest parameterized rows distinct and mapped TRX display names back to their selected provider case ids.
- Removed the unconditional fileless-C# path guess; same-stem fileless .NET cases now fail closed for both C# and VB changes unless unique compatible impacted evidence maps the case.
- Wired one backend-evidence preflight into production discovery and run setup. It reads static project/global evidence, probes inherited runner properties through `dotnet msbuild -getProperty`, requires complete output, attaches evaluated metadata to discovered cases, and refuses MTP before VSTest execution.
- Added provider regressions for probe invocation, evaluated metadata, malformed and over-bound output, decisive xUnit/global evidence, MTP refusal, xUnit v2 refusal, and run setup.
- Added `VbDotnetScale` with MSTest discovery, parameterized execution, TRX parsing, Julie extraction, and Julie-backed impact selection.

## WHY

VB.NET was already extracted by Julie but could be misclassified or selected through same-stem C# cases. Generic MSTest discovery exposed only display names while TRX results could carry different qualified names, and duplicate parameterized rows shared one provider id. The backend evidence is separate from the public framework string so Task 7 can add MTP routing without changing stored framework identities.

## HOW

Static evidence reads bounded project XML and the nearest applicable `global.json`. The property probe is `dotnet msbuild <project> -nologo -getProperty:<five runner properties>`; parser acceptance requires complete JSON (including the real `Properties` wrapper) and boolean-valued properties. Incomplete, malformed, or unknown runner evidence resolves to `Unknown`. Static and evaluated property values are preserved in backend metadata.

Generic diagnostic discovery uses `CodeFilePath` when present, normalizes it relative to the workspace, and emits unique display-qualified ids only when a qualified name has multiple rows. TRX parsing matches those ids by display name. Julie facts verify the VB test symbol and current `vbnet` file identity before selection.

Official evidence used:

- [MSTest SDK configuration](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-sdk): MSTest.Sdk defaults to Microsoft.Testing.Platform and `UseVSTest=true` switches to VSTest.
- [Microsoft.Testing.Platform with dotnet test](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test): .NET 10 `global.json` `test.runner` selection and MTP version support.
- [MSBuild property evaluation](https://learn.microsoft.com/en-us/visualstudio/msbuild/evaluate-items-and-properties): multiple `-getProperty` values produce JSON grouped under `Properties`.

## VERIFICATION

- Red phase: focused regressions reproduced fileless C# `KnownEmpty`, missing production probe invocation, malformed/over-bound probe handling, MTP routing risk, and cross-language fileless reconciliation; each was corrected under TDD.
- Green phase: `dotnet test --filter "FullyQualifiedName~ContinuousTestProjectInventoryTests|FullyQualifiedName~DotnetTestProviderTests|FullyQualifiedName~ContinuousTestImpactSelectorTests" --no-restore` — 266 passed, 0 failed, 0 skipped.
- Scale: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~Real_vb_fixture_discovers_runs_and_selects_with_julie_identity" --no-restore` — 1 passed, 0 failed, 0 skipped.
- xUnit v2 Scale: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~A_real_xunit_v2_project_is_refused_by_name_rather_than_by_a_raw_process_error" --no-restore` — 1 passed, 0 failed, 0 skipped.
- Build: `dotnet build Miller.slnx -c Release --no-restore` — 0 warnings, 0 errors.
- `git diff --check` passed.

## STATE

- Correction implementation commit SHA: `adc3c08b` (`fix(ct): wire dotnet backend evidence preflight`).
- Correction base: `c346aae1` (Task 6 implementation commit); original plan base: `9b246e68`.
- Path: `/home/murphy/source/miller`.
- Branch: `feature/ct-provider-correction-expansion`.
- HEAD at implementation verification: `adc3c08b`.
- Worktree after implementation commit: clean; no unrelated worktree was present.
- Worktree list at implementation commit: `/home/murphy/source/miller adc3c08b [feature/ct-provider-correction-expansion]`.

## CONCERNS

- MTP execution is intentionally not routed in Task 6; Task 7 should consume `DotnetTestBackendEvidence` and preserve the existing VSTest/xUnit lanes.
- Static inventory metadata is explicitly marked `static`; an effective MSTest.Sdk backend requires complete evaluated properties unless `global.json` states the runner directly.
- The report is finalized as a documentation-only follow-up after the implementation commit so the correction SHA is recorded without a self-referential commit value.
