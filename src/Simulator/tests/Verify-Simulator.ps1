param([string]$Executable = (Join-Path $PSScriptRoot '../bin/Debug/net48/Simulator.exe'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:checks = 0
$script:transaction = 0
$script:processes = [Collections.Generic.List[Diagnostics.Process]]::new()
$script:clients = [Collections.Generic.List[Net.Sockets.TcpClient]]::new()
$script:reservations = [Collections.Generic.List[Net.Sockets.TcpListener]]::new()
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$testRoot = Join-Path (Split-Path $PSScriptRoot) ('obj/verification/' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
Copy-Item -Path (Join-Path (Split-Path $Executable) '*') -Destination $testRoot -Recurse
$testExe = Join-Path $testRoot 'Simulator.exe'
$configPath = $testExe + '.config'
[xml]$script:baseConfig = Get-Content -LiteralPath $configPath
$script:ports = @()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Assert-Equal($Actual, $Expected, [string]$Message) {
    Assert-True ($Actual -ceq $Expected) "$Message (expected: $Expected; actual: $Actual)"
}
function Set-Setting([xml]$Config, [string]$Key, [string]$Value) {
    $node = @($Config.configuration.appSettings.add | Where-Object { $_.key -eq $Key })
    if ($node.Count -ne 1) { throw "Setting not found: $Key" }
    $node[0].SetAttribute('value', $Value)
}
function Save-Config([hashtable]$Changes = @{}) {
    [xml]$copy = $script:baseConfig.OuterXml
    foreach ($key in $Changes.Keys) { Set-Setting $copy $key $Changes[$key] }
    $copy.Save($configPath)
}
function Start-Simulator([bool]$ExpectReady = $true) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $testExe
    $info.WorkingDirectory = $testRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    $script:processes.Add($process)
    if ($ExpectReady) {
        $ready = $process.StandardOutput.ReadLineAsync()
        if (-not $ready.Wait(5000)) { throw 'READY timeout' }
        if ($ready.Result -cne 'READY') { throw ('Startup failed: ' + $process.StandardError.ReadToEnd()) }
        $script:checks++
    }
    return $process
}
function Command($Process, [string]$Text, [bool]$ExpectError = $false, [int]$Timeout = 3000) {
    $Process.StandardInput.WriteLine($Text)
    $Process.StandardInput.Flush()
    $pending = $Process.StandardOutput.ReadLineAsync()
    if (-not $pending.Wait($Timeout)) { throw "Command timeout: $Text" }
    $line = $pending.Result
    if ($null -eq $line) { throw ("Process closed stdout during '$Text': " + $Process.StandardError.ReadToEnd()) }
    $prefix = if ($ExpectError) { 'ERR ' } else { 'OK' }
    Assert-True ($null -ne $line -and $line.StartsWith($prefix)) "Command '$Text' returned '$line'"
    return $line
}
function Stop-Simulator($Process, [bool]$Eof = $false) {
    if ($Eof) { $Process.StandardInput.Close() } else { $null = Command $Process 'quit' }
    Assert-True ($Process.WaitForExit(3000)) 'Process did not exit promptly'
    Assert-Equal $Process.ExitCode 0 'Nonzero exit code'
    Assert-Equal $Process.StandardError.ReadToEnd() '' 'Unexpected diagnostic error'
}
function Connect-Equipment([int]$Id) {
    $client = [Net.Sockets.TcpClient]::new()
    $script:clients.Add($client)
    $pending = $client.ConnectAsync('127.0.0.1', $script:ports[$Id - 1])
    if (-not $pending.Wait(2000)) { throw "Connect timeout for equipment $Id" }
    $client.GetStream().ReadTimeout = 3000
    $client.GetStream().WriteTimeout = 3000
    return $client
}
function Send-Request($Client, [int]$Address, [int]$Quantity = 5, [byte]$Function = 3) {
    $script:transaction = ($script:transaction + 1) % 65536
    [byte[]]$frame = @(
        ($script:transaction -shr 8), ($script:transaction -band 255), 0, 0, 0, 6, 1, $Function,
        ($Address -shr 8), ($Address -band 255), ($Quantity -shr 8), ($Quantity -band 255)
    )
    $Client.GetStream().Write($frame, 0, $frame.Length)
    return $script:transaction
}
function Read-Exact($Stream, [int]$Count) {
    $buffer = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $received = $Stream.Read($buffer, $offset, $Count - $offset)
        if ($received -eq 0) { throw 'Unexpected end of Modbus stream' }
        $offset += $received
    }
    return ,$buffer
}
function Read-Response($Client, [int]$Transaction) {
    $header = Read-Exact $Client.GetStream() 7
    Assert-Equal (([int]$header[0] -shl 8) -bor $header[1]) $Transaction 'Transaction id'
    Assert-Equal (([int]$header[2] -shl 8) -bor $header[3]) 0 'Protocol id'
    Assert-Equal $header[6] 1 'Unit id'
    $length = ([int]$header[4] -shl 8) -bor $header[5]
    Assert-True ($length -ge 2 -and $length -le 254) 'Invalid MBAP length'
    return ,(Read-Exact $Client.GetStream() ($length - 1))
}
function Exchange($Client, [int]$Address, [int]$Quantity = 5, [byte]$Function = 3) {
    return ,(Read-Response $Client (Send-Request $Client $Address $Quantity $Function))
}
function Assert-Hex([byte[]]$Bytes, [string]$Expected, [string]$Label) {
    Assert-Equal ([BitConverter]::ToString($Bytes)) $Expected $Label
}
function Assert-PortsReleased {
    foreach ($port in $script:ports) {
        $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
        $probe.Server.ExclusiveAddressUse = $true
        try { $probe.Start(); $script:checks++ } finally { $probe.Stop() }
    }
}
function Assert-StartupFailure([hashtable]$Changes, [string]$ExpectedMessage) {
    Save-Config $Changes
    $failed = Start-Simulator $false
    Assert-True ($failed.WaitForExit(5000)) 'Invalid configuration did not terminate'
    Assert-Equal $failed.ExitCode 1 'Invalid configuration exit code'
    Assert-Equal $failed.StandardOutput.ReadToEnd() '' 'Invalid configuration emitted READY'
    Assert-True ($failed.StandardError.ReadToEnd().Contains($ExpectedMessage)) 'Missing setting-specific error'
    Assert-PortsReleased
}

try {
    # Reserve five distinct ephemeral ports before assigning them to the copied config.
    foreach ($id in 1..5) {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $script:reservations.Add($listener)
        $script:ports += $listener.LocalEndpoint.Port
        Set-Setting $script:baseConfig "Equipment.$id.Port" ([string]$listener.LocalEndpoint.Port)
    }
    foreach ($listener in $script:reservations) { $listener.Stop() }
    Save-Config
    $process = Start-Simulator
    $connections = @(foreach ($id in 1..5) { Connect-Equipment $id })
    $tickets = @(foreach ($id in 1..5) { Send-Request $connections[$id - 1] (($id - 1) * 100) })
    foreach ($id in 1..5) {
        $expected = if ($id % 2 -eq 0) { '03-0A-00-00-41-C8-00-00-3F-80-00-01' } else { '03-0A-41-C8-00-00-3F-80-00-00-00-01' }
        Assert-Hex (Read-Response $connections[$id - 1] $tickets[$id - 1]) $expected "Equipment $id normal bytes"
    }
    Write-Output 'PASS five simultaneous equipment reads and both word orders'

    Assert-Hex (Exchange $connections[0] 4 1) '03-02-00-01' 'Last register partial read'
    Assert-Hex (Exchange $connections[1] 101 2) '03-04-41-C8-00-00' 'Unaligned partial read'
    foreach ($request in @(@(99, 1), @(104, 2), @(105, 1), @(65535, 2))) {
        Assert-Hex (Exchange $connections[1] $request[0] $request[1]) '83-02' 'Out-of-map response'
    }
    foreach ($quantity in @(0, 126)) { Assert-Hex (Exchange $connections[0] 0 $quantity) '83-03' 'Invalid quantity' }
    foreach ($function in @(1, 2, 4, 5, 6, 7, 127)) {
        Assert-Hex (Exchange $connections[0] 0 1 $function) ('{0:X2}-01' -f ($function -bor 128)) 'Unsupported function'
    }
    # Valid write-multiple requests must also be rejected, without trailing response bytes.
    foreach ($pdu in @([byte[]]@(15,0,0,0,1,1,1), [byte[]]@(16,0,0,0,1,2,0,1), [byte[]]@(23,0,0,0,1,0,0,0,1,2,0,1))) {
        [byte[]]$frame = @(0x12,0x34,0,0,0,($pdu.Length + 1),1) + $pdu
        $connections[0].GetStream().Write($frame, 0, $frame.Length)
        Assert-Hex (Read-Response $connections[0] 0x1234) ('{0:X2}-01' -f ($pdu[0] -bor 128)) 'Unsupported write-multiple function'
    }
    [byte[]]$fragmented = @(0x12,0x35,0,0,0,6,1,3,0,0,0,5)
    $connections[0].GetStream().Write($fragmented, 0, 3)
    Start-Sleep -Milliseconds 30
    $connections[0].GetStream().Write($fragmented, 3, 5)
    Start-Sleep -Milliseconds 30
    $connections[0].GetStream().Write($fragmented, 8, 4)
    Assert-Hex (Read-Response $connections[0] 0x1235) '03-0A-41-C8-00-00-3F-80-00-00-00-01' 'Fragmented request'
    [byte[]]$combined = $fragmented + $fragmented
    $connections[0].GetStream().Write($combined, 0, $combined.Length)
    foreach ($iteration in 1..2) {
        Assert-Hex (Read-Response $connections[0] 0x1235) '03-0A-41-C8-00-00-3F-80-00-00-00-01' 'Coalesced requests'
    }
    Write-Output 'PASS partial reads, boundaries, quantities and unsupported functions'

    foreach ($state in @(0, 1, 2, 3, 65535)) {
        $null = Command $process "status 1 $state"
        Assert-Hex (Exchange $connections[0] 4 1) ('03-02-{0:X2}-{1:X2}' -f ($state -shr 8), ($state -band 255)) 'Status wire value'
    }
    $before = Command $process 'show 1'
    foreach ($bad in @('', 'unknown 1', 'status 0 1', 'status 6 1', 'status 1 -1', 'status 1 65536', 'status 1 2 extra', 'high 1 yes', 'delay 1 -1', 'values 1 high 30 NaN', 'values 1 normal Infinity 1', 'values 1 wrong 25 1', 'disconnect 1 yes', 'quit extra')) {
        $null = Command $process $bad $true
        Assert-Equal (Command $process 'show 1') $before 'Invalid command mutated state'
    }
    $null = Command $process 'status 1 1'
    $null = Command $process 'high 2 on'
    Assert-Hex (Exchange $connections[1] 100) '03-0A-00-00-42-C8-00-00-41-A0-00-01' 'High values CDAB'
    $null = Command $process 'values 2 high 123.456 7.89'
    Assert-Hex (Exchange $connections[1] 100) '03-0A-E9-79-42-F6-7A-E1-40-FC-00-01' 'Nonzero low words CDAB'
    $null = Command $process 'high 2 off'
    Assert-Hex (Exchange $connections[1] 100) '03-0A-00-00-41-C8-00-00-3F-80-00-01' 'Normal values restored'
    $null = Command $process 'values 1 normal 123.456 7.89'
    Assert-Hex (Exchange $connections[0] 0) '03-0A-42-F6-E9-79-40-FC-7A-E1-00-01' 'Nonzero low words ABCD'
    $null = Command $process 'values 1 normal 25 1'
    Write-Output 'PASS runtime states, invalid-command atomicity and configurable values'

    $null = Command $process 'exception 1 on'
    Assert-Hex (Exchange $connections[0] 0) '83-04' 'Injected exception'
    Assert-Hex (Exchange $connections[0] 5 1) '83-02' 'Address exception takes precedence'
    $null = Command $process 'delay 1 350'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Assert-Hex (Exchange $connections[0] 0) '83-04' 'Delayed injected exception'
    Assert-True ($timer.ElapsedMilliseconds -ge 300) 'Delay was not applied'
    $null = Command $process 'exception 1 off'
    $timer.Restart()
    Assert-Hex (Exchange $connections[0] 0) '03-0A-41-C8-00-00-3F-80-00-00-00-01' 'Delayed normal response'
    Assert-True ($timer.ElapsedMilliseconds -ge 300) 'Normal response was not delayed'
    $null = Command $process 'delay 1 0'
    $timer.Restart()
    Assert-Hex (Exchange $connections[0] 0) '03-0A-41-C8-00-00-3F-80-00-00-00-01' 'Faults disabled'
    Assert-True ($timer.ElapsedMilliseconds -lt 1500) 'Normal read remained blocked after disabling delay'

    $null = Command $process 'delay 1 6000'
    $pendingTicket = Send-Request $connections[0] 0
    Start-Sleep -Milliseconds 100
    $timer.Restart()
    $null = Command $process 'status 1 2' $false 1500
    foreach ($id in 2..5) {
        $reply = Exchange $connections[$id - 1] (($id - 1) * 100)
        Assert-Equal $reply[0] 3 'Unaffected equipment failed'
    }
    Assert-True ($timer.ElapsedMilliseconds -lt 2500) 'Other equipment blocked by delayed request'
    $null = Command $process 'disconnect 1 on' $false 1500
    $closed = $false
    try { $closed = $connections[0].GetStream().ReadByte() -eq -1 }
    catch [IO.IOException] { $closed = $true }
    Assert-True $closed 'Old connection remains open'
    $refused = $false
    try { $unexpected = Connect-Equipment 1; $unexpected.Dispose() }
    catch { $refused = $true }
    Assert-True $refused 'Disconnected equipment accepted a new connection'
    $null = Command $process 'disconnect 1 on'
    $null = Command $process 'high 1 on'
    $null = Command $process 'delay 1 0'
    $null = Command $process 'disconnect 1 off'
    $null = Command $process 'disconnect 1 off'
    $connections[0] = Connect-Equipment 1
    Assert-Hex (Exchange $connections[0] 0) '03-0A-42-C8-00-00-41-A0-00-00-00-02' 'Recovery preserves values and status'
    $null = Command $process 'disconnect 1 on'
    $reconnectBlocker = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $script:ports[0])
    $script:reservations.Add($reconnectBlocker)
    $reconnectBlocker.Server.ExclusiveAddressUse = $true
    $reconnectBlocker.Start()
    $null = Command $process 'disconnect 1 off' $true
    Assert-True ((Command $process 'show 1').Contains('disconnected=True')) 'Failed reconnect lost disconnected state'
    $reconnectBlocker.Stop()
    $null = Command $process 'disconnect 1 off'
    $connections[0] = Connect-Equipment 1
    Assert-Hex (Exchange $connections[0] 0) '03-0A-42-C8-00-00-41-A0-00-00-00-02' 'Retry after port collision'
    Write-Output 'PASS fault combinations, device isolation, disconnect and recovery'

    # Multiple requests on the same equipment must not deadlock shutdown while waiting for its lock.
    $secondClient = Connect-Equipment 1
    $null = Command $process 'delay 1 6000'
    $null = Send-Request $connections[0] 0
    $null = Send-Request $secondClient 0
    Start-Sleep -Milliseconds 100
    Stop-Simulator $process
    Assert-PortsReleased
    Save-Config
    $process = Start-Simulator
    Stop-Simulator $process $true
    Assert-PortsReleased
    Write-Output 'PASS shutdown during pending requests, EOF and released ports'

    Save-Config @{ 'Equipment.5.StartAddress' = '65530' }
    $process = Start-Simulator
    $boundaryClient = Connect-Equipment 5
    Assert-Hex (Exchange $boundaryClient 65530) '03-0A-41-C8-00-00-3F-80-00-00-00-01' 'Maximum supported start address'
    Assert-Hex (Exchange $boundaryClient 65535 1) '83-02' 'Address 65535 outside map'
    Stop-Simulator $process
    Assert-StartupFailure @{ 'Equipment.5.StartAddress' = '65531' } 'Equipment.5.StartAddress'
    Assert-StartupFailure @{ 'Equipment.2.Port' = [string]$script:ports[0] } 'duplicates'
    Assert-StartupFailure @{ 'Equipment.2.WordOrder' = 'DCBA' } 'Equipment.2.WordOrder'
    Assert-StartupFailure @{ 'Equipment.3.NormalTemperature' = 'NaN' } 'Equipment.3.NormalTemperature'
    Assert-StartupFailure @{ 'Equipment.4.StartAddress' = '-1' } 'Equipment.4.StartAddress'
    Assert-StartupFailure @{ 'Equipment.1.Port' = '0' } 'Equipment.1.Port'
    Assert-StartupFailure @{ 'ListenAddress' = 'invalid' } 'ListenAddress'
    Assert-StartupFailure @{ 'Equipment.1.WordOrder' = '' } 'Missing setting'
    Write-Output 'PASS configuration validation and maximum start address'

    Save-Config
    $blocker = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $script:ports[2])
    $script:reservations.Add($blocker)
    $blocker.Server.ExclusiveAddressUse = $true
    $blocker.Start()
    $process = Start-Simulator $false
    Assert-True ($process.WaitForExit(5000)) 'Port collision did not terminate'
    Assert-Equal $process.ExitCode 1 'Port collision exit code'
    Assert-Equal $process.StandardOutput.ReadToEnd() '' 'Port collision emitted READY'
    Assert-True ($process.StandardError.ReadToEnd().Contains('Simulator failed:')) 'Port collision diagnostic missing'
    $blocker.Stop()
    Assert-PortsReleased
    Write-Output 'PASS partial startup rollback after port collision'

    # A hidden real console is required for a Windows Ctrl+C event (CreateNoWindow cannot receive it).
    $consoleProcess = Start-Process -FilePath $testExe -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
    $script:processes.Add($consoleProcess)
    $startupTimer = [Diagnostics.Stopwatch]::StartNew()
    $consoleReady = $false
    while (-not $consoleReady -and $startupTimer.ElapsedMilliseconds -lt 5000) {
        try { $consoleClient = Connect-Equipment 1; $consoleReady = $true }
        catch { Start-Sleep -Milliseconds 50 }
    }
    Assert-True $consoleReady 'Hidden console did not start'
    Assert-Hex (Exchange $consoleClient 0) '03-0A-41-C8-00-00-3F-80-00-00-00-01' 'Console process read'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Send-ConsoleCancel.ps1') -TargetProcessId $consoleProcess.Id
    Assert-Equal $LASTEXITCODE 0 'Sending Ctrl+C failed'
    Assert-True ($consoleProcess.WaitForExit(5000)) 'Ctrl+C did not terminate simulator'
    Assert-Equal $consoleProcess.ExitCode 0 'Ctrl+C exit code'
    Assert-PortsReleased
    Write-Output 'PASS Ctrl+C shutdown and released ports'
    Write-Output "PASS all verification scenarios ($script:checks assertions)"
}
finally {
    foreach ($client in $script:clients) { $client.Dispose() }
    foreach ($process in $script:processes) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
    foreach ($listener in $script:reservations) { $listener.Stop() }
    # Keep copied build artifacts in obj/verification for diagnosis; tracked configs remain untouched.
}
