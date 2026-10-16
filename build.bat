@echo off
setlocal enabledelayedexpansion
REM ============================================================
REM JSR for Z8 - One-Shot Build Script (Steps 0-6)
REM Usage: build.bat
REM
REM Produces: libs\libraw.dll          (LibRaw, JO fork nikon-he-production)
REM           libs\libraw.lib          (import lib for the wrapper)
REM           libs\libraw_wrapper.dll  (C wrapper for P/Invoke)
REM           libs\jpeg-static.lib     (libjpeg-turbo static, linked into dng_ljpeg.dll)
REM           libs\dng_ljpeg.dll       (lossless-JPEG tile encoder wrapper)
REM           libs\msvcp140.dll        (VC++ runtime, Step 3d: libraw.dll is /MD and
REM           libs\vcruntime140.dll     Windows ships no VC++ redist - the three DLLs
REM           libs\vcruntime140_1.dll   ride along app-local next to libraw.dll)
REM           src\Jsr4Z8.Core\Payload\Embedded\payload.bin + payload.manifest.json
REM                                     (backend payload pair, Step 4; gitignored)
REM           out\JSR4Z8_v^<Version^>.exe (single-file, self-contained, native DLLs
REM                                     + VC++ runtime + ~202 MB backend payload embedded)
REM Layout (normalized by Directory.Build.props):
REM   build\obj\<Project>            intermediates
REM   build\bin\<Project>\<Config>   build outputs (dev debugging)
REM   build\publish                  single-file publish staging
REM   out\                           FINAL distributable
REM Steps 0-3 are incremental: existing artifacts are skipped (delete to rebuild).
REM Step 4 always runs pack-payload: it picks the HIGHEST backend version under
REM reference\ and swaps the embedded pair in only when the manifest hash changed.
REM Prereq: VS 2022/2026 + .NET 8 SDK + git (submodules) + CMake
REM Steps 0-3 ported from Save_Z8 build.bat; Steps 4-6 mirror its Step 4
REM (single-file publish, native DLLs compressed inside the bundle).
REM ============================================================

set ROOT=%~dp0
set LIBS=%ROOT%libs
set EXT=%ROOT%external\libraw
set OUT=%ROOT%out
set PUB=%ROOT%build\publish
set PAYLOAD_EMBED=%ROOT%src\Jsr4Z8.Core\Payload\Embedded
set PAYLOAD_SRC=
rem dir /o-n = descending name order = highest version (2.3.x same-length prefix assumed)
for /f "delims=" %%d in ('dir /b /ad /o-n "%ROOT%reference\Jiangtherapee-Bridge-*" 2^>nul') do if not defined PAYLOAD_SRC set "PAYLOAD_SRC=%ROOT%reference\%%d"

echo ============================================================
echo  JSR for Z8 - One-Shot Build (native / payload / publish)
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
echo [0/6] Found VS: !VCVARS!

REM --- Step 1: submodules ---
if exist "%EXT%\src\libraw_c_api.cpp" (
    echo [1/6] Submodules OK
) else (
    echo [1/6] Initializing git submodules...
    cd /d %ROOT%
    git submodule update --init --recursive
    if errorlevel 1 (echo ERROR: git submodule failed & exit /b 1)
)

REM --- Step 2: LibRaw DLL (skip if already built) ---
if exist "%LIBS%\libraw.dll" (
    echo [2/6] libraw.dll exists, skipping. Delete libs\libraw.dll to rebuild.
    goto :wrapper
)

echo [2/6] Building LibRaw (JO fork)...
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
echo [3/6] Building libraw_wrapper.dll...
call "!VCVARS!" x64 >nul 2>&1
if not exist %LIBS% mkdir %LIBS%

cl /nologo /LD /O2 /EHsc /W0 /I%EXT% /Fo:%TEMP%\ %ROOT%native\libraw_wrapper.cpp /Fe:%LIBS%\libraw_wrapper.dll /link /LIBPATH:%LIBS% libraw.lib
if errorlevel 1 (echo ERROR: libraw_wrapper build failed & exit /b 1)
del /q "%TEMP%\libraw_wrapper.obj" %LIBS%\libraw_wrapper.exp %LIBS%\libraw_wrapper.lib 2>nul
echo      libraw_wrapper.dll built OK

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

REM --- Step 3d: VC++ runtime DLLs, app-local (always re-staged: ~0.9 MB raw, and a
REM     stale copy from another VS toolset must never survive into the bundle) ---
REM libraw.dll is linked /MD (external\libraw\Makefile.msvc), so it imports
REM MSVCP140 / VCRUNTIME140 / VCRUNTIME140_1 at load time. Windows ships neither;
REM VC++ redist is only present if some other program installed it. They are staged
REM into libs\ so the csproj glob embeds them into the single-file bundle; the host
REM extracts them into the SAME directory as libraw.dll, and coreclr loads native
REM libraries with LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR, so the imports resolve from
REM there. Toolset-matched copies (VCToolsRedistDir is set by vcvarsall above).
echo [3d] Staging VC++ runtime DLLs (app-local deployment)...
if not defined VCToolsRedistDir (
    echo ERROR: VCToolsRedistDir not set - vcvarsall did not run correctly.
    exit /b 1
)
REM for /d, not a wildcard inside the path: cmd only expands wildcards in the last component.
set CRT_DIR=
for /d %%D in ("!VCToolsRedistDir!x64\Microsoft.VC*.CRT") do if not defined CRT_DIR set CRT_DIR=%%~fD
if not defined CRT_DIR (
    echo ERROR: no x64 VC++ runtime directory under "!VCToolsRedistDir!x64" - incomplete VS install?
    exit /b 1
)
for %%F in (msvcp140.dll vcruntime140.dll vcruntime140_1.dll) do (
    if exist "%LIBS%\%%F" del /q "%LIBS%\%%F"
    copy /y "!CRT_DIR!\%%F" "%LIBS%\%%F" >nul
    if not exist "%LIBS%\%%F" (
        echo ERROR: %%F missing in "!CRT_DIR!" - incomplete VS install?
        exit /b 1
    )
)
echo      VC++ runtime DLLs staged OK

REM --- Step 4: pack backend payload into Core embedded resources (pack-payload self-decides by manifest hash) ---
if not defined PAYLOAD_SRC (
    echo ERROR: reference\Jiangtherapee-Bridge-* not found - extract the backend distribution there first.
    exit /b 1
)
echo [4/6] Packing backend payload ~30-60 s (swaps in only if the manifest hash changed)...
dotnet run --project tools\pack-payload -- "%PAYLOAD_SRC%" "%PAYLOAD_EMBED%"
if errorlevel 1 (echo ERROR: payload packing failed & exit /b 1)
REM --- Step 5: single-file publish (Release, self-contained, payload embedded) ---
cd /d %ROOT%
echo [5/6] Publishing single-file exe (embeds + compresses ~202 MB payload, takes a few minutes)...
REM Clear any inherited Platform env var (e.g. x64 from a VS developer prompt): MSBuild
REM would otherwise treat it as a global platform and break the Any CPU publish below.
set Platform=
if exist "%PUB%" rmdir /s /q "%PUB%"
dotnet publish src\Jsr4Z8.UI\Jsr4Z8.UI.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:Platform="Any CPU" ^
  -p:DebugType=none -o "%PUB%"
if errorlevel 1 (echo ERROR: publish failed & exit /b 1)

REM --- Step 6: stage distributable to out\ (version from csproj ^<Version^>) ---
set VER=
for /f "tokens=3 delims=<>" %%v in ('findstr /r /c:"^ *<Version>" src\Jsr4Z8.UI\Jsr4Z8.UI.csproj') do set VER=%%v
if "%VER%"=="" (
    echo WARNING: no ^<Version^> in csproj, falling back to 0.0.0
    set VER=0.0.0
)
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%"
copy /y "%PUB%\Jsr4Z8.UI.exe" "%OUT%\JSR4Z8_v%VER%.exe" >nul
if errorlevel 1 (echo ERROR: staging to out\ failed & exit /b 1)

set SIZE_MB=0
for %%F in ("%OUT%\JSR4Z8_v%VER%.exe") do (
    set /a SIZE_MB=%%~zF/1048576
    echo       %OUT%\JSR4Z8_v%VER%.exe = %%~zF bytes ^(!SIZE_MB! MB^)
)

echo.
echo ============================================================
echo  BUILD SUCCESSFUL
echo  Distributable: %OUT%\JSR4Z8_v%VER%.exe  (single file, !SIZE_MB! MB)
echo  Selftest:      out\JSR4Z8_v%VER%.exe --selftest [file.nef]
echo  NOTE: the packed payload remains in src\Jsr4Z8.Core\Payload\Embedded\
echo        and slows every Core build. Restore fast dev builds with
echo        del src\Jsr4Z8.Core\Payload\Embedded\payload.bin src\Jsr4Z8.Core\Payload\Embedded\payload.manifest.json
echo        (out\ is unaffected - the exe carries the payload; the next build.bat
echo        re-packs the pair automatically, no manual delete needed to refresh.)
echo ============================================================
