@echo off
chcp 65001 > NUL
REM The child half of stub_dual.bat (same-argv child instance; spawned by the parent
REM stub, inherits the redirected stdout pipe so its bare lines reach the runner).
echo SRHQ_V12_PROGRESS^|50^|子实例同 argv 干活
exit /b 0
