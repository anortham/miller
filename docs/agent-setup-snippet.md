# Agent instructions snippet — make your agent prefer Miller

Installing the Miller MCP server does not guarantee an agent will use it. Newer harnesses (including
current Claude Code) defer MCP tool schemas behind on-demand tool search, so Miller's tool descriptions
may not be in the model's context when it picks an exploration strategy — and the built-in grep/read
tools always are, so agents fall back to shell searches even when Miller is faster and cheaper. Miller's
embedded server instructions cannot fix this alone: clients truncate them (Claude Code merges all
servers into a shared ~4KB block), and they only load after the server connects.

The reliable fix is a short routing block in instructions that are **always** in the model's context.
Paste the snippet below into:

- **Claude Code:** `~/.claude/CLAUDE.md` (user-level, applies everywhere) or a project `CLAUDE.md`.
- **Codex and other AGENTS.md-aware harnesses:** `~/.codex/AGENTS.md` or the project `AGENTS.md`.
- **Cursor:** a user or project rule (`.cursor/rules/`).

The README embeds a copy of this snippet under "Making Agents Actually Use Miller" — keep the two in
sync when editing.

```markdown
## Miller — code intelligence (use it before shell search)

The Miller MCP server is connected. Use it for codebase exploration instead of grep/rg/find/cat
chains and whole-file reads — it returns ranked, structured results in fewer tokens. If Miller's
tools are deferred (schemas not yet loaded), load them via tool search rather than falling back
to shell search.

| Instead of... | Use Miller |
|---|---|
| grep/rg for code, text, or TODO/FIXME markers | `search` (modes: symbol, text, file, content, source, markers) |
| reading a whole file | `inspect <file>` to list its symbols, then `inspect <symbol> depth=overview` |
| hand-tracing usages across files | `trace <symbol>` for references; `trace A mode=path to=B` for dependency paths |
| guessing a change's blast radius | `impact <symbol>` or `impact --git` (also suggests likely tests) |
| orienting in an unfamiliar area | `context "<task or area>"` for a token-budgeted entry-point bundle |
| raw find-and-replace edits | `edit` — index-aware, shows a diff preview; set apply=true only after it looks right |

Rules:
1. `search` before grep; `inspect` before reading any file whole.
2. `trace` references before changing a public API; `impact` before refactors.
3. Trust the index — do not re-verify Miller results with grep. If results look stale, run
   `workspace refresh` and retry.
4. These rules also apply to any subagents you dispatch.
```
