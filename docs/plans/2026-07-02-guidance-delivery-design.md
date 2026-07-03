# Guidance Delivery Under Real MCP Client Limits — Design

**Date:** 2026-07-02
**Status:** Approved design, pre-implementation (companion implementation plan to follow)
**Problem owner:** Miller MCP server (`src/Miller.Server`)
**External review:** Codex adversarial design review 2026-07-02 — verdict needs-attention,
4 findings (2 high, 2 medium), all accepted and folded in: §1 reframed around Tool Search
deferral (descriptions cannot carry discovery), §2 delivery matrix + accepted-loss statement
for non-plugin installs, §4 total-schema gate + golden-content assertions, per-tool description
budget overrides with `trace` as the drafted-first stress case.

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

*(Revised 2026-07-02 after adversarial review — Codex finding 1: Claude Code defers tool
descriptions when Tool Search is enabled (the default) — only tool NAMES and ServerInstructions
load at session start. Descriptions therefore cannot carry tool DISCOVERY; verified live in the
session that produced this design, where Miller's schemas arrived deferred.)*

Guidance lives in three verifiable channels with distinct jobs:

**(i) ServerInstructions — the DISCOVERY contract (≤1,900-char core).** The only
guaranteed-at-session-start prose. Its routing table is the load-bearing artifact of this whole
design: one line per tool saying when to reach for it, so an agent knows the tool exists before
any schema is loaded. Rewritten top-down by importance so truncation at any point after the
Rules still leaves a coherent block (degrades gracefully even if the shared budget delivers
only ~500 chars):

**(ii) Tool descriptions — the POST-DISCOVERY usage contracts.** Each of the nine
`[McpServerTool]` `[Description]`s is rewritten to be self-sufficient for correct USE once
loaded: one clause of what-it-does, when to reach for it, *when NOT to* (naming the tool to
prefer instead), and one copyable example call. Default budget ≤900 chars; a per-tool override
up to 1,500 chars is allowed where justified (`trace` is the known stress case — already at
894 chars today with no example and no when-not-to clause; client-side cap is 2KB/description).
Parameter descriptions stay ≤250 chars. **The implementation plan must contain the final
drafted text of all nine descriptions** (golden content), with golden-content assertions for
load-bearing clauses — marker-only checks are insufficient (Codex finding 4).
Additionally, a TOTAL schema budget is gated, not just per-field caps: the serialized
description+parameter text across all nine tools must stay ≤9,000 chars, with the measured
before (4,512 chars today) and after recorded in the implementation plan (Codex finding 3 —
in upfront-loading modes the whole schema competes for context).

The ServerInstructions core layout:

1. Identity line (1 line).
2. The six behavioral rules, compressed (~700 chars).
3. Routing table: one line per tool — `tool — when-to-use clause` (~900 chars).
4. One closing line: `workspace onboarding` gives telemetry-derived startup guidance.

Hard cap 1,900 chars after CRLF normalization (safety margin under the observed 2,047-char
delivery). **Nothing ships in the embedded doc beyond this core.** The old `## Workflows` and
`## Subagent Dispatching` sections (~10k chars) are deleted from the embedded resource.

**(iii) Delivery-time nudges — the adoption lever.** See §3. Note these fire only after a tool
is already in use — they reinforce cross-tool routing but cannot bootstrap discovery; that is
the core's job.

### 2. Tail relocation — with an explicit delivery matrix

*(Revised after adversarial review — Codex finding 2: plugin skills reach ONLY the Claude
Code/Cursor/Codex plugin install paths; the README's manual-archive and source-checkout installs
get no skills, and no OpenCode manifest exists. The original draft overclaimed delivery.)*

Who receives what, by install path:

| Install path | Core (ServerInstructions) | Tool descriptions | Nudges | Skills |
|---|---|---|---|---|
| Claude Code plugin | yes (subject to shared 4KB) | yes (deferred/upfront) | yes | yes |
| Cursor / Codex plugin | yes (client-dependent limits) | yes | yes | yes |
| Manual archive / raw MCP config | yes (client-dependent) | yes | yes | **no** |
| Source checkout (`dotnet run`) | yes | yes | yes | **no** |

**Accepted loss, stated plainly:** non-plugin installs lose the long-form workflow guidance
they nominally receive today (on Claude Code they never actually received it — it was cut at
2KB — but other clients may deliver the full 12k doc). Mitigation: the relocated content also
lands in the repo docs (`docs/`, linked from README), and the core's closing line points at
`workspace onboarding`, which is delivered via the tool channel on every install path.
The design does NOT claim OpenCode skill delivery.

Content from the deleted tail moves to:

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
3. Every `[McpServerTool]` `[Description]` is within its per-tool budget (default 900 chars,
   documented overrides up to 1,500) **and** matches golden-content assertions for its
   load-bearing clauses (when-to-use, when-not-to/alternative for the seven cut tools, example
   call) — marker-only "contains Use" checks are explicitly rejected as vacuous.
4. Parameter descriptions ≤250 chars (existing).
5. **Total schema budget:** the concatenated description + parameter-description text across
   all nine tools ≤9,000 chars, with before (4,512) and after measured and recorded.
6. Hint format guard through the shared formatter.
7. The constant `MaxServerInstructionsChars = 12_000` and the test name
   `Load_StaysUnderClaudeCodeInstructionBudget` are removed (the new name states the real
   contract, e.g. `Load_CoreFitsClaudeCodeDeliveryWindow`).

### 5. Evidence loop (report-only)

- The telemetry baseline table above is this design doc's committed snapshot.
- After ~2–3 weeks of dogfood on the new build, re-read the tool mix
  (`workspace onboarding --json` / telemetry export) and append a follow-up section comparing
  share for the starved tools (context, trace, impact, patterns, edit). Report-only — no
  numeric gate, because the telemetry population is small and maintainer-skewed.
- Follow-up checkpoint due ~2026-07-23: re-read the tool mix with `miller workspace onboarding --json`
  and append the before/after comparison to this doc.

### 6. Cross-harness posture

All MCP clients receive identical tool descriptions and the same ServerInstructions core;
Claude Code/Cursor/Codex plugin installs additionally receive the skills (see the §2 delivery
matrix — OpenCode and raw-MCP installs do not). Repo-level guidance continues to ship via
CLAUDE.md → AGENTS.md mirroring. No harness-specific branches. The core is written for the
harshest known client (Claude Code, shared 4KB budget); clients with bigger budgets simply get
the same tight core — by design, not by accident.

### 7. Durable decision record

- New `docs/adr/ADR-0001-guidance-delivery-channels.md`: descriptions are the primary guidance
  channel; ServerInstructions core ≤1,900 chars, most-important-first, no tail; nudges are
  one-line, contextual, compact-only. Includes the 2026-07-02 measurement so future agents do
  not re-derive (or re-fictionalize) the budget.
- CLAUDE.md gains one bullet under server/startup notes pointing at the ADR (then
  `scripts/sync-agents.sh`).

## Acceptance criteria (design-level)

- [ ] Discovery: the ≤1,900-char core alone (no descriptions loaded) tells an agent that all
      nine tools exist and when to reach for each. Usage: each description alone (no
      ServerInstructions delivered) is sufficient for correct use of its tool once loaded.
- [ ] The implementation plan contains the final text of all nine descriptions and the core;
      total schema budget measured before/after and ≤9,000 chars.
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
