@echo off
chcp 65001 > NUL
REM Direct-invoke failure stub (2026-10-15 probe §7.1 run0 shape): bare abort line
REM on stdout, MemoryError line above it, a Python-style traceback on stderr,
REM nonzero exit. No envelopes anywhere.
echo SRHQ_V12_PROGRESS^|42^|模拟阶段
echo MemoryError: Alignment cannot fit the current effective 4.00 GB budget
echo SRHQ_NET_ABORT:runtime^|模拟失败原因
1>&2 echo ValueError: seven distinct RAW frames are required
exit /b 1
