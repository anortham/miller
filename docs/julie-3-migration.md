# Julie 3.x migration

Source builds now pin julie-extract 3.7.2. The published Miller v1.29.1 archives
still bundle 2.42.0; these changes require building Miller from this checkout.

Julie 3.0 removed the `store` and `export` commands. Its standalone SQLite
artifact remains compatible with Miller's readers. Miller uses a separate
`.miller/symbols.db` in each checkout; it does not recreate the removed upstream
storage backend. Existing MCP tools and CLI workflows remain available.

## Upgrade

1. Stop existing Miller MCP servers and CT daemons before switching builds.
   Restart every client using Miller so an older store writer cannot recreate a
   workspace pointer during migration.
2. Remove an explicit `MILLER_INDEX_STORE=on` setting. The pinned Julie runtime
   does not support store mode. `MILLER_INDEX_STORE=off` remains usable.
3. Restore and build:

   ```bash
   bash scripts/restore-julie-extract.sh
   bash scripts/restore-semantic-sidecar.sh
   dotnet build Miller.slnx -c Release
   ```

   On Windows, use the corresponding `.ps1` restore scripts.
4. Open or refresh the workspace with the new build. Store-backed workspaces
   require a full source rebuild before Miller can serve the standalone index.
5. Check `workspace status` and `workspace health`, then search for a known
   symbol and any imported document. CT verdicts from the old index generation
   become stale; start the new CT daemon explicitly if testing is enabled.

## Data safety

Migration holds the workspace writer lock and rebuilds into the normal staging
artifact before promotion. The workspace's old store pointer remains until the
rebuild and import preservation succeed. A failed or cancelled migration must
not expose an old standalone artifact as current.

External and web imports are copied from the old per-view content sidecar into
the workspace content corpus. If a damaged pointer cannot be resolved through
the exact registered family/view, migration refuses to discard it; recover the
pointer before retrying. Unreadable imported-content databases also block the
switch. The original shared family store is retained;
migration does not delete its databases or other workspaces' sidecars. Source,
search, and semantic projections are rebuilt for the new artifact identity.

Historical family-store contracts and tests describe older Miller builds and
recovery fixtures. They do not imply that Julie 3.x supports the removed CLI.
