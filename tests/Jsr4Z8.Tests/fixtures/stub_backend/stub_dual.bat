@echo off
chcp 65001 > NUL
REM Dual-instance stub (2026-10-15 probe §7.3): the real engine's every invocation is
REM "parent spawns a same-argv child ~2s later; the child does the work and exits
REM first; the parent relays its exit code 0.3-0.5s later". This parent spawns the
REM child stub, keeps streaming AFTER the child has exited (ping ~2s), then exits 0 -
REM proving the runner waits the PARENT (its spawned pid) and takes ITS exit code.
echo SRHQ_V12_PROGRESS^|10^|父实例启动，派生同 argv 子实例
start "" /B cmd /c "%~dp0stub_dual_child.bat"
ping -n 3 127.0.0.1 > NUL
echo SRHQ_V12_PROGRESS^|100^|父实例转述子实例退出码
exit /b 0
