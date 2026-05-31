#!/usr/bin/env bash
#
# sync-agents.sh — regenerate AGENTS.md as a byte-for-byte mirror of CLAUDE.md.
#
# CLAUDE.md is the single source of truth. AGENTS.md exists so tools that read the open AGENTS.md convention
# get the same project guidance. The pre-commit hook (.githooks/pre-commit) fails the commit if the two
# diverge; run this to fix that.
#
# Usage: scripts/sync-agents.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="${REPO_ROOT}/CLAUDE.md"
DST="${REPO_ROOT}/AGENTS.md"

if [[ ! -f "${SRC}" ]]; then
  echo "error: source ${SRC} not found" >&2
  exit 1
fi

cp "${SRC}" "${DST}"
echo "AGENTS.md regenerated from CLAUDE.md."
