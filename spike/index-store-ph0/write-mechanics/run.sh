#!/usr/bin/env bash
# Entry point for the Ph0 write-side mechanics instrument.
#
#   ./run.sh                 full scale (~2 GB per store, ~25 min)
#   ./run.sh --scale quick   smoke scale (~100 MB per store, ~2 min)
#   ./run.sh --only gc       one experiment: probes | gc | granularity | promotion
#   ./run.sh --keep          keep the generated databases for inspection
#   ./run.sh --out DIR       write results somewhere other than ./out
#
# Every generated database lives under a temporary work directory and is
# deleted on exit. Only out/*.json, out/*.txt and out/summary.md survive.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$HERE/out"
SCALE=full
KEEP=0
ONLY=all

while [[ $# -gt 0 ]]; do
  case "$1" in
    --scale) SCALE="$2"; shift 2 ;;
    --out)   OUT="$2";   shift 2 ;;
    --only)  ONLY="$2";  shift 2 ;;
    --keep)  KEEP=1;     shift ;;
    -h|--help) sed -n '2,12p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

case "$SCALE" in
  full)  GC_VERSIONS=6100; GC_GENERATIONS=5
         GRAN_VERSIONS=2000; GRAN_TRIALS=3
         PROMO_VERSIONS=2500; PROMO_GENERATIONS=5
         MIN_FREE_GB=25 ;;
  quick) GC_VERSIONS=300;  GC_GENERATIONS=5
         GRAN_VERSIONS=300;  GRAN_TRIALS=3
         PROMO_VERSIONS=300;  PROMO_GENERATIONS=5
         MIN_FREE_GB=4 ;;
  *) echo "unknown scale: $SCALE (use full or quick)" >&2; exit 2 ;;
esac

WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/miller-ph0-write-mechanics.XXXXXX")"

cleanup() {
  if [[ "$KEEP" == "1" ]]; then
    echo "[run] keeping work directory: $WORKDIR"
  else
    rm -rf "$WORKDIR"
    echo "[run] removed work directory: $WORKDIR"
  fi
}
trap cleanup EXIT INT TERM

mkdir -p "$OUT"

FREE_GB="$(df -g "$WORKDIR" | awk 'NR==2 {print $4}')"
if [[ "$FREE_GB" -lt "$MIN_FREE_GB" ]]; then
  echo "[run] need ${MIN_FREE_GB} GB free under ${TMPDIR:-/tmp}, found ${FREE_GB} GB" >&2
  exit 1
fi

{
  echo "[run] scale=$SCALE workdir=$WORKDIR free=${FREE_GB}GB"
  echo "[run] sqlite3 CLI: $(sqlite3 --version)"
  echo "[run] python: $(python3 -V) sqlite lib $(python3 -c 'import sqlite3;print(sqlite3.sqlite_version)')"
  echo "[run] host: $(uname -srm)"
  echo "[run] started: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
} | tee "$OUT/run-log.txt"

run_stage() {
  local name="$1"; shift
  echo "[run] === $name ===" | tee -a "$OUT/run-log.txt"
  local started=$SECONDS
  "$@" 2>&1 | tee -a "$OUT/run-log.txt"
  echo "[run] $name finished in $((SECONDS - started))s" | tee -a "$OUT/run-log.txt"
}

# The live artifact is served by a running Miller indexer, so a read-only open can
# hit a transient SQLITE_BUSY. Sampling is evidence, not a dependency of the
# experiments: retry, then fall back to the committed row-shapes.txt.
sampled=0
for attempt in 1 2 3; do
  if "$HERE/sample_row_shapes.sh" "$OUT/row-shapes.txt.new" >>"$OUT/run-log.txt" 2>&1; then
    mv "$OUT/row-shapes.txt.new" "$OUT/row-shapes.txt"
    sampled=1
    break
  fi
  echo "[run] row-shape sampling attempt $attempt hit a busy artifact" | tee -a "$OUT/run-log.txt"
  sleep 5
done
rm -f "$OUT/row-shapes.txt.new"
if [[ "$sampled" == "0" ]]; then
  if [[ -f "$OUT/row-shapes.txt" ]]; then
    echo "[run] artifact busy; reusing the committed $OUT/row-shapes.txt" | tee -a "$OUT/run-log.txt"
  else
    echo "[run] artifact busy and no committed row-shapes.txt; aborting" >&2
    exit 1
  fi
fi

if [[ "$ONLY" == "all" || "$ONLY" == "probes" || "$ONLY" == "gc" ]]; then
  run_stage probes python3 "$HERE/pragma_probes.py" "$WORKDIR" "$OUT"
fi

if [[ "$ONLY" == "all" || "$ONLY" == "gc" ]]; then
  run_stage gc python3 "$HERE/gc_experiment.py" \
    "$WORKDIR" "$OUT" "$GC_VERSIONS" "$GC_GENERATIONS"
fi

if [[ "$ONLY" == "all" || "$ONLY" == "granularity" ]]; then
  run_stage granularity python3 "$HERE/granularity_experiment.py" \
    "$WORKDIR" "$OUT" "$GRAN_VERSIONS" "$GRAN_TRIALS"
fi

if [[ "$ONLY" == "all" || "$ONLY" == "promotion" ]]; then
  run_stage promotion python3 "$HERE/promotion_experiment.py" \
    "$WORKDIR" "$OUT" "$PROMO_VERSIONS" "$PROMO_GENERATIONS"
fi

python3 "$HERE/summarize.py" "$OUT" > "$OUT/summary.md"
echo "[run] wrote $OUT/summary.md" | tee -a "$OUT/run-log.txt"
echo "[run] finished: $(date -u +%Y-%m-%dT%H:%M:%SZ)" | tee -a "$OUT/run-log.txt"

REMAINING="$(find "$WORKDIR" -type f 2>/dev/null | wc -l | tr -d ' ')"
echo "[run] files left in work dir before cleanup: $REMAINING" | tee -a "$OUT/run-log.txt"
