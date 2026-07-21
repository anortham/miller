# Task 6 report — RC->v0.1.0 promotion-gate throughput floor (sidecar repo)

> Note: this path previously held a stale unrelated report ("Retrieval eval harness + dev golden
> set" from `worktree-semantic-integration`). `.razorback/sdd/` is gitignored scratch and
> `task-6-brief.md` in this directory is this run's brief, so the stale file was overwritten.

**Status:** COMPLETE — committed to `/Users/murphy/source/julie-semantic-sidecar` (branch `main`,
local, not pushed).
**Commit SHA:** `76923d21075f5ab7122223ebf63aeb6469fe70af`

## Deliverables

- `scripts/bench-throughput.py` — one-command steady-state throughput benchmark.
  - CLI: `--binary <path>` (required), `--batch 64` (default; rejects >250 with a clear argparse
    error before spawning), `--rounds 4` (default; measured rounds after one discarded warm-up),
    `--floor 40.0` (default = the gate floor), `--json`.
  - Speaks the frozen `julie.embedding.sidecar` v1 protocol (compact NDJSON, schema/version 1)
    over stdin/stdout, launching the binary with the `serve` verb.
  - Sends `health` first and FAILS (exit 2, actionable message) unless `ready:true` — a
    `model_not_prepared` binary is never measured.
  - Times `embed_batch` rounds after a warm-up round; reports steady-state units/s and PASS/FAIL.
  - Deterministic input texts generated per (round, index) — no randomness, no timestamps. Round
    structure ported from the referenced probe (`385e567c/tmp/sidecar-timing-probe.py`).
  - Exit codes: 0 PASS, 1 below floor, 2 not-ready / bad args / protocol error.
- `docs/rc-promotion-gate.md` — the RC promotion checklist. References the existing conformance
  suite, unit tests (both feature sets), and packaged smoke without restating/renumbering them,
  then ADDS the throughput floor as item 4.

## Measured throughput (M2 Ultra, metal backend, warm model)

- **64-text batches: 82.8 units/s** (repeat run 88.1) — PASS at floor 40, exit 0.
- 250-text batches: 79.7 units/s — PASS at floor 40, exit 0.
- Both within the expected ~rc.2 range (rc.2 recorded 78.9 / 77.4).

## Negative-path proof

- Same binary, `JULIE_EMBEDDING_CACHE_DIR` pointed at an empty temp dir.
- Output: `bench: FAIL — sidecar health is not ready (degraded_reason=model_not_prepared); a
  not-prepared sidecar cannot be benched — run \`prepare\` first.` — **exit code 2**.
- `--json` variant emits `{"error": "...", "pass": false}`, exit 2.
- Extra: `--batch 251` rejected pre-spawn (`exceeds the protocol maximum of 250`), exit 2.

## Gate-doc summary

- Floor (exact): **≥ 40 units/s steady-state on the M2 Ultra reference machine (64-text batches,
  warm model)**.
- Rationale table: rc.2 78.9 (64) / 77.4 (250); P0 llama-server floor 52.3; CPU-only regression
  ~6.6. 40 ≈ half of rc.2 — below the healthy Metal range and the P0 reference (no noise-flaking),
  far above a CPU-only fallback (~12× under, caught loudly).
- WHY: the rc.1 lesson (harness numbers ≠ engine numbers; CPU-only RC shipped ~12× under the
  design floor), citing Miller's
  `docs/findings/2026-07-20-first-real-shadow-converge-benchmark.md`.
- States the gate runs on the target machine against the packaged binary before any RC promotion,
  and that promotion itself requires explicit user approval.

## Notes

- No Rust code touched — `cargo test` untouched by construction (only two new files added).
- Files created in the sidecar repo only; no Cargo.toml/src/Miller-repo changes.
- Miller-tool note: used plain file reads for the sidecar repo (small Rust repo, not the
  Miller-indexed Miller workspace); the protocol contract at
  `docs/contracts/semantic-sidecar-protocol-v1.md` was the wire source.
- Serial-worker-commit: committed, **not pushed**; no release/tag actions taken.

## Fix round 1 (lead inline review)

- Finding (real, docs): gate item 3 wrongly described the packaged smoke as asserting a
  `ready:true` health. `scripts/package.sh --smoke` (lines 110–127) actually asserts `--version`
  plus an offline not-ready health from an empty cache dir (`ready:false` /
  `degraded_reason:"model_not_prepared"`) — the archive ships no model, so the smoke proves the
  fail-loud path. Corrected item 3 to describe the real check, keeping the "referenced, not
  restated" framing. No bench-script or other doc changes.

## Concerns

- None blocking. The bench uses a blocking `health` readline (no explicit init timeout); acceptable
  because a not-prepared or failed model still returns a `ready:false` response rather than hanging,
  and a warm model answers in seconds. A cold model load stays within the protocol's 120s init
  budget.
