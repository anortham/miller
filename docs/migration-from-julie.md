# Migrating From Julie To Miller

This guide is frozen with the Miller 1.14.0 release candidate and becomes operative only if the complete visible,
review, package, and sealed takeover gates pass. It must not be published from a failed candidate.

Miller 1.14.0 is the supported local-agent replacement for Julie after that activation gate. Miller owns deterministic code navigation,
retrieval, impact analysis, editing, workspace lifecycle, content, structural facts, telemetry, and local
reports. The packaged `julie-extract` binary still owns parser-backed extraction, and Eros remains the owner of
fleet-level ranking, guidance, confidence views, suppression persistence, and commercial orchestration.

This migration does not uninstall Julie or delete `.julie/`. Keep Julie available until the Miller verification
steps below pass for the workspaces that matter to you.

## Install Miller

Install the Miller plugin from the `anortham/miller` marketplace in Claude Code or Codex, install it from the
Cursor plugin marketplace, or download the matching Miller 1.14.0 release archive. Plugin and archive installs
already contain the pinned `julie-extract` and `julie-semantic-sidecar` executables.

Remove or disable the Julie MCP entry only after Miller connects successfully. A manual Miller entry launches
the versioned binary with the explicit `serve` argument:

```json
{
  "mcpServers": {
    "miller": {
      "type": "stdio",
      "command": "/absolute/path/to/miller",
      "args": ["serve"]
    }
  }
}
```

Miller binds from MCP client roots. For clients that do not send roots, set `MILLER_WORKSPACE_ROOT` to the
project root. Do not point it at a home directory, filesystem root, or system directory.

## Replace Julie Workflows

| Julie tool or workflow | Miller replacement |
|---|---|
| `fast_search` | `search`; use the default automatic route, or select `text`, `symbol`, `file`, `source`, `content`, `markers`, `external`, `web`, or `all-text` explicitly |
| `get_symbols`, `deep_dive` | `inspect`; start with `depth=overview`, then use `depth=full` only when the complete body or relationship set is needed |
| `get_context` | `context`; pass the task in `query` and keep the token budget bounded |
| `fast_refs` | `trace mode=refs`; use `inspect depth=overview` when bounded callers/callees are enough |
| `call_path` | `trace mode=path` with `target` and `to`; use `mode=bridge` for supported client-to-route paths |
| `blast_radius` | `impact` on a symbol/file, `impact --git`, or no arguments for the current working-tree diff |
| `rename_symbol` | `edit operation=rename_symbol`; exact evidence is required by default and preview never writes |
| `rewrite_symbol`, `edit_file` | `edit` operations for symbol body/signature replacement, anchored text replacement, insertion, rename, and API docs |
| `spillover_get` | `content import/search/read`; large exports are CLI-only through `miller content export` |
| `patterns` | `patterns`; Miller reads the same extractor-owned structural-fact contract and adds bounded filtering, grouping, and diagnostics |
| `manage_workspace` | `workspace` for status, refresh, full rebuild, health, registry lifecycle, leadership, onboarding, and dashboard launch |

Miller exposes nine MCP tools rather than mirroring Julie's thirteen names. Removed Julie behaviors are not
retained as deprecated aliases; agents discover only the current Miller surface.

## Build Miller Artifacts

1. Start Miller in the target repository.
2. Run `workspace status`, then `workspace refresh`.
3. Run `workspace health` and resolve any not-ready, stale, corrupt, or extractor-version diagnostic.
4. Use `workspace list` to confirm registered roots. Cross-workspace reads stay in the current session and pass
   the other workspace's selector through `workspace_id`.

Miller writes `.miller/symbols.db`, `search.db`, `content.db`, optional `vectors.db`, and related local sidecars.
It does not reuse `.julie/`, and it does not delete that directory. A full rebuild extracts into a separate
artifact and atomically promotes it; it does not merge into the live database.

## Semantic Retrieval

Local semantic retrieval is optional and uses the packaged `julie-semantic-sidecar`. BGE-small remains the
production default for Miller 1.14.0. CodeRankEmbed is exercised only through the isolated evaluation adapter
and is not a production surface or default.

- Run `miller semantic prepare` before enabling semantic retrieval on a fresh machine.
- Set `MILLER_SEMANTIC=off` for a permanent zero-work guarantee. No model is loaded, no vector artifact is
  opened or built, and lexical output remains byte-identical.
- Use the existing search retrieval override when a specific call must be lexical, hybrid, or semantic.
- A changed encoder fingerprint creates a new vector generation; Miller rebuilds rather than mixing models.

## Verify Before Removing Julie

- Exercise one exact symbol lookup, one concept search, one docs/config search, one reference trace, one impact
  query, and one preview-only edit in each important workspace.
- Confirm `inspect depth=overview` and `context` return enough implementation evidence without unbounded output.
- Confirm exact-reference results carry provenance and do not attribute same-name homonyms.
- Confirm `workspace health` reports the pinned extractor, search/content artifacts, and any enabled vector
  generation as ready after the initial refresh.
- Keep Julie configured but inactive until these checks pass and your agent instructions route code exploration
  to Miller.

## Deliberate Differences

- Miller does not provide a mutable session-wide workspace switch. Use explicit `workspace_id` selectors.
- Miller does not expose content export through MCP. Use the CLI/JSONL contract so an agent call cannot flood its
  context.
- Miller does not preserve `trace auto`; bounded callers/callees live in `inspect`, while refs, paths, and
  bridges remain explicit `trace` modes.
- Miller rejects rename when exact evidence or language/kind coverage is insufficient. It does not silently
  fall back to broad same-name replacement.
- Parser recognition and embedding generation remain external process boundaries. Miller consumes pinned
  artifacts and does not absorb extractor or fleet-orchestration ownership.

## Roll Back

1. Stop the Miller MCP process or remove its MCP entry.
2. Restore the Julie MCP entry and restart the client.
3. Keep `.miller/` for diagnosis or remove it later through an intentional, recoverable cleanup; rollback does
   not require modifying `.julie/`.
4. Report the Miller version, `workspace health --json`, the failing tool call, and its typed diagnostic. Do not
   send private sealed-evaluation artifacts.

Keep Julie as a rollback option until its separate retirement announcement defines the final support window.
New feature work belongs in Miller, `julie-extractors`, or Eros according to the ownership boundary above.
