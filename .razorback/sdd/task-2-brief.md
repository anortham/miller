### Task 2: Extract a reusable sidecar protocol processor without changing stdio

**Files:**
- Modify: sidecar `src/protocol.rs:36-214`
- Test: sidecar `tests/protocol_tests.rs`
- Test: sidecar `tests/serve_tests.rs`

**Interfaces:**
- Consumes: frozen protocol-v1 request limits and `EmbedEngine`.
- Produces: `ProtocolReply` and `process_line`, usable by stdio and broker connections.

**Contract inputs:** Task 1 contract; protocol conformance A1-A23 and B1-B6.

**File ownership:** Sidecar `src/protocol.rs`, protocol tests only.

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**Step 1: Write failing equivalence tests**

```rust
#[test]
fn reusable_processor_matches_stdio_for_success_error_blank_and_shutdown() {
    for line in fixture_lines() {
        assert_eq!(processor_reply(line), stdio_reply(line));
    }
}
```

**Step 2: Verify red**

Run: `cargo test reusable_processor_matches_stdio`

Expected: FAIL because the reusable processor does not exist.

**Step 3: Extract the interface**

```rust
pub struct ProtocolReply {
    pub line: String,
    pub stop_connection: bool,
}

pub fn process_line<E: EmbedEngine>(
    line: &[u8],
    engine: &E,
    limits: RequestLimits,
) -> std::io::Result<Option<ProtocolReply>>;
```

`run_loop_with_limits` becomes only capped line reading plus `process_line` plus write/flush. Blank lines return `Ok(None)`. In stdio mode, `stop_connection` exits the process loop exactly as today. In broker mode, the response is flushed and only that connection handler exits; the accept loop, service lock, accelerator lease, and engine remain live. No envelope or error literal changes.

**Step 4: Run focused and full fast sidecar gates**

Run: `cargo test protocol_tests && cargo test serve_tests`

Then: `cargo test`, `cargo clippy --all-targets -- -D warnings`, `cargo fmt --all -- --check`, and the Python harness tests.

**Step 5: Apply commit mode**

`parallel-lead-commit`: hand the verified diff to the lead; do not commit from this lane.

**Acceptance criteria:**
- [ ] Existing stdio output is byte-identical for every fixture row.
- [ ] EOF and `shutdown` retain existing behavior in stdio mode.
- [ ] Broker `shutdown` closes only the requesting connection after its response is flushed.
- [ ] No new protocol field, method, or error code exists.

