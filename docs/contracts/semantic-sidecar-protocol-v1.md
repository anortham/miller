# Semantic Sidecar Protocol Contract v1

Status: **frozen** (P1). This document is the complete specification of the `julie.embedding.sidecar`
version 1 wire protocol as spoken by `julie-semantic-sidecar`, plus the model-knob, `prepare`, backend
selection, and conformance obligations that a compliant implementation carries. Phase P2a implements
exactly what is written here; phase P2b's fake sidecar mimics exactly what is written here.

**This contract is a transcription, not an invention.** The wire protocol described in
[§ Envelopes](#envelopes), [§ Methods](#methods), and [§ Errors](#errors) already runs in production
inside Julie. Every wire statement in those sections carries a `file:line` citation into the reference
implementation. Where this contract adds behavior the reference does not yet have, the addition is
labeled **v1 additive — not yet in the reference implementation** and carries no citation. Where the
program design and the reference disagree, **the reference wins for v1 wire behavior** and the
disagreement is recorded in [§ Deviations from design](#deviations-from-design).

**Frozen means frozen.** An implementer may not add, rename, or repurpose a wire field, method name,
or error code. Every literal below is a decision, not a suggestion. A genuinely required change is a
**v2**: a new `semantic-sidecar-protocol-v2.md`, a bumped `version` in the envelope, and a re-frozen
consumer. Version 1 and version 2 responses are never mixed on one connection.

The one exception is **pre-ship amendment**: until P2a ships a binary speaking this contract, no
deployed implementation exists that a v2 could protect, so a defect found in review is fixed in place
at `version` 1. No such amendments have been made yet. Once the first `julie-semantic-sidecar` binary
is published, this exception is spent and the v2 rule is absolute.

**Compatibility posture (load-bearing).** `julie-semantic-sidecar` is a drop-in replacement for the
Python sidecar Julie runs today ([design §4.2](../plans/2026-07-19-miller-semantic-integration-design.md)).
An implementation that satisfies this contract must remain readable by the *unmodified* Julie Rust
consumer. Any statement here that would break that consumer is a defect in this document, not in the
consumer.

---

## Reference implementation

Citations use these short names. All paths are read-only inputs to this contract; nothing in this
program modifies them.

| Short name | Path | Role |
|---|---|---|
| `protocol.py` | `~/source/julie/python/embeddings_sidecar/sidecar/protocol.py` | Producer: envelope construction, dispatch, stdio loop |
| `runtime.py` | `~/source/julie/python/embeddings_sidecar/sidecar/runtime.py` | Producer: embedding runtime, health metadata, failure isolation |
| `main.py` | `~/source/julie/python/embeddings_sidecar/sidecar/main.py` | Producer: process entry, stdout purity |
| `sidecar_protocol.rs` | `~/source/julie/crates/julie-pipeline/src/embeddings/sidecar_protocol.rs` | Consumer: envelope types and validation |
| `sidecar_provider.rs` | `~/source/julie/crates/julie-pipeline/src/embeddings/sidecar_provider.rs` | Consumer: transport, timeouts, restart, circuit breaker |
| `embedding_sidecar_provider.rs` | `~/source/julie/src/tests/core/embedding_sidecar_provider.rs` | Consumer tests: observable invariants |

Line numbers are as of 2026-07-19. A citation that no longer resolves is a signal to re-verify the
claim against the current reference, not to delete the claim.

> **Note on `julie-embedding-host.rs`.** The P1 plan lists `~/source/julie/src/bin/julie-embedding-host.rs`
> as the Rust consumer. That binary contains no protocol logic — it acquires a singleton lock and binds an
> IPC front door, delegating to `julie_pipeline::embeddings::host_server`
> (`julie-embedding-host.rs:41`). The stdio protocol consumer this contract is bound to is
> `sidecar_provider.rs` + `sidecar_protocol.rs`. See
> [§ Deviations from design](#deviations-from-design), D8.

---

## Transport

- **Newline-delimited JSON over stdio.** One JSON object per line on stdin (requests) and stdout
  (responses). The producer writes compact JSON (`separators=(",", ":")`) followed by `"\n"` and
  flushes after every response (`protocol.py:250-251`). The consumer writes the request, a `"\n"`, and
  flushes (`sidecar_provider.rs:385-400`), then reads exactly one line
  (`sidecar_provider.rs:402`, reader loop `sidecar_provider.rs:495-519`).
- **Strictly request/response, one in flight.** The consumer serializes all calls behind a process
  mutex (`sidecar_provider.rs:212-215`; test `embedding_sidecar_provider.rs:274-292`). An
  implementation must not emit unsolicited lines on stdout, and must not reorder or interleave
  responses.
- **Blank input lines are skipped**, not answered (`protocol.py:226-228`).
- **stdin EOF ends the process.** The producer's loop is a `for line in reader` over stdin
  (`protocol.py:225`); EOF exits the loop and the process. This is the lifecycle Miller relies on —
  the child dies when Miller dies, with no sockets, PID files, or detached processes
  ([design §4.2](../plans/2026-07-19-miller-semantic-integration-design.md)).
- **stderr is free-form.** The producer logs diagnostics to stderr (`runtime.py:401-405`,
  `runtime.py:465-468`). The consumer discards it (`sidecar_provider.rs:153`, `Stdio::null()`).
  Nothing on stderr is contractual; nothing contractual may be sent there.

## Envelopes

### Request envelope

Emitted by the consumer at `sidecar_provider.rs:377-383`; typed at `sidecar_protocol.rs:10-17`;
parsed at `protocol.py:142-180`.

| Field | Type | Required on the wire | Semantics |
|---|---|---|---|
| `schema` | string | Optional to *accept*, always sent | Must equal `"julie.embedding.sidecar"` when present. `protocol.py:19` defines the literal; `protocol.py:152-158` rejects a mismatch and **ignores absence**. Consumer always sends it (`sidecar_provider.rs:378`). |
| `version` | integer | Optional to *accept*, always sent | Must equal `1` when present. `protocol.py:20` defines the literal; `protocol.py:160-169` rejects a mismatch and ignores absence. Consumer always sends it (`sidecar_provider.rs:379`). |
| `request_id` | string | Optional; echoed | Correlation id. Accepted under the key `request_id` or the alias `id` (`protocol.py:44-57`; consumer alias at `sidecar_protocol.rs:13`). A non-string value under either key is an error. Absent ⟹ treated as `""`. |
| `method` | string | **Required** | One of the four method names in [§ Methods](#methods). A non-string `method` is an error (`protocol.py:171-173`). |
| `params` | object | Optional, defaults to `{}` | Per-method parameters. A non-object `params` is an error (`protocol.py:175-179`). |

An implementation **must** accept a request that omits `schema` and `version`, because the reference
does. It **must** send both in every response, because the consumer validates both
(`sidecar_protocol.rs:112-126`).

### Response envelope

Constructed at `protocol.py:23-41`; typed at `sidecar_protocol.rs:20-33`; validated at
`sidecar_protocol.rs:108-145`.

| Field | Type | Presence | Semantics |
|---|---|---|---|
| `schema` | string | Always | `"julie.embedding.sidecar"` (`protocol.py:25`, `protocol.py:34`). Consumer rejects any other value (`sidecar_protocol.rs:112-118`). |
| `version` | integer | Always | `1` (`protocol.py:26`, `protocol.py:35`). Consumer rejects any other value (`sidecar_protocol.rs:120-126`). |
| `request_id` | string | Always | Echoes the request's `request_id` verbatim (`protocol.py:27`, `protocol.py:36`). Consumer rejects a mismatch (`sidecar_protocol.rs:128-134`). |
| `result` | object | Exactly one of `result`/`error` | Method-specific success payload. |
| `error` | object | Exactly one of `result`/`error` | `{ "code": string, "message": string }` (`protocol.py:37-40`; typed `sidecar_protocol.rs:29-33`). |

**Exactly-one-of invariant.** A response carrying both `result` and `error`, or neither, is a protocol
violation the consumer rejects (`sidecar_protocol.rs:136-142`). The producer structurally cannot emit
one: success and error envelopes are separate constructors (`protocol.py:23`, `protocol.py:32`).

**Request-id echo is load-bearing.** A response whose `request_id` differs from the outstanding
request's is treated as a stream desync, not an application error: the consumer marks the connection
fatal and resets the child process (`sidecar_provider.rs:439-442`, then
`sidecar_provider.rs:111-133`). Tests cover both the plain mismatch and a mismatch followed by a
stale correct-id line (`embedding_sidecar_provider.rs:188-199`, `embedding_sidecar_provider.rs:248-271`).
The echo also applies to errors: an error response carries the same `request_id` the request supplied
(`protocol.py:36`), and when a request could not be parsed at all it carries `""`
(`protocol.py:236`).

**Unknown fields are ignored (v1 additive as a *rule*; already true in practice).** The consumer's
envelope types name their fields explicitly and serde ignores anything else; `HealthResult`'s optional
fields all carry `#[serde(default)]` (`sidecar_protocol.rs:60-78`), so a health payload that omits them
or adds new ones still deserializes. The producer likewise reads only the keys it knows from the
request dict (`protocol.py:148-180`). This contract **freezes that behavior as a rule**: a v1 peer on
either side must ignore fields it does not recognize rather than error, so that additive health
metadata can ship without a version bump. The rule itself is new; the behavior it describes is the
reference's.

## Methods

Exactly four. `protocol.py:182-214` is the complete dispatch table; there is no fifth branch.

### `health`

Request `params`: ignored — the consumer sends `{}` (`sidecar_provider.rs:463`).

Result: the runtime's metadata dict with `ready` merged in (`protocol.py:60-64`). Fields the reference
produces (`runtime.py:320-331`):

| Field | Type | Semantics |
|---|---|---|
| `ready` | boolean | **Required.** `false` means the sidecar is alive but cannot embed. |
| `dims` | integer | Output dimensionality. **Required when `ready` is true** — the consumer rejects a ready health with no `dims` (`sidecar_protocol.rs:168-170`) and stores it as the expected dims for every subsequent response (`sidecar_provider.rs:91`). |
| `model_id` | string | Model identity. |
| `runtime` | string | Runtime family name (`runtime.py:272`). |
| `device` | string | Resolved device. |
| `resolved_backend` | string | Backend identity. |
| `accelerated` | boolean | Whether the resolved device is not CPU (`runtime.py:213`). |
| `degraded_reason` | string \| null | Why the resolved backend differs from the requested one, or null. |
| `capabilities` | object | Backend availability map — see below. |
| `load_policy` | object | Requested-vs-resolved backend record — see below. |

`capabilities` is an object keyed by backend name, each value an object with a boolean `available`.
The reference produces and self-validates exactly four keys: `cpu`, `cuda`, `directml`, `mps`
(`runtime.py:196-201`; validated `protocol.py:72-80`; consumer type `sidecar_protocol.rs:86-96`).

`load_policy` (`runtime.py:204-215`; validated `protocol.py:82-99`; consumer type
`sidecar_protocol.rs:98-106`):

| Field | Type | Semantics |
|---|---|---|
| `requested_device_backend` | non-empty string | Backend the sidecar tried to use. |
| `resolved_device_backend` | non-empty string | Backend it actually loaded on. |
| `accelerated` | boolean | Must equal top-level `accelerated` (`protocol.py:110-113`; consumer `sidecar_protocol.rs:195-199`). |
| `degraded_reason` | string \| null | Must equal top-level `degraded_reason` (`protocol.py:115-118`; consumer `sidecar_protocol.rs:201-205`). |

**Degradation invariant.** When `requested_device_backend != resolved_device_backend`,
`degraded_reason` must be non-null. Enforced on both sides (`protocol.py:120-123`,
`sidecar_protocol.rs:185-193`).

**Readiness gates construction.** A consumer probes `health` before its first embed and refuses to
construct if `ready` is false (`sidecar_provider.rs:465-467`; test
`embedding_sidecar_provider.rs:327-376`). A health probe that fails terminates the child and reports
with launch context (`sidecar_provider.rs:192-205`).

### `embed_query`

Request `params`: `{ "text": <string> }` (`sidecar_protocol.rs:35-38`; validated
`protocol.py:186-192`). A non-string `text` is an error.

Result: `{ "dims": <int>, "vector": [<float>, …] }` (`protocol.py:126-130`; typed
`sidecar_protocol.rs:45-49`).

Consumer validation (`sidecar_protocol.rs:147-165`): `dims` must equal the dims learned from `health`,
**and** `vector.len()` must equal that same value. Both are checked; a truthful `dims` field with a
wrong-length vector is still rejected. Test: `embedding_sidecar_provider.rs:161-172`.

`embed_query` applies the **query** instruction template (see [§ Prompt templates](#prompt-templates)).
There is no `kind` or `role` parameter — the method name selects the policy.

### `embed_batch`

Request `params`: `{ "texts": [<string>, …] }` (`sidecar_protocol.rs:40-43`; validated
`protocol.py:195-208` — `texts` must be a list and every element must be a string).

Result: `{ "dims": <int>, "vectors": [[<float>, …], …] }` (`protocol.py:133-139`; typed
`sidecar_protocol.rs:51-55`).

Consumer validation (`sidecar_protocol.rs:211-246`), in order:
1. `dims` equals the health-declared dims.
2. `vectors.len()` equals the request's `texts.len()` — **batch count match**.
3. Every element's length equals `dims`, reported with the offending index.

**One `dims` for the whole batch.** There is no per-item dims field. A batch is homogeneous by
construction.

**Empty batch.** `texts: []` returns `vectors: []` with `dims` still echoed (`runtime.py:360-361`).
Count match trivially holds. This is not an error.

`embed_batch` applies the **document** instruction template.

### `shutdown`

Request `params`: ignored — the consumer sends `{}` (`sidecar_provider.rs:477-481`).

Result: `{ "stopping": true }` (`protocol.py:212`).

After writing that response the producer breaks its loop and exits, but **only** when the response is
a success envelope with `result.stopping === true` (`protocol.py:253-260`) — an error response to a
`shutdown` request does not stop the loop.

The consumer gives `shutdown` a hard 500 ms budget and then terminates the child regardless
(`sidecar_provider.rs:60`, `sidecar_provider.rs:476-483`). **`Drop` does not send `shutdown`** — it
kills the process without blocking on I/O; graceful shutdown is the explicit `shutdown()` call only
(`embedding_sidecar_provider.rs:294-325`, comment at `embedding_sidecar_provider.rs:296-298`). An
implementation must therefore survive being killed at any point and must never rely on receiving
`shutdown`.

Any other `method` value produces an `unknown_method` error (`protocol.py:214`).

## Errors

### Error codes

**These four codes are the complete v1 set.** The reference emits no others. The program design lists
a different six-code vocabulary; the reference wins — see
[§ Deviations from design](#deviations-from-design), D1.

| Code | Emitted when | Citation |
|---|---|---|
| `invalid_request` | The request is structurally wrong: non-object request, non-string `request_id`/`id`, `schema` mismatch, `version` mismatch, non-string `method`, non-object `params`, or a method's params failing their type check. | `protocol.py:146`, `:150`, `:154`, `:162`, `:173`, `:178`, `:188`, `:199`, `:205` |
| `invalid_json` | The line could not be parsed as JSON. `request_id` is `""` because none could be read. | `protocol.py:236` |
| `unknown_method` | `method` is a string but not one of the four. | `protocol.py:214` |
| `internal_error` | Any unhandled exception raised while handling a well-formed request — OOM, backend failure, tokenizer failure. Message is `"{ExceptionType}: {message}"`. | `protocol.py:237-248` |

The consumer treats `code` as an opaque string: it surfaces it as
`sidecar error for method '<method>': [<code>] <message>` and does **not** switch on the value
(`sidecar_provider.rs:447-453`). Test `embedding_sidecar_provider.rs:174-186` asserts this with the
non-vocabulary code `"boom"`. Consequently an unknown code degrades gracefully rather than desyncing —
but an implementation must still emit only the four codes above, because the vocabulary is frozen.

### Application errors never kill the connection

A well-formed error envelope means the protocol loop survived, so the consumer does **not** mark the
connection fatal and does **not** restart the child (`sidecar_provider.rs:444-453`, and the comment
stating the rule verbatim at `sidecar_provider.rs:444-446`). This is the distinction P2b must
preserve: application-level embed errors do not trip the circuit breaker.

The reference achieves this by catching **all** exceptions inside the stdio loop
(`protocol.py:237-248`) rather than letting one propagate. An implementation must do the same: an
embed failure is an `internal_error` envelope, never a process exit.

### What *is* connection-fatal

These are consumer-side transport judgements, not wire messages. An implementation should understand
them because they determine when it gets restarted (`sidecar_provider.rs`, `mark_connection_fatal`
at `:341-344`):

| Condition | Citation |
|---|---|
| Request could not be encoded, written, or flushed to stdin | `:385-400` |
| stdout closed mid-request (child crashed) | `:404-416` |
| No response within the per-request timeout | `:417-423` |
| stdout reader thread disconnected | `:424-427` |
| Response line was not decodable JSON | `:430-437` |
| Envelope validation failed (schema/version/request-id/exactly-one-of) | `:439-442` |

Recovery: terminate, respawn, re-probe `health` (`sidecar_provider.rs:111-133`). After
**3 consecutive** fatal failures the provider is permanently disabled
(`FATAL_THRESHOLD` at `sidecar_provider.rs:116-126`). Tests cover timeout recovery
(`embedding_sidecar_provider.rs:201-226`), post-health exit recovery
(`:228-246`), and desync recovery (`:248-271`).

### Timeouts a sidecar must tolerate

Consumer defaults (`sidecar_provider.rs:58-60`), overridable by environment
(`sidecar_provider.rs:521-536`):

| Budget | Default | Environment override |
|---|---|---|
| Per-request response | 30,000 ms | `JULIE_EMBEDDING_SIDECAR_TIMEOUT_MS` |
| First `health` probe (model load) | 120,000 ms | `JULIE_EMBEDDING_SIDECAR_INIT_TIMEOUT_MS` |
| `shutdown` | 500 ms | — |

Miller applies its own per-request deadlines and one-restart-then-circuit-breaker policy on top
([design §4.2](../plans/2026-07-19-miller-semantic-integration-design.md)). An implementation must
answer the first `health` within the init budget on a cold model load, and must answer every other
request within the request budget or accept being restarted. Converge batches are bounded at
250 texts per RPC by the caller (design §4.2) — an implementation must size its internal batching so
that a 250-text `embed_batch` fits the request budget.

## Health metadata (v1 additive)

Everything in this section is **v1 additive — not yet in the reference implementation**. It ships as
new keys inside the existing `health` result, protected by the ignore-unknown rule in
[§ Envelopes](#envelopes). No consumer may *require* these keys, and their absence is not an error.

> The program design labels the entire health-metadata block additive. Half of it is not: `capabilities`,
> `load_policy`, `accelerated`, and `degraded_reason` already exist and are strictly validated on both
> sides. Only the keys below are genuinely new. See
> [§ Deviations from design](#deviations-from-design), D2.

| Field | Type | Semantics |
|---|---|---|
| `model_sha256` | string | Lowercase hex sha256 of the GGUF file actually loaded. Must match the pinned value in the sidecar's embedded manifest. |
| `model_revision` | string | Manifest revision identifier for the loaded model entry. |
| `sidecar_version` | string | Shim version — the semver of the `julie-semantic-sidecar` binary. |
| `llama_cpp_build` | string | Vendored llama.cpp build tag (e.g. `b10068`). |
| `pooling` | string | `last` or `cls`, per [§ Model knob table](#model-knob-table). |
| `normalization` | string | `l2`. The only v1 value; output is always L2-normalized. |
| `instruction_policy_version` | integer | Version of the prompt-template set applied. Starts at `1`. |
| `max_text_tokens` | integer | Token budget per input after instruction prefixing; longer inputs are truncated per [§ Truncation semantics](#truncation-semantics-v1-additive). |
| `max_batch_items` | integer | Largest `texts` array the implementation accepts. |
| `max_request_bytes` | integer | Largest single request line in bytes. |
| `native_dims` | integer | The model's native output dimensionality, distinct from `dims` when a lane slice is applied. |
| `mrl_lanes` | array of integers | Lanes the loaded model supports, or a single-element array for non-MRL models. |

**`encoder_fingerprint` composition.** Miller's vector artifact keys its generation identity on a
fingerprint derived from these fields; the composition and its invalidation semantics are specified in
[`vectors-v1.md`](vectors-v1.md), not here. This contract's obligation is only that the fields above are
reported truthfully and change whenever the produced vectors would change.

### Capability negotiation (v1 additive)

The `capabilities` map gains llama.cpp-relevant backends alongside the four the reference validates:

- **The four reference keys (`cpu`, `cuda`, `directml`, `mps`) must still be emitted**, because the
  reference producer's own validator requires all four to be present objects (`protocol.py:72-80`).
  A llama.cpp shim that cannot use `directml` reports `{"available": false}` rather than omitting the
  key. (The Rust consumer tolerates omission — every field defaults, `sidecar_protocol.rs:88-95` — but
  emitting all four keeps a shim readable by *any* v1 peer.)
- **Additive keys:** `metal`, `vulkan`, each `{"available": <bool>}`, same shape.
- A consumer selects nothing. Capability reporting is informational; backend selection is the
  sidecar's own decision, per [§ Backend selection](#backend-selection).

### Truncation semantics (v1 additive)

- Truncation is the **sidecar's** job, silently and deterministically. An input longer than
  `max_text_tokens` is truncated to that budget rather than erroring. A caller never has to pre-measure
  tokens.
- **Frozen budget for the pinned models** (the value `health` reports): `max_text_tokens = 32768` for
  `qwen3-0.6b-f16` and `512` for `bge-small-en-v1.5-f32` — each model's `context_length` in
  [`bench-pins.json`](../../eval/model-bench/bench-pins.json). The budget covers the **entire** model
  input: instruction prefix, text, and EOS marker.
- **Frozen algorithm — token-based tail truncation.** (Amended in review: an earlier draft described the
  fit by reference to the benchmark harness's `_fit`, a character-count approximation
  (`eval/model-bench/bench.py:144-156`); that description is retired — tokens, not characters, are the
  frozen unit.) Per input:
  1. Sanitize, then prefix the role's instruction string.
  2. `eos_reserve` = the token count of the model's EOS marker under its own tokenizer (`1` for Qwen3's
     `<|endoftext|>`; `0` when the model declares no EOS append).
  3. `special_token_overhead` = the count of tokens the model's tokenizer adds around every embedding
     input beyond the text itself (bge's `[CLS]`/`[SEP]`), measured once per model as the token-count
     difference between tokenizing a probe string with and without special tokens (amended in review:
     the committed goldens already encode this term — `eval/sidecar-conformance/generate.py`
     `special_token_overhead()`/`fit_to_budget()`, `2` for bge, `1` for qwen3 — and without it a
     512-content-token bge input would exceed the model's context as a 514-token request).
  4. Tokenize the prefixed string exactly as embedding input is tokenized, **without** the
     tokenizer-added special tokens. If the sequence exceeds
     `max_text_tokens − eos_reserve − special_token_overhead`, truncate the token sequence tail to
     that length.
  5. **Round-trip stability rule:** detokenize and retokenize the truncated sequence; while the
     retokenization differs from the truncated sequence, drop one more trailing token and repeat
     (terminates — the sequence only shrinks; typically 0–1 iterations). The stable detokenization is
     the final text body. This makes a string-in/string-out implementation and a token-level
     implementation produce the same embedded tokens.
  6. Append the EOS marker. The instruction prefix (truncation is tail-only) and the EOS marker
     (reserved in step 4) therefore **always survive** — load-bearing for `pooling=last`, where the
     final token carries the representation.
- Truncation is not reported per item on the wire in v1. A caller that needs to know inspects
  `max_text_tokens` from `health` and measures its own inputs.
- Truncation is **not** an error and does not flag the item.

## Prompt templates

Templates are applied **inside the sidecar**, keyed by model identity. Callers never pass an
instruction, a pooling mode, or a normalization flag — the method name (`embed_query` vs
`embed_batch`) is the entire caller-side signal
([design §4.1](../plans/2026-07-19-miller-semantic-integration-design.md)).

| Method | Role | Template applied |
|---|---|---|
| `embed_query` | query | The model's `query_instruction`, prefixed to the caller's text. |
| `embed_batch` | document | The model's `document_instruction`, prefixed to the caller's text. |

Both pinned models use an empty `document_instruction`, so document embedding is the identity
transform on the input text. This is a value, not an absence: a future model may set it, and the
sidecar applies whatever its manifest says.

Application order, per input (amended in review — an earlier draft appended the EOS before the fit,
which could cut the marker the knob table calls unconditional):

1. Prefix the role's instruction string.
2. Fit to `max_text_tokens − eos_reserve − special_token_overhead` per
   [§ Truncation semantics](#truncation-semantics-v1-additive) (tail truncation + stability rule).
3. Append the model's EOS marker, if the model declares one.
4. Tokenize and embed with the model's pooling mode.
5. L2-normalize.

The instruction strings themselves are frozen in the table below. They are exact — including the
trailing space on both query instructions and the embedded `\n` in Qwen3's — because an embedding model
is sensitive to both.

## Model knob table

Two models are pinned. Values are transcribed from
[`eval/model-bench/bench-pins.json`](../../eval/model-bench/bench-pins.json) and the P1 plan's Global
Constraints block; the `Source` column records where each value actually lives, because not every knob
is in the pins file — see [§ Deviations from design](#deviations-from-design), D6.

**Callers never pass these.** They are the sidecar's internal, model-keyed configuration, reported
through `health` and frozen here so two independent implementations produce interchangeable vectors.

| Knob | `Qwen3-Embedding-0.6B` (default) | `bge-small-en-v1.5` (fallback) | Source |
|---|---|---|---|
| Pins entry id | `qwen3-0.6b-f16` | `bge-small-en-v1.5-f32` | `bench-pins.json:24`, `:46` |
| Tier | `default` | `fallback` | `bench-pins.json:25`, `:47` |
| GGUF file | `Qwen3-Embedding-0.6B-f16.gguf` | `bge-small-en-v1.5-f32.gguf` | `bench-pins.json:30`, `:52` |
| sha256 | `421a27e58d165478cc7acb984a688c2aa41404968b0203e7cd743ece44c54340` | `bf40c42ad7d89382e9ba7376d5c4b73f6b556cb541fab37aaa1da9c320149b65` | `bench-pins.json:32`, `:54` |
| Source URL | `https://huggingface.co/Qwen/Qwen3-Embedding-0.6B-GGUF/resolve/main/Qwen3-Embedding-0.6B-f16.gguf` | `https://huggingface.co/CompendiumLabs/bge-small-en-v1.5-gguf/resolve/main/bge-small-en-v1.5-f32.gguf` | `bench-pins.json:31`, `:53` |
| License | `apache-2.0` | `mit` | `bench-pins.json:28`, `:50` |
| Native dims | `1024` | `384` | `bench-pins.json:34`, `:56` |
| MRL | yes, lanes `[256, 512, 1024]` | no, lanes `[384]` | `bench-pins.json:36-37`, `:58-59` |
| **Storage lane** | **512d int8** | **384d int8** | Global Constraints |
| Pooling | `last` | `cls` | `bench-pins.json:35`, `:57` |
| EOS append | `<|endoftext|>` appended to every input before tokenization | none | `bench.py:28`, applied `bench.py:151`,`:156` |
| `query_instruction` | `"Instruct: Given a code search query, retrieve the code or documentation that answers it\nQuery: "` | `"Represent this sentence for searching relevant passages: "` | `bench-pins.json:39`, `:61` |
| `document_instruction` | `""` | `""` | `bench-pins.json:40`, `:62` |
| Context length | `32768` | `512` | `bench-pins.json:41`, `:63` |
| Normalization | L2, always | L2, always | Global Constraints; `--embd-normalize 2` at `bench.py:65` |
| MRL/quantize order | slice → renormalize → quantize | n/a (native lane) | Global Constraints; `bench.py:298-309` |

Notes that are part of the contract:

- **The EOS append is Qwen3-only and unconditional.** It is appended to *every* input — query and
  document alike — before tokenization (`bench.py:151` and `:156` apply it to both roles).
- **`\n` in Qwen3's `query_instruction` is a literal newline**, not the two characters `\` and `n`.
- **`slice → renormalize → quantize` is an ordering, not a suggestion.** Slicing an L2-normalized
  vector denormalizes it; renormalizing before quantization is what makes the int8 symmetric scale
  meaningful. The benchmark harness implements exactly this order (`bench.py:298-305` slice then
  `l2()`, `bench.py:308-309` quantize then `l2()` again).
- **Where the lane slice happens is a division of labor, not a wire question.** This contract does not
  require the sidecar to emit sliced vectors: it requires that `dims` in the response equals
  `vector.len()` and equals what `health` declared. Whether an implementation serves native 1024d and
  lets Miller slice, or serves the 512d lane directly, is settled by
  [`vectors-v1.md`](vectors-v1.md) and the sidecar's `health` declaration — both are wire-legal.
- **Do not infer a dims allow-list from the reference.** `runtime.py:10` restricts its own runtime to
  `{384, 768, 1024}`. That is a producer-local guard for the Python sidecar's model set, not a wire
  constraint, and the 512d storage lane is deliberately outside it. v1 places no enum on `dims`.

`snowflake-arctic-embed-s` also appears in the pins file (`bench-pins.json:66-85`) as a benchmark
candidate. It is **not** pinned for the sidecar; only the two models above are.

## Per-item failure isolation

A single unencodable text inside a large batch must not fail the batch, and must not cost a linear
retry.

**Binary-search isolation (reference behavior).** Try the whole batch. On failure, split in half and
recurse on each half; good halves batch-encode normally and only the failing half splits further
(`runtime.py:381-413`). For 500 texts with 1 poison text this is ~9 splits rather than 500 individual
calls (`runtime.py:385-387`).

**Zero-vector substitution.** When recursion reaches a single text that still fails, the reference
logs to stderr and substitutes a zero vector of the declared dims (`runtime.py:398-406`). The batch
succeeds; `vectors.len()` still equals `texts.len()`, so the consumer's count-match invariant holds.

**Input sanitation happens before any of this** (`runtime.py:233-250`):

- A non-string element, an empty string, or a whitespace-only string becomes the literal `"[empty]"`.
- NUL bytes (`\x00`) are stripped; if the result is then blank it also becomes `"[empty]"`.
- **An empty string is therefore embedded, not rejected.** This is a wire fact conformance fixtures
  must encode as the expected behavior.

**Count and dims invariants survive isolation.** `runtime.py:372-379` re-checks that the output count
matches the input count and that every vector has the declared dims *after* the fallback path runs; a
mismatch raises, which the stdio loop converts into an `internal_error` envelope.

**Flagging is v1 additive.** The reference reports the skipped item only on stderr — there is no
wire-visible marker. See [§ Deviations from design](#deviations-from-design), D3. A v1 implementation
**may** add a `flagged_indices` array to the `embed_batch` result (integer indices of items that
received a substituted zero vector); it is additive, ignorable, and must never replace or reorder the
`vectors` array.

## Stdout purity

**stdout carries protocol lines and nothing else.** A single stray byte desyncs the consumer's line
reader and triggers a connection-fatal reset (`sidecar_provider.rs:430-437`).

The hard case is model load. C libraries write progress directly to file descriptor 1, bypassing any
language-level stdout object — llama.cpp does this, and so did the reference's safetensors/tqdm path.
A language-level redirect is insufficient. The reference's fix is the pattern to copy
(`main.py:37-48`):

1. `dup(1)` to save the real stdout descriptor.
2. `dup2(2, 1)` so fd 1 points at stderr for the duration of the load.
3. Load the model. All native chatter lands on stderr, which is free-form.
4. `dup2(saved, 1)` to restore, close the saved descriptor, and reattach the language-level stdout
   object to fd 1.

The restore must happen in a `finally`-equivalent so a failed load still leaves fd 1 correct
(`main.py:43-48`).

Implementation obligations:

- Redirect at the **file-descriptor** level, not the language level.
- Keep the redirect active for the entire duration of any native call that may print — model load,
  backend probe, and the first-start micro-benchmark in [§ Backend selection](#backend-selection).
- Emit the first protocol line only after fd 1 is restored.
- Never write logs, banners, progress, or version strings to stdout at any point.

## Backend selection

**v1 additive — not yet in the reference implementation.** The reference selects a torch device
(`runtime.py:21-40`) and falls back to CPU on a failed probe encode (`runtime.py:523-588`); the
micro-benchmark and the cached choice below are new for the llama.cpp shim
([design §4.1](../plans/2026-07-19-miller-semantic-integration-design.md)).

- **CPU is present in every build.** It is the floor, never unavailable
  (`capabilities.cpu.available` is unconditionally `true` in the reference, `runtime.py:197`).
- **First start micro-benchmarks.** On the first run for a given cache key, the sidecar times both a
  batch-1 shape (query latency) and an indexing-batch shape (converge throughput) on each available
  backend, and picks the winner.
- **The choice is cached, keyed by** shim version + model sha256 + GPU/driver identity. Any component
  changing re-runs the benchmark. The cache lives beside the model cache
  ([§ prepare subcommand](#prepare-subcommand)).
- **"Accelerated backend slower than CPU" is a normal outcome, not an error.** Vulkan losing to CPU on
  a given machine yields a cached CPU choice, `ready: true`, `accelerated: false`, and a
  `degraded_reason` naming the benchmark result. It must not fail startup, must not be retried every
  launch, and must not be logged as an error.
- **Report the outcome honestly through `health`**: `load_policy.requested_device_backend` is the
  backend the cached choice asked for, `resolved_device_backend` is what actually loaded, and the
  degradation invariant from [§ Methods](#methods) applies — differing values require a non-null
  `degraded_reason`.
- **A failed load on the chosen backend falls back to CPU** and records the reason, mirroring the
  reference's probe-failure path (`runtime.py:548-557`). A failure on CPU after that fallback is
  fatal and must surface as a failed `health` probe, not a silent hang
  (`runtime.py:579-588`).

## prepare subcommand

**v1 additive — not yet in the reference implementation.** The Python sidecar acquires models through
`sentence_transformers`' own HuggingFace download path (`runtime.py:494-497`); the explicit
acquisition subcommand below is new
([design §4.4](../plans/2026-07-19-miller-semantic-integration-design.md)).

**The sidecar binary is the single owner of model acquisition.** Miller never parses a model URL,
never computes a model path, and never downloads a weight file.

```
julie-semantic-sidecar prepare [--model <id>]
```

Obligations:

| Obligation | Requirement |
|---|---|
| Manifest ownership | The mapping model id → sha256 + size + source URL is **embedded in the binary** and versioned with it. `--model` accepts a manifest id (`qwen3-0.6b-f16`, `bge-small-en-v1.5-f32`); omitting it prepares the default tier. |
| Atomic download | Download to a temporary path in the cache directory, then rename into place. A partially written file is never visible under its final name. |
| Verification | sha256 of the completed file must equal the manifest value before the rename. A mismatch deletes the temp file and fails loudly. |
| Concurrency | A cache lock makes concurrent `prepare` invocations safe: one downloads, the others wait and then observe the finished file. Neither duplicates work nor corrupts the cache. |
| Offline mode | When the network is unavailable or offline mode is set, `prepare` **fails loudly with an actionable message** naming the model id, the expected path, and the source URL. It never silently degrades or half-prepares. |
| Progress | Progress is machine-readable on stdout, or on stderr with stdout left empty. `prepare` is a one-shot subcommand, not the RPC server — but a caller may still be parsing, so the format is a decision, not incidental logging. |
| Disk preflight | Check free space against the manifest's declared size before starting. Fail before downloading, not partway through. |
| Startup cleanup | On sidecar start, remove stale partial downloads left by a killed `prepare`. |

**The RPC protocol gains no download method.** There is no `prepare`, `download`, or `fetch` method —
[§ Methods](#methods) is complete at four. A `health` call against a missing model reports:

```json
{"ready": false, "degraded_reason": "model_not_prepared"}
```

(Amended in review: the reference's `protocol.py:63` runs `_validate_health_metadata` on **every**
health result, which would reject this minimal not-ready payload and make row B3 unsatisfiable. A
v1 implementation must apply full health-metadata validation only when `ready` is `true`; a
not-ready health owes nothing beyond `ready` and `degraded_reason`.)

`degraded_reason` is the exact string `model_not_prepared`. Because `ready` is false, a consumer
refuses to construct (`sidecar_provider.rs:465-467`) and Miller surfaces the not-prepared state rather
than hanging. Note that `dims` is not required when `ready` is false
(`sidecar_protocol.rs:168-170` requires it only for a ready response).

**Cache path resolution:** `JULIE_EMBEDDING_CACHE_DIR` if set, else the platform cache directory
(`~/.cache/julie-semantic`, `%LOCALAPPDATA%`-rooted on Windows). Shared with Julie by construction —
the same binary serving both consumers uses one cache.

Miller's `miller semantic prepare` verb and any dashboard affordance shell out to this subcommand.
Consent semantics live in Miller; mechanics live here.

## Conformance

An implementation claiming `julie.embedding.sidecar` v1 must pass all three groups below. Groups A and
B are pass/fail on observable wire behavior; group C is numeric and governed by the frozen tolerance
policy.

### Group A — envelope and method conformance

Each row is a request/response assertion drawn from the citations above.

| # | Given | Must |
|---|---|---|
| A1 | Any request | Response carries `schema: "julie.embedding.sidecar"`, `version: 1` |
| A2 | Request with `request_id: "x"` | Response echoes `request_id: "x"` |
| A3 | Request with `id: "x"` and no `request_id` | Response echoes `"x"` |
| A4 | Request omitting `schema` and `version` | Handled normally, not rejected |
| A5 | Request with `schema: "other"` or `version: 2` | `invalid_request` error, `request_id` still echoed |
| A6 | Any response | Exactly one of `result` / `error` present |
| A7 | `health` before any embed | `ready` boolean present; if `ready` is true, `dims` present |
| A8 | `health` result | `capabilities` has object values for `cpu`, `cuda`, `directml`, `mps`, each with boolean `available` |
| A9 | `health` with `requested_device_backend != resolved_device_backend` | `degraded_reason` non-null; `load_policy.accelerated` equals top-level `accelerated`; `load_policy.degraded_reason` equals top-level `degraded_reason` |
| A10 | `embed_query` with `text` | `dims` equals health dims **and** `vector.len()` equals `dims` |
| A11 | `embed_batch` with N texts | `vectors.len() == N`, every element of length `dims` |
| A12 | `embed_batch` with `texts: []` | `vectors: []`, no error |
| A13 | `embed_query` with `text: ""` | Succeeds with a vector — empty input is embedded, not rejected |
| A14 | `embed_query` with non-string `text` | `invalid_request` error |
| A15 | `embed_batch` with a non-string element | `invalid_request` error |
| A16 | Unparseable JSON line | `invalid_json` error with `request_id: ""` |
| A17 | `method: "nope"` | `unknown_method` error |
| A18 | An input that fails to encode inside a batch | Batch still returns N vectors; the failing item is a zero vector; no process exit |
| A19 | `shutdown` | `result: {"stopping": true}`, then process exit |
| A20 | Blank line on stdin | No response emitted; loop continues |
| A21 | Request with an unrecognized top-level field | Ignored, request handled normally |
| A22 | Entire session, from spawn to exit | stdout contains only protocol lines — no banner, progress, or log output |
| A23 | Any error condition in A5/A14–A17 | Process is still alive and answers the next request |

An error code outside `{invalid_request, invalid_json, unknown_method, internal_error}` fails
conformance even though the consumer tolerates it.

### Group B — lifecycle conformance

| # | Given | Must |
|---|---|---|
| B1 | stdin EOF | Process exits |
| B2 | SIGKILL at any point | No orphan process, no lock file, no cleanup requirement on the consumer |
| B3 | Model not present in cache | `health` returns `ready: false, degraded_reason: "model_not_prepared"` |
| B4 | Cold start with the model present | First `health` answers within the 120,000 ms init budget |
| B5 | `embed_batch` of 250 texts | Answers within the 30,000 ms request budget |
| B6 | Backend benchmark selects CPU over an available GPU | `ready: true`, `accelerated: false`, non-null `degraded_reason`, exit code 0 |

### Group C — numeric conformance

Bound to the fixture set in [`eval/sidecar-conformance/`](../../eval/sidecar-conformance/): a corpus
of texts covering ASCII identifiers, prose, markdown, CJK, astral-plane unicode, over-budget inputs,
single-character and whitespace-only strings, and both roles, plus golden vectors per pinned model.

**Frozen tolerance policy.** These three numbers are the bar. They are frozen by the P1 plan's Global
Constraints and restated identically in the fixture README:

| Check | Bar |
|---|---|
| Output dimensionality | **Exactly** equal to the requested lane. No tolerance. |
| L2 norm of every emitted vector | Within **`1e-3`** of `1.0`. |
| Cosine similarity to the CPU-generated golden vector | **`≥ 0.999`** per text. |

"Emitted vector" means a wire vector, which is always float — the wire never carries quantized
vectors (`vectors-v1.md` § Storage schema, division of labor). Quantized **storage codes** are not
held to the norm bar (symmetric-int8 rounding at lane dims drifts ~1.5e-3 inherently); the fixture
set bounds them by dual cosine checks (vs. golden and vs. their own pre-quantization float) plus a
code-range check instead.

**Bitwise equality is explicitly not the bar.** Metal, Vulkan, and CPU backends produce different
low-order bits for the same input; a bitwise gate would fail every accelerated build for no
correctness reason. Goldens are generated on the **CPU backend** precisely because it is the
reproducible one.

**The pass rule:** an implementation passes group C iff **every** corpus text, under **its own**
runtime and backend, meets all three checks against the committed goldens for the model it loaded.
One failing text fails conformance — there is no percentage threshold.

Role matters: a query-role text must be embedded through `embed_query` and compared against the
query-role golden, and a document-role text through `embed_batch` against the document-role golden.
Comparing across roles will fail the cosine bar, correctly — the instruction templates differ.

## Deviations from design

Recorded per the P1 plan's rule: where the reference implementation and the program design disagree,
the reference wins for v1 wire behavior and the disagreement is written down rather than silently
resolved.

**D1 — Error code vocabulary (design is wrong; reference wins).**
[Design §4.1](../plans/2026-07-19-miller-semantic-integration-design.md) and the P1 plan's Global
Constraints both state the error vocabulary is
`parse_error | invalid_params | embed_error | internal_error | unknown_method | serialize_error`.
The reference emits **none** of `parse_error`, `invalid_params`, `embed_error`, or `serialize_error`.
Its actual vocabulary is `invalid_request`, `invalid_json`, `unknown_method`, `internal_error`
(`protocol.py:146`, `:236`, `:214`, `:247`). The mapping: `parse_error` → `invalid_json`,
`invalid_params` → `invalid_request`; `embed_error` and `serialize_error` have no reference
counterpart — embed failures surface as `internal_error` (`protocol.py:237-248`). This contract
freezes the reference's four codes. **Impact:** a P2a implementation emitting the design's vocabulary
would be wire-legal (the consumer does not switch on `code`, `sidecar_provider.rs:447-453`) but would
fail conformance check A5/A16/A17 and would differ from the running sidecar for no benefit.

**D2 — Health metadata is only half additive (design overstates the gap).**
Design §4.1 presents backend/device identity, `accelerated`, and `degraded_reason` as part of the "v1
additive health metadata" block. They are not additive: `capabilities` and `load_policy` already exist
in the reference **producer**, which always emits them and strictly validates them on its own side
(`runtime.py:196-215`, `runtime.py:320-331`, `protocol.py:67-123`). The Rust **consumer** treats both
as optional (`#[serde(default)] Option<…>`, `sidecar_protocol.rs:74-77`) and enforces the cross-field
equality and degradation invariants only when the fields are present (`sidecar_protocol.rs:167-209`),
so a sidecar omitting them still interoperates with the existing consumer. This contract nevertheless
**requires emitting them** — that is reference producer behavior and conformance enforces it (group A);
the requirement's teeth are the conformance gate, not consumer rejection. The additive label is
reserved for the genuinely new keys in [§ Health metadata](#health-metadata-v1-additive), which are
documented in [§ Methods](#methods) as reference behavior.

**D3 — Failure isolation flags on stderr, not on the wire (design overstates).**
Design §4.1 specifies "zero-vector + flagged item". The reference substitutes the zero vector
(`runtime.py:406`) but reports the skipped text only via a stderr line (`runtime.py:399-405`) — there
is **no** wire-visible flag, and the response shape is unchanged. This contract keeps the zero-vector
substitution as v1 behavior and marks per-item flagging additive and optional.

**D4 — Backend enum mismatch (torch vs llama.cpp).**
The reference's `capabilities` keys are the torch device set `cpu | cuda | directml | mps`
(`runtime.py:196-201`), and its own validator **requires all four to be present**
(`protocol.py:72-80`). Design §4.1 describes llama.cpp backend selection over `Metal | Vulkan | CPU`.
These are different vocabularies for overlapping hardware (`mps` and `metal` both mean Apple GPU).
Resolution: emit the four reference keys unconditionally (false where inapplicable) **and** add
`metal`/`vulkan` additively, per
[§ Capability negotiation](#capability-negotiation-v1-additive). Nothing is renamed or removed.

**D5 — `request_id` alias and optional envelope fields are undocumented in the design.**
Three reference behaviors the design does not mention, all frozen here: `id` is accepted as an alias
for `request_id` on both sides (`protocol.py:52-55`, `sidecar_protocol.rs:13`, `:23`); `schema` and
`version` are **optional on inbound requests** and validated only when present
(`protocol.py:152-169`); and `shutdown` returns the specific payload `{"stopping": true}`, which is
also the producer's own signal to break its loop (`protocol.py:253-260`).

**D6 — Not every "verbatim from bench-pins.json" knob is in bench-pins.json.**
The P1 plan's Global Constraints describe the Qwen3 knobs as "verbatim from bench-pins.json". Four are
(`pooling`, `query_instruction`, `document_instruction`, dims/lanes). Three are **not in that file**:
the `<|endoftext|>` EOS append lives in `eval/model-bench/bench.py:28` (applied at `:151` and `:156`);
"L2 normalization always" is expressed as the llama-server flag `--embd-normalize 2`
(`bench.py:65`, noted in `bench-pins.json:18`); and the `slice → renormalize → quantize` order lives in
`bench.py:298-309`, sourced from design §4.1. Values are unchanged — only their provenance differs
from the plan's claim, and [§ Model knob table](#model-knob-table) records the real source per row.

**D7 — Fallback pins entry id.**
The P1 plan's Global Constraints name the fallback pins entry `bge-small-f32`. The actual id in
`bench-pins.json:46` is `bge-small-en-v1.5-f32`. This contract uses the file's id, since the pins file
is the single source for model identity.

**D8 — Named Rust consumer contains no protocol logic.**
The plan cites `~/source/julie/src/bin/julie-embedding-host.rs` as the Rust consumer. That binary is
the resident IPC host: it resolves paths, takes a singleton lock, and delegates to
`julie_pipeline::embeddings::host_server` (`julie-embedding-host.rs:41`). It never constructs an
envelope. The stdio consumer of this protocol is
`crates/julie-pipeline/src/embeddings/sidecar_provider.rs` with types and validators in
`sidecar_protocol.rs`; those are the files cited throughout this document.

**D9 — Reference dims guard is narrower than the pinned storage lane.**
`runtime.py:10` restricts the Python runtime to `{384, 768, 1024}`. The pinned default storage lane is
512d. This is not a wire conflict — the guard is producer-local and the wire places no enum on `dims` —
but a P2a implementation must not copy the guard, or it will reject its own default lane. Recorded so
the omission reads as deliberate.

## Boundary

This contract covers the sidecar's wire protocol, its model-knob configuration, its acquisition
subcommand, and its backend selection behavior. It does **not** cover: the shape or lifecycle of
Miller's `<workspace>/.miller/vectors.db` artifact (see [`vectors-v1.md`](vectors-v1.md)), Miller's
process supervision policy beyond the timeouts a sidecar must tolerate
([design §4.2](../plans/2026-07-19-miller-semantic-integration-design.md)), retrieval fusion or
ranking (design §6), or any fleet-level semantic concern, which stays outside Miller entirely
([ADR-0003](../adr/ADR-0003-semantic-retrieval-ownership.md)).

`MILLER_SEMANTIC=off` is a permanent zero-work guarantee: with it set, no sidecar process is spawned
and nothing in this contract executes.

## Stability rules

- v1 is **frozen**. Adding, renaming, removing, or repurposing any envelope field, method name, error
  code, knob value, prompt template, or conformance bar requires a v2 document and an envelope
  `version` bump.
- Fields marked **v1 additive** may be *added* by an implementation without a version bump, because
  the ignore-unknown rule makes them safe. They may not be made *required* without a v2.
- A v1 peer ignores fields it does not recognize; it never errors on them.
- The four error codes are a closed set. A new failure mode maps onto an existing code or waits for v2.
- The model knob table is the single source for embedding-affecting configuration. No other document,
  environment variable, or caller parameter may override pooling, instruction templates, EOS append,
  normalization, or MRL order.
- The tolerance policy in [§ Conformance](#conformance) is the same policy stated in the fixture
  README and the P1 plan. If the three documents ever disagree, that is a defect to reconcile, not a
  choice to make.
