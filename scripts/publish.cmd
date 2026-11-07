@echo off
rem Runs the tests, then publishes a self-contained single-file WindowsRepairAssistant.exe.
rem Output: artifacts\publish\win-x64\WindowsRepairAssistant.exe (+ .sha256 checksum)
rem Usage:  scripts\publish.cmd [--skip-tests]
setlocal
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"

rem Prefer a machine-wide .NET 10 SDK; fall back to a per-user install from dotnet-install.ps1.
set "DOTNET=dotnet"
"%DOTNET%" --list-sdks 2>nul | findstr /b /l /c:"10." >nul || set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet10\dotnet.exe"
"%DOTNET%" --list-sdks 2>nul | findstr /b /l /c:"10." >nul || (
    echo No .NET 10 SDK found. Install it with: winget install Microsoft.DotNet.SDK.10
    exit /b 1
)
echo Using %DOTNET%

pushd "%~dp0.."
set "OUT=artifacts\publish\win-x64"

if /i not "%~1"=="--skip-tests" (
    "%DOTNET%" test --configuration Release || goto :testsFailed
)

if exist "%OUT%" rmdir /s /q "%OUT%"
"%DOTNET%" publish src\WindowsRepairAssistant\WindowsRepairAssistant.csproj -p:PublishProfile=win-x64 || goto :publishFailed
if not exist "%OUT%\WindowsRepairAssistant.exe" goto :publishFailed

certutil -hashfile "%OUT%\WindowsRepairAssistant.exe" SHA256 | findstr /v /c:"hash" > "%OUT%\WindowsRepairAssistant.exe.sha256"

echo.
echo Published: %CD%\%OUT%\WindowsRepairAssistant.exe
echo SHA-256:
type "%OUT%\WindowsRepairAssistant.exe.sha256"
echo NOTE: the exe is unsigned. Sign it before public distribution - see README.
popd
exit /b 0

:testsFailed
echo Tests failed - not publishing.
popd
exit /b 1

:publishFailed
echo Publish failed.
popd
exit /b 1
