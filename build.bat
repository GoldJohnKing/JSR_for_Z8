@echo off
setlocal enabledelayedexpansion
REM ============================================================
REM JSR for Z8 - Native Library Build Script (Steps 0-3)
REM Usage: build.bat
REM
REM Produces: libs\libraw.dll          (LibRaw, JO fork nikon-he-production)
REM           libs\libraw.lib          (import lib for the wrapper)
REM           libs\libraw_wrapper.dll  (C wrapper for P/Invoke)
REM           libs\jpeg-static.lib     (libjpeg-turbo static, linked into dng_ljpeg.dll)
REM           libs\dng_ljpeg.dll       (lossless-JPEG tile encoder wrapper)
REM Layout (normalized by Directory.Build.props):
REM   build\obj\<Project>            intermediates
REM   build\bin\<Project>\<Config>   build outputs (dev debugging)
REM libs\ is gitignored. Steps 0-3 are incremental: existing artifacts
REM are skipped (delete to rebuild). Publish (Step 4+) comes in a later task.
REM Prereq: VS 2022/2026 + .NET 8 SDK + git (submodules) + CMake
REM Ported from Save_Z8 build.bat Steps 0-3.
REM ============================================================

set ROOT=%~dp0
set LIBS=%ROOT%libs
set EXT=%ROOT%external\libraw

echo ============================================================
echo  JSR for Z8 - Native Build (libraw / libjpeg-turbo / wrappers)
echo ============================================================

REM --- Step 0: prerequisites ---
where dotnet >nul 2>&1 || (echo ERROR: dotnet not found. Install .NET 8 SDK. & exit /b 1)

set VCVARS=
for %%y in (18 17) do (
    for %%e in (Enterprise Community Professional BuildTools) do (
        if exist "C:\Program Files\Microsoft Visual Studio\%%y\%%e\VC\Auxiliary\Build\vcvarsall.bat" (
            if "!VCVARS!"=="" set VCVARS=C:\Program Files\Microsoft Visual Studio\%%y\%%e\VC\Auxiliary\Build\vcvarsall.bat
        )
    )
)
if "!VCVARS!"=="" (
    echo ERROR: Visual Studio vcvarsall.bat not found.
    exit /b 1
)
echo [0/3] Found VS: !VCVARS!

REM --- Step 1: submodules ---
if exist "%EXT%\src\libraw_c_api.cpp" (
    echo [1/3] Submodules OK
) else (
    echo [1/3] Initializing git submodules...
    cd /d %ROOT%
    git submodule update --init --recursive
    if errorlevel 1 (echo ERROR: git submodule failed & exit /b 1)
)

REM --- Step 2: LibRaw DLL (skip if already built) ---
if exist "%LIBS%\libraw.dll" (
    echo [2/3] libraw.dll exists, skipping. Delete libs\libraw.dll to rebuild.
    goto :wrapper
)

echo [2/3] Building LibRaw (JO fork)...
call "!VCVARS!" x64 >nul 2>&1
cd /d %EXT%
if not exist bin mkdir bin
if not exist lib mkdir lib
if not exist object mkdir object

nmake /f Makefile.msvc bin\libraw.dll /NOLOGO
if errorlevel 1 (echo ERROR: LibRaw build failed & exit /b 1)

if not exist %LIBS% mkdir %LIBS%
copy /y bin\libraw.dll %LIBS%\ >nul
copy /y lib\libraw.lib %LIBS%\ >nul
echo       libraw.dll built OK

:wrapper
REM --- Step 3: wrapper DLL (always rebuilt - small and fast) ---
cd /d %ROOT%
echo [3/3] Building libraw_wrapper.dll...
call "!VCVARS!" x64 >nul 2>&1
if not exist %LIBS% mkdir %LIBS%

cl /nologo /LD /O2 /EHsc /W0 /I%EXT% /Fo:%TEMP%\ %ROOT%native\libraw_wrapper.cpp /Fe:%LIBS%\libraw_wrapper.dll /link /LIBPATH:%LIBS% libraw.lib
if errorlevel 1 (echo ERROR: libraw_wrapper build failed & exit /b 1)
del /q "%TEMP%\libraw_wrapper.obj" %LIBS%\libraw_wrapper.exp %LIBS%\libraw_wrapper.lib 2>nul
echo       libraw_wrapper.dll built OK

REM --- Step 3b: libjpeg-turbo static lib (skip if already built) ---
if exist "%LIBS%\jpeg-static.lib" (
    echo [3b] jpeg-static.lib exists, skipping.
    goto :lje
)
echo [3b] Building libjpeg-turbo...
if not exist "%LIBS%\include_dngljpeg" mkdir "%LIBS%\include_dngljpeg"
"C:\Program Files\CMake\bin\cmake.exe" -S external\libjpeg-turbo -B build\turbo -G "NMake Makefiles" -DENABLE_SHARED=OFF -DWITH_SIMD=OFF -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 (echo ERROR: cmake configure failed & exit /b 1)
"C:\Program Files\CMake\bin\cmake.exe" --build build\turbo --config Release
if errorlevel 1 (echo ERROR: turbo build failed & exit /b 1)
copy /y build\turbo\jpeg-static.lib "%LIBS%\" >nul
copy /y build\turbo\jconfig.h "%LIBS%\include_dngljpeg\" >nul
copy /y external\libjpeg-turbo\src\jmorecfg.h "%LIBS%\include_dngljpeg\" >nul
copy /y external\libjpeg-turbo\src\jpeglib.h "%LIBS%\include_dngljpeg\" >nul
copy /y external\libjpeg-turbo\src\jerror.h "%LIBS%\include_dngljpeg\" >nul
echo      jpeg-static.lib built OK

:lje
REM --- Step 3c: dng_ljpeg.dll lossless-JPEG encoder wrapper (always rebuilt) ---
cd /d %ROOT%
cl /nologo /LD /O2 /EHsc /W3 /I%LIBS%\include_dngljpeg native\dng_ljpeg_wrapper.cpp /Fe:%LIBS%\dng_ljpeg.dll /Fo:%TEMP%\ /link /LIBPATH:%LIBS% jpeg-static.lib
if errorlevel 1 (echo ERROR: dng_ljpeg build failed & exit /b 1)
del /q %LIBS%\dng_ljpeg.exp %LIBS%\dng_ljpeg.lib 2>nul
echo      dng_ljpeg.dll built OK

echo.
echo ============================================================
echo  NATIVE BUILD SUCCESSFUL
echo  Artifacts: %LIBS%\libraw.dll, libraw_wrapper.dll, dng_ljpeg.dll
echo ============================================================
