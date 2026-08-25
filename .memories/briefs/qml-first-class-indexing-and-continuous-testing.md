---
id: qml-first-class-indexing-and-continuous-testing
title: QML first-class indexing and continuous testing
status: completed
created: 2026-08-24T13:51:31.997Z
updated: 2026-08-25T01:29:39.804Z
tags:
  - qml
  - indexing
  - continuous-testing
---

## Goal

Treat QML as a first-class language across Julie Extractors and Miller, including an honest continuous-testing target.

## Landed

- Miller consumes the released Julie 2.35.1 QML contracts and provides typed QML visibility/resolution across store, artifact, and bounded one-shot paths.
- User-facing search, inspect, trace, impact, edit, and tests evidence covers QML with regression fixtures.
- Continuous testing discovers, selects, runs, and imports Qt Quick Test through CMake 3.21+, CTest JSON v1, and JUnit using generation-scoped out-of-tree builds.
- Unsupported qmake, function-level selection, and native QML coverage remain explicit gaps.
- Linux and Windows fast/Scale gates passed at source commit a7e04ecb; real Qt execution remains NOT VERIFIED because the available hosts lack Qt Quick Test development tooling.
- Grok adversarial review completed with six accepted findings fixed and lead-only clean closure.

## Evidence

- `docs/findings/2026-08-24-qml-continuous-testing-verification.md`
- `docs/findings/2026-08-25-qml-grok-adversarial-review.md`
- `docs/plans/2026-08-24-qml-first-class-indexing-resolution-implementation-plan.md`
- `docs/plans/2026-08-24-qml-continuous-testing-implementation-plan.md`
