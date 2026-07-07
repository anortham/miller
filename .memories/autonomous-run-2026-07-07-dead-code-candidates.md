# Autonomous Run Report — dead-code candidates (feat/dead-code-candidates)

**Status:** Complete
**Plan:** docs/plans/2026-07-07-dead-code-candidates-implementation-plan.md
**Branch:** feat/dead-code-candidates (10 commits over main @ d926b484)
**Scope:** Miller half of the cross-repo dead-code effort (julie-extractors v2.10.0 shipped separately the same day).

## What shipped

- `miller references candidates [--json]` CLI surface (Tasks 1–3): pure evaluator
  (`DeadCodeCandidates`, Miller.Core), artifact reader (`DeadCodeCandidateReader`,
  Miller.Indexing), CLI rendering + capabilities row.
- ELEVEN suppression rules (nine designed + two corrective): `override_member`
  (signature-derived, Core-owned policy) and `live_member_container` (type with
  evidenced members), plus a test-path fallback inside `test_symbol`.
- Contract docs (Task 4): `docs/contracts/references-candidates-v1.md` (shapes
  copied verbatim from shipped code), Eros-ownership truth-ups in
  `references-export-v1.md` + `cli-eros-v1.md`, CLAUDE.md/AGENTS.md boundary update.
- Dogfood evidence (Task 5 + FINAL VERDICT): gate re-run history 392 → 15 → 10 → 9 → 5.
- julie-extract pin 2.9.0 → 2.10.0 with real published-asset sha256s; download
  restore path verified (sha256 OK, binary 2.10.0).

## Evidence gate

**PASSED.** Full-repo scan, julie-extract 2.10.0: **5 candidates, zero
confirmed-live** (4 hand-verified dead SearchTool/WorkspaceTool members + IFS,
a true find under the write-only rule). First run was 392 candidates at ≈1%
precision. Details: docs/findings/2026-07-07-dead-code-candidates-dogfood.md.

## External review

Codex adversarial review of the julie-extractors lane: 2 findings (java FQ
static receivers, python match-pattern bindings) — both lead-confirmed via live
probes, both fixed and folded into v2.10.0 pre-publish. Grammar-level follow-ups
shipped: tree-sitter-razor fork fix (upstream PR tris203/tree-sitter-razor#27) +
C# A*B pointer-mis-parse recovery.

## Judgment calls

1. `override_member` matches whole-word `override`/`overrides` in signatures —
   a method literally NAMED "override" would be suppressed (conservative
   direction; documented in code).
2. `test_symbol` path fallback matches whole segments (test/tests/__tests__)
   only — `src/protest/` cannot fire it (pinned by test).
3. `live_member_container` uses loaded candidate-kind rows only; a container
   whose sole members are fields/ctors stays a candidate (conservative).
4. IFS classified a TRUE find per the pre-agreed write-only rule, with the
   shell-idiom nuance recorded in the findings doc.
5. Pin sha256s computed from downloaded release archives (julie-extractors
   publishes no .sha256 sidecars — same as v2.9.0).

## Tests

fast 2957/0 · scale 47/0 at HEAD b97ce75 (one earlier single-test flake +
one dotnet bus error under machine load; both clean on rerun).

## Blockers hit

None outstanding. (Mid-run: a shared-MCP jam caused by the v2.9.0 quadratic
scan — root-caused, binary swapped, resolved; documented in session ledger.)

## Next steps

- Merge the PR after review.
- Repoint tree-sitter-razor at upstream if/when PR #27 merges.
- Optional future: surface candidates in report/dashboard/MCP (requires
  explicit user approval per MCP-stinginess rule).
- Watch the 5-candidate list: the 4 dead symbols can now be safely deleted.
