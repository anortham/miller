<#
.SYNOPSIS
  restore-semantic-sidecar.ps1 — restore the pinned julie-semantic-sidecar.exe and the pinned
  sqlite-vec loadable extension into .tools\ (Windows mirror of the .sh).

.DESCRIPTION
  Reads scripts\semantic-pins.json for both pins: `sidecar` (keyed by rust target triple) and
  `sqliteVec` (keyed by .NET RID). Downloads each asset into a temp staging directory OUTSIDE the
  repo, VERIFIES its sha256 against the pin BEFORE extracting, then installs
  .tools\julie-semantic-sidecar-runtime\ and .tools\vec0.dll. A sha256 mismatch aborts before .tools\ is
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
    [string]$SourcePath,
    [string]$VerifyPackage,
    [string]$ExpectedTriple
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
$Pins      = if (-not [string]::IsNullOrWhiteSpace($env:MILLER_SEMANTIC_PINS)) { $env:MILLER_SEMANTIC_PINS } else { Join-Path $ScriptDir 'semantic-pins.json' }
$ToolsDir  = Join-Path $RepoRoot '.tools'
$RuntimeDir = Join-Path $ToolsDir 'julie-semantic-sidecar-runtime'

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

function Assert-PackageManifest {
    param(
        [string]$PackageRoot,
        [string]$ExpectedTriple
    )

    $manifestPath = Join-Path $PackageRoot 'package-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "package-manifest.json missing from sidecar package"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.sidecar_version -ne $sidecarVersion) {
        throw "sidecar package manifest version '$($manifest.sidecar_version)' does not match '$sidecarVersion'"
    }
    if ($manifest.rust_target -ne $ExpectedTriple) {
        throw "sidecar package manifest target '$($manifest.rust_target)' does not match '$ExpectedTriple'"
    }
    if ($null -ne (Get-ChildItem -LiteralPath $PackageRoot -Directory -Force | Select-Object -First 1)) {
        throw "sidecar package contains an undeclared directory"
    }

    $expected = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $executableCount = 0
    foreach ($entry in $manifest.files) {
        $path = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($path) -or
            [System.IO.Path]::IsPathRooted($path) -or
            [System.IO.Path]::GetFileName($path) -ne $path -or
            $path -in @('.', '..')) {
            throw "unsafe sidecar package path '$path'"
        }
        if (-not $expected.Add($path)) {
            throw "duplicate sidecar package path '$path'"
        }
        $file = Join-Path $PackageRoot $path
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "manifest file missing from sidecar package: $path"
        }
        Assert-Sha256 -File $file -Expected ([string]$entry.sha256)
        if ((Get-Item -LiteralPath $file).Length -ne [long]$entry.size) {
            throw "size mismatch for sidecar package file $path"
        }
        if ($entry.role -eq 'executable') {
            $executableCount++
            if ($path -ne 'julie-semantic-sidecar.exe') {
                throw "unexpected sidecar executable path '$path'"
            }
        }
    }
    if ($executableCount -ne 1) {
        throw "sidecar package manifest must declare exactly one executable"
    }

    $actual = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    Get-ChildItem -LiteralPath $PackageRoot -File -Force |
        Where-Object { $_.Name -ne 'package-manifest.json' } |
        ForEach-Object { [void]$actual.Add($_.Name) }
    if (-not $actual.SetEquals($expected)) {
        throw "sidecar package contents do not match package-manifest.json"
    }
}

if (-not [string]::IsNullOrWhiteSpace($VerifyPackage)) {
    if ([string]::IsNullOrWhiteSpace($ExpectedTriple)) {
        throw "-VerifyPackage requires -ExpectedTriple"
    }
    Assert-PackageManifest -PackageRoot $VerifyPackage -ExpectedTriple $ExpectedTriple
    Write-Host "Verified sidecar package: $VerifyPackage"
    exit 0
}

function Install-RuntimeDirectory {
    param([string]$Source)

    $candidate = Join-Path $ToolsDir ('.julie-semantic-sidecar-runtime.candidate.' + [Guid]::NewGuid().ToString('N'))
    $backup = Join-Path $ToolsDir ('.julie-semantic-sidecar-runtime.backup.' + [Guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $Source -Destination $candidate -Recurse
    try {
        if (Test-Path -LiteralPath $RuntimeDir) {
            Move-Item -LiteralPath $RuntimeDir -Destination $backup
        }
        Move-Item -LiteralPath $candidate -Destination $RuntimeDir
        Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
    }
    catch {
        Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $RuntimeDir) -and (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $RuntimeDir
        }
        throw
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
    $sourceRuntime = Join-Path $ToolsDir ('.julie-semantic-sidecar-source.' + [Guid]::NewGuid().ToString('N'))
    $binary = Join-Path $RuntimeDir 'julie-semantic-sidecar.exe'
    $sourceBinary = Join-Path $sourceFull 'target\release\julie-semantic-sidecar.exe'

    Write-Host "Building julie-semantic-sidecar v$sidecarVersion from source: $sourceFull"
    & cargo build --manifest-path $manifest --release --bin julie-semantic-sidecar
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build failed"
    }
    if (-not (Test-Path $sourceBinary)) {
        throw "expected build output not found: $sourceBinary"
    }

    $versionOutput = (& $sourceBinary --version 2>$null)
    if ($versionOutput -notlike "julie-semantic-sidecar*$sidecarVersion*") {
        throw "built binary does not report pinned sidecar version $sidecarVersion; actual '$versionOutput'"
    }

    New-Item -ItemType Directory -Force -Path $sourceRuntime | Out-Null
    Copy-Item -LiteralPath $sourceBinary -Destination (Join-Path $sourceRuntime 'julie-semantic-sidecar.exe')
    Install-RuntimeDirectory -Source $sourceRuntime
    Remove-Item -LiteralPath $sourceRuntime -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $ToolsDir 'julie-semantic-sidecar'), (Join-Path $ToolsDir 'julie-semantic-sidecar.exe') -Force -ErrorAction SilentlyContinue

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
    Assert-PackageManifest -PackageRoot $sidecarExtract -ExpectedTriple $triple

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
    $sidecarBinary = Join-Path $RuntimeDir 'julie-semantic-sidecar.exe'
    $vecLibrary    = Join-Path $ToolsDir $vecMember
    Install-RuntimeDirectory -Source $sidecarExtract
    Copy-Item -Path $vecStaged -Destination $vecLibrary -Force
    Remove-Item (Join-Path $ToolsDir 'julie-semantic-sidecar'), (Join-Path $ToolsDir 'julie-semantic-sidecar.exe') -Force -ErrorAction SilentlyContinue
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Installed: $sidecarBinary"
& $sidecarBinary --version 2>$null
Write-Host "Installed: $vecLibrary (sqlite-vec $vecVersion)"
