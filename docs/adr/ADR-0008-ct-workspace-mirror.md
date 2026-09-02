# ADR-0008: Shared continuous-testing workspace mirror

- Status: accepted
- Date: 2026-09-02

## Decision

Continuous-testing providers that must execute from a project tree use the internal
`CtWorkspaceMirror` reconciler. Its policy is closed data: provider name, cache and mirror names,
excluded and build-owned entry names, the Git-barrier switch, and one of two integrity modes.
The reconciler owns traversal, containment, link and special-file checks, metadata preservation,
manifest ownership, cancellation, path limits, and no-follow size measurement.

The sbt adapter keeps its existing `SbtWorkspaceShadow.Sync` result and path contract. It selects
the strict-hash profile, excludes `.git`, `.miller`, and `target`, and creates the dependency cache
only after mirror validation succeeds.

The Godot adapter selects the metadata-fast-path profile. It mirrors only the directory containing
the selected `project.godot`, excludes source `.godot` and Miller output, preserves build-owned
mirror state, maps contained source paths, and keeps the project and isolated Godot home candidates
under the supervised CT cache. Its over-budget marker is outside the reapable project candidate,
so an unchanged source digest cannot trigger a repeated cold copy/import attempt.

## Alternatives rejected

- A second Godot-specific reconciler would duplicate reviewed safety code and allow provider drift.
- A full copy per generation would discard the warm project and import cache and regress unchanged CT.
- Running Godot in the source tree cannot be made safe by ignore files or cleanup because Godot writes
  import state as part of normal operation.
- A callback, filesystem abstraction, plugin policy, hardlink, junction, or public extension point
  would widen the trust boundary without a required caller.

## Consequences

The shared module is an internal seam and adds no MCP, CT database, or provider-interface surface.
Sbt retains strict repair of destination mutations. Godot trades byte hashing on unchanged metadata
for a deterministic metadata fast path; copying a changed file still hashes the source before and
after the copy. Both profiles report copied/updated/deleted entries, copied bytes, hash metrics,
source metadata digest, elapsed time, and candidate bytes.
