@echo off
setlocal

set DIST=%~dp0dist
set APP_PUBLISH=%~dp0bin\Release\net8.0-windows\win-x64\publish
set BOOTSTRAPPER_PUBLISH=%~dp0WinSystemHelper.Bootstrapper\bin\Release\net8.0-windows\win-x64\publish

if exist "%DIST%" rmdir /s /q "%DIST%"
mkdir "%DIST%"

dotnet publish .\WinSystemHelper.csproj -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
if errorlevel 1 exit /b %errorlevel%

dotnet publish .\WinSystemHelper.Bootstrapper\WinSystemHelper.Bootstrapper.csproj -c Release -r win-x64
if errorlevel 1 (
  echo.
  echo NativeAOT bootstrapper publish failed.
  echo Install the Visual Studio "Desktop development with C++" workload, then run build.cmd again.
  exit /b %errorlevel%
)

xcopy /y /e /i "%APP_PUBLISH%\*" "%DIST%\" >nul
copy /y "%BOOTSTRAPPER_PUBLISH%\WinSystemHelper.Bootstrapper.exe" "%DIST%\WinSystemHelper.Bootstrapper.exe" >nul
copy /y ".\install.ps1" "%DIST%\install.ps1" >nul
copy /y ".\README.md" "%DIST%\README.md" >nul
del /q "%DIST%\*.pdb" 2>nul

echo Release package created at "%DIST%".
