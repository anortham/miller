#!/usr/bin/env bash
# Ph0 Task 5 entry point — resolution binding cost curve + store growth model.
#
#   ./run.sh            # binding curve then growth model, scratch cleaned up
#   ./run.sh binding
#   ./run.sh growth     # reuses output/binding-results.json
#   KEEP_SCRATCH=1 ./run.sh binding
#
# Scan artifacts and the fixture copy live under $TMPDIR and are removed on exit.
# Nothing is written outside this directory's output/ and $SCRATCH.
set -euo pipefail

PART="${1:-all}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../../.." && pwd)"
REPO_REV="${MILLER_REV:-$(git -C "$REPO_ROOT" rev-parse HEAD)}"
JULIE="$REPO_ROOT/.tools/julie-extract"
JOBS="${MILLER_EXTRACT_JOBS:-4}"
SCRATCH="${SCRATCH_DIR:-${TMPDIR:-/tmp}/miller-ph0-task5}"
OUT="$HERE/output"
JULIE_EXTRACTORS="${JULIE_EXTRACTORS_REPO:-/Users/murphy/source/julie-extractors}"

cleanup() {
  if [[ "${KEEP_SCRATCH:-0}" != "1" ]]; then
    rm -rf "$SCRATCH"
  else
    echo "scratch kept at $SCRATCH"
  fi
}
trap cleanup EXIT

[[ -x "$JULIE" ]] || { echo "missing pinned julie-extract at $JULIE" >&2; exit 1; }
mkdir -p "$OUT"

echo "julie-extract: $("$JULIE" --version)   jobs=$JOBS"
echo "repo:          $REPO_ROOT (rev ${REPO_REV:0:8}, branch $(git -C "$REPO_ROOT" branch --show-current))"
echo "scratch:       $SCRATCH"
echo

if [[ "$PART" == "all" || "$PART" == "binding" ]]; then
  rm -rf "$SCRATCH"
  mkdir -p "$SCRATCH/fixture"
  git -C "$REPO_ROOT" archive "$REPO_REV" | tar -x -C "$SCRATCH/fixture"
  cp -c -R "$SCRATCH/fixture" "$SCRATCH/pristine" 2>/dev/null || cp -R "$SCRATCH/fixture" "$SCRATCH/pristine"
  echo "fixture: $(find "$SCRATCH/fixture" -type f | wc -l | tr -d ' ') tracked files from ${REPO_REV:0:8}"

  # Second corpus for a repo-specific bytes-per-version. `git archive` keeps the
  # probe read-only on julie-extractors and reproducible from its HEAD.
  EXTRA_ARGS=()
  if [[ -d "$JULIE_EXTRACTORS/.git" ]]; then
    mkdir -p "$SCRATCH/fixture-julie-extractors"
    git -C "$JULIE_EXTRACTORS" archive HEAD | tar -x -C "$SCRATCH/fixture-julie-extractors"
    echo "julie-extractors fixture: $(find "$SCRATCH/fixture-julie-extractors" -type f | wc -l | tr -d ' ') files from $(git -C "$JULIE_EXTRACTORS" rev-parse --short HEAD)"
    EXTRA_ARGS=(--extra-root "julie-extractors=$SCRATCH/fixture-julie-extractors")
  fi

  # Real sibling-branch pair: the newest merge reachable from $REPO_REV. Its
  # merge-base is the base view's tree and its second parent is the new view's.
  SIB_ARGS=()
  read -r SIB_P1 SIB_TIP < <(git -C "$REPO_ROOT" log --merges -n 1 --format='%P' "$REPO_REV")
  if [[ -n "${SIB_TIP:-}" ]]; then
    SIB_BASE="$(git -C "$REPO_ROOT" merge-base "$SIB_P1" "$SIB_TIP")"
    echo "sibling pair: ${SIB_BASE:0:8} -> ${SIB_TIP:0:8} ($(git -C "$REPO_ROOT" diff --name-only --no-renames "$SIB_BASE" "$SIB_TIP" | wc -l | tr -d ' ') paths)"
    SIB_ARGS=(--repo-path "$REPO_ROOT" --sibling-base "$SIB_BASE" --sibling-tip "$SIB_TIP")
  fi

  python3 "$HERE/binding.py" --scratch "$SCRATCH" --julie "$JULIE" --jobs "$JOBS" --out "$OUT" \
    --repo-head "$REPO_REV" "${EXTRA_ARGS[@]}" "${SIB_ARGS[@]}"
fi

if [[ "$PART" == "all" || "$PART" == "growth" ]]; then
  [[ -f "$OUT/binding-results.json" ]] || { echo "run './run.sh binding' first" >&2; exit 1; }
  python3 "$HERE/growth.py" \
    --repo "miller=$REPO_ROOT" \
    --rev "miller=$REPO_REV" \
    --repo "julie-extractors=$JULIE_EXTRACTORS" \
    --binding-results "$OUT/binding-results.json" \
    --julie "$JULIE" \
    --out "$OUT/growth-results.json"
fi
