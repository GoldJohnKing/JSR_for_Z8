@echo off
chcp 65001 > NUL
REM Long-running stub: heartbeats via a GRANDCHILD process (the tree shape of the
REM real engine's invocation: parent + same-argv child + unnamed third grandchild,
REM probe §7.3), so cancellation must keep the /T tree kill. The heartbeat file is
REM derived from the stub's OWN argv --output-dir (unique per run: the runner
REM spec's output dir / the queue's GUID job dir). The env var below is set INSIDE
REM this cmd instance for the powershell grandchild alone - the test process never
REM touches process-global environment: xUnit runs test classes in parallel, and a
REM shared env var made stub_kill runs from different classes race on one file.
set "OUTDIR="
:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--output-dir" set "OUTDIR=%~2"
shift
goto parse
:parsed
if "%OUTDIR%"=="" exit /b 2
echo SRHQ_V12_PROGRESS^|42^|模拟阶段
REM heartbeat bounded to 2 minutes: an aborted test must not leak an immortal grandchild.
set "JSR4Z8_TEST_TICK=%OUTDIR%\tick"
start "" /B powershell -NoProfile -ExecutionPolicy Bypass -Command "$d=(Get-Date).AddMinutes(2);while((Get-Date) -lt $d){[IO.File]::WriteAllText($env:JSR4Z8_TEST_TICK,(Get-Date).ToString('o'));Start-Sleep -Milliseconds 250}" > NUL 2>&1
ping -n 30 127.0.0.1 > NUL
echo SRHQ_V12_PROGRESS^|100^|模拟完成
exit /b 0
