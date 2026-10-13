@echo off
chcp 65001 > NUL
REM Task 16 regression stub: the REAL backend's RESULT envelope embeds its whole
REM log (MemoryError and SRHQ_NET_ABORT text included) inside the JSON "log" field,
REM and arrives AFTER the bare tail lines; the stderr ERROR wrapper repeats it.
REM Message distillation must skip envelope lines so the user sees the bare abort
REM message, not raw JSON (measured on the real 4GB alignment failure 2026-10-14).
echo ASSISTANT_JSR_START^|{"job_id":"stub-fail-env","state":"running"}
echo SRHQ_V12_PROGRESS^|20^|准备对齐代理图
echo MemoryError: Driver reports process GPU usage 4105961472 above configured budget
echo SRHQ_NET_ABORT:runtime^|Alignment cannot fit the current effective 4.00 GB budget. No CPU/CUDA fallback was used.
echo ASSISTANT_JSR_RESULT^|{"exit_code":3,"state":"failed","log":"SRHQ_V12_PROGRESS^|20^|准备对齐代理图\r\nMemoryError: Driver reports process GPU usage 4105961472 above configured budget\r\nSRHQ_NET_ABORT:runtime^|Alignment cannot fit the current effective 4.00 GB budget. No CPU/CUDA fallback was used.\r\n"}
1>&2 echo ASSISTANT_JSR_ERROR^|JSR未完成，原始日志："...SRHQ_NET_ABORT:runtime^|Alignment cannot fit the current effective 4.00 GB budget..."
exit /b 3
