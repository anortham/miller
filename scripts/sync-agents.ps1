# sync-agents.ps1 - regenerate AGENTS.md as a byte-for-byte mirror of CLAUDE.md.
#
# CLAUDE.md is the single source of truth. AGENTS.md exists so tools that read the
# open AGENTS.md convention get the same project guidance.
#
# Usage: scripts/sync-agents.ps1

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
$Source = Join-Path $RepoRoot 'CLAUDE.md'
$Destination = Join-Path $RepoRoot 'AGENTS.md'

if (-not (Test-Path $Source)) {
    Write-Error "source $Source not found"
    exit 1
}

Copy-Item -Path $Source -Destination $Destination -Force
Write-Host 'AGENTS.md regenerated from CLAUDE.md.'
