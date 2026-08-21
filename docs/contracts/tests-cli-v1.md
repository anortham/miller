# Tests CLI JSON v1

Status: active Miller CLI JSON contract.

Commands:

```bash
miller tests status --json [--workspace-id SELECTOR] [--workspace DIR]
miller tests failures --json [--limit N] [--offset N] [--workspace-id SELECTOR] [--workspace DIR]
miller tests run --json [--wait] [--workspace-id SELECTOR] [--workspace DIR]
miller tests enable [--project PATH] [--json]
miller tests disable [--project PATH] [--json]
miller tests serve [--json]
miller tests stop [--json]
```

Miller owns continuous-test execution and verdicts. `tests status` is a cheap honest read: it
never creates `ct.db`, never creates `.miller/ct/`, and never starts the daemon. Start is
explicit (`tests serve` / MCP `start`).

`capabilities --json` advertises this contract as `tests_status` (`schema_version` 1) and lists
`tests status --json` plus `tests run --json` in `json_commands`.

## `tests status --json`

```json
{
  "schema_version": 1,
  "enabled": false,
  "kill_switch": false,
  "projects": [],
  "daemon": {
    "state": "stopped",
    "reason": "stopped",
    "running": false,
    "paused": false,
    "activity": "idle",
    "run": null
  },
  "verdict": "unknown",
  "selected": null,
  "stale_count": 0,
  "selected_count": 0,
  "last_run": null,
  "budget_holder": null
}
```

### Required fields

| Field | Type | Meaning |
|---|---|---|
| `schema_version` | number | This contract version. Currently `1`. |
| `enabled` | bool | Workspace is opted in (`<workspace>/.miller/ct.enabled`, or inherited from the main checkout on a linked worktree — see "Worktrees" below) or has enabled project rows. |
| `kill_switch` | bool | `true` when `MILLER_CT=off` (also `0`/`false`/`no`). |
| `projects` | array | **Enabled** projects only. Empty on a never-enabled workspace. |
| `daemon.state` | string | `running`, `paused`, or `stopped`. |
| `daemon.reason` | string | Why the daemon is in that state. Wording is not a contract. |
| `daemon.running` | bool | `true` only when `state` is `running`. |
| `daemon.paused` | bool | `true` only when `state` is `paused`. |
| `daemon.activity` | string | `idle`, `queued`, or `executing`. What the daemon is DOING, which the lifecycle state does not answer: a `running` daemon may have nothing to do, and a `paused` one may still hold accepted work. |
| `daemon.run` | object \| null | The run in flight, or `null`. |
| `daemon.run.project_path` | string | The project the daemon is executing. |
| `daemon.run.run_id` | string | The run's id in `ct.db`. |
| `daemon.run.selected_case_count` | number | Cases this run selected. |
| `daemon.run.started_at` | string | ISO-8601 UTC start time of the run. |
| `daemon.run.child` | string | `starting`, `active`, `quiet`, or `stalled` — the daemon's own reading of how lively the test process is, so a reader can separate a slow suite from a wedged one without comparing timestamps. `stalled` means the silence bound has passed and the kill is due; it is never reported when `MILLER_CT_STALL_TIMEOUT=off`, because nothing will act on it. |
| `verdict` | string | Aggregate verdict: `green`, `red`, `partial`, or `unknown`. |
| `selected` | object \| null | The LIVE index freshness key the verdict was judged at, or `null` when no readable index exists. It is never derived from stored `ct.db` rows. |
| `selected.index_identity` | string | The index generation identity: `ctgen1:store:<family>:<view>:<generation>` (family store mode) or `ctgen1:artifact:<id>:<hash-algorithm>` (legacy artifact mode). Present when `selected` is an object. See "Freshness key" below. |
| `selected.revision` | number | The live index revision counter at that identity. Present when `selected` is an object. |
| `stale_count` | number | Cases that need a run to be green at `selected` — watermark-aware: a green case whose fresh watermark covers the live key is NOT stale. When `selected` is `null`, falls back to the rows the store itself marked `stale`. `0` when the sidecar is absent. |
| `selected_count` | number | Stored case rows. `0` when the sidecar is absent. |
| `last_run` | string \| null | Latest `test_runs` timestamp, or `null`. |
| `budget_holder` | object \| null | User-global execution-budget owner, or `null` when idle. |

`projects[]` rows:

| Field | Type | Meaning |
|---|---|---|
| `id` | string | Stable project id (`ct-project:` + workspace-relative path). |
| `project_path` | string | Absolute project file path. |
| `framework` | string \| null | Provider framework (`xunit`, `nunit`, `mstest`, `dotnet`, `cargo`, `vitest`, `jest`, `pytest`, …). |
| `command` | string \| null | Optional whole-command override. Unused for dotnet projects. |
| `enabled` | bool | Always `true` in status output (disabled rows are omitted). |
| `exclude_traits` | string[] | Trait exclusions (`Name=Value`). |

`budget_holder` when present:

| Field | Type | Meaning |
|---|---|---|
| `pid` | number | Recorded holder process id. |
| `workspace_root` | string | Workspace that holds the execution budget. |
| `reason` | string | Why the budget was taken, for example `run`. |

A never-enabled workspace reports `enabled: false`, empty `projects`, `daemon.state: "stopped"`,
`verdict: "unknown"`, and null `selected` / `last_run` / `budget_holder`. The command creates no
files. `selected` is also `null` — and the verdict honest `unknown` — whenever the workspace has
no readable live index, whatever `ct.db` holds.

Under the kill switch (`MILLER_CT=off`, also `0`/`false`/`no`), status is a zero-WORK
short-circuit: it opens no `ct.db`, reads no live index, and reads no daemon-status or budget
file. The payload is the never-enabled shape above with `kill_switch: true`,
`daemon.reason: "disabled"`, and `daemon.activity: "idle"` — whatever state the workspace holds
on disk.

## Freshness key

CT freshness is the composite `(index_identity, revision)` taken from the live
`WorkspaceReadSnapshot` (`IndexGenerationIdentity` + the index revision; single construction path
`CtIndexCursor.FromSnapshot`). The identity is generation-scale on purpose:

- It does NOT change on routine index writes. The store log sequence, the revision counter, the
  manifest hash, and the manifest generation number are all excluded from it.
- It DOES change on every event that can restart or reuse the revision counter: a generation
  promotion, a view replan, a family recreate, an extractor upgrade / schema heal. An identity
  change makes EVERY stored result stale — the rebuild fail-safe.

Within one identity, a revision advance moves a per-case fresh watermark instead of staling
everything (`ContinuousTestStore.ApplyRevisionAdvance`, one transaction, staleness first):
currently fresh GREEN cases the change cannot reach carry forward to the new revision; impacted
cases go stale and lose their watermark rows; red and skipped results never ride the watermark; a
case whose reachability is unknown reads stale. A run executes the stale set (the impacted set
plus the already-owed backlog) as an explicit test-ID list. Truncated, degraded, or unavailable
impact data yields the Unknown outcome: everything goes stale and NOTHING executes — never a
whole-suite fallback. Green requires complete results at the selected composite key.

## Auto-run debounce

The daemon debounces automatic runs on the trailing edge: each newly observed change restarts the
timer, and the run starts after a quiet period. Changes that arrive during a run queue a follow-up
run; they never kill a healthy run in flight.

- Default: **2 seconds**.
- Override with `MILLER_CT_DEBOUNCE`: whole or decimal seconds. `0` means run immediately.
- Invalid, negative, or absurd (over 3600) values fall back to the default rather than wedging or
  disabling the auto-run loop.

## Enablement

`miller tests enable` discovers test projects through `ContinuousTestProjectInventory` and writes
per-project rows to `<workspace>/.miller/ct.db` (path, framework, command, exclusions). It also
writes `.miller/ct.enabled`. `--project PATH` scopes one file. `disable` mirrors: `--project`
disables one row; omitting it disables every stored row and removes the opt-in marker when none
remain enabled.

Discovery accepts a dotnet project only on a real test signal: an xunit/NUnit/MSTest reference,
`Microsoft.NET.Test.Sdk`, `Microsoft.NET.Sdk.Test`, or `Microsoft.Testing.Platform`. A test-like
file name alone does not qualify. When the project sets `VSTestTestCaseFilter` to a pure
conjunction of `Name!=Value` terms (for example `Category!=Scale`), enable seeds the row's
`exclude_traits` with the matching `Name=Value` exclusions, so a continuous run honors the same
default suite as a bare `dotnet test`. Any other filter shape seeds nothing. Re-running enable
refreshes stored exclusions.

Enable and disable do not start the daemon.

## `tests run --json`

| Field | Type | Meaning |
|---|---|---|
| `execution` | string | `daemon` when a live daemon holds the lease; otherwise `foreground_one_shot`. |
| `verdict` | string | `green`, `red`, `partial`, or `unknown`. |
| `reason` | string \| null | Channel or one-shot reason. |
| `waited` | bool | `true` when `--wait` was supplied. |
| `paused` | bool | `true` when another workspace held the user-global execution budget, so this run executed nothing. |
| `selected` | object \| null | Same shape as status `selected`. |

A live daemon receives `run` on the file command channel. With no daemon, Miller runs a foreground
one-shot in the calling process.

`--wait` waits for the daemon to FINISH the accepted run, then reports whatever verdict is true at
that moment. It does not wait for a verdict VALUE: accepting a run marks the selected cases stale,
which makes the verdict `partial` immediately, so a wait on the value returned within milliseconds
and reported a mid-run answer as the result. The wait tests `daemon.activity` instead, and ends on
the first of four bounded conditions — the daemon goes idle, it stops, its lease dies, or a limit
expires. Queued work that another workspace's execution budget is blocking is waited on for 30
seconds and then reported as still queued, rather than holding the caller for the whole timeout.

At most one workspace executes tests at a time. When another workspace already holds that budget,
this run executes NOTHING and reports `paused: true`, `verdict: "unknown"`, `waited: false`,
`selected: null`, and exit code `0` — a held budget is a deferral, not a failure. The verdict is
`unknown` rather than the stored one because a run that executed nothing holds no results at the
selected key, and green requires complete results at that key. `selected` is `null` because the
key is the LIVE index cursor and a total deferral opens no index at all. Retry the run once the
holding workspace finishes.

## `tests failures --json`

One page of the red cases, ordered by test-case id so paging is stable between calls.

```json
{
  "failures": [
    {
      "test_case_id": "xunit:Sample.Tests.Adds",
      "state": "red",
      "index_identity": "ctgen1:store:abc:view-1:gen-3",
      "revision": 42,
      "failure_summary": "Assert.Equal() Failure"
    }
  ],
  "truncated": 0,
  "total": 21,
  "offset": 20
}
```

The example above is the LAST page: 21 red cases, 20 skipped, one row returned, nothing after it.

| Field | Type | Meaning |
|---|---|---|
| `failures` | array | This page of red cases. |
| `truncated` | number | Red cases remaining AFTER this page. |
| `total` | number | All red cases at the current key. |
| `offset` | number | Cases skipped before this page. |

`--limit` defaults to 20 and is clamped to 1-200; `--offset` defaults to 0. The ceiling is a page
size, not the end of the list — `--offset` reaches everything past it. Compact output names the next
offset (`truncated: 10 (next: offset=20)`) so a reader can ask for the rest.

## `tests serve` / `tests stop`

`serve` is the only start path: `CtDaemonLauncher.SpawnDetached` launches `miller ct-daemon`, which
runs `ContinuousTestDaemonHost.RunAsync`. It refuses when the workspace is not enabled or when
`MILLER_CT=off`. `stop` signals the leased daemon, waits, then kills that process tree. No daemon
returns `already_stopped` and creates nothing.

`serve --json` reports `status` (`started`, `alreadyrunning`, `failed`, `refused`), `reason`
(string or null), and `pid` (number or null). `stop --json` reports `status` and `reason`.

## Worktrees: one family daemon

A linked git worktree inherits enablement from its main checkout's `.miller/ct.enabled` through
the git worktree link (a filesystem read, no `git` subprocess). Precedence, strictest first:
`MILLER_CT=off` → a local `.miller/ct.disabled` tombstone (written by a full `tests disable` on
that root) → a local `.miller/ct.enabled` → the inherited main-checkout marker → off. A
never-enabled repo's worktree stays fully off, and status reads still create nothing.

One daemon serves the whole repo family. `tests serve` on a worktree whose main checkout is opted
in anchors the daemon at the MAIN checkout; the `reason` names it (`family daemon at <main root>`).
The running family daemon adopts every registered, opted-in worktree of its own repo — each gets a
context bound to that worktree's own index and `ct.db` — and writes the worktree's own
`.miller/ct/daemon.status.json` on transitions with reason `adopted by <main root>`. The
user-global execution budget is shared: N worktrees never mean N concurrent suites.

On an adopted worktree, `tests run` routes to the family daemon and reaches that worktree's own
queue and `ct.db`. `tests stop` detaches that worktree only — it never stops the family daemon —
and reports `status: "detached"` (or `"not_adopted"` when the daemon does not serve that worktree;
both exit `0`). An unacknowledged detach request reports `status: "failed"` and exit `3`. A root
with its own live daemon keeps the full-stop semantics above.

## Run stall bound

A test process that goes SILENT is treated as wedged: Miller kills its process tree and fails the run.
The bound is on silence, not on total duration — a suite may legitimately run for an hour, but it prints
something far more often than every ten minutes. A wedged provider previously held the CT daemon
indefinitely (36 minutes in one dogfood run) because nothing was cancelling it.

- Default: **10 minutes** without output on stdout or stderr.
- Override with `MILLER_CT_STALL_TIMEOUT`: whole seconds (`900`) or a TimeSpan (`00:15:00`).
- `off` / `0` / `false` / `no` disables the bound and restores the unbounded wait.
- An unparseable value falls back to the default rather than failing the run.

A killed run reports a non-zero exit code, and the reason appears both in the run's stderr and in the CT
daemon log. The exit code is forced, never read from the killed child: a child that exits cleanly in the
same instant as the kill would otherwise report success for a run that never finished.

## Exit codes

Same process-level contract as [`cli-eros-v1.md`](cli-eros-v1.md): `0` success, `2` usage, `3`
operational refusal (not enabled, spawn failed, stop failed), `1` unexpected.
