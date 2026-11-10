@echo off
rem Builds OniMoono.exe (host.c, crash.c, crash_dialog.cpp, fmod_heap.c) with MSVC, in the current folder, which
rem holds the sources (*.c, *.cpp, *.h), host.rc, the manifests, Moo.ico, the crash window's fonts
rem (fonts\*.ttf, copied flat), and from the .NET SDK's
rem Microsoft.NETCore.App.Host.win-x64 pack nethost.h, hostfxr.h and libnethost.lib
rem (scripts/build-windows.sh and setup.ps1 copy them there).
rem   build.cmd COMMIT
rem The Visual Studio C++ build tools: %VCVARS64% when set, else the newest install vswhere finds.
setlocal
set "commit=%~1"
if "%commit%"=="" set "commit=unknown"
if not defined VCVARS64 (
    for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VCVARS64=%%i\VC\Auxiliary\Build\vcvars64.bat"
)
if not exist "%VCVARS64%" (
    echo build.cmd: no Visual Studio C++ build tools found ^(set VCVARS64 to vcvars64.bat^) 1>&2
    exit /b 3
)
call "%VCVARS64%" >nul || exit /b 1
echo #define MOONO_PRODUCT_VERSION "1.0.0+%commit%"> version.h
rem /MT: the static C runtime, so the exe needs no Visual C++ redistributable. /guard:cf, as the
rem SDK's apphost has (DllCharacteristics GUARD_CF).
rem /utf-8: the crash window's Chinese strings. /Brepro: no timestamps in the objects and the exe
rem (its PE timestamp is a hash of its contents), so a build of the same sources is the same.
cl /nologo /Brepro /O2 /W4 /WX /MT /GS /guard:cf /utf-8 /DUNICODE /D_UNICODE /c host.c crash.c crash_dialog.cpp fmod_heap.c || exit /b 1
rem FMOD's allocator (fmod_heap.c) as the exe has it, against its unit tests: a failure fails the build.
cl /nologo /O2 /W4 /WX /MT /Fefmod_heap_test.exe fmod_heap_test.c fmod_heap.obj || exit /b 1
fmod_heap_test.exe || exit /b 1
rc /nologo /fo host.res host.rc || exit /b 1
link /nologo /Brepro /OUT:OniMoono.exe /SUBSYSTEM:WINDOWS /ENTRY:wmainCRTStartup /MANIFEST:NO /GUARD:CF /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA ^
    host.obj crash.obj crash_dialog.obj fmod_heap.obj host.res libnethost.lib shell32.lib ole32.lib advapi32.lib user32.lib gdi32.lib || exit /b 1
