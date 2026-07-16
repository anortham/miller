# Autonomous Execution Report - Agent-Interaction Improvements

**Status:** Complete
**Plan:** docs/plans/2026-07-16-agent-interaction-improvements.md
**Branch:** agent-interaction-improvements
**PR:** pending — awaiting user approval to push (project rule: no push without explicit approval)
**Duration:** ~1 working day (plan authored, Codex-reviewed to v2, executed same day)
**Phases:** 6/6 code+doc phases complete (Phase 7 is measurement-gated by design)
**Tasks:** 21/24 complete — the 3 open tasks are deliberately gated: T5.5 (user approval), T7.1 (≥2026-07-28), T7.2 (T5.5 + 2 weeks)

## What shipped

- **Phase 1 — search empty results diagnose themselves:** symbol/file/text empty outputs now render the telemetry classifier's diagnosis with one typed next action (e911a02, 69dde9a); identifier-like text misses get did-you-mean symbol suggestions with byte-fitted `Try:` lines (da7e9cf); file-mode queries carrying prefixes above the workspace root (./, ../, ~/, absolute, repo-dir) now resolve — one root cause, fixed above the index interface so both lookup backends get it (21a6aa6).
- **Phase 2 — content contract friction removed:** `content read` resolves unique path suffixes and suggests near paths on a miss, destructive `remove` stays strict (d5fa8b5); oversized read windows clamp with an exact continuation chain instead of erroring — proven gap-free and terminating on the binary (9af3e59).
- **Phase 3 — output token trims:** impact compact capped at 40 rows with a JSON escape hatch (8b84b9c); edit match-proof compressed to ≤2 lines with JSON evidence pinned byte-for-byte (397ff4a); content-search compact groups hits by source so the 64-char source_id renders once (cf2a322); SignatureMaxLength single-homed in ToolRenderLimits with a mutation-tested guard (6ba6757); ADR-0001 figure refreshed to a self-measured 5,899 (5e637c9, 0248ed1).
- **Phase 4 — vocabulary rework:** instruction core rewritten fully affirmative at 1,893/1,900 chars (677120f); description sweep found 1 violation in 9 and added a durable anti-prohibition gate (e3a35c1); inspect's truncated-refs responses nudge to `trace mode=refs limit=<total>` — explicit limit because trace defaults to 20 (2581708, be7084d, 11d9d43).
- **Phase 5 — injection-only session hooks (code complete, delivery gated):** canary-gated routing block (103e936), fail-open SessionStart/SubagentStart emitter (964f111, 078f472), Claude Code + Codex manifest wiring with an event allowlist that structurally forbids deny/ask-capable hooks (c678ce7). Codex hooks are inert today (openai/codex#16430) — documented truthfully.
- **Phase 6 — instruction-tier expansion:** `miller rules [--harness]` CLI verb prints the embedded routing block framed for cursor/windsurf/cline/kiro/copilot/agents, every format verified against current official docs with URLs in docs/contracts/rules-v1.md (6b4f751); README documents the two tiers honestly (6722044, 669d1df).
- **Follow-up #24:** content's JSON next_actions suggested an invalid `content_kind=all-text` — and the catch-all recovery re-suggested the arg that caused the error, a self-reproducing loop. Fixed with replay-through-real-core pins (d67bf9c).

## Judgment calls (non-blocking decisions made)

- `src/Miller.Indexing/ContentCorpusExternalStore.cs` — Clamp trims tail but never past the requested center; continuation advances min(ctx,199) with EOF cap. The plan's literal arithmetic dropped the requested line, silently skipped pages, then errored past EOF for context_lines ≥ 200; the stated invariants won over the stated formula.
- `src/Miller.Server/Tools/SearchTool.cs` — Prefix recovery lives above ISymbolLookupIndex because FindByFilePathFragment has two independent implementations (in-memory + FTS sidecar, the shipped default); fixing only the plan-named file would have shipped a no-op.
- `src/Miller.Server/Tools/InspectTool.cs` — Truncation nudge fires only at RefLimit (50), not overview's cap-3: firing there would make the impact nudge unreachable on every overview read. Explicit `limit={refs.Count}` because trace's default limit=20 would return fewer refs than the truncated render.
- `src/Miller.Server/Cli/RulesRender.cs` — Windsurf shipped despite no published complete `always_on` example: both frontmatter parts are verbatim-documented in the official activation-mode table; gap recorded in the contract. stdout carries only file content (stderr gets the path) so redirects produce usable files.
- `src/Miller.Server/Tools/ContentTool.cs` (#24) — Read-recovery actions use `content_kind=external_file`, not `mode=all-text`: compact all-text output omits source_id, the very thing that recovery exists to find. Also: `content_kind=all` silently collapses to external_file for search/list, so the tempting all-text→all swap would trade a loud error for a silent lie.
- `hooks/claude-codex-hooks.json` — SubagentStart matcher omitted (documented match-all); a literal `*` parses as an invalid regex. No Windows variant: no commandWindows field exists and every hooks-array entry runs, so a pair would double-emit.
- JSON freeze held throughout: every new diagnostic/suggestion surface is compact-only; empty JSON stays literal `[]`; the only JSON content changes fixed proven bugs (invalid suggestions) within frozen shapes.

## External review (codex, plan-stage)

- **Findings:** 10 (against plan v1)
- **Verified real, fixed in plan v2:** 8 — JSON `[]` break risk (made compact-only), stale baselines T1.3/T2.1/T2.2 (re-baselined with regression pins), wrong file EditTool→EditService.cs, batch conflicts (serialized 5a/5b), unclamped semantics (exact algorithm specified), missing delivery gate (T5.5 added), unmeasurable T7.2 criterion (dropped).
- **Dismissed:** 1 — hook-stdout claim; instead made an in-task doc verification, later proved both hosts accept both payload forms.
- **Flagged:** 0.

## Tests

- Branch gate at d67bf9c: Release build 0 warnings/0 errors; plugin tests 48/48; fast suite 3,502/3,502; scale suite 52/52. First gate run hit the known pre-existing IndexerServiceScanTests:1077 Wait(5000) contention flake (passes 3× isolated at ~110ms); second run clean end-to-end.
- Net +104 tests on the branch (3,398 → 3,502 fast).

## Blockers hit

- None blocking completion. Deliberately gated work remains: **T5.5** (hook delivery: version bump, publish, live smoke on both hosts — needs explicit user approval), **T7.1** (edit-failure telemetry review, ≥2026-07-28), **T7.2** (adoption re-measure, T5.5 + 2 weeks).

## Files changed

- 42 files changed, 4,456 insertions(+), 238 deletions(-) over 243c5c2..HEAD. Core: SearchTool (+468), ContentTool (+252), ContentCorpusExternalStore (+90), CLI rules verb (+129), hooks (+97), tests (+~1,900), plan + contract + ADR docs.

## Next steps

- Review and approve push + PR (this report's PR field updates once created).
- Decide T5.5 timing — hooks reach no user until a release ships; the Phase 7 adoption clock starts there.
- Follow-up backlog (ledgered in .razorback/sdd/progress.md, none tasked): fast-suite flakiness (8 fixed-deadline Wait(5000) sites; wall time at the 30s tripwire ceiling); ScaleTraitConventionTests comment-stripper fails open on `/*` in string literals; routing-guidance duplication (canary-gated hooks block vs ungated README snippet, wording already drifted); EscapeCallString/EscapeShellishArgument 5 copies under 2 names + duplicated Truncate; FtsSymbolSearchIndex/FilePathSymbolLookup duplicated fragment ranking; suffix-ambiguity error lists opaque hashes; `content search --workspace-id all` dies on first workspace without content.db; Hosting/WorkspaceContext.cs namespace-folder mismatch; space-bearing file queries misclassified natural_language; inspect '(use depth=full)' renders at full depth.
