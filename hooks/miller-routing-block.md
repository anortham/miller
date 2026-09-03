# Miller — this workspace's code-intelligence server

Miller serves a fresh index of this workspace's code. One Miller call beats shell greps and full-file reads.

## Rules

1. Search before reading: run `search` before grep/rg/cat or opening whole files — hits come back ranked, with file, line, and enclosing symbol already attached.
2. Structure before content: `inspect` a file's symbols or a symbol's signature first, then read only the region you actually need.
3. Impact before changing: run `impact` to see blast radius and which tests to run — before a refactor, and again after edits to confirm what moved.
4. Trace a thread with `trace refs|path|bridge`; use `inspect` for callers/callees.
5. Edit with a preview: `edit` dry-runs a diff and writes nothing until you set apply=true, so a rename or body rewrite is proved before it lands.
6. Trust the index: results are current for the indexed revision; if one looks stale, run `workspace refresh` and retry — beats re-checking by hand.
7. A deleted worktree leaves a dead registry row, however it went — `git worktree remove`, `rm -rf`, or a harness/CI teardown. Call Miller `workspace remove path=<exact old path>`; it works after the directory is gone. At session end run `workspace prune dry_run=true`, and apply it once the preview lists only roots you know are gone.

## When to reach for each tool

- search — ranked symbol, natural-language, marker, docs/config, or source-body search; auto may use semantics, lexical does zero vector work. Scope with file_pattern, language, and limit.
- inspect — a file or symbol you can already NAME: definition, signature, docs, refs, callers, body. depth=overview adds bounded refs/callers/callees and a body preview.
- context — FIRST call in an unfamiliar area: a token-budgeted bundle of entry-point symbols for a task, with reasons and copyable next calls.
- trace — exact refs, shortest dependency paths, or cross-language route chains.
- impact — before a refactor or after edits: impacted symbols plus likely tests, from a symbol, file, or git diff. With no args it reads the working-tree diff.
- edit — index-aware replace/rename/body-rewrite with a diff preview and match proof.
- patterns — pre-extracted code-shape facts (routes, config keys, doc structure) across 40 languages.
- content — import then search/read logs, CI output, web markdown, and large text.
- workspace — index lifecycle and semantic-broker health: status, refresh, health, list, onboarding, dashboard.
- tests — which tests your change made stale, and their last verdict; opt-in per workspace.
  CT off reports `enabled: false` plus the test projects it found. For a one-off
  answer, run the tests. Enable only for ongoing verdicts; start is explicit.

Run `workspace onboarding` early for telemetry-derived guidance about THIS repo.

Use compact output by default. Request format=json only when you need machine-readable fields or chaining; extract only the fields you need.
