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
echo Origin:  %REMOTE_URL%
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
set /p "CONFIRM_STAGE=Stage all Git changes with git add -A? [Y/n]: "
if /i "!CONFIRM_STAGE!"=="n" (
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
set "STAGED_FILES=%TEMP%\winsystemhelper-staged-%RANDOM%.tmp"
set "SECRET_SCAN=%TEMP%\winsystemhelper-secret-scan-%RANDOM%.tmp"
git diff --cached --name-only > "%STAGED_FILES%" 2>> "%PUSH_LOG%"

set "SECRET_SCAN_SIZE=0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$blocked = Get-Content -LiteralPath $env:STAGED_FILES | Where-Object { $_ -match '(^|/)(config\.json|appsettings\.json|appsettings\.Development\.json|secrets\.json|.*\.log|.*\.zip)$' -or $_ -match '(^|/)(dist|artifacts|bin|obj)/' }; $blocked" > "%SECRET_SCAN%" 2>nul
if exist "%SECRET_SCAN%" for %%F in ("%SECRET_SCAN%") do set "SECRET_SCAN_SIZE=%%~zF"
if not "!SECRET_SCAN_SIZE!"=="0" (
    echo.
    echo Sensitive or generated files are staged. Review and unstage them before pushing:
    type "%SECRET_SCAN%"
    set "STEP_SECRET_SCAN=FAILED"
    set "FAILED_STEP=Sensitive staged file check"
    set "ERROR_CODE=1"
    goto :fail
)

set "SECRET_SCAN_SIZE=0"
git grep --cached -n -I -E "([0-9]{8,}:[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]+|-----BEGIN (RSA |OPENSSH |EC |DSA )?PRIVATE KEY-----)" > "%SECRET_SCAN%" 2>nul
if exist "%SECRET_SCAN%" for %%F in ("%SECRET_SCAN%") do set "SECRET_SCAN_SIZE=%%~zF"
if not "!SECRET_SCAN_SIZE!"=="0" (
    echo.
    echo Possible secret values were found in staged content:
    type "%SECRET_SCAN%"
    set "STEP_SECRET_SCAN=FAILED"
    set "FAILED_STEP=Secret pattern scan"
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

if exist "%STAGED_FILES%" del /q "%STAGED_FILES%" 2>nul
if exist "%SECRET_SCAN%" del /q "%SECRET_SCAN%" 2>nul
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
