# Store Bootstrap Dogfood Repairs Design

## Goal

Restore default family-store startup on Linux, preserve checkout-replacement safety, and make the active store family diagnosable without querying SQLite manually.

## Architecture

- Centralize Git administrative-directory birth-time capture in `Miller.Indexing`. Linux uses `statx` birth time and returns unknown when the filesystem cannot provide it; other platforms retain the supported native creation-time path.
- Use the same birth-time evidence for workspace replacement identity and store-family lineage. Missing evidence never proves replacement.
- Before seeding a planned family store from legacy `symbols.db`, validate that the artifact satisfies the current Julie schema contract. An incompatible or corrupt seed is ignored and the producer performs a normal source scan.
- Keep `workspace_id` as the user-facing root identity and the UUID `family_id` as the internal shared-lineage identity. Add family, view, store-path, and member labels to existing status/health/dashboard diagnostics; add no MCP tool.

## Architecture Quality

- Affected modules: workspace root identity, store bootstrap coordination, existing workspace diagnostics.
- Caller-facing interfaces remain the existing workspace CLI/MCP/dashboard surfaces; diagnostic JSON additions are additive.
- Architecture risk: medium. Identity evidence is persisted, but the registry schema and producer store contract do not change.
- Rejected shortcuts: deleting pointers or registry rows, disabling store mode, treating Linux identity as always unknown, renaming existing family directories, or retrying an incompatible seed indefinitely.

## Tasks

### Task 1: Stable Git directory birth-time evidence

- Modify `src/Miller.Indexing/WorkspaceRootIdentity.cs` and its focused tests.
- Make `StoreWorkspaceCoordinator` consume the same evidence for common-dir lineage.
- [x] Ordinary `.git` metadata changes do not alter captured identity on Linux.
- [x] Recreated Git administrative directories still compare as replacements when birth time is available.

### Task 2: Incompatible legacy seed fallback

- Modify `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs` and focused coordinator/bootstrap tests.
- [x] A current compatible artifact remains eligible for `--from-artifact`.
- [x] A schema-incompatible, corrupt, or non-artifact file is not passed to the producer as a seed.
- [x] Startup falls through to a source import without deleting the legacy artifact.

### Task 3: Store-family observability and live dogfood

- Extend existing workspace facts/render/dashboard paths with family ID, view ID, store path, and member display labels.
- [x] Compact output stays bounded and JSON additions are additive.
- [x] No new MCP tool is added.
- [x] Fast tests, the relevant store scale tests, release build, live restart, and representative MCP calls pass.

## Verification

- Worker red/green: focused test class filters for each changed behavior.
- Affected change: `scripts/test.sh` and the relevant store scale test filters.
- Branch gate: `dotnet build Miller.slnx -c Release`, then live MCP status/health/onboarding/search/inspect/context/trace/impact/patterns/content checks.
- Security scope: none declared.
