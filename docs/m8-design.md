# M8 design — logging polish (debuggability for a multi-process product)

> Historical status: this milestone design is implementation history. The current logging contract is summarized
> in [`CLAUDE.md`](../CLAUDE.md): Miller uses one shared daily `.miller/logs/miller-<YYYYMMDD>` pair with
> `pid`/`role`/`cid` as line properties, not per-pid files or a startup log reaper.

Status: **historical implemented design**. This was grounded against the live code and is retained as
implementation history. Current logging behavior is the shared daily log pair described above. House style
matches [m5](m5-design.md)/[m6](m6-design.md)/[m7](m7-design.md). Confidence ~86.

## Goal

Make Miller's logs actually usable for debugging — especially the multi-process reality M3 introduced (one
leader + N readers). Five verified gaps, all additive (the call sites are already structured — message
templates with named properties, not string interpolation, so this is sinks + context + a few catch sites, not
a rewrite). This is also the substrate the M9 big-file/log-viewer tool will consume (gap #5).

## Verified gaps (checked against the live tree, 2026-05-30)

| # | Gap | Severity | Evidence |
|---|---|---|---|
| 1 | **All processes write ONE shared file** | High | `Program.cs:31` `Path.Combine(logsPath, "miller-.log")`, daily rolling, no `shared:` flag (default `shared:false` = exclusive lock) and no PID/role in the name. M3 runs a leader + N reader processes in the SAME `<root>/.miller/logs`. |
| 2 | **No correlation id** | High | grep for `correlat|CorrelationId|TraceId|Activity` = empty. The toolbox spec (L199) called for "a per-call correlation id"; never built. Can't follow one operation across log lines. |
| 3 | **julie's stderr tail not surfaced** (codes already are) | Low (revised) | CORRECTION of an earlier misread: `IndexerCore.cs:141-173` ALREADY logs the structured `ex.Errors` codes with a transient(Info)/abnormal(Error)/unexpected(Warn) split — that part is done well. What is NOT surfaced is `ex.StandardError` (julie's raw stderr text): default `Exception.ToString()` omits the custom property, so `{Exception}` never shows it. Residual = add a bounded stderr tail next to the codes. |
| 4 | **No runtime verbosity dial** | Medium | `Program.cs:28` `MinimumLevel.Information()` hardcoded; 1 `LogDebug`, 0 `LogTrace` in the tree. Must recompile to get debug detail. |
| 5 | **No machine-readable sink** | Low (synergy) | Only the human text template. A JSONL sink is parseable AND becomes the M9 log-viewer's first dogfood input. |

## Decisions

### D1 — Per-process log files (gap #1)
Each process writes its OWN file: `<root>/.miller/logs/miller-<role>-<pid>-.log` (Serilog appends the date before
`.log` with daily rolling). `<role>` is `leader`/`reader` IF known at startup — but leadership is won later
(IndexerService), so the **file name uses pid** (always known) and the **role is a log PROPERTY** (decision-2),
not part of the path. Final: `miller-<pid>-.log`. This removes the shared-file contention entirely (each process
owns its file) and keeps the multi-process story honest. Retain `retainedFileCountLimit` per file; add a small
cap so abandoned pid files are pruned (Serilog prunes per-file-prefix rolls; stale pid prefixes need a
startup sweep — D6).

Rejected alt: `shared:true` on one file. It works (Serilog's cross-process mutex) but interleaves all processes
into one stream with no per-process separation — the opposite of "organized for debugging," and slower.

> **SUPERSEDED 2026-05-31 — reverted to the rejected alt (`shared:true`, one daily pair).** In practice the
> per-pid scheme created two files per process *launch*; ordinary dogfooding (and Claude Code restarts) piled up
> dozens of `miller-<pid>-<date>.{log,jsonl}` quickly, which was the actual complaint. We now write ONE shared
> daily pair (`miller-<YYYYMMDD>.log`/`.jsonl`, Serilog `shared:true`). Per-process separation is preserved by
> the `pid` + `role` line PROPERTIES (already enriched on every line — see D2), so "which process wrote this" is
> still answerable without a file-per-process. The interleaving the original note worried about is sorted by
> filtering on `pid`/`cid` in the `.jsonl`. This also DELETES the D6 startup reaper and all per-pid file-name
> parsing (LogFileReaper) — retention is now just `retainedFileCountLimit` days on the two shared files. An
> upgraded workspace can delete leftover `miller-<pid>-*` files once; nothing recreates them.

### D2 — Correlation id threaded through every line of one operation (gap #2)
- Generate the id ONCE at the start of each `tools/call`, in the central `TelemetryCallToolFilter` (the single
  choke point every call already passes through).
- Reuse it as the telemetry row id (today `Record()` self-generates a `Guid.CreateVersion7()` at dispose — move
  that generation up to call-start and pass it to the scope), so **the same id ties the log lines to the ledger
  row**. One id, two homes.
- Push it into Serilog `LogContext.PushProperty("cid", id)` for the duration of the inner handler, so every log
  line emitted on that async flow (tool body, the readers it calls) carries `cid`. Add `cid` + `pid` + `role`
  to both output templates.
- Background/hosted-service logs (indexer, freshness) have no call cid — they get a stable component context
  (`SourceContext`) instead; that is fine (they are not per-call).

### D3 — Surface julie's stderr tail at the catch sites (gap #3, revised)
The codes are ALREADY logged at `IndexerCore.cs:141-173` (transient→Info, abnormal→Error, unexpected→Warn). This
decision only ADDS the missing raw-stderr tail and removes the duplication that adding it would create:
- A small pure helper `ExtractErrorLog.Describe(exception) -> (codes, stderrTail)` (bounded stderr length): for a
  `JulieExtractFailedException` it joins `Errors` codes (matching today's `string.Join(", ", e.Code)` exactly) and
  returns the last N chars of `StandardError` (ellipsis when truncated); for any other `JulieExtractException`
  (e.g. usage) it returns the stderr tail with `(n/a)` codes; for anything else `("(n/a)", "")`.
- Refactor the three existing catch branches in `ExecuteIsolated` to source `codes` from the helper (no
  behavior change to the existing codes/transient logic) and add `{ExtractStderrTail}` to each log template so
  julie's actual stderr is visible. Keep the existing Info/Error/Warn levels. This is the daemon-debugging payoff
  and the helper kills the inline-codes duplication.

### D4 — Runtime verbosity dial (gap #4)
- A Serilog `LoggingLevelSwitch` whose initial level is read from an env var `MILLER_LOG_LEVEL`
  (Verbose/Debug/Information/Warning/Error/Fatal; default Information; unknown value -> Information + a one-time
  warn). Wired via `MinimumLevel.ControlledBy(levelSwitch)`.
- So an operator debugging the daemon sets `MILLER_LOG_LEVEL=Debug` and relaunches — no recompile. (A live
  hot-reload of the switch is NOT in scope; env-at-startup covers the use case.)
- Add a handful of genuinely useful `LogDebug` lines on the hot paths this would illuminate (per-file extract
  outcome incl. revision; freshness poll observed-vs-built revision; leader election transitions) — debug-level
  so they cost nothing at the default level. No noise at Information.

### D5 — JSONL sink alongside the human log (gap #5)
- Add a SECOND file sink writing compact JSON lines (`Serilog.Formatting.Compact.CompactJsonFormatter`) to
  `<root>/.miller/logs/miller-<pid>-.jsonl` (daily rolling, same retention). The human template stays for eyeballs;
  the JSONL is for machines — and is the M9 log-viewer's first input (a Miller log read by Miller's own tool).
- Verify the package: `Serilog.Formatting.Compact` (add the NuGet ref). If it pulls weight we don't want, fall
  back to a hand-rolled JSON output template — but the formatter is the right tool; prefer it.

### D6 — Startup hygiene + seam
- A startup sweep prunes stale per-pid log files beyond a small keep-count/age (so `miller-<pid>-*.log/.jsonl`
  don't accumulate forever as pids churn) — a pure planner `LogFileReaper.Plan(existingFiles, keep, now) ->
  toDelete` (testable, no I/O) + a thin delete. Never deletes the current pid's files.
- Pure ↔ infra seam held: `ExtractErrorLog.Describe`, the `MILLER_LOG_LEVEL` parser, and `LogFileReaper.Plan`
  are pure + unit-tested; the sink config + the actual file delete + `LogContext` push are thin infra.

> **SUPERSEDED 2026-05-31 — the startup sweep is removed.** It existed only to bound per-pid file growth; with
> the shared daily pair (see the D1 note) there is nothing to sweep — `retainedFileCountLimit` days on the two
> files is the whole retention story. `LogFileReaper` (+ its tests) and all per-pid name parsing are deleted. The
> pure↔infra seam now covers `ExtractErrorLog.Describe` and the `MILLER_LOG_LEVEL` parser only.

## Components
- **Program.cs:** per-pid file path (D1); `LoggingLevelSwitch` from `MILLER_LOG_LEVEL` (D4); JSONL sink (D5);
  templates gain `cid`/`pid`/`role`; startup log-file reap (D6).
- **Telemetry/TelemetryCallToolFilter.cs:** generate cid at entry, `LogContext.PushProperty`, pass cid to the
  scope (D2).
- **Telemetry/TelemetryScope.cs + TelemetryLedger.Record:** accept the externally-supplied row id (cid) instead
  of self-generating (D2). (Keep a fallback self-generate when no cid supplied, for direct-Record callers/tests.)
- **Hosting/IndexerCore.cs (+ other extract catch sites):** structured julie-error logging via
  `ExtractErrorLog.Describe` (D3); a few `LogDebug` outcome lines (D4).
- **New pure helpers:** `Server/Logging/ExtractErrorLog.cs`, `Server/Logging/LogLevelParse.cs`,
  `Server/Logging/LogFileReaper.cs`.

## Test strategy
- **Pure unit (default suite):** `ExtractErrorLog.Describe` (a JulieExtractFailedException with codes+stderr ->
  joined codes + bounded tail; a generic exception -> message only; null-safe; stderr truncation boundary).
  `LogLevelParse` (each valid level; unknown -> Information; null/empty -> Information; case-insensitive).
  `LogFileReaper.Plan` (keeps current pid; keeps newest N; deletes older; ignores unrelated files).
- **Correlation (default suite):** a test that the filter pushes `cid` and the telemetry row id equals the cid
  pushed (reuse the SoftBudgetFilter end-to-end SDK harness pattern — assert the ledger row id matches the
  LogContext-observed cid for one call).
- **Sink config (default suite, fast file I/O like the TelemetryLedger tests):** logging to a temp dir writes
  BOTH a `.log` and a `.jsonl`; the JSONL line parses as JSON and carries `cid`/`pid`. Per-pid file name format
  asserted.
- No Scale tests needed (no julie binary involved) — but the existing Scale suite must stay green (the cid/id
  change touches the telemetry path).

## Implementation order (TDD by layer)
1. Pure helpers (`ExtractErrorLog`, `LogLevelParse`, `LogFileReaper`) + tests. 2. cid plumbing (filter ->
scope/ledger row id) + correlation test. 3. Program.cs sinks (per-pid, JSONL, level switch, templates, reap).
4. D3 catch-site wiring + D4 debug lines. 5. Full gate.

## Verify / exit
- Build 0/0; default suite green and < 10s; existing Scale suite still green.
- Two concurrent `miller` processes write SEPARATE pid log files (no contention).
- A `tools/call`'s log lines and its telemetry row share one `cid`.
- A forced extract failure logs julie's error codes + stderr tail, not just the message.
- `MILLER_LOG_LEVEL=Debug` yields the debug lines; default stays quiet.
- A `.jsonl` sink exists, parses, and carries `cid` — ready for M9 to consume.
- **Exit:** logs are organized, correlatable, machine-readable, and honest about multi-process — debuggable.

## Explicitly NOT in M8
- Live hot-reload of the level switch (env-at-startup only). A log-shipping/remote sink. OpenTelemetry export
  (could layer later on the structured base). The M9 log-viewer tool itself (separate milestone; this just
  produces its input).
