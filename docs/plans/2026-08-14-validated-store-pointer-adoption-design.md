# Validated Store Pointer Adoption Design

## Problem

The performance replay must serve an isolated copy of a large family store without changing the live workspace pointer. Direct CLI reads and warm-reader bootstrap already trust a valid `.miller/store.json`, but a lock-winning leader resolves only through the user-global registry. An isolated `MILLER_HOME` has no copied family/member lineage, so leader bootstrap ignores the valid staged pointer and creates a fresh family. That loses the incident history the replay exists to measure.

The replay also uses one `workspace` value for two different identities. Julie import must read the original source root recorded by the copied view, while Miller must run from a staged root whose own view and pointer match that staged root.

## Decision

### Miller bootstrap

When registry resolution has no usable binding, leader bootstrap may adopt the workspace's existing store pointer only when the entire binding is independently valid:

- pointer location and `workspace_root` match the canonical current root;
- store catalog family matches the pointer family;
- the selected view exists and records the same canonical root;
- `CURRENT`, generation manifest, coordinator, resolution base, and artifact identity pass the existing family read-session validation;
- root-replacement and lineage safety checks do not contradict adoption.

Successful adoption registers the validated family/member binding in the isolated registry and continues through the normal coordinator path. Invalid, stale, mismatched, or unreadable pointers fail visibly or follow the existing repair path without registry mutation. The pointer is evidence to validate, not an authority bypass.

### Replay harness

The harness gains a distinct source-root input. Julie rows use the original read-only source root plus a disposable family store and disposable supervision paths. Miller CLI/MCP rows use a staged workspace whose copied family contains a ready view bound to that staged canonical root and whose pointer names that view. A producer result that changes the view must be explicitly adopted in the staged pointer or the workload fails.

`workspace open` setup seeds or verifies the isolated binding through validated pointer adoption; it must not mint a new family. Mutating rows continue to receive fresh store copies. The live pointer and live family remain byte-stable.

## Rejected Alternatives

- A performance-only environment override would bypass registry/view authority and create a second untrusted binding path.
- Temporarily replacing the live workspace pointer could redirect concurrent sessions and violates the recovery safety boundary.
- Rebuilding a fresh family is supported behavior but discards the retained deltas and resolution history under investigation.
- Directly editing the isolated registry would couple the harness to private SQLite schema and bypass product validation.

## Architecture Quality

- **Affected modules:** `StoreFamilyResolver`, `StoreWorkspaceCoordinator`, `IndexBootstrapService`, `WorkspaceRegistry`, and `scripts/perf-recovery.py`.
- **Caller-facing interface:** the existing store pointer and replay CLI; no new MCP tool, public product CLI verb, or production environment override.
- **Depth/locality:** pointer validation and registry adoption stay inside the store coordinator; callers do not learn registry schema.
- **Test surface:** resolver/bootstrap behavior through existing host registration and workspace/read contracts; replay behavior through the harness CLI.
- **Risk:** medium because bootstrap authority changes, bounded by fail-closed validation and no-mutation tests.

## Acceptance Criteria

- [ ] A valid staged pointer with a ready root-matching view is adopted into an empty isolated registry by a lock-winning leader.
- [ ] Direct CLI, warm-reader, and leader paths select the same family/view.
- [ ] Family, view-root, store-root, generation, base, or root-identity mismatch refuses adoption without registry mutation.
- [ ] A normal registered workspace retains the existing registry-first behavior.
- [ ] Julie replay argv uses the original source root while Miller uses the staged root.
- [ ] Original source files, `.miller/store.json`, and live family facts remain unchanged.
- [ ] `workspace.open.no_change` reuses the copied family and does not mint a family.
- [ ] Linux focused tests pass; Windows path/casing/reparse behavior is covered in unit tests and later native acceptance.

