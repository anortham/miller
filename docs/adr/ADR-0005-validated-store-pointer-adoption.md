# ADR-0005: Validated Store Pointer Adoption

## Context

Miller's direct CLI and warm-reader bootstrap can open a valid workspace store pointer, but a lock-winning leader resolves only through registry family/member lineage. An isolated registry therefore ignores a valid copied-family pointer and creates a new family, making incident-store replay impossible without mutating the live pointer or private registry.

## Decision

Leader bootstrap may adopt an existing pointer only after the normal family read contract proves the pointer family, catalog, selected view, canonical root, current generation, coordinator, resolution base, and artifact identity agree. Adoption records the validated binding in the registry and then uses the normal coordinator lifecycle. Any mismatch fails closed and must not mutate the registry.

The replay harness separately models Julie's source root and Miller's staged workspace root. No performance-only binding override is added.

## Consequences

An isolated or restored Miller home can recover authoritative registry membership from a valid local pointer. Direct CLI, warm-reader, and leader behavior become consistent. Bootstrap performs bounded validation before registry repair. Tests must prove mismatches cannot inject or rewrite registry lineage.

## Applies To

- `src/Miller.Indexing/Store/StoreFamilyResolver.cs`
- `src/Miller.Indexing/WorkspaceRegistry.cs`
- `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs`
- `src/Miller.Server/Hosting/IndexBootstrapService.cs`
- `scripts/perf-recovery.py`

## Future Agents

Do not add an environment variable or direct registry edit that bypasses pointer validation. Keep pointer adoption fail-closed and reuse the same family-read invariants as normal serving.
