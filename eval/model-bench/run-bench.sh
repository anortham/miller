#!/usr/bin/env bash
# Miller semantic-integration P0 model benchmark.
#
# download -> verify sha256 -> build corpus -> pooling sanity gate -> embed
# -> rank each lane -> score via the Task 6 retrieval-eval harness.
#
# Reproducible from a clean cache: every artifact is sha256-pinned in
# bench-pins.json and re-verified on every run. Bench tooling only — this is NOT
# the future julie-semantic-sidecar.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
CACHE="$HERE/.cache"
DIST="$CACHE/dist"
RUNS="$CACHE/runs"
PINS="$HERE/bench-pins.json"

QUERIES="$REPO_ROOT/eval/retrieval-eval/sets/dev/queries.jsonl"
CORPUS="$CACHE/corpus/corpus.jsonl"
MANIFEST="$CACHE/corpus/corpus-manifest.json"

MILLER_REPO="${MILLER_REPO:-/Users/murphy/source/miller}"
JULIE_REPO="${JULIE_REPO:-/Users/murphy/source/julie}"
MILLER_BIN="${MILLER_BIN:-$REPO_ROOT/src/Miller.Server/bin/Release/net10.0/miller}"

LLAMA_TAG="$(python3 -c "import json;print(json.load(open('$PINS'))['runtime']['release_tag'])")"
LLAMA_BIN="$CACHE/llama/llama-$LLAMA_TAG/llama-server"

mkdir -p "$DIST" "$RUNS"/{sanity,vecs,results,reports}

say() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }

# --- 1. download + verify ---------------------------------------------------

fetch() { # url sha256 filename
  local url="$1" want="$2" name="$3" dest="$DIST/$3"
  if [[ ! -f "$dest" ]]; then
    echo "  downloading $name"
    curl -fsSL -o "$dest.part" "$url"
    mv "$dest.part" "$dest"
  fi
  local got
  got="$(shasum -a 256 "$dest" | awk '{print $1}')"
  if [[ "$got" != "$want" ]]; then
    echo "  SHA MISMATCH for $name" >&2
    echo "    expected $want" >&2
    echo "    actual   $got" >&2
    exit 1
  fi
  echo "  ok $name ($got)"
}

say "1/6 download + verify pinned artifacts"
eval "$(python3 - "$PINS" <<'PY'
import json, shlex, sys
p = json.load(open(sys.argv[1]))
r = p["runtime"]
print(f"fetch {shlex.quote(r['url'])} {r['sha256']} {shlex.quote(r['asset'])}")
for c in p["candidates"]:
    print(f"fetch {shlex.quote(c['url'])} {c['sha256']} {shlex.quote(c['file'])}")
PY
)"

if [[ ! -x "$LLAMA_BIN" ]]; then
  say "extracting llama.cpp $LLAMA_TAG"
  mkdir -p "$CACHE/llama"
  tar xzf "$DIST/$(python3 -c "import json;print(json.load(open('$PINS'))['runtime']['asset'])")" -C "$CACHE/llama"
fi
[[ -x "$LLAMA_BIN" ]] || { echo "llama-server not found at $LLAMA_BIN" >&2; exit 1; }

# --- 2. corpus --------------------------------------------------------------

say "2/6 build corpus (excludes eval/, .razorback/, .claude/ — golden-set trap)"
python3 "$HERE/build_corpus.py" \
  --repo "miller=$MILLER_REPO" --repo "julie=$JULIE_REPO" \
  --out "$CORPUS" --manifest "$MANIFEST"

# --- 3. BM25 baseline -------------------------------------------------------

say "3/6 BM25 baseline arms (Miller's real lexical search)"
if [[ -x "$MILLER_BIN" ]]; then
  for mode in symbol auto; do
    python3 "$HERE/bm25_baseline.py" --miller "$MILLER_BIN" --queries "$QUERIES" \
      --repo "miller=$MILLER_REPO" --repo "julie=$JULIE_REPO" \
      --out "$RUNS/results/bm25-$mode-topk.jsonl" --mode "$mode" --ratio 0
    # Relative-band variant so negatives compare against the semantic `thr`
    # arms at a matched policy shape (BM25 scores are unbounded, so only a
    # relative band is meaningful — an absolute floor would be arbitrary).
    python3 "$HERE/bm25_baseline.py" --miller "$MILLER_BIN" --queries "$QUERIES" \
      --repo "miller=$MILLER_REPO" --repo "julie=$JULIE_REPO" \
      --out "$RUNS/results/bm25-$mode-thr.jsonl" --mode "$mode" --ratio 0.85
  done
else
  echo "  SKIP: miller binary not built at $MILLER_BIN (dotnet build Miller.slnx -c Release)" >&2
fi

# --- 4/5. per-candidate sanity gate, embed, rank ----------------------------

lanes_for() { # candidate_id -> "dims:quant" lines
  python3 - "$PINS" "$1" <<'PY'
import json, sys
p = json.load(open(sys.argv[1]))
c = next(c for c in p["candidates"] if c["id"] == sys.argv[2])
for d in c["mrl_lanes"]:
    print(f"{d}:f32")
    print(f"{d}:int8")
PY
}

CANDIDATES="${CANDIDATES:-$(python3 -c "import json;print(' '.join(c['id'] for c in json.load(open('$PINS'))['candidates']))")}"

for cid in $CANDIDATES; do
  model_file="$(python3 -c "import json;print(next(c['file'] for c in json.load(open('$PINS'))['candidates'] if c['id']=='$cid'))")"
  model="$DIST/$model_file"

  say "4/6 pooling sanity gate: $cid"
  if ! python3 "$HERE/bench.py" sanity --pins "$PINS" --candidate "$cid" \
        --binary "$LLAMA_BIN" --model "$model" --out "$RUNS/sanity/$cid.json"; then
    echo "  SANITY FAILED for $cid — candidate dropped, not scored." >&2
    continue
  fi

  say "5/6 embed corpus + queries: $cid"
  if [[ ! -f "$RUNS/vecs/$cid.corpus.npy" ]]; then
    python3 "$HERE/bench.py" embed --pins "$PINS" --candidate "$cid" \
      --binary "$LLAMA_BIN" --model "$model" \
      --corpus "$CORPUS" --queries "$QUERIES" --outdir "$RUNS/vecs" --batch "${BATCH:-16}"
  else
    echo "  cached: $RUNS/vecs/$cid.corpus.npy"
  fi

  # Two threshold policies per lane, because they answer different questions:
  #   topk — raw top-k, matching the unthresholded BM25 baseline. This is the
  #          apples-to-apples arm for recall@10 / nDCG@10. A thresholded arm
  #          emits ~1.5 docs and caps recall by construction, so comparing it
  #          against a top-10 baseline would understate the model.
  #   thr  — floor + relative band. This is the arm that can actually pass a
  #          negative query, and the one that models shipped precision.
  while IFS=: read -r dims quant; do
    base="$cid-${dims}d-$quant"
    python3 "$HERE/bench.py" rank --pins "$PINS" --candidate "$cid" \
      --corpus "$CORPUS" --queries "$QUERIES" --vecdir "$RUNS/vecs" \
      --out "$RUNS/results/$base-topk.jsonl" --dims "$dims" --quant "$quant" \
      --k "${K:-10}" --floor -1 --ratio 0
    python3 "$HERE/bench.py" rank --pins "$PINS" --candidate "$cid" \
      --corpus "$CORPUS" --queries "$QUERIES" --vecdir "$RUNS/vecs" \
      --out "$RUNS/results/$base-thr.jsonl" --dims "$dims" --quant "$quant" \
      --k "${K:-10}" --floor "${FLOOR:-0.35}" --ratio "${RATIO:-0.85}"
  done < <(lanes_for "$cid")
done

# --- 6. score every arm -----------------------------------------------------

say "6/6 score all arms via the retrieval-eval harness"
for results in "$RUNS/results"/*.jsonl; do
  arm="$(basename "$results" .jsonl)"
  echo "--- $arm"
  dotnet run --project "$REPO_ROOT/eval/retrieval-eval" -c Release -- score \
    --queries "$QUERIES" --results "$results" \
    --out "$RUNS/reports/$arm.json" \
    --corpus "miller=$MILLER_REPO" --corpus "julie=$JULIE_REPO" --k "${K:-10}"
done

say "done — reports in $RUNS/reports"
python3 "$HERE/summarize.py" --reports "$RUNS/reports" --out "$RUNS/summary.md" || true
