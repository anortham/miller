#!/usr/bin/env bash
# Configure Cursor to run Miller from this checkout via the global MCP config.
#
# Implements the recommended Cursor path (README "Cursor global MCP install",
# docs/findings/2026-06-08-cursor-plugin-relative-launcher-root-cause.md) with a
# local-dev twist: MILLER_BINARY points at this checkout's Release build, so a
# `dotnet build -c Release` updates the server Cursor runs without reinstalling.
#
# - Copies the plugin launcher + manifest to a standalone root under
#   ~/.miller/plugin-cache/cursor-global-miller (NOT the checkout — an
#   empty-window launch must fail closed, never index the Miller repo).
# - Merges a `miller` entry into ~/.cursor/mcp.json, preserving other servers.
# - Retires any legacy ~/.cursor/plugins/local/miller copy to a backup dir
#   (local plugin installs start from empty/global windows and produce
#   duplicate plugin-miller-miller rows).
#
# Re-run after changing bin/miller-plugin-launcher.cjs to refresh the snapshot.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LAUNCHER_ROOT="${HOME}/.miller/plugin-cache/cursor-global-miller"
CURSOR_MCP_JSON="${HOME}/.cursor/mcp.json"
LEGACY_PLUGIN="${HOME}/.cursor/plugins/local/miller"
MILLER_BINARY="${REPO_ROOT}/src/Miller.Server/bin/Release/net10.0/miller"

echo "Building Miller Release..."
dotnet build "${REPO_ROOT}/Miller.slnx" -c Release

if [[ ! -x "${MILLER_BINARY}" ]]; then
  echo "Missing Miller binary: ${MILLER_BINARY}" >&2
  exit 1
fi

echo "Snapshotting launcher to ${LAUNCHER_ROOT}..."
mkdir -p "${LAUNCHER_ROOT}/bin" "${HOME}/.cursor"
cp "${REPO_ROOT}/bin/miller-plugin-launcher.cjs" "${LAUNCHER_ROOT}/bin/"
cp "${REPO_ROOT}/miller-plugin.json" "${LAUNCHER_ROOT}/"

echo "Merging miller server into ${CURSOR_MCP_JSON}..."
node - "${CURSOR_MCP_JSON}" "${MILLER_BINARY}" <<'EOF'
const fs = require('node:fs');

const mcpJsonPath = process.argv[2];
const millerBinary = process.argv[3];

let config = {};
if (fs.existsSync(mcpJsonPath)) {
  config = JSON.parse(fs.readFileSync(mcpJsonPath, 'utf8'));
}

config.mcpServers ??= {};
config.mcpServers.miller = {
  type: 'stdio',
  command: 'node',
  args: ['${userHome}/.miller/plugin-cache/cursor-global-miller/bin/miller-plugin-launcher.cjs'],
  env: {
    MILLER_WORKSPACE_ROOT: '${workspaceFolder}',
    MILLER_BINARY: millerBinary,
  },
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
echo "Cursor global Miller MCP config installed."
echo "  config:   ${CURSOR_MCP_JSON}"
echo "  launcher: ${LAUNCHER_ROOT}/bin/miller-plugin-launcher.cjs"
echo "  binary:   ${MILLER_BINARY}"
"${MILLER_BINARY}" version
echo
echo "Reload Cursor (Developer: Reload Window) to restart the Miller MCP server."
