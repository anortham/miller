# Inspect Correctness And Bounds

**Date:** 2026-07-26
**Scope:** Phase 10 Miller-to-Julie takeover re-audit for the existing `inspect` MCP tool

## Result

Miller keeps one file/symbol inspect surface. The pass adds typed test evidence, exact test locations,
implementation and inheritance relationships, stable file hierarchy metadata, bounded MCP documentation, and a
12 KiB ceiling on every final MCP response without changing exhaustive CLI/process output.

Inbound, outgoing, implementation, and inheritance evidence now come from one SQLite connection and one artifact
snapshot. Exact and unresolved fallback tiers remain separate. Test locations derive only from the complete
deduplicated exact-reference symbol set and typed extractor test evidence.

## Contract

- MCP file listings return at most 10 symbols.
- Every final MCP response is valid compact text or JSON within 12 KiB; irreducible metadata returns
  `refusal/output_metadata_too_large` with zero served-result attribution.
- MCP documentation is Unicode-safely bounded to 2 KiB and reports `doc_truncated` only when shortened.
- Static cores and CLI/process output retain complete documentation and are exempt from the final MCP ceiling.
- File rows expose language, start/end lines, parent symbol identity, stable nesting depth, parent-qualified
  compact labels, and typed test evidence.
- Overview/full symbol reads expose bounded exact test locations and typed implementation/inheritance sections
  with exact/fallback coverage.
- Miller does not infer required methods, parameter types, return types, exports, or dependencies from
  signatures. Those sections require extractor-provided language-parity facts.

The active public shape is
[`inspect-json-v1.md`](../contracts/inspect-json-v1.md).

## Verification

- Focused Inspect/reference/resolver/continuation/guidance gate: 238 passed.
- Fixtures prove one-snapshot inbound/outgoing partitioning, exact-only test location deduplication, unresolved
  typed fallbacks, overview/full truncation counts, multi-level hierarchy under kind filters, Unicode-safe
  documentation truncation, CLI/MCP separation, and refusal telemetry.
- `git diff --check`: passed.
- Fresh read-only Claude implementation review: `approve`, no findings.
- Fresh read-only Claude tests/contracts review: `approve`, no findings.
- Fast suite: 5,046 passed, 2 skipped.
- Scale suite: 91 passed against the restored pinned extractor, 3 semantic-environment skips.
- Release build: 0 warnings, 0 errors.
- Native AOT `osx-arm64` publish: passed after the required runtime-target restore.

## Review Disposition

Accepted findings fixed filtered hierarchy depth, bounded long documentation, single-snapshot evidence reads,
defensive hierarchy maps, explicit parent paths, asymmetric evidence tests, Unicode and CLI separation coverage,
negative test-location cases, typed fallback coverage, truthful count fields, and telemetry assertions.

Rejected claims were disproved by live code: CLI bypasses the MCP instance budget; test-location IDs are computed
before display paging; `ToolDiagnosticRenderer` records diagnostic code/class; `TruncateUtf8` reserves suffix
bytes; and the private fixed typed-kind set always supplies both dictionary keys.
