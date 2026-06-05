# Bridge Provider Scope Design

Date: 2026-06-05

## Status

Approved direction. First provider-seam slice implemented: current reducers are behind `dotnet-web`, bridge graphs carry
capability metadata, and trace reports no-bridge provider status. Config-driven provider selection remains open.

## Problem

Miller's current cross-language bridge delivers a real agent-facing capability: it can materialize a code path such as
frontend HTTP call -> endpoint -> request/response DTO -> mapper -> entity -> table. That is the feature we had hoped
semantic embeddings would unlock in Julie.

The pre-provider implementation was too stack-specific to present as general language support:

- `RouteBridge` assumes TS/JS/Vue-style URL literals on one side and ASP.NET controller annotations on the other.
- `DtoEntityBridge` assumes AutoMapper-style `CreateMap<A,B>` and projection breadcrumbs.
- `EntityTableBridge` assumes EF `DbSet<T>` and opportunistic Dapper SQL.
- `BridgeGraphBuilder.Build` always ran those same static reducers.
- `BridgeKind` and rendered labels used C#/TS-oriented naming in comments and output.

The pinned `julie-extract` reports 36 supported language/extension groups. Miller's bridge should not look
authoritative for all of them when only one stack family has real evidence.

## Decision

Keep the bridge feature, but make it provider-scoped and evidence-first.

Miller should not try to "semantically understand" every language pair. It should build a scored, inspectable graph of
explicit cross-layer evidence from providers that know a framework stack. Breadth comes from adding tested providers, not
from vague global config.

## What Exists

The generic pieces are worth keeping:

- `BridgeGraph`, `BridgeNode`, `ScoredEdge`, and bridge walking.
- `CandidateEdge`, `Signal`, `BridgeScorer`, confidence bands, and reduced-confidence flags.
- `TraceTool mode=bridge` as the agent-facing surface.
- Name and field-shape corroborators, as long as they remain finishers/corroborators rather than sole evidence.

The stack-specific pieces should be isolated as the first provider:

- provider id: `dotnet-web`
- server conventions: ASP.NET controller routes and HTTP verb annotations
- client conventions: TypeScript/JavaScript/Vue URL literals and common HTTP carriers
- mapping conventions: AutoMapper `CreateMap`, future `ToDto`/projection support when fixtures prove it
- persistence conventions: EF `DbSet<T>`, future Dapper `FROM` only after the extract contract can pair SQL literals to type arguments

## Proposed Architecture

Introduce a bridge-provider seam above graph building:

```text
RepositoryIndexLoader
  -> BridgeGraphBuildPlan
       symbols
       bridge breadcrumbs
       enabled providers
  -> IBridgeProvider.BuildCandidates(...)
       DotnetWebBridgeProvider
       future providers
  -> BridgeScorer
  -> BridgeGraph
```

Core contracts should stay generic:

- `BridgeGraphBuilder` becomes the provider orchestrator and scorer runner.
- Provider reducers produce `CandidateEdge` values; they do not score.
- `BridgeScorer` remains provider-agnostic and decides confidence from typed signals.
- Provider capability metadata is attached to the graph or build report so `trace mode=bridge` can say which providers
  were active and which language/framework families are unsupported.

The first implementation should move today's static reducers behind a `DotnetWebBridgeProvider` without changing
existing bridge results.

## Configuration

Configuration should select and tune providers; it should not invent provider logic.

Recommended committed workspace file:

```json
{
  "bridge": {
    "providers": ["dotnet-web"],
    "dotnetWeb": {
      "clientLanguages": ["typescript", "javascript", "vue", "tsx", "jsx"],
      "serverLanguage": "csharp"
    }
  }
}
```

Use a committed root `miller.json`, not `.miller/`, because `.miller/` is rebuildable runtime state and ignored. Start
with only the `bridge` section; future product settings can extend the same file without migrating bridge config.

Default behavior for beta should be conservative:

- enable `dotnet-web` automatically only when no config is present and the extract contains C# endpoint evidence plus
  frontend URL literals or dotnet mapping/persistence breadcrumbs;
- treat an explicit provider list in `miller.json` as authoritative;
- otherwise build an empty bridge graph with a clear capability note;
- never infer support for a language solely because `julie-extract` indexed that language.

Capability metadata should live on `BridgeGraph` as a small immutable report:

- active provider ids;
- skipped provider ids plus reasons;
- unsupported/no-provider notes;
- provider-local evidence counts when cheap to compute.

Keeping the report on `BridgeGraph` makes `TraceTool` able to render capability status even when the graph has zero
edges.

## Agent Value

This is useful to coding agents when it is reliable. It reduces blind search and helps assemble the correct code
neighborhood for tasks such as:

- changing an endpoint and finding frontend callers plus request/response DTOs;
- changing a DTO/entity/table field and finding the likely API path;
- deciding which files and tests to inspect before a cross-layer edit;
- onboarding into a polyglot repo without manually grepping routes, DTO names, mapping calls, and table declarations.

The value is trust, not breadth. A narrow high-confidence provider is better than a broad bridge that guesses.

## Non-Goals

- Do not claim bridge support for every `julie-extract` language.
- Do not make config rules expressive enough to become a second parser.
- Do not use semantic embeddings as primary evidence.
- Do not let name similarity alone create confident edges.
- Do not add a second provider without a fixture, precision guard, and capability report.

## Architecture Quality

**Affected modules:** `Miller.Core.Graph`, `Miller.Core.Resolver`, `Miller.Indexing.RepositoryIndexLoader`, and
`Miller.Server.Tools.TraceTool`.

**Caller-facing interface:** `trace mode=bridge` should remain the main surface. The added visible behavior is explicit
provider/capability reporting when bridge coverage is absent or partial.

**Depth/locality check:** The new seam belongs above candidate production. Scoring and graph traversal stay generic so
new providers do not fork confidence logic.

**Test surface:** Tests should exercise the provider through `BridgeGraphBuilder.Build`/`RepositoryIndexLoader.Load` and
`TraceTool.Run`, not by asserting private reducer internals.

**Seams/adapters:** `IBridgeProvider` earns its keep because future stacks must add framework-specific extraction logic
without editing one monolithic static builder.

**Rejected shortcuts:** A global "language bridge config" that only lists suffixes/routes is rejected; it would create
authoritative-looking guesses without framework evidence.

**Architecture risk:** medium. The change touches a core graph construction boundary, but it can be staged as a
behavior-preserving extraction of the existing dotnet bridge before any new provider is added.

## Acceptance Criteria

- [x] Existing bridge results remain unchanged when `dotnet-web` is active.
- [x] Bridge builder accepts an explicit provider set and does not hard-code the dotnet reducers directly.
- [x] `trace mode=bridge` reports provider/capability status on no-bridge paths when useful.
- [x] Unsupported/no-provider paths do not look like empty-success; they surface an explicit skipped/no-provider note.
- [ ] The default provider selection is conservative and tested.
- [x] The old C#/TS-specific labels in public comments and docs are reframed as `dotnet-web`, not universal bridge support.
- [x] Fast tests cover the provider seam, disabled-provider behavior, and unchanged dotnet bridge output.
- [ ] Scale or fixture tests prove any future provider before it is enabled.

## Implementation Sketch

1. [done] Add provider interfaces and build-result metadata in `Miller.Core`.
2. [done] Extract current reductions into `DotnetWebBridgeProvider` with unchanged candidate output.
3. [done] Change `BridgeGraphBuilder.Build` to orchestrate providers, score candidates, and carry capability metadata.
4. Thread provider selection from indexing/load configuration.
5. [done] Update `TraceTool` rendering to show provider capability status on no-bridge paths.
6. Update docs and agent instructions so agents treat bridge output as provider-scoped evidence.

## Resolved Choices

- Config file: committed root `miller.json`.
- Default behavior: auto-enable `dotnet-web` only when no config exists and the extract has provider evidence.
- Metadata location: immutable capability report on `BridgeGraph`.
