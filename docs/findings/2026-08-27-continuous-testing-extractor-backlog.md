# Julie continuous-testing extractor backlog

**Status:** Non-executable backlog note, recorded 2026-08-28.

The Julie source plan at
`/home/murphy/source/julie-extractors/.claude/worktrees/ct-language-audit-plan/docs/plans/2026-08-27-continuous-testing-extractor-evidence.md`
is not executable as written. Its contract shapes and helper names are stale; do not run it or
copy its steps into an implementation plan. The source plan and its finding remain read-only in
the Julie audit worktree at commit `2ea9b0daa2e736f9248d8caf4c475e47dea0d522`.

The authoritative ownership ranking is
[`2026-08-27-continuous-testing-language-readiness-audit.md`](2026-08-27-continuous-testing-language-readiness-audit.md).
The Miller implementation now owns language-family/path mapping, complete file evidence,
detailed test roles, provider symbol identity, and the JavaScript, QML, .NET, and Go provider
contracts documented in [`continuous-testing.md`](../continuous-testing.md) and
[`contracts/tests-cli-v1.md`](../contracts/tests-cli-v1.md).

## Verified Julie backlog

- Go `t.Run`: emit exact literal child identity when Julie's Go extractor can prove it. Miller
  currently discovers and runs top-level `TestXxx` cases only; child subtests remain outside its
  V1 case contract.
- F#: add the extractor and capability evidence needed for `.fsproj` source rows. Miller can
  discover an `.fsproj`, but project discovery is not proof that F# source is extracted.
- Scala: complete evidence for parameterized tests and teardown lifecycle.
- R: add primary testthat lifecycle evidence for setup and teardown before claiming those roles.

These are bounded producer facts, not blockers for the current Miller providers. Any future Julie
work must use the live contracts in the Julie repository and add focused extractor evidence before
an implementation plan is made executable. Miller has no provider for the other languages listed
in the audit; adding one remains a separate Miller decision.

No Julie file was modified and no Julie worktree was removed while recording this handoff.
