# Miller — this workspace's code-intelligence server

Miller serves a fresh index of this workspace's code. One Miller call beats shell greps and full-file reads.

## Rules

1. Search before reading: use `search` for ranked file, line, and symbol hits.
2. Structure before content: `inspect` a file's symbols or a symbol's signature first, then read only the region you actually need.
3. Use `impact` before a refactor and after edits to find affected symbols and tests.
4. After edits, check `tests status` with the selected `workspace_id`. When CT is enabled and idle with stale work, use `tests run wait=true` after checking its scope; when off, run focused tests directly.
5. Trace a thread with `trace refs|path|bridge`; use `inspect` for callers/callees.
6. Preview with `edit`; only apply=true writes.
7. Freshness describes source-check evidence; background activity alone does not prove freshness. For stale results, repeat the read with `ensure_fresh=true`; if it reports contention, follow that diagnostic.
8. Name the workspace: every workspace-bound call takes `workspace_id` from `workspace list`, or from `workspace open path=/absolute/project` when the repo is absent. Only `workspace list|open|remove|prune|dashboard` run without one.
9. A deleted worktree leaves a dead registry row. Call `workspace remove path=<exact old path>`. At session end preview `workspace prune dry_run=true`; apply only for roots you know are gone.

## When to reach for each tool

- search — ranked symbol, natural-language, marker, docs/config, or source-body search; auto may use semantics, lexical does zero vector work. Scope with file_pattern, language, and limit.
- inspect — a file or symbol you can already NAME: definition, signature, docs, refs, callers, body. depth=overview adds bounded refs/callers/callees and a body preview.
- context — FIRST call in an unfamiliar area: a token-budgeted bundle of entry-point symbols for a task, with reasons and next calls.
- trace — exact refs, shortest dependency paths, or cross-language route chains.
- impact — before a refactor or after edits: impacted symbols plus likely tests, from a symbol, file, or git diff. With no args it reads the working-tree diff.
- edit — index-aware replace/rename/body-rewrite with a diff preview and match proof.
- patterns — pre-extracted code-shape facts (routes, config keys, doc structure) across 40 languages.
- content — import then search/read logs, CI output, web markdown, and large text.
- workspace — index lifecycle and semantic-broker health: status, refresh, health, list, open, onboarding, dashboard.
- tests — continuous testing (CT), opt-in per workspace: which cases your change staled and their last verdict. status is cheap and starts nothing; after an edit, run wait=true executes the explicit selection, including owed and red cases. CT off reports `enabled: false` plus the test projects it found: run those with your test runner for a one-off answer; enable only for ongoing verdicts; start is explicit.

Run `workspace onboarding` early for telemetry-derived guidance about THIS repo.

Use compact output by default. Request format=json only when you need machine-readable fields or chaining; extract only the fields you need.
