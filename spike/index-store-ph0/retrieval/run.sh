#!/usr/bin/env bash
# Ph0 filtered-retrieval instrument: FTS equivalence, sqlite-vec pre-filtering,
# DocId/BM25 economics.
#
#   ./run.sh              run everything, delete the generated databases
#   ./run.sh --keep       keep work/ for inspection
#   ./run.sh fts          run one instrument (fts | vec | docid)
#
# Reads the live artifacts read-only. Writes only under work/, which is deleted
# on exit unless --keep is given. Peak scratch is about 2 GB.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$ROOT/work"
KEEP=0
TARGETS=()

for arg in "$@"; do
  case "$arg" in
    --keep) KEEP=1 ;;
    fts|vec|docid) TARGETS+=("$arg") ;;
    *) echo "unknown argument: $arg" >&2; exit 64 ;;
  esac
done
[ ${#TARGETS[@]} -eq 0 ] && TARGETS=(fts vec docid)

cleanup() {
  if [ "$KEEP" -eq 1 ]; then
    echo "# kept $WORK"
  else
    rm -rf "$WORK"
    echo "# removed $WORK"
  fi
}
trap cleanup EXIT

PYTHON="${PYTHON:-python3}"
"$PYTHON" - <<'PY'
import sqlite3, sys
con = sqlite3.connect(":memory:")
con.execute("CREATE VIRTUAL TABLE probe USING fts5(a, tokenize='trigram')")
if not hasattr(con, "enable_load_extension"):
    sys.exit("python sqlite3 was built without extension loading; cannot run the vector probe")
PY

SEARCH_DB="${MILLER_PH0_SEARCH_DB:-/Users/murphy/source/miller/.miller/search.db}"
VECTORS_DB="${MILLER_PH0_VECTORS_DB:-/Users/murphy/source/miller/.miller/vectors.db}"
VEC_EXT="${MILLER_SQLITE_VEC_PATH:-/Users/murphy/source/miller/.tools/vec0.dylib}"
[ -f "$SEARCH_DB" ] || { echo "missing search sidecar: $SEARCH_DB" >&2; exit 1; }

rm -rf "$WORK"
mkdir -p "$WORK"

echo "# python:      $("$PYTHON" -c 'import sys,sqlite3;print(sys.version.split()[0], "sqlite", sqlite3.sqlite_version)')"
echo "# search.db:   $SEARCH_DB ($(du -h "$SEARCH_DB" | cut -f1))"
echo "# vectors.db:  $VECTORS_DB"
echo "# sqlite-vec:  $VEC_EXT"
echo

for target in "${TARGETS[@]}"; do
  case "$target" in
    fts)   script=fts_equivalence.py ;;
    vec)   script=vec_prefilter.py ;;
    docid) script=docid_bm25.py ;;
  esac
  echo "===== $script ====="
  "$PYTHON" "$ROOT/$script" "$WORK" 2>&1 | tee "$WORK/${target}.log" | tail -6
  echo
done

echo "===== summary ====="
"$PYTHON" "$ROOT/summarize.py" "$WORK"
