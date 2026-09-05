# M1 discovery boundary decision

M1 is in progress, not complete. Its typed runner/lifecycle work is independent of
this decision, but session/caller enforcement must not silently relax the plan.

## Conflict in the plan

The plan requires admission before any generation SQLite handle opens and prohibits
new producer wire fields. Existing discovery cannot satisfy both constraints:

- `StoreFamilyResolver.ReadCatalog` reads `store_meta.family_id` and
  `SELECT view_id, root FROM views` before selecting a family/view binding.
- `julie-extract store reader acquire` requires family, view and generation as inputs.
- The existing `StoreMaintenanceReport` includes aggregate counts and family/generation
  facts, but no view/root catalogue or writer binary version. It cannot replace this
  discovery read with the current wire contract.
- Writer eligibility checks must also work when the desired view does not yet exist,
  or before the writer has activated the reader catalogue and compatible writer floor.
  Requiring successful reader admission before that writer preflight creates a cycle.

Relevant sources: `src/Miller.Indexing/Store/StoreFamilyResolver.cs:ReadCatalog`,
`src/Miller.Indexing/Store/StoreArtifactVersionReader.cs:TryReadFamilyWriterFloor`,
`src/Miller.Indexing/Reads/FamilyStoreReadSession.cs:ReadFamilyBinaryVersion`, and
Julie `crates/julie-extract-cli/src/store/maintenance_report.rs:StoreMaintenanceReport`.
Producer `validate_reader_catalog` and `validate_reader_writer_floor` enforce the
admission prerequisites; they must not be bypassed by a same-PID exception.

## Amendment approved on 2026-09-05

The user approved the recommended metadata-only exception. M1 now records its exact scope. Session integration may proceed; release pin adoption remains separate.

Permit a narrowly named metadata-only bootstrap/discovery path for family/view/root
and compatibility facts. Use read-only bounded transactions, close before admission,
and treat the result as provisional. Revalidate the selected identity after acquiring
the registration. If discovery or validation races maintenance, refuse/retry safely;
never turn provisional metadata into served source facts.

Every actual family-store serving session, deferred fact-cache worker, bounded CLI
read and CT snapshot must still acquire/retain protection before opening its generation.
No symbols, relationships, source facts or query results may use the discovery path.
Existing WAL maintenance and producer mutation anchors are not serving sessions and
must remain distinct from reader registration.

The alternative is extending the producer-owned discovery contract. That is broader
cross-repository work and changes M1's explicit no-new-wire constraint.

## Other audit findings

Background fact warming outlives FamilyStoreReadSession. It needs a retained reference
to the same registration until its deferred connection closes. A second admission per
query is unnecessary. The typed handle is being given an idempotent retained-owner
token; Task 2 must wire it before scheduling and release it in a finally after the
returned shared task settles, including completed-task and failed-task paths.

Miller's `indexer.lock` is not the producer's coordinator writer lease or maintenance
intent. Do not remove Miller workspace serialization merely to acquire a reader.
Coordinator/tree-diff paths already close sessions before invoking producer mutations.

Producer admission's global retained sequence is not Miller's per-view freshness
cursor. Preserve that distinction when validating admitted manifests; do not substitute
the producer allocator high-water or global sequence for existing consumer revision
and level-stamp identities.

## Source build provenance

Julie source `3b3e5b6f03b724448df9012bb75224e99ca68f5d` built successfully in 35 seconds
with `cargo build --release --locked -j 4 -p julie-extract-cli`. The existing J1 target
cache was used. Binary reports 2.40.0; SHA-256
`8edb83508478bb8967675fd19590da830536b00ba9f10a1e6f2d3d0c8cb55b16`.
It was not installed into Miller `.tools`. Released pin remains 2.39.0 and M1 release
adoption remains pin-blocked, independently of this discovery decision.
