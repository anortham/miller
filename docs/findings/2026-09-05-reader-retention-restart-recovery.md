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

## Published 2.40.1 qualification

The user approved the paired patch release and Miller pin adoption. Julie
`v2.40.1` published at 2026-09-05T22:07:52Z from
`1d424c2fcfde16d7d5df2b8686f35b9a1f9295b9`. Source CI `33993422701` and
release workflow `33994328326` passed. All four downloaded archive digests match
the live release assets and all embedded binaries match their packaged checksums.
Miller's ordinary restore fetched the published Linux URL without a source or
test-executable override. The previous 2.40.0 assets and tag are unchanged.

The new real-producer test
`LegacyStoreWithExpiredDeadClaimActivatesAndAdmitsTheOriginalSnapshot` first failed
with typed Busy on published 2.40.0. It recreates the legacy coordinator in a
disposable store using an actual exited child's identity, then exercises Miller's
public factory. It checks acquire-before-open, the original manifest and symbol,
failed orphan ownership, the permanent 2.40.0 writer floor, close-before-release,
and no remaining registration.

Its first 2.40.1 run got past admission and found a separate pin-qualification
omission. Julie's existing `initialize_store_database` sets `min_reader_version`
to the creating binary's version. Miller's independently qualified
`ReaderContractCapability` still read 2.40.0. That rejected newly created 2.40.1
stores. The capability is now explicitly 2.40.1, not automatically aliased to the
package pin. A future pin still requires qualification. The permanent producer
reader-registration writer floor remains 2.40.0; these are different values.

Added pure coverage accepting 2.40.1 and refusing 2.40.2, alongside the existing
2.40.0 acceptance and 2.41.0 refusal. Before the capability change, exactly the
new acceptance case failed. Afterward all four cases and the installed-producer
legacy recovery test passed, five tests total. The Linux fast gate passed with
9,867 passes, nine platform/availability skips, zero failures, 41 seconds.
The complete Linux Scale gate on published 2.40.1 passed with 217 passes,
24 toolchain/platform/opt-in skips, zero failures, 74 seconds. It includes the
native reader lifecycle and supported-language inventory/projection tests.

Windows full Release fast verification on exact candidate
`820359c53567f5de31eddafae262b56318e79793`, restored from the published Windows
archive on NTFS, passed with 9,841 passes, 35 platform/availability skips and zero
failures in 11 minutes 50 seconds. The effective command was `dotnet test -c
Release --no-build --filter Category!=Scale --blame-hang
--blame-hang-timeout 2m --logger console`. The collector reported all tests finished
and no hang sequence was needed. The earlier run was manually aborted after
5,928 passes because buffered output was mistaken for a stall; it is not counted
as passing. The retry's nested PowerShell quoting lost the optional verbose-log
redirect and emitted a separate `verbosity=normal` command error after the test
summary. The actual full test process completed successfully; no green suite was
rerun just to repair log formatting. These runner issues are not Miller MCP
latency evidence.

The separate Windows installed-producer gate ran
`RealProducerReaderRetentionScaleTests` and `ReaderRetentionLanguageScaleTests`:
13 passed, zero skips/failures, 98 seconds. This includes the legacy dead-request
upgrade and the supported-language reader inventory/projection checks.

The final Linux Release solution build passed with zero warnings/errors in
2.40 seconds. Later finding/checkpoint edits are documentation only.

## Integration boundary

Julie 2.40.1 is published. Miller's paired bootstrap retry, explicit reader
capability qualification, pin and regressions passed the Linux and Windows gates
on `820359c53567f5de31eddafae262b56318e79793`. The approved integration is a local
fast-forward of `fix/reader-admission-bootstrap-retry` into main, with only this
finding, the program status and checkpoints added after the tested candidate.
The running resident process still uses the earlier build and needs the user's
rebuild/restart after integration. Stored-data recovery alone is not proof of a
healthy resident restart. M3 remains unstarted. No semantic runtime or Miller
marketplace release is included.

## 2.40.2 writer-transaction follow-up

The subsequent resident restart did bootstrap successfully, but its forced
extractor-upgrade imports failed at Julie's `store_import_ensure_view` with
`database is locked`. During imports, reader admission also reported Busy. Idle
samples had no writer lease or queued/claimed request, so this was not established
as another abandoned-request incident. Serving a readable view did not discharge
the owed full-level upgrade.

The user supplied a producer fix, reviewed and published as
[julie-extract 2.40.2](https://github.com/anortham/julie-extractors/releases/tag/v2.40.2)
at 2026-09-06T00:24:47Z. Tagged source is
`2bf79f26b79e9bef597b0909b374614514a1ac3a`; source CI `33999763883` and release
workflow `34000602133` passed. `StoreWriterConnection.transaction()` now reserves
the writer with `Immediate` before a quantum reads. Previously method resolution
through `DerefMut` selected rusqlite's deferred transaction. The regression fails
on the old implementation because a competing write succeeds, and passes on the
fix because the quantum retains writer ownership and completes.

Two preliminary explanatory claims were corrected during review. Pinned rusqlite
0.40.0 already sets a five-second busy timeout; the explicit timeout stabilizes
policy rather than changing a zero default. The final regression uses a competing
writer, not a checkpoint. The exact live competing connection remains unproven.
Neither the release nor passing fixture tests alone certify live upgrade recovery.
The producer's [release evidence](../../../julie-extractors/docs/release-evidence/2026-09-06-v2-40-2-release.md)
records the review boundaries and source/native gates.

Miller adopts all four actual release archive hashes and explicitly qualifies its
reader capability at 2.40.2. Reader-floor tests retain 2.40.0/2.40.1 acceptance,
add 2.40.2 acceptance, and reject 2.40.3/2.41.0. The new acceptance failed before
the capability change; all five cases then passed. Normal Linux restore verified
the public archive and executable; the installed reader/retention/all-language
focused scope passed 67 tests with zero skips/failures.

Final adoption candidate `eb93670fca852a7d86b30a8cf9e21545a92aa631` passed:

- Linux fast: 9,868 passed, nine expected skips, zero failures, 42 seconds.
- Linux full Scale: 217 passed, 24 expected skips, zero failures, 75 seconds.
- Release solution build: zero warnings/errors, 23.99 seconds.
- Windows NTFS after normal public-package restore: 99 passed, two Unix-only
  restore-script skips, zero failures, 95 seconds. Scope included real producer
  reader retention, all-language projection, session retention, pin/schema and
  CLI capability tests. The unrelated Windows full suite was not repeated for
  this pin/capability-only consumer change.
- Windows executable SHA-256:
  `b16c9697b351a7039c8da5e05c19066495f6f71e9f06e1be8a7dbd2e9ef7142c`.
- Linux executable SHA-256:
  `7fa9be4456a84571c1fd0c5451a415b3df3b254ee78e74a8267c1cfd33be7d01`.

Only finding/program/checkpoint documentation follows the tested candidate before
the approved local fast-forward. No production behavior beyond explicit reader
capability qualification changes in Miller.

Full resident verification remains a post-rebuild/restart gate: observe completion
of the owed full extraction, discharge of the failure journal, and no recurring
import lock failure. No semantic runtime or Miller marketplace release is needed
for this pin adoption; M2-M5 remain unstarted.
