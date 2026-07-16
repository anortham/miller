---
id: miller-agent-interaction-improvement-program-post-
title: Miller — agent-interaction improvement program (post-1.9.0)
status: active
created: 2026-07-16T12:58:31.853Z
updated: 2026-07-16T13:15:26.857Z
tags:
  - miller
  - project-direction
  - adoption
  - search-quality
  - hooks
  - telemetry
  - v1.9.0
---

## What Miller Is

- Read-only .NET 10 SQLite/MCP consumer of `julie-extract` output: the free, deterministic, embedding-free local code-intelligence core. Replacement story = Miller + `julie-extractors` + Eros. Boundaries unchanged: no new MCP tools without explicit approval (9-tool surface), no semantic/vector features, language parity load-bearing, `Miller.Core` zero-I/O, fast/Scale test split.

## Current State — 2026-07-16

- Miller v1.9.0 released and verified (pin julie-extract 2.10.0 era); razor/C#/SQL support and grammar improvements landed over recent releases.
- 2026-07-16 six-topic agent→Miller audit complete (telemetry 30d/30k calls + ponytail/context-mode comparison). Headline findings: inspect+search still ~80% of calls; edit 26% error rate; content 13.8% errors (contract friction); text-mode search empties stuck ~40% and 87.5% diagnosed `true_no_hit`; the computed `empty_diagnosis` reaches telemetry only, never the agent; both comparison projects deliver guidance via SessionStart hook injection because MCP ServerInstructions has no portable always-on channel.
- Multi-phase plan written and **adversarially reviewed by Codex (v2 incorporates the review)**: JSON empty results frozen at `[]` (compact-only diagnostics), tasks re-baselined against already-shipped display_path/alias/path-fragment behavior, exact clamp semantics, explicit-event hook design, and an approval-gated hook delivery/trust/smoke gate (T5.5) that starts the measurement clock.

## Direction

Adoption and actionability over new capability: make the existing 9 tools cheaper to trust (diagnosis-bearing empty output, contract-tolerant inputs, leaner compact rendering), then widen guidance delivery beyond the truncated ServerInstructions channel (injection-only plugin hooks + instruction-tier harness files), then re-measure.

## Work Queue

1. **Execute `docs/plans/2026-07-16-agent-interaction-improvements.md` (v2, Codex-reviewed — pending user approval):**
   - P1 empty-result actionability (search + content diagnosis surfacing, did-you-mean, probe-first file-mode residual fixes) — compact-only, JSON pinned
   - P2 content contract fixes (unique-suffix resolution + near-path suggestions, window clamp with exact semantics, alias regression pins)
   - P3 output trims (impact row cap 40, content source_id dedup, edit match-proof ≤2 lines via EditService.cs, shared SignatureMaxLength)
   - P4 affirmative-redirect vocabulary rework (instructions + descriptions, budgets unchanged)
   - P5 injection-only SessionStart/SubagentStart hooks for Claude Code + Codex plugins (explicit event argument, fail-open, MILLER_SESSION_HOOKS=0 opt-out) + **approval-gated T5.5 delivery gate** (release, install, Codex trust review, live smoke)
   - P6 `miller rules` CLI verb (embedded resource) + instruction-tier harness docs (verified formats only)
   - P7 re-measure: T7.1 edit-failure telemetry ≥2026-07-28; T7.2 adoption re-measure ≥2 weeks after T5.5 delivery (text-empty <30%, content errors <5%, non-inspect/search share >25%)
2. Ranking polish (present-but-not-top) stays benchmark-gated and deferred until P7 data.
3. Public site token-savings metrics — still open, unchanged priority.

## Guardrails (standing)

- Hooks may only inject prompts; any deny/ask/modify hook is out of policy (manifest test enforces event allowlist). Hooks ship to nobody until the T5.5 release gate.
- Every guidance surface ships with a budget test + drift/canary test; never raise the 1,900/900/9,000 caps. Empty compact output ≤6 lines/≤400 chars, replace-don't-stack.
- JSON output shapes frozen for this plan (empty search JSON stays `[]`); Eros contracts additive-only.
- Redirect vocabulary: affirm capability, name the alternative, no bare-NOT without an alternative.
- context-mode is Elastic-2.0: ideas only, no code.
- No push/tag/release/pin-bump without explicit approval; pushed release-prep = live marketplace release.

## References

- `docs/plans/2026-07-16-agent-interaction-improvements.md` — the multi-phase plan (v2)
- `.memories/` checkpoint `checkpoint_c0039ff3` (2026-07-16) — full audit evidence trail
- `docs/adr/ADR-0001-guidance-delivery-channels.md` — channel budgets + rationale
- `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md` — ranking evidence base
