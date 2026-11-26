@echo off
rem Builds the Windows player's vma.dll and shaderc_shared.dll with MSVC and the static C runtime
rem (/MT, CMake's MultiThreaded), in the current folder, which holds vma.cpp and the unpacked
rem sources (sources.sh): vma\, vulkan-headers\, and shaderc\ with glslang, spirv-tools and
rem spirv-headers in its third_party\ (scripts/build-native-libs.sh puts them there). The DLLs
rem go to out\.
rem   build.cmd [JOBS]
rem JOBS: Ninja's parallel jobs (0 or none: Ninja's default). Needs the Visual Studio C++ build
rem tools with their CMake and Ninja (%VCVARS64% when set, else the newest install vswhere
rem finds) and Python 3 on PATH (shaderc's, glslang's and SPIRV-Tools' generated sources).
setlocal
set "jobs=%~1"
if "%jobs%"=="" set "jobs=0"
if not defined VCVARS64 (
    for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VCVARS64=%%i\VC\Auxiliary\Build\vcvars64.bat"
)
if not exist "%VCVARS64%" (
    echo build.cmd: no Visual Studio C++ build tools found ^(set VCVARS64 to vcvars64.bat^) 1>&2
    exit /b 3
)
call "%VCVARS64%" >nul || exit /b 1
if not exist out mkdir out
rem vma.dll: VMA's header-only implementation, its C API exported (vma.cpp). /Brepro: no
rem timestamps, so a build of the same sources is the same.
cl /nologo /Brepro /O2 /MT /EHsc /std:c++17 /W3 /GS /guard:cf /DNDEBUG /Ivma\include /Ivulkan-headers\include /LD vma.cpp /Feout\vma.dll /link /Brepro /GUARD:CF /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA || exit /b 1
rem shaderc_shared.dll: shaderc with glslang and SPIRV-Tools linked in, as upstream builds it
rem (SHADERC_ENABLE_SHARED_CRT off: the static runtime); no tests, tools or examples. Not
rem SHADERC_SKIP_INSTALL: shaderc then turns glslang's install on (a generator expression
rem where glslang reads a boolean) without SPIRV-Tools', and CMake fails to generate.
cmake -G Ninja -S shaderc -B b -DCMAKE_BUILD_TYPE=Release -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded ^
    -DSHADERC_ENABLE_SHARED_CRT=OFF -DSHADERC_SKIP_TESTS=ON -DSHADERC_SKIP_EXAMPLES=ON -DSHADERC_SKIP_EXECUTABLES=ON ^
    -DSHADERC_SKIP_COPYRIGHT_CHECK=ON -DSHADERC_ENABLE_WERROR_COMPILE=OFF ^
    -DSPIRV_SKIP_TESTS=ON -DSPIRV_SKIP_EXECUTABLES=ON -DSPIRV_WERROR=OFF ^
    -DENABLE_GLSLANG_BINARIES=OFF -DGLSLANG_TESTS=OFF || exit /b 1
if "%jobs%"=="0" (
    ninja -C b shaderc_shared || exit /b 1
) else (
    ninja -C b -j %jobs% shaderc_shared || exit /b 1
)
copy /y b\libshaderc\shaderc_shared.dll out\ >nul || exit /b 1
