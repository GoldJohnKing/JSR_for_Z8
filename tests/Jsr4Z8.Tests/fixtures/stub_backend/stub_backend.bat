@echo off
chcp 65001 > NUL
REM Direct-invoke stub (2026-10-15 probe §7): replays the BARE engine stdout of a
REM successful jsr_backend.exe run - zero ASSISTANT_* envelopes. PTC echo line,
REM V12 progress, export-phase ETA progress, DONE tail, exit 0.
echo PTC {"selection": "manual_fullwell", "read_source": "manual RN(DN)"}
echo SRHQ_V12_PROGRESS^|42^|模拟阶段
echo SRHQ_ETA_PROGRESS^|dng^|10880^|11040
echo DONE total 1.0s
exit /b 0
