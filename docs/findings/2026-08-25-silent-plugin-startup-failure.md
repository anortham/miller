# Silent plugin startup failure on Windows (2026-08-25)

## Report

A user ran Miller in Claude Code on Windows. Miller failed to connect when the session launched. A `/mcp`
reconnect started Miller. `<workspace>/.miller/logs/miller-20260825.log` held **no line at all** for the
failed attempt.

Log evidence supplied by the user, `C:\source\Lab Handbook`:

| Time | Process | Fact |
|---|---|---|
| 07:37:27 | pid 26756 | miller **1.20.1**, client claude-code **2.1.238**, healthy session |
| 07:38:33 | pid 26756 | transport completed reading; that session's client exited |
| 07:38:33 → 07:39:13 | — | **40.5-second gap. Nothing logged. This is the failed attempt.** |
| 07:39:13 | pid 32068 | miller **1.22.0**, client claude-code **2.1.245**, healthy in 4 ms |

Both the Miller plugin version and the Claude Code client version changed across the gap.

## Why the log was empty

A plugin launch runs two processes, and neither could report a pre-server failure.

1. `bin/miller-plugin-launcher.cjs` wrote **no log file anywhere**. Its only output was
   `console.error(error.message)` on a thrown error.
2. `miller.exe` assigned `Log.Logger` partway down `Program.cs`. Everything above that line — the CLI branch,
   the workspace resolve, the sensitive-root guard, the logs-directory create — ran with the silent default
   logger. Everything below it ran with **no top-level catch** at all.

So a launcher that failed, a launcher that was killed, and a `miller.exe` that died in its first moments all
produced the same result: zero lines. The empty log was a design gap, not a symptom of the specific cause.

## The cold-download exposure (real, but not this incident's cause)

The launcher caches per version at `~/.miller/plugin-cache/<version>/<target>/package/`. A version bump is a
**cold download**, not an overwrite. Windows release archives measured live from GitHub:

| Version | `x86_64-pc-windows-msvc.zip` |
|---|---|
| 1.19.4 | 101,759,619 bytes |
| 1.20.1 | 100,762,258 bytes |
| 1.21.0 | 102,555,862 bytes |
| 1.22.0 | 103,211,562 bytes |

The v1.22.0 archive expands to 324,744,223 bytes in 62 files; four files carry 91% of the bytes
(`Miller.Dashboard.exe` 112.7 MB, `julie-extract.exe` 90.3 MB, `ggml-vulkan.dll` 57.7 MB, `miller.exe`
34.8 MB). Measured on a fast Linux desktop with no antivirus: SHA-256 of the archive 0.093 s, zip listing
0.009 s, extraction 1.6 s. Those steps are **not** the bottleneck. The cost is the user's link speed plus the
antivirus cost of writing 324.7 MB and first-running an unsigned 34.8 MB executable. Neither is known for
this machine.

All of that runs **before** `miller.exe` starts, inside Claude Code's MCP startup budget: `MCP_TIMEOUT`,
milliseconds, default `30000` — confirmed from the Claude Code env-vars reference and from a live local log
line, `{"debug":"Starting connection with timeout of 30000ms"}`. Claude Code does not auto-reconnect stdio
servers, so a missed window stays failed until the user reconnects by hand.

**This exposure did not fire here.** The next section shows the user's link ran at ~25 MB/s, which finishes
the archive in about four seconds. The size is a standing risk on a slow link, not this incident's cause.

## Root cause, settled from disk evidence

The user listed `~\.miller\plugin-cache\1.22.0\x86_64-pc-windows-msvc` after the incident. It settles the
question, and it **refutes the MCP_TIMEOUT explanation**.

| Path | Created | Last write | Bytes |
|---|---|---|---|
| `stage-29240-1787661513135\` | 07:38:33 | 07:38:33 | (orphan) |
| `downloads\…zip.tmp-29240-1787661513748` | 07:38:33 | **07:38:35** | **59,635,900** |
| `downloads\…zip` | 07:38:57 | 07:39:01 | 103,211,562 |
| `downloads\…zip.sha256` | 07:39:12 | 07:39:12 | 107 |
| `package\` | 07:39:12 | 07:39:12 | — |

The embedded epoch stamps decode to `12:38:33.135Z` (stage) and `12:38:33.748Z` (archive temp), i.e. local
07:38:33 — the same second the 1.20.1 process shut down. The plugin update had **not** pre-staged anything;
this was the first spawn at 1.22.0, so `ensureMillerPackage` found an empty cache. That part of the earlier
reading holds.

**The timeout reading does not.** The link is fast: attempt 1 wrote 59,635,900 bytes between 07:38:33 and
07:38:35, about **28 MB/s**, and the later complete download moved 103,211,562 bytes in four seconds, about
**25 MB/s**. A 103 MB archive on that link costs ~4 seconds. It was never close to a 30-second budget, and
antivirus never entered the picture.

What actually happened to attempt 1 (pid 29240): the body **stalled at 57.8%** and never wrote another byte.
Both leftovers survive — the `stage-` directory whose cleanup lives in a `finally`, and the `.tmp-` file whose
cleanup lives in a `.catch`. Neither ran, so the process was **terminated**, not thrown out of.

That stall was unbounded by design. The old `downloadFile` set no `timeout` on `https.get`, armed no idle
watchdog on the response body, and drained it with a bare `response.pipe(output)`. A body that simply stops
delivering therefore left the promise unsettled **forever**: no error, no exit code, no output. The client
waited on a launcher that would never finish or fail, then reported a connect failure. This is the most silent
failure shape there is, and it is the one the launcher had no defence against.

Three attempts ran inside the 40-second gap: 07:38:33 (stalled at 57.8%, killed), ~07:38:57 (downloaded the
full archive, which landed at its final cached name at 07:39:01), and 07:39:12 (re-fetched the sidecar, found
the cached archive, hashed, extracted, promoted `package\`). `miller.exe` started at 07:39:13.598.

Two claims made during triage did **not** survive, and are recorded so they are not repeated:

- **`MCP_TIMEOUT` was not the cause.** The measured link finishes the download in four seconds. The archive
  size raises the exposure but did not fire here. The documentation added for it is still correct and useful;
  it is simply not this incident's cause.
- **The 4 ms between `transport reading messages` and `Application started` does not prove a warm cache.**
  Both lines come from inside `miller.exe`, after the launcher spawned it.

One earlier correction is confirmed by the disk: the completed archive at 07:39:01 is exactly what made the
final attempt fast. `ensureDownloadedArchive` keeps and reuses a finished archive; only a kill *during* the
download loses bytes, because the temp name carries the process id and is never resumed.

## Fix shipped

The fix closes the silence rather than the single cause, because the silence is what made the cause
unknowable. See CLAUDE.md, "A launch may never fail silently".

- `bin/miller-plugin-launcher.cjs` writes `~/.miller/logs/launcher-<YYYYMMDD>.log` (honoring `MILLER_HOME`)
  and the same lines to stderr, naming every stage with download progress in MB/s. A cache miss says plainly
  that a large download is starting and names `MCP_TIMEOUT`. Retention is 14 days.
- **The stall that caused this incident can no longer hang the launcher.** `https.get` carries a connect
  timeout; a body that stops delivering is destroyed by an idle watchdog after 15 s — deliberately shorter
  than the client's 30 s start budget, so the launcher reports and retries while the client is still
  waiting; the body drains through `stream/promises.pipeline` so a dead body rejects instead of leaving the
  promise unsettled forever. A stalled or reset download is **retried up to three times**
  (`downloadWithRetry`), which on this user's 25 MB/s link would have recovered attempt 1 in seconds rather
  than failing the session. The archive rename retries `EPERM`/`EBUSY`/`EACCES`, a missing Windows `tar.exe`
  reports what to install, and the top-level catch prints the stack and calls `process.exit(1)`.
- **The version cache is bounded.** Each version installs into its own directory and nothing ever removed the
  old ones: ~430 MB per dead version (retained archive plus extracted package), forever. A dev box was
  measured carrying 855 MB across two versions abandoned three weeks earlier.
  `pruneOldCachedVersions` keeps the version being installed plus the one other most-recently-used, and runs
  only on a cache miss. Recency comes from a `.last-used` marker stamped on every launch, so a version a
  second client still uses survives; a locked version is reported, never retried.
- **Leftovers from a killed attempt are swept.** `sweepStaleInstallLeftovers` removes `stage-<pid>-<ms>`
  directories and `*.tmp-<pid>-<ms>` files older than 10 minutes, and logs what it removed. The age comes
  from the name's own epoch stamp, so a live sibling install is never touched. This incident stranded
  59,635,900 bytes plus an empty stage directory that nothing would ever have reclaimed.
- `Program.cs` tracks a `startupStage` and wraps the whole startup in one catch that calls
  `StartupFailureLog` and returns exit 70. That writer always reaches stderr, then appends a `role:startup`
  line to the first writable candidate of resolved-logs-dir → `<home>/.miller/logs` → temp. It never throws
  and never recreates a candidate whose parent is gone.
- `Serilog.Debugging.SelfLog` now reports to stderr, so a file sink that cannot open its file stops being
  silent.
- `StartupBreadcrumb` names the log directory on stderr unconditionally, so an empty workspace log is
  decisive: no breadcrumb means `miller` never started.
- `WorkspaceRootSafety.Normalize` skips a malformed forbidden entry; `MillerHome.Resolve` refuses an unrooted
  user profile by name instead of logging into the launch directory.
- `docs/install.md` gained "When the plugin fails to connect", naming all three logs and the `MCP_TIMEOUT`
  setting.

## Verification

- `scripts/test-plugin.sh` — 63 pass (14 new).
- `scripts/test.sh` — 8484 pass, 9 pre-existing platform skips.
- `dotnet build Miller.slnx -c Release` — 0 warnings, 0 errors.
- Forced a `create-logs-dir` failure by making `<workspace>/.miller` a file: exit 70, stdout empty, stage and
  stack on stderr, and the `role:startup` line appended to `~/.miller/logs/`.
- Ran the launcher against the real binary: stdout empty, every stage recorded.

## Not done

- **No release.** None of this reaches the affected user without a version bump and a published release, which
  needs explicit approval.
- Archive size is untouched. The non-AOT dashboard (112.7 MB) and the Vulkan library (57.7 MB) are the two
  largest items and the real long-term fix for the timeout exposure.
- **Splitting the dashboard into its own on-demand download.** Measured: it is 49.7 MB of the 113.2 MB
  archive (44%), and removing it would take the download to 64.4 MB with no .NET prerequisite and no startup
  cost. Deferred anyway, because the incident's cause was a stall rather than size, and a download triggered
  by `workspace dashboard` re-creates exactly the fetch-inside-a-user-action trap this work just closed for
  the main binary. Cache pruning reclaims more bytes for far less risk. Revisit when field launcher logs show
  cold installs genuinely running out of client budget.
- Making the build framework-dependent on .NET 10. Measured: 113.2 MB → 59.0 MB compressed, but only
  −10.1 MB once the dashboard is out, against a hard .NET 10 runtime prerequisite for every user and a
  36 ms → 281 ms startup regression that every one-shot CLI invocation pays.
- HTTP `Range` resume for a partial download. A retry from scratch costs ~4 s on a healthy link, so resume
  earns its complexity only for genuinely slow connections. Revisit if field logs show repeated stalls.
