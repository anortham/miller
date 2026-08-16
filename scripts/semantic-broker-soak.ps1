[CmdletBinding()]
param(
    [string]$ToolsRoot,
    [string]$MillerHome,
    [string]$OutputDirectory,
    [int]$DurationMinutes = 30,
    [int]$DurationSeconds = 0,
    [int]$TimeoutSeconds = 120,
    [ValidateRange(1, 100)]
    [int]$RapidReconnectCount = 8,
    [ValidateRange(0, 86400)]
    [int]$SleepResumeWindowSeconds = 0
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ToolsRoot) { $ToolsRoot = Join-Path $repoRoot '.tools' }
if (-not $MillerHome) {
    $MillerHome = Join-Path ([System.IO.Path]::GetTempPath()) ("miller-semantic-soak-home-" + [guid]::NewGuid().ToString('N'))
}
if (-not $OutputDirectory) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $OutputDirectory = Join-Path $repoRoot "artifacts/semantic-broker-soak/$stamp"
}
if ($DurationSeconds -le 0) { $DurationSeconds = $DurationMinutes * 60 }
New-Item -ItemType Directory -Force -Path $MillerHome, $OutputDirectory | Out-Null

$candidate = Join-Path $ToolsRoot 'julie-semantic-sidecar-runtime/julie-semantic-sidecar.exe'
$summaryPath = Join-Path $OutputDirectory 'summary.json'
function Write-Skip([string]$Reason) {
    [ordered]@{
        status = 'skipped'
        reason = $Reason
        candidate = $candidate
        acceptance = @{ releaseGate = $true }
    } | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 $summaryPath
    Write-Error $Reason -ErrorAction Continue
    exit 77
}

if (-not (Test-Path $candidate)) {
    Write-Skip "Broker-capable julie-semantic-sidecar not found at $candidate. Restore the pinned package with scripts/restore-semantic-sidecar.ps1."
}

$capabilityError = Join-Path $OutputDirectory 'broker-capability.stderr'
$capabilityOutput = Join-Path $OutputDirectory 'broker-capability.stdout'
$capability = Start-Process -FilePath $candidate -ArgumentList 'broker' -NoNewWindow -PassThru -Wait `
    -RedirectStandardOutput $capabilityOutput -RedirectStandardError $capabilityError
if ($capability.ExitCode -ne 2 -or
    -not (Select-String -Quiet -SimpleMatch 'broker requires --model' $capabilityError)) {
    Write-Skip 'The candidate does not expose the shared broker CLI. Restore the pinned package with scripts/restore-semantic-sidecar.ps1.'
}

$probeProject = Join-Path $repoRoot 'scripts/Miller.SemanticBrokerProbe/Miller.SemanticBrokerProbe.csproj'
& dotnet build $probeProject -c Release --nologo | Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'probe-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
$probe = Join-Path $repoRoot 'scripts/Miller.SemanticBrokerProbe/bin/Release/net10.0/miller-semantic-broker-probe.dll'
$candidateSha256 = (Get-FileHash -Algorithm SHA256 $candidate).Hash.ToLowerInvariant()
$candidateVersion = (& $candidate --version).Trim()
$defaultModel = 'bge-small-en-v1.5-f32'
$fallbackModel = 'qwen3-0.6b-f16'
$allProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$normalExitCodes = [System.Collections.Generic.List[int]]::new()
$normalProbeExpectedCount = 0
$normalProbeCompleteCount = 0
$observedExpectedKillCount = 0

function Stop-AllProbes {
    foreach ($process in $allProcesses) {
        if (-not $process.HasExited) {
            try { $process.Kill($true) } catch { }
        }
    }
    Get-CimInstance Win32_Process |
        Where-Object {
            $_.CommandLine -like "*$candidate* broker *" -and
            $_.CommandLine -like "*$MillerHome\semantic\*"
        } |
        ForEach-Object {
            try { Stop-Process -Id $_.ProcessId -Force } catch { }
        }
}

trap {
    Stop-AllProbes
    [Console]::Error.WriteLine($_)
    exit 1
}

function Get-GpuMemory {
    if (-not (Get-Command nvidia-smi -ErrorAction SilentlyContinue)) { return $null }
    $values = & nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return [int](($values | ForEach-Object { [int]$_.Trim() } | Measure-Object -Sum).Sum)
}

function Save-BrokerTree([string]$Name) {
    Get-CimInstance Win32_Process |
        Where-Object {
            $_.CommandLine -like "*$candidate* broker *" -and
            $_.CommandLine -like "*$MillerHome\semantic\*"
        } |
        Select-Object ProcessId, ParentProcessId, Name, CommandLine |
        ConvertTo-Json -Depth 3 |
        Set-Content -Encoding utf8 (Join-Path $OutputDirectory $Name)
}

function Get-BrokerCount {
    return @(
        Get-CimInstance Win32_Process |
            Where-Object {
                $_.CommandLine -like "*$candidate* broker *" -and
                $_.CommandLine -like "*$MillerHome\semantic\*"
            }
    ).Count
}

# Windows named pipes live in a machine-global namespace and the broker pipe is keyed only by model identity
# (docs/contracts/semantic-broker-v1.md: `\\.\pipe\miller-semantic-<identity>`), so -MillerHome does NOT isolate this
# run the way the Unix socket path under <miller-home>/semantic/ does. A broker owned by any other Miller session for
# the same model silently absorbs every probe: they connect as isOwner:false / spawnAttempts:0, Get-BrokerCount finds
# nothing under THIS run's home, and the ownership scenarios die on the opaque "Could not identify the broker process."
#
# Checked before the run AND after each probe, because the race is not only at startup: a live Miller indexing a
# workspace respawns its broker whenever a file change triggers vector convergence, so one can appear mid-run. That is
# how a 30-minute gate run was lost — the soak's own JSONL output was landing inside the indexed workspace and kept
# waking the indexer, which is why `artifacts/` is now gitignored.
function Assert-NoForeignBroker([string]$When) {
    # Match on the image name, not the command line: a shell or editor whose command line merely MENTIONS
    # julie-semantic-sidecar (this script, a grep, a CI step) would otherwise register as a foreign broker.
    $foreign = @(
        Get-CimInstance Win32_Process |
            Where-Object {
                $_.Name -like 'julie-semantic-sidecar*' -and
                $_.CommandLine -like '* broker *' -and
                $_.CommandLine -notlike "*$MillerHome\semantic\*"
            }
    )
    if ($foreign.Count -eq 0) { return }

    $detail = ($foreign | ForEach-Object { "  pid $($_.ProcessId): $($_.CommandLine)" }) -join [Environment]::NewLine
    throw @"
$($foreign.Count) semantic broker(s) from another Miller session are running ($When). On Windows they own the
machine-global pipe for their model identity, so this soak's probes attach to them instead of spawning the brokers
these scenarios need to own and kill. Results would be invalid.

$detail

Stop the other Miller session (or start it with MILLER_SEMANTIC=off) and re-run, and keep the workspace quiet for the
duration. On Unix this cannot happen: the endpoint is a socket path under <miller-home>/semantic/, which -MillerHome
already isolates.
"@
}

function Start-Probe(
    [string]$Label,
    [int]$RunSeconds,
    [string]$Model = $defaultModel
) {
    $out = Join-Path $OutputDirectory "$Label.jsonl"
    $err = Join-Path $OutputDirectory "$Label.stderr"
    $arguments = @(
        $probe,
        '--tools-root', $ToolsRoot,
        '--miller-home', $MillerHome,
        '--model', $Model,
        '--label', $Label,
        '--duration-seconds', $RunSeconds,
        '--startup-timeout-seconds', $TimeoutSeconds,
        '--request-timeout-seconds', 30,
        '--grace-seconds', 30,
        '--interval-ms', 50,
        '--batch-size', 8
    )
    $process = Start-Process -FilePath dotnet -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError $err
    $allProcesses.Add($process)
    return $process
}

function Wait-Ready([string]$Label) {
    $path = Join-Path $OutputDirectory "$Label.jsonl"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path $path) -and
            (Select-String -Quiet -SimpleMatch '"event":"failed"' $path)) {
            throw "Probe $Label reported failed readiness."
        }
        if ((Test-Path $path) -and
            (Select-String -Quiet -SimpleMatch '"event":"ready"' $path)) {
            Assert-NoForeignBroker "when probe $Label became ready"
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Probe $Label did not become ready within $TimeoutSeconds seconds."
}

function Wait-Probe(
    [System.Diagnostics.Process]$Process,
    [int]$AllowedSeconds = $TimeoutSeconds
) {
    if (-not $Process.WaitForExit($AllowedSeconds * 1000)) {
        $Process.Kill($true)
        throw "Probe $($Process.Id) exceeded its deadline."
    }
    return $Process.ExitCode
}

function Prepare-VerifiedModel([string]$Model) {
    $stdoutPath = Join-Path $OutputDirectory "prepare-$Model.stdout"
    $stderrPath = Join-Path $OutputDirectory "prepare-$Model.stderr"
    $prepare = Start-Process -FilePath $candidate -ArgumentList @('prepare', '--model', $Model) -NoNewWindow -PassThru -Wait `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    if ($prepare.ExitCode -ne 0) {
        $detail = if (Test-Path $stderrPath) { (Get-Content $stderrPath -Raw).Trim() } else { '' }
        throw "Model $Model preparation failed with exit code $($prepare.ExitCode). $detail"
    }
}

function Read-Events([string]$Label) {
    $path = Join-Path $OutputDirectory "$Label.jsonl"
    return @(Get-Content $path | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}

function Record-NormalProbe(
    [string]$Label,
    [System.Diagnostics.Process]$Process,
    [int]$AllowedSeconds = $TimeoutSeconds
) {
    $exitCode = Wait-Probe $Process $AllowedSeconds
    Assert-NoForeignBroker "after probe $Label completed"
    $normalExitCodes.Add($exitCode)
    $script:normalProbeExpectedCount++
    if (@(Read-Events $Label | Where-Object event -eq complete).Count -eq 1) {
        $script:normalProbeCompleteCount++
    }
}

Assert-NoForeignBroker 'before starting'
Prepare-VerifiedModel $defaultModel
Prepare-VerifiedModel $fallbackModel

$gpuBefore = Get-GpuMemory
Save-BrokerTree 'process-tree-before.json'

$warm = Start-Probe 'warm' 2
Wait-Ready 'warm'
$warmBrokerCount = Get-BrokerCount
$gpuWarm = Get-GpuMemory
Record-NormalProbe 'warm' $warm

$same = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$same.Add((Start-Probe 'same-1' 12))
Wait-Ready 'same-1'
2..8 | ForEach-Object {
    $same.Add((Start-Probe "same-$_" 5))
}
2..8 | ForEach-Object { Wait-Ready "same-$_" }
$sameModelBrokerCount = Get-BrokerCount
$gpuMany = Get-GpuMemory
Save-BrokerTree 'process-tree-eight.json'
for ($index = 0; $index -lt $same.Count; $index++) {
    Record-NormalProbe "same-$($index + 1)" $same[$index]
}

$nonOwnerKeeper = Start-Probe 'non-owner-keeper' 10
Wait-Ready 'non-owner-keeper'
$nonOwner = Start-Probe 'non-owner-crash' 20
Wait-Ready 'non-owner-crash'
$nonOwner.Kill($true)
$nonOwner.WaitForExit()
if ($nonOwner.ExitCode -ne 0 -and
    @(Read-Events 'non-owner-crash' | Where-Object event -eq complete).Count -eq 0) {
    $observedExpectedKillCount++
}
Record-NormalProbe 'non-owner-keeper' $nonOwnerKeeper
$nonOwnerKeeperComplete = Read-Events 'non-owner-keeper' |
    Where-Object event -eq complete |
    Select-Object -Last 1

$crash = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$crash.Add((Start-Probe 'broker-crash-1' 15))
Wait-Ready 'broker-crash-1'
2..3 | ForEach-Object { $crash.Add((Start-Probe "broker-crash-$_" 10)) }
2..3 | ForEach-Object { Wait-Ready "broker-crash-$_" }
$brokerPid = $null
foreach ($index in 1..3) {
    $ready = Read-Events "broker-crash-$index" |
        Where-Object { $_.event -eq 'ready' -and $null -ne $_.ownerProcessId } |
        Select-Object -First 1
    if ($ready) { $brokerPid = [int]$ready.ownerProcessId; break }
}
if (-not $brokerPid) { throw 'Could not identify the broker process.' }
$brokerKillUnixTimeMilliseconds = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
Stop-Process -Id $brokerPid -Force
for ($index = 0; $index -lt $crash.Count; $index++) {
    Record-NormalProbe "broker-crash-$($index + 1)" $crash[$index]
}
$brokerRecovery = 1..3 |
    ForEach-Object { Read-Events "broker-crash-$_" } |
    Where-Object {
        $_.event -eq 'recovered' -and
        $_.unixTimeMilliseconds -gt $brokerKillUnixTimeMilliseconds
    } |
    Sort-Object unixTimeMilliseconds |
    Select-Object -First 1
$brokerRecoveryUnixTimeMilliseconds = $brokerRecovery.unixTimeMilliseconds
$brokerRecoverySeconds = $null
if ($null -ne $brokerRecoveryUnixTimeMilliseconds) {
    $brokerRecoverySeconds =
        ($brokerRecoveryUnixTimeMilliseconds - $brokerKillUnixTimeMilliseconds) / 1000.0
}

$owner = Start-Probe 'owner-short' 20
Wait-Ready 'owner-short'
$survivor = Start-Probe 'owner-survivor' 10
Wait-Ready 'owner-survivor'
$ownerKillUnixTimeMilliseconds = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$owner.Kill($true)
$owner.WaitForExit()
if ($owner.ExitCode -ne 0 -and
    @(Read-Events 'owner-short' | Where-Object event -eq complete).Count -eq 0) {
    $observedExpectedKillCount++
}
Record-NormalProbe 'owner-survivor' $survivor
$ownerRecovery = Read-Events 'owner-survivor' |
    Where-Object {
        $_.event -eq 'recovered' -and
        $_.unixTimeMilliseconds -gt $ownerKillUnixTimeMilliseconds
    } |
    Sort-Object unixTimeMilliseconds |
    Select-Object -First 1
$ownerRecoveryUnixTimeMilliseconds = $ownerRecovery.unixTimeMilliseconds
$ownerRecoverySeconds = $null
if ($null -ne $ownerRecoveryUnixTimeMilliseconds) {
    $ownerRecoverySeconds =
        ($ownerRecoveryUnixTimeMilliseconds - $ownerKillUnixTimeMilliseconds) / 1000.0
}

$rapidKeeperSeconds = [Math]::Max(
    30,
    $SleepResumeWindowSeconds + ($RapidReconnectCount * 5) + 10)
$rapidKeeper = Start-Probe 'rapid-reconnect-keeper' $rapidKeeperSeconds
Wait-Ready 'rapid-reconnect-keeper'
$rapidReconnectFailures = 0
foreach ($index in 1..$RapidReconnectCount) {
    $rapid = Start-Probe "rapid-reconnect-$index" 0
    try {
        Record-NormalProbe "rapid-reconnect-$index" $rapid
        if ($rapid.ExitCode -ne 0) { $rapidReconnectFailures++ }
    } catch { $rapidReconnectFailures++; throw }
}
$sleepResumeExercised = $SleepResumeWindowSeconds -gt 0
if ($sleepResumeExercised) {
    Write-Host "Sleep and resume this Windows machine during the next $SleepResumeWindowSeconds seconds."
    Start-Sleep -Seconds $SleepResumeWindowSeconds
    $afterResume = Start-Probe 'after-resume' 1
    try {
        Record-NormalProbe 'after-resume' $afterResume
        if ($afterResume.ExitCode -ne 0) { $rapidReconnectFailures++ }
    } catch { $rapidReconnectFailures++; throw }
}
try {
    Record-NormalProbe 'rapid-reconnect-keeper' $rapidKeeper ($rapidKeeperSeconds + $TimeoutSeconds + 60)
    if ($rapidKeeper.ExitCode -ne 0) { $rapidReconnectFailures++ }
} catch { $rapidReconnectFailures++; throw }

$old = Start-Probe 'model-old' 5 $defaultModel
$new = Start-Probe 'model-new' 5 $fallbackModel
Wait-Ready 'model-old'
Wait-Ready 'model-new'
$oldReady = Read-Events 'model-old' | Where-Object event -eq ready | Select-Object -Last 1
$newReady = Read-Events 'model-new' | Where-Object event -eq ready | Select-Object -Last 1
$acceleratedBrokerCount = @(
    @($oldReady, $newReady) | Where-Object { $_ -and $_.accelerated }
).Count
Record-NormalProbe 'model-old' $old
Record-NormalProbe 'model-new' $new

$soak = Start-Probe 'soak' $DurationSeconds
Record-NormalProbe 'soak' $soak ($DurationSeconds + $TimeoutSeconds + 60)

$gpuAfter = Get-GpuMemory
Save-BrokerTree 'process-tree-after.json'
$finalBrokerCount = Get-BrokerCount
$events = Get-ChildItem $OutputDirectory -Filter '*.jsonl' |
    ForEach-Object { Get-Content $_.FullName } |
    Where-Object { $_ } |
    ForEach-Object { $_ | ConvertFrom-Json }
$completed = @($events | Where-Object event -eq complete)
$hungRequests = [int](($completed | Measure-Object hungCount -Sum).Sum)
$failedRequests = [int](($completed | Measure-Object failedCount -Sum).Sum)
$failedEventCount = @($events | Where-Object event -eq failed).Count
$soakComplete = Read-Events 'soak' | Where-Object event -eq complete | Select-Object -Last 1
$warmReady = Read-Events 'warm' | Where-Object event -eq ready | Select-Object -Last 1
$nonOwnerKeeperFailed =
    [int]$nonOwnerKeeperComplete.failedCount + [int]$nonOwnerKeeperComplete.hungCount
$oneDelta = $null
$manyDelta = $null
$gpuPass = $null
if ($null -ne $gpuBefore -and $null -ne $gpuWarm -and $null -ne $gpuMany) {
    $oneDelta = $gpuWarm - $gpuBefore
    $manyDelta = $gpuMany - $gpuBefore
    if ($warmReady.accelerated -and $warmBrokerCount -eq 1 -and $oneDelta -ge 64) {
        $gpuPass = $manyDelta -le ($oneDelta + 256)
    }
}

$summary = [ordered]@{
    status = 'complete'
    candidate = $candidate
    candidateSha256 = $candidateSha256
    candidateVersion = $candidateVersion
    soakSeconds = $DurationSeconds
    warmBrokerCount = $warmBrokerCount
    sameModelBrokerCount = $sameModelBrokerCount
    oldEndpoint = $oldReady.endpointIdentity
    newEndpoint = $newReady.endpointIdentity
    acceleratedBrokerCount = $acceleratedBrokerCount
    brokerRecoverySeconds = $brokerRecoverySeconds
    ownerRecoverySeconds = $ownerRecoverySeconds
    hungRequests = $hungRequests
    failedRequests = $failedRequests
    failedEventCount = $failedEventCount
    normalProbeExitCodes = @($normalExitCodes)
    normalProbeExpectedCount = $normalProbeExpectedCount
    normalProbeCompleteCount = $normalProbeCompleteCount
    expectedKillCount = 2
    observedExpectedKillCount = $observedExpectedKillCount
    finalBrokerCount = $finalBrokerCount
    brokerCrash = [ordered]@{
        killUnixTimeMilliseconds = $brokerKillUnixTimeMilliseconds
        recoveryUnixTimeMilliseconds = $brokerRecoveryUnixTimeMilliseconds
    }
    ownerCrash = [ordered]@{
        killUnixTimeMilliseconds = $ownerKillUnixTimeMilliseconds
        recoveryUnixTimeMilliseconds = $ownerRecoveryUnixTimeMilliseconds
    }
    soak = [ordered]@{
        configuredDurationSeconds = $DurationSeconds
        observedTrafficMilliseconds = [long]$soakComplete.trafficElapsedMilliseconds
    }
    windows = [ordered]@{
        rapidReconnectCount = $RapidReconnectCount
        rapidReconnectFailures = $rapidReconnectFailures
        sleepResumeWindowSeconds = $SleepResumeWindowSeconds
        sleepResumeExercised = $sleepResumeExercised
    }
    gpu = [ordered]@{
        source = 'nvidia-smi global memory.used'
        beforeMiB = $gpuBefore
        warmMiB = $gpuWarm
        manyMiB = $gpuMany
        afterMiB = $gpuAfter
        oneSessionDeltaMiB = $oneDelta
        manySessionDeltaMiB = $manyDelta
        thresholdMiB = 256
        warmBrokerCount = $warmBrokerCount
        warmAccelerated = [bool]$warmReady.accelerated
        pass = $gpuPass
    }
    acceptance = [ordered]@{
        sameModelOneBroker = $sameModelBrokerCount -eq 1
        separateModelEndpoints = $oldReady -and $newReady -and $oldReady.endpointIdentity -ne $newReady.endpointIdentity
        atMostOneAccelerated = $acceleratedBrokerCount -le 1
        brokerRecoveryWithinThirtySeconds = $null -ne $brokerRecoverySeconds -and $brokerRecoverySeconds -le 30
        ownerRecoveryWithinThirtySeconds = $null -ne $ownerRecoverySeconds -and $ownerRecoverySeconds -le 30
        zeroHung = $hungRequests -eq 0
        zeroFailed = $failedRequests -eq 0 -and $failedEventCount -eq 0
        normalProbeExitCodes = @($normalExitCodes | Where-Object { $_ -ne 0 }).Count -eq 0
        normalProbeCompletions = $normalProbeCompleteCount -eq $normalProbeExpectedCount
        expectedKills = $observedExpectedKillCount -eq 2
        nonOwnerKeeper = $nonOwnerKeeperFailed -eq 0
        soakDuration = [long]$soakComplete.trafficElapsedMilliseconds -ge ($DurationSeconds * 1000)
        noOrphanBroker = $finalBrokerCount -eq 0
        rapidReconnect = $rapidReconnectFailures -eq 0
        sleepResume = if ($sleepResumeExercised) { $rapidReconnectFailures -eq 0 } else { $null }
        gpuEffectivelyConstant = $gpuPass
    }
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 $summaryPath
$summary | ConvertTo-Json -Depth 8
& dotnet $probe --verify-summary $summaryPath
if ($LASTEXITCODE -ne 0) { throw 'Semantic broker soak acceptance failed.' }
