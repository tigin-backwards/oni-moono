# guard.ps1 -Match RUNHOME -Log PATH: waits for the game process whose command line holds RUNHOME
# (OniMoono.exe or BrutalNotIncluded*; never by name alone) and, while it runs, kills that one
# process if its visible frame (DWM extended frame bounds) is on monitor 1 (x < 2560 and
# y < 1440). Logs the frame and the focus owner every 2 s.
param([string]$Match, [string]$Log)
if (-not $Match) { 'no Match'; exit 2 }
Add-Type -Namespace W -Name G -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern System.IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("dwmapi.dll")]
public static extern int DwmGetWindowAttribute(System.IntPtr h, int attr, out RECT r, int size);
'@
function L($s) { Add-Content -Path $Log -Value "$(Get-Date -Format HH:mm:ss) $s" }
$deadline = (Get-Date).AddMinutes(5)
$p = $null
while (-not $p -and (Get-Date) -lt $deadline) {
    $c = Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'OniMoono.exe' -or $_.Name -like 'BrutalNotIncluded*') -and $_.CommandLine -and $_.CommandLine.Contains($Match) } | Select-Object -First 1
    if ($c) { $p = Get-Process -Id $c.ProcessId -ErrorAction SilentlyContinue }
    if (-not $p) { Start-Sleep -Milliseconds 300 }
}
if (-not $p) { L "no process"; exit 1 }
L "pid $($p.Id) $($p.ProcessName)"
$last = ''
while (-not $p.HasExited) {
    $p.Refresh()
    $h = $p.MainWindowHandle
    if ($h -ne [System.IntPtr]::Zero) {
        $r = New-Object W.G+RECT
        if ([W.G]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [W.G]::GetWindowRect($h, [ref]$r) | Out-Null }
        if ($r.Left -lt 2560 -and $r.Top -lt 1440) {
            L "window $($r.Left),$($r.Top) on monitor 1: killing $($p.Id)"
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            exit 3
        }
        $fpid = 0
        [W.G]::GetWindowThreadProcessId([W.G]::GetForegroundWindow(), [ref]$fpid) | Out-Null
        $now = "window $($r.Left),$($r.Top)-$($r.Right),$($r.Bottom) focused $($fpid -eq $p.Id)"
        if ($now -ne $last) { L $now; $last = $now }
    }
    Start-Sleep -Milliseconds 500
}
L "exited"
