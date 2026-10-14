param([Parameter(Mandatory = $true)][int]$TargetProcessId)

$ErrorActionPreference = 'Stop'
# Run in a separate process so attaching to the test console never detaches the test runner.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SimulatorConsoleSignal
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);
}
'@
[void][SimulatorConsoleSignal]::FreeConsole()
if (-not [SimulatorConsoleSignal]::AttachConsole($TargetProcessId)) { exit 1 }
try {
    if (-not [SimulatorConsoleSignal]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)) { exit 2 }
    if (-not [SimulatorConsoleSignal]::GenerateConsoleCtrlEvent(0, 0)) { exit 3 }
    Start-Sleep -Milliseconds 200
}
finally { [void][SimulatorConsoleSignal]::FreeConsole() }
exit 0
