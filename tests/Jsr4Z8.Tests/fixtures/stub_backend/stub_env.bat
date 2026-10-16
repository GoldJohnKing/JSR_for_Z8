@echo off
chcp 65001 > NUL
REM _PYI_* injection echo stub (2026-10-16 meipass2 experiment e1-c3): BackendRunner
REM sets the _PYI_* triple on every engine spawn (the pre-extracted-tree runtime
REM contract - a wrong value makes the bootloader silently fall back to per-run
REM self-extraction). This stub echoes the three variables verbatim so the test can
REM lock the injected values character-exact against the experiment recipe, plus
REM its own %~f0: the same-path lock (evaluation 2026-10-16-thin-archive-probe.md
REM fact A) - _PYI_ARCHIVE_FILE must equal the SPAWNED exe's own path; a
REM foreign archive is silently ignored (parent-path self-extraction).
echo _PYI_ARCHIVE_FILE=%_PYI_ARCHIVE_FILE%
echo _PYI_APPLICATION_HOME_DIR=%_PYI_APPLICATION_HOME_DIR%
echo _PYI_PARENT_PROCESS_LEVEL=%_PYI_PARENT_PROCESS_LEVEL%
echo _PYI_SELF_PATH=%~f0
echo DONE env echo
exit /b 0
