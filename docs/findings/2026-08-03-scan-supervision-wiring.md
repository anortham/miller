# 2026-08-03 — Scan supervision wiring (W4/W5/W6), verified end to end

The fleet-safety plan's three extractor-dependent workstreams, landed against
`julie-extract` 2.22.0. This records what was verified, the three places the
implementation deviates from the plan, and the one hazard that survives.

## What Miller now sends

Every `scan` argv carries three paths resolved from the ARTIFACT's directory by
[`ExtractSupervisionPolicy`](../../src/Miller.Indexing/ExtractSupervision.cs) —
not the workspace root, so a full rebuild into `symbols.db.rebuild` and a
cross-workspace refresh each supervise into the workspace they are scanning:

```
--spool-dir      <workspace>/.miller/spool
--progress-file  <workspace>/.miller/scan.progress
--parent-pid     <Miller's pid>
```

`MILLER_EXTRACT_SUPERVISION=off` sends the pre-2.22.0 argv. A test asserts it is
byte-identical to what `BuildScanArgs` produced before the flags existed, rather
than merely equivalent.

## Verified against the real binary

A locally built `julie-extract 2.22.0` run with Miller's exact argv on a scratch
workspace:

| Check | Result |
|---|---|
| Report | `status: ok`, zero errors, zero warnings |
| `spool_dir_excluded` on `.miller/spool` | **not raised** — the directory holds only spools, which is what the warning distinguishes |
| Spool directory after the scan | empty (julie-extract's own guard removed its spool) |
| Progress file | 7 records covering all six phases (`existing_artifact` → `artifact_write`) |
| Progress file / spool dir indexed as source | no — neither appears in `files` |
| Rescan on the same progress path | truncated and rewritten, as the contract specifies |
| Hard link `evil.progress` → a 1,110,016-byte artifact | refused `invalid_path`, **artifact byte-identical afterwards** |

That last row is the release-critical one: it is the convergent critical finding
from the cross-model review (the progress file was truncated before identity was
proven), proven fixed against a real multi-hundred-KB artifact rather than only in
a unit test.

## Three deviations from the plan, and why

**1. `.miller/spool`, not `.miller/tmp`, and the progress file is a SIBLING of it.**
julie-extract raises `spool_dir_excluded` whenever the spool directory holds
anything that is not a spool or a sentinel, because such a directory is excluded
from the walk and would silently swallow source. A progress file living inside it
would raise that warning on every scan forever — which is how a warning channel
stops being read, and it would bury the `--spool-dir $ROOT/src` case the warning
exists for.

**2. A fixed `scan.progress`, not a nonce name deleted in `finally`.**
The progress-file v1 contract is written for exactly this consumer shape: it
specifies truncate-on-new-scan, and states that a length DECREASE must be read as
a fresh baseline AND as progress, precisely because the named supervisor reuses
one path per workspace. A fixed path also leaks nothing when Miller is killed
mid-scan — a nonce name leaks one file per kill, since the spool reaper only ever
removes sentinel-backed spool names — and it leaves a readable post-mortem of
where a killed scan stopped, which the kill message now quotes.

**3. Miller does not delete the child's spool after exit.**
The plan had Miller remove the exact child PID's spool files. julie-extract's own
guard removes the spool on every exit path including early returns, and its reaper
covers hard kills by advisory lock. A Miller-side delete keyed on the child pid
would be the pid-probe design julie-extractors explicitly rejected, and on a
shared spool directory it could remove a live sibling worktree's spool.

## How the stall signal changed

`ProgressStamp` SUMS the heartbeat's length with the artifact bytes and the
child's output lines. It does not replace them, and that is deliberate: a progress
file that cannot be written degrades the signal to the pre-2.22.0 one rather than
to nothing, which is the mitigation for the reviewer finding that a permanently
failing progress sink is silent on julie-extract's side.

A length DECREASE registers as progress with no special case, because the policy
compares stamps for INEQUALITY rather than for growth.

Before this, the stamp was blind to the whole extraction/spool phase — the
artifact is not touched at all until near the end — so W10's healthy 61.3-minute
74k-file rebuild looked identical to a wedged process for minutes at a time.

## Surviving hazard: `--parent-pid` is Unix-only

`std` exposes no Windows counterpart for `parent_id`, so julie-extract accepts and
ignores the flag there. Windows containment is Miller's job object
([`WindowsKillOnCloseJob`](../../src/Miller.Indexing/WindowsKillOnCloseJob.cs),
renamed from `WindowsBrokerJob` and moved out of `Semantic/` because two
subsystems now use it). Every julie-extract spawn is attached for the life of the
`Run` call and the handle is disposed only after the child has exited, so it can
never be the thing that kills a healthy scan.

That path is compile-verified and shares its implementation with the semantic
broker's job, which has shipped. It is **not executed on this machine or in CI** —
both are POSIX. A Windows regression here would surface as a julie-extract that
outlives a killed Miller, i.e. as the pre-2.22.0 behavior, not as a new failure.
