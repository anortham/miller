# WAL recurrence hardening verification

The three gaps from the post-restart finding are closed in local branches.
No main-branch merge, push, release, pin update, or running-server replacement
was performed. The changes supplement the previously merged performance fixes.

## Miller

Production commit `fdc8edcdcf821f8e29abd5ffd779f879657abbe3`, branch
`fix/wal-recurrence`, worktree `/home/murphy/source/miller/.worktrees/wal-recurrence`.

- `StoreWalCheckpoint.Maintain` discovers nonempty WALs even without an owed marker.
  It preserves debt on blocked or unsuccessful cleanup and reports before/after
  bytes, elapsed time, outcome and debt age. Repeated marks preserve the original age.
- Both coordinator refresh and resident idle maintenance consume the same report.
  No-change refresh works without a resident indexer. The existing idle loop checks
  at most once per 30 seconds, using its existing clock; no new timer or service.
- Logs warn when remaining debt is at least five minutes old, either WAL is at least
  256 MiB, measurements are unavailable, or checkpoint execution is skipped/failed.
  Smaller recent busy debt is logged at Information, not silently discarded.
- Existing health warnings expose overdue/oversized/unknown WAL evidence through
  assembled store facts, including dashboard health. Reads only inspect filesystem
  metadata; they do not checkpoint or create an owed marker. Renderers stay pure.
- Family lock waiting is now one second per database instead of 300 seconds.
  A held exclusive coordinator lock reproduced a wait beyond four seconds before
  the repair. The regression now completes with Busy while the lock remains held.
  This bounds SQLite lock waits, not storage I/O for folding an already-large WAL.

Regression tests cover markerless no-change refresh with a new coordinator instance,
repeated writes under a pinned snapshot, persisted age, eventual recovery while idle
connections remain open, retained data, warning thresholds, unavailable observations,
read-only health reporting, and the real resident drain/retry schedule.

## Standalone producer

Production commits `60ae102b8b3566fa660018c91782176cb6c11300` and
`0b7ef7cc91a9775d4d4b03a60b243a7cf466d1b9`, branch `fix/wal-recurrence`, worktree
`/home/murphy/source/julie-extractors/.worktrees/wal-recurrence`.
This branch continues the unmerged J1 branch at `ecd021c0`; it is not independent
of J1 and does not silently replace the original J1 worktree.

`store::dispatch` performs one nonfatal completion checkpoint after command-owned
connections and transactions have dropped. It covers import, update, delete,
from-artifact via import, reader mutations, and applied maintenance mutations.
Read-only operations and dry-runs are excluded. The helper resolves CURRENT after
the command, opens existing databases without CREATE, and consumes the checkpoint
busy result for both store.db and coord.db. Each database waits up to 250 ms on locks.

Busy/unavailable/remaining WAL diagnostics go to stderr, leaving committed stdout
reports and exit status unchanged. A later write, replay or no-change command
retries without a Miller marker. A failed command with an unavailable layout keeps
its original error report without a redundant stderr message. This distinction was
required by an existing maintenance JSON contract test, which was not weakened.

Real CLI tests hold both store and coordinator snapshots during two committed
writes, release the snapshots while keeping connections open, then prove replay
truncation and preservation of both request records and all three complete file
versions. Read-only maintenance leaves WAL sizes unchanged; a distinct unchanged
import subsequently clears them.

## Verification ledger

| Scope | Source revision | Result |
| --- | --- | --- |
| Miller affected Linux tests | code committed as fdc8edcd | 238 passed |
| Miller Release compilation | same production code, built through the final Scale gate | warnings treated as errors; passed |
| Miller fast Release suite | code committed as fdc8edcd | 9693 passed, 9 skipped; 37 seconds |
| Miller Scale suite | code committed as fdc8edcd | 204 passed, 24 skipped; 68 seconds |
| Miller Windows NTFS affected suite | fdc8edcd, exact full-SHA guest sync | 238 passed, none skipped; 18 seconds test time |
| Julie import contract, Linux | initial repair 60ae102b | 39 passed |
| Julie maintenance contract, Linux | final repair 0b7ef7cc | 19 passed |
| Julie WAL CLI regressions, Linux | final repair 0b7ef7cc | 3 passed; 1.24 seconds |
| Julie default tier, Linux | final repair 0b7ef7cc | passed; 89 seconds |
| Julie Windows NTFS CLI contracts | 0b7ef7cc, exact full-SHA guest sync | 38 import + 18 maintenance passed; 33.24 + 6.05 seconds |

The Windows targets compile fewer platform-specific tests than Linux. All three
new WAL CLI tests ran and passed on Windows. Windows logs are under
`/home/murphy/.local/share/win-test/logs/`; Miller log
`20260905T123601Z-miller-2571347.log`. The producer default-tier log is retained at
its worktree's `.miller/wal-default-gate.log`.

The first producer default run caught the redundant-stderr regression, then the
unchanged regression and full default gate passed after repair. The first Miller
fast run caught a new test returning a null maintenance report after an overlapping
build/edit window. Adding an explicit marker precondition and rebuilding the test
produced passing focused, full fast, Scale and Windows runs. Do not edit tests while
a build is compiling them. No existing assertion was removed or relaxed.

No external review was selected. Security scope: none declared. No parser facts,
language coverage, schemas, or public MCP tools changed.

## Remaining limits and integration

This prevents silent neglected cleanup, not all possible temporary WAL growth.
A reader can keep a snapshot pinned indefinitely, and a large transaction can
produce a large WAL before commit. The system warns, retains debt and retries;
it does not kill readers, delete live WALs, or claim that journal_size_limit is a
hard bound. A process that is not running has no maintenance timer. For a standalone
store, retry occurs on the next applicable command; Miller also retries through
normal refresh and a resident idle loop when present.

Integrate the Miller branch locally and rebuild/restart to activate its changes.
The producer fix requires a new producer binary. Because its branch includes J1,
release/pin adoption must be coordinated with the pending M1 reader-contract work;
do not blindly bump the Miller pin as part of this repair. No semantic-sidecar changes
were needed.

Both new worktrees remain for review and integration. Existing Miller tool-latency,
CT-provider and postrelease-audit worktrees were clean and merged; the dogfood
worktree retains its pre-existing untracked `.tools`. The unrelated preservation
branch remains unmerged. Original Julie J1 is clean and unmerged; its CT audit
worktree still contains its two pre-existing untracked docs. None was altered.
