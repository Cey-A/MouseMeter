@echo off
rem ============================================================
rem  update.cmd - rebuild and relaunch MouseMeter in one step
rem
rem    update.cmd            pull, verify, rebuild exe, relaunch
rem    update.cmd -q         skip the tests, just rebuild and relaunch
rem    update.cmd -n         skip git pull (offline / local edits only)
rem    update.cmd -k         keep the app closed after rebuilding
rem
rem  Why this exists: the running exe holds a write lock on itself, so
rem  Build.ps1 cannot overwrite it. This script stops the app first,
rem  then builds, then starts the fresh copy.
rem
rem  The app saves its settings on a clean exit, so CloseMainWindow is
rem  tried before Stop-Process. Force-killing would lose the widget
rem  position and the material slider value.
rem ============================================================
cd /d "%~dp0"

set DO_PULL=1
set DO_VERIFY=1
set DO_START=1

:parse
if "%~1"=="" goto run
if /i "%~1"=="-q" set DO_VERIFY=0
if /i "%~1"=="-n" set DO_PULL=0
if /i "%~1"=="-k" set DO_START=0
if /i "%~1"=="--quick" set DO_VERIFY=0
if /i "%~1"=="--no-pull" set DO_PULL=0
if /i "%~1"=="--keep-closed" set DO_START=0
shift
goto parse

:run
echo.
echo   MouseMeter update
echo   %~dp0
echo.

rem ---------- 1. stop the running instance ----------
echo   [1/5] stopping the running instance
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$p = Get-Process MouseMeter -ErrorAction SilentlyContinue; if (-not $p) { Write-Host '        not running'; exit 0 }; $p | ForEach-Object { $_.CloseMainWindow() | Out-Null }; $w = 0; while ((Get-Process MouseMeter -ErrorAction SilentlyContinue) -and $w -lt 30) { Start-Sleep -Milliseconds 100; $w++ }; $still = Get-Process MouseMeter -ErrorAction SilentlyContinue; if ($still) { Write-Host '        clean exit timed out, forcing'; $still | Stop-Process -Force; Start-Sleep -Milliseconds 400 } else { Write-Host '        exited cleanly, settings saved' }"

rem ---------- 2. pull ----------
if not "%DO_PULL%"=="1" goto skip_pull
echo   [2/5] git pull
where git >nul 2>&1
if errorlevel 1 goto no_git
git rev-parse --git-dir >nul 2>&1
if errorlevel 1 goto no_repo
git pull --ff-only
if errorlevel 1 goto pull_failed
goto pull_done

:no_git
echo         git not found - skipping pull
goto pull_done

:no_repo
echo         not a git repository - skipping pull
goto pull_done

:pull_failed
echo.
echo   pull failed - local changes or a diverged branch.
echo   Resolve it, or run:  update.cmd -n
exit /b 1

:skip_pull
echo   [2/5] git pull - skipped

:pull_done

rem ---------- 3. verify (compile + tests) ----------
if not "%DO_VERIFY%"=="1" goto skip_verify
echo   [3/5] verify
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Verify.ps1" -Quick
if errorlevel 1 goto verify_failed
goto verify_done

:verify_failed
echo.
echo   verify failed - the old exe is untouched, nothing to roll back.
exit /b 1

:skip_verify
echo   [3/5] verify - skipped

:verify_done

rem ---------- 4. rebuild the exe ----------
echo   [4/5] rebuilding dist\MouseMeter.exe
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1"
if errorlevel 1 goto build_failed
if not exist "%~dp0dist\MouseMeter.exe" goto no_exe
goto build_done

:build_failed
echo.
echo   build failed - see the error above.
exit /b 1

:no_exe
echo.
echo   build reported success but produced no exe.
exit /b 1

:build_done

rem ---------- 5. launch ----------
if not "%DO_START%"=="1" goto skip_start
echo   [5/5] starting the new build
start "" "%~dp0dist\MouseMeter.exe"
goto done

:skip_start
echo   [5/5] launch - skipped

:done
echo.
echo   done
echo.
exit /b 0
