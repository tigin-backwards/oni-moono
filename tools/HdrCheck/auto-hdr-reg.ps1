# Helper of auto-hdr.sh (Windows PowerShell 5.1): saves, sets and restores the HKCU values that
# switch Auto HDR on, and finds windows. HKCU only; never HKLM, never elevated.
#   -Action save    -State FILE -Exes 'A.exe|B.exe'   the values below as they are (refuses an existing FILE)
#   -Action apply   -State FILE -Exes ... -Phase global|force
#   -Action restore -State FILE                        every saved value back as it was, keys created removed
#   -Action show    -State FILE                        the values as they are now
#   -Action rect    -Match TEXT -Proc PATTERN          "X Y W H" of the visible main window (DWM frame bounds)
#   -Action overlays                                   visible top-level windows of Game Bar and the shell
# The values: UserGpuPreferences\DirectXUserGlobalSettings; UserGpuPreferences\<exe path> per exe;
# Direct3D\<exe name>\Name and \D3DBehaviors per exe (ForceAutoHDR's method, phase force only).
# -Root (a test's scratch key) replaces HKCU:\Software\Microsoft for the values.
param([string]$Action, [string]$State = '', [string]$Exes = '', [string]$Phase = '', [string]$Match = '', [string]$Proc = '', [string]$Root = 'HKCU:\Software\Microsoft')
$ErrorActionPreference = 'Stop'
$gpu = "$Root\DirectX\UserGpuPreferences"
$d3d = "$Root\Direct3D"

function Load { Get-Content -Raw -LiteralPath $State | ConvertFrom-Json }
function ExeList($s) { @($s.Split('|') | Where-Object { $_ }) }
function Sub($exe) { "$d3d\" + [IO.Path]::GetFileNameWithoutExtension($exe) }
function Targets($exes) {
    $t = @([pscustomobject]@{ Key = $gpu; Name = 'DirectXUserGlobalSettings' })
    foreach ($e in $exes) {
        $t += [pscustomobject]@{ Key = $gpu; Name = $e }
        $t += [pscustomobject]@{ Key = (Sub $e); Name = 'Name' }
        $t += [pscustomobject]@{ Key = (Sub $e); Name = 'D3DBehaviors' }
    }
    $t
}
# Keys, parents first.
function KeyList($exes) { @($gpu, $d3d) + @($exes | ForEach-Object { Sub $_ }) }
function Read1($key, $name) {
    if (-not (Test-Path -LiteralPath $key)) { return $null }
    $k = Get-Item -LiteralPath $key
    if ($k.GetValueNames() -notcontains $name) { return $null }
    [pscustomobject]@{ Kind = $k.GetValueKind($name).ToString(); Data = $k.GetValue($name, $null, 'DoNotExpandEnvironmentNames') }
}
function Describe($key, $name) {
    $v = Read1 $key $name
    $short = $key.Replace($Root + '\', 'HKCU\...\')
    if ($null -eq $v) { "$short [$name]: absent" } else { "$short [$name]: $($v.Kind) '$($v.Data)'" }
}
# KEY=VALUE tokens merged into a ';'-separated string: each named token replaced or appended.
function Merge($s, $tokens) {
    $parts = @()
    if ($s) { $parts = @($s.Split(';') | Where-Object { $_.Trim() }) }
    foreach ($k in $tokens.Keys) {
        $parts = @($parts | Where-Object { $_.Split('=')[0].Trim() -ne $k }) + "$k=$($tokens[$k])"
    }
    ($parts -join ';') + ';'
}
function SetString($key, $name, $data) {
    if (-not (Test-Path -LiteralPath $key)) { [void](New-Item -Path $key -Force) }
    [void](New-ItemProperty -LiteralPath $key -Name $name -PropertyType String -Value $data -Force)
}

switch ($Action) {
    'save' {
        if (Test-Path -LiteralPath $State) { throw "state $State exists: not overwritten" }
        $list = ExeList $Exes
        $values = @(foreach ($t in Targets $list) {
            $v = Read1 $t.Key $t.Name
            [pscustomobject]@{ Key = $t.Key; Name = $t.Name; Existed = ($null -ne $v); Kind = $(if ($v) { $v.Kind } else { '' }); Data = $(if ($v) { $v.Data } else { $null }) }
        })
        $keys = @(foreach ($k in KeyList $list) { [pscustomobject]@{ Key = $k; Existed = (Test-Path -LiteralPath $k) } })
        [pscustomobject]@{ Exes = $list; Values = $values; Keys = $keys } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $State -Encoding UTF8
        foreach ($v in $values) { 'saved: ' + (Describe $v.Key $v.Name) }
    }
    'apply' {
        $s = Load
        if ($Phase -eq 'global') {
            # The tokens Windows' own toggle leaves (MajorGeeks; MaaEnd #7513's log); others kept.
            $cur = Read1 $gpu 'DirectXUserGlobalSettings'
            SetString $gpu 'DirectXUserGlobalSettings' (Merge $(if ($cur) { [string]$cur.Data } else { '' }) ([ordered]@{ AutoHDREnable = '1'; SwapEffectUpgradeEnable = '1' }))
            'set: ' + (Describe $gpu 'DirectXUserGlobalSettings')
        } elseif ($Phase -eq 'force') {
            # ForceAutoHDR (7gxycn08/ForceAutoHDR, ForceAutoHDR.py add_exe_to_reg), its strength left out.
            foreach ($e in $s.Exes) {
                $cur = Read1 $gpu $e
                SetString $gpu $e (Merge $(if ($cur) { [string]$cur.Data } else { '' }) ([ordered]@{ AppStatus = '1'; AutoHDREnable = '2097' }))
                SetString (Sub $e) 'Name' ([IO.Path]::GetFileName($e))
                SetString (Sub $e) 'D3DBehaviors' 'BufferUpgradeOverride=1;BufferUpgradeEnable10Bit=1'
                'set: ' + (Describe $gpu $e); 'set: ' + (Describe (Sub $e) 'Name'); 'set: ' + (Describe (Sub $e) 'D3DBehaviors')
            }
        } else { throw "unknown phase '$Phase'" }
    }
    'restore' {
        $s = Load
        $bad = 0
        foreach ($v in $s.Values) {
            try {
                if ($v.Existed) {
                    if (-not (Test-Path -LiteralPath $v.Key)) { [void](New-Item -Path $v.Key -Force) }
                    $data = $v.Data
                    if ($v.Kind -eq 'DWord') { $data = [int]$data } elseif ($v.Kind -eq 'QWord') { $data = [long]$data } elseif ($v.Kind -eq 'MultiString') { $data = [string[]]$data }
                    [void](New-ItemProperty -LiteralPath $v.Key -Name $v.Name -PropertyType $v.Kind -Value $data -Force)
                } elseif ((Test-Path -LiteralPath $v.Key) -and ((Get-Item -LiteralPath $v.Key).GetValueNames() -contains $v.Name)) {
                    Remove-ItemProperty -LiteralPath $v.Key -Name $v.Name
                }
            } catch { "restore failed: $($v.Key) [$($v.Name)]: $_"; $bad++ }
        }
        # Keys this run created, children first.
        $created = @($s.Keys | Where-Object { -not $_.Existed })
        [array]::Reverse($created)
        foreach ($k in $created) {
            try { if (Test-Path -LiteralPath $k.Key) { Remove-Item -LiteralPath $k.Key -Recurse } } catch { "restore failed: key $($k.Key): $_"; $bad++ }
        }
        # Read back and compare with what was saved.
        foreach ($v in $s.Values) {
            $now = Read1 $v.Key $v.Name
            $same = if ($v.Existed) { $now -and $now.Kind -eq $v.Kind -and ("$($now.Data)" -eq "$($v.Data)") } else { $null -eq $now }
            if (-not $same) { $bad++ }
            $(if ($same) { 'restored: ' } else { 'MISMATCH: ' }) + (Describe $v.Key $v.Name) + $(if ($v.Existed) { " (saved $($v.Kind) '$($v.Data)')" } else { ' (saved absent)' })
        }
        foreach ($k in $s.Keys) {
            $now = Test-Path -LiteralPath $k.Key
            if ($now -ne $k.Existed) { $bad++; "MISMATCH: key $($k.Key) exists $now, saved $($k.Existed)" }
        }
        if ($bad) { "registry restore: $bad problem(s)"; exit 1 } else { 'registry restore: every value as saved' }
    }
    'show' { $s = Load; foreach ($v in $s.Values) { 'now: ' + (Describe $v.Key $v.Name) } }
    'rect' {
        Add-Type -Namespace AHdr -Name W -MemberDefinition '[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(System.IntPtr h, int a, out RECT r, int s); [DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h); public struct RECT { public int L, T, R, B; }'
        $p = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$Match*" -and $_.Name -like $Proc } | Select-Object -First 1
        if ($p) {
            $g = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
            if ($g -and $g.MainWindowHandle -ne [IntPtr]::Zero -and [AHdr.W]::IsWindowVisible($g.MainWindowHandle)) {
                # DWMWA_EXTENDED_FRAME_BOUNDS (9): the visible frame, without the invisible resize border.
                $r = New-Object AHdr.W+RECT
                if ([AHdr.W]::DwmGetWindowAttribute($g.MainWindowHandle, 9, [ref]$r, 16) -eq 0) { "$($r.L) $($r.T) $($r.R - $r.L) $($r.B - $r.T)" }
            }
        }
    }
    'overlays' {
        Add-Type -Namespace AHdr -Name E -MemberDefinition @'
public delegate bool Cb(System.IntPtr h, System.IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(Cb cb, System.IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(System.IntPtr h, int a, out RECT r, int s);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(System.IntPtr h, int a, out int v, int s);
public struct RECT { public int L, T, R, B; }
'@
        $names = @{}
        Get-Process | ForEach-Object { $names[[uint32]$_.Id] = $_.ProcessName }
        $script:rows = @()
        $cb = [AHdr.E+Cb]{ param($h, $l)
            if ([AHdr.E]::IsWindowVisible($h)) {
                $procId = [uint32]0; [void][AHdr.E]::GetWindowThreadProcessId($h, [ref]$procId)
                $pn = $names[$procId]
                # Only Game Bar's and the shell's windows (notifications, overlays), never the user's own.
                if ($pn -match 'GameBar|Xbox|ShellExperienceHost|ShellHost') {
                    $cloaked = 0; [void][AHdr.E]::DwmGetWindowAttribute($h, 14, [ref]$cloaked, 4)
                    $r = New-Object AHdr.E+RECT; [void][AHdr.E]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
                    $t = New-Object System.Text.StringBuilder 256; [void][AHdr.E]::GetWindowText($h, $t, 256)
                    $c = New-Object System.Text.StringBuilder 256; [void][AHdr.E]::GetClassName($h, $c, 256)
                    if ($cloaked -eq 0 -and ($r.R - $r.L) -gt 0) { $script:rows += "$pn '$t' class $c at $($r.L),$($r.T) $($r.R - $r.L)x$($r.B - $r.T)" }
                }
            }
            $true }
        [void][AHdr.E]::EnumWindows($cb, [IntPtr]::Zero)
        if ($script:rows.Count) { $script:rows } else { 'none' }
    }
    default { throw "unknown action '$Action'" }
}
