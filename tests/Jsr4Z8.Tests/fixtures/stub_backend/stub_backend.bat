@echo off
chcp 65001 > NUL
echo ASSISTANT_JSR_START^|{"job_id":"stub-1","state":"running","stage":"reconstruction","engine":"stormer7","dng":true}
echo ASSISTANT_JSR_LOG^|模拟后端启动
echo SRHQ_V12_PROGRESS^|42^|模拟阶段
echo ASSISTANT_JSR_LOG^|代理已就绪，启动长跑子进程
REM detached grandchild: short on purpose - start /B cannot detach the inherited
REM stdout/stderr pipes, so a long child would block the runner's stream until it exits
start "" /B cmd /c ping -n 4 127.0.0.1 > NUL 2>&1
echo ASSISTANT_JSR_RESULT^|{"exit_code":0,"state":"complete","log":"done"}
exit /b 0
