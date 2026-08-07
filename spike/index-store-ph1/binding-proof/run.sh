#!/usr/bin/env bash
# Ph1 Task 1 entry point — binding-mechanism proof (G1-G5).
#
#   ./run.sh                # full proof, scratch removed on exit
#   KEEP_SCRATCH=1 ./run.sh # keep the scratch artifacts for inspection
#   ./run.sh pairs          # re-print the pair table without measuring
#
# Every artifact this builds lives under $TMPDIR and is removed on exit. Nothing
# is written outside this directory's output/. The julie-extractors checkout is
# read-only: its bytes come out through `git archive`.
set -euo pipefail

MODE="${1:-all}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../../.." && pwd)"
JULIE_EXTRACTORS="${JULIE_EXTRACTORS_REPO:-/Users/murphy/source/julie-extractors}"
JULIE="${MILLER_JULIE_EXTRACT:-$REPO_ROOT/.tools/julie-extract}"
[[ -x "$JULIE" ]] || JULIE="/Users/murphy/source/miller/.tools/julie-extract"
JOBS="${MILLER_EXTRACT_JOBS:-4}"
OUT="$HERE/output"

# The scratch dir is always freshly created by THIS invocation (mktemp -d), and the
# cleanup trap deletes only a directory carrying this run's ownership marker. A
# SCRATCH_DIR override must name a path that does not exist yet — a pre-existing
# directory is refused rather than adopted, so the trap can never delete data this
# run did not create. Unique dirs also let concurrent runs coexist.
if [[ -n "${SCRATCH_DIR:-}" ]]; then
  [[ -e "$SCRATCH_DIR" ]] && { echo "SCRATCH_DIR=$SCRATCH_DIR already exists; refusing to adopt (and later delete) a pre-existing path" >&2; exit 1; }
  mkdir -p "$SCRATCH_DIR"
  SCRATCH="$SCRATCH_DIR"
else
  SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/miller-ph1-binding-proof.XXXXXX")"
fi
MARKER="$SCRATCH/.miller-ph1-binding-proof-owned"
touch "$MARKER"

cleanup() {
  if [[ "${KEEP_SCRATCH:-0}" != "1" ]]; then
    [[ -f "$MARKER" ]] && rm -rf "$SCRATCH"
  else
    echo "scratch kept at $SCRATCH"
  fi
}
trap cleanup EXIT

[[ -x "$JULIE" ]] || { echo "missing pinned julie-extract at $JULIE" >&2; exit 1; }
[[ -d "$JULIE_EXTRACTORS/.git" ]] || { echo "missing julie-extractors at $JULIE_EXTRACTORS" >&2; exit 1; }
mkdir -p "$OUT" "$SCRATCH"

echo "julie-extract:    $("$JULIE" --version)   jobs=$JOBS (scans run SEQUENTIALLY)"
echo "miller:           $REPO_ROOT @ $(git -C "$REPO_ROOT" rev-parse --short HEAD) ($(git -C "$REPO_ROOT" branch --show-current))"
echo "julie-extractors: $JULIE_EXTRACTORS @ $(git -C "$JULIE_EXTRACTORS" rev-parse --short HEAD) (read-only, git archive)"
echo "scratch:          $SCRATCH"
echo

# Pairs picked from each repo's real merge history by the Ph0 method: for each
# merge commit, base = merge-base(p1, p2) and tip = p2, counted over indexed
# extensions only.
#
# Two families, because one pair cannot serve both jobs:
#
#   q_*      the divergence quantiles Ph0 measured (miller median 16 / p90 77,
#            julie-extractors median 28 / p90 369). Those merges are old, so their
#            trees are 17-60% of today's corpus. They answer "how big is a real
#            task branch's delta", not "what does it cost at fixture scale".
#   scale_*  merges whose base tree is near today's corpus, so the numbers are
#            comparable to the Ph0 anchors (miller fixture = 1,420 indexed files).
#            miller/scale_sibling43 is the exact pair Ph0's refuted bind measured
#            at 24,390 ms, so it is a direct like-for-like comparison.
#
# G3/G5 carry their verdict on the scale_* band only (bind.py G3_MIN_CORPUS_FILES);
# every pair's numbers are reported, and the all-pairs verdict is published beside it.
# The first pair of each corpus is the one G1's determinism probe builds twice, so
# a scale_* pair leads each list. miller/scale_deletes106 is the only merge in
# either repo's history that deletes an indexed path.
#
#   corpus:label:base:tip:merge:changed_indexed_files:added:deleted
PAIRS=(
  "miller:scale_sibling43:b0d96b75:425f995d:759a8d3a:43:12:0"
  "miller:scale_deletes106:a26cadfa:3a933e0b:11247e91:106:39:1"
  "miller:scale_nostruct4:a8c499c9:97f2b80d:d9b65e52:4:0:0"
  "miller:q_median16:09697a7e:2d06ae9a:75a877cb:16:2:0"
  "miller:q_p90_77:4f91191c:afbca712:9a4bb833:77:20:0"
  "julie-extractors:scale_23:058b166a:7b94810f:c4dd8c8f:23:6:0"
  "julie-extractors:scale_54:bfced7be:3d7f7c46:3992b03b:54:20:0"
  "julie-extractors:q_median28:300d1d92:b4ab3dcc:dc5bc515:28:14:0"
  "julie-extractors:q_p90_369:597bffc1:a75378c6:0fe1ea4e:369:82:0"
)

if [[ "$MODE" == "pairs" ]]; then
  printf '%s\n' "${PAIRS[@]}"
  exit 0
fi

PAIR_ARGS=()
for pair in "${PAIRS[@]}"; do PAIR_ARGS+=(--pair "$pair"); done

python3 "$HERE/bind.py" \
  --scratch "$SCRATCH" \
  --julie "$JULIE" \
  --jobs "$JOBS" \
  --out "$OUT" \
  --repo "miller=$REPO_ROOT" \
  --repo "julie-extractors=$JULIE_EXTRACTORS" \
  "${PAIR_ARGS[@]}" \
  --repeat-pair "miller:scale_sibling43"
