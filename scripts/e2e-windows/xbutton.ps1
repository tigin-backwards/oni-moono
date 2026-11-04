# xbutton.ps1 -Match RUNHOME -Button 1|2 -X X -Y Y [-Action press|down|up] [-HoldMs MS]: posts
# WM_XBUTTONDOWN/WM_XBUTTONUP (XBUTTON1 or XBUTTON2 in the high word of wParam, the client point
# in lParam) to the visible top-level windows of the game process whose command line holds
# RUNHOME (never by name alone or by title), found with EnumWindows and GetWindowThreadProcessId.
# PostMessage only: the system's pointer, its input queue and the foreground window are not
# touched. Prints the windows it posted to.
param([string]$Match, [int]$Button = 1, [int]$X = 0, [int]$Y = 0, [string]$Action = 'press', [int]$HoldMs = 120)
if (-not $Match) { 'no -Match'; exit 2 }
if ($Button -ne 1 -and $Button -ne 2) { 'Button is 1 or 2'; exit 2 }
Add-Type -Namespace W -Name X -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool EnumWindows(EnumProc f, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool IsWindowVisible(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool PostMessage(System.IntPtr h, uint msg, System.IntPtr w, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern int GetClassName(System.IntPtr h, System.Text.StringBuilder s, int n);
public static System.Collections.Generic.List<System.IntPtr> Windows(uint pid)
{
    var found = new System.Collections.Generic.List<System.IntPtr>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) found.Add(h); return true; }, System.IntPtr.Zero);
    return found;
}
'@
$procs = @(Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'OniMoono.exe' -or $_.Name -like 'BrutalNotIncluded*') -and $_.CommandLine -and $_.CommandLine.Contains($Match) })
if ($procs.Count -ne 1) { "found $($procs.Count) game processes for $Match"; exit 3 }
$gamePid = [uint32]$procs[0].ProcessId
$windows = [W.X]::Windows($gamePid)
if ($windows.Count -eq 0) { "pid $gamePid has no visible window"; exit 4 }
$WM_XBUTTONDOWN = 0x020B; $WM_XBUTTONUP = 0x020C
$wParam = [System.IntPtr]($Button -shl 16)
$lParam = [System.IntPtr](($Y -shl 16) -bor ($X -band 0xFFFF))
function Post($msg) {
    foreach ($h in $windows) {
        $c = New-Object System.Text.StringBuilder 64
        [W.X]::GetClassName($h, $c, 64) | Out-Null
        $ok = [W.X]::PostMessage($h, $msg, $wParam, $lParam)
        "pid $gamePid hwnd 0x$($h.ToString('X')) class $c msg 0x$($msg.ToString('X')) wParam 0x$($wParam.ToString('X')) lParam 0x$($lParam.ToString('X')) posted $ok"
    }
}
if ($Action -eq 'press' -or $Action -eq 'down') { Post $WM_XBUTTONDOWN }
if ($Action -eq 'press') { Start-Sleep -Milliseconds $HoldMs }
if ($Action -eq 'press' -or $Action -eq 'up') { Post $WM_XBUTTONUP }
