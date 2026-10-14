@echo off
chcp 65001 > NUL
if "%JSR4Z8_TEST_TICK%"=="" exit /b 1
REM Orphan scenario: spawn a DETACHED heartbeat child (Start-Process gives it its own
REM hidden console, so it does NOT inherit this harness's redirected stdout pipe and
REM cannot hold the runner's readers open), report that child's pid in the START
REM envelope the way the real bridge reports jsr_backend's pid, then exit WITHOUT a
REM RESULT envelope. The heartbeat self-terminates after 2 minutes so an aborted test
REM cannot leak an immortal process.
set "PIDFILE=%JSR4Z8_TEST_TICK%.pid"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$c='$t=$env:JSR4Z8_TEST_TICK;$s=Get-Date;while(((Get-Date)-$s).TotalSeconds -lt 120){[IO.File]::WriteAllText($t,$t);Start-Sleep -Milliseconds 250}';(Start-Process powershell -ArgumentList '-NoProfile','-Command',$c -WindowStyle Hidden -PassThru).Id" > "%PIDFILE%"
set /p ORPHAN_PID=<"%PIDFILE%"
if "%ORPHAN_PID%"=="" exit /b 2
REM Wait (bounded: ~1s polls, ~5s budget) until the child has proven life (first tick
REM write) before dying without RESULT. Under parallel-test load a cold powershell child
REM can take seconds to reach its first write; dying earlier lets the runner's START-pid
REM taskkill land before any tick exists (observed flake: "orphan child never started").
set /a TRIES=0
:waittick
if exist "%JSR4Z8_TEST_TICK%" goto ticked
if %TRIES% GEQ 5 goto ticked
set /a TRIES=TRIES+1
ping -n 2 127.0.0.1 > NUL
goto waittick
:ticked
echo ASSISTANT_JSR_START^|{"job_id":"stub-orphan","state":"running","pid":%ORPHAN_PID%}
echo ASSISTANT_JSR_LOG^|模拟孤儿：harness 未发 RESULT 即退出
exit /b 1
