---
id: ship-jvm-ruby-php-and-gdscript-continuous-testing-
title: Ship JVM, Ruby, PHP, and GDScript continuous testing providers before v1.27.0
status: active
created: 2026-09-02T04:10:06.163Z
updated: 2026-09-02T04:10:06.163Z
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

## Why now

The release audit found the provider plan untracked and unimplemented. The user made it a release prerequisite.

## Constraints

- Execute `docs/plans/2026-09-01-ct-providers-jvm-ruby-php-gdscript-implementation-plan.md` completely.
- Preserve the already verified v1.27.0 release fixes as the implementation base.
- Use one isolated worktree and serialized task commits because provider tasks share registration/inventory files.
- TDD each provider, verify external runner surfaces from official docs or installed tools, keep real toolchain tests Scale-tagged.
- Do not resume publication until the implementation and branch gates are green and the user re-authorizes release.

## Success criteria

Tasks 1-9 land with inline review, Release build/fast/Scale/Windows/security gates pass, docs match provider registrations, and no performance regression is introduced.

## Reference

- docs/plans/2026-09-01-ct-providers-jvm-ruby-php-gdscript-implementation-plan.md
