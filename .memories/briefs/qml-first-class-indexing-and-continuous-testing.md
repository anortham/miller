---
id: qml-first-class-indexing-and-continuous-testing
title: QML first-class indexing and continuous testing
status: active
created: 2026-08-24T13:51:31.997Z
updated: 2026-08-24T14:21:26.002Z
tags:
  - qml
  - indexing
  - continuous-testing
---

## Goal

Treat QML as a first-class language across Julie Extractors and Miller, including an honest continuous-testing target.

## Why Now

QML has become materially more important to the product and needs deliberate quality, not incidental generic-language coverage.

## Constraints

- Julie Extractors remains the source of versioned extraction facts; Miller consumes those facts and resolves/query-routes them.
- Capability claims require golden evidence and explicit open gaps.
- QML testing must follow supported Qt Quick Test contracts and work on Windows, Linux, and macOS.
- Do not claim QML coverage when only C++ coverage exists; native QML coverage has separate tooling/licensing constraints.
- Preserve fast narrow language/provider verification and keep slow real-tool smokes outside default suites.

## Success Criteria

- QML extraction deeply covers language- and module-native semantics with current grammar evidence.
- Miller exposes and resolves QML facts through normal user-facing tools with regression fixtures.
- Miller can discover, enable, select, run, and import Qt Quick Test results as a declared CT framework.
- Unsupported Qt build-system or coverage lanes are explicit contract gaps, not silent omissions.

## Status

Audit and planning completed on 2026-08-24. Miller indexing/resolution is specified in `docs/plans/2026-08-24-qml-first-class-indexing-resolution-design.md` and its implementation plan. Qt Quick Test continuous testing is specified in `docs/plans/2026-08-24-qml-continuous-testing-design.md` and its implementation plan. Implementation is approval-gated.
