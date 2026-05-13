@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0"
set "DIST=%ROOT%dist"
set "ARTIFACTS=%ROOT%artifacts"
set "OTA_PACKAGE=%ARTIFACTS%\WinSystemHelper-ota.zip"
set "APP_PUBLISH=%ROOT%bin\Release\net8.0-windows\win-x64\publish"
set "BOOTSTRAPPER_PUBLISH=%ROOT%WinSystemHelper.Bootstrapper\bin\Release\net8.0-windows\win-x64\publish"
set "BUILD_LOG=%ROOT%build.log"

set "STEP_DOTNET=SKIPPED"
set "STEP_CLEAN=SKIPPED"
set "STEP_APP=SKIPPED"
set "STEP_BOOTSTRAPPER=SKIPPED"
set "STEP_COPY_APP=SKIPPED"
set "STEP_COPY_BOOTSTRAPPER=SKIPPED"
set "STEP_COPY_INSTALL=SKIPPED"
set "STEP_COPY_README=SKIPPED"
set "STEP_CLEAN_SYMBOLS=SKIPPED"
set "STEP_PACKAGE_ZIP=SKIPPED"
set "FAILED_STEP=None"
set "ERROR_CODE=0"

echo.
echo ============================================================
echo WinSystemHelper Release Build
echo ============================================================
echo Started: %date% %time%
echo Root:    "%ROOT%"
echo Dist:    "%DIST%"
echo OTA ZIP: "%OTA_PACKAGE%"
echo Log:     "%BUILD_LOG%"
echo.

if exist "%BUILD_LOG%" del /q "%BUILD_LOG%" 2>nul

echo [1/10] Checking .NET SDK availability...
set "SDK_LIST=%TEMP%\winsystemhelper-sdks-%RANDOM%.tmp"
dotnet --list-sdks > "%SDK_LIST%" 2>> "%BUILD_LOG%"
set "LAST_STEP_CODE=!errorlevel!"
if exist "%SDK_LIST%" type "%SDK_LIST%" >> "%BUILD_LOG%"
set "SDK_LIST_SIZE=0"
if exist "%SDK_LIST%" for %%F in ("%SDK_LIST%") do set "SDK_LIST_SIZE=%%~zF"
if exist "%SDK_LIST%" del /q "%SDK_LIST%" 2>nul

if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_DOTNET=FAILED"
    set "FAILED_STEP=Check .NET SDK"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)

if "!SDK_LIST_SIZE!"=="0" (
    set "STEP_DOTNET=FAILED"
    set "FAILED_STEP=Check .NET SDK"
    set "ERROR_CODE=1"
    echo No .NET SDK was found. Install the .NET 8 SDK, then run build.cmd again. >> "%BUILD_LOG%"
    goto :fail
)
set "STEP_DOTNET=OK"

echo [2/10] Preparing dist folder...
if exist "%DIST%" (
    rmdir /s /q "%DIST%"
    set "LAST_STEP_CODE=!errorlevel!"
    if not "!LAST_STEP_CODE!"=="0" (
        set "STEP_CLEAN=FAILED"
        set "FAILED_STEP=Clean dist folder"
        set "ERROR_CODE=!LAST_STEP_CODE!"
        goto :fail
    )
)

mkdir "%DIST%"
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_CLEAN=FAILED"
    set "FAILED_STEP=Create dist folder"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)
set "STEP_CLEAN=OK"

echo.
echo [3/10] Publishing WinSystemHelper framework-dependent single-file app...
dotnet publish "%ROOT%WinSystemHelper.csproj" -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true >> "%BUILD_LOG%" 2>&1
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_APP=FAILED"
    set "FAILED_STEP=Publish WinSystemHelper"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)
set "STEP_APP=OK"

echo.
echo [4/10] Publishing NativeAOT bootstrapper...
dotnet publish "%ROOT%WinSystemHelper.Bootstrapper\WinSystemHelper.Bootstrapper.csproj" -c Release -r win-x64 >> "%BUILD_LOG%" 2>&1
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_BOOTSTRAPPER=FAILED"
    set "FAILED_STEP=Publish bootstrapper"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :bootstrapper_fail
)
set "STEP_BOOTSTRAPPER=OK"

echo.
echo [5/10] Copying app publish output...
xcopy /y /e /i "%APP_PUBLISH%\*" "%DIST%\" >nul
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_COPY_APP=FAILED"
    set "FAILED_STEP=Copy app publish output"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)
set "STEP_COPY_APP=OK"

echo [6/10] Copying bootstrapper...
copy /y "%BOOTSTRAPPER_PUBLISH%\WinSystemHelper.Bootstrapper.exe" "%DIST%\WinSystemHelper.Bootstrapper.exe" >nul
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_COPY_BOOTSTRAPPER=FAILED"
    set "FAILED_STEP=Copy bootstrapper"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)
set "STEP_COPY_BOOTSTRAPPER=OK"

echo [7/10] Copying install.ps1...
copy /y "%ROOT%install.ps1" "%DIST%\install.ps1" >nul
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_COPY_INSTALL=FAILED"
    set "FAILED_STEP=Copy install.ps1"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)
set "STEP_COPY_INSTALL=OK"

echo [8/10] Copying README.md...
copy /y "%ROOT%README.md" "%DIST%\README.md" >nul
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_COPY_README=FAILED"
    set "FAILED_STEP=Copy README.md"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)
set "STEP_COPY_README=OK"

echo [9/10] Removing debug symbols from dist...
del /q "%DIST%\*.pdb" 2>nul
set "STEP_CLEAN_SYMBOLS=OK"

echo [10/10] Creating OTA update ZIP...
if not exist "%ARTIFACTS%" mkdir "%ARTIFACTS%"
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_PACKAGE_ZIP=FAILED"
    set "FAILED_STEP=Create artifacts folder"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)

if exist "%OTA_PACKAGE%" del /q "%OTA_PACKAGE%" 2>nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -LiteralPath '%DIST%\WinSystemHelper.exe','%DIST%\WinSystemHelper.Bootstrapper.exe','%DIST%\install.ps1','%DIST%\README.md' -DestinationPath '%OTA_PACKAGE%' -Force" >> "%BUILD_LOG%" 2>&1
set "LAST_STEP_CODE=!errorlevel!"
if not "!LAST_STEP_CODE!"=="0" (
    set "STEP_PACKAGE_ZIP=FAILED"
    set "FAILED_STEP=Create OTA update ZIP"
    set "ERROR_CODE=!LAST_STEP_CODE!"
    goto :fail
)
set "STEP_PACKAGE_ZIP=OK"

goto :success

:bootstrapper_fail
echo.
echo NativeAOT bootstrapper publish failed.
echo Install the Visual Studio "Desktop development with C++" workload, then run build.cmd again.
goto :fail

:success
set "ERROR_CODE=0"
call :summary
echo.
echo Release package created successfully.
exit /b 0

:fail
call :summary
echo.
echo Build failed at step: !FAILED_STEP!
exit /b !ERROR_CODE!

:summary
echo.
echo ============================================================
echo Build Summary
echo ============================================================
echo .NET SDK available:      !STEP_DOTNET!
echo Clean dist folder:       !STEP_CLEAN!
echo Publish app:             !STEP_APP!
echo Publish bootstrapper:    !STEP_BOOTSTRAPPER!
echo Copy app files:          !STEP_COPY_APP!
echo Copy bootstrapper:       !STEP_COPY_BOOTSTRAPPER!
echo Copy install.ps1:        !STEP_COPY_INSTALL!
echo Copy README.md:          !STEP_COPY_README!
echo Remove PDB files:        !STEP_CLEAN_SYMBOLS!
echo Create OTA ZIP:          !STEP_PACKAGE_ZIP!
echo Failed step:             !FAILED_STEP!
echo Exit code:               !ERROR_CODE!
echo Build log:               "%BUILD_LOG%"

if not "!ERROR_CODE!"=="0" (
    echo.
    echo Last build log lines:
    if exist "%BUILD_LOG%" (
        powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath $env:BUILD_LOG -Tail 80" 2>nul
    ) else (
        echo   No build log was created.
    )
)

if "!ERROR_CODE!"=="0" (
    echo.
    echo Output files:
    if exist "%DIST%\WinSystemHelper.exe" for %%F in ("%DIST%\WinSystemHelper.exe") do echo   %%~nxF - %%~zF bytes
    if exist "%DIST%\WinSystemHelper.Bootstrapper.exe" for %%F in ("%DIST%\WinSystemHelper.Bootstrapper.exe") do echo   %%~nxF - %%~zF bytes
    if exist "%DIST%\install.ps1" for %%F in ("%DIST%\install.ps1") do echo   %%~nxF - %%~zF bytes
    if exist "%DIST%\README.md" for %%F in ("%DIST%\README.md") do echo   %%~nxF - %%~zF bytes
    if exist "%OTA_PACKAGE%" for %%F in ("%OTA_PACKAGE%") do echo   %%~nxF - %%~zF bytes
    echo.
    echo Dist folder:
    echo   "%DIST%"
    echo OTA update package:
    echo   "%OTA_PACKAGE%"
)
echo ============================================================
exit /b 0
