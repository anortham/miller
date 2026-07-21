#!/usr/bin/env python3
"""Embed, sanity-check, and rank for the Miller semantic model benchmark.

Throwaway bench tooling, not product code.

Subcommands:
  sanity  — pooling sanity check for one candidate (must pass before scoring)
  embed   — embed the corpus + dev queries for one candidate
  rank    — MRL-slice / quantize / cosine-rank one lane into a results JSONL

Embeddings come from `llama-server`'s OpenAI-compatible `/v1/embeddings`
endpoint. The upstream macos-arm64 prebuilt archive ships no `llama-embedding`
binary (examples/ are not built in release.yml), so HTTP is the only prebuilt
path — see bench-pins.json.
"""

import argparse
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

import numpy as np

QWEN_EOS = "<|endoftext|>"


def truncate_on_word(text: str, budget: int) -> str:
    if len(text) <= budget:
        return text
    cut = text[:budget]
    space = cut.rfind(" ")
    return (cut[:space] if space > budget * 0.6 else cut).rstrip()


def load_pins(pins_path: Path) -> dict:
    return json.loads(pins_path.read_text())


def candidate(pins: dict, cid: str) -> dict:
    for c in pins["candidates"]:
        if c["id"] == cid:
            return c
    raise SystemExit(f"unknown candidate: {cid}")


class LlamaServer:
    """Owns a llama-server child for the duration of one candidate's embedding."""

    def __init__(self, binary: Path, model: Path, pooling: str, ctx: int, port: int, log: Path):
        self.binary, self.model, self.pooling = binary, model, pooling
        self.ctx, self.port, self.log = ctx, port, log
        self.proc = None
        self.load_seconds = None

    def __enter__(self):
        self.log.parent.mkdir(parents=True, exist_ok=True)
        cmd = [
            str(self.binary), "-m", str(self.model),
            "--embedding", "--pooling", self.pooling,
            "-c", str(self.ctx), "-b", "8192", "-ub", "8192",
            "--embd-normalize", "2",
            "--host", "127.0.0.1", "--port", str(self.port),
            "-np", "1", "--log-disable",
        ]
        started = time.monotonic()
        self.logfh = self.log.open("w")
        self.logfh.write(" ".join(cmd) + "\n")
        self.logfh.flush()
        self.proc = subprocess.Popen(cmd, stdout=self.logfh, stderr=subprocess.STDOUT)
        self._await_health()
        self.load_seconds = time.monotonic() - started
        print(f"  llama-server ready in {self.load_seconds:.1f}s (pooling={self.pooling})")
        return self

    def _await_health(self, timeout=300):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.proc.poll() is not None:
                raise SystemExit(f"llama-server exited early; see {self.log}")
            try:
                with urllib.request.urlopen(f"http://127.0.0.1:{self.port}/health", timeout=2) as r:
                    if r.status == 200:
                        return
            except (urllib.error.URLError, TimeoutError, ConnectionError):
                time.sleep(0.5)
        raise SystemExit(f"llama-server did not become healthy; see {self.log}")

    def embed(self, texts, batch=32) -> np.ndarray:
        out = []
        for i in range(0, len(texts), batch):
            out.append(self._embed_batch(texts[i : i + batch]))
            done = min(i + batch, len(texts))
            if done % 2048 < batch or done == len(texts):
                print(f"    embedded {done}/{len(texts)}", flush=True)
        return np.vstack(out)

    def _embed_batch(self, texts) -> np.ndarray:
        payload = json.dumps({"input": texts, "model": "bench"}).encode()
        req = urllib.request.Request(
            f"http://127.0.0.1:{self.port}/v1/embeddings",
            data=payload,
            headers={"Content-Type": "application/json"},
        )
        for attempt in range(3):
            try:
                with urllib.request.urlopen(req, timeout=600) as r:
                    body = json.loads(r.read())
                rows = sorted(body["data"], key=lambda d: d["index"])
                return np.asarray([d["embedding"] for d in rows], dtype=np.float32)
            except (urllib.error.URLError, TimeoutError, ConnectionError) as exc:
                if attempt == 2:
                    raise SystemExit(f"embed failed after retries: {exc}")
                time.sleep(2)

    def __exit__(self, *exc):
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=20)
            except subprocess.TimeoutExpired:
                self.proc.kill()
        self.logfh.close()


def text_budget(cand: dict) -> int | None:
    """Character cap for a candidate's input text, or None for no cap.

    Short-context models (bge and arctic are both 512-token) reject an
    over-length request outright with HTTP 400 rather than truncating, so the
    caller must fit the text. Code is token-dense — roughly 1.6 chars/token
    here, against ~4 for prose — and the cap leaves headroom for the
    instruction prefix. This is a real capability difference: a 512-token model
    genuinely sees less of each card than Qwen3's 32K context does, and the
    findings doc reports it as such rather than hiding it in the harness.
    """
    ctx = cand.get("context_length", 0)
    return int(ctx * 1.6) if 0 < ctx <= 1024 else None


def _fit(text: str, cand: dict) -> str:
    budget = text_budget(cand)
    return truncate_on_word(text, budget) if budget and len(text) > budget else text


def prep_doc(text: str, cand: dict) -> str:
    text = _fit(cand.get("document_instruction", "") + text, cand)
    return text + QWEN_EOS if cand["id"].startswith("qwen3") else text


def prep_query(text: str, cand: dict) -> str:
    text = _fit(cand.get("query_instruction", "") + text, cand)
    return text + QWEN_EOS if cand["id"].startswith("qwen3") else text


def l2(mat: np.ndarray) -> np.ndarray:
    norms = np.linalg.norm(mat, axis=1, keepdims=True)
    return mat / np.maximum(norms, 1e-12)


def read_queries(path: Path):
    out = []
    for line in path.read_text().splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            out.append(json.loads(line))
    return out


def read_corpus(path: Path):
    return [json.loads(l) for l in path.read_text().splitlines() if l.strip()]


# --- sanity -----------------------------------------------------------------

SANITY_PAIRS = {
    "anchor": "method FullRebuildPromotion.Promote atomically replaces the served index artifact with a freshly rebuilt database file",
    "similar": "swap a newly built index database over the live one instead of merging into it while readers hold it open",
    "dissimilar": "css property background-color sets the paint color behind an element's content box",
}
SANITY_MARGIN = 0.10


def cmd_sanity(args):
    pins = load_pins(args.pins)
    cand = candidate(pins, args.candidate)
    with LlamaServer(args.binary, args.model, cand["pooling"], min(cand["context_length"], 8192),
                     args.port, args.out.parent / f"llama-{cand['id']}-sanity.log") as srv:
        vecs = l2(srv.embed([
            prep_query(SANITY_PAIRS["anchor"], cand),
            prep_doc(SANITY_PAIRS["similar"], cand),
            prep_doc(SANITY_PAIRS["dissimilar"], cand),
        ]))
        load_seconds = srv.load_seconds

    sim = float(vecs[0] @ vecs[1])
    dis = float(vecs[0] @ vecs[2])
    margin = sim - dis
    passed = margin >= SANITY_MARGIN

    result = {
        "candidate": cand["id"],
        "pooling": cand["pooling"],
        "similar_cosine": round(sim, 4),
        "dissimilar_cosine": round(dis, 4),
        "margin": round(margin, 4),
        "required_margin": SANITY_MARGIN,
        "passed": passed,
        "load_seconds": round(load_seconds, 2),
        "native_dims": int(vecs.shape[1]),
        "declared_dims": cand["native_dims"],
        "dims_match": int(vecs.shape[1]) == cand["native_dims"],
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))
    if not result["dims_match"]:
        print(f"SANITY FAIL: dims {vecs.shape[1]} != declared {cand['native_dims']}", file=sys.stderr)
        return 3
    if not passed:
        print(f"SANITY FAIL: margin {margin:.4f} < {SANITY_MARGIN}", file=sys.stderr)
        return 3
    print(f"SANITY PASS: {cand['id']} margin {margin:.4f}")
    return 0


# --- embed ------------------------------------------------------------------


def cmd_embed(args):
    pins = load_pins(args.pins)
    cand = candidate(pins, args.candidate)
    corpus = read_corpus(args.corpus)
    queries = read_queries(args.queries)
    args.outdir.mkdir(parents=True, exist_ok=True)

    ctx = min(cand["context_length"], 8192)
    prepared = [prep_doc(u["text"], cand) for u in corpus]
    budget = text_budget(cand)
    truncated = sum(1 for u, p in zip(corpus, prepared) if budget and len(u["text"]) > budget)

    with LlamaServer(args.binary, args.model, cand["pooling"], ctx, args.port,
                     args.outdir / f"llama-{cand['id']}.log") as srv:
        t0 = time.monotonic()
        doc_vecs = l2(srv.embed(prepared, batch=args.batch))
        corpus_seconds = time.monotonic() - t0

        t1 = time.monotonic()
        q_vecs = l2(srv.embed([prep_query(q["query"], cand) for q in queries], batch=args.batch))
        query_seconds = time.monotonic() - t1
        load_seconds = srv.load_seconds

    np.save(args.outdir / f"{cand['id']}.corpus.npy", doc_vecs)
    np.save(args.outdir / f"{cand['id']}.queries.npy", q_vecs)

    perf = {
        "candidate": cand["id"],
        "corpus_units": len(corpus),
        "queries": len(queries),
        "native_dims": int(doc_vecs.shape[1]),
        "model_load_seconds": round(load_seconds, 2),
        "corpus_embed_seconds": round(corpus_seconds, 1),
        "corpus_units_per_second": round(len(corpus) / corpus_seconds, 1),
        "query_embed_seconds": round(query_seconds, 2),
        "query_embed_ms_per_query": round(query_seconds / len(queries) * 1000, 1),
        "batch_size": args.batch,
        "context_length": cand["context_length"],
        "text_budget_chars": budget,
        "units_truncated": truncated,
        "units_truncated_pct": round(truncated / len(corpus) * 100, 1) if corpus else 0.0,
    }
    (args.outdir / f"{cand['id']}.perf.json").write_text(json.dumps(perf, indent=2) + "\n")
    print(json.dumps(perf, indent=2))
    return 0


# --- rank -------------------------------------------------------------------


def quantize_int8(mat: np.ndarray) -> np.ndarray:
    """Symmetric per-vector int8, then dequantize — models the storage lane's
    precision loss without pulling in a vector store."""
    scale = np.maximum(np.abs(mat).max(axis=1, keepdims=True), 1e-12) / 127.0
    return np.round(mat / scale).astype(np.int8).astype(np.float32) * scale


def cmd_rank(args):
    pins = load_pins(args.pins)
    cand = candidate(pins, args.candidate)
    corpus = read_corpus(args.corpus)
    queries = read_queries(args.queries)

    doc_vecs = np.load(args.vecdir / f"{cand['id']}.corpus.npy")
    q_vecs = np.load(args.vecdir / f"{cand['id']}.queries.npy")

    if args.dims < doc_vecs.shape[1]:
        if not cand.get("mrl"):
            raise SystemExit(f"{cand['id']} is not MRL — cannot slice to {args.dims}")
        doc_vecs, q_vecs = doc_vecs[:, : args.dims], q_vecs[:, : args.dims]
    elif args.dims > doc_vecs.shape[1]:
        raise SystemExit(f"requested {args.dims} dims > native {doc_vecs.shape[1]}")
    # MRL contract: slice THEN renormalize (design §4.1).
    doc_vecs, q_vecs = l2(doc_vecs), l2(q_vecs)

    if args.quant == "int8":
        doc_vecs, q_vecs = l2(quantize_int8(doc_vecs)), l2(quantize_int8(q_vecs))

    doc_repo = np.array([u["repo"] for u in corpus])
    doc_id = np.array([u["doc_id"] for u in corpus])
    is_test = np.array([bool(u.get("is_test")) for u in corpus])
    # Symbol-card units carry the real symbols.db id inside their unit_id
    # ("{repo}:sym:{symbol_id}"), which is the join key the fusion-arm adapter
    # shares with the lexical CLI dumps.
    unit_sym = np.array([u["unit_id"].split(":", 2)[2] if u["unit_id"].split(":", 2)[1] == "sym" else ""
                         for u in corpus])

    # Production parity (design §5.2): test symbols get cards but are excluded
    # from default search recall via the is_test metadata filter, and Miller's
    # BM25 baseline already hides them for natural-language queries. Ranking
    # them here would let the semantic arms compete over a doc population the
    # shipped surface never returns.
    eligible = np.ones(len(corpus), dtype=bool) if args.include_tests else ~is_test
    excluded_tests = int(is_test.sum()) if not args.include_tests else 0

    if args.symbol_dump is not None:
        args.symbol_dump.mkdir(parents=True, exist_ok=True)

    rows, kept_counts = [], []
    for qi, q in enumerate(queries):
        mask = (doc_repo == q["repo"]) & eligible
        sims = doc_vecs[mask] @ q_vecs[qi]
        ids = doc_id[mask]

        if args.symbol_dump is not None:
            # Production semantic-arm shape: symbol cards only, top-k by cosine,
            # unthresholded (the serving arm's KNN has no cosine floor).
            syms = unit_sym[mask]
            sym_rows = [(float(s), sid, did) for s, sid, did in zip(sims, syms, ids) if sid]
            sym_rows.sort(key=lambda t: (-t[0], t[1]))
            dump = [{"symbol_id": sid, "doc_id": did, "score": 0.0, "rank": n + 1}
                    for n, (_, sid, did) in enumerate(sym_rows[: args.symbol_k])]
            (args.symbol_dump / f"{q['query_id']}.json").write_text(json.dumps(dump))

        # Collapse unit scores onto doc_ids: a file's score is its best unit.
        best = {}
        for score, did in zip(sims, ids):
            if score > best.get(did, -2.0):
                best[did] = float(score)
        ordered = sorted(best.items(), key=lambda kv: (-kv[1], kv[0]))[: args.k]

        # Threshold policy (documented in README): a doc is shown only if it
        # clears an absolute cosine floor AND stays within a relative band of
        # the query's best hit. Results are therefore post-threshold, which is
        # what the scorer's negative-query rule requires.
        # ratio <= 0 disables the relative band entirely (the `topk` policy).
        # Multiplying by a zero ratio would still silently drop negative
        # cosines, which is filtering, not "unthresholded".
        band = ordered[0][1] * args.ratio if ordered and args.ratio > 0 else None
        ranked = [
            d for d, s in ordered
            if s >= args.floor and (band is None or s >= band)
        ]
        kept_counts.append(len(ranked))
        rows.append({"query_id": q["query_id"], "ranked": ranked})

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w") as fh:
        for r in rows:
            fh.write(json.dumps(r) + "\n")
    print(f"{args.out.name}: {len(rows)} queries, mean kept {np.mean(kept_counts):.2f}, "
          f"empty {sum(1 for c in kept_counts if c == 0)}, "
          f"test units excluded {excluded_tests}/{len(corpus)}")
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    sub = ap.add_subparsers(dest="cmd", required=True)

    for name in ("sanity", "embed", "rank"):
        p = sub.add_parser(name)
        p.add_argument("--pins", type=Path, required=True)
        p.add_argument("--candidate", required=True)
        if name in ("sanity", "embed"):
            p.add_argument("--binary", type=Path, required=True)
            p.add_argument("--model", type=Path, required=True)
            p.add_argument("--port", type=int, default=8477)
        if name == "sanity":
            p.add_argument("--out", type=Path, required=True)
        if name == "embed":
            p.add_argument("--corpus", type=Path, required=True)
            p.add_argument("--queries", type=Path, required=True)
            p.add_argument("--outdir", type=Path, required=True)
            p.add_argument("--batch", type=int, default=32)
        if name == "rank":
            p.add_argument("--corpus", type=Path, required=True)
            p.add_argument("--queries", type=Path, required=True)
            p.add_argument("--vecdir", type=Path, required=True)
            p.add_argument("--out", type=Path, required=True)
            p.add_argument("--dims", type=int, required=True)
            p.add_argument("--quant", choices=["f32", "int8"], default="f32")
            p.add_argument("--k", type=int, default=10)
            p.add_argument("--floor", type=float, default=0.35)
            p.add_argument("--ratio", type=float, default=0.85)
            p.add_argument("--include-tests", action="store_true",
                           help="rank is_test corpus units too (off by default: production parity)")
            p.add_argument("--symbol-dump", type=Path, default=None,
                           help="also write per-query symbol-level rankings (fusion-arm adapter input) here")
            p.add_argument("--symbol-k", type=int, default=20,
                           help="symbol-dump depth: production semantic KNN k for a limit-10 request")

    args = ap.parse_args()
    return {"sanity": cmd_sanity, "embed": cmd_embed, "rank": cmd_rank}[args.cmd](args)


if __name__ == "__main__":
    sys.exit(main())
