@echo off
setlocal
title CouchDesk launcher
cd /d "%~dp0"

REM --- Require the .NET 8 SDK ---
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] .NET SDK not found on PATH.
    echo Install the .NET 8 SDK from:
    echo     https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

echo ============================================================
echo  CouchDesk - starting tray utility in Release
echo  First run will restore packages and build (may take a bit).
echo  Use the notification-area icon to control the server and quit.
echo ============================================================
echo.

dotnet run --project "src\Core" -c Release

echo.
endlocal
