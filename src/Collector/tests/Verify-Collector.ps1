param(
    [string]$CollectorExecutable = (Join-Path $PSScriptRoot '../bin/Debug/net48/Collector.exe'),
    [string]$SimulatorExecutable = (Join-Path $PSScriptRoot '../../Simulator/bin/Debug/net48/Simulator.exe'),
    [switch]$KeepLogs
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Start-Child([string]$Executable, [bool]$Interactive) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.WorkingDirectory = Split-Path -Parent $Executable
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $Interactive
    $info.RedirectStandardOutput = $Interactive
    return [Diagnostics.Process]::Start($info)
}

function Send-Command($Process, [string]$Command) {
    $Process.StandardInput.WriteLine($Command)
    $Process.StandardInput.Flush()
    $reply = $Process.StandardOutput.ReadLineAsync()
    if (-not $reply.Wait(3000) -or $null -eq $reply.Result -or -not $reply.Result.StartsWith('OK')) {
        throw "Simulator did not acknowledge '$Command'."
    }
}

function Read-CollectorLog([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return }
    foreach ($file in (Get-ChildItem -LiteralPath $Directory -Filter 'collector-*.log' | Sort-Object Name)) {
        $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
        $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
        try {
            $reader = [IO.StreamReader]::new($stream)
            try { $contents = $reader.ReadToEnd() }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }

        $lines = $contents -split '\r?\n'
        # The final, unterminated line can still be in the process of being written.
        for ($i = 0; $i -lt $lines.Count - 1; $i++) {
            if ($lines[$i].Length -eq 0) { continue }
            $row = ConvertFrom-Json -InputObject $lines[$i]
            $stamp = $row.'@t'
            $utc = if ($stamp -is [datetime] -or $stamp -is [datetimeoffset]) {
                ([datetimeoffset]$stamp).ToUniversalTime()
            } else {
                [datetimeoffset]::Parse([string]$stamp, [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::AdjustToUniversal)
            }
            [pscustomobject]@{ Utc = $utc; Event = $row }
        }
    }
}

function Wait-Monotonic([Diagnostics.Stopwatch]$Clock, [double]$Seconds) {
    while ($Clock.Elapsed.TotalSeconds -lt $Seconds) {
        Start-Sleep -Milliseconds 100
    }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('collector-verification-' + [guid]::NewGuid().ToString('N'))
$copyDirectory = Join-Path $root 'collector'
$logDirectory = Join-Path $root 'logs'
$simulator = $null
$collector = $null
$settings = [ordered]@{}
$sampleIncrements = [ordered]@{}
$communicationAlarms = [ordered]@{}
$recoverySeconds = $null
$restoredAt = $null
$recoveryObservedAt = $null
$observationStart = $null
$observationEnd = $null
$fs01 = $false
$fs02 = $false
$commit = 'unknown'
$failure = $null
$stage = 'configuration'

try {
    if (Get-Command git -ErrorAction SilentlyContinue) {
        try {
            $revision = & git -C $PSScriptRoot rev-parse HEAD 2>$null
            if ($LASTEXITCODE -eq 0 -and $revision) { $commit = [string]$revision }
        }
        catch { $commit = 'unknown' }
    }

    $simulatorExe = (Resolve-Path -LiteralPath $SimulatorExecutable).Path
    $originalCollector = (Resolve-Path -LiteralPath $CollectorExecutable).Path
    [void][IO.Directory]::CreateDirectory($copyDirectory)
    Copy-Item -Path (Join-Path (Split-Path -Parent $originalCollector) '*') -Destination $copyDirectory -Recurse
    $collectorExe = Join-Path $copyDirectory (Split-Path -Leaf $originalCollector)
    $configPath = "$collectorExe.config"
    $config = [xml]::new()
    $config.Load($configPath)
    $logSetting = $config.SelectSingleNode('/configuration/appSettings/add[@key="LogDirectory"]')
    if ($null -eq $logSetting) { throw 'Collector configuration is missing LogDirectory.' }
    $logSetting.SetAttribute('value', $logDirectory)
    $config.Save($configPath)
    foreach ($node in $config.SelectNodes('/configuration/appSettings/add')) {
        $key = $node.GetAttribute('key')
        if ($key -match '^(PollIntervalMilliseconds|ReadTimeoutMilliseconds|JudgeIntervalMilliseconds|CommunicationFaultMilliseconds|CommunicationRecoverySuccessCount|ThresholdConsecutiveCount|BackoffMaximumMilliseconds|BackoffMaximumJitterFraction|SampleBufferCapacity|AlarmQueueCapacity|Equipment\.[1-5]\.(Host|Port|UnitId|StartAddress|WordOrder))$') {
            $settings[$key] = $node.GetAttribute('value')
        }
    }

    $stage = 'startup'
    $simulator = Start-Child $simulatorExe $true
    $ready = $simulator.StandardOutput.ReadLineAsync()
    if (-not $ready.Wait(5000) -or $ready.Result -cne 'READY') { throw 'Simulator did not report READY.' }
    $collector = Start-Child $collectorExe $false
    $warmup = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($collector.HasExited) { throw 'Collector exited during warmup.' }
        $readyEquipment = @(Read-CollectorLog $logDirectory | Where-Object {
            $_.Event.EventKind -eq 'StatusSnapshot' -and $_.Event.Count -ge 2
        } | ForEach-Object { $_.Event.EquipmentId } | Sort-Object -Unique)
        if ($readyEquipment.Count -eq 5) { break }
        Start-Sleep -Milliseconds 200
    } while ($warmup.Elapsed.TotalSeconds -lt 25)
    if ($readyEquipment.Count -ne 5) { throw 'All five devices must report samples before the fault is injected.' }

    $stage = 'FS-01'
    Send-Command $simulator 'delay 1 3000'
    $observationClock = [Diagnostics.Stopwatch]::StartNew()
    $observationStart = [datetimeoffset]::UtcNow
    $observationEnd = $observationStart.AddSeconds(60)
    Wait-Monotonic $observationClock 60
    Send-Command $simulator 'delay 1 0'
    # Allow the first snapshot after the observation end to appear.
    Wait-Monotonic $observationClock 72
    if ($collector.HasExited) { throw 'Collector exited during FS-01.' }

    $rows = @(Read-CollectorLog $logDirectory)
    $fs01 = $true
    foreach ($id in 1..5) {
        $snapshots = @($rows | Where-Object {
            $_.Event.EventKind -eq 'StatusSnapshot' -and $_.Event.EquipmentId -eq $id
        })
        if ($snapshots.Count -lt 2) { throw "Too few snapshots for equipment $id." }
        $first = $snapshots | Sort-Object { [math]::Abs(($_.Utc - $observationStart).TotalSeconds) } |
            Select-Object -First 1
        $last = $snapshots | Sort-Object { [math]::Abs(($_.Utc - $observationEnd).TotalSeconds) } |
            Select-Object -First 1
        $increment = [long]$last.Event.Count - [long]$first.Event.Count
        $alarmCount = @($rows | Where-Object {
            $_.Utc -ge $observationStart -and $_.Utc -lt $observationEnd -and
            $_.Event.EventKind -eq 'AlarmRaised' -and $_.Event.Detail -eq 'Communication' -and
            $_.Event.EquipmentId -eq $id
        }).Count
        $sampleIncrements[[string]$id] = $increment
        $communicationAlarms[[string]$id] = $alarmCount
        if (($id -eq 1 -and $alarmCount -lt 1) -or
            ($id -ne 1 -and ($increment -lt 57 -or $alarmCount -ne 0))) { $fs01 = $false }
    }

    $stage = 'FS-02'
    Send-Command $simulator 'disconnect 2 on'
    $offline = [Diagnostics.Stopwatch]::StartNew()
    Wait-Monotonic $offline 20
    $restoredAt = [datetimeoffset]::UtcNow
    $recoveryClock = [Diagnostics.Stopwatch]::StartNew()
    Send-Command $simulator 'disconnect 2 off'
    do {
        if ($collector.HasExited) { throw 'Collector exited during FS-02.' }
        $recovered = Read-CollectorLog $logDirectory | Where-Object {
            $_.Event.EventKind -eq 'PollRecovered' -and $_.Event.EquipmentId -eq 2 -and $_.Utc -ge $restoredAt
        } | Sort-Object Utc | Select-Object -First 1
        if ($null -ne $recovered) {
            $recoveryObservedAt = $recovered.Utc
            $recoverySeconds = ($recoveryObservedAt - $restoredAt).TotalSeconds
            $fs02 = $recoverySeconds -le 15
            break
        }
        if ($recoveryClock.Elapsed.TotalSeconds -ge 15.5) { break }
        Start-Sleep -Milliseconds 100
    } while ($true)
}
catch {
    $failure = "Verification stopped during $stage."
    if ($stage -eq 'FS-01') { $fs01 = $false }
    if ($stage -eq 'FS-02') { $fs02 = $false }
}
finally {
    foreach ($process in @($collector, $simulator)) {
        if ($null -eq $process) { continue }
        try {
            if (-not $process.HasExited) {
                if ($process -eq $simulator) {
                    try { Send-Command $simulator 'quit' } catch {}
                }
                if (-not $process.WaitForExit(1000)) {
                    $process.Kill()
                    $process.WaitForExit()
                }
            }
        }
        catch {
            if ($null -eq $failure) { $failure = 'A child process could not be stopped.' }
        }
        finally { $process.Dispose() }
    }

    try {
        $cleanupDirectory = if ($KeepLogs) { $copyDirectory } else { $root }
        if (Test-Path -LiteralPath $cleanupDirectory) {
            Remove-Item -LiteralPath $cleanupDirectory -Recurse -Force
        }
    }
    catch {
        if ($null -eq $failure) { $failure = 'Temporary files could not be removed.' }
    }
}

$passed = $fs01 -and $fs02 -and $null -eq $failure
[pscustomobject]@{
    Result = if ($passed) { 'PASS' } else { 'FAIL' }
    'FS-01' = if ($fs01) { 'PASS' } else { 'FAIL' }
    'FS-02' = if ($fs02) { 'PASS' } else { 'FAIL' }
    ObservationStartUtc = $observationStart
    ObservationEndUtc = $observationEnd
    SampleIncrements = $sampleIncrements
    SampleCountMethod = 'Approximation using the snapshots nearest the endpoints (10-second cadence).'
    CommunicationAlarms = $communicationAlarms
    RestoreCommandUtc = $restoredAt
    PollRecoveredUtc = $recoveryObservedAt
    RecoverySeconds = $recoverySeconds
    Commit = $commit
    Settings = $settings
    LogDirectory = $logDirectory
    LogsKept = [bool]$KeepLogs
    Error = $failure
} | ConvertTo-Json -Depth 5

if ($passed) { exit 0 }
exit 1
