@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0"
set "PUSH_LOG=%ROOT%git-push.log"
set "STEP_GIT=SKIPPED"
set "STEP_REPO=SKIPPED"
set "STEP_BUILD=SKIPPED"
set "STEP_STAGE=SKIPPED"
set "STEP_SECRET_SCAN=SKIPPED"
set "STEP_COMMIT=SKIPPED"
set "STEP_PUSH=SKIPPED"
set "FAILED_STEP=None"
set "ERROR_CODE=0"

cd /d "%ROOT%"

if /i "%~1"=="/?" goto :usage
if /i "%~1"=="--help" goto :usage
if /i "%~1"=="-h" goto :usage

echo.
echo ============================================================
echo WinSystemHelper Git Publish Helper
echo ============================================================
echo Started: %date% %time%
echo Root:    "%ROOT%"
echo Log:     "%PUSH_LOG%"
echo.

if exist "%PUSH_LOG%" del /q "%PUSH_LOG%" 2>nul

echo [1/7] Checking Git availability...
git --version > "%PUSH_LOG%" 2>&1
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_GIT=FAILED"
    set "FAILED_STEP=Check Git"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)
set "STEP_GIT=OK"

echo [2/7] Checking repository...
git rev-parse --is-inside-work-tree >> "%PUSH_LOG%" 2>&1
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_REPO=FAILED"
    set "FAILED_STEP=Check repository"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)

for /f "usebackq delims=" %%B in (`git branch --show-current 2^>nul`) do set "BRANCH=%%B"
if "%BRANCH%"=="" set "BRANCH=main"

for /f "usebackq delims=" %%R in (`git remote get-url origin 2^>nul`) do set "REMOTE_URL=%%R"
if "%REMOTE_URL%"=="" (
    set "STEP_REPO=FAILED"
    set "FAILED_STEP=Read origin remote"
    set "ERROR_CODE=1"
    echo Missing git remote named origin. >> "%PUSH_LOG%"
    goto :fail
)
set "STEP_REPO=OK"

echo Branch:  %BRANCH%
echo Origin:  configured (URL omitted)
echo.
echo Current changes:
git status --short
echo.

git diff --quiet
set "HAS_TRACKED_CHANGES=!errorlevel!"
git diff --cached --quiet
set "HAS_STAGED_CHANGES=!errorlevel!"
for /f %%C in ('git ls-files --others --exclude-standard ^| find /c /v ""') do set "UNTRACKED_COUNT=%%C"

if "!HAS_TRACKED_CHANGES!"=="0" if "!HAS_STAGED_CHANGES!"=="0" if "!UNTRACKED_COUNT!"=="0" (
    echo No changes to commit or push.
    set "STEP_BUILD=SKIPPED"
    set "STEP_STAGE=SKIPPED"
    set "STEP_SECRET_SCAN=SKIPPED"
    set "STEP_COMMIT=SKIPPED"
    set "STEP_PUSH=SKIPPED"
    goto :success
)

set "RUN_BUILD=Y"
set /p "RUN_BUILD=Run build.cmd before committing? [Y/n]: "
if /i "!RUN_BUILD!"=="n" (
    set "STEP_BUILD=SKIPPED"
) else (
    echo.
    echo [3/7] Running build.cmd...
    call "%ROOT%build.cmd"
    set "LAST_STEP_CODE=!errorlevel!"
    if not "!LAST_STEP_CODE!"=="0" (
        set "STEP_BUILD=FAILED"
        set "FAILED_STEP=Run build.cmd"
        set "ERROR_CODE=!LAST_STEP_CODE!"
        goto :fail
    )
    set "STEP_BUILD=OK"
)

echo.
set "CONFIRM_STAGE=N"
set /p "CONFIRM_STAGE=Stage all Git changes with git add -A? [y/N]: "
if /i not "!CONFIRM_STAGE!"=="y" (
    set "STEP_STAGE=FAILED"
    set "FAILED_STEP=Stage changes canceled by user"
    set "ERROR_CODE=1"
    goto :fail
)

echo [4/7] Staging changes...
git add -A >> "%PUSH_LOG%" 2>&1
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_STAGE=FAILED"
    set "FAILED_STEP=Stage changes"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)
set "STEP_STAGE=OK"

echo.
echo Staged files:
git diff --cached --name-status
echo.

echo [5/7] Running safety checks on staged files...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Check-PublishSafety.ps1" -Mode Index
if errorlevel 1 (
    set "STEP_SECRET_SCAN=FAILED"
    set "FAILED_STEP=Index privacy inspection"
    set "ERROR_CODE=1"
    goto :fail
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Check-PublishSafety.ps1" -Mode History
if errorlevel 1 (
    set "STEP_SECRET_SCAN=FAILED"
    set "FAILED_STEP=History privacy inspection"
    set "ERROR_CODE=1"
    goto :fail
)

git diff --cached --check >> "%PUSH_LOG%" 2>&1
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_SECRET_SCAN=FAILED"
    set "FAILED_STEP=Git whitespace/conflict check"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)

set "STEP_SECRET_SCAN=OK"

echo.
set /p "COMMIT_MESSAGE=Commit message: "
if "!COMMIT_MESSAGE!"=="" set "COMMIT_MESSAGE=Update WinSystemHelper"

echo.
echo [6/7] Creating commit...
git commit -m "!COMMIT_MESSAGE!" >> "%PUSH_LOG%" 2>&1
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_COMMIT=FAILED"
    set "FAILED_STEP=Create commit"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)
set "STEP_COMMIT=OK"

echo.
set "CONFIRM_PUSH=N"
set /p "CONFIRM_PUSH=Push commit to origin/%BRANCH%? [y/N]: "
if /i not "!CONFIRM_PUSH!"=="y" (
    set "STEP_PUSH=SKIPPED"
    set "FAILED_STEP=Push canceled by user"
    set "ERROR_CODE=1"
    goto :fail
)

echo [7/7] Pushing to origin/%BRANCH%...
git push origin "%BRANCH%" >> "%PUSH_LOG%" 2>&1
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_PUSH=FAILED"
    set "FAILED_STEP=Push to GitHub"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)
set "STEP_PUSH=OK"

goto :success

:usage
echo.
echo WinSystemHelper Git Publish Helper
echo.
echo Usage:
echo   git-push.cmd
echo.
echo What it does:
echo   1. Shows current Git status.
echo   2. Optionally runs build.cmd.
echo   3. Stages changes only after you type y.
echo   4. Runs staged-file and secret-pattern checks.
echo   5. Commits with your message.
echo   6. Pushes only after you type y.
echo.
echo No commit or push is performed from this help screen.
pause
exit /b 0

:success
set "ERROR_CODE=0"
call :summary
echo.
echo Done. All requested Git steps completed successfully.
pause
exit /b 0

:fail
call :summary
echo.
echo Git publish failed at step: !FAILED_STEP!
echo.
echo Last log lines:
if exist "%PUSH_LOG%" (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath $env:PUSH_LOG -Tail 80" 2>nul
) else (
    echo   No log file was created.
)
pause
exit /b !ERROR_CODE!

:summary
echo.
echo ============================================================
echo Git Publish Summary
echo ============================================================
echo Git available:          !STEP_GIT!
echo Repository check:       !STEP_REPO!
echo Build:                  !STEP_BUILD!
echo Stage changes:          !STEP_STAGE!
echo Safety checks:          !STEP_SECRET_SCAN!
echo Commit:                 !STEP_COMMIT!
echo Push:                   !STEP_PUSH!
echo Branch:                 %BRANCH%
echo Failed step:            !FAILED_STEP!
echo Exit code:              !ERROR_CODE!
echo Log:                    "%PUSH_LOG%"
echo ============================================================
exit /b 0
