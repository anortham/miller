#!/usr/bin/env bash
#
# test.sh — the friendly front door to Miller's test suites.
#
# Miller splits tests into two suites to keep the dev loop fast (the lesson from julie, whose suite grew
# to 30+ minutes because slow integration tests ran on every change):
#
#   fast  — the default suite (Category!=Scale): pure logic + contract tests, no julie-extract subprocess.
#           Target <10s. This is what you run on every change. It is ALSO the bare `dotnet test` default
#           (the test csproj sets VSTestTestCaseFilter=Category!=Scale), so this wrapper just adds a
#           wall-clock budget tripwire on top.
#   scale — the Scale suite (Category=Scale): live tests that spawn the real pinned julie-extract or build
#           large fixtures. Slower; run before a commit/PR or when touching the indexing/extract path.
#           Skips (does not fail) if .tools/julie-extract is absent — run scripts/restore-julie-extract.sh.
#   all   — both suites.
#
# Usage:
#   scripts/test.sh            # fast suite (default)
#   scripts/test.sh fast       # fast suite, with a budget tripwire
#   scripts/test.sh scale      # scale suite only
#   scripts/test.sh all        # fast + scale
#   FAST_BUDGET_SECONDS=15 scripts/test.sh fast   # override the local budget ceiling
#
# Any extra args after the suite name are passed through to `dotnet test`
# (e.g. scripts/test.sh fast -v n).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
SOLUTION="${REPO_ROOT}/Miller.slnx"
CONFIG="${CONFIG:-Release}"

# The local fast-suite budget. Mirrors the CI ceiling (ci.yml) so a slow test is caught on the dev loop,
# not just in CI. The local target is <10s; this ceiling absorbs machine variance while still catching a
# runaway. Override with FAST_BUDGET_SECONDS.
FAST_BUDGET_SECONDS="${FAST_BUDGET_SECONDS:-30}"

SUITE="${1:-fast}"
shift || true   # drop the suite arg if present; remaining "$@" passes through to dotnet test

run_fast() {
  echo "==> building fast suite"
  dotnet build "${SOLUTION}" -c "${CONFIG}"

  echo "==> fast suite (Category!=Scale), budget ${FAST_BUDGET_SECONDS}s"
  local start elapsed
  start=$(date +%s)
  dotnet test "${SOLUTION}" -c "${CONFIG}" --no-build --no-restore --filter "Category!=Scale" "$@"
  elapsed=$(( $(date +%s) - start ))
  echo "    fast suite wall time: ${elapsed}s (local target <10s, ceiling ${FAST_BUDGET_SECONDS}s)"
  if [ "${elapsed}" -gt "${FAST_BUDGET_SECONDS}" ]; then
    echo "ERROR: fast suite took ${elapsed}s (> ${FAST_BUDGET_SECONDS}s ceiling)." >&2
    echo "       A slow test likely leaked into the default suite. Either speed it up or tag it" >&2
    echo "       [Trait(\"Category\",\"Scale\")] so it moves to the scale suite." >&2
    return 1
  fi
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
