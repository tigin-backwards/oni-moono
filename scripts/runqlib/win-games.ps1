# win-games.ps1 -Action list|kill|boost [-Match TEXT] | -Action killpid -Id PID -Created EPOCH: runq's
# Windows side (scripts/runqlib/winenv.py).
# list: every game process as JSON lines (pid, name, command line), read-only. kill and boost act
# only on games whose command line contains MATCH (a runq job's run home folder): never by name, as
# the user may be playing the same executables. A game process is our host (OniMoono.exe,
# OniBrutal.exe) or any Unity player (an executable with its NAME_Data folder beside it), as in
# out/local-tools/win-run.ps1. boost (by -Match, or -Id and -Created) gives the game the -Priority
# class (Normal, AboveNormal or High; default High) and turns Windows' power throttling (EcoQoS)
# off for it. killpid kills one game a runq job started (seen while the
# job held the Windows slot, under a run home of its own), by its pid and its start time (a pid
# Windows reused for another process is left alone). Nothing here moves the pointer or takes the
# focus.
param([string]$Action = 'list', [string]$Match = '', [int]$Id = 0, [long]$Created = 0, [ValidateSet('Normal', 'AboveNormal', 'High')][string]$Priority = 'High')
$hosts = @('OniMoono.exe', 'OniBrutal.exe')
function Test-Game($p) {
    if ($p.ProcessId -eq $PID) { return $false }
    if ($hosts -contains $p.Name) { return $true }
    $exe = $p.ExecutablePath
    if (-not $exe -or -not $exe.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    $dir = Split-Path -Parent $exe
    if (-not $dir) { return $false }
    try { Test-Path -LiteralPath ([IO.Path]::Combine($dir, [IO.Path]::GetFileNameWithoutExtension($exe) + '_Data\globalgamemanagers')) } catch { $false }
}
$games = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and (Test-Game $_) })
function Get-Created($p) { if ($p.CreationDate) { ([DateTimeOffset]$p.CreationDate).ToUnixTimeSeconds() } else { 0 } }
if ($Action -eq 'killpid') {
    if (-not $Id -or -not $Created) { 'no -Id or -Created'; exit 2 }
    foreach ($p in $games) { if ($p.ProcessId -eq $Id -and (Get-Created $p) -eq $Created) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue; "killed $($p.ProcessId)" } }
    exit 0
}
if ($Action -ne 'list' -and -not $Match -and -not ($Action -eq 'boost' -and $Id -and $Created)) { 'no -Match'; exit 2 }
$mine = @($games | Where-Object { ($Match -and $_.CommandLine.Contains($Match)) -or ($Id -and $_.ProcessId -eq $Id -and (Get-Created $_) -eq $Created) })
switch ($Action) {
    'list' { foreach ($p in $games) { [pscustomobject]@{ pid = $p.ProcessId; name = $p.Name; cmdline = $p.CommandLine; created = (Get-Created $p) } | ConvertTo-Json -Compress } }
    'kill' { foreach ($p in $mine) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue; "killed $($p.ProcessId)" } }
    'boost' {
        Add-Type -Namespace RunQ -Name P -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct Throttling { public uint Version; public uint ControlMask; public uint StateMask; }
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern bool SetProcessInformation(System.IntPtr process, int infoClass, ref Throttling info, int size);
'@
        foreach ($c in $mine) {
            $p = Get-Process -Id $c.ProcessId -ErrorAction SilentlyContinue
            if (-not $p) { continue }
            $p.PriorityClass = $Priority
            $t = New-Object RunQ.P+Throttling
            $t.Version = 1; $t.ControlMask = 1; $t.StateMask = 0
            $ok = [RunQ.P]::SetProcessInformation($p.Handle, 4, [ref]$t, 12)
            "boosted $($p.Id): priority $($p.PriorityClass), throttling off $ok"
        }
    }
}
