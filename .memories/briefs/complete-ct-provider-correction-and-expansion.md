---
id: complete-ct-provider-correction-and-expansion
title: Complete CT provider correction and expansion
status: completed
created: 2026-08-28T01:10:53.661Z
updated: 2026-08-28T15:43:10.639Z
tags:
  - continuous-testing
  - javascript
  - qml
  - visual-basic
  - go
  - language-parity
---

## Goal

Correct JavaScript test discovery, review and close QML runner gaps, add Visual Basic .NET support, and add a first-class Go continuous-testing provider.

## Why now

The current JavaScript working tree has known runner-contract errors. Julie now supplies enough cross-language test-role evidence that Miller owns the next CT expansion work.

## Constraints

- Preserve the current dirty Miller checkout and unrelated user work.
- Follow Miller CT safety, freshness, isolation, and Scale-test rules.
- Keep `Miller.Core` free of I/O dependencies and add no MCP tools.
- Use Julie facts without inventing unsupported extractor evidence; Go `t.Run` child extraction and other Julie gaps remain bounded follow-up unless required for correctness.
- Support every relevant language family honestly and fail closed on incomplete evidence.
- Do not remove the Julie audit worktree until its documents and Goldfish state are preserved and the user approves removal.

## Success criteria

- Jest/Vitest discovery matches documented defaults and bounded literal config behavior without false cases or false empty results.
- QML coverage names and supports the approved Qt Quick Test runner/build-system shapes, with honest unsupported reporting for the rest.
- `.vbproj` projects and `.vb` facts work through the existing .NET CT provider and selector contracts.
- Go projects can be discovered, selected, run under supervised CT paths, and parsed into stable case verdicts.
- Focused, fast-suite, required Scale, build, and Windows gates pass for the completed scope.

## References

- Julie audit worktree: `docs/findings/2026-08-27-continuous-testing-language-readiness-audit.md`
- Miller CT contract: `docs/continuous-testing.md`
- Existing QML implementation plan: `docs/plans/2026-08-24-qml-continuous-testing-implementation-plan.md`
