# test.ps1 - PowerShell front door to Miller's test suites.
#
# Usage:
#   scripts/test.ps1            # fast suite (default)
#   scripts/test.ps1 fast       # fast suite, with a budget tripwire
#   scripts/test.ps1 scale      # scale suite only
#   scripts/test.ps1 all        # fast + scale
#   $env:FAST_BUDGET_SECONDS=15; scripts/test.ps1 fast
#
# Any extra args after the suite name are passed through to dotnet test
# (for example: scripts/test.ps1 fast -v n).

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
$Solution = Join-Path $RepoRoot 'Miller.slnx'
$Config = if ([string]::IsNullOrWhiteSpace($env:CONFIG)) { 'Release' } else { $env:CONFIG }
$FastBudgetSeconds = if ([string]::IsNullOrWhiteSpace($env:FAST_BUDGET_SECONDS)) {
    30
} else {
    [int]$env:FAST_BUDGET_SECONDS
}

$Suite = if ($args.Count -gt 0) { [string]$args[0] } else { 'fast' }
[string[]]$DotnetArgs = @()
if ($args.Count -gt 1) {
    $DotnetArgs = [string[]]@($args[1..($args.Count - 1)])
}

function Show-Help {
    Write-Host @"
Usage:
  scripts/test.ps1            # fast suite (default)
  scripts/test.ps1 fast       # fast suite, with a budget tripwire
  scripts/test.ps1 scale      # scale suite only
  scripts/test.ps1 all        # fast + scale

Environment:
  CONFIG=Release|Debug
  FAST_BUDGET_SECONDS=30
"@
}

function Invoke-FastSuite {
    Write-Host "==> fast suite (Category!=Scale), budget ${FastBudgetSeconds}s"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & dotnet test $Solution -c $Config --filter 'Category!=Scale' @DotnetArgs
    $code = $LASTEXITCODE
    $sw.Stop()
    if ($code -ne 0) {
        exit $code
    }

    $elapsed = [int][Math]::Ceiling($sw.Elapsed.TotalSeconds)
    Write-Host "    fast suite wall time: ${elapsed}s (local target <10s, ceiling ${FastBudgetSeconds}s)"
    if ($elapsed -gt $FastBudgetSeconds) {
        [Console]::Error.WriteLine("ERROR: fast suite took ${elapsed}s (> ${FastBudgetSeconds}s ceiling).")
        [Console]::Error.WriteLine('       Move slow tests to Category=Scale.')
        exit 1
    }
}

function Invoke-ScaleSuite {
    Write-Host "==> scale suite (Category=Scale) - spawns the real julie-extract"
    $unixBinary = Join-Path $RepoRoot '.tools/julie-extract'
    $winBinary = Join-Path $RepoRoot '.tools/julie-extract.exe'
    if (-not (Test-Path $unixBinary) -and -not (Test-Path $winBinary)) {
        Write-Host "    note: .tools/julie-extract not found - these tests will SKIP (not fail)."
        Write-Host "    run scripts/restore-julie-extract.ps1 to enable them on Windows."
    }

    & dotnet test $Solution -c $Config --filter 'Category=Scale' @DotnetArgs
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        exit $code
    }
}

switch ($Suite) {
    'fast' { Invoke-FastSuite }
    'scale' { Invoke-ScaleSuite }
    'all' {
        Invoke-FastSuite
        Invoke-ScaleSuite
    }
    { $_ -in @('-h', '--help', 'help') } {
        Show-Help
    }
    default {
        [Console]::Error.WriteLine("error: unknown suite '$Suite'. Use one of: fast | scale | all (see --help).")
        exit 2
    }
}
