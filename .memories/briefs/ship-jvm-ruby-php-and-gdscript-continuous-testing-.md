---
id: ship-jvm-ruby-php-and-gdscript-continuous-testing-
title: Ship JVM, Ruby, PHP, and GDScript continuous testing providers before v1.27.0
status: active
created: 2026-09-02T04:10:06.163Z
updated: 2026-09-02T09:31:37.953Z
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

## Current blocker

Task 7 cannot implement a normal in-place sbt 1.x provider without violating CT write isolation. An official sbt 1.13.0 probe created both workspace `target/` and `project/target/` during build loading even when boot/global/Ivy/Coursier caches were redirected, the server and generated build properties were disabled, and a session target override was supplied. Official sbt source confirms the canonical working directory is the application base and `target/out` is allocated before session settings.

Continuing requires an explicit product choice: design a generation-owned source/build shadow with measurable copy and compatibility costs, or remove runnable sbt from this release plan. Release remains held.

## Success criteria

Tasks 1-9 land with inline review, Release build/fast/Scale/Windows/security gates pass, docs match provider registrations, and no performance regression is introduced.

## Reference

- docs/plans/2026-09-01-ct-providers-jvm-ruby-php-gdscript-implementation-plan.md
