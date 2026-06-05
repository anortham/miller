# Miller Free-Core Boundary And AOT Release Plan

- **Date:** 2026-06-04
- **Status:** Handoff for alignment work
- **Repos involved:** `miller`, `eros`, `julie-extractors`
- **Decision level:** Product boundary + release architecture

## Practical Answer

Miller should stay the open-source free code-intelligence core.

It should not be ported away from .NET just because of Native AOT concerns. A local AOT publish
already produced a native `osx-arm64` binary after warnings were temporarily demoted. The work is
AOT hardening and release packaging, not a rewrite.

## Product Boundary

Miller owns the local developer code-intelligence layer on top of `julie-extractors` artifacts:

- extract orchestration through the pinned `julie-extract` binary
- artifact/schema/version compatibility checks
- fast lexical and structural search
- `inspect` / `get_symbols` style source understanding
- context building
- trace / impact / references
- edit support if it stays inside the lean local-tool boundary
- workspace registry, freshness, and simple operational status
- deterministic cross-language structural resolution
- fast default tests with expensive scale gates kept opt-in

Miller should avoid:

- embeddings and semantic vector search in the default path
- commercial dashboards and workflow orchestration
- continuous test-quality scoring beyond basic likely-test support
- organization-wide portfolio analytics
- hosted services, accounts, policies, billing, or team governance
- duplicating `julie-extractors` parsing or schema ownership

The scale assumption is important: SQLite is the right Miller substrate for exact, local,
read-mostly code facts and FTS-backed lexical recall. It is not the chosen substrate for
portfolio-scale semantic/vector retrieval. If Eros needs LanceDB-scale projections, that is an
Eros-owned storage/runtime decision above the shared artifacts, not a reason to pull embeddings or
vector indexing into Miller's free core.

## Relationship To `julie-extractors`

`julie-extractors` remains the extraction product:

```text
source tree -> versioned SQLite/JSONL artifact
```

Miller consumes that product. It must keep its pin files, contract constants, restore scripts, and
tests in sync with each `julie-extract` release.

Miller should treat the artifact contract as the stable integration surface, not the Rust crate or
private Julie internals.

## Relationship To Eros

Eros should become a commercial extension over the same data, not a full duplicate of the free
core. Miller should expose a clean enough CLI/MCP/process surface that Eros can either:

- consume the same `julie-extractors` SQLite artifacts directly for advanced projections, or
- call Miller's public tool/process surface where that is cheaper and stable.

Miller must not make Eros depend on private .NET types or internal indexes.

Eros architecture decisions should remain downstream of Miller product completion. Until Miller is
functionally strong as the free core, Eros cannot know which workflows should consume Miller
directly, which should read `julie-extractors` artifacts, and which genuinely need Eros-owned
projections such as LanceDB-backed semantic retrieval.

The dividing line:

- Miller answers "where is the code and what does it structurally connect to?"
- Eros answers "what should the agent do next, how confident are we, and what higher-level
  evidence supports that?"

## Why .NET Is Still Acceptable

Miller's architecture is already close to the shape Native AOT wants:

- typed C# core logic
- no source parsing in-process
- no Rust FFI
- extraction through a subprocess
- SQLite as the artifact boundary
- clear logic/infrastructure split
- fast unit tests that do not require live subprocesses

The concern is not .NET itself. The concern is a small set of AOT-sensitive framework patterns.

## Current AOT Probe

Local command used:

```bash
dotnet publish src/Miller.Server/Miller.Server.csproj \
  -c Release \
  -r osx-arm64 \
  /p:PublishAot=true \
  /p:SelfContained=true \
  /p:JsonSerializerIsReflectionEnabledByDefault=false
```

With normal `TreatWarningsAsErrors=true`, publish fails on AOT/trim warnings. With warnings
temporarily demoted, publish completes and produces a native `miller` binary.

Observed local output:

- `miller` native executable: about 13 MB expanded
- `libe_sqlite3.dylib`: about 1.5 MB
- `libblake3_dotnet.dylib`: about 433 KB
- local copied `.tools/julie-extract`: about 64 MB expanded, about 9 MB compressed
- compressed platform-specific package estimate: roughly 15-25 MB if debug symbols are excluded

The package size is dominated by the paired `julie-extract` binary, not by .NET AOT. Release
packages should pair one Miller target with one matching `julie-extract` target, never ship all
extractor targets in one archive.

## AOT Hardening Work

Before AOT becomes a required release gate:

1. Add a non-blocking `aot-smoke` CI lane.
2. Replace reflection-based `System.Text.Json` serialization/deserialization with source-generated
   JSON contexts.
3. Replace MCP `.WithToolsFromAssembly()` reflection discovery with explicit generic tool
   registration.
4. Investigate Serilog trim warnings. Either move to an AOT-clean logging configuration or suppress
   narrowly with runtime smoke coverage.
5. Keep warnings as errors for the normal build, then turn AOT warnings into release-blocking
   errors once the warnings are understood.
6. Add release matrix jobs for platform-specific archives:
   - `linux-x64`
   - `linux-arm64`
   - `osx-arm64`
   - `win-x64`
7. Package only the matching `julie-extract` binary and checksum for each platform.
8. Exclude `.pdb`, `.dSYM`, and other debug symbols from normal user downloads. Publish them as
   separate debug artifacts if needed.

## Packaging Contract

Each release archive should contain:

```text
miller[.exe]
.tools/julie-extract[.exe]
libe_sqlite3.<platform-extension>
libblake3_dotnet.<platform-extension>
README or install note
checksums
```

The runtime should keep locating `julie-extract` from `AppContext.BaseDirectory/.tools` first, then
fall back to `PATH` only as a convenience.

## Near-Term Alignment Tasks

- Finish Miller's full product implementation first. Its functional success as the free core is the
  main input into every Eros duplication, projection, storage, and runtime-language decision.
- Keep Miller focused on projection-specific loading for first-read `search` / `inspect` latency.
- Add the Miller CLI surface so behavior can be tested outside MCP and used by other process-level
  consumers.
- Treat AOT as a release-readiness track, not a reason to switch to Go, TypeScript, or Python.
- Update older docs if they imply Eros must fully substitute for Miller rather than extend the free
  core through public contracts.

## Acceptance Criteria For The Next Session

- Miller still builds and tests normally before AOT work begins.
- AOT warnings are captured in CI output even if not yet blocking.
- JSON and MCP reflection warnings have tracked fixes.
- Release archives are platform-specific and do not bundle unrelated `julie-extract` targets.
- Eros alignment docs agree that Miller is the free core and Eros is the commercial extension layer.
