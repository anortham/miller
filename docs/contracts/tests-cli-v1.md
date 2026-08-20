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
| `enabled` | bool | Workspace is opted in (`<workspace>/.miller/ct.enabled`) or has enabled project rows. |
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
| `selected` | object \| null | The selected freshness key, or `null` when no run has stored one. |
| `selected.index_identity` | string | Store cursor or legacy artifact id. Present when `selected` is an object. |
| `selected.revision` | number | Integer store log sequence or artifact revision. Present when `selected` is an object. |
| `stale_count` | number | Cases currently `stale` in `ct.db`. `0` when the sidecar is absent. |
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
files.

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
this run executes NOTHING and reports `paused: true`, `verdict: "unknown"`, `waited: false`, and
exit code `0` — a held budget is a deferral, not a failure. The verdict is `unknown` rather than the
stored one because a run that executed nothing holds no results at the selected key, and green
requires complete results at that key. `selected` still names the key the stored rows carry, so the
caller can still see what CT knows. Retry the run once the holding workspace finishes.

## `tests failures --json`

One page of the red cases, ordered by test-case id so paging is stable between calls.

```json
{
  "failures": [
    {
      "test_case_id": "xunit:Sample.Tests.Adds",
      "state": "red",
      "index_identity": "store:abc",
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
