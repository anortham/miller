# Cross-Workspace Selector Dogfood

Date: 2026-06-05

## Scope

Validated the workflow where an agent starts in one repo, then needs to examine another registered
workspace through Miller without changing directories or shelling into that repo.

## Evidence

Starting workspace:

```text
/Users/murphy/source/miller
```

`workspace list` from the live MCP server returned 17 registered workspaces, including:

- `/Users/murphy/source/miller` as current
- `/Users/murphy/source/MyraNext`
- `/Users/murphy/source/julie-extractors`
- `/Users/murphy/source/openclaw`
- `/Users/murphy/source/hermes-agent`

Cross-workspace read tools worked when the agent used the display ID:

```text
search(workspace_id="MyraNext", query="application settings controller")
```

returned `AppSettingsController`, `getApplicationSettings`, and related MyraNext symbols while the
session remained in the Miller checkout.

```text
search(workspace_id="julie-extractors", query="typed axios get route literals")
```

returned the JavaScript/TypeScript/Vue URL literal configuration and extractor symbols from the
`julie-extractors` workspace. This first read took about 58 seconds, which is consistent with the
existing first-read projection/loading follow-up rather than a selector failure.

## Gap Found

The natural user input for another repo is often the root path. Before this change, read tools rejected
that selector:

```text
search failed: unknown workspace selector '/Users/murphy/source/MyraNext'. Use workspace(operation="list")
to see display IDs; selectors accept display_id, unique prefix, full workspace_id, current, or primary.
```

That forced agents to list workspaces and translate the path into a display ID, even when the exact root
path was already registered.

## Change

`WorkspaceRegistrySelector` now accepts an absolute registered root path as a `workspace_id` selector.
The comparison reuses the existing workspace path safety semantics, so trailing separators and symlink
aliases resolve the same way as the workspace lifecycle commands.

The MCP parameter descriptions and embedded agent instructions now advertise registered root paths as a
valid selector form.

## Post-Change Check

The local CLI uses the same selector resolver for workspace status:

```bash
dotnet run --project src/Miller.Server -- workspace status --id /Users/murphy/source/MyraNext
```

returned:

```text
# workspace  miller 0.1.0+4069f953e02a
MyraNext-29cad844b05f  /Users/murphy/source/MyraNext  [reader]
symbols: 29695  ext: 12  rev: 1  unknown  queue: empty
freshness: ready
```

The live MCP read tools will pick up the root-path selector behavior after rebuilding and restarting the
Miller MCP server.

## Result

Cross-workspace querying is viable for beta:

- display ID and unique-prefix selectors already work for read tools;
- exact registered root paths now work as selectors too;
- agents can stay in the current session and route `search`, `inspect`, `context`, `impact`, and `trace`
  to another registered workspace;
- first-read latency on larger workspaces remains tracked separately under projection-specific read-path
  follow-up work.
