<#
.SYNOPSIS
    Verifies Collector fault isolation, recovery, and Ctrl+C shutdown.
.DESCRIPTION
    Requires Windows PowerShell 5.1, built Collector, Simulator and Server
    executables, and TCP port 5443 free. Allow approximately three minutes.
    The Server stage requires a trusted HTTPS certificate binding and URL
    reservation for https://+:5443/, plus MINIMES_TEST_SQL_CONNECTION_STRING
    pointing to a test SQL Server with CREATE DATABASE permission.
#>
param(
    [string]$CollectorExecutable = (Join-Path $PSScriptRoot '../bin/Debug/net48/Collector.exe'),
    [string]$SimulatorExecutable = (Join-Path $PSScriptRoot '../../Simulator/bin/Debug/net48/Simulator.exe'),
    [switch]$KeepLogs,
    [string]$ServerExecutable = (Join-Path $PSScriptRoot '../../Server/bin/Debug/net48/Server.exe'),
    [switch]$SkipServerStage
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

function Start-Collector([string]$Executable) {
    # Ctrl+C requires a real console. Cache the handle for ExitCode on PowerShell 5.1.
    $process = Start-Process -FilePath $Executable -WorkingDirectory (Split-Path -Parent $Executable) -WindowStyle Hidden -PassThru
    [void]$process.Handle
    return $process
}

function Send-CollectorCancel($Process, [double]$TimeoutMilliseconds) {
    if ($Process.HasExited) { throw 'Collector exited before Ctrl+C.' }
    $signalStarted = [datetime]::UtcNow
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '../../Simulator/tests/Send-ConsoleCancel.ps1') -TargetProcessId $Process.Id *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Sending Ctrl+C failed.' }
    $remaining = [math]::Max(0, [math]::Min([int]::MaxValue, $TimeoutMilliseconds - $clock.Elapsed.TotalMilliseconds))
    if (-not $Process.WaitForExit([int][math]::Floor($remaining))) {
        throw 'Collector exceeded the Ctrl+C exit deadline.'
    }
    # ExitTime also covers processes that exit before the signal helper returns.
    return ($Process.ExitTime.ToUniversalTime() - $signalStarted).TotalSeconds
}

function Read-CollectorLog([string]$Directory, [switch]$RequireComplete) {
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

        if ($RequireComplete -and -not $contents.EndsWith("`n")) {
            throw 'Collector log did not end with a complete JSON line.'
        }
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

function Get-ShutdownRows([string]$Directory) {
    $rows = @(Read-CollectorLog $Directory -RequireComplete)
    if ($rows.Count -eq 0) { throw 'Collector shutdown log is empty.' }
    return $rows
}

function Assert-PortAvailable {
    $listeners = @()
    try {
        $listeners += [Net.Sockets.TcpListener]::new([Net.IPAddress]::Any, 5443)
        if ([Net.Sockets.Socket]::OSSupportsIPv6) {
            $listeners += [Net.Sockets.TcpListener]::new([Net.IPAddress]::IPv6Any, 5443)
        }
        foreach ($listener in $listeners) {
            $listener.Server.ExclusiveAddressUse = $true
            $listener.Start()
        }
    }
    catch { throw 'TCP port 5443 is unavailable.' }
    finally { foreach ($listener in $listeners) { $listener.Stop() } }
}

function Invoke-TestSql([string]$ConnectionString, [string]$Statement) {
    $connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = $Statement
            return $command.ExecuteScalar()
        }
        finally { $command.Dispose() }
    }
    finally { $connection.Dispose() }
}

function Start-TestServer([string]$Executable, [string]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.Arguments = $Arguments
    $info.WorkingDirectory = Split-Path -Parent $Executable
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables.Remove('MINIMES_TEST_SQL_CONNECTION_STRING')
    $process = [Diagnostics.Process]::Start($info)
    return [pscustomobject]@{
        Process = $process
        Output = $process.StandardOutput.ReadToEndAsync()
        ErrorOutput = $process.StandardError.ReadToEndAsync()
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
$serverDirectory = Join-Path $root 'server'
$serverLogDirectory = Join-Path $root 'server-logs'
$withServerLogDirectory = Join-Path $root 'logs-with-server'
$collectorKey = 'change-me-change-me-change-me-0001'
$dashboardKey = 'change-me-change-me-change-me-0002'
$simulator = $null
$collector = $null
$collectorWithServer = $null
$migration = $null
$server = $null
$databaseName = $null
$adminConnection = $null
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
$fs13 = $false
$exitCodeNoServer = $null
$shutdownSecondsNoServer = $null
$shutdownIncompleteCount = $null
$exitZero = 'FAIL'
$exitCodeWithServer = $null
$serverStageReason = $null
$sqlConnection = $env:MINIMES_TEST_SQL_CONNECTION_STRING
$missingSqlConnection = -not $SkipServerStage -and [string]::IsNullOrWhiteSpace($sqlConnection)
if ($SkipServerStage -or $missingSqlConnection) {
    $exitZero = 'SKIPPED'
    $serverStageReason = if ($SkipServerStage) {
        'Server stage skipped by -SkipServerStage.'
    } else {
        'MINIMES_TEST_SQL_CONNECTION_STRING is empty or unset.'
    }
}
$logsKept = $false
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
    Copy-Item -Path (Join-Path (Split-Path -Parent $originalCollector) '*') -Destination $copyDirectory -Recurse -Exclude '.env'
    Set-Content -LiteralPath (Join-Path $copyDirectory '.env') -Encoding Ascii -Value @(
        'SERVER_BASE_URL=https://localhost:5443'
        "COLLECTOR_API_KEY=$collectorKey"
    )
    $collectorExe = Join-Path $copyDirectory (Split-Path -Leaf $originalCollector)
    $configPath = "$collectorExe.config"
    $config = [xml]::new()
    $config.Load($configPath)
    $logSetting = $config.SelectSingleNode('/configuration/appSettings/add[@key="LogDirectory"]')
    if ($null -eq $logSetting) { throw 'Collector configuration is missing LogDirectory.' }
    $logSetting.SetAttribute('value', $logDirectory)
    $config.Save($configPath)
    $shutdownSetting = $config.SelectSingleNode('/configuration/appSettings/add[@key="ShutdownTimeoutMilliseconds"]')
    $shutdownMilliseconds = if ($null -eq $shutdownSetting) { 10000 } else { [int]$shutdownSetting.GetAttribute('value') }
    foreach ($node in $config.SelectNodes('/configuration/appSettings/add')) {
        $key = $node.GetAttribute('key')
        if ($key -match '^(PollIntervalMilliseconds|ReadTimeoutMilliseconds|JudgeIntervalMilliseconds|CommunicationFaultMilliseconds|CommunicationRecoverySuccessCount|ThresholdConsecutiveCount|BackoffMaximumMilliseconds|BackoffMaximumJitterFraction|SampleBufferCapacity|AlarmQueueCapacity|Equipment\.[1-5]\.(Host|Port|UnitId|StartAddress|WordOrder))$') {
            $settings[$key] = $node.GetAttribute('value')
        }
    }

    $stage = 'TCP port 5443 availability check before Collector startup'
    Assert-PortAvailable
    $stage = 'startup'
    $simulator = Start-Child $simulatorExe $true
    $ready = $simulator.StandardOutput.ReadLineAsync()
    if (-not $ready.Wait(5000) -or $ready.Result -cne 'READY') { throw 'Simulator did not report READY.' }
    $collector = Start-Collector $collectorExe
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
        $transitions = @($rows | Where-Object {
            $_.Event.EquipmentId -eq $id -and $_.Event.Detail -eq 'Communication' -and
            $_.Event.EventKind -in @('AlarmRaised', 'AlarmCleared', 'StatusReported', 'AlarmUnsentHeld', 'AlarmUnsentCancelled')
        })
        $alarmCount = 0
        for ($i = 0; $i -lt $transitions.Count; $i++) {
            $row = $transitions[$i]
            if ($row.Utc -lt $observationStart -or $row.Utc -ge $observationEnd) { continue }
            # StatusReported has no event subtype. A subsequent clear before another
            # transition confirms that the report opened the communication alarm.
            if ($row.Event.EventKind -eq 'AlarmRaised' -or
                ($row.Event.EventKind -eq 'StatusReported' -and $i + 1 -lt $transitions.Count -and
                    $transitions[$i + 1].Event.EventKind -eq 'AlarmCleared')) {
                $alarmCount++
            }
        }
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

    $stage = 'FS-13 Ctrl+C'
    $shutdownSecondsNoServer = Send-CollectorCancel $collector ($shutdownMilliseconds + 5000.0)
    $exitCodeNoServer = $collector.ExitCode
    $stage = 'FS-13 shutdown log'
    $rows = @(Get-ShutdownRows $logDirectory)
    $kinds = @($rows | ForEach-Object { $_.Event.EventKind })
    $incomplete = $rows | Where-Object { $_.Event.EventKind -eq 'ShutdownIncomplete' } | Select-Object -Last 1
    if ($null -ne $incomplete) { $shutdownIncompleteCount = $incomplete.Event.Count }
    $fs13 = $shutdownSecondsNoServer * 1000 -le ($shutdownMilliseconds + 5000.0) -and
        $exitCodeNoServer -eq 3 -and $shutdownIncompleteCount -gt 0 -and
        'ShutdownRequested' -in $kinds -and 'ShutdownCompleted' -notin $kinds

    if ($exitZero -ne 'SKIPPED') {
        $stage = 'ExitZero TCP port 5443 availability check'
        Assert-PortAvailable
        $stage = 'ExitZero Server setup'
        $originalServer = (Resolve-Path -LiteralPath $ServerExecutable).Path
        [void][IO.Directory]::CreateDirectory($serverDirectory)
        Copy-Item -Path (Join-Path (Split-Path -Parent $originalServer) '*') -Destination $serverDirectory -Recurse -Exclude '.env'
        $serverExe = Join-Path $serverDirectory (Split-Path -Leaf $originalServer)

        $stage = 'ExitZero temporary database creation'
        $sqlSettings = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($sqlConnection)
        $sqlSettings['Initial Catalog'] = 'master'
        $sqlSettings['Pooling'] = $false
        $adminConnection = $sqlSettings.ConnectionString
        $databaseName = 'MiniMES_Verify_' + [guid]::NewGuid().ToString('N')
        [void](Invoke-TestSql $adminConnection "CREATE DATABASE [$databaseName]")
        $sqlSettings['Initial Catalog'] = $databaseName
        $testConnection = $sqlSettings.ConnectionString

        $stage = 'ExitZero temporary Server configuration'
        if ($testConnection -match '[\r\n]') { throw 'Connection settings must fit on one .env line.' }
        [IO.File]::WriteAllLines((Join-Path $serverDirectory '.env'), [string[]]@(
            'SERVER_URL=https://+:5443/'
            "DB_CONNECTION_STRING=$testConnection"
            "COLLECTOR_API_KEY=$collectorKey"
            "DASHBOARD_API_KEY=$dashboardKey"
            "LOG_DIRECTORY=$serverLogDirectory"
        ), [Text.UTF8Encoding]::new($false))

        $stage = 'ExitZero Server migration'
        $migration = Start-TestServer $serverExe 'migrate'
        if (-not $migration.Process.WaitForExit(60000) -or $migration.Process.ExitCode -ne 0) {
            throw 'Server migration failed.'
        }
        $sampleCountSql = 'SELECT COUNT(*) FROM dbo.SAMPLE'
        $initialSampleCount = [long](Invoke-TestSql $testConnection $sampleCountSql)

        $stage = 'ExitZero TCP port 5443 availability check before Server startup'
        Assert-PortAvailable
        $stage = 'ExitZero Server startup'
        $server = Start-TestServer $serverExe ''
        $logSetting.SetAttribute('value', $withServerLogDirectory)
        $config.Save($configPath)
        $collectorWithServer = Start-Collector $collectorExe

        $stage = 'ExitZero waiting for samples'
        $receiveClock = [Diagnostics.Stopwatch]::StartNew()
        do {
            if ($collectorWithServer.HasExited -or $server.Process.HasExited) {
                throw 'Server or Collector exited before samples arrived.'
            }
            $stored = Read-CollectorLog $withServerLogDirectory | Where-Object {
                $_.Event.EventKind -eq 'SampleStored' -and $_.Event.Count -gt 0
            } | Select-Object -First 1
            if ($null -ne $stored) { break }
            Start-Sleep -Milliseconds 100
        } while ($receiveClock.Elapsed.TotalSeconds -lt 30)
        if ($null -eq $stored) { throw 'Server did not acknowledge samples within 30 seconds.' }

        $stage = 'ExitZero Ctrl+C'
        $shutdownSecondsWithServer = Send-CollectorCancel $collectorWithServer $shutdownMilliseconds
        $exitCodeWithServer = $collectorWithServer.ExitCode
        $stage = 'ExitZero shutdown log and database verification'
        $rows = @(Get-ShutdownRows $withServerLogDirectory)
        $kinds = @($rows | ForEach-Object { $_.Event.EventKind })
        $requested = [array]::IndexOf($kinds, 'ShutdownRequested')
        $completed = [array]::IndexOf($kinds, 'ShutdownCompleted')
        $finalSampleCount = [long](Invoke-TestSql $testConnection $sampleCountSql)
        if ($exitCodeWithServer -eq 0 -and $shutdownSecondsWithServer * 1000 -le $shutdownMilliseconds -and
            $requested -ge 0 -and $completed -gt $requested -and 'ShutdownIncomplete' -notin $kinds -and
            $finalSampleCount -gt $initialSampleCount) {
            $exitZero = 'PASS'
        } else {
            $serverStageReason = 'Collector shutdown or database sample count did not meet expectations.'
        }
    }
}
catch {
    $failure = "Verification stopped during $stage ($($_.Exception.GetType().Name))."
    if ($stage -eq 'FS-01') { $fs01 = $false }
    if ($stage -eq 'FS-02') { $fs02 = $false }
    if ($stage -like 'ExitZero TCP port*' -or
        ($stage -like 'TCP port*' -and $exitZero -ne 'SKIPPED')) {
        $serverStageReason = 'TCP port 5443 is unavailable.'
    }
}
finally {
    $cleanupErrors = @()
    $serverProcess = if ($null -ne $server) { $server.Process }
    $migrationProcess = if ($null -ne $migration) { $migration.Process }
    foreach ($process in @($collectorWithServer, $collector, $serverProcess, $migrationProcess, $simulator)) {
        if ($null -eq $process) { continue }
        try {
            if (-not $process.HasExited) {
                if ($process -eq $simulator) {
                    try { Send-Command $simulator 'quit' } catch {}
                }
                if ($process -eq $serverProcess) {
                    try {
                        $process.StandardInput.WriteLine('')
                        $process.StandardInput.Flush()
                    }
                    catch {}
                }
                if (-not $process.WaitForExit(3000)) {
                    $process.Kill()
                    if (-not $process.WaitForExit(5000)) { throw 'Process did not stop.' }
                }
            }
        }
        catch {
            $cleanupErrors += 'A child process could not be stopped.'
        }
        finally { $process.Dispose() }
    }

    # Always remove the Server copy and its temporary .env, including with -KeepLogs.
    foreach ($path in @((Join-Path $serverDirectory '.env'), $serverDirectory, $copyDirectory)) {
        try {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force
            }
        }
        catch { $cleanupErrors += 'A temporary executable directory could not be removed.' }
    }

    if ($null -ne $databaseName) {
        try {
            $dropDatabaseSql = @"
IF DB_ID(N'$databaseName') IS NOT NULL
BEGIN
    ALTER DATABASE [$databaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$databaseName];
END
"@
            [void](Invoke-TestSql $adminConnection $dropDatabaseSql)
        }
        catch { $cleanupErrors += "Temporary database $databaseName could not be removed." }
    }

    $keys = @($collectorKey, $dashboardKey)
    $keyCheckPassed = $false
    try {
        $logFiles = @(foreach ($directory in @($logDirectory, $withServerLogDirectory, $serverLogDirectory)) {
            if (Test-Path -LiteralPath $directory) {
                Get-ChildItem -LiteralPath $directory -File -Recurse | ForEach-Object { $_.FullName }
            }
        })
        $keyFound = $logFiles.Count -gt 0 -and
            (Select-String -LiteralPath $logFiles -Pattern $keys -SimpleMatch -Quiet)
        foreach ($child in @($server, $migration)) {
            if ($null -eq $child) { continue }
            foreach ($output in @($child.Output, $child.ErrorOutput)) {
                if (-not $output.Wait(1000)) { throw 'Server output capture did not complete.' }
                foreach ($key in $keys) {
                    if ($output.Result.Contains($key)) { $keyFound = $true }
                }
            }
        }
        $keyCheckPassed = -not $keyFound
        if ($keyFound) { $cleanupErrors += 'An API key was found in a log or child output.' }
    }
    catch { $cleanupErrors += 'API key absence could not be verified.' }

    try {
        if (Test-Path -LiteralPath $root) {
            if ($KeepLogs -and $keyCheckPassed -and $cleanupErrors.Count -eq 0) {
                $logsKept = $true
            } else {
                Remove-Item -LiteralPath $root -Recurse -Force
            }
        }
    }
    catch { $cleanupErrors += 'The temporary root could not be removed.' }
    $failure = (@($failure) + $cleanupErrors | Where-Object { $_ }) -join ' '
}

$basePassed = $fs01 -and $fs02 -and $fs13 -and -not $failure
$result = 'FAIL'
$scriptExitCode = 1
if ($basePassed) {
    if ($exitZero -eq 'PASS' -or ($SkipServerStage -and $exitZero -eq 'SKIPPED')) {
        $result = 'PASS'
        $scriptExitCode = 0
    }
    elseif ($missingSqlConnection) {
        $result = 'PARTIAL'
        $scriptExitCode = 4
    }
}

$report = [pscustomobject]@{
    Result = $result
    'FS-01' = if ($fs01) { 'PASS' } else { 'FAIL' }
    'FS-02' = if ($fs02) { 'PASS' } else { 'FAIL' }
    'FS-13' = if ($fs13) { 'PASS' } else { 'FAIL' }
    ExitCodeNoServer = $exitCodeNoServer
    ShutdownSecondsNoServer = $shutdownSecondsNoServer
    ShutdownIncompleteCount = $shutdownIncompleteCount
    ExitZero = $exitZero
    ExitCodeWithServer = $exitCodeWithServer
    ServerStageReason = $serverStageReason
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
    LogsKept = $logsKept
    Error = if ($failure) { $failure }
}

$json = $report | ConvertTo-Json -Depth 5
foreach ($key in $keys) {
    if ($json.IndexOf($key, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $scriptExitCode = 1
        $report.Result = 'FAIL'
        $report.Error = 'An API key was found in verification output.'
        $json = $report | ConvertTo-Json -Depth 5
        foreach ($keyToRedact in $keys) {
            $json = [regex]::Replace($json, [regex]::Escape($keyToRedact), '[REDACTED]', 'IgnoreCase')
        }
        break
    }
}
$json

exit $scriptExitCode
