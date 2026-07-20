# Sidecar conformance fixtures

Frozen conformance fixture set for `julie-semantic-sidecar`. A sidecar implementation — the P2a binary,
or P2b's fake sidecar — proves it embeds correctly by reproducing these vectors under its own runtime,
without needing the other side to exist.

| File | What it is |
|---|---|
| `corpus.jsonl` | 39 input texts, fixed order, each labelled with an edge-case `class` and a `role` |
| `golden-qwen3-0.6b-f16.jsonl` | Golden vectors for the pinned default model (1024d native, 512d int8 lane) |
| `golden-bge-small-f32.jsonl` | Golden vectors for the pinned fallback model (384d native, 384d int8 lane) |
| `generate.py` | Regenerates the goldens, and (`--verify`) asserts the tolerance policy against them |

Committed payload is ~790 KiB of JSONL. No model weights or binaries live here.

## Regeneration

```bash
python3 eval/sidecar-conformance/generate.py            # regenerate and overwrite the goldens
python3 eval/sidecar-conformance/generate.py --verify   # regenerate and assert tolerance (the gate)
```

`--verify` exits non-zero and lists every violation if any vector drifts. It never writes.

Both modes read the P0 model-bench cache and **never download anything**. If the cache is missing, the
script fails with the restore command rather than fetching:

```bash
eval/model-bench/run-bench.sh download
eval/model-bench/run-bench.sh verify
```

The pinned llama.cpp build (`b10068`) and both GGUF sha256s come from
[`../model-bench/bench-pins.json`](../model-bench/bench-pins.json), which is the single source of model
identity. `generate.py` refuses to run if the pinned llama.cpp release tag no longer matches the build the
goldens were generated with.

## Why CPU goldens

The cached macOS `llama-server` is a Metal build and offloads to the GPU by default. GPU and CPU kernels
produce numerically different results for the same input, so a Metal-generated golden would not be
reproducible on a Linux CI runner, a Vulkan box, or a Mac with a different GPU.

Goldens are therefore generated on the **CPU backend**, which is the one backend every platform has.
`generate.py` does not merely request CPU — it proves it. Before any embedding runs, `prove_cpu_backend()`
starts a verbose short-lived server and parses its device assignments, failing the run unless *every* layer
reports `assigned to device CPU` and no `using device …` offload line appears. CPU is forced with
`LLAMA_ARG_DEVICE=none` and `LLAMA_ARG_N_GPU_LAYERS=0`, the documented environment equivalents of
`-dev none` / `-ngl 0`; using the environment lets the bench's proven `LlamaServer` command line be reused
verbatim rather than re-derived.

This is also why bitwise equality is explicitly **not** the bar: an implementation running on Metal, CUDA,
or Vulkan will differ in the last few significant digits and still be correct.

## Tolerance policy (frozen)

Every emitted vector must satisfy all of:

| Check | Bar | Applies to |
|---|---|---|
| Dimensionality | exactly equal to the requested lane | native and lane vectors |
| L2 norm | within `1e-3` of `1.0` | emitted **float** vectors (`vector_native`, `norm_lane`) |
| Cosine vs. golden | `>= 0.999` | native vector and reconstructed lane vector |
| int8 quantization fidelity | cosine `>= 0.999` against its own pre-quantization float | `vector_lane_int8` |
| int8 code range | `abs(code) <= 127` | `vector_lane_int8` |

**The norm bar governs float vectors, not int8 storage codes.** `vector_lane_int8` is a storage encoding:
reconstructing it as `code * lane_int8_scale` legitimately produces an L2 norm off by roughly `1.5e-3` at
384/512 dims, because symmetric per-vector int8 rounding is lossy by construction. That is quantization
physics, not drift. The renormalized float the codes are derived from (`norm_lane`) is held to the `1e-3`
bar, and the codes themselves are bounded by the two cosine checks above — which is a stricter statement
about the vector's *direction*, the only property cosine retrieval consumes.

### Float rounding to 6 decimals

Committed floats are rounded to 6 decimal places. A 6-decimal perturbation moves a unit vector by at most
`~5e-7` per component, which for 1024 dims bounds the cosine change at roughly `1e-8` — five orders of
magnitude inside the `0.999` bar and two inside the `1e-3` norm bar. Rounding therefore cannot mask a real
implementation defect, and it cuts the committed payload roughly in half versus full float32 repr.

## Corpus

39 texts in a fixed order (the order *is* part of the fixture). Each row carries `text_id`, `class`, `role`,
and `text`; some carry `notes`, `sanitization_expected`, or `batch_expand`.

`role` selects the instruction template — `query` gets the model's `query_instruction`, `document` gets its
(empty) `document_instruction`. Callers never choose knobs; model identity does.

Classes covered: `ascii_identifier`, `code_snippet`, `sql`, `nl_prose`, `markdown_fence`, `cjk`,
`emoji_astral`, `mixed_script`, `url_path`, `structured_config`, `single_char`, `whitespace_only`,
`empty_string`, `control_bytes`, `long_truncation`, `batch_group`.

Two of these encode behaviours worth calling out.

**Empty and whitespace-only input is not an error.** This is recorded from the running reference
implementation, not assumed: `~/source/julie/python/embeddings_sidecar/sidecar/runtime.py:233-250`
(`_sanitize_texts`) replaces any non-string, empty, or whitespace-only input with the literal string
`[empty]` and strips NUL bytes from everything else, then embeds normally. `generate.py` mirrors that
function exactly. Hence `empty-001` and `whitespace-001/002` have real golden vectors — the vector of
`[empty]` — and `control-bytes-001` does *not* get the substitution, because stripping its NUL leaves a
non-blank string. A conformant sidecar must reproduce all four.

**Batch semantics.** `batch-group-001` carries `batch_expand: 250`. During generation the text is embedded
250 times inside a single batch and every position is checked against position 0 at the `0.999` cosine bar,
so batch size and position cannot perturb a vector. Only one golden vector is committed for it; the
generator asserts the invariance and records `batch_group_positions_checked: 250`.

`long-truncation-001/002` exceed bge-small's 512-token budget and are flagged `input_truncated: true` in the
bge goldens and `false` in the qwen3 goldens (32K context) — a real capability difference the fixtures record
rather than hide.

## Golden row shape

```jsonc
{
  "text_id": "ascii-ident-001",
  "class": "ascii_identifier",
  "role": "query",
  "model": "qwen3-0.6b-f16",
  "storage_schema": "vec0-int8-512-cosine-v1",
  "native_dims": 1024,
  "lane_dims": 512,
  "instruction_applied": true,      // a non-empty instruction template was prefixed
  "eos_appended": true,             // qwen3 only: "<|endoftext|>" appended after prefixing
  "sanitized_to_empty_marker": false,
  "input_truncated": false,         // text exceeded the model's char budget and was cut on a word
  "prepared_chars": 132,            // length of the final string handed to the tokenizer
  "norm_native": 1.0,
  "norm_lane": 1.0,                 // after slice -> renormalize, before quantization
  "vector_native": [/* native_dims floats, 6dp */],
  "vector_lane_int8": [/* lane_dims ints in [-127, 127] */],
  "lane_int8_scale": 0.007871,      // reconstruct with code * scale
  "batch_group_positions_checked": null,
  "generator": {
    "llama_cpp": "b10068",
    "backend": "cpu",
    "pooling": "last",
    "model_file": "Qwen3-Embedding-0.6B-f16.gguf",
    "model_sha256": "421a27e5…",
    "server_flags": { "embd_normalize": 2, "ctx": 8192, "device": "none", "n_gpu_layers": 0, "…": "…" },
    "request_batch_size": 16,
    "float_decimals": 6
  }
}
```

Native vectors are committed for **all 39 texts** in both models — the payload fits the 2 MB budget with
room to spare, so no core-subset fallback was needed.

### Lane derivation order

Frozen as **slice → renormalize → quantize**:

1. take the first `lane_dims` components of the L2-normalized native vector (MRL slice; a no-op for
   bge-small, whose lane equals its native width),
2. re-normalize the slice to unit length,
3. symmetric per-vector int8: `scale = max(abs(v)) / 127`, `code = round(v / scale)`.

Reversing steps 1 and 2 produces a different vector. Implementations must use this order.

## Pass/fail rule for implementations

> A sidecar implementation **passes conformance** if and only if, for every text in `corpus.jsonl`, embedded
> under its own runtime and backend with the role's instruction template applied, every check in the
> tolerance policy table above holds against the committed golden for that model — for all 39 texts and both
> pinned models. One text failing one check fails conformance.

An implementation is free to run on any backend and any hardware; it is not free to produce a vector that
points somewhere else. Failing only on a specific backend is a backend bug, not a licence to relax the bar.

## Reproducibility

Generation is deterministic given the cache: the corpus order is fixed, server settings are fixed, embedding
is temperature-free, and the CPU backend is asserted rather than assumed. The committed goldens were
produced by one run and independently reproduced by a later `--verify` run on the same cache. The gate is
bidirectional — a deliberately corrupted golden was confirmed to fail it.

Generation and verification each take well under a minute for both models on an Apple M2 Ultra. Wall time is
report-only; it is not a gate.
