# Embeddings findings (2026-05-28)

Machine: Apple M2 Ultra. The original abandonment story blamed "embedding hardware acceleration." This doc
records what's actually true and why embeddings are **out of the default pipeline**.

## 1. The runtime was never the blocker — batching + the wrong GPU path were

Measured (`spike/Codesearch.Spike/EmbedBench.cs`, LLamaSharp 0.25, nomic-embed-text-v1.5 Q8_0, 768-dim, batch=1):

| backend | throughput | per-symbol |
|---|---|---|
| CPU | 109 sym/s | 9.2 ms |
| Metal (GPU, 13/13 layers offloaded) | 36 sym/s | 27.6 ms |

**CPU beat Metal 3×.** Cause: batch-1 + short sequences + a tiny model → GPU kernel-dispatch/sync overhead
dwarfs compute. The old 51–182 s indexing was the **ONNX+CoreML** path (CoreML falls back to CPU on transformer
ops); the committed-but-never-benchmarked LLamaSharp/Metal path is what this measured.

## 2. MPS vs Metal — the real fast path on Apple Silicon (and why .NET can't match it)

Two different "GPU" paths, often conflated:
- **llama.cpp Metal** (what we benchmarked): slow for batch-1 embeddings, loses to CPU. (ggml-metal is tuned for
  autoregressive decode, not batched encoder attention.)
- **PyTorch MPS** (what julie's sidecar uses): **significantly faster than CPU** because it BATCHES.

julie's `python/embeddings_sidecar/runtime.py`: model `nomic-ai/CodeRankEmbed` (code-specific, 768-dim),
sentence-transformers `model.encode(texts, batch_size=32, normalize_embeddings=True)`, float16 on GPU, device
priority cuda→directml→mps→cpu, VRAM-aware batch sizing, OOM/device-fallback handling. It speaks line-delimited
JSON over stdin/stdout — callable from C# exactly like `julie-server extract`.

**No pure-.NET stack reaches MPS/MLX-class batched throughput on Apple Silicon:**
- No MLX binding for .NET (MLX is the Apple-Silicon throughput leader).
- ONNX Runtime CoreML EP is weak for transformers (~25% of nodes on CoreML; ORT itself warns "worse than CPU").
- TorchSharp can't load HF embedding models turnkey; MPS support is beta; no ROCm.
- llama.cpp Metal is bad for embeddings (above).
- ONNX CPU (arm64, SIMD) is "good enough" for small bi-encoders (~5–14k sentences/s) but not GPU-class.

**If embeddings were ever needed, the right move is to REUSE julie's existing sidecar as a 2nd prebuilt
subprocess** (symmetric with the Rust extract CLI) — not reimplement embedding in .NET. But:

## 3. Cost framing (corrected)

- The **"1.6 GB" figure is the FULL-symbol-set vector index** (openclaw 565k × 768 × 4 B) for free-text
  *semantic search* — a separate feature. It is NOT the cross-language bridge's cost.
- The real recurring cost of embeddings is the **Python MPS sidecar dependency**: it violates the pure-.NET goal,
  adds process management + ~8.6 s cold start to a daily-use tool. Disk is secondary.

## 4. Verdict: embeddings are OUT of the default path

The only feature that ostensibly justified embeddings was the cross-language bridge. That is now proven
recoverable **without** embeddings across 3 repos (→ [cross-language-bridge.md](cross-language-bridge.md)):
embeddings rescue **0** concepts and would *introduce* false positives (semantically-close infra pairs score high
cosine similarity → wrong merges). Per julie's own evals lexical already ships as the default there too.

**Decision:** ship pure-.NET lexical + deterministic structural. Keep embeddings out of the bridge entirely.
If retained at all in codesearch, scope them to opt-in NL/free-text semantic *search* (their actual strength),
delivered via the reusable Python sidecar — never as a dependency of the core daily path.
