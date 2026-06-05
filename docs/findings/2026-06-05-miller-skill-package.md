# Miller Skill Package

- **Date:** 2026-06-05
- **Scope:** Project-local Codex skills for Miller adoption.
- **Location:** `.agents/skills/`

## Result

Added a first Miller skill package with six `miller-` prefixed skills:

- `miller-explore-area`
- `miller-impact-analysis`
- `miller-editing`
- `miller-bridge-trace`
- `miller-cross-workspace`
- `miller-search-debug`

The `miller-` prefix avoids flat-namespace collisions with Julie's existing `explore-area`, `editing`,
and `impact-analysis` skills.

## Design

Each skill is intentionally small and workflow-focused:

- `SKILL.md` contains trigger metadata, allowed Miller tools, and the minimum process guidance.
- `agents/openai.yaml` gives UI metadata and a default prompt for explicit invocation.
- No auxiliary README or reference files were added.

The package teaches agents to use Miller's existing MCP surface:

- `context` for unfamiliar-area orientation;
- `search` with `mode=content` and `regions=...` for non-symbol text;
- `inspect` before whole-file reads;
- `impact` before refactors;
- `edit` with preview-first behavior;
- `trace mode=bridge` for provider-scoped bridge evidence;
- `workspace_id` for cross-workspace reads.

## Verification

- `git diff --check`
- ASCII scan over `.agents/skills`
- Ruby/YAML structural validation for every `SKILL.md` and `agents/openai.yaml`
- Manual line-count check: each `SKILL.md` is 55-70 lines

The bundled skill-creator `quick_validate.py` could not run because the current Python environment is
missing `yaml` / PyYAML, so Ruby's built-in YAML parser was used instead.
