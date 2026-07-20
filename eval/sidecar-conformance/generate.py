#!/usr/bin/env python3
"""Generate and verify the julie-semantic-sidecar conformance golden vectors.

Embeds `corpus.jsonl` with both pinned models on the **CPU backend** of the
cached llama.cpp `b10068` server and writes one golden JSONL per model.

Two modes:
  (default)  regenerate and overwrite the committed goldens
  --verify   regenerate and assert the frozen tolerance policy against the
             committed goldens without writing them

Everything about how a model is invoked -- pooling, instruction prefixes, EOS
append, text budget, L2 normalization -- is reused from
`eval/model-bench/bench.py`, which is the proven reference for these flags.
No network access: the pinned llama.cpp build and both GGUFs must already be in
`eval/model-bench/.cache/`.
"""

import argparse
import json
import os
import re
import subprocess
import sys
import time
from importlib import util as importlib_util
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
MODEL_BENCH = HERE.parent / "model-bench"
CACHE = MODEL_BENCH / ".cache"
LLAMA_DIR = CACHE / "llama" / "llama-b10068"
DIST = CACHE / "dist"
PINS = MODEL_BENCH / "bench-pins.json"
CORPUS = HERE / "corpus.jsonl"

LLAMA_BUILD = "b10068"
BACKEND = "cpu"
FLOAT_DECIMALS = 6
NORM_TOLERANCE = 1e-3
COSINE_FLOOR = 0.999
EMPTY_SUBSTITUTE = "[empty]"

RESTORE_HINT = (
    "conformance fixtures never download anything. Restore the pinned cache first:\n"
    "    eval/model-bench/run-bench.sh download\n"
    "    eval/model-bench/run-bench.sh verify"
)

# Plan Global Constraints name the fallback pins entry `bge-small-f32`; the
# actual key in bench-pins.json is `bge-small-en-v1.5-f32`. The golden filename
# keeps the plan's short name so the contract citations stay stable.
LANES = [
    {"key": "qwen3-0.6b-f16", "pins_id": "qwen3-0.6b-f16", "lane_dims": 512,
     "storage_schema": "vec0-int8-512-cosine-v1", "golden": "golden-qwen3-0.6b-f16.jsonl"},
    {"key": "bge-small-f32", "pins_id": "bge-small-en-v1.5-f32", "lane_dims": 384,
     "storage_schema": "vec0-int8-384-cosine-v1", "golden": "golden-bge-small-f32.jsonl"},
]


def load_bench_module():
    """Load bench.py without leaving a __pycache__ in the model-bench directory."""
    previous = sys.dont_write_bytecode
    sys.dont_write_bytecode = True
    try:
        spec = importlib_util.spec_from_file_location("model_bench", MODEL_BENCH / "bench.py")
        module = importlib_util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module
    finally:
        sys.dont_write_bytecode = previous


def require_cache() -> tuple[Path, dict]:
    server = LLAMA_DIR / "llama-server"
    if not server.is_file():
        raise SystemExit(f"missing {server}\n{RESTORE_HINT}")
    if not PINS.is_file():
        raise SystemExit(f"missing {PINS}\n{RESTORE_HINT}")
    pins = json.loads(PINS.read_text())
    if pins["runtime"]["release_tag"] != LLAMA_BUILD:
        raise SystemExit(
            f"pinned llama.cpp is {pins['runtime']['release_tag']}, goldens were generated with {LLAMA_BUILD}"
        )
    return server, pins


def model_path(cand: dict) -> Path:
    path = DIST / cand["file"]
    if not path.is_file():
        raise SystemExit(f"missing model weights {path}\n{RESTORE_HINT}")
    return path


def cpu_environment() -> dict:
    """Force the Metal-built macOS server onto CPU compute.

    `--list-devices` reports MTL0 on this machine and llama-server offloads by
    default, so both knobs are required for reproducible goldens. These are the
    documented environment equivalents of `-dev none` / `-ngl 0`, which lets the
    bench's LlamaServer command line be reused verbatim.
    """
    os.environ["LLAMA_ARG_DEVICE"] = "none"
    os.environ["LLAMA_ARG_N_GPU_LAYERS"] = "0"
    os.environ.setdefault("DYLD_LIBRARY_PATH", str(LLAMA_DIR))
    os.environ.setdefault("LD_LIBRARY_PATH", str(LLAMA_DIR))
    return dict(os.environ)


def prove_cpu_backend(server: Path, probe_model: Path, port: int, log: Path) -> dict:
    """Assert every layer lands on the CPU device before any golden is trusted."""
    log.parent.mkdir(parents=True, exist_ok=True)
    cmd = [str(server), "-m", str(probe_model), "--embedding", "--pooling", "cls",
           "-c", "512", "--host", "127.0.0.1", "--port", str(port), "-np", "1", "-v"]
    with log.open("w") as fh:
        proc = subprocess.Popen(cmd, stdout=fh, stderr=subprocess.STDOUT, env=cpu_environment())
        deadline = time.monotonic() + 120
        assigned = None
        while time.monotonic() < deadline:
            text = log.read_text(errors="replace")
            if "load_tensors: layer" in text:
                assigned = sorted(set(re.findall(r"assigned to device (\w+)", text)))
                if assigned:
                    break
            if proc.poll() is not None:
                break
            time.sleep(0.5)
        proc.terminate()
        try:
            proc.wait(timeout=20)
        except subprocess.TimeoutExpired:
            proc.kill()

    text = log.read_text(errors="replace")
    if not assigned:
        raise SystemExit(f"could not read device assignment from {log}")
    if assigned != ["CPU"]:
        raise SystemExit(f"backend probe assigned layers to {assigned}, expected CPU only; see {log}")
    using = sorted(set(re.findall(r"using device (\w+)", text)))
    if using:
        raise SystemExit(f"backend probe still selected offload devices {using}; see {log}")
    return {"devices_assigned": assigned, "probe_log": log.name}


def sanitize(text: str) -> tuple[str, bool]:
    """Mirror the reference sidecar's `_sanitize_texts`.

    `~/source/julie/python/embeddings_sidecar/sidecar/runtime.py:233-250`: empty
    or whitespace-only input is never an error -- it is replaced with the
    literal `[empty]`; NUL bytes are stripped from everything else. Recorded
    here as the golden fact, not an assumption.
    """
    if not isinstance(text, str) or not text.strip():
        return EMPTY_SUBSTITUTE, True
    safe = text.replace("\x00", "")
    if not safe.strip():
        return EMPTY_SUBSTITUTE, True
    return safe, safe != text


def read_corpus() -> list[dict]:
    if not CORPUS.is_file():
        raise SystemExit(f"missing {CORPUS}")
    return [json.loads(line) for line in CORPUS.read_text(encoding="utf-8").splitlines() if line.strip()]


def prepare(bench, row: dict, cand: dict) -> tuple[str, bool]:
    sanitized, changed = sanitize(row["text"])
    prep = bench.prep_query if row["role"] == "query" else bench.prep_doc
    return prep(sanitized, cand), changed


def l2(vec: np.ndarray) -> np.ndarray:
    return vec / max(float(np.linalg.norm(vec)), 1e-12)


def quantize_int8(vec: np.ndarray) -> tuple[np.ndarray, float]:
    scale = max(float(np.abs(vec).max()), 1e-12) / 127.0
    return np.round(vec / scale).astype(np.int8), scale


def lane_vector(native: np.ndarray, lane_dims: int) -> tuple[np.ndarray, float, np.ndarray]:
    """Frozen order: slice -> renormalize -> quantize (design MRL contract)."""
    sliced = l2(native[:lane_dims])
    codes, scale = quantize_int8(sliced)
    return codes, scale, sliced


def round_floats(vec: np.ndarray) -> list[float]:
    return [round(float(v), FLOAT_DECIMALS) for v in vec]


def embed_lane(bench, lane: dict, pins: dict, server: Path, port: int, logdir: Path) -> list[dict]:
    cand = bench.candidate(pins, lane["pins_id"])
    weights = model_path(cand)
    corpus = read_corpus()
    ctx = min(cand["context_length"], 8192)

    prepared, truncated_flags = [], []
    for row in corpus:
        text, changed = prepare(bench, row, cand)
        prepared.append(text)
        budget = bench.text_budget(cand)
        raw_prefixed = (cand.get("query_instruction", "") if row["role"] == "query"
                        else cand.get("document_instruction", "")) + sanitize(row["text"])[0]
        truncated_flags.append(bool(budget and len(raw_prefixed) > budget))
        row["_sanitized"] = changed

    started = time.monotonic()
    with bench.LlamaServer(server, weights, cand["pooling"], ctx, port,
                           logdir / f"llama-{lane['key']}.log") as srv:
        vectors = srv.embed(prepared, batch=16)
        batch_checks = run_batch_group_check(corpus, prepared, srv)
    elapsed = time.monotonic() - started

    generator = {
        "llama_cpp": LLAMA_BUILD,
        "backend": BACKEND,
        "pooling": cand["pooling"],
        "model_file": cand["file"],
        "model_sha256": cand["sha256"],
        "server_flags": {
            "embd_normalize": 2, "ctx": ctx, "batch": 8192, "ubatch": 8192,
            "n_parallel": 1, "device": "none", "n_gpu_layers": 0,
        },
        "request_batch_size": 16,
        "float_decimals": FLOAT_DECIMALS,
    }

    rows = []
    for row, prep_text, native, truncated in zip(corpus, prepared, vectors, truncated_flags):
        native = l2(np.asarray(native, dtype=np.float32))
        codes, scale, sliced = lane_vector(native, lane["lane_dims"])
        rows.append({
            "text_id": row["text_id"],
            "class": row["class"],
            "role": row["role"],
            "model": lane["key"],
            "storage_schema": lane["storage_schema"],
            "native_dims": int(native.shape[0]),
            "lane_dims": lane["lane_dims"],
            "instruction_applied": bool(
                cand.get("query_instruction" if row["role"] == "query" else "document_instruction", "")),
            "eos_appended": prep_text.endswith(bench.QWEN_EOS),
            "sanitized_to_empty_marker": prep_text.find(EMPTY_SUBSTITUTE) >= 0 and row.get("sanitization_expected", False),
            "input_truncated": truncated,
            "prepared_chars": len(prep_text),
            "norm_native": round(float(np.linalg.norm(native)), FLOAT_DECIMALS),
            "norm_lane": round(float(np.linalg.norm(sliced)), FLOAT_DECIMALS),
            "vector_native": round_floats(native),
            "vector_lane_int8": [int(c) for c in codes],
            "lane_int8_scale": round(scale, 9),
            "batch_group_positions_checked": batch_checks.get(row["text_id"]),
            "generator": generator,
        })

    print(f"  {lane['key']}: {len(rows)} vectors, native {rows[0]['native_dims']}d, "
          f"lane {lane['lane_dims']}d int8, {elapsed:.1f}s")
    return rows


def run_batch_group_check(corpus: list[dict], prepared: list[str], srv) -> dict:
    """Embed batch-marker texts at every position of a full-size batch."""
    checks = {}
    for row, prep_text in zip(corpus, prepared):
        expand = row.get("batch_expand")
        if not expand:
            continue
        stacked = srv.embed([prep_text] * expand, batch=expand)
        first = l2(np.asarray(stacked[0], dtype=np.float32))
        worst = min(float(first @ l2(np.asarray(v, dtype=np.float32))) for v in stacked)
        if worst < COSINE_FLOOR:
            raise SystemExit(
                f"batch position invariance failed for {row['text_id']}: worst cosine {worst:.6f}")
        checks[row["text_id"]] = expand
    return checks


def write_golden(path: Path, rows: list[dict]) -> None:
    with path.open("w", encoding="utf-8") as fh:
        for row in rows:
            fh.write(json.dumps(row, ensure_ascii=False) + "\n")


def read_golden(path: Path) -> dict:
    if not path.is_file():
        raise SystemExit(f"missing committed golden {path}; run generate.py without --verify first")
    rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    return {r["text_id"]: r for r in rows}


def cosine(a, b) -> float:
    a, b = np.asarray(a, dtype=np.float64), np.asarray(b, dtype=np.float64)
    return float(a @ b / max(np.linalg.norm(a) * np.linalg.norm(b), 1e-12))


def check_row(fresh: dict, golden: dict) -> list[str]:
    failures = []
    tid = fresh["text_id"]

    if fresh["native_dims"] != golden["native_dims"]:
        failures.append(f"{tid}: native dims {fresh['native_dims']} != {golden['native_dims']}")
    if len(fresh["vector_native"]) != fresh["native_dims"]:
        failures.append(f"{tid}: native vector length {len(fresh['vector_native'])} != declared dims")
    if fresh["lane_dims"] != golden["lane_dims"] or len(fresh["vector_lane_int8"]) != golden["lane_dims"]:
        failures.append(f"{tid}: lane dims mismatch")
    if failures:
        return failures

    native_fresh = np.asarray(fresh["vector_native"], dtype=np.float64)
    native_norm = float(np.linalg.norm(native_fresh))
    if abs(native_norm - 1.0) > NORM_TOLERANCE:
        failures.append(f"{tid}: native L2 norm {native_norm:.6f} outside 1.0 +/- {NORM_TOLERANCE}")

    # The norm bar governs emitted FLOAT vectors. `vector_lane_int8` is a
    # storage encoding whose reconstruction norm legitimately drifts by ~1.5e-3
    # at 384/512 dims; its fidelity is bounded by the cosine bar below instead.
    lane_float = native_fresh[: fresh["lane_dims"]]
    lane_float = lane_float / max(float(np.linalg.norm(lane_float)), 1e-12)
    if abs(fresh["norm_lane"] - 1.0) > NORM_TOLERANCE:
        failures.append(f"{tid}: renormalized lane L2 norm {fresh['norm_lane']:.6f} "
                        f"outside 1.0 +/- {NORM_TOLERANCE}")

    lane_fresh = np.asarray(fresh["vector_lane_int8"], dtype=np.float64) * fresh["lane_int8_scale"]
    if max(abs(c) for c in fresh["vector_lane_int8"]) > 127:
        failures.append(f"{tid}: int8 code out of range")
    quant_cos = cosine(lane_fresh, lane_float)
    if quant_cos < COSINE_FLOOR:
        failures.append(f"{tid}: int8 quantization cosine {quant_cos:.6f} < {COSINE_FLOOR}")

    native_cos = cosine(fresh["vector_native"], golden["vector_native"])
    if native_cos < COSINE_FLOOR:
        failures.append(f"{tid}: native cosine {native_cos:.6f} < {COSINE_FLOOR}")

    lane_golden = np.asarray(golden["vector_lane_int8"], dtype=np.float64) * golden["lane_int8_scale"]
    lane_cos = cosine(lane_fresh, lane_golden)
    if lane_cos < COSINE_FLOOR:
        failures.append(f"{tid}: lane cosine {lane_cos:.6f} < {COSINE_FLOOR}")

    for field in ("role", "class", "storage_schema", "instruction_applied", "eos_appended", "input_truncated"):
        if fresh[field] != golden[field]:
            failures.append(f"{tid}: {field} {fresh[field]!r} != committed {golden[field]!r}")
    return failures


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--verify", action="store_true",
                    help="regenerate and assert the tolerance policy instead of overwriting goldens")
    ap.add_argument("--port", type=int, default=8478)
    ap.add_argument("--logdir", type=Path, default=HERE / ".scratch")
    args = ap.parse_args()

    server, pins = require_cache()
    bench = load_bench_module()
    args.logdir.mkdir(parents=True, exist_ok=True)

    probe_model = model_path(bench.candidate(pins, "bge-small-en-v1.5-f32"))
    backend_proof = prove_cpu_backend(server, probe_model, args.port + 1, args.logdir / "cpu-probe.log")
    print(f"backend proof: layers assigned to {backend_proof['devices_assigned']}")

    started = time.monotonic()
    failures, generated = [], {}
    for lane in LANES:
        rows = embed_lane(bench, lane, pins, server, args.port, args.logdir)
        generated[lane["key"]] = rows

        target = HERE / lane["golden"]
        if args.verify:
            golden = read_golden(target)
            if {r["text_id"] for r in rows} != set(golden):
                failures.append(f"{lane['key']}: corpus text_ids differ from committed golden")
                continue
            for row in rows:
                failures.extend(check_row(row, golden[row["text_id"]]))
        else:
            write_golden(target, rows)
            print(f"  wrote {target.name} ({target.stat().st_size / 1024:.0f} KiB)")

    elapsed = time.monotonic() - started
    if args.verify:
        checked = sum(len(r) for r in generated.values())
        if failures:
            print(f"\nCONFORMANCE FAIL: {len(failures)} violation(s) across {checked} vectors", file=sys.stderr)
            for line in failures[:40]:
                print(f"  {line}", file=sys.stderr)
            return 1
        print(f"\nCONFORMANCE PASS: {checked} vectors across {len(LANES)} models "
              f"(dims exact, |norm-1| <= {NORM_TOLERANCE}, cosine >= {COSINE_FLOOR}) in {elapsed:.1f}s")
    else:
        print(f"\ngenerated {sum(len(r) for r in generated.values())} vectors in {elapsed:.1f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
