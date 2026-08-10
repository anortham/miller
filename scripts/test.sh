#!/usr/bin/env bash
#
# test.sh — the friendly front door to Miller's test suites.
#
# Miller splits tests into two suites to keep the dev loop fast (the lesson from julie, whose suite grew
# to 30+ minutes because slow integration tests ran on every change):
#
#   fast  — the default suite (Category!=Scale): pure logic + contract tests, no julie-extract subprocess.
#           This is what you run on every change. It is ALSO the bare `dotnet test` default (the test csproj
#           sets VSTestTestCaseFilter=Category!=Scale). The wrapper reports local wall time but never fails
#           on elapsed time.
#   scale — the Scale suite (Category=Scale): live tests that spawn the real pinned julie-extract or build
#           large fixtures. Slower; run before a commit/PR or when touching the indexing/extract path.
#           Skips (does not fail) if .tools/julie-extract is absent — run scripts/restore-julie-extract.sh.
#   all   — both suites.
#
# Usage:
#   scripts/test.sh            # fast suite (default)
#   scripts/test.sh fast       # fast suite, with report-only local timing
#   scripts/test.sh scale      # scale suite only
#   scripts/test.sh all        # fast + scale
#
# Any extra args after the suite name are passed through to `dotnet test`
# (e.g. scripts/test.sh fast -v n).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
SOLUTION="${REPO_ROOT}/Miller.slnx"
CONFIG="${CONFIG:-Release}"

SUITE="${1:-fast}"
shift || true   # drop the suite arg if present; remaining "$@" passes through to dotnet test

run_fast() {
  echo "==> building fast suite"
  dotnet build "${SOLUTION}" -c "${CONFIG}"

  echo "==> fast suite (Category!=Scale)"
  local start elapsed
  start=$(date +%s)
  dotnet test "${SOLUTION}" -c "${CONFIG}" --no-build --no-restore --filter "Category!=Scale" "$@"
  elapsed=$(( $(date +%s) - start ))
  echo "    fast suite wall time: ${elapsed}s (report-only; compare repeated runs on the same local machine)"
}

run_scale() {
  echo "==> scale suite (Category=Scale) — spawns the real julie-extract"
  if [ ! -x "${REPO_ROOT}/.tools/julie-extract" ] && [ ! -f "${REPO_ROOT}/.tools/julie-extract.exe" ]; then
    echo "    note: .tools/julie-extract not found — these tests will SKIP (not fail)."
    echo "    run scripts/restore-julie-extract.sh to enable them."
  fi
  dotnet test "${SOLUTION}" -c "${CONFIG}" --filter "Category=Scale" "$@"
}

case "${SUITE}" in
  fast)  run_fast "$@" ;;
  scale) run_scale "$@" ;;
  all)
    run_fast "$@"
    run_scale "$@"
    ;;
  -h|--help|help)
    sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    ;;
  *)
    echo "error: unknown suite '${SUITE}'. Use one of: fast | scale | all (see --help)." >&2
    exit 2
    ;;
esac
