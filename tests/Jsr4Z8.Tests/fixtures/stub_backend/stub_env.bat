@echo off
chcp 65001 > NUL
REM _PYI_* injection echo stub (2026-10-16 meipass2 experiment e1-c3): BackendRunner
REM sets the _PYI_* triple on every engine spawn (the pre-extracted-tree runtime
REM contract - a wrong value makes the bootloader silently fall back to per-run
REM self-extraction). This stub echoes the three variables verbatim so the test can
REM lock the injected values character-exact against the experiment recipe.
echo _PYI_ARCHIVE_FILE=%_PYI_ARCHIVE_FILE%
echo _PYI_APPLICATION_HOME_DIR=%_PYI_APPLICATION_HOME_DIR%
echo _PYI_PARENT_PROCESS_LEVEL=%_PYI_PARENT_PROCESS_LEVEL%
echo DONE env echo
exit /b 0
