param(
    [int]$TargetProcessId,
    [string]$ExecutablePath,
    [string]$LogPath,
    [string]$OutputPath,
    [ValidateSet('fresh', 'post-use', 'activity')][string]$Round = 'fresh',
    [string]$StopPath,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-CpuDelta($Previous, [string]$Key, [double]$CpuSeconds, [bool]$SamplingStarted) {
    $delta = if ($Previous.ContainsKey($Key)) { $CpuSeconds - $Previous[$Key] }
        elseif ($SamplingStarted) { $CpuSeconds } else { 0 }
    $Previous[$Key] = $CpuSeconds
    return $delta
}

function Get-ResourceTotals($Counters, [double]$ElapsedSeconds, [int]$Processors) {
    if ($ElapsedSeconds -le 0 -or [double]::IsNaN($ElapsedSeconds) -or
        [double]::IsInfinity($ElapsedSeconds) -or $Processors -le 0 -or @($Counters).Count -eq 0) {
        throw 'invalid_resource_interval'
    }
    foreach ($counter in $Counters) {
        foreach ($field in @('PrivateBytes', 'WorkingSet', 'CpuDeltaSeconds')) {
            if ($null -eq $counter.$field -or $counter.$field -lt 0 -or
                [double]::IsNaN($counter.$field) -or [double]::IsInfinity($counter.$field)) {
                throw 'invalid_resource_counter'
            }
        }
    }
    $privateBytes = ($Counters | Measure-Object PrivateBytes -Sum).Sum
    $workingSet = ($Counters | Measure-Object WorkingSet -Sum).Sum
    $cpuSeconds = ($Counters | Measure-Object CpuDeltaSeconds -Sum).Sum
    [pscustomobject]@{
        PrivateBytes = $privateBytes
        WorkingSet = $workingSet
        CpuDeltaSeconds = $cpuSeconds
        CpuPercent = $cpuSeconds / $ElapsedSeconds / $Processors * 100
        MemoryWithinBudget = $privateBytes -le 90000000
    }
}

function Get-RoundResult($Mode, $Samples, $Elapsed, $Peak, $CpuPercent, $Missed, $Incomplete) {
    if ($Mode -eq 'activity') {
        return @{ type = 'activity_result'; samples = $Samples; durationSeconds = $Elapsed
            sampledPrivateBytesPeak = $Peak; peakIsLowerBound = $true; incompleteSamples = $Incomplete
            fullResourceAcceptance = $false }
    }
    $valid = $Elapsed -ge 600 -and $Samples -ge 600 -and $Incomplete -eq 0
    return @{ type = 'result'; valid = $valid; roundPassed = $valid -and $Peak -le 90000000 -and
        $CpuPercent -le 0.3 -and $Missed -eq 0; samples = $Samples; durationSeconds = $Elapsed
        privateBytesPeak = $Peak; cpuPercent = $CpuPercent; missedIntervals = $Missed
        fullResourceAcceptance = $false }
}

function Test-FreshStartupEvents($Events) {
    return @($Events | Where-Object {
        $_ -ne 'guide_runtime' -and $_ -match '^(guide_|drag_|track_|snap|overlay|target_content_event)'
    }).Count -eq 0
}

if ($SelfTest) {
    if (!(Test-FreshStartupEvents @('guide_runtime', 'start', 'ball_loaded'))) {
        throw 'runtime_build_marker_was_misclassified_as_prior_use'
    }
    foreach ($activityEvent in @('guide_consent', 'guide_worker_complete', 'guide_request_start',
            'guide_result', 'drag_start', 'track_start', 'snap', 'overlay', 'target_content_event')) {
        if (Test-FreshStartupEvents @('guide_runtime', 'start', 'ball_loaded', $activityEvent)) {
            throw 'fresh_round_accepted_prior_activity'
        }
    }
    $counters = @(
        [pscustomobject]@{ PrivateBytes = 70000000; WorkingSet = 120000000; CpuDeltaSeconds = 0.24 },
        [pscustomobject]@{ PrivateBytes = 20000000; WorkingSet = 40000000; CpuDeltaSeconds = 0.12 }
    )
    $totals = Get-ResourceTotals $counters 10 12
    if ($totals.PrivateBytes -ne 90000000 -or $totals.WorkingSet -ne 160000000 -or
        !$totals.MemoryWithinBudget -or [Math]::Abs($totals.CpuPercent - 0.3) -gt 0.000001) {
        throw 'resource_totals_or_units_failed'
    }
    $counters[1].PrivateBytes++
    if ((Get-ResourceTotals $counters 10 12).MemoryWithinBudget) { throw 'decimal_memory_limit_failed' }
    $cpuHistory = @{}
    if ((Get-CpuDelta $cpuHistory 'root' 2 $false) -ne 0 -or
        [Math]::Abs((Get-CpuDelta $cpuHistory 'root' 2.24 $true) - 0.24) -gt 0.000001 -or
        (Get-CpuDelta $cpuHistory 'new-helper' 0.12 $true) -ne 0.12) {
        throw 'new_helper_cpu_was_omitted'
    }
    foreach ($invalid in @($null, -1, [double]::NaN, [double]::PositiveInfinity)) {
        $counters[0].CpuDeltaSeconds = $invalid
        $rejected = $false
        try { Get-ResourceTotals $counters 10 12 | Out-Null } catch { $rejected = $true }
        if (!$rejected) { throw 'invalid_counter_was_accepted' }
    }
    $validCounter = @([pscustomobject]@{ PrivateBytes = 1; WorkingSet = 2; CpuDeltaSeconds = 0.1 })
    foreach ($interval in @(0, -1, [double]::NaN, [double]::PositiveInfinity)) {
        $rejected = $false
        try { Get-ResourceTotals $validCounter $interval 12 | Out-Null } catch { $rejected = $true }
        if (!$rejected) { throw 'invalid_interval_was_accepted' }
    }
    $active = Get-RoundResult activity 10 10 180000000 1 0 1
    if ($active.ContainsKey('roundPassed') -or !$active.peakIsLowerBound -or $active.incompleteSamples -ne 1) {
        throw 'activity_was_misclassified_as_idle_acceptance'
    }
    if (!(Get-RoundResult fresh 600 600.0956023 84967424 0 0 0).roundPassed) {
        throw 'complete_fresh_round_was_rejected'
    }
    if ((Get-RoundResult post-use 600 600 90000001 0 0 0).roundPassed -or
        (Get-RoundResult post-use 600 600 85000000 0.31 0 0).roundPassed -or
        (Get-RoundResult post-use 600 600 85000000 0 1 0).roundPassed) {
        throw 'failed_idle_round_was_accepted'
    }
    foreach ($incomplete in @(@(599, 600, 0), @(600, 599, 0), @(600, 600, 1))) {
        if ((Get-RoundResult fresh $incomplete[0] $incomplete[1] 85000000 0 0 $incomplete[2]).roundPassed) {
            throw 'incomplete_idle_round_was_accepted'
        }
    }
    Write-Output 'PASS: startup metadata/activity separation, generated resource totals, helper CPU inclusion, idle gates, incomplete-run refusal and activity/idle separation'
    exit 0
}

# No launches, input, target-content reads, process mutations or runtime diagnostics.
if ($TargetProcessId -le 0 -or !$ExecutablePath -or !$LogPath -or !$OutputPath) { throw 'resource_arguments_required' }
if ($Round -eq 'activity' -and (!$StopPath -or (Test-Path -LiteralPath $StopPath))) {
    throw 'activity_requires_new_stop_marker'
}
$expectedPath = (Resolve-Path -LiteralPath $ExecutablePath).Path
$appVersion = (Get-Item -LiteralPath $expectedPath).VersionInfo.ProductVersion
$logFile = Get-Item -LiteralPath $LogPath
$root = Get-Process -Id $TargetProcessId
if ($root.Path -ne $expectedPath -or $root.ProcessName -ne 'CetaFae.Probe') { throw 'resource_target_mismatch' }
$rootStart = $root.StartTime.ToUniversalTime()
$processors = (Get-CimInstance Win32_ComputerSystem -Property NumberOfLogicalProcessors).NumberOfLogicalProcessors
$owned = @{ $TargetProcessId = $root }
$previousCpu = @{}
$samplingStarted = $false
$initialLogLength = $logFile.Length
$initialDate = (Get-Date).Date
if ($Round -eq 'fresh' -and !(Test-FreshStartupEvents @(Get-Content -LiteralPath $logFile.FullName | ForEach-Object {
        ($_ | ConvertFrom-Json).evt }))) {
    throw 'fresh_round_already_used'
}
$output = [IO.StreamWriter]::new([IO.File]::Open([IO.Path]::GetFullPath($OutputPath),
    [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read))
$output.AutoFlush = $true
function Write-Sample($Row) { $output.WriteLine(($Row | ConvertTo-Json -Depth 5 -Compress)) }
function Read-ProductCounters {
    # Follow only owned parent IDs, without querying any process command line.
    $pending = [Collections.Generic.Queue[int]]::new()
    foreach ($ownedId in @($owned.Keys)) {
        if ($Round -eq 'activity' -and $ownedId -ne $TargetProcessId -and $owned[$ownedId].HasExited) {
            $owned[$ownedId].Dispose()
            $owned.Remove($ownedId)
        }
        else { $pending.Enqueue($ownedId) }
    }
    while ($pending.Count -gt 0) {
        $parentId = $pending.Dequeue()
        if ($Round -eq 'activity' -and $parentId -ne $TargetProcessId -and $owned[$parentId].HasExited) { continue }
        $parentStart = $owned[$parentId].StartTime.ToUniversalTime()
        foreach ($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$parentId" -Property ProcessId, CreationDate)) {
            $childId = [int]$child.ProcessId
            if (!$owned.ContainsKey($childId) -and $child.CreationDate.ToUniversalTime() -ge $parentStart) {
                try {
                    $owned[$childId] = Get-Process -Id $childId
                    $pending.Enqueue($childId)
                }
                catch {
                    if ($Round -ne 'activity') { throw }
                    [pscustomobject]@{ ProcessId = $childId; Unavailable = $true }
                }
            }
        }
    }
    foreach ($expected in $owned.Values) {
        $process = $null
        try {
            $process = Get-Process -Id $expected.Id
            if ($process.HasExited -or $process.StartTime -ne $expected.StartTime -or
                ($process.Id -eq $TargetProcessId -and $process.Path -ne $expectedPath)) {
                throw 'owned_process_identity_changed'
            }
            $key = "$($process.Id):$($process.StartTime.ToUniversalTime().Ticks)"
            $delta = Get-CpuDelta $previousCpu $key $process.TotalProcessorTime.TotalSeconds $samplingStarted
            [pscustomobject]@{ ProcessId = $process.Id; StartUtc = $process.StartTime.ToUniversalTime().ToString('o')
                PrivateBytes = $process.PrivateMemorySize64; WorkingSet = $process.WorkingSet64; CpuDeltaSeconds = $delta
                Unavailable = $false }
        }
        catch {
            if ($Round -ne 'activity' -or $expected.Id -eq $TargetProcessId) { throw }
            [pscustomobject]@{ ProcessId = $expected.Id; Unavailable = $true }
        }
        finally { if ($null -ne $process) { $process.Dispose() } }
    }
}
try {
    Write-Sample @{ type = 'environment'; round = $Round; startedAt = (Get-Date).ToString('o')
        rootProcessId = $TargetProcessId; rootStartUtc = $rootStart.ToString('o'); executable = $expectedPath
        applicationVersion = $appVersion
        runtimeVersion = @($root.Modules | Where-Object ModuleName -eq 'coreclr.dll' | ForEach-Object { $_.FileVersionInfo.FileVersion })
        logicalProcessors = $processors; os = [Environment]::OSVersion.VersionString
        durationSeconds = $(if ($Round -eq 'activity') { $null } else { 600 })
        warmupSeconds = $(if ($Round -eq 'activity') { 0 } else { 30 }); intervalSeconds = 1
        limitations = 'Counters are read sequentially by an external tool. Exited processes, unreadable counters or idle-log activity invalidate the run. Post-use history and display/network environment require separate evidence.' }
    if ($Round -ne 'activity') { Start-Sleep -Seconds 30 }
    Read-ProductCounters | Out-Null
    $samplingStarted = $true
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $previousSeconds = 0.0
    $totalCpuSeconds = 0.0
    $peakPrivateBytes = 0.0
    $samples = 0
    $missedIntervals = 0
    $incompleteSamples = 0
    do {
        Start-Sleep -Milliseconds ([int][Math]::Max(1, (($samples + 1) - $clock.Elapsed.TotalSeconds) * 1000))
        $counters = @(Read-ProductCounters)
        if ($Round -ne 'activity' -and ((Get-Date).Date -ne $initialDate -or
            (Get-Item -LiteralPath $logFile.FullName).Length -ne $initialLogLength)) {
            throw 'idle_log_changed_or_rotated'
        }
        $elapsed = $clock.Elapsed.TotalSeconds
        $interval = $elapsed - $previousSeconds
        if ($interval -gt 1.5) { $missedIntervals++ }
        $totals = $null
        if (@($counters | Where-Object Unavailable).Count -gt 0) { $incompleteSamples++ }
        else {
            $totals = Get-ResourceTotals $counters $interval $processors
            $totalCpuSeconds += $totals.CpuDeltaSeconds
            $peakPrivateBytes = [Math]::Max($peakPrivateBytes, $totals.PrivateBytes)
        }
        Write-Sample @{ type = 'sample'; elapsedSeconds = $elapsed; intervalSeconds = $interval
            processes = $counters; totals = $totals }
        $samples++
        $previousSeconds = $elapsed
    } while (($Round -eq 'activity' -and !(Test-Path -LiteralPath $StopPath)) -or
        ($Round -ne 'activity' -and $elapsed -lt 600))
    if ($Round -ne 'activity' -and ((Get-Date).Date -ne $initialDate -or
        (Get-Item -LiteralPath $logFile.FullName).Length -ne $initialLogLength)) {
        throw 'idle_log_changed_or_rotated'
    }
    $cpuPercent = $totalCpuSeconds / $elapsed / $processors * 100
    $result = Get-RoundResult $Round $samples $elapsed $peakPrivateBytes $cpuPercent $missedIntervals $incompleteSamples
    Write-Sample $result
    Write-Output "Resource sampling finished: mode=$Round; samples=$samples; metadata=$OutputPath"
    if ($Round -ne 'activity' -and !$result.roundPassed) { exit 1 }
}
catch {
    Write-Sample @{ type = 'failure'; category = 'incomplete_resource_run'; fullResourceAcceptance = $false }
    throw
}
finally {
    $output.Dispose()
    foreach ($process in $owned.Values) { $process.Dispose() }
}
