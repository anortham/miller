# Avoid empty WAL checkpoint invalidation

The user approved this repair after the published CLI reproduced increasing
`data_version` without changes to store rows or modification time.

No architecture, schema, public API, or dependency change. Keep the existing
checkpoint entry points in both projects. Before TRUNCATE, check that this
database's WAL may contain frames. Missing, zero-byte, or header-only WALs have
nothing to checkpoint. Unknown metadata must retain the checkpoint attempt.
Miller must still reject unreadable/corrupt databases and preserve owed work.

Do not skip all reader-command store checkpoints: first-time floor activation
and previous interrupted writes can leave real store WAL work. Do not remove
the maintenance inspector's concurrent-write guard or pause resident processes.

Targets:

- Miller: `src/Miller.Indexing/Store/StoreWalCheckpoint.cs` and its tests.
- Julie: `crates/julie-extract-cli/src/store/completion.rs` and CLI WAL tests.

Acceptance:

- Repeated no-op checkpoints do not change an observer's data_version.
- Reader acquire/renew/release leave an unchanged store snapshot valid.
- Coordinator WAL writes are still checkpointed.
- Real store WAL writes, busy-reader debt and later retry remain covered.
- Corrupt, missing and unreadable cases keep honest outcomes.
- Focused RED/GREEN before branch gates; published binaries and live stores
  remain untouched. No release is authorized by this implementation request.
