#!/usr/bin/env bash
set -euo pipefail

CONFIG="${CONFIG:-Release}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/spike/SearchProjection.Spike/SearchProjection.Spike.csproj"

if [[ "${1:-}" == "" || "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  cat <<USAGE
Usage:
  scripts/bench-search.sh <db-path> [spike args...]

Examples:
  scripts/bench-search.sh .miller/symbols.db
  scripts/bench-search.sh .miller/symbols.db --content-scope docs --repetitions 5

Environment:
  CONFIG=Release|Debug   Build configuration. Default: Release
USAGE
  exit 0
fi

DB_PATH="$1"
shift

dotnet run -c "$CONFIG" --project "$PROJECT" -- --db "$DB_PATH" "$@"
