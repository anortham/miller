# codesearch — research findings (2026-05)

> Historical origin research. These docs predate the Miller name and `julie-extract` product split, and may mention
> `codesearch`, `julie-server`, or old schema versions. Use [`../README.md`](../README.md) for the docs map and
> [`../../README.md`](../../README.md) for current behavior.

Durable record of the viability investigation that decided whether to revive this project and how to build it.
**Purpose: so we never have to re-run these experiments.** Each doc states the question, the method, the
measured data, the conclusion, and how to reproduce.

> Context: codesearch is a C#/.NET code-intelligence MCP server. It was frozen 2026-01-28. This investigation
> (2026-05-28) resolved every reason it stalled and settled the architecture. See the personal memory at
> `~/.claude/projects/-Users-murphy-source-codesearch/memory/` for the running decision log.

## TL;DR

1. **The architecture is settled: pure C#/.NET + one Rust `julie-server extract` subprocess. No Python, no embeddings, no GPU, no LanceDB/tantivy, no in-process Rust/FFI.**
2. Every original blocker is gone:
   - **Parsing**: shell out to julie's prebuilt `julie-server extract` CLI → canonical SQLite. Zero FFI. ~15k symbols/sec. → [search-and-storage.md](search-and-storage.md)
   - **Lexical search**: pure-.NET. In-memory inverted index = ~35 MB RAM, **25 µs** ranked queries over 565k symbols. FTS5 = 78 MB on disk, 3.5 ms. The old "huge index / poor perf" was the **vectors**, not lexical. → [search-and-storage.md](search-and-storage.md)
   - **Embeddings**: not needed. The fast Apple-Silicon path (PyTorch MPS) has no pure-.NET peer, but it doesn't matter because → [embeddings.md](embeddings.md)
   - **Cross-language bridge** (the sole remaining justification for embeddings): proven recoverable **without embeddings** by deterministic lexical+structural signals, across **3 repos / 2 convention styles** (97–100% recall, embeddings rescue **0** concepts). → [cross-language-bridge.md](cross-language-bridge.md)
3. **The decision record**: → [architecture-decision.md](architecture-decision.md)

## Index

| doc | covers |
|---|---|
| [search-and-storage.md](search-and-storage.md) | extract→SQLite contract; FTS5 vs in-memory index benchmark; .NET 10 SIMD cosine + tokenizer perf |
| [embeddings.md](embeddings.md) | MPS vs llama.cpp-Metal; .NET accel matrix; storage cost; why embeddings are out of the default path |
| [cross-language-bridge.md](cross-language-bridge.md) | the 3-repo bridge probe (method, gold sets, per-strategy results, the resolver spec) |
| [architecture-decision.md](architecture-decision.md) | the settled architecture + ADR-style rationale + open questions |

## How to reproduce (commands)

```bash
# 1. Extract any repo to canonical SQLite (Rust prebuilt, daemon-free)
JS=~/source/julie/target/release/julie-server
$JS extract scan --root ~/source/<REPO> --db /tmp/<repo>.sqlite --standalone --json

# 2. Search-index + .NET perf benchmarks (spike project)
cd ~/source/codesearch/spike/Codesearch.Spike
dotnet run -c Release -- contract /tmp/<repo>.sqlite   # verify extract→C# contract
dotnet run -c Release -- bench    /tmp/<repo>.sqlite   # cosine (TensorPrimitives) + tokenizer
dotnet run -c Release -- search   /tmp/<repo>.sqlite   # FTS5 pretok / FTS5 trigram / in-memory index
dotnet run -c Release -- embed    /tmp/<repo>.sqlite   # LLamaSharp embedding throughput (CPU vs Metal)

# 3. Cross-language bridge probe (12-agent workflow, parameterized)
#    Workflow tool, scriptPath = spike/xlang-bridge-probe-generic.js
#    args = {"repo":"Tycho","db":"/tmp/tycho.sqlite","root":"/Users/murphy/source/Tycho","conventions":"..."}
```

Spike sources: `spike/Codesearch.Spike/{SearchBench,Bench,EmbedBench,CodeTokenizer,ContractCheck}.cs`.
Probe workflow: `spike/xlang-bridge-probe-generic.js`.
