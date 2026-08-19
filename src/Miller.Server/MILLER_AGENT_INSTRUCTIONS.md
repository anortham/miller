# Miller — Code Intelligence Server

Fresh index of this workspace's code. One Miller call beats shell greps and full-file reads.

## Rules

1. Search before reading: run `search` before grep/rg/cat or opening whole files.
2. Structure before content: `inspect` a file's symbols or a symbol's signature before reading it whole.
3. Impact before changing: run `impact` to see blast radius and which tests to run.
4. Trace a thread with `trace refs|path|bridge`; use `inspect` for callers/callees.
5. Edit with a preview: `edit` dry-runs a diff; set apply=true only after it looks right.
6. Trust the index: results are current for the indexed revision; if one looks stale, run `workspace refresh` and retry — beats re-checking by hand.

## When to reach for each tool

- search — ranked symbol, natural-language, marker, docs/config, or source-body search; auto may use semantics, lexical does zero vector work.
- inspect — a file or symbol you can already NAME: definition, signature, docs, refs, callers, body.
- context — FIRST call in an unfamiliar area: a token-budgeted bundle of entry-point symbols for a task, with reasons.
- trace — exact refs, shortest dependency paths, or cross-language route chains.
- impact — before a refactor or after edits: impacted symbols plus likely tests, from a symbol, file, or git diff.
- edit — index-aware replace/rename/body-rewrite with a diff preview and match proof.
- patterns — pre-extracted code-shape facts (routes, config keys, doc structure).
- content — import then search/read logs, CI output, web markdown, and large text.
- workspace — index lifecycle and semantic-broker health: status, refresh, health, list, onboarding, dashboard.
- tests — continuous-test status (cheap, starts nothing); start is explicit; enable is opt-in.

Run `workspace onboarding` early for telemetry-derived guidance about THIS repo.
