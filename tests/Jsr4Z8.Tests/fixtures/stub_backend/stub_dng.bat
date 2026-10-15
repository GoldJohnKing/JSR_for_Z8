@echo off
chcp 65001 > NUL
REM Queue-runner success stub (direct-invoke shape): parses --output-dir /
REM --out-prefix from its own argv - which doubles as an integration check that
REM BackendRequest.BuildArguments wrote the exact argv contract - then replays the
REM bare-line success protocol AND produces a product file where the real engine
REM would (<output>\<prefix>_V15_3_5_K7_<ts>\<prefix>_product.dng), so
REM TaskQueueRunner's **/<prefix>*.dng glob finds it.
set "OUTDIR="
set "PREFIX="
:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--output-dir" set "OUTDIR=%~2"
if /i "%~1"=="--out-prefix" set "PREFIX=%~2"
shift
goto parse
:parsed
if "%OUTDIR%"=="" exit /b 2
if "%PREFIX%"=="" exit /b 2
if not exist "%OUTDIR%" mkdir "%OUTDIR%"
set "PRODDIR=%OUTDIR%\%PREFIX%_V15_3_5_K7_1_1"
if not exist "%PRODDIR%" mkdir "%PRODDIR%"
echo fake dng bytes > "%PRODDIR%\%PREFIX%_product.dng"
echo SRHQ_V12_PROGRESS^|40^|模拟阶段
echo DONE total 1.0s
exit /b 0
