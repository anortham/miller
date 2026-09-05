# Reader-retention upgrade failure after restart

The first live restart after M1 exposed an upgrade case absent from its initial
qualification fixtures. The rebuilt Miller and published extractor were correct:
Miller `1.27.2+1ec6eda86831`, julie-extract `2.40.0`. Both new resident Miller
processes failed bootstrap with a typed reader-admission `Busy` refusal.

## Cause

The existing Miller family `a271f2bd-7368-4da6-b5aa-24ffad69fb1f` still had the
pre-reader coordinator catalog, writer floor 2.31.3 and binary version 2.39.0.
Request `6a3c263d1a0e46c9bfa3976e2a675ab3` was an update left claimed by
`cli-2841932`, whose process had exited. Its writer lease expired at
1788639046595; its request deadline expired later, at 1788639341383. A dead
requester does not authorize cleanup before that request deadline.

After expiry, first-reader floor activation still returned Busy forever.
`MaintenanceExecutor::activate_reader_writer_floor` called the shared fence
acquisition with stale-request reaping disabled. The fence refused any claimed
request. Normal maintenance already used transactional dead-requester cleanup,
but the new activation path skipped it.

Miller added a second liveness problem. `IndexBootstrapService.MarkBootstrapFailed`
retried scan-admission timeouts and rollback failures, but treated the new
`FamilyStoreReadException` wrapping a reader Busy refusal as terminal. Even a
normal live-writer conflict clearing later could leave the resident host unbound.

## Live recovery

No database rows or lease files were edited manually, and no readers were killed.
The published producer performed a normal import with the existing family/view,
four jobs, workspace-local spool/progress files and parent supervision. Its normal
request recovery marked the dead request failed with `coordinator_requester_dead`.
The import committed and reused manifest 3660, hash
`0cccac1ae02317d6662f085cd6577e367cdc8152201be85705b270cc6d6b8601`.
The progress record ended after 58,251 ms. Reader admission then activated the
catalog and permanent 2.40.0 writer floor successfully.

Status afterward reported revision 83962, 165,079 symbols, and current lexical and
content sidecars. Binary metadata and writer floor both read 2.40.0. This recovered
the stored data and on-demand reads; it did not replace the running resident
bootstrap code. The semantic broker remained unstarted and the old leader identity
was stale, so this was not claimed as a fully healthy resident restart.

## Fixes

- Julie `fix/reader-floor-orphan-recovery`, implementation `1e19b4cc`, in the reused
  `.worktrees/reader-retention-contract` directory. Activation now uses the existing
  fenced cleanup. The existing deadline and liveness policy remains authoritative;
  it does not reap live claimants/requesters, unknown identities, or requests with
  unexpired/absent deadlines. A live writer rolls back attempted cleanup together
  with the refused activation. No schema, floor-version or extraction change.
- Miller `fix/reader-admission-bootstrap-retry`, implementation `ff37b93e`, in the
  reused `.worktrees/reader-retention-integration` directory. Only a typed Busy
  admission with `MayHaveAcquired=false` uses the existing jittered, root- and
  generation-guarded bootstrap retry. Incompatible, invalid report, unknown reader
  identity, ambiguous acquire and message-only "busy" remain terminal. No new
  scheduler, public API, fallback or pin change.

## Verification

- Producer regression: one expected `MaintenanceBusy` failure and three passes
  before the fix, then all four activation tests passed. New coverage includes a
  real exited child process and six preservation/rollback scenarios.
- Producer full affected targets: registration 35 passed plus one old-binary opt-in
  skip; maintenance 39 passed plus one helper ignore. Formatting and whitespace
  checks passed.
- Windows producer activation scope at full commit
  `1e19b4ccd2c286874a48145e2611e21a5f2f0e51`: four passed, zero skips/failures,
  5.24 seconds, through `win-test` on NTFS.
- Miller regression: one expected failure and five passes before the fix. Focused
  bootstrap/session scope after the fix: 139 passed in six seconds.
- Miller full Linux Release fast suite: 9,865 passed, nine skips, zero failures,
  39 seconds. Full installed Scale: 216 passed, 24 skips, zero failures, 82 seconds.
- Windows Miller bootstrap scope at
  `ff37b93ee181089fd5ce66a723935921d760b2a4`: 87 passed, one expected symlink skip,
  zero failures, five seconds. Command through `win-test`: `dotnet test -c Release
  --filter 'FullyQualifiedName~BootstrapAdmissionRetryTests|FullyQualifiedName~IndexBootstrapServiceTests'`.
- Final `dotnet build Miller.slnx -c Release --no-restore` passed with zero
  warnings/errors in 18.59 seconds. Later finding/checkpoint changes are docs-only.

## Integration boundary

The initial 2.40.0 release and its checksums are not overwritten. These follow-up
fixes are isolated and are not in either project's main or released binaries yet.
Shipping automatic recovery for other pre-reader stores requires a producer patch
release and Miller pin adoption as well as the bootstrap fix. That additional
publication decision is separate from the completed 2.40.0 release. M3 remains
unstarted while this rollout correction is handled.
