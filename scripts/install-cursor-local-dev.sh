#!/usr/bin/env bash
# Configure Cursor to run Miller from this checkout via user-global MCP config.
#
# Miller binds workspace roots from MCP client roots on the first tool call, so a
# single ~/.cursor/mcp.json entry works per editor window (see
# docs/plans/2026-06-25-mcp-roots-workspace-binding-design.md).
#
# - Writes ~/.cursor/mcp.json with the Release build path.
# - Retires any legacy ~/.cursor/plugins/local/miller copy to a backup dir
#   (local plugin installs start from empty/global windows and produce
#   duplicate plugin-miller-miller rows).
#
# Re-run after `dotnet build -c Release` to refresh the binary path.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CURSOR_MCP_JSON="${HOME}/.cursor/mcp.json"
LEGACY_PLUGIN="${HOME}/.cursor/plugins/local/miller"
MILLER_BINARY="${REPO_ROOT}/src/Miller.Server/bin/Release/net10.0/miller"

echo "Building Miller Release..."
dotnet build "${REPO_ROOT}/Miller.slnx" -c Release

if [[ ! -x "${MILLER_BINARY}" ]]; then
  echo "Missing Miller binary: ${MILLER_BINARY}" >&2
  exit 1
fi

echo "Writing Cursor MCP config to ${CURSOR_MCP_JSON}..."
mkdir -p "${HOME}/.cursor"
node - "${CURSOR_MCP_JSON}" "${MILLER_BINARY}" <<'EOF'
const fs = require('node:fs');

const mcpJsonPath = process.argv[2];
const millerBinary = process.argv[3];

let config = {};
if (fs.existsSync(mcpJsonPath)) {
  try {
    config = JSON.parse(fs.readFileSync(mcpJsonPath, 'utf8'));
  } catch {
    config = {};
  }
}

config.mcpServers ??= {};
config.mcpServers.miller = {
  type: 'stdio',
  command: millerBinary,
  args: ['serve'],
};

fs.writeFileSync(mcpJsonPath, `${JSON.stringify(config, null, 2)}\n`);
EOF

if [[ -d "${LEGACY_PLUGIN}" ]]; then
  backup="${HOME}/.cursor/plugin-backups/miller-local-$(date +%Y%m%d-%H%M%S)"
  echo "Retiring legacy Cursor local plugin to ${backup}..."
  mkdir -p "${HOME}/.cursor/plugin-backups"
  mv "${LEGACY_PLUGIN}" "${backup}"
fi

echo
echo "Cursor Miller MCP config installed."
echo "  config:   ${CURSOR_MCP_JSON}"
echo "  binary:   ${MILLER_BINARY}"
"${MILLER_BINARY}" version
echo
echo "Reload Cursor (Developer: Reload Window) to restart the Miller MCP server."
