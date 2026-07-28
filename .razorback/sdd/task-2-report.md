# Task 2 report: reusable sidecar protocol processor

## Result

- Added public `ProtocolReply { line, stop_connection }` and `process_line`.
- Kept `run_loop_with_limits` responsible for capped framing, newline write, flush, and stdio loop exit.
- Preserved the frozen protocol envelope, compact serialization, blank-line behavior, EOF behavior, and four-code error vocabulary.
- Did not stage or commit.

## TDD evidence

- RED: `cargo test reusable_processor_matches_stdio` failed with `E0425` because `protocol::process_line` did not exist.
- GREEN: the same command passed 1 test with 0 failures.
- The test compares exact serialized stdio output for success, error, blank, and shutdown inputs.
- The test proves shutdown returns `stop_connection = true` while a later processor call still serves health, leaving connection/process policy to the transport caller.

## Verification

- `cargo test --test protocol_tests`: 45 passed, 0 failed.
- `cargo test --test serve_tests`: 3 passed, 0 failed, 5 model-backed tests ignored.
- `cargo test`: 219 passed, 0 failed, 25 model-backed tests ignored.
- `cargo clippy --all-targets -- -D warnings`: passed.
- `cargo fmt --all -- --check`: passed.
- `python3 -B -m unittest discover -s scripts/tests -p 'test_*.py'`: 30 passed.
- `git diff --check`: passed.
- Miller post-change impact identified only the protocol/serve call chain and its protocol tests.

## Changed files

- `src/protocol.rs`
- `tests/protocol_tests.rs`

`tests/serve_tests.rs` was not changed; its existing process-level serve coverage and the existing fast CLI EOF test remained green.

## Architecture Quality

- **Affected modules:** sidecar `protocol`; stdio `run_loop_with_limits`; future broker connection adapter.
- **Caller-facing interface:** two public items expose one processed reply without exposing `Response`, `Outcome`, dispatch, or serialization details.
- **Depth/locality check:** protocol parsing and compact JSON serialization remain in one module; transports only frame bytes, flush replies, and interpret `stop_connection`.
- **Test surface:** the test calls `process_line` exactly as the broker will and compares it to the existing stdio caller.
- **Seams/adapters:** the seam is required by the approved next broker task; stdio is the first adapter and broker IPC will be the second.
- **Rejected shortcuts:** no duplicated broker parser, no public response envelope, no transport-specific shutdown branch, no new dependency, method, field, or error code.
- **Architecture risk:** low.

Checklist:

- Complexity stays local.
- The interface is smaller than the parsing, dispatch, and serialization behavior it unlocks.
- Tests exercise the caller-facing interface.
- The seam is required for the next approved adapter.
- No speculative extensibility was added.
- The structural cause, stdio-only protocol processing, is removed.

## Judgment calls

- Used the existing public `health::Limits` type rather than inventing a duplicate `RequestLimits` name from the plan sketch.
- `process_line` accepts a line already bounded by its transport; capped reading remains transport-owned so broker and stdio can enforce limits without allocating oversized lines.
- An oversized stdio line is drained by `read_line_capped` and uses the same private serializer because no request bytes remain to pass to `process_line`.
- `ProtocolReply.line` excludes the newline. Each transport owns framing and must flush before acting on `stop_connection`.

## Final repository state

- Path: `/Users/murphy/source/julie-semantic-sidecar/.worktrees/shared-semantic-broker`
- Branch: `codex/shared-semantic-broker`
- HEAD: `9c47ea92d78d8dd4c10abc61f728cdf5aac9a38d`
- Dirty state: modified `src/protocol.rs` and `tests/protocol_tests.rs` only.
