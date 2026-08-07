#!/usr/bin/env bash
# Ph0 Task 3 entry script: read-path + physical-byte instrument for the versioned index store.
#
#   ./run.sh                 build, measure, summarize, delete every generated database
#   MILLER_PH0_KEEP=1 ./run.sh   keep the scratch databases for poking at afterwards
#
# Evidence lands in out/*.json and out/summary.md. Scratch databases live under $TMPDIR and are
# removed on exit (peak ~2.1 GB).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$HERE/out"
SCRATCH="${MILLER_PH0_SCRATCH:-${TMPDIR:-/tmp}/miller-ph0-readpath}"
SOURCE_DB="${MILLER_PH0_SOURCE:-file:/Users/murphy/source/miller/.miller/symbols.db?mode=ro}"
KEYS="${MILLER_PH0_KEYS:-300}"
PASSES="${MILLER_PH0_PASSES:-15}"
PY="${PYTHON:-python3}"

cleanup() {
  if [ "${MILLER_PH0_KEEP:-0}" = "1" ]; then
    echo "== keeping scratch databases in $SCRATCH"
  else
    rm -rf "$SCRATCH"
    echo "== removed scratch databases"
  fi
}
trap cleanup EXIT

mkdir -p "$OUT" "$SCRATCH"
export MILLER_PH0_SOURCE="$SOURCE_DB"

step() { echo; echo "== $*"; }

step "environment"
{
  echo "date_utc: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "host: $(uname -srm)"
  echo "worktree: $(cd "$HERE" && git rev-parse --show-toplevel)"
  echo "commit: $(cd "$HERE" && git rev-parse --short HEAD)"
  echo "branch: $(cd "$HERE" && git branch --show-current)"
  echo "python: $($PY -c 'import sys,sqlite3;print(sys.version.split()[0], "sqlite", sqlite3.sqlite_version)')"
  echo "source_db: $SOURCE_DB"
  echo "scratch: $SCRATCH"
} | tee "$OUT/environment.txt"

step "1. sample real task-branch divergence from git history"
bash "$HERE/lib/divergence.sh" 1417 25 | tee "$OUT/divergence.tsv"
DIVERGENCES="$($PY - "$OUT/divergence.tsv" <<'EOF'
import sys
rows = [float(l.split("\t")[3]) for l in open(sys.argv[1]).read().splitlines()[1:]]
rows.sort()
# Seven sibling worktrees drawn at even quantiles of the observed distribution.
picks = [rows[min(len(rows) - 1, round((i / 8.0) * len(rows)))] for i in range(1, 8)]
print(",".join(f"{p:.3f}" for p in picks))
EOF
)"
P90="$($PY - "$OUT/divergence.tsv" <<'EOF'
import sys
rows = [float(l.split("\t")[3]) for l in open(sys.argv[1]).read().splitlines()[1:]]
rows.sort()
p = rows[int(len(rows) * 0.9)]
print(",".join([f"{p:.3f}"] * 7))
EOF
)"
echo "sampled divergences (views 2-8): $DIVERGENCES" | tee "$OUT/divergence-picks.txt"
echo "stress divergences (p90 everywhere): $P90" | tee -a "$OUT/divergence-picks.txt"

step "2. build the three key-shape variants (single view of data)"
$PY "$HERE/lib/instrument.py" build-single "$SCRATCH/single.db"
$PY "$HERE/lib/instrument.py" build-keepfile "$SCRATCH/keepfile.db"
$PY "$HERE/lib/instrument.py" build-v4 "$SCRATCH/v4single.db"
$PY "$HERE/lib/instrument.py" bytes "$SCRATCH/single.db" --label single > "$OUT/bytes-single.json"
$PY "$HERE/lib/instrument.py" bytes "$SCRATCH/keepfile.db" --label keepfile > "$OUT/bytes-keepfile.json"
$PY "$HERE/lib/instrument.py" bytes "$SCRATCH/v4single.db" --label v4single > "$OUT/bytes-v4single.json"
rm -f "$SCRATCH/keepfile.db"*

step "3. build the 8-view family store at sampled divergence"
$PY "$HERE/lib/instrument.py" build-store "$SCRATCH/store.db" --divergences "$DIVERGENCES" \
  > "$OUT/store-build.json"
$PY "$HERE/lib/instrument.py" bytes "$SCRATCH/store.db" --label store > "$OUT/bytes-store.json"

step "4. measure representative reads (base view and most-diverged view)"
$PY "$HERE/lib/instrument.py" plans --store "$SCRATCH/store.db" --baseline "$SCRATCH/single.db" \
  --v4single "$SCRATCH/v4single.db" --view 1 > "$OUT/query-plans.json"
for v in 1 8; do
  $PY "$HERE/lib/instrument.py" verify --store "$SCRATCH/store.db" \
    --baseline "$SCRATCH/single.db" --v4single "$SCRATCH/v4single.db" \
    --view "$v" --keys "$KEYS" > "$OUT/verify-view$v.json"
  $PY -c "import json,sys; d=json.load(open('$OUT/verify-view$v.json')); \
print('view $v result-set equivalence:', d['verdict'], \
{k: v['rows_compared'] for k, v in d['classes'].items()}); \
sys.exit(0 if d['verdict'] == 'IDENTICAL' else 1)"
done
$PY "$HERE/lib/instrument.py" measure --store "$SCRATCH/store.db" \
  --baseline "$SCRATCH/single.db" --v4single "$SCRATCH/v4single.db" \
  --view 1 --keys "$KEYS" --passes "$PASSES" --label view1 > "$OUT/reads-view1.json"
$PY "$HERE/lib/instrument.py" measure --store "$SCRATCH/store.db" \
  --baseline "$SCRATCH/single.db" --v4single "$SCRATCH/v4single.db" \
  --view 8 --keys "$KEYS" --passes "$PASSES" --label view8 > "$OUT/reads-view8.json"

step "5. materialize view 8 as a dedicated per-worktree copy (the denominator, physically)"
$PY "$HERE/lib/instrument.py" build-dedicated-view "$SCRATCH/dedicated8.db" \
  --store "$SCRATCH/store.db" --view 8 > "$OUT/dedicated-view8-build.json"
$PY "$HERE/lib/instrument.py" bytes "$SCRATCH/dedicated8.db" --label dedicated_view8 \
  > "$OUT/bytes-dedicated-view8.json"
rm -f "$SCRATCH/dedicated8.db"*

step "6. stress configuration: every view diverged at the p90 of the sampled history"
$PY "$HERE/lib/instrument.py" build-store "$SCRATCH/stress.db" --divergences "$P90" \
  > "$OUT/store-build-stress.json"
$PY "$HERE/lib/instrument.py" bytes "$SCRATCH/stress.db" --label store_stress \
  > "$OUT/bytes-store-stress.json"
rm -f "$SCRATCH/stress.db"*

step "7. retention sensitivity: two retained history generations, invisible to every view"
$PY "$HERE/lib/instrument.py" inflate "$SCRATCH/store.db" --generations 2 \
  > "$OUT/store-inflate.json"
$PY "$HERE/lib/instrument.py" bytes "$SCRATCH/store.db" --label store_inflated \
  > "$OUT/bytes-store-inflated.json"
$PY "$HERE/lib/instrument.py" measure --store "$SCRATCH/store.db" \
  --baseline "$SCRATCH/single.db" --v4single "$SCRATCH/v4single.db" \
  --view 1 --keys "$KEYS" --passes "$PASSES" --label inflated > "$OUT/reads-inflated.json"

step "8. summary"
$PY "$HERE/lib/summarize.py" "$OUT" | tee "$OUT/summary.md"

echo
echo "== done; evidence in $OUT"
