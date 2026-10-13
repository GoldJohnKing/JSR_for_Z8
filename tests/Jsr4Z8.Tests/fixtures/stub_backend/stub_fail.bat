@echo off
chcp 65001 > NUL
echo ASSISTANT_JSR_START^|{"job_id":"stub-fail","state":"running"}
echo ASSISTANT_JSR_LOG^|模拟失败前日志
echo SRHQ_V12_PROGRESS^|42^|模拟阶段
echo MemoryError: Alignment cannot fit the current effective 4.00 GB budget
echo SRHQ_NET_ABORT:runtime^|模拟失败原因
echo ASSISTANT_JSR_RESULT^|{"exit_code":3,"state":"failed","log":"failed"}
1>&2 echo ASSISTANT_JSR_ERROR^|模拟失败
exit /b 1
