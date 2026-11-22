@echo off
rem Builds caps.exe and probe_native.dll (tools/D3d11Native) with MSVC in the current folder,
rem which holds the sources. renderdoc_app.h comes from RenderDoc's install
rem (%RENDERDOC_DIR%, default C:\Program Files\RenderDoc). The C++ build tools as
rem src\OniMoono.NativeHost\build.cmd finds them (%VCVARS64% or vswhere).
setlocal
if not defined RENDERDOC_DIR set "RENDERDOC_DIR=C:\Program Files\RenderDoc"
if not defined VCVARS64 (
    for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VCVARS64=%%i\VC\Auxiliary\Build\vcvars64.bat"
)
if not exist "%VCVARS64%" ( echo build.cmd: no Visual Studio C++ build tools found 1>&2 & exit /b 3 )
call "%VCVARS64%" >nul || exit /b 1
cl /nologo /O2 /W4 /WX /MT /EHsc caps.cpp /Fecaps.exe || exit /b 1
cl /nologo /std:c17 /O2 /W4 /WX /MT /LD /I "%RENDERDOC_DIR%" probe_native.c /Feprobe_native.dll || exit /b 1
