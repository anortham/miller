#!/usr/bin/env bash
#
# install-hooks.sh — point git at the repo's tracked hooks directory (.githooks/).
#
# Git does not version .git/hooks, so the repo ships its hooks under .githooks/ and this one-time command
# tells git to use them. Run once after cloning. Currently the only hook keeps AGENTS.md in sync with
# CLAUDE.md (see .githooks/pre-commit + scripts/sync-agents.sh).
#
# Usage: scripts/install-hooks.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${REPO_ROOT}"

git config core.hooksPath .githooks
chmod +x .githooks/* 2>/dev/null || true
echo "git hooks installed: core.hooksPath -> .githooks"
