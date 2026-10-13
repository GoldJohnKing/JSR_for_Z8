@echo off
chcp 65001 > NUL
REM Queue-runner success stub (Task 13): replays the success protocol AND produces a
REM product file at the location the real backend would - <output>\<prefix>_V15_3_5_K7_<ts>\<prefix>_product.dng
REM - so TaskQueueRunner's **/<prefix>*.dng glob finds it. output/prefix are parsed from
REM the request JSON (arg %2; arg %1 is --assistant-jsr-job), which doubles as an
REM integration check that BackendRequest.BuildJson wrote the exact field contract.
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "(Get-Content -Raw '%~2' | ConvertFrom-Json).parameters.output"`) do set "OUTDIR=%%i"
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "(Get-Content -Raw '%~2' | ConvertFrom-Json).parameters.prefix"`) do set "PREFIX=%%i"
if "%OUTDIR%"=="" exit /b 2
if "%PREFIX%"=="" exit /b 2
if not exist "%OUTDIR%" mkdir "%OUTDIR%"
set "PRODDIR=%OUTDIR%\%PREFIX%_V15_3_5_K7_1_1"
if not exist "%PRODDIR%" mkdir "%PRODDIR%"
echo fake dng bytes > "%PRODDIR%\%PREFIX%_product.dng"
echo ASSISTANT_JSR_START^|{"job_id":"stub-dng","state":"running","stage":"reconstruction","engine":"stormer7","dng":true}
echo ASSISTANT_JSR_LOG^|模拟后端启动
echo SRHQ_V12_PROGRESS^|40^|模拟阶段
echo ASSISTANT_JSR_RESULT^|{"exit_code":0,"state":"complete","log":"done"}
exit /b 0
