@echo off
chcp 65001 > NUL
if "%JSR4Z8_TEST_TICK%"=="" exit /b 1
echo ASSISTANT_JSR_START^|{"job_id":"stub-kill","state":"running","stage":"reconstruction"}
echo ASSISTANT_JSR_LOG^|长跑模拟：心跳子进程已启动
echo SRHQ_V12_PROGRESS^|42^|模拟阶段
start "" /B powershell -NoProfile -ExecutionPolicy Bypass -Command "while($true){[IO.File]::WriteAllText($env:JSR4Z8_TEST_TICK,(Get-Date).ToString('o'));Start-Sleep -Milliseconds 250}" > NUL 2>&1
ping -n 30 127.0.0.1 > NUL
echo ASSISTANT_JSR_RESULT^|{"exit_code":0,"state":"complete","log":"late"}
exit /b 0
