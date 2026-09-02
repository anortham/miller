---
id: ship-jvm-ruby-php-and-gdscript-continuous-testing-
title: Ship JVM, Ruby, PHP, and GDScript continuous testing providers before v1.27.0
status: active
created: 2026-09-02T04:10:06.163Z
updated: 2026-09-02T12:58:46.986Z
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

Tasks 1–6 are implemented and accepted. The approved Task 7 architecture is committed at `2670cb7f` in `docs/plans/2026-09-02-sbt-ct-workspace-shadow-design.md`.

The executable child plan is drafted at `docs/plans/2026-09-02-sbt-ct-build-root-shadow-implementation-plan.md` and awaits the writing-plans approval gate. It contains two serialized worker commits:

1. provider-private project-stable build-root reconciliation with split janitor candidates and cold/warm metrics;
2. sbt backend, JVM/factory registration, report parsing/copying, Scale support, and ADR-0007.

The user delegated the mirror scope decision; the selected boundary is the sbt build-root subtree. Whole-workspace copying was rejected because CT identity, lock, output root, and the 2 GB cache budget are per discovered project.

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
