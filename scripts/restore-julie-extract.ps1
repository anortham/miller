<#
.SYNOPSIS
  restore-julie-extract.ps1 — restore the pinned julie-extract.exe into .tools\ (Windows mirror of the .sh).

.DESCRIPTION
  Reads scripts\julie-pins.json for the version, per-triple asset name + sha256, and the URL template.
  Detects the host platform (x86_64-pc-windows-msvc is the only Windows asset), downloads the matching
  release archive from anortham/julie-extractors, VERIFIES its sha256 against the pin (julie-extractors
  publishes no checksum assets — these were download-verified and committed), extracts ONLY
  julie-extract.exe from the archive, and removes the archive. Fails loudly on unsupported platforms
  (no windows-arm64).
  While a contract bump is staged before release assets publish, pass -FromSource or set
  MILLER_JULIE_SOURCE to build julie-extract from a local julie-extractors checkout.
#>
[CmdletBinding()]
param(
    [switch]$FromSource,
    [string]$SourcePath
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
$Pins      = Join-Path $ScriptDir 'julie-pins.json'
$ToolsDir  = Join-Path $RepoRoot '.tools'

if (-not (Test-Path $Pins)) {
    Write-Error "pins file not found at $Pins"
    exit 1
}

$config = Get-Content $Pins -Raw | ConvertFrom-Json

function Restore-FromSource {
    param([string]$SourceRoot)

    if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
        throw "from-source restore requires -SourcePath or MILLER_JULIE_SOURCE"
    }
    $sourceFull = (Resolve-Path $SourceRoot).Path
    $manifest = Join-Path $sourceFull 'Cargo.toml'
    if (-not (Test-Path $manifest)) {
        throw "from-source path is not a Julie checkout: $sourceFull"
    }
    if ($null -eq (Get-Command cargo -ErrorAction SilentlyContinue)) {
        throw "cargo is required for -FromSource restore"
    }

    New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null
    $binary = Join-Path $ToolsDir 'julie-extract.exe'
    $sourceBinary = Join-Path $sourceFull 'target\release\julie-extract.exe'

    Write-Host "Building julie-extract v$($config.version) from source: $sourceFull"
    & cargo build --manifest-path $manifest --release -p julie-extract-cli --bin julie-extract
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build failed"
    }
    if (-not (Test-Path $sourceBinary)) {
        throw "expected build output not found: $sourceBinary"
    }

    Copy-Item -Path $sourceBinary -Destination $binary -Force
    $versionOutput = (& $binary --version 2>$null)
    if ($versionOutput -notlike "julie-extract*") {
        throw "restored binary does not self-identify as julie-extract; actual '$versionOutput'"
    }

    Write-Host "Installed: $binary"
    & $binary --version 2>$null
}

$sourceFromEnv = $env:MILLER_JULIE_SOURCE
if ($FromSource -or -not [string]::IsNullOrWhiteSpace($sourceFromEnv)) {
    $source = if (-not [string]::IsNullOrWhiteSpace($SourcePath)) { $SourcePath } else { $sourceFromEnv }
    Restore-FromSource -SourceRoot $source
    exit 0
}

# --- detect platform -> triple (only x64 Windows is published) ---
$arch = $env:PROCESSOR_ARCHITECTURE
$triple = $null
switch ($arch) {
    'AMD64' { $triple = 'x86_64-pc-windows-msvc' }
    'x86'   { $triple = 'x86_64-pc-windows-msvc' }  # 32-bit host running the x64 asset is not supported; flagged below
}

if ($null -eq $triple -or $arch -ne 'AMD64') {
    Write-Error @"
unsupported platform 'windows/$arch'. julie-extract v$($config.version) publishes only x86_64-pc-windows-msvc on
Windows. No windows-arm64 prebuilt asset exists; build from source with cargo, or use a supported host.
"@
    exit 1
}

$asset  = $config.assets.$triple.name -replace '\{VER\}', $config.version
$sha256 = $config.assets.$triple.sha256
if ([string]::IsNullOrEmpty($asset) -or [string]::IsNullOrEmpty($sha256)) {
    Write-Error @"
no published asset pin for julie-extract v$($config.version) / $triple in $Pins.
Until release assets publish, run:
  `$env:MILLER_JULIE_SOURCE='C:\path\to\julie-extractors'; scripts\restore-julie-extract.ps1 -FromSource
"@
    exit 1
}

$url = $config.urlTemplate.Replace('{VER}', $config.version).Replace('{asset}', $asset)

New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null
$archive = Join-Path $ToolsDir $asset
$binary  = Join-Path $ToolsDir 'julie-extract.exe'

Write-Host "Restoring julie-extract v$($config.version) for $triple"
Write-Host "  url:    $url"
Write-Host "  sha256: $sha256"

# --- download ---
Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing

# --- verify sha256 ---
$actual = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $sha256.ToLowerInvariant()) {
    Remove-Item $archive -Force -ErrorAction SilentlyContinue
    Write-Error "sha256 mismatch for $archive`n  expected: $sha256`n  actual:   $actual"
    exit 1
}
Write-Host "  sha256 OK"

# --- extract ONLY julie-extract.exe from the archive ---
$staging = Join-Path $ToolsDir ('julie-extract-stage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    Expand-Archive -Path $archive -DestinationPath $staging -Force
    $found = Get-ChildItem -Path $staging -Recurse -Filter 'julie-extract.exe' | Select-Object -First 1
    if ($null -eq $found) {
        Write-Error "julie-extract.exe not found inside $asset"
        exit 1
    }
    Copy-Item -Path $found.FullName -Destination $binary -Force
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

# --- cleanup ---
Remove-Item $archive -Force -ErrorAction SilentlyContinue

Write-Host "Installed: $binary"
& $binary --version 2>$null
