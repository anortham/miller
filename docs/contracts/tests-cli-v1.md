# Tests CLI JSON v1

Status: active Miller CLI JSON contract.

Commands:

```bash
miller tests status --json [--workspace-id SELECTOR] [--workspace DIR]
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
    "paused": false
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

Enable and disable do not start the daemon.

## `tests run --json`

| Field | Type | Meaning |
|---|---|---|
| `execution` | string | `daemon` when a live daemon holds the lease; otherwise `foreground_one_shot`. |
| `verdict` | string | `green`, `red`, `partial`, or `unknown`. |
| `reason` | string \| null | Channel or one-shot reason. |
| `waited` | bool | `true` when `--wait` was supplied. |
| `selected` | object \| null | Same shape as status `selected`. |

A live daemon receives `run` on the file command channel. With no daemon, Miller runs a foreground
one-shot in the calling process. `--wait` waits for a terminal verdict (`green` / `red` / `partial`)
either way.

## `tests serve` / `tests stop`

`serve` is the only start path: `CtDaemonLauncher.SpawnDetached` launches `miller ct-daemon`, which
runs `ContinuousTestDaemonHost.RunAsync`. It refuses when the workspace is not enabled or when
`MILLER_CT=off`. `stop` signals the leased daemon, waits, then kills that process tree. No daemon
returns `already_stopped` and creates nothing.

## Exit codes

Same process-level contract as [`cli-eros-v1.md`](cli-eros-v1.md): `0` success, `2` usage, `3`
operational refusal (not enabled, spawn failed, stop failed), `1` unexpected.
