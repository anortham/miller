# ADR-0001: Guidance delivery channels

**Status:** Accepted
**Date:** 2026-07-02
**Supersedes:** the fictional 12,000-char `ServerInstructions` budget
(`MaxServerInstructionsChars`, `Load_StaysUnderClaudeCodeInstructionBudget`)

Design: [`docs/plans/2026-07-02-guidance-delivery-design.md`](../plans/2026-07-02-guidance-delivery-design.md).
Implementation: [`docs/plans/2026-07-02-guidance-delivery-implementation.md`](../plans/2026-07-02-guidance-delivery-implementation.md).

## Context

Miller shipped an 11,856-char embedded guidance doc as MCP `ServerInstructions`, guarded by a
test budget of 12,000 chars. That budget was fiction:

- **Claude Code truncates MCP `ServerInstructions` at ~2KB per server, inside a shared ~4KB
  budget across all configured servers.** The cut is silent, mid-sentence, and order-dependent —
  not user-configurable ([Claude Code MCP docs](https://code.claude.com/docs/en/mcp),
  [anthropics/claude-code#43474](https://github.com/anthropics/claude-code/issues/43474)).
  Depending on server order Miller may receive less than 2KB or nothing.
- **Measured live 2026-07-02:** Miller's 11,856-char doc was injected up to **character 2,047**,
  then `… [truncated]`. What survived: the six Rules, `search`, and half of `inspect`. What was
  cut: `context`, `trace`, `impact`, `edit`, `patterns`, `content`, `workspace` — seven of nine
  tools received zero guidance.
- **Tool descriptions cannot carry discovery.** With Tool Search enabled (the Claude Code
  default), only tool NAMES and `ServerInstructions` load at session start; `[Description]`
  schemas arrive deferred, after a tool is already known. Discovery — knowing a tool exists at
  all — must ride the `ServerInstructions` core.
- **Adoption evidence (telemetry 2026-06-06 → 2026-07-03, 5,249 calls, maintainer workspace):**
  the two tools whose guidance survived the 2KB window (`inspect` ~48%, `search` ~21%) took ~73%
  of all calls; the seven cut tools were nearly unused (`context` 1.3%, `trace` 0.6%, `edit`
  0.3%, `patterns` 0.1%, `impact` ~0%). Guidance that is not delivered is not adopted.

## Decision

Guidance flows through **three channels with distinct, individually verifiable jobs**, plus a
relocated long-form tail. No new MCP tools (CLAUDE.md boundary); no reliance on users editing
their own CLAUDE.md.

1. **Embedded core = the DISCOVERY contract (≤1,900 chars).** The only prose guaranteed at
   session start. It carries a routing table — one line per tool, when to reach for it — written
   most-important-first so truncation at any point after the Rules still leaves a coherent block.
   The 1,900 cap sits under the observed 2,047-char delivery window. Nothing ships in the embedded
   doc beyond this core.
2. **Tool `[Description]`s = the POST-DISCOVERY usage contracts.** Each of the nine descriptions is
   self-sufficient for correct USE once loaded: what-it-does, when to reach for it, when NOT to
   (naming the tool to prefer instead), and one copyable example. Budgets: ≤900 default, `trace`
   ≤1,500, `search` ≤1,100; parameter descriptions ≤250; total description text across all nine
   tools ≤9,000 chars. Descriptions-only: 4,512 before this work; 5,821 at acceptance (2026-07-02);
   **5,899 re-measured 2026-07-16**, after the affirmative-redirect sweep replaced bare prohibitions
   with redirects (params-inclusive schema total 13,917 on the same date, surfaced in the gate's
   failure message as report-only evidence). These are dated snapshots for context;
   `AgentInstructionsTests.CombinedToolDescriptions_StayWithinTotalSchemaBudget` is authoritative, so a
   stale figure here is documentation lag, never a budget change.
3. **Success-path nudges = the adoption lever.** One contextual next-step line through the shared
   `NextStepHint` formatter — compact output only, max one line per response, real copyable
   targets, per-tool suppression rules. JSON output is byte-identical (no nudges). Nudges reinforce
   cross-tool routing but fire only after a tool is already in use — they cannot bootstrap
   discovery; that is the core's job.

**Tail relocation.** The old `## Workflows` and `## Subagent Dispatching` sections (~10k chars) are
deleted from the embedded resource and relocated to the plugin-bundled skills plus
[`docs/agent-guidance.md`](../agent-guidance.md). Accepted loss, stated plainly: non-plugin installs
(manual archive, raw MCP config, source checkout) knowingly get **core + descriptions + nudges
only** — no skills. The core's closing line points every install path at `workspace onboarding`,
which is delivered through the tool channel regardless of install path.

## Consequences

- On Claude Code, all nine tools are now discoverable within the ~2KB window instead of two.
- Guidance is delivered where it verifiably lands: descriptions per-tool (no cross-server
  competition), the core in the one guaranteed-at-start slot, nudges at decision time.
- The design is written for the harshest known client (Claude Code shared 4KB budget). Clients with
  larger budgets get the same tight core by design, not by accident — no harness-specific branches.
- Non-plugin installs lose long-form workflow prose (which, on Claude Code, they never actually
  received — it was cut at 2KB). Mitigation: `docs/agent-guidance.md` + `workspace onboarding`.
- Gates move from a fictional length ceiling to real contracts in `AgentInstructionsTests`: core
  ≤1,900 normalized, every tool named in the routing table, per-tool + total description budgets,
  golden-clause assertions, and the `NextStepHint` format guard.

## Applies To

- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` (the embedded core — discovery contract).
- The nine `[McpServerTool]` `[Description]` attributes across `src/Miller.Server/Tools/*.cs`
  (usage contracts).
- `src/Miller.Server/Tools/NextStepHint.cs` and its three consuming renderers (`SearchTool`,
  `InspectTool`, `TraceTool`) — nudge format is shared; nudge decisions stay per-tool.
- `tests/Miller.Tests/Server/AgentInstructionsTests.cs` — the gate home for all of the above.
- Plugin skills + `docs/agent-guidance.md` — the relocated long-form tail.

## Future Agents

- **Never raise the core cap without re-measuring the live client window.** The 1,900-char ceiling
  is anchored to a 2026-07-02 measurement of Claude Code, not a round number. If the client limit
  changes, re-measure and record the new figure here before touching the cap.
- **Add new-tool guidance to BOTH channels:** a routing-table line in the core AND a golden
  `[Description]`. A tool named in only one is half-delivered.
- **Nudge decisions stay per-tool; nudge format stays in `NextStepHint`.** Only a tool knows its
  result shape; the formatter exists so nine renderers cannot drift.
- **Gates live in `AgentInstructionsTests`.** Do not weaken a gate to fit new content — revise the
  content, or if a limit genuinely changed, revise the gate and this ADR together and say why.
- **Do not re-invent the 12k budget.** It was fiction. If you find yourself adding a long tail to
  the embedded doc, that content belongs in a skill or `docs/agent-guidance.md`.
