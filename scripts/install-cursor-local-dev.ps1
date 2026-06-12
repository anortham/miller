# Configure Cursor to run Miller from this checkout via the global MCP config.
#
# PowerShell mirror of install-cursor-local-dev.sh — keep the two in step.
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
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$launcherRoot = Join-Path $env:USERPROFILE '.miller\plugin-cache\cursor-global-miller'
$cursorMcpJson = Join-Path $env:USERPROFILE '.cursor\mcp.json'
$legacyPlugin = Join-Path $env:USERPROFILE '.cursor\plugins\local\miller'
$millerBinary = Join-Path $repoRoot 'src\Miller.Server\bin\Release\net10.0\miller.exe'

Write-Host 'Building Miller Release...'
dotnet build (Join-Path $repoRoot 'Miller.slnx') -c Release
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

if (-not (Test-Path $millerBinary)) {
    throw "Missing Miller binary: $millerBinary"
}

Write-Host "Snapshotting launcher to $launcherRoot..."
New-Item -ItemType Directory -Force (Join-Path $launcherRoot 'bin'), (Join-Path $env:USERPROFILE '.cursor') | Out-Null
Copy-Item (Join-Path $repoRoot 'bin\miller-plugin-launcher.cjs') (Join-Path $launcherRoot 'bin') -Force
Copy-Item (Join-Path $repoRoot 'miller-plugin.json') $launcherRoot -Force

Write-Host "Merging miller server into $cursorMcpJson..."
$config = [ordered]@{}
if (Test-Path $cursorMcpJson) {
    $config = Get-Content $cursorMcpJson -Raw | ConvertFrom-Json -AsHashtable
}
if (-not $config.Contains('mcpServers')) {
    $config['mcpServers'] = [ordered]@{}
}
# Single-quoted on purpose: ${userHome} / ${workspaceFolder} are Cursor config
# interpolations, not PowerShell variables.
$config['mcpServers']['miller'] = [ordered]@{
    type    = 'stdio'
    command = 'node'
    args    = @('${userHome}/.miller/plugin-cache/cursor-global-miller/bin/miller-plugin-launcher.cjs')
    env     = [ordered]@{
        MILLER_WORKSPACE_ROOT = '${workspaceFolder}'
        MILLER_BINARY         = $millerBinary
    }
}
$config | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $cursorMcpJson

if (Test-Path $legacyPlugin) {
    $backup = Join-Path $env:USERPROFILE ('.cursor\plugin-backups\miller-local-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Write-Host "Retiring legacy Cursor local plugin to $backup..."
    New-Item -ItemType Directory -Force (Join-Path $env:USERPROFILE '.cursor\plugin-backups') | Out-Null
    Move-Item $legacyPlugin $backup
}

Write-Host ''
Write-Host 'Cursor global Miller MCP config installed.'
Write-Host "  config:   $cursorMcpJson"
Write-Host "  launcher: $(Join-Path $launcherRoot 'bin\miller-plugin-launcher.cjs')"
Write-Host "  binary:   $millerBinary"
& $millerBinary version
Write-Host ''
Write-Host 'Reload Cursor (Developer: Reload Window) to restart the Miller MCP server.'
