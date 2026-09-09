$ErrorActionPreference = 'Stop'
$python = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $python) {
    throw 'python is required to run the release preflight.'
}

& $python.Source (Join-Path $PSScriptRoot 'release-preflight.py') @args
exit $LASTEXITCODE
