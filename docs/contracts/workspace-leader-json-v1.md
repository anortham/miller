# Workspace leader JSON contract v1

Status: active additive contract for `miller workspace leader --json` and MCP `workspace operation=leader`.

`workspace leader` reports indexer leadership and can queue a graceful handoff request. It never kills processes.
Old leaders that do not understand handoff may leave the request unobserved before the wait timeout.

## Command

```bash
miller workspace leader --json [--id SELECTOR|--workspace-id SELECTOR] [--path DIR|--workspace DIR] [--handoff] [--wait]
```

MCP uses:

```text
workspace operation=leader handoff=true wait=true format=json
```

## Top-level fields

| Field | Type | Description |
|---|---|---|
| `schema_version` | number | Contract version. Currently `1`. |
| `workspace` | object | Selected workspace identity and responder process facts. |
| `indexer_leader` | object | Recorded leader identity and liveness facts. |
| `recommendation` | string | Human-readable remediation recommendation. |
| `handoff` | object | Handoff request status for this invocation. |

## `workspace`

| Field | Type | Description |
|---|---|---|
| `root` | string | Workspace root. |
| `workspace_id` | string or null | Stable Miller workspace id. |
| `display_id` | string or null | Human-sized selector. |
| `db` | string | Workspace `.miller/symbols.db` path. |
| `leader` | boolean | Whether the responding process is the current indexer leader. |
| `server_version` | string or null | Responding Miller version when available. |
| `server_pid` | number or null | Responding process id when available. |

## `indexer_leader`

| Field | Type | Description |
|---|---|---|
| `this_process` | boolean | Whether the responder is the leader. |
| `pid` | number or null | Recorded leader pid. |
| `version` | string or null | Recorded leader Miller version. |
| `process_path` | string or null | Recorded leader executable path. |
| `started_at` | string or null | Recorded leader start time in UTC. |
| `extractor_version` | string or null | Recorded leader bundled extractor version. |
| `alive` | boolean or null | Liveness probe result. |
| `own_extractor_version` | string or null | Responding process bundled extractor version. |
| `artifact_extractor_version` | string or null | Extractor version recorded in the current artifact. |
| `own_eligibility` | object or null | Responding process leadership eligibility. |

`own_eligibility`, when present:

| Field | Type | Description |
|---|---|---|
| `eligible` | boolean | Whether this process may lead indexing. |
| `reason` | string | Eligibility reason. |

## `handoff`

| Field | Type | Description |
|---|---|---|
| `requested` | boolean | Whether this invocation queued a handoff request. |
| `waited` | boolean | Whether this invocation waited briefly for observation. |
| `observed` | boolean | Whether the request disappeared or the leader identity changed before timeout. |
| `request_id` | string or null | Request id when `requested=true`. |
| `note` | string or null | Short status note. |

The request is written to the local `.miller/requests` queue. A live leader that supports this contract drains it,
verifies requester liveness, and abdicates through the normal leadership teardown/cooldown path.
