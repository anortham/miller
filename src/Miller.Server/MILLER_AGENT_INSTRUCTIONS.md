# Miller — Code Intelligence Server

Fresh index of this workspace's code. One Miller call beats shell greps and full-file reads.

## Rules

1. Search before reading with `search`.
2. Structure before content: use `inspect` first.
3. Impact before changing: use `impact`.
4. Trace with `trace`; inspect callers/callees.
5. Edit with a diff preview.
6. Trust the index: results are current for the indexed revision; refresh changed files — beats re-checking by hand.

Use compact output by default. Request format=json only when you need machine-readable fields or chaining; extract only the fields you need.

- User-level GUI clients: discover with `workspace operation=list`; if absent, call `workspace operation=open path=/absolute/project`; pass returned `workspace_id` on every workspace-bound call (works with no primary).
- Unscoped exceptions: `workspace` list/open/remove/prune/dashboard and `content search workspace_id=all|registered` for text audits. Follow schemas; not every operation needs an ID.

## When to reach for each tool

- content — import before reading; external text/docs/logs.
- context — unfamiliar areas: token-budgeted entry points.
- edit — indexed rewrite + preview; apply=true to write.
- impact — affected symbols/tests; default view shows test impact.
- inspect — named file/symbol: signatures, hierarchy, relations.
- patterns — extracted routes/config/docs across languages.
- search — ranked symbol/source/docs/marker/text; auto may use semantics, lexical does zero vector work.
- tests — continuous verdicts, opt-in; check status after edits, run stale tests when enabled; direct tests valid when off.
- trace — refs, dependency paths, bridges.
- workspace — lifecycle, registered workspaces, onboarding, health, semantic-broker health.

`workspace onboarding` gives guidance
