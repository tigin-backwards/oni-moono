@echo off
rem Builds dupcap.exe and hdrtoggle.exe in the current folder (which holds their sources) with the Visual Studio C++
rem build tools: %VCVARS64% when set, else the newest install vswhere finds.
setlocal
if not defined VCVARS64 (
    for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VCVARS64=%%i\VC\Auxiliary\Build\vcvars64.bat"
)
if not exist "%VCVARS64%" (
    echo build.cmd: no Visual Studio C++ build tools found ^(set VCVARS64 to vcvars64.bat^) 1>&2
    exit /b 3
)
call "%VCVARS64%" >nul || exit /b 1
cl /nologo /O2 /W3 dupcap.c d3d11.lib dxgi.lib dxguid.lib user32.lib >nul || exit /b 1
if exist hdrtoggle.c cl /nologo /O2 /W3 hdrtoggle.c user32.lib >nul || exit /b 1
