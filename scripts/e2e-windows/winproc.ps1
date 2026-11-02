# winproc.ps1 -Match RUNHOME -Action pid|kill|rect: the game process whose command line holds
# RUNHOME (never by name alone): ours (OniMoono.exe) or the original's copy (BrutalNotIncluded*).
param([string]$Match, [string]$Action = 'pid')
if (-not $Match) { 'no -Match'; exit 2 }
$procs = @(Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'OniMoono.exe' -or $_.Name -like 'BrutalNotIncluded*') -and $_.CommandLine -and $_.CommandLine.Contains($Match) })
switch ($Action) {
    'kill' { foreach ($p in $procs) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue; "killed $($p.ProcessId)" } }
    'pid'  { foreach ($p in $procs) { $p.ProcessId } }
}
