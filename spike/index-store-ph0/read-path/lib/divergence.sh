#!/usr/bin/env bash
# Samples real task-branch divergence from this repo's git history.
# Emits a TSV: merge_sha, branch_files_changed, index_relevant_changed, percent_of_indexed_files
set -euo pipefail

REPO="${MILLER_PH0_REPO:-/Users/murphy/source/miller}"
INDEXED_FILE_COUNT="${1:-1417}"
SAMPLE="${2:-25}"

cd "$REPO"

printf 'merge_sha\tchanged_files\tindex_relevant\tpct_of_indexed\n'

for m in $(git log --merges -n "$SAMPLE" --format=%H); do
  p1=$(git rev-parse "$m^1" 2>/dev/null || true)
  p2=$(git rev-parse "$m^2" 2>/dev/null || true)
  [ -z "$p2" ] && continue
  base=$(git merge-base "$p1" "$p2")
  all=$(git diff --name-only "$base" "$p2" | wc -l | tr -d ' ')
  rel=$(git diff --name-only "$base" "$p2" \
        | grep -Ei '\.(cs|md|json|py|razor|ya?ml|sh|js|ps1|html|css)$' \
        | wc -l | tr -d ' ')
  pct=$(awk -v r="$rel" -v n="$INDEXED_FILE_COUNT" 'BEGIN{printf "%.3f", 100.0*r/n}')
  printf '%s\t%s\t%s\t%s\n' "$(git rev-parse --short "$m")" "$all" "$rel" "$pct"
done
