# Task 6 Report: One-time machine cleanup + AccessIQ rebuild

**Status:** COMPLETE WITH LOCAL LAUNCH CAVEAT
**commit SHA:** 08acb8f
**Worktree:** `/Users/murphy/source/miller/.worktrees/dashboard-registry-hygiene`

## Summary

The live machine cleanup and AccessIQ rebuild targets are complete. The registry now contains only existing roots, the old `miller-bindsvc-*` temp directories are gone, AccessIQ has been rebuilt to schema 4 with `julie-extract` 2.11.0 metadata, and the branch dashboard was verified live in a foreground session.

The only caveat: this shell runner terminates detached child processes launched from `exec_command`, so the branch dashboard could not be left running via `nohup`/launcher after verification. The foreground verification proved the branch binary and routes; the normal dashboard launcher can be started again after merge.

## Before evidence

- Registry rows before cleanup: 294.
- AccessIQ artifact before rebuild: `schema_version=3`, `binary_version=2.8.1`.
- Old bindsvc temp dirs over one hour old: 1185.

## Cleanup evidence

- Registry backup exists: `/Users/murphy/.miller/workspaces.db.bak-20260708T202046Z`.
- Current registry rows: 55.
- Current missing-root registry rows: 0.
- Current old `$TMPDIR/miller-bindsvc-*` dirs: 0.
- No active `dotnet test` / `vstest` process was present during the final read-only verification.

## AccessIQ rebuild evidence

`/Users/murphy/source/AccessIQ/.miller/symbols.db` metadata now reports:

```text
binary_version|2.11.0
extract_contract_version|3
schema_version|4
sqlite_schema_version|4
```

## Dashboard evidence

Verified with:

```bash
MILLER_DASHBOARD_PORT=4977 \
MILLER_REGISTRY_DB="$HOME/.miller/workspaces.db" \
MILLER_TELEMETRY_DB="$HOME/.miller/telemetry.db" \
MILLER_TOOLS_ROOT="/Users/murphy/source/miller/.worktrees/dashboard-registry-hygiene/src/Miller.Server/bin/Release/net10.0/.tools" \
MILLER_DASHBOARD_PREFERRED_ROOT="/Users/murphy/source/miller/.worktrees/dashboard-registry-hygiene" \
/usr/local/share/dotnet/dotnet /Users/murphy/source/miller/.worktrees/dashboard-registry-hygiene/src/Miller.Dashboard/bin/Release/net10.0/Miller.Dashboard.dll
```

Observed:

- Startup logging emitted Information-level `Microsoft.Hosting.Lifetime` lines.
- `/` returned HTTP 200.
- AccessIQ workspace page returned HTTP 200.
- `/index.json` returned `workspace_count=55`, `live_count=54`, `missing_root_count=0`, `error_count=1`.
- Rendered `/` included the live/stale split and `miller workspace prune` hint.

## Acceptance criteria

- [x] Registry backup exists before mutation and path is recorded.
- [x] Prune/cleanup outcome leaves no missing-root registry rows.
- [x] Bindsvc rows and old bindsvc temp dirs are gone.
- [x] AccessIQ `symbols.db` reports `schema_version=4`.
- [x] AccessIQ dashboard page returns 200 from the branch dashboard.
- [x] Before/after evidence recorded.
- [x] No missing-root workspace data remains; eros was not force-rebuilt.

## Notes

The final registry still has one stale dashboard row because its root exists but the registry state is `error`. `workspace prune` correctly does not remove existing-root rows; the dashboard groups that row under stale per Task 5.
