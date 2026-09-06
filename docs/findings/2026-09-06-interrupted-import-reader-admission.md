# Interrupted first import blocks family-store admission

## Observed failure

On 2026-09-06 at 01:02:49 UTC, the MCP connection for the
`fact-cache-resource-accounting` worktree closed during its first import. Miller
shut down its extractor child. The importer had committed progress to the shared
store but had not reconciled the coordinator watermark:

- `store.db`: `MAX(store_log.sequence) = 84654`
- `coord.db`: `family_allocator_marks['store_log', ''].high_water = 84653`
- Import request `7098b9982f6e4270999e0049d5a3a699` remained claimed by the dead
  `cli-3187286`, with an expired writer lease.
- The worktree view existed, but `current_generation` was NULL.

Producer reader admission correctly detected the watermark inconsistency and
returned `StaleSnapshot`. Because the served log sequence is family-wide, this
also refused new readers of the previously published main view. On worktree
restart, Miller mistook the existing view row for a published view and attempted
reader admission before it could resume importing.

This is a different failure from the deferred-write-transaction race repaired
in julie-extract 2.40.2. It occurred with that released binary.

## Repairs

### Producer: recover durable watermark lag during admission

The Julie correction is commit `ddf66d4c`, merged and released in 2.40.3.
After existing snapshot, generation, manifest, writer, and maintenance
validation, admission monotonically advances an existing log watermark to the
durable sequence it observed. The update and reader registration commit in the
same coordinator transaction.

The store connection remains query-only. Missing allocator metadata still
refuses admission. There is no retention bypass, writer lease acquisition,
migration, full allocator scan, or request-state repair on this path. The next
writer retains responsibility for reconciling other allocators and requests.

### Consumer: distinguish a view row from a published view

`StoreFamilyResolver` now includes `current_generation` in its short catalog
snapshot and returns a Planned binding for an unpublished row. It preserves the
view identity and validates the root before choosing recovery.

`FamilyStoreReadSession.HasViewForImportPreflight` makes the same distinction.
Otherwise `StoreWorkspaceCoordinator.ReadState` would still try reader admission
before importing a Planned view. An unpublished row under the wrong root still
fails rather than being silently treated as absent.

Both checks are metadata-only planning. A published candidate still requires
normal producer retention admission before serving data.

## Live recovery and verification

Replaying the original import with its original idempotency key and scan controls
through the released CLI reconciled the watermark. Main Miller search then
reported `freshness: ready`. That replay published L1 and safely failed
`changed_between_waves`: source files had changed since the original request.

A new full import completed L1/L2/L3 and published worktree manifest generation 2.
Miller workspace refresh repaired the lexical sidecars; a lexical symbol search
on the worktree reported `freshness: ready` at revision 85112. Vector convergence
still required a resident worktree leader.

Main had accumulated scan-failure backoff during the outage. An explicit Miller
refresh cleared that journal through the normal success path. Final main status
reported fresh, with current search/content sidecars and ready vectors.

Recovery used normal producer and Miller operations. No database rows were
manually edited, no WAL/database files were deleted, no reader was bypassed, and
no resident process was killed. Miller now pins the published 2.40.3 correction.
Existing processes using 2.40.2 still need a rebuild and restart to use it.

Regression coverage includes the previously missed state where a real producer
schema contains a view row but no published generation, as well as the absent-row
case. Producer coverage includes durable lag, ahead watermarks, uncommitted writer
rows, missing metadata, and rejected generation/manifest changes without partial
coordinator mutation.

The broad consumer test run also exposed a three-second wait in
`BackgroundWarmKeepsTheSamePinUntilItsConnectionCloses`. The test passed when run
alone; its synchronous start barrier could block a thread-pool worker needed by
the background load. The barrier now awaits a `TaskCompletionSource` with the
same timeout and unchanged ownership assertions. No production timing was
changed.

## Release adoption

Julie 2.40.3 was published from `e8118a11` after source CI, local release gates,
and 130 Windows NTFS contract tests passed. All four downloaded archives matched
their public SHA-256 digests and packaged executable checksums. Miller's public
Linux restore reported 2.40.3; no source binary override was used.

Fresh stores use their creator version as their reader floor. The existing
2.40.2 capability ceiling correctly refused a new 2.40.3 fixture before the
Miller capability update. The explicit ceiling now accepts 2.40.3 and still
refuses 2.40.4 and 2.41.0. The 53-test focused qualification, including real
producer retention and both unpublished-view bootstrap cases, passed.

Final public-package qualification passed: Release build with zero warnings and
errors, 9,923 fast tests with nine skips, and 218 Scale tests with 24 skips.
The merged plugin suite previously passed all 82 tests. The notice-file version
check caught an outdated pin label during qualification; that label was corrected
before the final green run.

Published package evidence is in
[Julie's release record](https://github.com/anortham/julie-extractors/blob/main/docs/release-evidence/2026-09-06-v2-40-3-release.md).
