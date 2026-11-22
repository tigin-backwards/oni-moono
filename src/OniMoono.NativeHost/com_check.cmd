@echo off
rem Compiles com_check.cpp in the current folder, never runs it: tools/ComCheckGen's check of
rem OniMoono.Desktop's COM, struct, enum and DLL declarations against the Windows SDK headers. It
rem compiles twice, with the C vtables (CINTERFACE) and with the C++ classes (COM_CHECK_IIDS); a
rem failed static_assert, a difference from the headers, fails it. Called by build.cmd (with the
rem build tools set up) and scripts/com-check.sh; finds the tools as build.cmd does when cl is not
rem on PATH.
setlocal
where cl >nul 2>nul && goto compile
if not defined VCVARS64 (
    for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VCVARS64=%%i\VC\Auxiliary\Build\vcvars64.bat"
)
if not exist "%VCVARS64%" (
    echo com_check.cmd: no Visual Studio C++ build tools found ^(set VCVARS64 to vcvars64.bat^) 1>&2
    exit /b 3
)
call "%VCVARS64%" >nul || exit /b 1
:compile
cl /nologo /std:c++17 /W4 /WX /Zs com_check.cpp || exit /b 1
cl /nologo /std:c++17 /W4 /WX /Zs /DCOM_CHECK_IIDS com_check.cpp || exit /b 1
echo com_check.cpp: every declaration matches the Windows SDK headers
