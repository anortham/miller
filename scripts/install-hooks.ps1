# install-hooks.ps1 - point git at the repo's tracked hooks directory (.githooks/).
#
# Git does not version .git/hooks, so the repo ships hooks under .githooks/ and this
# one-time command tells git to use them.
#
# Usage: scripts/install-hooks.ps1

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir

Push-Location $RepoRoot
try {
    & git config core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $isWindowsHost = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
    if (-not $isWindowsHost) {
        $preCommit = Join-Path $RepoRoot '.githooks/pre-commit'
        if (Test-Path $preCommit) {
            & chmod +x $preCommit 2>$null
        }
    }

    Write-Host 'git hooks installed: core.hooksPath -> .githooks'
}
finally {
    Pop-Location
}
