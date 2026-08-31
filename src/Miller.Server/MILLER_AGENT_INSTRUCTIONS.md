# Miller — Code Intelligence Server

Fresh index of this workspace's code. One Miller call beats shell greps and full-file reads.

## Rules

1. Search before reading with `search`.
2. Structure before content: use `inspect` first.
3. Impact before changing: use `impact` before refactor or after edits.
4. Trace with `trace`; inspect callers/callees.
5. Edit with a preview; apply after diff looks right.
6. Trust the index: results are current for the indexed revision; `workspace refresh` re-indexes changed files — beats re-checking by hand.

## Workspace targeting

- User-level GUI clients (Codex, Cursor, VS Code) must not use launch cwd, `MILLER_WORKSPACE_ROOT`/`GOLDFISH_WORKSPACE`, MCP Roots, `current`, `primary`, or session binding for workspace-bound MCP calls.
- Discover with `workspace operation=list`; if absent, call `workspace operation=open path=/absolute/project`; use returned `workspace_id` on every workspace-bound call. Explicit registered selectors work with no primary, matching primary, or different primary.
- Unscoped exceptions: `workspace` list/open/prune/dashboard and `content search workspace_id=all|registered` for text audits. Follow schemas; not every operation needs an ID.
- CLI/source-checkout startup-root behavior is separate from GUI MCP targeting.

## When to reach for each tool

- content — external text.
- context — unfamiliar areas.
- edit — indexed rewrite + preview.
- impact — affected symbols/tests.
- inspect — named file/symbol.
- patterns — extracted routes/config/docs.
- search — ranked symbol/source/docs/marker/text; auto may use semantics, lexical does zero vector work.
- tests — continuous verdicts; runner for inner-loop tests.
- trace — refs, dependency paths, bridges.
- workspace — lifecycle, registered workspaces, onboarding, health, semantic-broker health.

`workspace onboarding` gives guidance
