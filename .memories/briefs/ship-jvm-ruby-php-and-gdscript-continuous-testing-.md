---
id: ship-jvm-ruby-php-and-gdscript-continuous-testing-
title: Ship JVM, Ruby, PHP, and GDScript continuous testing providers before v1.27.0
status: active
created: 2026-09-02T04:10:06.163Z
updated: 2026-09-02T12:52:47.202Z
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

## Approved Task 7 resolution

The approved adaptation at `docs/plans/2026-09-02-sbt-ct-workspace-shadow-design.md` uses a provider-private, project-stable mirror of the sbt build-root subtree:

- reconcile changed source files before each operation while retaining warm sbt/Zinc targets;
- run sbt only from the mirror and copy JUnit evidence into immutable generation results;
- keep `IJvmTestBackend`, `CtGenerationPaths`, and public CT contracts unchanged;
- use separate `sbt-workspace` and `sbt-deps` janitor candidates under the 2 GB project cache budget;
- exclude every nested `.git`, install an isolated Git barrier, and document live-Git/global-plugin/`../` builds as v1 limits;
- parse all contained sbt test-report XML by root element, not filename prefix;
- enforce the repository's 260-character Windows path budget before copy/launch;
- gate with source immutability, Windows, Scale, disk, and cold/warm performance evidence.

The user approved the approach after Claude review and delegated the build-root-vs-workspace mirror scope decision. The selected scope is the sbt build-root subtree because CT identity, cache budget, locking, and outputs are project-scoped.

## Success criteria

Tasks 1-9 land with inline review, Release build/fast/Scale/Windows/security gates pass, docs match provider registrations, and no performance regression is introduced.

## Reference

- docs/plans/2026-09-01-ct-providers-jvm-ruby-php-gdscript-implementation-plan.md
- docs/plans/2026-09-02-sbt-ct-workspace-shadow-design.md
