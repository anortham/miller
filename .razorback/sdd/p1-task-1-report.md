# P1 Task 1 — Sidecar protocol contract — worker report

**Status:** complete
**Commit SHA:** none - parallel-lead-commit
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`
**Branch:** `worktree-semantic-p1` @ `25794d0` (ahead 1 of `origin/main`)
**Dirty state at report time:** `?? docs/contracts/semantic-sidecar-protocol-v1.md` (mine, untracked).
Also present and untracked: `docs/contracts/vectors-v1.md` and `eval/sidecar-conformance/` — created by
the parallel Task 2 / Task 3 workers, **not touched by me**.

**Created (sole file):** `docs/contracts/semantic-sidecar-protocol-v1.md` (~700 lines)
**Modified:** nothing. No `src/`, no `tests/`, no `docs/README.md`.

## Invariant

**The contract's wire behavior is transcription, not invention.** Every statement in
§ Envelopes / § Methods / § Errors resolves to a `file:line` citation in the running reference
implementation. Statements the reference does not support carry the label
**"v1 additive — not yet in the reference implementation"** and no citation. Where the design and the
reference disagree, the reference wins for v1 wire behavior and the disagreement is recorded in
§ Deviations from design (D1–D9) rather than silently resolved.

## Sections delivered

All plan-required sections present, verified programmatically:
`## Envelopes`, `## Methods`, `## Errors`, `## Health metadata (v1 additive)`, `## Prompt templates`,
`## Model knob table`, `## prepare subcommand`, `## Conformance`, `## Deviations from design`,
plus `## Per-item failure isolation`, `## Stdout purity`, `## Backend selection`, `## Transport`,
`## Reference implementation`, `## Boundary`, `## Stability rules`.

## Verification — documented consistency pass

| # | Check | Method | Result |
|---|---|---|---|
| V1 | Every `file:line` citation resolves to a real line in a real file | Script: parsed all 170 citations, resolved each against the actual line count of the 9 cited files | **PASS** — 170/170 in range, 0 unknown files |
| V2 | Cited lines actually contain the claimed content | Script: 26 targeted assertions on the highest-risk citations (schema/version literals, all 4 error codes, `stopping`, compact-JSON separators, 4-backend tuple, dims guard, zero-vector, `[empty]`, `dup2`, `id` alias, exactly-one-of, ready-needs-dims, all 3 timeouts, `FATAL_THRESHOLD`, `Stdio::null`, `QWEN_EOS`, `--embd-normalize 2`) | **PASS** — 26/26 |
| V3 | Knob table matches `bench-pins.json` byte-for-byte | Script: loaded the JSON and asserted each pinned value (id, file, sha256, url, license, pooling, query_instruction, document_instruction, native_dims, context_length) appears in the doc, for both models | **PASS** — 20/20 |
| V4 | Both pinned models' prompt templates exact | Included in V3; Qwen3's embedded newline is escaped as `\n` in the markdown table and the doc states explicitly that it is a literal newline, not two characters | **PASS** |
| V5 | Internal cross-refs resolve | Script: extracted all 31 headings, slugged them, checked all 12 distinct `](#anchor)` links | **PASS** — 0 broken |
| V6 | Relative links resolve | Script: `../adr/ADR-0003-…`, `../plans/2026-07-19-miller-semantic-integration-design.md`, `../../eval/model-bench/bench-pins.json`, `../../eval/sidecar-conformance/` all exist; `vectors-v1.md` (Task 2) now exists too | **PASS** |
| V7 | No uncited wire claim | Manual read-through of § Envelopes / § Methods / § Errors: every row and bullet carries a citation. Only § Health metadata (v1 additive), § Truncation semantics, § Capability negotiation, § Backend selection, § prepare are uncited — each explicitly labeled additive | **PASS** |
| V8 | Design's rejected error vocabulary does not leak into normative text | Script: `parse_error`/`invalid_params`/`embed_error`/`serialize_error` occur only at lines 660–664, inside § Deviations (starts line 651) | **PASS** |
| V9 | Tolerance policy stated exactly as frozen | dims exact / norm `1e-3` / cosine `≥ 0.999` / CPU goldens, all present with the "bitwise is not the bar" rationale and the one-failing-text-fails rule | **PASS** |
| V10 | Worker ceiling: no code path changed | `scripts/test.sh` (fast suite) | **1 pre-existing flake** — see below |

### V10 detail

`scripts/test.sh` → `Failed: 1, Passed: 3617, Skipped: 1` in 23s.
Failure: `Miller.Tests.Server.IndexerServiceScanTests.StartAsync_WhenEnabledLeader_BuildsSearchSidecarAfterStartupScan`.

Re-ran in isolation: **passes** (`Failed: 0, Passed: 1`, 191 ms). My change is one untracked markdown
file and cannot reach indexer code. Three P1 workers plus Miller MCP indexing were contending for the
workspace concurrently (a Miller `inspect` in this session reported
`freshness: unconfirmed_lock_busy`), which is the plausible cause. Reporting rather than
investigating further — it is outside this task's file ownership.

## Miller MCP calls used

| Call | What it confirmed |
|---|---|
| `workspace operation=list filter=julie` | Julie is registered as `julie-316c0b0829f9` (`/Users/murphy/source/julie`, state ready, rev 20), so Rust-side facts could be checked through the index rather than by guesswork |
| `inspect target=validate_response_envelope depth=overview workspace_id=julie-316c0b0829f9` | Confirmed the symbol exists at `sidecar_protocol.rs:108`, its signature, and — decisively — its **9 callers/refs**, which surfaced that the real stdio consumer is `sidecar_provider.rs:365` (+ `rpc_client.rs`), **not** the `julie-embedding-host.rs` binary the plan named. That finding became deviation D8 |

API-shape evidence: every protocol field name in the contract was read out of `protocol.py`,
`runtime.py`, and `sidecar_protocol.rs` in this session — none from memory. The four-code error
vocabulary in particular was derived by enumerating every `_error_response(...)` call site in
`protocol.py`, not by trusting the plan's list.

## Deviations found (reference vs design/plan)

Nine, all recorded in the contract's § Deviations from design. The first four are substantive.

**D1 — Error code vocabulary: the design is simply wrong. (Highest impact.)**
Design §4.1 and the plan's Global Constraints both state the vocabulary is
`parse_error | invalid_params | embed_error | internal_error | unknown_method | serialize_error`.
The reference emits **none** of `parse_error`, `invalid_params`, `embed_error`, `serialize_error`.
Actual set: `invalid_request`, `invalid_json`, `unknown_method`, `internal_error`. Mapping:
`parse_error`→`invalid_json`, `invalid_params`→`invalid_request`; `embed_error` and `serialize_error`
have no counterpart (embed failures surface as `internal_error`). Contract freezes the reference's
four. **A P2a implementation coded to the design's list would be wire-legal but fail conformance
A5/A16/A17** — worth the lead's attention because the plan's Global Constraints block is what workers
were told to copy verbatim.

**D2 — Health metadata is only half additive.** Design labels the whole health block "v1 additive".
But `capabilities`, `load_policy`, `accelerated`, `degraded_reason` already exist **and are strictly
validated on both sides**, including cross-field equality and a degradation invariant
(`protocol.py:67-123`, `sidecar_protocol.rs:167-209`). Treating them as optional would produce a
sidecar the existing consumer *rejects*. Only 12 genuinely-new keys carry the additive label.

**D3 — Failure isolation flags to stderr, not the wire.** Design says "zero-vector + flagged item".
Reference substitutes the zero vector (`runtime.py:406`) but reports the skip only on stderr
(`runtime.py:399-405`); the response shape is unchanged. Per-item flagging marked additive/optional.

**D4 — Backend enum mismatch.** Reference `capabilities` keys are the torch set
`cpu|cuda|directml|mps`, and its own validator **requires all four present**. Design describes
llama.cpp `Metal|Vulkan|CPU`. Resolved by emitting all four (false where inapplicable) **plus**
additive `metal`/`vulkan`. Nothing renamed.

**D5 — Undocumented reference behaviors, now frozen:** `id` is an accepted alias for `request_id`;
`schema`/`version` are **optional on inbound requests** (validated only when present); `shutdown`
returns exactly `{"stopping": true}`, which is also the producer's own loop-break signal.

**D6 — "Verbatim from bench-pins.json" is inaccurate for 3 of 7 Qwen3 knobs.** In the pins file:
pooling, query_instruction, document_instruction, dims/lanes. **Not in it:** the `<|endoftext|>` EOS
append (lives at `bench.py:28`, applied `:151`/`:156`), "L2 normalization always" (expressed as
`--embd-normalize 2`, `bench.py:65`), and `slice → renormalize → quantize` (`bench.py:298-309`, from
design §4.1). Values are unchanged — only provenance differs; the knob table records the true source
per row.

**D7 — Fallback pins id.** Plan says `bge-small-f32`; the file has `bge-small-en-v1.5-f32`
(`bench-pins.json:46`). Used the file's id.

**D8 — The plan's named Rust consumer has no protocol logic.** `src/bin/julie-embedding-host.rs` takes
a singleton lock and delegates to `host_server` (line 41); it never builds an envelope. The real stdio
consumer is `crates/julie-pipeline/src/embeddings/sidecar_provider.rs` + `sidecar_protocol.rs`, which
is what the contract cites throughout. Found via the Miller `inspect` caller list.

**D9 — Reference dims guard excludes the pinned lane.** `runtime.py:10` restricts to `{384,768,1024}`;
the default storage lane is 512d. Not a wire conflict (guard is producer-local, wire has no dims enum),
but P2a must not copy it or it rejects its own default lane.

## Concerns for the lead

1. **The plan's Global Constraints error-code list is wrong and workers were told to copy it verbatim.**
   D1 is not a nuance — 4 of 6 listed codes do not exist. Recommend correcting the plan text, or at
   minimum ensuring the P2a task points at this contract's § Errors rather than the plan block.
2. **D6 weakens the "byte-for-byte from bench-pins.json" acceptance criterion.** Three Qwen3 knobs
   cannot be verified against that file because they are not in it. I verified them against their real
   sources (`bench.py`) and recorded provenance per row, but Task 4's literal-consistency grep should
   use the same sources or it will report false mismatches.
3. **Wire-legal ambiguity I deliberately left open:** whether the sidecar emits native 1024d and Miller
   slices to 512d, or the sidecar serves the lane directly. Both satisfy this contract (`dims` echo +
   `health` declaration cover either). The contract defers the choice to `vectors-v1.md`. If Task 2
   also defers it, **nobody owns it** — worth a lead check.
4. **`fusion_profile` / `encoder_fingerprint` composition** is referenced as living in `vectors-v1.md`.
   I did not define it, to stay inside file ownership. Task 4 should confirm the forward reference
   lands.
5. **One flaky fast-suite test under parallel load** (V10). Passes in isolation; likely worker/MCP
   contention, not a code defect. Lead may want a clean serial re-run before the branch gate.
