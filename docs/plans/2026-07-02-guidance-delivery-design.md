# Guidance Delivery Under Real MCP Client Limits — Design

**Date:** 2026-07-02
**Status:** Approved design, pre-implementation (companion implementation plan to follow)
**Problem owner:** Miller MCP server (`src/Miller.Server`)

## Problem

Miller ships an 11,856-char embedded guidance doc (`MILLER_AGENT_INSTRUCTIONS.md`) as MCP
`ServerInstructions`, guarded by a test budget of 12,000 chars named
`Load_StaysUnderClaudeCodeInstructionBudget`. That budget is fiction:

- Claude Code truncates MCP server instructions at **~2KB per server**, inside a **shared ~4KB
  budget across all configured servers** ([Claude Code MCP docs](https://code.claude.com/docs/en/mcp),
  [anthropics/claude-code#43474](https://github.com/anthropics/claude-code/issues/43474) — silent,
  mid-sentence, order-dependent, not user-configurable).
- Measured live 2026-07-02: Miller's doc was injected up to **character 2,047**, then `… [truncated]`.
- What survives the window: the six Rules, `search`, and half of `inspect`.
  What is cut: **`context`, `trace`, `impact`, `edit`, `patterns`, `content`, `workspace`** —
  seven of nine tools receive zero guidance on Claude Code.
- Because the ~4KB budget is shared across servers, Miller may receive **less than 2KB or nothing**
  depending on server order, which the user cannot control.

### Adoption evidence (telemetry baseline, 2026-06-06 → 2026-07-03, 5,249 calls, this workspace)

| Tool | Calls | Share | Guidance survives 2KB window? |
|---|---:|---:|---|
| inspect (all depths) | ~2,504 | ~48% | yes (partially) |
| search (all modes) | ~1,081 | ~21% | yes |
| content (search+read+…) | ~412 | ~8% | no |
| workspace (status+open+…) | ~203 | ~4% | no |
| context | 70 | 1.3% | no |
| trace | 29 (15 empty) | 0.6% | no |
| edit | 15 | 0.3% | no |
| patterns | 7 | 0.1% | no |
| impact | ≈0 (no tool-mix row) | ~0% | no |

The two tools whose guidance survives take ~73% of traffic; the starved tools are the cut tools.
Confounder, honestly noted: most of this telemetry is from the maintainer's machine where a
SessionStart hook injects a Miller tool table into every session — and trace/impact/patterns
*still* barely register. Static guidance alone, even when delivered, is not sufficient; guidance
must also arrive at decision time (see Nudges).

## Decisions (approved 2026-07-02)

1. **Channels in scope:** everything Miller ships — ServerInstructions, per-tool/param
   `[Description]`s, plugin-bundled skills, README/docs — plus in-output nudges.
   **No new MCP tools** (CLAUDE.md boundary). No reliance on users editing their own CLAUDE.md.
2. **Primary channel = the nine tool `[Description]`s.** They are delivered per-tool, do not
   compete with other servers, and are already test-guarded (≤900 chars). ServerInstructions
   becomes a best-effort bonus layer that must degrade gracefully to zero delivery.
3. **Success-path nudges:** one contextual line, only when a concrete next step exists, always
   copyable, never boilerplate. Failure-path `next_actions` unchanged.
4. **Verification:** build-time structural gates + report-only telemetry before/after checkpoint.
   No new eval infrastructure.
5. **Approach A over B/C:** no undeliverable tail kept for hypothetical clients (B rejected);
   no generic hint engine (C rejected as speculative). Every byte of shipped guidance lives in a
   channel whose delivery is verifiable.

## Design

### 1. Channel contract

Guidance lives in exactly three verifiable channels:

**(i) Tool descriptions — the routing contracts.** Each of the nine `[McpServerTool]`
`[Description]`s is rewritten to be self-sufficient: one clause of what-it-does, *when to reach
for it*, *when NOT to* (naming the tool to prefer instead), and one copyable example call.
≤900 chars each (existing guard). Parameter descriptions stay ≤250 chars. A description must
make sense to an agent that has received **no** ServerInstructions at all.

**(ii) ServerInstructions — the ≤1,900-char core.** Rewritten top-down by importance so that
truncation at any point after the Rules still leaves a coherent block:

1. Identity line (1 line).
2. The six behavioral rules, compressed (~700 chars).
3. Routing table: one line per tool — `tool — when-to-use clause` (~900 chars).
4. One closing line: `workspace onboarding` gives telemetry-derived startup guidance.

Hard cap 1,900 chars after CRLF normalization (safety margin under the observed 2,047-char
delivery). **Nothing ships in the embedded doc beyond this core.** The old `## Workflows` and
`## Subagent Dispatching` sections (~10k chars) are deleted from the embedded resource.

**(iii) Delivery-time nudges — the adoption lever.** See §3.

### 2. Tail relocation

Content from the deleted tail moves to channels with verifiable delivery:

- Workflow guidance (explore-an-area, impact-before-edit, refs/path tracing, marker audits,
  cross-workspace, editing discipline) merges into the **existing** plugin-bundled skills
  (`miller-explore-area`, `miller-impact-analysis`, `miller-editing`, `miller-search-debug`,
  `miller-text-audit`, `miller-cross-workspace`, `miller-bridge-trace`, `miller-large-file`).
  No new skills. Skill descriptions get the same routing language so they trigger reliably.
- Subagent-dispatch guidance and anything repo-maintainer-facing moves to `README.md` /
  `docs/README.md` as appropriate.
- No content is deleted outright without relocation unless it is redundant with a rewritten
  tool description — each removal in the implementation plan names its destination or the
  description that supersedes it.

### 3. Success-path nudges

One trailing hint line on success outputs, computed per-tool from the actual result, using a
shared formatter so all hints render identically and one test guards the format.

| Tool | Nudge (when) | Suppress (never fire when) |
|---|---|---|
| `search` (symbol hits) | `next: inspect <top-hit> depth=overview` | top hit is a file/content hit with its own affordance; result already empty (failure path owns it) |
| `inspect` (symbol, overview/full) | `next: impact target=<X> — <N> dependents` when refs ≥ threshold | refs below threshold; target is a test symbol; depth=summary |
| `trace` refs (non-empty) | `next: impact target=<X> before editing` | target is a test symbol |
| `impact` (non-empty) | `next: run likely tests` pointer (already present via likely-tests section — no new nudge) | — |
| `context` | existing `## next inspect` footer (unchanged) | — |
| `edit` preview | existing `pass apply=true` line (unchanged) | — |
| `patterns` list/summary | existing `Next:` block (unchanged) | — |
| `content`, `workspace` | failure-path only (unchanged) | — |

Rules: exactly one hint line max per response; the hint names real targets (copyable); the
dependent-count threshold and test-symbol suppression keep it from becoming boilerplate.
Compact output only; JSON gets no nudges (agents chaining JSON don't need routing prose).
The threshold value is an implementation-plan decision; start at refs ≥ 4.

**New seam:** one static hint formatter (e.g. `NextStepHint.Render(tool, argsLine, reason)`),
so formats cannot drift per tool. Hint *decisions* stay inside each tool's renderer — only the
tool knows its result shape.

### 4. Test gates (replacing the fictional budget)

`AgentInstructionsTests` is rewritten to guard the real contract:

1. `AgentInstructions.Load()` length ≤ **1,900** chars after `\r\n` normalization.
2. Every tool name appears in the core with routing language (structural: the routing table
   contains one line per registered tool — enumerate via reflection like today's tests).
3. Every `[McpServerTool]` `[Description]` is ≤900 chars **and** contains a when-to-use marker
   (structural check: contains "Use " or equivalent routing phrase — exact predicate decided in
   the implementation plan) **and** contains a when-not-to/alternative clause for the seven
   tools cut by the 2KB window (context, trace, impact, edit, patterns, content, workspace).
4. Parameter descriptions ≤250 chars (existing).
5. Hint format guard through the shared formatter.
6. The constant `MaxServerInstructionsChars = 12_000` and the test name
   `Load_StaysUnderClaudeCodeInstructionBudget` are removed (the new name states the real
   contract, e.g. `Load_CoreFitsClaudeCodeDeliveryWindow`).

### 5. Evidence loop (report-only)

- The telemetry baseline table above is this design doc's committed snapshot.
- After ~2–3 weeks of dogfood on the new build, re-read the tool mix
  (`workspace onboarding --json` / telemetry export) and append a follow-up section comparing
  share for the starved tools (context, trace, impact, patterns, edit). Report-only — no
  numeric gate, because the telemetry population is small and maintainer-skewed.

### 6. Cross-harness posture

Codex/Cursor/OpenCode receive identical tool descriptions and the same plugin skills; repo-level
guidance continues to ship via CLAUDE.md → AGENTS.md mirroring. No harness-specific branches.
The ServerInstructions core is written for the harshest known client (Claude Code); clients with
bigger budgets simply get the same tight core — by design, not by accident.

### 7. Durable decision record

- New `docs/adr/ADR-0001-guidance-delivery-channels.md`: descriptions are the primary guidance
  channel; ServerInstructions core ≤1,900 chars, most-important-first, no tail; nudges are
  one-line, contextual, compact-only. Includes the 2026-07-02 measurement so future agents do
  not re-derive (or re-fictionalize) the budget.
- CLAUDE.md gains one bullet under server/startup notes pointing at the ADR (then
  `scripts/sync-agents.sh`).

## Acceptance criteria (design-level)

- [ ] A Claude Code agent receiving ONLY tool descriptions (zero ServerInstructions) has, for
      every tool, enough routing guidance to know when to use it and when not to.
- [ ] `AgentInstructions.Load()` ≤1,900 chars normalized; every tool named in the routing table;
      gates enumerated in §4 all enforced by `AgentInstructionsTests`.
- [ ] Embedded doc contains no content beyond the core; Workflows/dispatch content relocated to
      the named skills/README with nothing silently dropped.
- [ ] Success-path nudges implemented per §3 with suppression rules and one shared formatter;
      compact-only; ≤1 line; JSON byte-identical except where §3 says otherwise (nowhere).
- [ ] Telemetry baseline committed (this doc); follow-up checkpoint scheduled as a docs task.
- [ ] ADR-0001 written; CLAUDE.md bullet added and AGENTS.md re-synced.
- [ ] All existing suites green (fast + scale); Release build 0/0.

## Out of scope

- New MCP tools or operations (boundary: CLAUDE.md).
- Eval harness for adoption (rejected as (b) during design Q&A).
- Generic result-shape hint engine (Approach C, rejected).
- Waiting on or working around anthropics/claude-code#43474 beyond designing for its current
  behavior; if Claude Code later raises the limit, the core still works.
