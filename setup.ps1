# One-time setup for playing Oxygen Not Included on Moono (Windows).
#
# Finds the game through Steam's libraries, builds the player when run from a copy of the
# repository (a release has it in player\ beside this script), installs it to
# %LOCALAPPDATA%\oni-moono\player, and puts the line for the game's Steam launch options on the
# clipboard unless every Steam account here already has it (read from Steam's localconfig.vdf,
# never written). The install location stays the same, so after an update running this again is all
# that is needed.
#   setup.cmd [GAME_DIR], or powershell -ExecutionPolicy Bypass -File setup.ps1 [GAME_DIR]
param([string]$Game)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

# Explorer's "Run with PowerShell" closes the window as soon as the script ends; started that way
# (its command sets the process's execution policy), the window stays open until Enter.
$fromExplorer = [Environment]::CommandLine -match 'Set-ExecutionPolicy -Scope Process Bypass'
function Finish($code) { if ($fromExplorer) { Read-Host 'Press Enter to close' | Out-Null }; exit $code }
function Die($message) { Write-Host "setup: $message" -ForegroundColor Red; Finish 1 }
trap { Die $_ }

# The game: the argument, or the first Steam library holding it.
function Find-Game {
    $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $folders = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (-not (Test-Path $folders)) { return $null }
    foreach ($line in Get-Content -Encoding UTF8 -LiteralPath $folders) {
        if ($line -match '^\s*"path"\s*"(.*)"\s*$') {
            # Path.Combine, not Join-Path: Join-Path throws for a drive that no longer exists (a
            # library on a removed or renamed drive), and the trap made that the setup's end.
            $dir = [System.IO.Path]::Combine(($Matches[1] -replace '\\\\', '\'), 'steamapps\common\OxygenNotIncluded')
            if (Test-Path -LiteralPath ([System.IO.Path]::Combine($dir, 'OxygenNotIncluded_Data'))) { return $dir }
        }
    }
    return $null
}

# OniMoono.exe for a player built here: our native host (src\OniMoono.NativeHost), which asks
# hybrid-graphics laptops for their discrete GPU as the original game's exe does. It needs the
# Visual Studio C++ build tools, which the .NET SDK doesn't include; without them the SDK's own
# OniMoono.exe stays, which plays the same but leaves the GPU to the driver's default (a
# release's player always has ours).
function Build-NativeHost($dotnet, $built) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath }
    if (-not $vs) {
        Write-Host 'No Visual Studio C++ build tools: OniMoono.exe is the .NET SDK''s, which does not ask a laptop with two GPUs for the faster one (a release''s does).' -ForegroundColor Yellow
        return
    }
    $version = & $dotnet msbuild (Join-Path $here 'src\OniMoono.Restarter') -getProperty:BundledNETCoreAppPackageVersion
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $pack = @((Join-Path $packages "microsoft.netcore.app.host.win-x64\$version\runtimes\win-x64\native"),
              (Join-Path (Split-Path $dotnet) "packs\Microsoft.NETCore.App.Host.win-x64\$version\runtimes\win-x64\native")) |
        Where-Object { Test-Path (Join-Path $_ 'libnethost.lib') } | Select-Object -First 1
    if (-not $pack) { Die "the .NET SDK's nethost ($version) wasn't found" }
    $stage = Join-Path $env:TEMP "oni-moono-nativehost-$PID"
    New-Item -ItemType Directory -Force $stage | Out-Null
    try {
        foreach ($file in 'host.c', 'host.rc', 'OniMoono.exe.manifest', 'build.cmd') { Copy-Item (Join-Path $here "src\OniMoono.NativeHost\$file") $stage }
        Copy-Item (Join-Path $here 'src\OniMoono.Host\Moo.ico') $stage
        foreach ($file in 'nethost.h', 'hostfxr.h', 'libnethost.lib') { Copy-Item (Join-Path $pack $file) $stage }
        # The commit, as the SDK stamps it into OniMoono.dll.
        $product = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $built 'OniMoono.dll')).ProductVersion
        $commit = if ($product -match '\+(.+)$') { $Matches[1] } else { 'unknown' }
        Push-Location $stage
        $env:VCVARS64 = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
        # (stderr joined inside cmd: Windows PowerShell stops on a native command's stderr here)
        $output = & cmd /c "build.cmd $commit 2>&1"
        Pop-Location
        if (-not (Test-Path (Join-Path $stage 'OniMoono.exe'))) { $output | Write-Host; Die 'building OniMoono.exe failed' }
        Copy-Item -Force (Join-Path $stage 'OniMoono.exe') $built
    } finally {
        Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
    }
}

Write-Host 'Moono: Oxygen Not Included on its own engine (unofficial). Setup.'
if (-not $Game) { $Game = Find-Game }
if (-not $Game) { Die "Oxygen Not Included wasn't found in any Steam library; install it, or pass its directory: setup.ps1 'C:\path\to\OxygenNotIncluded'" }
if (-not (Test-Path (Join-Path $Game 'OxygenNotIncluded_Data\Managed'))) { Die "$Game isn't an Oxygen Not Included install (no OxygenNotIncluded_Data\Managed)" }
Write-Host "Game: $Game"

if (Test-Path (Join-Path $here 'player\OniMoono.exe')) {
    $built = Join-Path $here 'player' # a release
    # Extracted over a release from before the Moono name, player\ still holds that player
    # (OniBrutal.exe and its OniBrutal.* assemblies), which this one doesn't use.
    Get-ChildItem -LiteralPath $built -File -Filter 'OniBrutal.*' | Where-Object Name -like 'OniBrutal.*' | Remove-Item -Force
} elseif (-not (Test-Path (Join-Path $here 'global.json'))) {
    # Opened from inside the zip, Explorer runs this script alone from a temporary folder.
    Die 'extract the whole zip first (right-click it -> Extract All), then run setup.cmd from the extracted folder'
} else {
    # A .NET SDK of the version global.json pins (a later patch release works too): one already
    # installed, or one installed for this user (no administrator needed).
    $pinned = (Get-Content (Join-Path $here 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $band = $pinned.Substring(0, $pinned.Length - 2) # 10.0.402 -> 10.0.4
    $dotnet = $null
    $userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    foreach ($candidate in @($userDotnet, (Get-Command dotnet -ErrorAction SilentlyContinue).Source)) {
        if ($candidate -and (Test-Path $candidate) -and ((& $candidate --list-sdks) -match "^$([regex]::Escape($band))")) { $dotnet = $candidate; break }
    }
    if (-not $dotnet) {
        Write-Host "Installing the .NET SDK $pinned for this user..."
        $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
        Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
        & $installer -Version $pinned -InstallDir (Split-Path $userDotnet) | Out-Null
        Remove-Item $installer
        $dotnet = $userDotnet
    }
    $env:DOTNET_ROOT = Split-Path $dotnet
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'

    Write-Host 'Building the player (a few minutes the first time)...'
    # Self-contained: it carries its own .NET runtime.
    foreach ($project in 'src\OniMoono.Host', 'src\OniMoono.Restarter') {
        # The output is shown only if the build fails.
        $output = & $dotnet build (Join-Path $here $project) -c Release -r win-x64 --self-contained true "-p:OniGameDir=$Game" -v q -nologo 2>&1
        if ($LASTEXITCODE -ne 0) { $output | Write-Host; Die 'the build failed' }
    }
    $built = Join-Path $here 'out\bin\OniMoono.Host\release_win-x64'
    Remove-Item -Force (Join-Path $built 'Restarter'), (Join-Path $built 'FacadeGen.*') -ErrorAction SilentlyContinue
    $restarter = Join-Path $here 'out\bin\OniMoono.Restarter\release_win-x64'
    foreach ($file in 'Restarter.exe', 'Restarter.dll', 'Restarter.deps.json', 'Restarter.runtimeconfig.json') { Copy-Item (Join-Path $restarter $file) $built }
    Build-NativeHost $dotnet $built
}

# Installed to one place, so the launch options never change.
$player = Join-Path $env:LOCALAPPDATA 'oni-moono\player'
if (Get-Process -Name OniMoono, OniBrutal -ErrorAction SilentlyContinue) { Die 'the game is running on Moono; quit it first' }
if (Test-Path $player) { Remove-Item -Recurse -Force $player }
New-Item -ItemType Directory -Force (Split-Path $player) | Out-Null
Copy-Item -Recurse $built $player

$exe = Join-Path $player 'OniMoono.exe'
$launch = "`"$exe`" -- %command%"

# What each Steam account on this machine has as the game's launch options, read (never written)
# from its localconfig.vdf: Steam keeps them under Software\Valve\Steam\apps\457140, and the
# account's name under friends\PersonaName.
function Read-LaunchOptions($file) {
    $text = Get-Content -Raw -Encoding UTF8 -LiteralPath $file
    $tokens = [regex]::Matches($text, '"((?:[^"\\]|\\.)*)"|[{}]')
    $path = New-Object System.Collections.Generic.List[string]
    $key = $null; $persona = $null; $options = $null
    foreach ($t in $tokens) {
        if ($t.Value -eq '{') { $path.Add($key); $key = $null; continue }
        if ($t.Value -eq '}') { if ($path.Count) { $path.RemoveAt($path.Count - 1) }; $key = $null; continue }
        $value = $t.Groups[1].Value -replace '\\(.)', '$1'
        if ($null -eq $key) { $key = $value; continue }
        $where = ($path -join '/').ToLowerInvariant()
        if ($where -eq 'userlocalconfigstore/friends' -and $key -eq 'PersonaName') { $persona = $value }
        if ($where -eq 'userlocalconfigstore/software/valve/steam/apps/457140' -and $key -eq 'LaunchOptions') { $options = $value }
        $key = $null
    }
    [pscustomobject]@{ Persona = $persona; Options = $options }
}

$accounts = @()
$steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
if ($steam) {
    foreach ($config in Get-ChildItem -Path (Join-Path $steam 'userdata\*\config\localconfig.vdf') -ErrorAction SilentlyContinue) {
        try { $accounts += Read-LaunchOptions $config.FullName } catch { }
    }
}
# Set: the options start this player for the game (other options of the user's may be there too).
function Is-Set($options) { $options -and $options.Contains("`"$exe`"") -and $options.Contains('%command%') }
# Old: the options start the player from before the Moono name (oni-brutal's OniBrutal.exe).
function Is-Old($options) { $options -and $options -match '\\OniBrutal\.exe"' -and $options.Contains('%command%') }
# Options from before the Moono Tweaks screen: they still work, and win over the screen.
function Says-Set($a) {
    Write-Host "Steam already starts Oxygen Not Included on Moono for $($a.Persona)." -ForegroundColor Green
    if ($a.Options -match '--game-tweak|--splash|--frame-stats') {
        Write-Host "  Its launch options choose the tweaks, so the game's Moono Tweaks screen can't change those."
        Write-Host "  To choose them in the game instead, set the launch options to: $launch"
    }
}
$unset = @($accounts | Where-Object { -not (Is-Set $_.Options) })

Write-Host ''
if ($accounts.Count -gt 0 -and $unset.Count -eq 0) {
    foreach ($a in $accounts) { Says-Set $a }
    Write-Host ''
    Write-Host 'Done. Nothing else to do: press Play in Steam. The optional tweaks are on the main menu: Moono Tweaks.'
    Write-Host 'To play the original game again, clear the launch options (right-click Oxygen Not Included -> Properties).'
    Finish 0
}
foreach ($a in $accounts) {
    if (Is-Set $a.Options) { Says-Set $a }
    elseif (Is-Old $a.Options) { Write-Host "$($a.Persona): the launch options point at the old oni-brutal install; paste the new line." -ForegroundColor Yellow }
    elseif ($a.Options) { Write-Host "$($a.Persona): the launch options are set to something else: $($a.Options)" -ForegroundColor Yellow }
    else { Write-Host "$($a.Persona): no launch options yet." -ForegroundColor Yellow }
}
# On the clipboard: copied from the console, a long line picks up its wrapping as spaces.
Set-Clipboard -Value $launch
if ($accounts.Count -gt 0) { Write-Host '' }
Write-Host 'Done. The launch options are on your clipboard. In Steam: right-click Oxygen Not Included ->'
Write-Host 'Properties -> Launch Options, and paste them with Ctrl+V. They are:'
Write-Host ''
Write-Host "    $launch" -ForegroundColor Green
Write-Host ''
Write-Host 'Then press Play as usual. The optional tweaks are on the main menu: Moono Tweaks.'
Write-Host 'To play the original game again, clear the launch options.'
Finish 0
