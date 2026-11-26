@echo off
rem Compiles com_check.cpp in the current folder, never runs it: tools/ComCheckGen's check of
rem OniMoono.Desktop's COM, struct, enum and DLL declarations against the Windows SDK headers. It
rem compiles twice, with the C vtables (CINTERFACE) and with the C++ classes (COM_CHECK_IIDS); a
rem failed static_assert, a difference from the headers, fails it. Then each "// export: DLL NAME"
rem line of com_check.cpp is looked up in that DLL's export table in System32 (dumpbin /exports,
rem learn.microsoft.com/cpp/build/reference/exports): a DLL that does not export the function the
rem declaration names fails it too. Called by build.cmd (with the build tools set up) and
rem scripts/com-check.sh; finds the tools as build.cmd does when cl is not on PATH. The last line
rem names the Windows SDK and MSVC versions used (vcvars64's choice, not pinned).
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
for /f "tokens=3,4" %%a in ('findstr /b /c:"// export: " com_check.cpp') do call :export %%a %%b || exit /b 1
for /f "tokens=*" %%v in ('ver') do set OSVER=%%v
echo com_check.cpp: every declaration matches the Windows SDK headers (Windows SDK %WindowsSDKVersion:\=%, MSVC %VCToolsVersion%; exports of %OSVER%)
exit /b 0

:export
dumpbin /nologo /exports "%SystemRoot%\System32\%1" > com_check_exports.txt || (
    echo com_check.cmd: dumpbin could not read %SystemRoot%\System32\%1 1>&2
    exit /b 1
)
findstr /r /c:" %2$" /c:" %2 (forwarded" com_check_exports.txt >nul || (
    echo com_check.cmd: error: %1 does not export %2 1>&2
    exit /b 1
)
exit /b 0
