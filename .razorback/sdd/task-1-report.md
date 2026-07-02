# Task 1 Report — Backend HTTP Boundary: Whitelist + Adapter Reads (16 families)

**Status:** DONE
**Branch:** `feat/backend-http-boundary`
**Commit:** `d731506` (d7315068231a4a392269a842eca080338ed5c6ec)

## What I implemented

1. **16 pattern-id constants** in `src/Miller.Core/Graph/BridgeStructuralPatterns.cs`, all appended to
   `BridgeFactPatternIds` (the SqliteBridgeReader SQL load whitelist). Whitelist grew 14 → 30.
   - Route-template: `ExpressRoute`, `FastifyRoute`, `FastApiRoute`, `FlaskRoute`, `DjangoUrlPattern`,
     `SpringRequestMapping`, `GoNetHttpRoute`, `GinRoute`, `EchoRoute`, `RailsRoute`.
   - Mount/include: `ExpressRouterMount`, `FastApiIncludeRouter`, `FlaskBlueprintRegistration`, `DjangoUrlInclude`.
   - Rails extras: `RailsResourceRoute` (expanded later), `RailsMount` (evidence-only).
2. **`BackendRoutePatternIds`** — public `IReadOnlyList<string>` naming exactly the 10 route-template families
   (NOT the 4 mounts, NOT `RailsResourceRoute`, NOT `RailsMount`).
3. **`TryReadBackendRoute`** in `StructuralRouteFactAdapter.cs` — sibling of `TryReadRouteHandler`:
   - family gate = `BackendRoutePatternIds`;
   - route path = `effective_route_template ?? normalized_route_template` (differs from `TryReadRouteHandler`);
   - Spring `attribute_kind="class_route"` → false (prefix fact, never an endpoint);
   - blank route → false (Django `route_syntax="regex"` honest exclusion);
   - nullable UPPERCASE verb (verbless → null → downstream Medium `verb_unknown`);
   - `IsTestFact` filter; `containing_symbol_id` passthrough (`?? string.Empty`); `FilePath=fact.Path`;
     `Line=fact.Span.StartLine`; reuses the existing `StructuralRouteHandler` record.
4. **`TryReadMountFact`** + new **`StructuralMountFact(Fact, MountPath, MountTarget, IncludedModule, FilePath)`**:
   - family gate = the 4 composing mounts only (NOT `RailsMount` — evidence-only, no adapter read);
   - `MountPath = normalized_mount_path ?? mount_path`; neither present → false (prefix-less include composes nothing);
   - `MountTarget = mount_target ?? string.Empty`; `IncludedModule = included_module` (Django only);
     `FilePath = fact.Path`; `IsTestFact` filter applied.
5. **`TryReadRouteHandler` (Next/Nuxt) left byte-identical** — the new read is a sibling, not an extension.

## Files changed

- `src/Miller.Core/Graph/BridgeStructuralPatterns.cs` (+57): 16 constants, 16 whitelist entries, `BackendRoutePatternIds`.
- `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs` (+116): `using System.Linq`; 5 private pattern aliases;
  `TryReadBackendRoute`; `TryReadMountFact`; `IsBackendRoutePattern`/`IsMountFactPattern` predicates;
  `StructuralMountFact` record.
- `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs` (+359): 13 `[Fact]` + 2 `[Theory]` (19 cases total).

## Verification

- **Invariant:** all 16 ids load through the whitelist; `TryReadBackendRoute` proves effective-template precedence,
  nullable/uppercased verb, Spring class_route rejection, Django regex blank rejection, test-fact rejection,
  non-route-family gate; `TryReadMountFact` proves Django `included_module` read, normalized-path precedence,
  `mount_path` fallback, prefix-less rejection, test-fact rejection, non-mount-family gate (incl. RailsMount);
  existing Next/Nuxt reads stay green.
- **RED:** worker-scope filter → CS0117 for every new member (feature missing) before implementation.
- **GREEN (assigned worker scope):** `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter
  "FullyQualifiedName~BridgeGraphBuilderTests&Category!=Scale" -v minimal` → **113 passed, 0 failed** (107 ms).
- **Build gate:** `dotnet build Miller.slnx -c Release` → **0 Warning(s) / 0 Error(s)**.
- **Worker ceiling:** `scripts/test.sh` → **2653 passed, 0 failed** (baseline 2634 + 19 new cases), wall 14s (<30s).
- **Timestamp:** 2026-07-02.

## Miller calls used (orientation)

- `inspect(src/Miller.Core/Graph/StructuralRouteFactAdapter.cs)` — confirmed the 5 methods / 12 aliases / 4 records
  layout and that `TryReadRouteHandler` is the template at :132.
- `inspect(TryReadRouteHandler, depth=full)` — confirmed body shape: pattern gate → route-path precedence
  (`route_path ?? normalized_route_template`) → `IsTestFact` → nullable-uppercase verb → `StructuralRouteHandler` ctor.
- `inspect(StructuralRouteHandler, depth=full)` — confirmed the record's 6 fields
  `(Fact, RoutePath, string? Verb, ContainingSymbolId, FilePath, Line)`; consumed by `FileRouteBridge.ResolveClientRequests`.
- `inspect(src/Miller.Core/Graph/BridgeStructuralPatterns.cs)` — confirmed 14 constants + `BridgeFactPatternIds` at :24.

## API-shape evidence (nothing invented)

- `StructuralRouteHandler` fields — Miller `inspect StructuralRouteHandler depth=full` + file read :277 (reused as-is).
- `MetadataString(fact,key)` returns null for missing/blank — file read :225 (used for all metadata reads).
- `IsTestFact(fact, symbolsById)` — file read :160 (container `IsTest` OR test path).
- `StructuralFactRecord` members `.PatternId/.Path/.ContainingSymbolId/.Span.StartLine/.Metadata` — used by the
  existing `Fact(...)` test helper (:35) and `TryReadRouteHandler` body.
- Metadata key names (`effective_route_template`, `normalized_route_template`, `attribute_kind="class_route"`,
  `verb`, `normalized_mount_path`, `mount_path`, `mount_target`, `included_module`, `route_syntax="regex"`) — taken
  from the plan's authoritative "Decided Consumption Contracts" section (plan lines 33-53), not guessed.

## Self-review findings

- `TryReadRouteHandler` untouched (verified: its two tests + all navigation/client tests stay green).
- Spring class_route rejection is ordered BEFORE the blank-route check so a class_route fact that DOES carry a
  `normalized_route_template` is still rejected on the attribute reason (test pins this).
- `IsBackendRoutePattern` gates against `BridgeStructuralPatterns.BackendRoutePatternIds` directly (single source of
  truth) so the adapter gate can never drift from the provider's family list.
- No LINQ was in the adapter before; added `using System.Linq;` for the `Enumerable.Contains(..., StringComparer.Ordinal)`
  gate. Release build stays 0-warning.

## Judgment calls (file:line — chose X over Y because …)

1. **`MountTarget = mount_target ?? string.Empty`** (`StructuralRouteFactAdapter.cs` mount ctor). The Task-1
   description says "MountTarget = mount_target (always present for the 4)", but the plan's authoritative Decided
   Consumption Contracts (plan :50) lists Django `url_include` as emitting `included_module` and NOT `mount_target`.
   The decided record shape declares `MountTarget` as non-nullable `string`. To honor the non-nullable shape without a
   nullable-warning-as-error, I coalesce to empty string (matching the existing `ContainingSymbolId ?? string.Empty`
   passthrough pattern). Chose contract-faithful `?? string.Empty` over changing the record to `string?` (which would
   deviate from the decided shape). Flagged as a minor spec-vs-contract tension for the lead — not a redesign.
2. **`IsBackendRoutePattern` uses the `BackendRoutePatternIds` list** rather than a 10-way `string.Equals` chain.
   DRY single-source-of-truth over mirroring the verbose chain style of the 5 existing predicates; the 4-family mount
   gate still uses the explicit chain style since it has no published list.
3. **`IsTestFact` applied to `TryReadMountFact`** — the task called this a judgment call. Applied it to mirror
   `TryReadRouteHandler`: a mount fact in a test file must not seed real composed edges in Task 3. Pinned by
   `..._TryReadMountFact_RejectsTestFacts` (container-symbol `IsTest` route, since Django `tests/urls.py` paths don't
   trip the JS-oriented `IsTestPath` substrings).
4. **No Goldfish checkpoint committed.** The per-task instruction says "commit ONLY the two src files + the test file",
   and Task 1's acceptance criteria list no checkpoint (the plan's global checkpoint cadence is a lead-orchestration
   concern). Kept the commit scoped to exactly the 3 files. Flagged for the lead.
