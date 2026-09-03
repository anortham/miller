---
id: ship-jvm-ruby-php-and-gdscript-continuous-testing-
title: Ship JVM, Ruby, PHP, and GDScript continuous testing providers before v1.27.0
status: completed
created: 2026-09-02T04:10:06.163Z
updated: 2026-09-03T03:52:34.405Z
tags:
  - continuous-testing
  - providers
  - jvm
  - ruby
  - php
  - gdscript
  - release
---

## Goal

Implement and verify the approved CT-provider plan before resuming the held v1.27.0 release.

## Current state

Tasks 1–7 are implemented and accepted. Task 7 landed through child-plan commits `850e651d` through `c02c2d9e`: a provider-private project-stable sbt build-root mirror, split janitor candidates, class-level sbt backend, JVM/factory registration, contained report copying, Scale support, and ADR-0007.

Task 7 evidence: 31 shadow tests, 19 backend tests, 7 factory tests, and the CT Scale convention passed; Release build passed with zero warnings/errors; the bare fast suite passed 9,593 with 9 platform/tool skips. The exact sbt Scale smoke skipped honestly because `sbt` is absent.

Task 8 (GDScript/GUT) is next, followed by Task 9 documentation and the final Release/Scale/Windows/performance/release reconciliation gates.

## Constraints

- Preserve the already verified v1.27.0 release fixes as the implementation base.
- Use the existing isolated feature worktree and serialized task commits.
- TDD each provider and verify runner surfaces from official docs or installed tools.
- Do not resume publication until Tasks 7–9 and all Release/fast/Scale/Windows/performance gates are green and the user re-authorizes release.

## Success criteria

Tasks 1–9 land with inline review, Release build/fast/Scale/Windows/security gates pass, docs match registrations, performance does not regress, held release work is reconciled cleanly, and the new Miller release is explicitly approved and pushed.

## References

- docs/plans/2026-09-01-ct-providers-jvm-ruby-php-gdscript-implementation-plan.md
- docs/plans/2026-09-02-sbt-ct-workspace-shadow-design.md
- docs/plans/2026-09-02-sbt-ct-build-root-shadow-implementation-plan.md
