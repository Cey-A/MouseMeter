@echo off
rem ============================================================
rem  dev.cmd - build and run MouseMeter from source, in one step
rem
rem    dev.cmd              build if the source changed, then run
rem    dev.cmd -s           same, but open the settings window
rem    dev.cmd -f           force a rebuild even if nothing changed
rem    dev.cmd -v           verbose (shows the compile step's timing)
rem
rem  Order matters: the build happens BEFORE the old instance is
rem  stopped, so a compile error leaves the working copy running
rem  and you only lose the "restart", not the running app.
rem ============================================================
setlocal
cd /d "%~dp0"

set PS_ARGS=-NoProfile -ExecutionPolicy Bypass -File "%~dp0DevRun.ps1"
set APP_ARGS=

:parse
if "%~1"=="" goto run
if /i "%~1"=="-s" set APP_ARGS=%APP_ARGS% -Settings
if /i "%~1"=="-f" set APP_ARGS=%APP_ARGS% -Force
if /i "%~1"=="-v" set APP_ARGS=%APP_ARGS% -Verbose
if /i "%~1"=="--settings" set APP_ARGS=%APP_ARGS% -Settings
shift
goto parse

:run
powershell %PS_ARGS% %APP_ARGS%
set EXITCODE=%ERRORLEVEL%
endlocal & exit /b %EXITCODE%
