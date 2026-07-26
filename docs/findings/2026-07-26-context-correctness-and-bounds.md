# Context correctness and bounds re-audit — 2026-07-26

## Decision

The Phase 10 `context` re-audit is locally complete. The existing Phase 5 task-ranked pivot/body design remains
the right architecture. This pass closed correctness and bounded-work gaps without changing the nine-tool MCP
surface, spending sealed evaluator tasks, or touching Julie.

## Corrections

- Non-positive MCP and CLI token budgets now produce zero bytes. The CLI returns before workspace or index work
  and cannot attach an out-of-budget diagnostic.
- `exclude_tests` is applied only to `reference_mode=usage`, matching the public parameter contract. Reference-off
  pivot selection remains unchanged, while usage-mode semantic test filtering is pinned.
- Failing-test and stack-trace symbol hints inspect at most 24 distinct identifier tokens and six exact-name
  matches per token. Ambiguous entry anchors admit at most 10 candidates.
- Stack parsing admits at most 24 frames and preserves textual order across recognized .NET and Python shapes.
- Every cap emits a truthful diagnostic. Matched and unmatched capped evidence remain distinct; an unexamined
  suffix never produces an absolute no-match claim.
- Compact output now labels the section `anchor diagnostics`, because ambiguous or capped anchors may still
  contribute pivots. JSON retains `anchor_diagnostics`.
- String bounds no longer split valid UTF-16 surrogate pairs, including when the pair starts at the cut.
- Semantic/query-term rescue bodies remain partial unless an authoritative task anchor is rendered. The existing
  three-quarter selection reserve remains the tested conservative allowance for the byte-based token estimator.

## Contract

[`context-json-v1.md`](../contracts/context-json-v1.md) freezes the CLI/MCP input mapping, pivot and anchor work
caps, disposition vocabulary, tiny-budget behavior, semantic-off guarantee, and exact/fallback reference
boundary. [`cli-eros-v1.md`](../contracts/cli-eros-v1.md) points to that context-specific contract.

## Review disposition

Fresh subscription-backed Claude reviews were repeated until both code/tests and public docs returned
`APPROVE`.

Accepted review findings covered bounded anchor work, cap diagnostics, mixed-language frame order, usage-mode
test-filter coverage, zero-byte CLI behavior, exact boundary tests, and contract precision. Rejected findings
were disproved by live source: identifier tokens already deduplicate before their cap, diagnostic values are
bounded in JSON and newline-sanitized in compact output, `FindByName` has a documented prebuilt DocId order, and
anchor diagnostics do not drive recovery actions.

## Verification

- Focused Context ecosystem: 156 passed, 0 failed.
- Fast suite: 5,066 passed, 2 expected skips, 0 failed.
- Scale suite: 91 passed, 3 expected semantic-runtime skips, 0 failed.
- Release build: 0 warnings, 0 errors.
- Native AOT publish: `osx-arm64` succeeded after restoring the runtime target.

The operator-owned sealed replay remains unspent for the final takeover decision gate.
