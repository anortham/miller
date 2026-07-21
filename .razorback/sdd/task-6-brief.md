### Task 6: RC→v0.1.0 promotion gate — target-machine throughput floor (sidecar repo)

**Files:**
- Create (in `/Users/murphy/source/julie-semantic-sidecar`): `docs/rc-promotion-gate.md`, `scripts/bench-throughput.py`

**Interfaces:**
- Consumes: the existing probe methodology at `$HOME/.claude/jobs/385e567c/tmp/sidecar-timing-probe.py` (64-text and 250-text `embed_batch` rounds over stdio) and the v0.1.0-rc.2 measured facts: **78.9 units/s** steady-state (64-text), 77.4 (250-text), P0 llama-server floor 52.3 units/s, M2 Ultra.
- Produces: a repeatable `scripts/bench-throughput.py --binary <path> [--batch 64] [--rounds N]` that prints units/s and a PASS/FAIL against a floor; `docs/rc-promotion-gate.md` — the checklist an RC must pass before promotion to a non-prerelease, including the new floor: **≥ 40 units/s steady-state on the M2 Ultra reference machine (64-text batches, warm model)** — half of rc.2's observed rate, chosen to catch backend regressions (a CPU-only regression measures ~6.6) without flaking on machine noise. Also records the WHY: the rc.1 lesson (harness numbers ≠ engine numbers; CPU-only shipped 12× under the design floor — `docs/findings/2026-07-20-first-real-shadow-converge-benchmark.md` in Miller).

**Contract inputs:** Conformance suite and unit tests remain gate items; this task ADDS the throughput floor, it does not restate or renumber existing release steps. Pushing to the sidecar repo is allowed; promotion itself stays a user decision.

**File ownership:** Create/Modify in `/Users/murphy/source/julie-semantic-sidecar`: `docs/rc-promotion-gate.md`, `scripts/bench-throughput.py`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The gate document and the tool that makes the floor checkable in one command, so RC→v0.1.0 promotion (a pending user decision) can be run against rc.2 with the floor in force.

**Approach:** Port the probe script into the repo with a stable CLI (no `Date.now`-style hidden state; JSON output with `--json`). Bench script must verify the binary answers `health ready:true` before timing (a `model_not_prepared` run must FAIL the bench, not measure zeros). `cargo test` untouched.

**Acceptance criteria:**
- [ ] `scripts/bench-throughput.py --binary target/release/julie-semantic-sidecar` on this machine reports ~rc.2 numbers and PASS.
- [ ] Gate doc lists the floor, the reference machine, the command, and the rc.1 rationale.
- [ ] Committed to the sidecar repo (push allowed; no release action).

