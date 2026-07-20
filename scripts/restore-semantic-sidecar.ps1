<#
.SYNOPSIS
  restore-semantic-sidecar.ps1 — restore the pinned julie-semantic-sidecar.exe and the pinned
  sqlite-vec loadable extension into .tools\ (Windows mirror of the .sh).

.DESCRIPTION
  Reads scripts\semantic-pins.json for both pins: `sidecar` (keyed by rust target triple) and
  `sqliteVec` (keyed by .NET RID). Downloads each asset into a temp staging directory OUTSIDE the
  repo, VERIFIES its sha256 against the pin BEFORE extracting, then installs
  .tools\julie-semantic-sidecar.exe and .tools\vec0.dll. A sha256 mismatch aborts before .tools\ is
  touched at all.

  Semantic retrieval is OPTIONAL (ADR-0003): a machine that never runs this script still builds and
  still runs — semantic simply fails open with a reason. Only a STALE restored sidecar fails the build.

  The Windows sidecar asset is a .zip (Expand-Archive); the Windows sqlite-vec asset is a .tar.gz
  (bsdtar, shipped with Windows 10+). The sidecar binary sits at the ARCHIVE ROOT, not under
  dist\<triple>\ as julie-extract's does.

  While a contract bump is staged before release assets publish, pass -FromSource or set
  MILLER_SEMANTIC_SIDECAR_SOURCE to build from a local julie-semantic-sidecar checkout.
  Set MILLER_SEMANTIC_PINS to override the pin file (testing).
#>
[CmdletBinding()]
param(
    [switch]$FromSource,
    [string]$SourcePath
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
$Pins      = if (-not [string]::IsNullOrWhiteSpace($env:MILLER_SEMANTIC_PINS)) { $env:MILLER_SEMANTIC_PINS } else { Join-Path $ScriptDir 'semantic-pins.json' }
$ToolsDir  = Join-Path $RepoRoot '.tools'

if (-not (Test-Path $Pins)) {
    Write-Error "pins file not found at $Pins"
    exit 1
}

$config = Get-Content $Pins -Raw | ConvertFrom-Json
$sidecarVersion = $config.sidecar.version
if ([string]::IsNullOrWhiteSpace($sidecarVersion)) {
    Write-Error "$Pins has no .sidecar.version"
    exit 1
}

function Assert-Sha256 {
    param([string]$File, [string]$Expected)

    $actual = (Get-FileHash -Path $File -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected.ToLowerInvariant()) {
        Write-Error "sha256 mismatch for $(Split-Path -Leaf $File)`n  expected: $Expected`n  actual:   $actual`n  nothing was installed into $ToolsDir"
        exit 1
    }
}

function Restore-FromSource {
    param([string]$SourceRoot)

    if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
        throw "from-source restore requires -SourcePath or MILLER_SEMANTIC_SIDECAR_SOURCE"
    }
    $sourceFull = (Resolve-Path $SourceRoot).Path
    $manifest = Join-Path $sourceFull 'Cargo.toml'
    if (-not (Test-Path $manifest)) {
        throw "from-source path is not a julie-semantic-sidecar checkout: $sourceFull"
    }
    if ($null -eq (Get-Command cargo -ErrorAction SilentlyContinue)) {
        throw "cargo is required for -FromSource restore"
    }

    New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null
    $binary = Join-Path $ToolsDir 'julie-semantic-sidecar.exe'
    $sourceBinary = Join-Path $sourceFull 'target\release\julie-semantic-sidecar.exe'

    Write-Host "Building julie-semantic-sidecar v$sidecarVersion from source: $sourceFull"
    & cargo build --manifest-path $manifest --release --bin julie-semantic-sidecar
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build failed"
    }
    if (-not (Test-Path $sourceBinary)) {
        throw "expected build output not found: $sourceBinary"
    }

    Copy-Item -Path $sourceBinary -Destination $binary -Force
    $versionOutput = (& $binary --version 2>$null)
    if ($versionOutput -notlike "julie-semantic-sidecar*") {
        throw "restored binary does not self-identify as julie-semantic-sidecar; actual '$versionOutput'"
    }

    Write-Host "Installed: $binary"
    & $binary --version 2>$null
    Write-Host "note: -FromSource restores the sidecar only; re-run without it to restore vec0.dll"
}

$sourceFromEnv = $env:MILLER_SEMANTIC_SIDECAR_SOURCE
if ($FromSource -or -not [string]::IsNullOrWhiteSpace($sourceFromEnv)) {
    $source = if (-not [string]::IsNullOrWhiteSpace($SourcePath)) { $SourcePath } else { $sourceFromEnv }
    Restore-FromSource -SourceRoot $source
    exit 0
}

# --- detect platform -> triple (only x64 Windows is published) ---
$arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
$triple = $null
switch ($arch) {
    'X64' { $triple = 'x86_64-pc-windows-msvc' }
}

if ($null -eq $triple) {
    Write-Error @"
unsupported platform 'windows/$arch'. julie-semantic-sidecar v$sidecarVersion publishes only
x86_64-pc-windows-msvc on Windows. No windows-arm64 prebuilt asset exists; build from source with
cargo, or use a supported host.
"@
    exit 1
}

$rid = $config.sidecar.ridByTriple.$triple
if ([string]::IsNullOrWhiteSpace($rid)) {
    Write-Error "no sqlite-vec RID mapping for $triple in $Pins"
    exit 1
}

$sidecarAsset = $config.sidecar.assets.$triple.name -replace '\{VER\}', $sidecarVersion
$sidecarSha   = $config.sidecar.assets.$triple.sha256
if ([string]::IsNullOrEmpty($sidecarAsset) -or [string]::IsNullOrEmpty($sidecarSha)) {
    Write-Error @"
no published asset pin for julie-semantic-sidecar v$sidecarVersion / $triple in $Pins.
Until release assets publish, run:
  `$env:MILLER_SEMANTIC_SIDECAR_SOURCE='C:\path\to\julie-semantic-sidecar'; scripts\restore-semantic-sidecar.ps1 -FromSource
"@
    exit 1
}

$vecVersion = $config.sqliteVec.version
$vecAsset   = $config.sqliteVec.assets.$rid.name -replace '\{VER\}', $vecVersion
$vecMember  = $config.sqliteVec.assets.$rid.member
$vecSha     = $config.sqliteVec.assets.$rid.sha256
if ([string]::IsNullOrEmpty($vecAsset) -or [string]::IsNullOrEmpty($vecMember) -or [string]::IsNullOrEmpty($vecSha)) {
    Write-Error "no sqlite-vec pin for RID $rid in $Pins"
    exit 1
}

$sidecarUrl = $config.sidecar.urlTemplate.Replace('{VER}', $sidecarVersion).Replace('{asset}', $sidecarAsset)
$vecUrl     = $config.sqliteVec.urlTemplate.Replace('{VER}', $vecVersion).Replace('{asset}', $vecAsset)

# Staging lives OUTSIDE the repo so a failed verification cannot leave debris in .tools\.
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('miller-semantic-restore-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    $sidecarArchive = Join-Path $staging $sidecarAsset
    Write-Host "Restoring julie-semantic-sidecar v$sidecarVersion for $triple"
    Write-Host "  url:    $sidecarUrl"
    Write-Host "  sha256: $sidecarSha"
    Invoke-WebRequest -Uri $sidecarUrl -OutFile $sidecarArchive -UseBasicParsing
    Assert-Sha256 -File $sidecarArchive -Expected $sidecarSha
    Write-Host "  sha256 OK"

    $vecArchive = Join-Path $staging $vecAsset
    Write-Host "Restoring sqlite-vec v$vecVersion ($vecMember) for $rid"
    Write-Host "  url:    $vecUrl"
    Write-Host "  sha256: $vecSha"
    Invoke-WebRequest -Uri $vecUrl -OutFile $vecArchive -UseBasicParsing
    Assert-Sha256 -File $vecArchive -Expected $vecSha
    Write-Host "  sha256 OK"

    # --- extract (sidecar .zip has the binary at the ARCHIVE ROOT; sqlite-vec ships a .tar.gz) ---
    $sidecarExtract = Join-Path $staging 'sidecar'
    New-Item -ItemType Directory -Force -Path $sidecarExtract | Out-Null
    Expand-Archive -Path $sidecarArchive -DestinationPath $sidecarExtract -Force
    $foundSidecar = Get-ChildItem -Path $sidecarExtract -Recurse -Filter 'julie-semantic-sidecar.exe' | Select-Object -First 1
    if ($null -eq $foundSidecar) {
        Write-Error "julie-semantic-sidecar.exe not found inside $sidecarAsset"
        exit 1
    }

    $vecExtract = Join-Path $staging 'vec'
    New-Item -ItemType Directory -Force -Path $vecExtract | Out-Null
    & tar -xzf $vecArchive -C $vecExtract $vecMember
    if ($LASTEXITCODE -ne 0) {
        throw "tar failed to extract $vecMember from $vecAsset"
    }
    $vecStaged = Join-Path $vecExtract $vecMember
    if (-not (Test-Path $vecStaged)) {
        Write-Error "$vecMember missing from $vecAsset"
        exit 1
    }

    # --- install: every download verified and extracted before .tools\ is touched ---
    New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null
    $sidecarBinary = Join-Path $ToolsDir 'julie-semantic-sidecar.exe'
    $vecLibrary    = Join-Path $ToolsDir $vecMember
    Copy-Item -Path $foundSidecar.FullName -Destination $sidecarBinary -Force
    Copy-Item -Path $vecStaged -Destination $vecLibrary -Force
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Installed: $sidecarBinary"
& $sidecarBinary --version 2>$null
Write-Host "Installed: $vecLibrary (sqlite-vec $vecVersion)"
