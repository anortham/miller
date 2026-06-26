# Configure Cursor to run Miller from this checkout via user-global MCP config.
#
# PowerShell mirror of install-cursor-local-dev.sh — keep the two in step.
#
# Miller binds workspace roots from MCP client roots on the first tool call (see
# docs/plans/2026-06-25-mcp-roots-workspace-binding-design.md).
#
# - Writes ~/.cursor/mcp.json with the Release build path.
# - Retires any legacy ~/.cursor/plugins/local/miller copy.
#
# Re-run after `dotnet build -c Release` to refresh the binary path.
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cursorMcpJson = Join-Path $env:USERPROFILE '.cursor\mcp.json'
$legacyPlugin = Join-Path $env:USERPROFILE '.cursor\plugins\local\miller'
$millerBinary = Join-Path $repoRoot 'src\Miller.Server\bin\Release\net10.0\miller.exe'

Write-Host 'Building Miller Release...'
dotnet build (Join-Path $repoRoot 'Miller.slnx') -c Release
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

if (-not (Test-Path $millerBinary)) {
    throw "Missing Miller binary: $millerBinary"
}

Write-Host "Writing Cursor MCP config to $cursorMcpJson..."
New-Item -ItemType Directory -Force (Join-Path $env:USERPROFILE '.cursor') | Out-Null
$config = @{}
if (Test-Path $cursorMcpJson) {
    try {
        $config = Get-Content $cursorMcpJson -Raw | ConvertFrom-Json -AsHashtable
    } catch {
        $config = @{}
    }
}
if (-not $config.Contains('mcpServers')) {
    $config['mcpServers'] = @{}
}
$config['mcpServers']['miller'] = [ordered]@{
    type    = 'stdio'
    command = $millerBinary
    args    = @('serve')
}
$config | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $cursorMcpJson

if (Test-Path $legacyPlugin) {
    $backup = Join-Path $env:USERPROFILE ('.cursor\plugin-backups\miller-local-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Write-Host "Retiring legacy Cursor local plugin to $backup..."
    New-Item -ItemType Directory -Force (Join-Path $env:USERPROFILE '.cursor\plugin-backups') | Out-Null
    Move-Item $legacyPlugin $backup
}

Write-Host ''
Write-Host 'Cursor Miller MCP config installed.'
Write-Host "  config:   $cursorMcpJson"
Write-Host "  binary:   $millerBinary"
& $millerBinary version
Write-Host ''
Write-Host 'Reload Cursor (Developer: Reload Window) to restart the Miller MCP server.'
