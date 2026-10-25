@echo off
rem ============================================================
rem  check.cmd - one command, everything green or a clear reason why
rem
rem    check.cmd            verify (compile + tests + exe) then sync docs
rem    check.cmd -q         quick: skip the exe build
rem    check.cmd -n        verify only, do not touch the docs
rem    check.cmd -fix       repair BOM / encoding issues, then continue
rem
rem  This is the workflow's single entry point. The previous one had three
rem  separate steps and nothing enforced them, so verification was skipped
rem  on every change that "looked obviously fine" - which is exactly when
rem  it was not.
rem
rem  Dev loop stays dev.cmd (it restarts the running app).
rem  This one answers: am I safe to commit / hand over?
rem ============================================================
setlocal
cd /d "%~dp0"

set VERIFY_ARGS=-NoProfile -ExecutionPolicy Bypass -File "%~dp0Verify.ps1"
set VERIFY_FLAGS=
set DO_SYNC=1

:parse
if "%~1"=="" goto run
if /i "%~1"=="-q" set VERIFY_FLAGS=%VERIFY_FLAGS% -Quick
if /i "%~1"=="-n" set DO_SYNC=0
if /i "%~1"=="-fix" set VERIFY_FLAGS=%VERIFY_FLAGS% -Fix
if /i "%~1"=="--quick" set VERIFY_FLAGS=%VERIFY_FLAGS% -Quick
if /i "%~1"=="--no-docs" set DO_SYNC=0
if /i "%~1"=="--fix" set VERIFY_FLAGS=%VERIFY_FLAGS% -Fix
shift
goto parse

:run
powershell %VERIFY_ARGS% %VERIFY_FLAGS%
if errorlevel 1 (
    echo.
    echo   verify failed - docs left untouched
    endlocal & exit /b 1
)

if "%DO_SYNC%"=="0" (
    endlocal & exit /b 0
)

rem Docs sync only after a green verify: the assertion count comes from the
rem test run, so syncing a red tree would publish a stale number.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Sync-Docs.ps1"
if errorlevel 1 (
    echo.
    echo   docs sync failed
    endlocal & exit /b 1
)

endlocal & exit /b 0
