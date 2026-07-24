# Phase 5 context one-call actionability — 2026-07-23

## Decision

Phase 5 is complete for implementation and visible calibration. `context` now returns task-ranked pivots,
bounded pivot implementations, neighbour signatures, typed evidence, and a sufficiency disposition under one
hard output budget. The operator-owned sealed replay remains unspent until the takeover plan's final decision
gate.

## Shipped behavior

- The shared Phase 4 symbol retrieval pipeline supplies full-query and bounded task-term candidates to the pure
  `ContextPivotRanker`; context does not maintain a second lexical engine.
- Pivot selection merges repeated evidence, preserves explicit entry-symbol order, ranks edited files, failing
  tests, and line-aware stack frames, and limits duplicate names, files, and test pivots before filling the
  four-pivot budget.
- Stack parsing handles colon/line frames and Python `File "...", line ...` frames.
- Optional semantic candidates participate only when the semantic policy serves a hybrid query.
  `MILLER_SEMANTIC=off` performs zero semantic calls and remains byte-deterministic.
- Pivot bodies use inspect's extractor spans and BLAKE3 freshness guard. CLI and MCP share the same
  I/O-degradation wrapper.
- Graph neighbours are supplemented by at most two task-ranked same-file declarations per pivot. This recovers
  local evidence such as Rust `LANGUAGE` constants when the extractor has no resolvable graph edge.
- Token allocation prioritizes pivot bodies, usage evidence, and neighbour signatures. Every selected item is
  rendered; the fixed twelve-neighbour omission was removed.
- Compact output uses `## pivots`, `## implementations`, neighbours, and a reasoned disposition. JSON uses the
  same `item_type`, `role`, `reason`, and `confidence` vocabulary in normal and usage modes.
- Empty or ignored anchors return typed diagnostics and a concrete search recovery action. Sufficient bundles
  omit the old unconditional inspect nudge.
- `token_budget` bounds the complete success, empty, diagnostic, and error output. A budget too small for JSON
  returns `{}` when possible; zero returns no bytes.

## Visible one-call replay

The four public tasks whose earlier trajectories overused `context` were replayed against the frozen registered
snapshots with semantic retrieval disabled. Token estimates use Miller's UTF-8-bytes-divided-by-four estimator.

| Task | Previous context tokens | Phase 5 tokens | One-call evidence |
|---|---:|---:|---|
| `dev-001` | 2,973 | 1,727 | exact normalizer body, POSIX body, Windows neighbour |
| `dev-004` | 3,987 | 2,296 | `recoverWorkspace` body and candidate-selection evidence |
| `dev-007` | 3,972 | 3,983 | external scanner dispatch body, scanner helper, grammar body, contract test |
| `dev-008` | 3,991 | 1,724 | Rust smoke-test body, `LANGUAGE` neighbour, grammar export, binding bodies |

`dev-007` spends eleven more context tokens than the previous bounded render, but the previous trajectory
followed it with seven calls and failed the call budget. The Phase 5 bundle contains the edit target and
implementation in the first call, so tokens-to-action and calls-to-action both improve.

All four results report `sufficient` and emit no next action. This is visible calibration, not sealed decision
evidence.

## Performance

- Twenty-one separate CLI processes queried the real registered Goldfish snapshot with semantic retrieval off:
  minimum 280 ms, median 280 ms, p95 290 ms, maximum 530 ms. These values include process startup.
- The real-extractor in-process scale gate runs twenty-one steady-state actionable context calls and requires
  p95 below 100 ms.

## Claude review

Claude reported four findings:

- Rejected: a claimed C# lambda-shadowing compile failure was disproved by the live Release build and focused
  test compilation.
- Accepted: CLI body reads now use the same guarded path as MCP, so transient file I/O degrades to unavailable
  evidence instead of terminating the command.
- Accepted: usage-mode symbol items now retain task-specific reasons and explicit roles.
- Accepted: a scanner-specific scoring bonus was removed. Generic code-token density, kind, signature, and path
  affinity preserve the visible Razor result without vocabulary-specific ranking.

## Final verification

- Fast suite: 4,703 passed, 2 expected skips, 0 failed.
- Scale suite: 87 passed, 0 failed.
- Release build: 0 warnings, 0 errors.
- Native AOT publish: `osx-arm64` succeeded after the required runtime-target restore.
- Plugin contracts: 48 passed.
- Agent-efficiency Python harness: 99 passed.
- Retrieval evaluator: 95 passed.

## Gate status

- Hard output budgets: passed in compact and JSON success, empty, diagnostic, and error paths.
- One-call task completion and tokens-to-action: passed on the four visible context-loss tasks.
- Real artifact p95: passed at 290 ms including CLI startup; in-process scale ceiling remains 100 ms.
- Semantic-off determinism and zero work: passed.
- Selected equals rendered: passed in compact, JSON, normal, and usage modes.
- Sealed trajectory gate: intentionally deferred to the final frozen replay.
